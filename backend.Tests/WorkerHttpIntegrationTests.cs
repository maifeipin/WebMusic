using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public const string TestJwtKey = "SUPER_SECURE_TEST_JWT_SECRET_KEY_12345678901234567890";
    public const string WorkerPassword = "DedicatedWorkerPass12345!";
    public const string MacWorkerPassword = "MacDedicatedSecretPass12345!";
    public const string NasWorkerPassword = "NasDedicatedSecretPass12345!";
    public const string AdminPassword = "AdminSecurePass12345!";

    public TestWebApplicationFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _connectionString,
                ["Jwt:Key"] = TestJwtKey,
                ["Jwt:Issuer"] = "WebMusicTestIssuer",
                ["Jwt:Audience"] = "WebMusicTestAudience",
                ["BootstrapAdmin:Username"] = "admin",
                ["BootstrapAdmin:Password"] = "BootstrapAdminPass12345!",
                ["AutomationWorker:Username"] = "catalog-worker",
                ["AutomationWorker:Password"] = WorkerPassword,
                ["AutomationWorker:Nodes"] = $"mac={MacWorkerPassword};nas={NasWorkerPassword}",
                ["AutomationBot:Password"] = AdminPassword
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor != null) services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseNpgsql(_connectionString);
            });

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = "WebMusicTestIssuer",
                    ValidAudience = "WebMusicTestAudience",
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtKey))
                };
            });
        });
    }
}

[Trait("Category", "Integration")]
public class WorkerHttpIntegrationTests : IClassFixture<PostgreSqlFixture>, IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly TestWebApplicationFactory _factory;
    private HttpClient _client = null!;

    public WorkerHttpIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
        _factory = new TestWebApplicationFactory(_fixture.Container.GetConnectionString());
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Ensure Worker users exist with dedicated credentials
        EnsureWorkerUser(db, "catalog-worker", TestWebApplicationFactory.WorkerPassword);
        EnsureWorkerUser(db, "catalog-worker-mac", TestWebApplicationFactory.MacWorkerPassword);
        EnsureWorkerUser(db, "catalog-worker-nas", TestWebApplicationFactory.NasWorkerPassword);

        // Ensure Admin user exists
        var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin-user");
        if (admin == null)
        {
            db.Users.Add(new User
            {
                Username = "admin-user",
                PasswordHash = PasswordService.Hash(TestWebApplicationFactory.AdminPassword),
                Role = "Admin",
                IsAdmin = true
            });
        }
        else
        {
            admin.PasswordHash = PasswordService.Hash(TestWebApplicationFactory.AdminPassword);
            admin.Role = "Admin";
            admin.IsAdmin = true;
        }

        await db.SaveChangesAsync();
    }

    private static void EnsureWorkerUser(AppDbContext db, string username, string password)
    {
        var worker = db.Users.FirstOrDefault(u => u.Username == username);
        if (worker == null)
        {
            db.Users.Add(new User
            {
                Username = username,
                PasswordHash = PasswordService.Hash(password),
                Role = "Worker",
                IsAdmin = false
            });
        }
        else
        {
            worker.PasswordHash = PasswordService.Hash(password);
            worker.Role = "Worker";
            worker.IsAdmin = false;
        }
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        var resp = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            username,
            password
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.NotNull(token);
        return token!;
    }

    [Fact]
    public async Task WorkerLogin_WithDedicatedNodeCredentials_BindsServerEnforcedWorkerNodeClaim()
    {
        // Login with dedicated node credentials; server enforces worker_node = user.Username
        var token = await LoginAsync("catalog-worker-mac", TestWebApplicationFactory.MacWorkerPassword);

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(token);

        var workerNodeClaim = jwt.Claims.FirstOrDefault(c => c.Type == "worker_node")?.Value;
        Assert.Equal("catalog-worker-mac", workerNodeClaim);

        var roleClaim = jwt.Claims.FirstOrDefault(c => c.Type == "role" || c.Type == System.Security.Claims.ClaimTypes.Role)?.Value;
        Assert.Equal("Worker", roleClaim);
    }

    [Fact]
    public async Task WorkerLogin_DifferentNodes_ReceiveDistinctServerBoundWorkerNodeClaims()
    {
        // Each node authenticates with its own server-registered credentials
        var macToken = await LoginAsync("catalog-worker-mac", TestWebApplicationFactory.MacWorkerPassword);
        var nasToken = await LoginAsync("catalog-worker-nas", TestWebApplicationFactory.NasWorkerPassword);

        var handler = new JwtSecurityTokenHandler();
        var macClaim = handler.ReadJwtToken(macToken).Claims.FirstOrDefault(c => c.Type == "worker_node")?.Value;
        var nasClaim = handler.ReadJwtToken(nasToken).Claims.FirstOrDefault(c => c.Type == "worker_node")?.Value;

        Assert.Equal("catalog-worker-mac", macClaim);
        Assert.Equal("catalog-worker-nas", nasClaim);
        Assert.NotEqual(macClaim, nasClaim);
    }

    [Fact]
    public async Task Worker_CallingAdminCatalogPreview_Receives403Forbidden()
    {
        var workerToken = await LoginAsync("catalog-worker", TestWebApplicationFactory.WorkerPassword);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/enrichment/catalog/preview");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Worker_CallingWorkerPreview_Receives200OkWithQuotaDetails()
    {
        var workerToken = await LoginAsync("catalog-worker", TestWebApplicationFactory.WorkerPassword);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/enrichment/worker/preview");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("totalEligible", out _));
        Assert.True(json.TryGetProperty("dailyQuota", out var dailyQuota));
        Assert.Equal(2000, dailyQuota.GetInt32());
        Assert.True(json.TryGetProperty("remainingToday", out _));
        Assert.True(json.TryGetProperty("providers", out var providers));
        var hasMb = providers.TryGetProperty("musicBrainz", out _) || providers.TryGetProperty("MusicBrainz", out _);
        var hasCaa = providers.TryGetProperty("coverArtArchive", out _) || providers.TryGetProperty("CoverArtArchive", out _);
        var hasLrc = providers.TryGetProperty("lrclib", out _) || providers.TryGetProperty("LRCLIB", out _);
        Assert.True(hasMb, "Provider MusicBrainz should be present");
        Assert.True(hasCaa, "Provider CoverArtArchive should be present");
        Assert.True(hasLrc, "Provider LRCLIB should be present");
    }

    [Fact]
    public async Task Admin_CallingWorkerPreview_Receives403Forbidden_EnforcingRoleIsolation()
    {
        var adminToken = await LoginAsync("admin-user", TestWebApplicationFactory.AdminPassword);

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/enrichment/worker/preview");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Worker_CallingAdminFavoritesStart_Receives403Forbidden()
    {
        var workerToken = await LoginAsync("catalog-worker", TestWebApplicationFactory.WorkerPassword);

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/enrichment/favorites/start")
        {
            Content = JsonContent.Create(new { batchSize = 20 })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", workerToken);

        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task CoverPromotion_CrashRecovery_StartupReconcilerCleansOrphanCover()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var env = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();

        var coversDir = Path.Combine(env.ContentRootPath, "data", "covers");
        Directory.CreateDirectory(coversDir);

        // 1. Simulate a legitimate committed cover in DB
        var legitFileName = $"enriched-legit-{Guid.NewGuid():N}.jpg";
        var legitFilePath = Path.Combine(coversDir, legitFileName);
        await File.WriteAllBytesAsync(legitFilePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

        var src = await db.ScanSources.FirstOrDefaultAsync() ?? new ScanSource { Name = "S", Path = "/s", Type = "local" };
        if (src.Id == 0) { db.ScanSources.Add(src); await db.SaveChangesAsync(); }

        var media = new MediaFile
        {
            FilePath = $"/test/legit-{Guid.NewGuid():N}.mp3",
            Title = "Legit Song",
            Artist = "Artist",
            Album = "Album",
            Genre = "Pop",
            CoverArt = $"/api/media/cover/{legitFileName}",
            ScanSourceId = src.Id
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        // 2. Simulate a crash between File.Copy and DB commit:
        // An orphan file was copied to covers/ but NEVER committed to MediaFiles
        var orphanFileName = $"enriched-crash-uncommitted-{Guid.NewGuid():N}.jpg";
        var orphanFilePath = Path.Combine(coversDir, orphanFileName);
        await File.WriteAllBytesAsync(orphanFilePath, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 });

        Assert.True(File.Exists(orphanFilePath), "Orphan file must exist prior to reconciliation");
        Assert.True(File.Exists(legitFilePath), "Legit file must exist prior to reconciliation");

        // 3. Run ReconcileOrphanCoversAsync (simulating server startup reconciliation)
        var cleaned = await CoverPromotionReconciler.ReconcileOrphanCoversAsync(db, env, gracePeriodSeconds: 0);

        // 4. Verify that orphan file was cleaned up and legitimate file remains intact
        Assert.True(cleaned >= 1);
        Assert.False(File.Exists(orphanFilePath), "Orphan file left from simulated crash must be deleted by reconciler");
        Assert.True(File.Exists(legitFilePath), "Legitimate committed cover must remain intact");

        // Cleanup
        try { File.Delete(legitFilePath); } catch { }
    }
}
