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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
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
        Assert.Equal(1, submit1.MatchedWithoutAssets);
        Assert.Equal(0, submit1.Unmatched);

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
        Assert.Equal(1, job.MatchedWithoutAssets);
        Assert.Equal(0, job.Unmatched);
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
    public async Task TargetedLease_PostgreSql_HonorsFullEligibilityAndRejection()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        // 1. Valid incomplete track
        var validTrack = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = "Adele Song",
            Artist = "Adele",
            FilePath = $"/adele/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(validTrack);

        // 2. Complete track (has cover and lyrics)
        var completeTrack = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = "Complete Song",
            Artist = "Complete Artist",
            CoverArt = "data/covers/some.jpg",
            FilePath = $"/comp/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(completeTrack);

        // 3. Unknown title track
        var unknownTrack = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = "Unknown Track 1",
            Artist = "Valid Artist",
            FilePath = $"/unk/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(unknownTrack);

        // 4. Cooldown track
        var cooldownTrack = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = "Cooldown Song",
            Artist = "Cooldown Artist",
            Album = "Album X",
            FilePath = $"/cool/{Guid.NewGuid():N}.mp3"
        };
        db.MediaFiles.Add(cooldownTrack);
        await db.SaveChangesAsync();

        db.Lyrics.Add(new Lyric
        {
            MediaFileId = completeTrack.Id,
            Content = "[00:01.00] lyrics",
            Source = "LRCLIB",
            CreatedAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
        });

        var fp = MusicEnrichmentService.ComputeFingerprint("Cooldown Song", "Cooldown Artist", "Album X");
        db.EnrichmentAttempts.Add(new EnrichmentAttempt
        {
            JobId = "job-cd",
            MediaFileId = cooldownTrack.Id,
            Provider = "MusicBrainz",
            InputFingerprint = fp,
            Outcome = "Failed",
            RetryCount = 1,
            RetryAfter = DateTime.UtcNow.AddDays(20),
            CreatedAt = DateTime.UtcNow.AddDays(-10)
        });
        await db.SaveChangesAsync();

        var controller = CreateWorkerController(db, workerNodeId: "worker-targeted");

        // Positive test: valid track leases cleanly
        var resValid = await controller.LeaseBatch(new WorkerLeaseRequest
        {
            WorkerNodeId = "worker-targeted",
            SpecificMediaFileId = validTrack.Id
        });
        var batchValid = Assert.IsType<WorkerLeaseBatchResponse>(Assert.IsType<OkObjectResult>(resValid).Value);
        Assert.Equal(1, batchValid.Total);
        Assert.Equal(validTrack.Id, batchValid.Items.Single().MediaFileId);

        // Negative 1: Complete track must be rejected
        var resComp = await controller.LeaseBatch(new WorkerLeaseRequest
        {
            WorkerNodeId = "worker-targeted",
            SpecificMediaFileId = completeTrack.Id
        });
        var batchComp = Assert.IsType<WorkerLeaseBatchResponse>(Assert.IsType<OkObjectResult>(resComp).Value);
        Assert.Equal(0, batchComp.Total);
        Assert.Contains("ineligible", batchComp.Message, StringComparison.OrdinalIgnoreCase);

        // Negative 2: Unknown title track must be rejected
        var resUnk = await controller.LeaseBatch(new WorkerLeaseRequest
        {
            WorkerNodeId = "worker-targeted",
            SpecificMediaFileId = unknownTrack.Id
        });
        var batchUnk = Assert.IsType<WorkerLeaseBatchResponse>(Assert.IsType<OkObjectResult>(resUnk).Value);
        Assert.Equal(0, batchUnk.Total);

        // Negative 3: Cooldown track must be rejected
        var resCool = await controller.LeaseBatch(new WorkerLeaseRequest
        {
            WorkerNodeId = "worker-targeted",
            SpecificMediaFileId = cooldownTrack.Id
        });
        var batchCool = Assert.IsType<WorkerLeaseBatchResponse>(Assert.IsType<OkObjectResult>(resCool).Value);
        Assert.Equal(0, batchCool.Total);
        Assert.Contains("ineligible", batchCool.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task Migration_AddMatchedWithoutAssets_UpBackfillsHistoricalJobs_AndDownCompensatesUnmatched()
    {
        var dbName = $"webmusic_migration_backfill_{Guid.NewGuid():N}";
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
            }

            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connStr).Options;
            using var db = new AppDbContext(options);

            // 1. Mark baseline migration
            await db.Database.ExecuteSqlRawAsync(@"
                CREATE TABLE IF NOT EXISTS ""__EFMigrationsHistory"" (
                    ""MigrationId"" character varying(150) NOT NULL PRIMARY KEY,
                    ""ProductVersion"" character varying(32) NOT NULL
                );
                INSERT INTO ""__EFMigrationsHistory"" (""MigrationId"", ""ProductVersion"")
                VALUES ('20260906112053_Initial_EnrichmentBaseline', '8.0.10')
                ON CONFLICT DO NOTHING;
            ");

            // 2. Migrate to previous migration (before AddMatchedWithoutAssetsToEnrichmentJob)
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260906122244_HardenWorkerProtocolAndProviderLedger");

            // Verify MatchedWithoutAssets column does NOT exist yet in pre-migration schema
            var preColCheck = await db.Database.SqlQueryRaw<int>(@"
                SELECT 1 AS ""Value""
                FROM information_schema.columns
                WHERE table_name = 'EnrichmentJobs' AND column_name = 'MatchedWithoutAssets'
            ").AnyAsync();
            Assert.False(preColCheck, "MatchedWithoutAssets column must NOT exist before migration");

            // 3. Insert historical data:
            // Job 1 had 2 tracks: 1 was MatchedWithoutAssets, 1 was truly Unmatched.
            // Old code counted both as Unmatched, so Unmatched = 2.
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""EnrichmentJobs"" (""Id"", ""Scope"", ""Total"", ""Processed"", ""Updated"", ""Unmatched"", ""Skipped"", ""Failed"", ""Cursor"", ""Status"", ""SongIdsJson"", ""StartedAt"")
                VALUES ('job-hist-1', 'Catalog', 2, 2, 0, 2, 0, 0, 2, 'Completed', '[]', NOW());

                INSERT INTO ""EnrichmentJobs"" (""Id"", ""Scope"", ""Total"", ""Processed"", ""Updated"", ""Unmatched"", ""Skipped"", ""Failed"", ""Cursor"", ""Status"", ""SongIdsJson"", ""StartedAt"")
                VALUES ('job-hist-2', 'Catalog', 1, 1, 0, 1, 0, 0, 1, 'Completed', '[]', NOW());
            ");

            // Insert attempts: job-hist-1 has 1 MatchedWithoutAssets, 1 Unmatched.
            // job-hist-2 has 1 MatchedWithoutAssets (like ID 99).
            await db.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""EnrichmentAttempts"" (""JobId"", ""MediaFileId"", ""Provider"", ""Outcome"", ""Confidence"", ""RetryCount"", ""Detail"", ""CreatedAt"")
                VALUES ('job-hist-1', 991, 'MusicBrainz', 'MatchedWithoutAssets', 0.95, 0, 'No cover', NOW());

                INSERT INTO ""EnrichmentAttempts"" (""JobId"", ""MediaFileId"", ""Provider"", ""Outcome"", ""Confidence"", ""RetryCount"", ""Detail"", ""CreatedAt"")
                VALUES ('job-hist-1', 992, 'MusicBrainz', 'Unmatched', 0.30, 0, 'Score too low', NOW());

                INSERT INTO ""EnrichmentAttempts"" (""JobId"", ""MediaFileId"", ""Provider"", ""Outcome"", ""Confidence"", ""RetryCount"", ""Detail"", ""CreatedAt"")
                VALUES ('job-hist-2', 99, 'MusicBrainz', 'MatchedWithoutAssets', 1.00, 0, 'No cover or lyrics', NOW());
            ");

            // 4. Upgrade: Apply new migration containing the data repair SQL
            await migrator.MigrateAsync("20260907074500_AddMatchedWithoutAssetsToEnrichmentJob");

            // 5. Assert: Column exists and data has been correctly backfilled
            var postColCheck = await db.Database.SqlQueryRaw<int>(@"
                SELECT 1 AS ""Value""
                FROM information_schema.columns
                WHERE table_name = 'EnrichmentJobs' AND column_name = 'MatchedWithoutAssets'
            ").AnyAsync();
            Assert.True(postColCheck, "MatchedWithoutAssets column must exist after migration");

            var job1AfterUp = await db.Database.SqlQueryRaw<int>(@"
                SELECT ""MatchedWithoutAssets"" AS ""Value"" FROM ""EnrichmentJobs"" WHERE ""Id"" = 'job-hist-1'
            ").SingleAsync();
            var job1UnmatchedAfterUp = await db.Database.SqlQueryRaw<int>(@"
                SELECT ""Unmatched"" AS ""Value"" FROM ""EnrichmentJobs"" WHERE ""Id"" = 'job-hist-1'
            ").SingleAsync();
            Assert.Equal(1, job1AfterUp);
            Assert.Equal(1, job1UnmatchedAfterUp); // 2 - 1 = 1

            var job2AfterUp = await db.Database.SqlQueryRaw<int>(@"
                SELECT ""MatchedWithoutAssets"" AS ""Value"" FROM ""EnrichmentJobs"" WHERE ""Id"" = 'job-hist-2'
            ").SingleAsync();
            var job2UnmatchedAfterUp = await db.Database.SqlQueryRaw<int>(@"
                SELECT ""Unmatched"" AS ""Value"" FROM ""EnrichmentJobs"" WHERE ""Id"" = 'job-hist-2'
            ").SingleAsync();
            Assert.Equal(1, job2AfterUp);
            Assert.Equal(0, job2UnmatchedAfterUp); // 1 - 1 = 0

            // 6. Rollback: Down migration restores legacy semantics
            await migrator.MigrateAsync("20260906122244_HardenWorkerProtocolAndProviderLedger");

            var postRollbackColCheck = await db.Database.SqlQueryRaw<int>(@"
                SELECT 1 AS ""Value""
                FROM information_schema.columns
                WHERE table_name = 'EnrichmentJobs' AND column_name = 'MatchedWithoutAssets'
            ").AnyAsync();
            Assert.False(postRollbackColCheck, "MatchedWithoutAssets column must be removed after rollback");

            var job1AfterDown = await db.Database.SqlQueryRaw<int>(@"
                SELECT ""Unmatched"" AS ""Value"" FROM ""EnrichmentJobs"" WHERE ""Id"" = 'job-hist-1'
            ").SingleAsync();
            var job2AfterDown = await db.Database.SqlQueryRaw<int>(@"
                SELECT ""Unmatched"" AS ""Value"" FROM ""EnrichmentJobs"" WHERE ""Id"" = 'job-hist-2'
            ").SingleAsync();
            Assert.Equal(2, job1AfterDown); // Restored to 2
            Assert.Equal(1, job2AfterDown); // Restored to 1
        }
        finally
        {
            using var masterDb = _fixture.CreateDbContext();
            await masterDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE);");
        }
    }

    [Fact]
    public async Task ShadowRun_Enforces_ReadOnly_Transaction_And_Computes_Sha256()
    {
        using var db = _fixture.CreateDbContext();
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var sample = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = "Shadow Test Song " + Guid.NewGuid().ToString("N"),
            Artist = "Shadow Test Artist",
            FilePath = "/test/shadow.mp3",
            Duration = TimeSpan.FromSeconds(200),
            AddedAt = DateTime.UtcNow
        };
        db.MediaFiles.Add(sample);
        await db.SaveChangesAsync();

        // 1. Verify that during Shadow Run's SET TRANSACTION READ ONLY, any attempt to write is rejected by PostgreSQL with SQLSTATE 25006
        var rogueMb = new Mock<ILocalMusicBrainzService>();
        rogueMb.Setup(m => m.BaseUrl).Returns("http://192.168.2.18:5050");
        rogueMb.Setup(m => m.ScanMediaIdentityAsync(It.IsAny<MediaFile>(), It.IsAny<CancellationToken>()))
            .Returns<MediaFile, CancellationToken>(async (mf, ct) =>
            {
                // Attempt a rogue write during shadow run
                await db.Database.ExecuteSqlRawAsync($@"
                    INSERT INTO ""MediaIdentities"" (""MediaFileId"", ""Provider"", ""RecordingId"", ""Status"", ""Confidence"", ""MatchMethod"", ""CoverStatus"", ""LyricsStatus"", ""MatchedAt"")
                    VALUES ({sample.Id}, 'MusicBrainzLocal', 'rogue-mbid', 'approved', 0.99, 'Test', 'Pending', 'Pending', NOW());
                ", ct);
                return new LocalMusicBrainzScanResult(true, null, 200, null, 10);
            });

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
        {
            await LocalMusicBrainzShadowRunner.RunAsync(
                db,
                rogueMb.Object,
                count: 10,
                onlyUnidentified: false,
                includeAllItemsInReport: true
            );
        });
        Assert.Equal("25006", ex.SqlState); // SQLSTATE 25006: read_only_sql_transaction

        // 2. Normal execution succeeds and generates stable SHA-256
        var mockMb = new Mock<ILocalMusicBrainzService>();
        mockMb.Setup(m => m.BaseUrl).Returns("http://192.168.2.18:5050");
        mockMb.Setup(m => m.ScanMediaIdentityAsync(It.IsAny<MediaFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalMusicBrainzScanResult(
                true,
                new LocalMusicBrainzCandidate("mbid-shadow-1", null, null, sample.Title, sample.Artist, TimeSpan.FromSeconds(200), null, 0.95, false),
                200,
                null,
                50
            ));

        var report = await LocalMusicBrainzShadowRunner.RunAsync(
            db,
            mockMb.Object,
            count: 10,
            onlyUnidentified: false,
            includeAllItemsInReport: true
        );

        Assert.True(report.Summary.HighConfidence >= 1);
        Assert.NotNull(report.HighConfidenceSha256);
        Assert.Equal(64, report.HighConfidenceSha256.Length); // Valid 64-character SHA-256 hex

        // 3. Verify zero writes occurred
        var identitiesCount = await db.MediaIdentities.CountAsync();
        Assert.Equal(0, identitiesCount);
    }

    [Fact]
    public async Task ShadowRun_CLI_Simulation_Guarantees_Zero_Writes_And_No_Bootstrap_Drift()
    {
        using var db = _fixture.CreateDbContext();
        var scanSource = await GetOrCreateScanSourceAsync(db);

        // Seed initial media file
        var sample = new MediaFile
        {
            ScanSourceId = scanSource.Id,
            Title = "CLI Shadow Song " + Guid.NewGuid().ToString("N"),
            Artist = "CLI Shadow Artist",
            FilePath = "/test/cli_shadow.mp3",
            Duration = TimeSpan.FromSeconds(180),
            AddedAt = DateTime.UtcNow
        };
        db.MediaFiles.Add(sample);
        await db.SaveChangesAsync();

        // 1. Snapshot initial state before CLI execution
        var initialMigrations = await db.Database.SqlQueryRaw<string>(@"SELECT ""MigrationId"" AS ""Value"" FROM ""__EFMigrationsHistory"" ORDER BY ""MigrationId""").ToListAsync();
        var initialUsers = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Username, u.PasswordHash, u.Role, u.IsAdmin }).ToListAsync();
        var initialMediaFilesCount = await db.MediaFiles.CountAsync();
        var initialMediaIdentitiesCount = await db.MediaIdentities.CountAsync();
        var initialLyricsCount = await db.Lyrics.CountAsync();
        var initialWorkerSubmissionsCount = await db.WorkerSubmissions.CountAsync();
        var initialEnrichmentJobsCount = await db.EnrichmentJobs.CountAsync();

        // Create dummy cover file in temp directory
        var tempCoverDir = Path.Combine(Path.GetTempPath(), "cli_cover_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempCoverDir);
        var dummyCoverFile = Path.Combine(tempCoverDir, "orphan_test_cover.jpg");
        await File.WriteAllTextAsync(dummyCoverFile, "fake-image-bytes");

        var tempReportFile = Path.Combine(Path.GetTempPath(), "cli_report_" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var mockMb = new Mock<ILocalMusicBrainzService>();
            mockMb.Setup(m => m.BaseUrl).Returns("http://192.168.2.18:5050");
            mockMb.Setup(m => m.ScanMediaIdentityAsync(It.IsAny<MediaFile>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LocalMusicBrainzScanResult(
                    true,
                    new LocalMusicBrainzCandidate("mbid-cli-1", null, null, sample.Title, sample.Artist, TimeSpan.FromSeconds(180), null, 0.98, false),
                    200,
                    null,
                    45
                ));

            // Execute shadow runner (simulating CLI shadow-run-local execution)
            var report = await LocalMusicBrainzShadowRunner.RunAsync(
                db,
                mockMb.Object,
                count: 5,
                onlyUnidentified: true,
                includeAllItemsInReport: true
            );

            // Write report file as CLI would
            var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(tempReportFile, json);

            // Verify report was written and has high confidence SHA-256
            Assert.True(File.Exists(tempReportFile));
            Assert.NotNull(report.HighConfidenceSha256);
            Assert.True(report.Summary.HighConfidence >= 1);

            // 2. Strict assertions: NO changes to migrations, users, password hashes, or tables
            var currentMigrations = await db.Database.SqlQueryRaw<string>(@"SELECT ""MigrationId"" AS ""Value"" FROM ""__EFMigrationsHistory"" ORDER BY ""MigrationId""").ToListAsync();
            Assert.Equal(initialMigrations, currentMigrations);

            var currentUsers = await db.Users.AsNoTracking().Select(u => new { u.Id, u.Username, u.PasswordHash, u.Role, u.IsAdmin }).ToListAsync();
            Assert.Equal(initialUsers.Count, currentUsers.Count);
            for (int i = 0; i < initialUsers.Count; i++)
            {
                Assert.Equal(initialUsers[i].Username, currentUsers[i].Username);
                Assert.Equal(initialUsers[i].PasswordHash, currentUsers[i].PasswordHash);
            }

            Assert.Equal(initialMediaFilesCount, await db.MediaFiles.CountAsync());
            Assert.Equal(initialMediaIdentitiesCount, await db.MediaIdentities.CountAsync());
            Assert.Equal(initialLyricsCount, await db.Lyrics.CountAsync());
            Assert.Equal(initialWorkerSubmissionsCount, await db.WorkerSubmissions.CountAsync());
            Assert.Equal(initialEnrichmentJobsCount, await db.EnrichmentJobs.CountAsync());

            // Cover directory dummy file must NOT be reconciled or deleted
            Assert.True(File.Exists(dummyCoverFile), "Cover files must not be touched during CLI shadow run");
        }
        finally
        {
            if (File.Exists(tempReportFile)) File.Delete(tempReportFile);
            if (Directory.Exists(tempCoverDir)) Directory.Delete(tempCoverDir, true);
        }
    }

    private (string reportId, Microsoft.Extensions.Configuration.IConfiguration config) CreateIntegrationTestReport(string reportId, List<ShadowRunAuditItem> items, List<int>? approvedIds = null)
    {
        var cleanId = IdentityImportService.NormalizeReportId(reportId);
        var tempDir = Path.Combine(Path.GetTempPath(), "webmusic_itest_reports_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var reportPath = Path.Combine(tempDir, $"{cleanId}.json");
        var report = new ShadowRunReport(
            Mode: "SHADOW_RUN_ZERO_WRITE",
            TargetNode: "http://127.0.0.1:5050",
            Timestamp: DateTime.UtcNow,
            Summary: new ShadowRunSummary(
                TotalEvaluated: items.Count,
                HighConfidence: items.Count(i => i.Outcome == "HighConfidence"),
                HighConfidenceRate: 1.0,
                Proposed: 0,
                ProposedRate: 0,
                Unmatched: 0,
                UnmatchedRate: 0,
                Failed: 0,
                DerivativeOrClip: 0,
                AverageElapsedMs: 10
            ),
            Samples: new Dictionary<string, List<ShadowRunAuditItem>>
            {
                ["highConfidence"] = items.Where(i => i.Outcome == "HighConfidence").ToList()
            },
            AllItems: items,
            HighConfidenceSha256: "itest_high_conf_sha256"
        );
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(reportPath, json);

        var auditPath = Path.Combine(tempDir, $"{cleanId}_audit.md");
        var idsList = approvedIds ?? items.Where(i => i.Outcome == "HighConfidence").Select(i => i.MediaId).ToList();
        var auditContent = $@"# Audit Record
- Report: {cleanId}

## 可写入候选 ({idsList.Count})
```text
{string.Join(", ", idsList)}
```
";
        File.WriteAllText(auditPath, auditContent);

        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IdentityImport:ReportsDirectory"] = tempDir
            })
            .Build();

        return (cleanId, config);
    }

    [Fact]
    public async Task IdentityImport_Migration_UpDown_Succeeds()
    {
        using var db = _fixture.CreateDbContext();
        var migrator = db.Database.GetService<IMigrator>();

        // Rollback down to previous migration
        await migrator.MigrateAsync("20260907074500_AddMatchedWithoutAssetsToEnrichmentJob");

        var hasBatchesDown = await db.Database.SqlQueryRaw<bool>(
            @"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'IdentityImportBatches') AS ""Value"""
        ).FirstAsync();
        Assert.False(hasBatchesDown, "IdentityImportBatches table must not exist after migration Down");

        var hasItemsDown = await db.Database.SqlQueryRaw<bool>(
            @"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'IdentityImportItems') AS ""Value"""
        ).FirstAsync();
        Assert.False(hasItemsDown, "IdentityImportItems table must not exist after migration Down");

        // Re-apply migration Up to latest
        await migrator.MigrateAsync();

        var hasBatchesUp = await db.Database.SqlQueryRaw<bool>(
            @"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'IdentityImportBatches') AS ""Value"""
        ).FirstAsync();
        Assert.True(hasBatchesUp, "IdentityImportBatches table must exist after migration Up");

        var hasItemsUp = await db.Database.SqlQueryRaw<bool>(
            @"SELECT EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'IdentityImportItems') AS ""Value"""
        ).FirstAsync();
        Assert.True(hasItemsUp, "IdentityImportItems table must exist after migration Up");
    }

    [Fact]
    public async Task IdentityImport_ConcurrentApply_OnlyOneSucceeds()
    {
        using var dbSetup = _fixture.CreateDbContext();
        await ResetStateAsync(dbSetup);
        var scanSource = await GetOrCreateScanSourceAsync(dbSetup);

        var file1 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Con Track 1", Artist = "Con Artist", Album = "Con Album", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/c1.mp3" };
        var file2 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Con Track 2", Artist = "Con Artist", Album = "Con Album", Duration = TimeSpan.FromSeconds(210), FilePath = "/m/c2.mp3" };
        dbSetup.MediaFiles.AddRange(file1, file2);
        await dbSetup.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(file1.Id, file1.Title, file1.Artist, file1.Album, 200, "Tier1", 1000, "HighConfidence", 1.0, 10, "mbid-c1", file1.Title, file1.Artist, 200, null, false, null),
            new(file2.Id, file2.Title, file2.Artist, file2.Album, 210, "Tier1", 1000, "HighConfidence", 0.99, 10, "mbid-c2", file2.Title, file2.Artist, 210, null, false, null)
        };
        var (reportId, config) = CreateIntegrationTestReport("concurrent_apply_report", reportItems);

        var serviceSetup = new IdentityImportService(dbSetup, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);
        var batch = await serviceSetup.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest(reportId, new List<int> { file1.Id, file2.Id }), "admin");
        await serviceSetup.ApproveBatchAsync(batch.Id, "admin");

        // Concurrent apply from two distinct DbContext instances
        using var db1 = _fixture.CreateDbContext();
        using var db2 = _fixture.CreateDbContext();
        var s1 = new IdentityImportService(db1, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);
        var s2 = new IdentityImportService(db2, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);

        var task1 = Task.Run(() => s1.ApplyBatchAsync(batch.Id, "admin_user_1"));
        var task2 = Task.Run(() => s2.ApplyBatchAsync(batch.Id, "admin_user_2"));

        await Task.WhenAll(task1.ContinueWith(_ => { }), task2.ContinueWith(_ => { }));

        var tasks = new[] { task1, task2 };
        int successCount = tasks.Count(t => t.IsCompletedSuccessfully);
        int faultCount = tasks.Count(t => t.IsFaulted);

        Assert.Equal(1, successCount);
        Assert.Equal(1, faultCount);

        // Verify that database has exactly 2 identities (one per track), no duplicates
        using var dbVerify = _fixture.CreateDbContext();
        var insertedIdentities = await dbVerify.MediaIdentities.Where(i => i.Provider == "MusicBrainzLocal").ToListAsync();
        Assert.Equal(2, insertedIdentities.Count);
    }

    [Fact]
    public async Task IdentityImport_FingerprintDrift_CausesWholeBatchZeroWrite()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var file1 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Drift Track 1", Artist = "Artist 1", Album = "Album 1", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/d1.mp3" };
        var file2 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Drift Track 2", Artist = "Artist 2", Album = "Album 2", Duration = TimeSpan.FromSeconds(220), FilePath = "/m/d2.mp3" };
        db.MediaFiles.AddRange(file1, file2);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(file1.Id, file1.Title, file1.Artist, file1.Album, 200, "Tier1", 1000, "HighConfidence", 1.0, 10, "mbid-d1", file1.Title, file1.Artist, 200, null, false, null),
            new(file2.Id, file2.Title, file2.Artist, file2.Album, 220, "Tier1", 1000, "HighConfidence", 1.0, 10, "mbid-d2", file2.Title, file2.Artist, 220, null, false, null)
        };
        var (reportId, config) = CreateIntegrationTestReport("drift_itest_report", reportItems);

        var service = new IdentityImportService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);
        var batch = await service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest(reportId, new List<int> { file1.Id, file2.Id }), "admin");
        await service.ApproveBatchAsync(batch.Id, "admin");

        // Tamper album on file2 (metadata drift)
        file2.Album = "Drifted Album Name By User";
        await db.SaveChangesAsync();

        var initialCount = await db.MediaIdentities.CountAsync();

        // Apply must fail with atomic rollback
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyBatchAsync(batch.Id, "admin"));
        Assert.Contains("metadata fingerprint changed", ex.Message);

        // Entire batch must be ZERO WRITE
        var finalCount = await db.MediaIdentities.CountAsync();
        Assert.Equal(initialCount, finalCount);
    }

    [Fact]
    public async Task IdentityImport_ExistingLocalOrManualIdentity_Rejects()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var file1 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Conflict Track 1", Artist = "Artist 1", Album = "Album", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/cf1.mp3" };
        var file2 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Conflict Track 2", Artist = "Artist 2", Album = "Album", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/cf2.mp3" };
        db.MediaFiles.AddRange(file1, file2);
        await db.SaveChangesAsync();

        // Add pre-existing MusicBrainzLocal identity to file1
        db.MediaIdentities.Add(new MediaIdentity
        {
            MediaFileId = file1.Id,
            Provider = "MusicBrainzLocal",
            RecordingId = "rec-existing-1",
            Status = "approved",
            MatchMethod = "PreviousBatch"
        });
        // Add manual identity to file2
        db.MediaIdentities.Add(new MediaIdentity
        {
            MediaFileId = file2.Id,
            Provider = "MusicBrainz",
            RecordingId = "rec-manual-2",
            Status = "manual",
            MatchMethod = "ManualEdit"
        });
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(file1.Id, file1.Title, file1.Artist, file1.Album, 200, "Tier1", 1000, "HighConfidence", 1.0, 10, "new-rec-1", file1.Title, file1.Artist, 200, null, false, null),
            new(file2.Id, file2.Title, file2.Artist, file2.Album, 200, "Tier1", 1000, "HighConfidence", 1.0, 10, "new-rec-2", file2.Title, file2.Artist, 200, null, false, null)
        };
        var (reportId, config) = CreateIntegrationTestReport("conflict_report", reportItems);

        var service = new IdentityImportService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);

        var preview = await service.PreviewBatchAsync(new PreviewIdentityImportBatchRequest(reportId, new List<int> { file1.Id, file2.Id }));
        Assert.Equal(2, preview.TotalCandidates);
        Assert.Equal(0, preview.ValidCount);
        Assert.Equal(2, preview.InvalidCount);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest(reportId, new List<int> { file1.Id, file2.Id }), "admin"));
        Assert.Contains("validation failed", ex.Message);
    }

    [Fact]
    public async Task IdentityImport_Rollback_ExactlyDeletesBatchIdentities()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var file1 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Roll Track 1", Artist = "Artist 1", Album = "Album 1", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/r1.mp3" };
        var file2 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Roll Track 2", Artist = "Artist 2", Album = "Album 2", Duration = TimeSpan.FromSeconds(210), FilePath = "/m/r2.mp3" };
        db.MediaFiles.AddRange(file1, file2);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(file1.Id, file1.Title, file1.Artist, file1.Album, 200, "Tier1", 1000, "HighConfidence", 1.0, 10, "mbid-r1", file1.Title, file1.Artist, 200, null, false, null),
            new(file2.Id, file2.Title, file2.Artist, file2.Album, 210, "Tier1", 1000, "HighConfidence", 0.99, 10, "mbid-r2", file2.Title, file2.Artist, 210, null, false, null)
        };
        var (reportId, config) = CreateIntegrationTestReport("rollback_exact_report", reportItems);

        var service = new IdentityImportService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);
        var batch = await service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest(reportId, new List<int> { file1.Id, file2.Id }), "admin");
        await service.ApproveBatchAsync(batch.Id, "admin");
        await service.ApplyBatchAsync(batch.Id, "admin");

        Assert.Equal(2, await db.MediaIdentities.CountAsync(i => i.MatchMethod == batch.BatchTag));

        var rolledBack = await service.RollbackBatchAsync(batch.Id, "admin");
        Assert.Equal("RolledBack", rolledBack.Status);
        Assert.Equal(0, await db.MediaIdentities.CountAsync(i => i.MatchMethod == batch.BatchTag));
    }

    [Fact]
    public async Task IdentityImport_RollbackRefusedIfIdentityModified_ZeroDeleted()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        var file1 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Mod Track 1", Artist = "Artist 1", Album = "Album 1", Duration = TimeSpan.FromSeconds(200), FilePath = "/m/m1.mp3" };
        var file2 = new MediaFile { ScanSourceId = scanSource.Id, Title = "Mod Track 2", Artist = "Artist 2", Album = "Album 2", Duration = TimeSpan.FromSeconds(210), FilePath = "/m/m2.mp3" };
        db.MediaFiles.AddRange(file1, file2);
        await db.SaveChangesAsync();

        var reportItems = new List<ShadowRunAuditItem>
        {
            new(file1.Id, file1.Title, file1.Artist, file1.Album, 200, "Tier1", 1000, "HighConfidence", 1.0, 10, "mbid-m1", file1.Title, file1.Artist, 200, null, false, null),
            new(file2.Id, file2.Title, file2.Artist, file2.Album, 210, "Tier1", 1000, "HighConfidence", 0.99, 10, "mbid-m2", file2.Title, file2.Artist, 210, null, false, null)
        };
        var (reportId, config) = CreateIntegrationTestReport("rollback_mod_report", reportItems);

        var service = new IdentityImportService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance, null, config);
        var batch = await service.CreateDraftBatchAsync(new CreateIdentityImportBatchRequest(reportId, new List<int> { file1.Id, file2.Id }), "admin");
        await service.ApproveBatchAsync(batch.Id, "admin");
        await service.ApplyBatchAsync(batch.Id, "admin");

        // Simulate human modification of track 1's identity status
        var iden1 = await db.MediaIdentities.FirstAsync(i => i.MediaFileId == file1.Id);
        iden1.Status = "manual";
        await db.SaveChangesAsync();

        var countBefore = await db.MediaIdentities.CountAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackBatchAsync(batch.Id, "admin"));
        Assert.Contains("expected 'approved'", ex.Message);

        var countAfter = await db.MediaIdentities.CountAsync();
        Assert.Equal(countBefore, countAfter); // Zero deleted!
    }

    [Fact]
    public async Task IdentityImport_LegacyPilotRound2_Backfill_DoesNotAddOrModifyMediaIdentities()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        // Clean up legacy batch if exists
        await db.Database.ExecuteSqlRawAsync(@"
            DELETE FROM ""IdentityImportBatches"" WHERE ""BatchTag"" = 'LegacyPilotRound2_20260908';
        ");

        // Seed 10 pilot files and 10 identities with IDs 20..29 in PostgreSQL
        await db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO ""MediaFiles"" (""Id"", ""ScanSourceId"", ""Title"", ""Artist"", ""Album"", ""Genre"", ""Year"", ""SizeBytes"", ""FileHash"", ""ParentPath"", ""FilePath"", ""Duration"", ""AddedAt"")
            OVERRIDING SYSTEM VALUE
            VALUES
                (3, " + scanSource.Id + @", '9 Crimes', 'Damien Rice', '9', 'Pop', 2006, 1000, 'hash3', '/m', '/m/3.mp3', interval '200 seconds', NOW()),
                (5, " + scanSource.Id + @", 'Chasing Pavements', 'Adele', '19', 'Pop', 2008, 1000, 'hash5', '/m', '/m/5.mp3', interval '200 seconds', NOW()),
                (12, " + scanSource.Id + @", 'I Don''t Want to Miss a Thing', 'Aerosmith', 'Armageddon', 'Rock', 1998, 1000, 'hash12', '/m', '/m/12.mp3', interval '200 seconds', NOW()),
                (19, " + scanSource.Id + @", 'Now You''re Gone', 'basshunter', 'Now You''re Gone', 'Dance', 2007, 1000, 'hash19', '/m', '/m/19.mp3', interval '200 seconds', NOW()),
                (36, " + scanSource.Id + @", 'Viva La Vida', 'Coldplay', 'Viva la Vida', 'Rock', 2008, 1000, 'hash36', '/m', '/m/36.mp3', interval '200 seconds', NOW()),
                (53, " + scanSource.Id + @", 'Finally', 'Fergie', 'The Dutchess', 'Pop', 2006, 1000, 'hash53', '/m', '/m/53.mp3', interval '200 seconds', NOW()),
                (55, " + scanSource.Id + @", 'big big world', 'emilia', 'Big Big World', 'Pop', 1998, 1000, 'hash55', '/m', '/m/55.mp3', interval '200 seconds', NOW()),
                (65, " + scanSource.Id + @", '1973', 'James blunt', 'All the Lost Souls', 'Pop', 2007, 1000, 'hash65', '/m', '/m/65.mp3', interval '200 seconds', NOW()),
                (67, " + scanSource.Id + @", 'Beautiful Girl', 'INXS', 'Welcome to Wherever You Are', 'Rock', 1992, 1000, 'hash67', '/m', '/m/67.mp3', interval '200 seconds', NOW()),
                (71, " + scanSource.Id + @", 'You''re Beautiful', 'James Blunt', 'Back to Bedlam', 'Pop', 2004, 1000, 'hash71', '/m', '/m/71.mp3', interval '200 seconds', NOW())
            ON CONFLICT (""Id"") DO NOTHING;

            INSERT INTO ""MediaIdentities"" (""Id"", ""MediaFileId"", ""Provider"", ""RecordingId"", ""MatchMethod"", ""Confidence"", ""Status"", ""CoverStatus"", ""LyricsStatus"", ""MatchedAt"", ""LastVerifiedAt"")
            OVERRIDING SYSTEM VALUE
            VALUES
                (20, 3, 'MusicBrainzLocal', 'd1b9c306-ea8c-4b1e-baca-24d93701e70d', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (21, 5, 'MusicBrainzLocal', '453f8ecf-e853-45ec-8335-d240a15cd75f', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (22, 12, 'MusicBrainzLocal', '2e2e66bd-a016-4713-bd7f-dbb4037cc9b8', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (23, 19, 'MusicBrainzLocal', 'ce7c1d28-b716-4e42-bd30-40612d6241f8', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (24, 36, 'MusicBrainzLocal', '307ce9da-5690-4e21-ab71-9d12ea106e52', 'AuditedPilotRound2_20260908', 0.995, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (25, 53, 'MusicBrainzLocal', 'b6175cb0-6730-4975-b66b-c38ff5d80db2', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (26, 55, 'MusicBrainzLocal', '58558a25-f4a4-4c6f-a6e6-0d04b1a8419d', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (27, 65, 'MusicBrainzLocal', '1ec5f8bb-f073-46f1-95c0-f0dd0a1664b2', 'AuditedPilotRound2_20260908', 0.995, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (28, 67, 'MusicBrainzLocal', '8c8fa617-91ce-4872-a4ec-1d58d628af9a', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (29, 71, 'MusicBrainzLocal', 'b4c986df-547c-441c-b77d-55b88cc200ae', 'AuditedPilotRound2_20260908', 0.995, 'approved', 'Pending', 'Pending', NOW(), NOW())
            ON CONFLICT (""Id"") DO NOTHING;
        ");

        var initialCount = await db.MediaIdentities.CountAsync();
        var initialIdentities = await db.MediaIdentities
            .Where(i => i.Id >= 20 && i.Id <= 29)
            .Select(i => new { i.Id, i.RecordingId, i.Confidence, i.Status })
            .ToListAsync();

        var service = new IdentityImportService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance);
        var batch = await service.BackfillLegacyPilotRound2Async();

        // 1. Assert ZERO new rows in MediaIdentities
        var countAfter = await db.MediaIdentities.CountAsync();
        Assert.Equal(initialCount, countAfter);

        // 2. Assert existing MediaIdentities were NOT modified
        var afterIdentities = await db.MediaIdentities
            .Where(i => i.Id >= 20 && i.Id <= 29)
            .Select(i => new { i.Id, i.RecordingId, i.Confidence, i.Status })
            .ToListAsync();
        Assert.Equal(initialIdentities, afterIdentities);

        // 3. Assert batch and item records created accurately
        Assert.Equal("LegacyPilotRound2_20260908", batch.BatchTag);
        Assert.Equal("LegacyApplied", batch.Status);
        Assert.Equal("admin_pilot", batch.ApprovedBy);
        Assert.NotNull(batch.ApprovedAt);
        Assert.Equal(10, batch.ItemCount);
        Assert.Equal(10, batch.AppliedCount);
        Assert.Equal(10, batch.Items.Count);
        Assert.All(batch.Items, i =>
        {
            Assert.Equal("Applied", i.Status);
            Assert.InRange(i.CreatedIdentityId!.Value, 20, 29);
        });

        // 4. Assert rollback refused
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackBatchAsync(batch.Id, "admin"));
        Assert.Contains("cannot be rolled back", ex.Message);

        // 5. Assert idempotency: calling backfill again returns same batch without duplicate insert
        var secondRun = await service.BackfillLegacyPilotRound2Async();
        Assert.Equal(batch.Id, secondRun.Id);
    }

    [Fact]
    public async Task IdentityImport_LegacyPilotRound2_Backfill_RefusesIfIdentityContentInvalid()
    {
        using var db = _fixture.CreateDbContext();
        await ResetStateAsync(db);
        var scanSource = await GetOrCreateScanSourceAsync(db);

        // Clean up legacy batch if exists
        await db.Database.ExecuteSqlRawAsync(@"
            DELETE FROM ""IdentityImportBatches"" WHERE ""BatchTag"" = 'LegacyPilotRound2_20260908';
        ");

        // Seed 10 pilot files and 10 identities with IDs 20..29, BUT TAMPER ID 20 (tampered recording ID)
        await db.Database.ExecuteSqlRawAsync(@"
            INSERT INTO ""MediaFiles"" (""Id"", ""ScanSourceId"", ""Title"", ""Artist"", ""Album"", ""Genre"", ""Year"", ""SizeBytes"", ""FileHash"", ""ParentPath"", ""FilePath"", ""Duration"", ""AddedAt"")
            OVERRIDING SYSTEM VALUE
            VALUES
                (3, " + scanSource.Id + @", '9 Crimes', 'Damien Rice', '9', 'Pop', 2006, 1000, 'hash3', '/m', '/m/3.mp3', interval '200 seconds', NOW()),
                (5, " + scanSource.Id + @", 'Chasing Pavements', 'Adele', '19', 'Pop', 2008, 1000, 'hash5', '/m', '/m/5.mp3', interval '200 seconds', NOW()),
                (12, " + scanSource.Id + @", 'I Don''t Want to Miss a Thing', 'Aerosmith', 'Armageddon', 'Rock', 1998, 1000, 'hash12', '/m', '/m/12.mp3', interval '200 seconds', NOW()),
                (19, " + scanSource.Id + @", 'Now You''re Gone', 'basshunter', 'Now You''re Gone', 'Dance', 2007, 1000, 'hash19', '/m', '/m/19.mp3', interval '200 seconds', NOW()),
                (36, " + scanSource.Id + @", 'Viva La Vida', 'Coldplay', 'Viva la Vida', 'Rock', 2008, 1000, 'hash36', '/m', '/m/36.mp3', interval '200 seconds', NOW()),
                (53, " + scanSource.Id + @", 'Finally', 'Fergie', 'The Dutchess', 'Pop', 2006, 1000, 'hash53', '/m', '/m/53.mp3', interval '200 seconds', NOW()),
                (55, " + scanSource.Id + @", 'big big world', 'emilia', 'Big Big World', 'Pop', 1998, 1000, 'hash55', '/m', '/m/55.mp3', interval '200 seconds', NOW()),
                (65, " + scanSource.Id + @", '1973', 'James blunt', 'All the Lost Souls', 'Pop', 2007, 1000, 'hash65', '/m', '/m/65.mp3', interval '200 seconds', NOW()),
                (67, " + scanSource.Id + @", 'Beautiful Girl', 'INXS', 'Welcome to Wherever You Are', 'Rock', 1992, 1000, 'hash67', '/m', '/m/67.mp3', interval '200 seconds', NOW()),
                (71, " + scanSource.Id + @", 'You''re Beautiful', 'James Blunt', 'Back to Bedlam', 'Pop', 2004, 1000, 'hash71', '/m', '/m/71.mp3', interval '200 seconds', NOW())
            ON CONFLICT (""Id"") DO NOTHING;

            INSERT INTO ""MediaIdentities"" (""Id"", ""MediaFileId"", ""Provider"", ""RecordingId"", ""MatchMethod"", ""Confidence"", ""Status"", ""CoverStatus"", ""LyricsStatus"", ""MatchedAt"", ""LastVerifiedAt"")
            OVERRIDING SYSTEM VALUE
            VALUES
                (20, 3, 'MusicBrainzLocal', 'tampered-fake-mbid', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (21, 5, 'MusicBrainzLocal', '453f8ecf-e853-45ec-8335-d240a15cd75f', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (22, 12, 'MusicBrainzLocal', '2e2e66bd-a016-4713-bd7f-dbb4037cc9b8', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (23, 19, 'MusicBrainzLocal', 'ce7c1d28-b716-4e42-bd30-40612d6241f8', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (24, 36, 'MusicBrainzLocal', '307ce9da-5690-4e21-ab71-9d12ea106e52', 'AuditedPilotRound2_20260908', 0.995, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (25, 53, 'MusicBrainzLocal', 'b6175cb0-6730-4975-b66b-c38ff5d80db2', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (26, 55, 'MusicBrainzLocal', '58558a25-f4a4-4c6f-a6e6-0d04b1a8419d', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (27, 65, 'MusicBrainzLocal', '1ec5f8bb-f073-46f1-95c0-f0dd0a1664b2', 'AuditedPilotRound2_20260908', 0.995, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (28, 67, 'MusicBrainzLocal', '8c8fa617-91ce-4872-a4ec-1d58d628af9a', 'AuditedPilotRound2_20260908', 1.0, 'approved', 'Pending', 'Pending', NOW(), NOW()),
                (29, 71, 'MusicBrainzLocal', 'b4c986df-547c-441c-b77d-55b88cc200ae', 'AuditedPilotRound2_20260908', 0.995, 'approved', 'Pending', 'Pending', NOW(), NOW())
            ON CONFLICT (""Id"") DO NOTHING;
        ");

        var service = new IdentityImportService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<IdentityImportService>.Instance);

        // Must reject backfill because ID 20 has tampered RecordingId
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BackfillLegacyPilotRound2Async());
        Assert.Contains("RecordingId mismatch", ex.Message);

        // Assert ZERO batches inserted
        var batchCount = await db.IdentityImportBatches.CountAsync(b => b.BatchTag == "LegacyPilotRound2_20260908");
        Assert.Equal(0, batchCount);
    }
}
