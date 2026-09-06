using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
using WebMusic.Backend.Controllers;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class PostgreSqlFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .WithDatabase("webmusic_itest")
        .WithUsername("postgres")
        .WithPassword("itest_secret_pass")
        .Build();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        // 1. Restore tracked production schema baseline fixture
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media_prod_schema.sql");
        if (!File.Exists(fixturePath))
        {
            var current = Directory.GetCurrentDirectory();
            while (current != null)
            {
                var candidate = Path.Combine(current, "backend.Tests", "Fixtures", "media_prod_schema.sql");
                if (File.Exists(candidate)) { fixturePath = candidate; break; }
                current = Directory.GetParent(current)?.FullName;
            }
        }
        Assert.True(File.Exists(fixturePath), $"Tracked baseline fixture missing: {fixturePath}");

        var rawLines = await File.ReadAllLinesAsync(fixturePath);
        var sql = string.Join("\n", rawLines.Where(l => !l.TrimStart().StartsWith("\\")));
        await using (var conn = new Npgsql.NpgsqlConnection(Container.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Mark baseline migration and apply pending migrations
        using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                ""MigrationId"" character varying(150) NOT NULL PRIMARY KEY,
                ""ProductVersion"" character varying(32) NOT NULL
            );
            INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
            VALUES ('20260906112053_Initial_EnrichmentBaseline', '8.0.10')
            ON CONFLICT (""MigrationId"") DO NOTHING;
        ");
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(Container.GetConnectionString())
            .Options;
        return new AppDbContext(options);
    }
}

[Trait("Category", "Integration")]
public class PostgreSqlIntegrationTests : IClassFixture<PostgreSqlFixture>
{
    private readonly PostgreSqlFixture _fixture;

    public PostgreSqlIntegrationTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    private WorkerEnrichmentController CreateWorkerController(AppDbContext db, string? tempFolder = null, string workerNodeId = "worker-1")
    {
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(tempFolder ?? Path.GetTempPath());
        var controller = new WorkerEnrichmentController(db, mockEnv.Object);
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Worker"),
            new System.Security.Claims.Claim("worker_node", workerNodeId),
            new System.Security.Claims.Claim("sub", "1")
        }, "TestAuth"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };
        return controller;
    }

    private async Task<ScanSource> GetOrCreateScanSourceAsync(AppDbContext db)
    {
        var src = await db.ScanSources.FirstOrDefaultAsync();
        if (src == null)
        {
            src = new ScanSource { Name = "PG-Test", Path = "/m", Type = "local" };
            db.ScanSources.Add(src);
            await db.SaveChangesAsync();
        }
        return src;
    }

    private async Task ResetStateAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync(@"
            DELETE FROM ""WorkerSubmissions"";
            DELETE FROM ""EnrichmentJobItems"";
            DELETE FROM ""EnrichmentJobs"";
            DELETE FROM ""ProviderQuotaLedgers"";
            DELETE FROM ""EnrichmentAttempts"";
            DELETE FROM ""MusicEnrichments"";
            DELETE FROM ""MediaIdentities"";
            DELETE FROM ""MediaFiles"";
        ");
    }

    [Fact]
    public async Task DualWorkerConcurrentLease_WithSkipLocked_PartitionsDisjointTracks()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var prefix = Guid.NewGuid().ToString("N")[..8];
        var seededIds = new List<int>();
        for (int i = 1; i <= 20; i++)
        {
            var mf = new MediaFile
            {
                ScanSourceId = scanSource.Id,
                Title = $"{prefix} Song {i}",
                Artist = $"{prefix} Artist {i}",
                FilePath = $"/music/{prefix}_{i}.mp3"
            };
            db.MediaFiles.Add(mf);
            await db.SaveChangesAsync();
            seededIds.Add(mf.Id);
        }

        // Two concurrent workers call LeaseBatch at the exact same moment
        var task1 = Task.Run(async () =>
        {
            using var db1 = _fixture.CreateDbContext();
            var ctrl1 = CreateWorkerController(db1, workerNodeId: "worker-alpha");
            return await ctrl1.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-alpha", BatchSize = 10 });
        });

        var task2 = Task.Run(async () =>
        {
            using var db2 = _fixture.CreateDbContext();
            var ctrl2 = CreateWorkerController(db2, workerNodeId: "worker-beta");
            return await ctrl2.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-beta", BatchSize = 10 });
        });

        var results = await Task.WhenAll(task1, task2);

        var res1 = Assert.IsType<OkObjectResult>(results[0]);
        var res2 = Assert.IsType<OkObjectResult>(results[1]);

        var lease1 = Assert.IsType<WorkerLeaseBatchResponse>(res1.Value);
        var lease2 = Assert.IsType<WorkerLeaseBatchResponse>(res2.Value);

        Assert.NotNull(lease1.BatchId);
        Assert.NotNull(lease2.BatchId);
        Assert.NotEqual(lease1.BatchId, lease2.BatchId);

        var ids1 = lease1.Items.Select(x => x.MediaFileId).ToHashSet();
        var ids2 = lease2.Items.Select(x => x.MediaFileId).ToHashSet();

        // CRITICAL: Both workers MUST receive completely disjoint tracks (FOR UPDATE SKIP LOCKED)
        var overlap = ids1.Intersect(ids2).ToList();
        Assert.Empty(overlap);
    }

    [Fact]
    public async Task PersistentProviderQuotaLedger_EnforcesRequestCap_AndDoesNotResetWhenLeaseExpires()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");

        // Seed provider ledger to 1990 reserved units (allowing 10 units = 5 tracks @ 2 units/track)
        db.ProviderQuotaLedgers.Add(new ProviderQuotaLedger
        {
            Provider = "MusicBrainz",
            Date = todayStr,
            DailyLimit = 2000,
            ReservedUnits = 1990,
            ConsumedUnits = 1990,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        for (int i = 1; i <= 10; i++)
        {
            db.MediaFiles.Add(new MediaFile
            {
                ScanSourceId = scanSource.Id,
                Title = $"Quota Test Song {Guid.NewGuid():N}",
                Artist = "Quota Artist",
                FilePath = $"/quota/{Guid.NewGuid():N}.mp3"
            });
        }
        await db.SaveChangesAsync();

        var controller = CreateWorkerController(db);

        // Worker 1 requests 10, should be clamped to 5 tracks
        var lease1 = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-quota-1", BatchSize = 10 });
        var ok1 = Assert.IsType<OkObjectResult>(lease1);
        var val1 = Assert.IsType<WorkerLeaseBatchResponse>(ok1.Value);
        Assert.Equal(5, val1.Total);

        // Worker 2 immediately requests, should receive 429
        var lease2 = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-quota-2", BatchSize = 10 });
        var status2 = Assert.IsType<ObjectResult>(lease2);
        Assert.Equal(429, status2.StatusCode);

        // Simulate Worker 1's items expiring in the database
        var items = await db.EnrichmentJobItems.Where(i => i.JobId == val1.BatchId).ToListAsync();
        foreach (var it in items)
        {
            it.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-10); // expired
        }
        await db.SaveChangesAsync();

        // Worker 3 requests again: quota MUST STILL BE BLOCKED because ReservedUnits is monotonic
        var lease3 = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-quota-3", BatchSize = 5 });
        var status3 = Assert.IsType<ObjectResult>(lease3);
        Assert.Equal(429, status3.StatusCode);
    }

    [Fact]
    public async Task DatabaseLevelSubmissionIdempotency_PreventsDoubleCounting()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var mf = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = $"Idempotent Track {Guid.NewGuid():N}",
            Artist = "Idempotent Artist",
            FilePath = $"/idem/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(mf);
        await db.SaveChangesAsync();

        var controller = CreateWorkerController(db, workerNodeId: "worker-idem");
        var leaseRes = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-idem", BatchSize = 1 });
        var okLease = Assert.IsType<OkObjectResult>(leaseRes);
        var leaseBatch = Assert.IsType<WorkerLeaseBatchResponse>(okLease.Value);

        var leasedItem = leaseBatch.Items.First();
        var submissionId = Guid.NewGuid().ToString();

        var submitReq = new WorkerSubmitBatchRequest
        {
            BatchId = leaseBatch.BatchId!,
            WorkerNodeId = "worker-idem",
            SubmissionId = submissionId,
            Results = new List<WorkerItemSubmission>
            {
                new WorkerItemSubmission
                {
                    ItemId = leasedItem.ItemId,
                    MediaFileId = leasedItem.MediaFileId,
                    Outcome = "MatchedWithoutAssets",
                    RecordingId = "mb-recording-123",
                    Confidence = 0.95
                }
            }
        };

        // First submit
        var res1 = await controller.SubmitBatch(submitReq);
        var ok1 = Assert.IsType<OkObjectResult>(res1);
        var submit1 = Assert.IsType<WorkerSubmitBatchResponse>(ok1.Value);
        Assert.Equal(1, submit1.Processed);
        Assert.Equal(0, submit1.IgnoredOrExpired);

        // Verify WorkerSubmissions table recorded the unique submission
        var subRecord = await db.WorkerSubmissions.FirstOrDefaultAsync(s => s.ItemId == leasedItem.ItemId && s.SubmissionId == submissionId);
        Assert.NotNull(subRecord);

        // Second submit (same submission ID / duplicate request)
        var res2 = await controller.SubmitBatch(submitReq);
        var ok2 = Assert.IsType<OkObjectResult>(res2);
        var submit2 = Assert.IsType<WorkerSubmitBatchResponse>(ok2.Value);

        // Must be marked IgnoredOrExpired = 1, Processed = 0
        Assert.Equal(0, submit2.Processed);
        Assert.Equal(1, submit2.IgnoredOrExpired);

        // Verify database counters are NOT doubled
        var job = await db.EnrichmentJobs.FindAsync(leaseBatch.BatchId);
        Assert.Equal(1, job!.Processed);
    }

    [Fact]
    public async Task StateMachine_UploadCoverBoundToItemId_AndCompletedRejection()
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), $"webmusic_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);

        try
        {
            using var db = _fixture.CreateDbContext();
            await ResetStateAsync(db);
            var scanSource = await GetOrCreateScanSourceAsync(db);

            var mf = new MediaFile
            {
                ScanSourceId = scanSource.Id,
                Title = $"Upload Song {Guid.NewGuid():N}",
                Artist = "Upload Artist",
                FilePath = $"/upload/{Guid.NewGuid():N}.mp3"
            };
            db.MediaFiles.Add(mf);
            await db.SaveChangesAsync();

            var controller = CreateWorkerController(db, tempFolder, workerNodeId: "worker-uploader");
            var leaseRes = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-uploader", BatchSize = 1 });
            var okLease = Assert.IsType<OkObjectResult>(leaseRes);
            var leaseBatch = Assert.IsType<WorkerLeaseBatchResponse>(okLease.Value);
            var item = leaseBatch.Items.First();

            // 1. Upload valid JPEG payload bound to itemId
            var validJpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
            var httpContextJpeg = new DefaultHttpContext();
            httpContextJpeg.Request.Body = new MemoryStream(validJpeg);
            httpContextJpeg.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Worker"),
                new System.Security.Claims.Claim("worker_node", "worker-uploader"),
                new System.Security.Claims.Claim("sub", "1")
            }, "TestAuth"));
            controller.ControllerContext = new ControllerContext { HttpContext = httpContextJpeg };

            var validRes = await controller.UploadCover(item.ItemId);
            Assert.IsType<OkObjectResult>(validRes);

            // Item state machine should now be in AwaitingAssets, staged cover file exists, but MediaFile.CoverArt is NOT set yet!
            var dbItem = await db.EnrichmentJobItems.FindAsync(item.ItemId);
            Assert.Equal("AwaitingAssets", dbItem!.Status);
            Assert.Equal("Matched", dbItem.CoverStatus);
            Assert.NotNull(dbItem.StagedCoverPath);
            Assert.True(File.Exists(dbItem.StagedCoverPath));

            var dbMediaBefore = await db.MediaFiles.FindAsync(item.MediaFileId);
            Assert.True(string.IsNullOrEmpty(dbMediaBefore!.CoverArt), "MediaFile.CoverArt must NOT be updated prior to submit!");

            // 2. Submit batch transitions item to Completed and atomically promotes staged cover
            var submitRes = await controller.SubmitBatch(new WorkerSubmitBatchRequest
            {
                BatchId = leaseBatch.BatchId!,
                WorkerNodeId = "worker-uploader",
                SubmissionId = Guid.NewGuid().ToString(),
                Results = new List<WorkerItemSubmission>
                {
                    new WorkerItemSubmission
                    {
                        ItemId = item.ItemId,
                        MediaFileId = item.MediaFileId,
                        Outcome = "Matched",
                        Confidence = 0.98
                    }
                }
            });
            Assert.IsType<OkObjectResult>(submitRes);

            var completedItem = await db.EnrichmentJobItems.FindAsync(item.ItemId);
            Assert.Equal("Completed", completedItem!.Status);

            var dbMediaAfter = await db.MediaFiles.FindAsync(item.MediaFileId);
            Assert.False(string.IsNullOrEmpty(dbMediaAfter!.CoverArt), "MediaFile.CoverArt must now be populated upon submit!");

            // 3. Attempting to upload cover to a Completed item MUST be rejected (Conflict 409)
            httpContextJpeg.Request.Body = new MemoryStream(validJpeg);
            var lateUploadRes = await controller.UploadCover(item.ItemId);
            Assert.IsType<ConflictObjectResult>(lateUploadRes);
        }
        finally
        {
            if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true);
        }
    }

    [Fact]
    public async Task DynamicCandidateSelection_AllowsReenrichmentOfCompletedItemsAfterCooldown()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        // Seed track that needs lyrics and cover
        var mf = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = $"Candidate Test {Guid.NewGuid():N}",
            Artist = "Dynamic Artist",
            FilePath = $"/cand/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(mf);
        await db.SaveChangesAsync();

        var currentFp = MusicEnrichmentService.ComputeFingerprint(mf.Title, mf.Artist, mf.Album);

        // Simulate old completed item
        var oldJob = new EnrichmentJob { Id = Guid.NewGuid().ToString("N"), Scope = "Test", Total = 1, Status = "Completed", StartedAt = DateTime.UtcNow.AddDays(-20) };
        db.EnrichmentJobs.Add(oldJob);

        var oldItem = new EnrichmentJobItem
        {
            JobId = oldJob.Id,
            MediaFileId = mf.Id,
            Status = "Completed", // Completed status from previous run
            Outcome = "MatchedWithoutAssets",
            InputFingerprint = currentFp,
            CoverStatus = "Pending",
            LyricsStatus = "Pending",
            CompletedAt = DateTime.UtcNow.AddDays(-20)
        };
        db.EnrichmentJobItems.Add(oldItem);

        // Cooldown that has EXPIRED (e.g. 15 days ago)
        var expiredCooldown = new EnrichmentAttempt
        {
            JobId = oldJob.Id,
            MediaFileId = mf.Id,
            Provider = "MusicBrainz",
            InputFingerprint = currentFp,
            HTTPStatus = 200,
            Outcome = "MatchedWithoutAssets",
            Confidence = 0.95,
            RetryAfter = DateTime.UtcNow.AddDays(-1), // Expired!
            CreatedAt = DateTime.UtcNow.AddDays(-15)
        };
        db.EnrichmentAttempts.Add(expiredCooldown);
        var controller = CreateWorkerController(db, workerNodeId: "worker-dyn");

        // Since cooldown is expired, the track SHOULD be picked up for re-enrichment despite old Completed item!
        var leaseRes = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-dyn", BatchSize = 10 });
        var okLease = Assert.IsType<OkObjectResult>(leaseRes);
        var batch = Assert.IsType<WorkerLeaseBatchResponse>(okLease.Value);

        Assert.Contains(batch.Items, i => i.MediaFileId == mf.Id);
    }

    [Fact]
    public async Task ConcurrentDifferentSubmissions_OnlyOneAcquiresCompletionRight()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var mf = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = $"Atomic Submit Race {Guid.NewGuid():N}",
            Artist = "Race Artist",
            FilePath = $"/race/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(mf);
        await db.SaveChangesAsync();

        var controller = CreateWorkerController(db, workerNodeId: "worker-race");
        var leaseRes = await controller.LeaseBatch(new WorkerLeaseRequest { WorkerNodeId = "worker-race", BatchSize = 1 });
        var okLease = Assert.IsType<OkObjectResult>(leaseRes);
        var leaseBatch = Assert.IsType<WorkerLeaseBatchResponse>(okLease.Value);
        var item = leaseBatch.Items.First();

        var subId1 = Guid.NewGuid().ToString();
        var subId2 = Guid.NewGuid().ToString();

        // Two concurrent submit calls with different SubmissionId for the same leased item
        var task1 = Task.Run(async () =>
        {
            using var db1 = _fixture.CreateDbContext();
            var c1 = CreateWorkerController(db1, workerNodeId: "worker-race");
            return await c1.SubmitBatch(new WorkerSubmitBatchRequest
            {
                BatchId = leaseBatch.BatchId!,
                WorkerNodeId = "worker-race",
                SubmissionId = subId1,
                Results = new List<WorkerItemSubmission>
                {
                    new WorkerItemSubmission
                    {
                        ItemId = item.ItemId,
                        MediaFileId = item.MediaFileId,
                        Outcome = "MatchedWithoutAssets",
                        RecordingId = "rec-1",
                        Confidence = 0.95
                    }
                }
            });
        });

        var task2 = Task.Run(async () =>
        {
            using var db2 = _fixture.CreateDbContext();
            var c2 = CreateWorkerController(db2, workerNodeId: "worker-race");
            return await c2.SubmitBatch(new WorkerSubmitBatchRequest
            {
                BatchId = leaseBatch.BatchId!,
                WorkerNodeId = "worker-race",
                SubmissionId = subId2,
                Results = new List<WorkerItemSubmission>
                {
                    new WorkerItemSubmission
                    {
                        ItemId = item.ItemId,
                        MediaFileId = item.MediaFileId,
                        Outcome = "MatchedWithoutAssets",
                        RecordingId = "rec-2",
                        Confidence = 0.90
                    }
                }
            });
        });

        var results = await Task.WhenAll(task1, task2);
        var r1 = Assert.IsType<WorkerSubmitBatchResponse>(Assert.IsType<OkObjectResult>(results[0]).Value);
        var r2 = Assert.IsType<WorkerSubmitBatchResponse>(Assert.IsType<OkObjectResult>(results[1]).Value);

        Assert.Equal(1, r1.Processed + r2.Processed);
        Assert.Equal(1, r1.IgnoredOrExpired + r2.IgnoredOrExpired);

        // Submissions table must have exactly 1 record for this ItemId
        var recordedSubmissions = await db.WorkerSubmissions.AsNoTracking().Where(s => s.ItemId == item.ItemId).ToListAsync();
        Assert.Single(recordedSubmissions);

        // Job counter must strictly be 1
        var job = await db.EnrichmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == leaseBatch.BatchId);
        Assert.Equal(1, job!.Processed);
    }

    [Fact]
    public async Task ProductionSchemaBaselineDrill_VerifiesFingerprint_AndAppliesMigrations()
    {
        // 1. Create a clean database in the PostgreSQL container for this drill
        var drillDbName = $"prod_drill_{Guid.NewGuid():N}";
        using (var masterDb = _fixture.CreateDbContext())
        {
            await masterDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{drillDbName}\";");
        }

        try
        {
            // Build connection string for the drill database
            var masterConnStr = _fixture.Container.GetConnectionString();
            var drillConnStr = new Npgsql.NpgsqlConnectionStringBuilder(masterConnStr)
            {
                Database = drillDbName
            }.ConnectionString;

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(drillConnStr)
                .Options;

            // 2. Restore MEDIA's schema dump into the clean database from tracked Fixtures
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media_prod_schema.sql");
            if (!File.Exists(fixturePath))
            {
                var current = Directory.GetCurrentDirectory();
                while (current != null)
                {
                    var candidate = Path.Combine(current, "backend.Tests", "Fixtures", "media_prod_schema.sql");
                    if (File.Exists(candidate)) { fixturePath = candidate; break; }
                    current = Directory.GetParent(current)?.FullName;
                }
            }
            Assert.True(File.Exists(fixturePath), $"Tracked baseline fixture missing: {fixturePath}");

            var rawLines = await File.ReadAllLinesAsync(fixturePath);
            var sql = string.Join("\n", rawLines.Where(l => !l.TrimStart().StartsWith("\\")));
            await using (var conn = new Npgsql.NpgsqlConnection(drillConnStr))
            {
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }

            // 3. Verify SchemaFingerprintVerifier against the restored schema
            using (var drillDb = new AppDbContext(options))
            {
                var verifyResult = SchemaFingerprintVerifier.VerifyFingerprint(drillDb);
                Assert.True(verifyResult.Success, $"Baseline verification failed on restored production dump: {string.Join(", ", verifyResult.Errors)}");
                Assert.Equal(16, verifyResult.VerifiedTables.Count);

                // 4. Apply EF Core migrations on top of the verified baseline
                await drillDb.Database.ExecuteSqlRawAsync(@"
                    CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                        ""MigrationId"" character varying(150) NOT NULL,
                        ""ProductVersion"" character varying(32) NOT NULL,
                        CONSTRAINT ""PK___EFMigrationsHistory"" PRIMARY KEY (""MigrationId"")
                    );
                    INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                    VALUES ('20260906112053_Initial_EnrichmentBaseline', '8.0.10')
                    ON CONFLICT DO NOTHING;
                ");

                // Now run pending migrations
                await drillDb.Database.MigrateAsync();

                // 5. Verify new tables and foreign keys exist
                var hasWorkerSubmissions = await drillDb.Database.SqlQueryRaw<int>(
                    @"SELECT 1 AS ""Value"" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'WorkerSubmissions'"
                ).AnyAsync();
                Assert.True(hasWorkerSubmissions, "WorkerSubmissions table must exist after migration");

                var hasLedgers = await drillDb.Database.SqlQueryRaw<int>(
                    @"SELECT 1 AS ""Value"" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'ProviderQuotaLedgers'"
                ).AnyAsync();
                Assert.True(hasLedgers, "ProviderQuotaLedgers table must exist after migration");

                var hasFk = await drillDb.Database.SqlQueryRaw<string>(
                    @"SELECT constraint_name AS ""Value"" FROM information_schema.table_constraints
                      WHERE table_schema = 'public' AND table_name = 'WorkerSubmissions' AND constraint_type = 'FOREIGN KEY'"
                ).FirstOrDefaultAsync();
                Assert.NotNull(hasFk);
            }
        }
        finally
        {
            using var masterDb = _fixture.CreateDbContext();
            await masterDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{drillDbName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task SchemaFingerprint_VerifiesAllBaselineTablesOnPostgreSql()
    {
        var baselineDbName = $"webmusic_fingerprint_{Guid.NewGuid():N}";
        var masterConnStr = _fixture.Container.GetConnectionString();
        using (var masterDb = _fixture.CreateDbContext())
        {
            await masterDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{baselineDbName}\";");
        }

        try
        {
            var drillConnStr = new Npgsql.NpgsqlConnectionStringBuilder(masterConnStr)
            {
                Database = baselineDbName
            }.ConnectionString;

            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media_prod_schema.sql");
            var rawLines = await File.ReadAllLinesAsync(fixturePath);
            var sql = string.Join("\n", rawLines.Where(l => !l.TrimStart().StartsWith("\\")));
            await using (var conn = new Npgsql.NpgsqlConnection(drillConnStr))
            {
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(drillConnStr)
                .Options;

            using var db = new AppDbContext(options);
            var result = SchemaFingerprintVerifier.VerifyFingerprint(db);
            Assert.True(result.Success, $"Verification failed: {string.Join(", ", result.Errors)}");
            Assert.Equal(16, result.VerifiedTables.Count);
        }
        finally
        {
            using var masterDb = _fixture.CreateDbContext();
            await masterDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{baselineDbName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task SchemaFingerprint_RejectsUnexpectedExtraColumn()
    {
        var dbName = $"webmusic_extra_col_{Guid.NewGuid():N}";
        var masterConnStr = _fixture.Container.GetConnectionString();
        using (var masterDb = _fixture.CreateDbContext())
        {
            await masterDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{dbName}\";");
        }

        try
        {
            var connStr = new Npgsql.NpgsqlConnectionStringBuilder(masterConnStr) { Database = dbName }.ConnectionString;
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media_prod_schema.sql");
            var rawLines = await File.ReadAllLinesAsync(fixturePath);
            var sql = string.Join("\n", rawLines.Where(l => !l.TrimStart().StartsWith("\\")));
            await using (var conn = new Npgsql.NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                // Inject unauthorized extra column
                await using var cmdExtra = new Npgsql.NpgsqlCommand("ALTER TABLE public.\"Users\" ADD COLUMN \"UnauthorizedColumn\" text;", conn);
                await cmdExtra.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connStr).Options;
            using var db = new AppDbContext(options);
            var result = SchemaFingerprintVerifier.VerifyFingerprint(db);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("unexpected extra column: 'UnauthorizedColumn'"));
        }
        finally
        {
            using var masterDb = _fixture.CreateDbContext();
            await masterDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task SchemaFingerprint_RejectsForeignKeyDeleteRuleMismatch()
    {
        var dbName = $"webmusic_fk_mismatch_{Guid.NewGuid():N}";
        var masterConnStr = _fixture.Container.GetConnectionString();
        using (var masterDb = _fixture.CreateDbContext())
        {
            await masterDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{dbName}\";");
        }

        try
        {
            var connStr = new Npgsql.NpgsqlConnectionStringBuilder(masterConnStr) { Database = dbName }.ConnectionString;
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media_prod_schema.sql");
            var rawLines = await File.ReadAllLinesAsync(fixturePath);
            var sql = string.Join("\n", rawLines.Where(l => !l.TrimStart().StartsWith("\\")));
            await using (var conn = new Npgsql.NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                // Alter foreign key to have NO ACTION instead of CASCADE
                await using var cmdFk = new Npgsql.NpgsqlCommand(@"
                    ALTER TABLE public.""Favorites"" DROP CONSTRAINT ""FK_Favorites_MediaFiles_MediaFileId"";
                    ALTER TABLE public.""Favorites"" ADD CONSTRAINT ""FK_Favorites_MediaFiles_MediaFileId""
                        FOREIGN KEY (""MediaFileId"") REFERENCES public.""MediaFiles""(""Id"") ON DELETE NO ACTION;
                ", conn);
                await cmdFk.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connStr).Options;
            using var db = new AppDbContext(options);
            var result = SchemaFingerprintVerifier.VerifyFingerprint(db);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("delete rule mismatch"));
        }
        finally
        {
            using var masterDb = _fixture.CreateDbContext();
            await masterDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task SchemaFingerprint_RejectsIndexUniquenessMismatch()
    {
        var dbName = $"webmusic_idx_mismatch_{Guid.NewGuid():N}";
        var masterConnStr = _fixture.Container.GetConnectionString();
        using (var masterDb = _fixture.CreateDbContext())
        {
            await masterDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{dbName}\";");
        }

        try
        {
            var connStr = new Npgsql.NpgsqlConnectionStringBuilder(masterConnStr) { Database = dbName }.ConnectionString;
            var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "media_prod_schema.sql");
            var rawLines = await File.ReadAllLinesAsync(fixturePath);
            var sql = string.Join("\n", rawLines.Where(l => !l.TrimStart().StartsWith("\\")));
            await using (var conn = new Npgsql.NpgsqlConnection(connStr))
            {
                await conn.OpenAsync();
                await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();

                // Alter unique index IX_MediaFiles_FilePath to non-unique
                await using var cmdIdx = new Npgsql.NpgsqlCommand(@"
                    DROP INDEX public.""IX_MediaFiles_FilePath"";
                    CREATE INDEX ""IX_MediaFiles_FilePath"" ON public.""MediaFiles"" USING btree (""FilePath"");
                ", conn);
                await cmdIdx.ExecuteNonQueryAsync();
            }

            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connStr).Options;
            using var db = new AppDbContext(options);
            var result = SchemaFingerprintVerifier.VerifyFingerprint(db);

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("uniqueness mismatch"));
        }
        finally
        {
            using var masterDb = _fixture.CreateDbContext();
            await masterDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }
}
