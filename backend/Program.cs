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

// Ensure Database Created
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    // For dev: Quick schema update by deletion
    // db.Database.EnsureDeleted(); 
    // db.Database.EnsureCreated();
    
    // Better: Just use EnsureCreated. If it exists, it assumes it's fine. 
    // Since I added tables, I need to force update.
    // I will delete the .db file via command line.
    db.Database.EnsureCreated();

    if (db.Database.IsNpgsql())
    {
        db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"IsAdmin\" boolean NOT NULL DEFAULT FALSE;");
        db.Database.ExecuteSqlRaw("UPDATE \"Users\" SET \"IsAdmin\" = TRUE WHERE \"Id\" = 1 AND NOT EXISTS (SELECT 1 FROM \"Users\" WHERE \"IsAdmin\" = TRUE);");
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS ""MusicEnrichments"" (
            ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ""MediaFileId"" integer NOT NULL,
            ""Provider"" text NOT NULL,
            ""ExternalId"" text NULL,
            ""Confidence"" double precision NOT NULL,
            ""Status"" text NOT NULL,
            ""Details"" text NOT NULL,
            ""CreatedAt"" timestamp with time zone NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_MusicEnrichments_MediaFileId_CreatedAt"" ON ""MusicEnrichments"" (""MediaFileId"", ""CreatedAt"");");

        db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""EnrichmentJobs"" (
            ""Id"" text PRIMARY KEY,
            ""Scope"" text NOT NULL,
            ""RequestedByUserId"" integer NULL,
            ""Total"" integer NOT NULL,
            ""Processed"" integer NOT NULL,
            ""Updated"" integer NOT NULL,
            ""Unmatched"" integer NOT NULL,
            ""Skipped"" integer NOT NULL,
            ""Failed"" integer NOT NULL,
            ""Cursor"" integer NOT NULL,
            ""Status"" text NOT NULL,
            ""SongIdsJson"" text NOT NULL DEFAULT '[]',
            ""StartedAt"" timestamp with time zone NOT NULL,
            ""FinishedAt"" timestamp with time zone NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_EnrichmentJobs_Status"" ON ""EnrichmentJobs"" (""Status"");

        CREATE TABLE IF NOT EXISTS ""EnrichmentAttempts"" (
            ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ""JobId"" text NOT NULL,
            ""MediaFileId"" integer NOT NULL,
            ""Provider"" text NOT NULL,
            ""RequestKey"" text NULL,
            ""HTTPStatus"" integer NULL,
            ""Outcome"" text NOT NULL,
            ""Confidence"" double precision NOT NULL,
            ""RetryCount"" integer NOT NULL,
            ""Detail"" text NOT NULL,
            ""CreatedAt"" timestamp with time zone NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_EnrichmentAttempts_JobId_CreatedAt"" ON ""EnrichmentAttempts"" (""JobId"", ""CreatedAt"");

        CREATE TABLE IF NOT EXISTS ""MediaIdentities"" (
            ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ""MediaFileId"" integer NOT NULL,
            ""Provider"" text NOT NULL,
            ""RecordingId"" text NULL,
            ""ReleaseId"" text NULL,
            ""ArtistId"" text NULL,
            ""ISRC"" text NULL,
            ""AcoustId"" text NULL,
            ""MatchMethod"" text NOT NULL,
            ""Confidence"" double precision NOT NULL,
            ""Status"" text NOT NULL,
            ""MatchedAt"" timestamp with time zone NOT NULL,
            ""LastVerifiedAt"" timestamp with time zone NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_MediaIdentities_Provider_RecordingId"" ON ""MediaIdentities"" (""Provider"", ""RecordingId"");
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MediaIdentities_MediaFileId_Provider"" ON ""MediaIdentities"" (""MediaFileId"", ""Provider"");
        DROP INDEX IF EXISTS ""IX_MediaIdentities_MediaFileId"";

        CREATE TABLE IF NOT EXISTS ""MediaTags"" (
            ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ""MediaFileId"" integer NOT NULL,
            ""Namespace"" text NOT NULL,
            ""Key"" text NOT NULL,
            ""Value"" text NOT NULL,
            ""NumericValue"" double precision NULL,
            ""Confidence"" double precision NOT NULL,
            ""Status"" text NOT NULL,
            ""CreatedAt"" timestamp with time zone NOT NULL,
            ""UpdatedAt"" timestamp with time zone NOT NULL
        );
        DROP INDEX IF EXISTS ""IX_MediaTags_MediaFileId_Namespace_Key"";
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MediaTags_MediaFileId_Namespace_Key"" ON ""MediaTags"" (""MediaFileId"", ""Namespace"", ""Key"");

        CREATE TABLE IF NOT EXISTS ""TagEvidences"" (
            ""Id"" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            ""MediaTagId"" integer NOT NULL,
            ""Source"" text NOT NULL,
            ""SourceId"" text NULL,
            ""EvidenceUrl"" text NULL,
            ""EvidenceText"" text NULL,
            ""RetrievedAt"" timestamp with time zone NOT NULL,
            ""ExpiresAt"" timestamp with time zone NULL,
            ""RawPayload"" text NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_TagEvidences_MediaTagId"" ON ""TagEvidences"" (""MediaTagId"");");
    }
    else if (db.Database.IsSqlite())
    {
        db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""EnrichmentJobs"" (
            ""Id"" TEXT PRIMARY KEY,
            ""Scope"" TEXT NOT NULL,
            ""RequestedByUserId"" INTEGER NULL,
            ""Total"" INTEGER NOT NULL,
            ""Processed"" INTEGER NOT NULL,
            ""Updated"" INTEGER NOT NULL,
            ""Unmatched"" INTEGER NOT NULL,
            ""Skipped"" INTEGER NOT NULL,
            ""Failed"" INTEGER NOT NULL,
            ""Cursor"" INTEGER NOT NULL,
            ""Status"" TEXT NOT NULL,
            ""SongIdsJson"" TEXT NOT NULL DEFAULT '[]',
            ""StartedAt"" TEXT NOT NULL,
            ""FinishedAt"" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_EnrichmentJobs_Status"" ON ""EnrichmentJobs"" (""Status"");

        CREATE TABLE IF NOT EXISTS ""EnrichmentAttempts"" (
            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
            ""JobId"" TEXT NOT NULL,
            ""MediaFileId"" INTEGER NOT NULL,
            ""Provider"" TEXT NOT NULL,
            ""RequestKey"" TEXT NULL,
            ""HTTPStatus"" INTEGER NULL,
            ""Outcome"" TEXT NOT NULL,
            ""Confidence"" REAL NOT NULL,
            ""RetryCount"" INTEGER NOT NULL,
            ""Detail"" TEXT NOT NULL,
            ""CreatedAt"" TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_EnrichmentAttempts_JobId_CreatedAt"" ON ""EnrichmentAttempts"" (""JobId"", ""CreatedAt"");

        CREATE TABLE IF NOT EXISTS ""MediaIdentities"" (
            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
            ""MediaFileId"" INTEGER NOT NULL,
            ""Provider"" TEXT NOT NULL,
            ""RecordingId"" TEXT NULL,
            ""ReleaseId"" TEXT NULL,
            ""ArtistId"" TEXT NULL,
            ""ISRC"" TEXT NULL,
            ""AcoustId"" TEXT NULL,
            ""MatchMethod"" TEXT NOT NULL,
            ""Confidence"" REAL NOT NULL,
            ""Status"" TEXT NOT NULL,
            ""MatchedAt"" TEXT NOT NULL,
            ""LastVerifiedAt"" TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_MediaIdentities_Provider_RecordingId"" ON ""MediaIdentities"" (""Provider"", ""RecordingId"");
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MediaIdentities_MediaFileId_Provider"" ON ""MediaIdentities"" (""MediaFileId"", ""Provider"");
        DROP INDEX IF EXISTS ""IX_MediaIdentities_MediaFileId"";

        CREATE TABLE IF NOT EXISTS ""MediaTags"" (
            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
            ""MediaFileId"" INTEGER NOT NULL,
            ""Namespace"" TEXT NOT NULL,
            ""Key"" TEXT NOT NULL,
            ""Value"" TEXT NOT NULL,
            ""NumericValue"" REAL NULL,
            ""Confidence"" REAL NOT NULL,
            ""Status"" TEXT NOT NULL,
            ""CreatedAt"" TEXT NOT NULL,
            ""UpdatedAt"" TEXT NOT NULL
        );
        DROP INDEX IF EXISTS ""IX_MediaTags_MediaFileId_Namespace_Key"";
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MediaTags_MediaFileId_Namespace_Key"" ON ""MediaTags"" (""MediaFileId"", ""Namespace"", ""Key"");

        CREATE TABLE IF NOT EXISTS ""TagEvidences"" (
            ""Id"" INTEGER PRIMARY KEY AUTOINCREMENT,
            ""MediaTagId"" INTEGER NOT NULL,
            ""Source"" TEXT NOT NULL,
            ""SourceId"" TEXT NULL,
            ""EvidenceUrl"" TEXT NULL,
            ""EvidenceText"" TEXT NULL,
            ""RetrievedAt"" TEXT NOT NULL,
            ""ExpiresAt"" TEXT NULL,
            ""RawPayload"" TEXT NULL
        );");
    }

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
