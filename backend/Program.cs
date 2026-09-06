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

// Database Migration & Baseline Handshake
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    EnsureBaselineMigrationApplied(db);
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

    // Bootstrap Automation / Enrichment Bot Account (requires explicit secret)
    var botUsername = builder.Configuration["AutomationBot:Username"] ?? "enrichment-bot";
    var botPassword = builder.Configuration["AutomationBot:Password"] ?? Environment.GetEnvironmentVariable("ENRICHMENT_BOT_PASSWORD");
    if (string.IsNullOrWhiteSpace(botPassword) || botPassword.Length < 12)
    {
        throw new InvalidOperationException("ENRICHMENT_BOT_PASSWORD or AutomationBot:Password must be set in environment (at least 12 characters). Default fallback passwords are strictly prohibited.");
    }

    var botUser = db.Users.FirstOrDefault(u => u.Username == botUsername);
    if (botUser == null)
    {
        db.Users.Add(new WebMusic.Backend.Models.User
        {
            Username = botUsername,
            PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(botPassword),
            IsAdmin = true
        });
        db.SaveChanges();
    }
    else
    {
        botUser.PasswordHash = WebMusic.Backend.Services.PasswordService.Hash(botPassword);
        if (!botUser.IsAdmin)
        {
            botUser.IsAdmin = true;
        }
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

static void EnsureBaselineMigrationApplied(AppDbContext db)
{
    const string baselineMigrationId = "20260906111004_Initial_EnrichmentBaseline";
    const string productVersion = "8.0.10";

    try
    {
        if (db.Database.IsNpgsql())
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = false;
            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
                shouldClose = true;
            }

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'Users');";
                var res = cmd.ExecuteScalar();
                var usersTableExists = res is bool b && b;

                if (usersTableExists)
                {
                    db.Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                            ""MigrationId"" character varying(150) NOT NULL PRIMARY KEY,
                            ""ProductVersion"" character varying(32) NOT NULL
                        );
                        INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        SELECT '" + baselineMigrationId + @"', '" + productVersion + @"'
                        WHERE NOT EXISTS (
                            SELECT 1 FROM ""__EFMigrationsHistory"" WHERE ""MigrationId"" = '" + baselineMigrationId + @"'
                        );");
                }
            }
            finally
            {
                if (shouldClose) conn.Close();
            }
        }
        else if (db.Database.IsSqlite())
        {
            var conn = db.Database.GetDbConnection();
            var shouldClose = false;
            if (conn.State != System.Data.ConnectionState.Open)
            {
                conn.Open();
                shouldClose = true;
            }

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Users';";
                var count = Convert.ToInt32(cmd.ExecuteScalar());
                if (count > 0)
                {
                    db.Database.ExecuteSqlRaw(@"
                        CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                            ""MigrationId"" TEXT NOT NULL PRIMARY KEY,
                            ""ProductVersion"" TEXT NOT NULL
                        );
                        INSERT OR IGNORE INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                        VALUES ('" + baselineMigrationId + @"', '" + productVersion + @"');");
                }
            }
            finally
            {
                if (shouldClose) conn.Close();
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Migration] Baseline check notice: {ex.Message}");
    }
}
