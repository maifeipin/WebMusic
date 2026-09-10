using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WebMusic.Backend.Data;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.DataProtection;

// EF tooling must not execute migration/bootstrap code from this top-level
// program. AppDbContextFactory supplies the design-time context instead.
if (string.Equals(Environment.GetEnvironmentVariable("WEBMUSIC_EF_DESIGN_TIME"), "1", StringComparison.Ordinal))
{
    return;
}

// Disable default claim mapping to keep claims as 'sub', 'name', etc.
System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
// Fix for Npgsql 6.0+ forcing UTC. Enable legacy behavior to simplify migration.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Suppress verbose EF Core SQL logs and HttpClient URI parameter query logs in console
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

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
builder.Services.AddScoped<WebMusic.Backend.Services.IIdentityImportService, WebMusic.Backend.Services.IdentityImportService>();
builder.Services.AddHttpClient<WebMusic.Backend.Services.ILocalMusicBrainzService, WebMusic.Backend.Services.LocalMusicBrainzService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            if (host.StartsWith('[') && host.EndsWith(']'))
            {
                host = host.Substring(1, host.Length - 2);
            }

            if (!System.Net.IPAddress.TryParse(host, out var ip) || !WebMusic.Backend.Services.LocalMusicBrainzService.IsPrivateOrLoopbackIp(ip))
            {
                throw new InvalidOperationException($"Outbound connection to '{context.DnsEndPoint.Host}' is strictly prohibited. Only private or loopback IP literals are allowed.");
            }

            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });
builder.Services.AddHttpClient<WebMusic.Backend.Services.ILocalMusicBrainzDetailService, WebMusic.Backend.Services.LocalMusicBrainzDetailService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host.Trim('[', ']');
            if (!System.Net.IPAddress.TryParse(host, out var ip) || !WebMusic.Backend.Services.LocalMusicBrainzService.IsPrivateOrLoopbackIp(ip))
            {
                throw new InvalidOperationException($"Outbound connection to '{context.DnsEndPoint.Host}' is strictly prohibited. Only private or loopback IP literals are allowed.");
            }

            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(ip, context.DnsEndPoint.Port, cancellationToken);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });
builder.Services.AddScoped<WebMusic.Backend.Services.ILocalIdentityAutoScanService, WebMusic.Backend.Services.LocalIdentityAutoScanService>();
builder.Services.AddScoped<WebMusic.Backend.Services.IMusicBrainzCommunitySignalService, WebMusic.Backend.Services.MusicBrainzCommunitySignalService>();
builder.Services.AddHttpClient<WebMusic.Backend.Services.ILastFmTrackInfoClient, WebMusic.Backend.Services.LastFmTrackInfoClient>(client =>
    client.BaseAddress = new Uri("https://ws.audioscrobbler.com/"));
builder.Services.AddScoped<WebMusic.Backend.Services.ILastFmGlobalPopularityService, WebMusic.Backend.Services.LastFmGlobalPopularityService>();
builder.Services.AddScoped<WebMusic.Backend.Services.IExternalSignalRefreshService, WebMusic.Backend.Services.ExternalSignalRefreshService>();
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

// Local identity auto-scan CLI. It is deliberately before migrations, account
// bootstrap and cleanup. Production only permits its physical zero-write mode.
if (args.Contains("local-identity-auto-scan", StringComparer.OrdinalIgnoreCase))
{
    using var scanScope = app.Services.CreateScope();
    var scanner = scanScope.ServiceProvider.GetRequiredService<WebMusic.Backend.Services.ILocalIdentityAutoScanService>();
    var count = 100;
    int? afterId = null;
    string? outFile = null;

    for (var index = 0; index < args.Length; index++)
    {
        static int? ReadIntArgument(string[] source, ref int i, string name)
        {
            if (source[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < source.Length && int.TryParse(source[i + 1], out var spaced))
            {
                i++;
                return spaced;
            }
            return source[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) && int.TryParse(source[i][(name.Length + 1)..], out var equals) ? equals : null;
        }

        var parsedCount = ReadIntArgument(args, ref index, "--count");
        if (parsedCount.HasValue) { count = parsedCount.Value; continue; }
        var parsedAfter = ReadIntArgument(args, ref index, "--after-id");
        if (parsedAfter.HasValue) { afterId = parsedAfter; continue; }
        if (args[index].Equals("--out", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length) { outFile = args[++index]; continue; }
        if (args[index].StartsWith("--out=", StringComparison.OrdinalIgnoreCase)) outFile = args[index]["--out=".Length..];
    }

    var persistState = args.Contains("--persist-state", StringComparer.OrdinalIgnoreCase);
    if (persistState && app.Environment.IsProduction())
    {
        throw new InvalidOperationException("Production local-identity-auto-scan is dry-run only. Persisted scan state requires a separately approved release.");
    }

    var request = new WebMusic.Backend.Services.LocalIdentityAutoScanRequest(
        args.Contains("--incremental", StringComparer.OrdinalIgnoreCase)
            ? WebMusic.Backend.Services.LocalIdentityScanMode.Incremental
            : WebMusic.Backend.Services.LocalIdentityScanMode.Full,
        count,
        afterId,
        DryRun: !persistState,
        PersistState: persistState,
        MirrorVersion: app.Configuration["MusicBrainz:MirrorVersion"]);
    var report = await scanner.ScanAsync(request);
    Console.WriteLine($"Local identity auto scan completed: evaluated={report.Evaluated}, matched={report.Matched}, unmatched={report.Unmatched}, skipped={report.Skipped}, failed={report.Failed}");
    Console.WriteLine($"Input SHA-256: {report.InputSha256}");
    Console.WriteLine($"Result SHA-256: {report.ResultSha256}");
    if (!string.IsNullOrWhiteSpace(outFile))
    {
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outFile, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved report -> {outFile}");
    }

    if (report.Failed > 0 && !args.Contains("--allow-partial", StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Scan completed with {report.Failed} failure(s). Exiting with code 1. Pass '--allow-partial' to exit 0.");
        Environment.ExitCode = 1;
    }
    return;
}

// External Signal Refresh CLI Mode (Controlled pipeline for MusicBrainz and Last.fm score refreshes)
if (args.Contains("external-signal-refresh", StringComparer.OrdinalIgnoreCase))
{
    using var refreshScope = app.Services.CreateScope();
    var refresher = refreshScope.ServiceProvider.GetRequiredService<WebMusic.Backend.Services.IExternalSignalRefreshService>();
    var provider = "musicbrainz";
    var count = 100;
    int? afterId = null;
    var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
    var dryRun = args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase);
    var allowPartial = args.Contains("--allow-partial", StringComparer.OrdinalIgnoreCase);
    string? outFile = null;

    for (var index = 0; index < args.Length; index++)
    {
        static int? ReadIntArgument(string[] source, ref int i, string name)
        {
            if (source[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < source.Length && int.TryParse(source[i + 1], out var spaced))
            {
                i++;
                return spaced;
            }
            return source[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase) && int.TryParse(source[i][(name.Length + 1)..], out var equals) ? equals : null;
        }

        if (args[index].Equals("--provider", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
        {
            provider = args[++index];
            continue;
        }
        if (args[index].StartsWith("--provider=", StringComparison.OrdinalIgnoreCase))
        {
            provider = args[index]["--provider=".Length..];
            continue;
        }

        var parsedCount = ReadIntArgument(args, ref index, "--count");
        if (parsedCount.HasValue) { count = parsedCount.Value; continue; }
        var parsedAfter = ReadIntArgument(args, ref index, "--after-id");
        if (parsedAfter.HasValue) { afterId = parsedAfter; continue; }
        if (args[index].Equals("--out", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length) { outFile = args[++index]; continue; }
        if (args[index].StartsWith("--out=", StringComparison.OrdinalIgnoreCase)) outFile = args[index]["--out=".Length..];
    }

    var request = new WebMusic.Backend.Services.ExternalSignalRefreshRequest(provider, count, afterId, force, dryRun);
    var report = await refresher.RefreshAsync(request);
    Console.WriteLine($"External signal refresh completed for {report.Provider}: evaluated={report.Evaluated}, updated={report.Updated}, skipped={report.Skipped}, failed={report.Failed}, continuationId={report.ContinuationAfterMediaFileId}");
    if (report.Aborted)
    {
        Console.WriteLine($"Batch aborted early: {report.StopReason}");
    }
    if (!string.IsNullOrWhiteSpace(outFile))
    {
        var directory = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(outFile, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved report -> {outFile}");
    }

    if ((report.Failed > 0 || report.Aborted) && !allowPartial)
    {
        Console.Error.WriteLine($"External signal refresh completed with failures or abort ({report.Failed} failure(s), aborted={report.Aborted}). Exiting with code 1. Pass '--allow-partial' to exit 0.");
        Environment.ExitCode = 1;
    }
    return;
}

// Local MusicBrainz Shadow Run CLI Mode (ZERO-WRITE: executed BEFORE any migrations, user bootstrap, or cleanup)
if (args.Contains("shadow-run-local") || args.Any(a => a.Equals("--shadow-run", StringComparison.OrdinalIgnoreCase) || a.Equals("--shadow-run-local", StringComparison.OrdinalIgnoreCase)))
{
    using var shadowScope = app.Services.CreateScope();
    var db = shadowScope.ServiceProvider.GetRequiredService<AppDbContext>();
    var localMb = shadowScope.ServiceProvider.GetRequiredService<WebMusic.Backend.Services.ILocalMusicBrainzService>();

    int count = 1000;
    string? outFile = null;

    // Support both '--count 1000' and '--count=1000', '--out file' and '--out=file'
    for (int i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--count", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedCount))
        {
            count = parsedCount;
        }
        else if (arg.StartsWith("--count=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg["--count=".Length..], out var eqCount))
        {
            count = eqCount;
        }

        if (arg.Equals("--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            outFile = args[i + 1];
        }
        else if (arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
        {
            outFile = arg["--out=".Length..];
        }
    }

    if (count <= 0 || count > 1000)
    {
        Console.WriteLine($"❌ Error: --count must be between 1 and 1000 (received: {count})");
        Environment.Exit(1);
    }

    bool onlyUnidentified = !args.Contains("--all");
    bool includeAll = args.Contains("--include-all") || !string.IsNullOrEmpty(outFile);

    Console.WriteLine("=== 🧪 Shadow Run: Local MusicBrainz Identity Scanner (ZERO WRITE) ===");
    Console.WriteLine($"Target Node: {localMb.BaseUrl}");
    Console.WriteLine($"Batch Count: {count} (Max: 1000)");
    Console.WriteLine($"Filter:      {(onlyUnidentified ? "Only tracks lacking MusicBrainzLocal identity" : "All tracks")}");
    Console.WriteLine("Mode:        生产数据库、封面、歌词和身份表零写入（SET TRANSACTION READ ONLY 物理门禁）");
    Console.WriteLine();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var report = await WebMusic.Backend.Services.LocalMusicBrainzShadowRunner.RunAsync(
        db,
        localMb,
        count: count,
        onlyUnidentified: onlyUnidentified,
        includeAllItemsInReport: includeAll,
        progressCallback: (done, total) =>
        {
            if (done % 50 == 0 || done == total)
            {
                Console.WriteLine($"  [{done}/{total}] tracks evaluated ({Math.Round((double)done / total * 100, 1)}%)...");
            }
        }
    );
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine("=== 📊 Shadow Run Summary ===");
    Console.WriteLine($"Total Evaluated:            {report.Summary.TotalEvaluated}");
    Console.WriteLine($"High Confidence (>=0.85):   {report.Summary.HighConfidence} ({report.Summary.HighConfidenceRate:P1})");
    Console.WriteLine($"Proposed Match (0.70-0.85): {report.Summary.Proposed} ({report.Summary.ProposedRate:P1})");
    Console.WriteLine($"Unmatched (<0.70):          {report.Summary.Unmatched} ({report.Summary.UnmatchedRate:P1})");
    Console.WriteLine($"Failed / Errors:            {report.Summary.Failed}");
    Console.WriteLine($"Clips / Derivatives:        {report.Summary.DerivativeOrClip}");
    Console.WriteLine($"Average Response Time:      {report.Summary.AverageElapsedMs} ms/track");
    Console.WriteLine($"Total Wall Time:            {sw.Elapsed.TotalSeconds:F2} s");
    if (!string.IsNullOrEmpty(report.HighConfidenceSha256))
    {
        Console.WriteLine($"HighConfidence SHA-256:     {report.HighConfidenceSha256}");
    }
    Console.WriteLine();

    if (!string.IsNullOrEmpty(outFile))
    {
        var outDir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = System.Text.Json.JsonSerializer.Serialize(report, jsonOptions);
        await System.IO.File.WriteAllTextAsync(outFile, json);
        Console.WriteLine($"💾 Saved full shadow run report -> {outFile}");
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
