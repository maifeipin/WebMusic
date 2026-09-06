using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WebMusic.Backend.Data;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.DataProtection;

// Disable default claim mapping to keep claims as 'sub', 'name', etc.
System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
// Fix for Npgsql 6.0+ forcing UTC. Enable legacy behavior to simplify migration.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Suppress verbose EF Core SQL logs in console
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

// Configure Logs with Timestamp
builder.Logging.AddSimpleConsole(options =>
{
    options.IncludeScopes = false;
    options.SingleLine = false; 
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

// Add services to the container.

builder.Services.AddControllers(options =>
{
    // Global exception filter for unified API error responses
    options.Filters.Add<WebMusic.Backend.Filters.GlobalExceptionFilter>();
});

// Allow large uploads
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 10L * 1024 * 1024 * 1024;
    options.ValueLengthLimit = 1024 * 1024;
    options.MemoryBufferThreshold = 1024 * 1024;
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var provider = builder.Configuration["DatabaseProvider"] ?? "Sqlite";
    var connectionString = builder.Configuration.GetConnectionString(provider);

    // Fallback: Check DefaultConnection if specific provider string not found
    if (string.IsNullOrEmpty(connectionString))
    {
        connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrEmpty(connectionString)) throw new InvalidOperationException($"No connection string found for provider '{provider}' and 'DefaultConnection' is missing.");
    }

    if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        options.UseNpgsql(connectionString);
    }
    else
    {
        options.UseSqlite(connectionString);
    }
});

// Configuration
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is missing");
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

if (builder.Environment.IsProduction() && (jwtKey.Length < 32 || jwtKey.StartsWith("ChangeThisSecretKey", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("A unique JWT_KEY of at least 32 characters is required in production.");
}

// Authentication
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
        
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                
                // Read token from query string for Stream endpoint
                if (!string.IsNullOrEmpty(accessToken) && 
                    path.StartsWithSegments("/api/media/stream"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

// CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Services
builder.Services.AddScoped<WebMusic.Backend.Services.ISmbService, WebMusic.Backend.Services.SmbService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<WebMusic.Backend.Services.ScannerService>();
builder.Services.AddSingleton<WebMusic.Backend.Services.BackgroundTaskQueue>();
builder.Services.AddSingleton<WebMusic.Backend.Services.ScanStateService>();
builder.Services.AddScoped<WebMusic.Backend.Services.TagService>();
builder.Services.AddSingleton<WebMusic.Backend.Services.PathResolver>(); // Centralized path resolution
builder.Services.AddScoped<WebMusic.Backend.Services.DataManagementService>();
builder.Services.AddScoped<WebMusic.Backend.Services.LyricsService>();
builder.Services.AddScoped<WebMusic.Backend.Services.MusicEnrichmentService>();
builder.Services.AddHttpClient(); // Required for IHttpClientFactory
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "data", "data-protection-keys")));
builder.Services.AddSingleton<WebMusic.Backend.Services.IShareAccessService, WebMusic.Backend.Services.ShareAccessService>();
builder.Services.AddHostedService<WebMusic.Backend.Services.JobWorker>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Schema Baseline Verification CLI Mode
if (args.Contains("verify-baseline"))
{
    using var verifyScope = app.Services.CreateScope();
    var db = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
    Console.WriteLine("=== 🔍 Schema Fingerprint Verification ===");
    var result = WebMusic.Backend.Services.SchemaFingerprintVerifier.VerifyFingerprint(db);
    if (!result.Success)
    {
        Console.WriteLine($"❌ Schema fingerprint verification failed with {result.Errors.Count} error(s):");
        foreach (var err in result.Errors)
        {
            Console.WriteLine($"  - {err}");
        }
        Environment.Exit(1);
    }

    Console.WriteLine($"✅ Verified all {result.VerifiedTables.Count} required tables, primary keys, and column datatypes.");

    if (args.Contains("--apply"))
    {
        WebMusic.Backend.Services.SchemaFingerprintVerifier.ApplyBaseline(db);
        Console.WriteLine($"🎉 Baseline migration '{WebMusic.Backend.Services.SchemaFingerprintVerifier.BaselineMigrationId}' recorded in __EFMigrationsHistory successfully.");
    }
    else
    {
        Console.WriteLine("ℹ️ Pass '--apply' to record baseline migration once schema verification passes.");
    }
    return;
}

// Database Migration
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    if (!db.Users.Any())
    {
        var adminUsername = builder.Configuration["BootstrapAdmin:Username"];
        var adminPassword = builder.Configuration["BootstrapAdmin:Password"];
        if (string.IsNullOrWhiteSpace(adminUsername) || string.IsNullOrWhiteSpace(adminPassword) || adminPassword.Length < 12)
        {
            throw new InvalidOperationException("The first startup requires BOOTSTRAP_ADMIN_USERNAME and a BOOTSTRAP_ADMIN_PASSWORD of at least 12 characters.");
        }

        db.Users.Add(new WebMusic.Backend.Models.User
        {
            Username = adminUsername,
            PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(adminPassword),
            IsAdmin = true
        });
        db.SaveChanges();
    }

    // 1. Maintain Legacy Enrichment Bot as Admin (for existing run_enrichment_task.sh)
    var botUsername = builder.Configuration["AutomationBot:Username"] ?? "enrichment-bot";
    var botPassword = builder.Configuration["AutomationBot:Password"] ?? Environment.GetEnvironmentVariable("ENRICHMENT_BOT_PASSWORD");
    if (!string.IsNullOrWhiteSpace(botPassword) && botPassword.Length >= 12)
    {
        var botUser = db.Users.FirstOrDefault(u => u.Username == botUsername);
        if (botUser == null)
        {
            db.Users.Add(new WebMusic.Backend.Models.User
            {
                Username = botUsername,
                PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(botPassword),
                IsAdmin = true,
                Role = "Admin"
            });
            db.SaveChanges();
        }
        else
        {
            botUser.PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(botPassword);
            botUser.Role = "Admin";
            botUser.IsAdmin = true;
            db.SaveChanges();
        }
    }

    // 2. Bootstrap Dedicated Catalog Worker Nodes (least privilege, Role="Worker", IsAdmin=false)
    // Server-registered independent credentials per node. NEVER falls back to admin bot credentials.
    var defaultWorkerUsername = builder.Configuration["AutomationWorker:Username"] ?? "catalog-worker";
    var defaultWorkerPassword = builder.Configuration["AutomationWorker:Password"]
        ?? Environment.GetEnvironmentVariable("ENRICHMENT_WORKER_SECRET");

    if (!string.IsNullOrWhiteSpace(defaultWorkerPassword) && defaultWorkerPassword.Length >= 12)
    {
        BootstrapWorkerNodeUser(db, defaultWorkerUsername, defaultWorkerPassword);
    }

    // Support multiple server-registered worker nodes via ENRICHMENT_WORKER_NODES or configuration
    // Format: "mac=secret1;nas=secret2" or "catalog-worker-mac:secret1,catalog-worker-nas:secret2"
    var workerNodesConfig = builder.Configuration["AutomationWorker:Nodes"]
        ?? Environment.GetEnvironmentVariable("ENRICHMENT_WORKER_NODES");
    if (!string.IsNullOrWhiteSpace(workerNodesConfig))
    {
        var nodeEntries = workerNodesConfig.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var entry in nodeEntries)
        {
            var parts = entry.Split(new[] { '=', ':' }, 2);
            if (parts.Length == 2)
            {
                var nodeName = parts[0].Trim();
                var nodeSecret = parts[1].Trim();
                if (!string.IsNullOrEmpty(nodeName) && nodeSecret.Length >= 12)
                {
                    var uName = nodeName.StartsWith("catalog-worker", StringComparison.OrdinalIgnoreCase)
                        ? nodeName
                        : $"catalog-worker-{nodeName}";
                    BootstrapWorkerNodeUser(db, uName, nodeSecret);
                }
            }
        }
    }

    // 3. Startup reconciliation: clean any orphaned cover files from uncommitted crashes
    WebMusic.Backend.Services.CoverPromotionReconciler.ReconcileOrphanCoversAsync(db, app.Environment).GetAwaiter().GetResult();
}

static void BootstrapWorkerNodeUser(WebMusic.Backend.Data.AppDbContext db, string username, string password)
{
    var workerUser = db.Users.FirstOrDefault(u => u.Username == username);
    if (workerUser == null)
    {
        db.Users.Add(new WebMusic.Backend.Models.User
        {
            Username = username,
            PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(password),
            IsAdmin = false,
            Role = "Worker"
        });
        db.SaveChanges();
    }
    else
    {
        workerUser.PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(password);
        workerUser.Role = "Worker";
        workerUser.IsAdmin = false;
        db.SaveChanges();
    }
}

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

// Optional: API Logging Middleware (Configurable)
if (app.Configuration.GetValue<bool>("EnableApiRequestLogging"))
{
    app.UseMiddleware<WebMusic.Backend.Middleware.ApiLoggingMiddleware>();
}

app.MapControllers();

// Check for FFmpeg presence
try
{
    var process = new System.Diagnostics.Process
    {
        StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = "-version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        }
    };
    process.Start();
    process.WaitForExit();
    Console.WriteLine("FFmpeg functionality check: PASS");
}
catch
{
    Console.WriteLine("WARNING: FFmpeg not found in PATH. Transcoding feature will fail.");
}

app.Run();

public partial class Program { }
