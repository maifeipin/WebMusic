using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using WebMusic.Backend.Controllers;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class WorkerLeaseAndSubmitTests
{
    private AppDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private WorkerEnrichmentController CreateController(AppDbContext db, IWebHostEnvironment? env = null, string workerNodeId = "worker-1")
    {
        var mockEnv = new Mock<IWebHostEnvironment>();
        mockEnv.Setup(e => e.ContentRootPath).Returns(System.IO.Path.GetTempPath());
        var controller = new WorkerEnrichmentController(db, env ?? mockEnv.Object);
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "Worker"),
            new System.Security.Claims.Claim("worker_node", workerNodeId),
            new System.Security.Claims.Claim("sub", "1")
        }, "TestAuth"));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user }
        };
        return controller;
    }

    [Fact]
    public async Task LeaseBatch_EnforcesQuotaReservation_AndReclaimsExpired()
    {
        var dbName = Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        // Seed source
        var source = new ScanSource { Id = 1, Name = "S", Path = "/m", Type = "local" };
        db.ScanSources.Add(source);

        // Seed 10 media files
        for (int i = 1; i <= 10; i++)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = i,
                ScanSourceId = 1,
                Title = $"Song {i}",
                Artist = $"Artist {i}",
                FilePath = $"/music/{i}.mp3"
            });
        }

        // Seed today's ProviderQuotaLedger with 1990 reserved units (leaving 10 units = 5 tracks @ 2 units/track)
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
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

        var controller = CreateController(db);

        // Request 10 tracks when only 10 units / 2 = 5 tracks remain
        var leaseResult = await controller.LeaseBatch(new WorkerLeaseRequest
        {
            WorkerNodeId = "worker-1",
            BatchSize = 10
        });

        var okResult = Assert.IsType<OkObjectResult>(leaseResult);
        var val = Assert.IsType<WorkerLeaseBatchResponse>(okResult.Value);
        Assert.Equal(5, val.Total); // Must be clamped to 5 tracks!

        // Next lease request should hit 429 quota reached
        var controller2 = CreateController(db, workerNodeId: "worker-2");
        var quotaExceededResult = await controller2.LeaseBatch(new WorkerLeaseRequest
        {
            WorkerNodeId = "worker-2",
            BatchSize = 10
        });

        var statusResult = Assert.IsType<ObjectResult>(quotaExceededResult);
        Assert.Equal(429, statusResult.StatusCode);
    }

    [Fact]
    public async Task SubmitBatch_ValidatesOwnershipAndLeaseExpiration()
    {
        var dbName = Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var source = new ScanSource { Id = 1, Name = "S", Path = "/m", Type = "local" };
        db.ScanSources.Add(source);
        db.MediaFiles.Add(new MediaFile { Id = 101, ScanSourceId = 1, Title = "Track", Artist = "Band", FilePath = "/m/t.mp3" });

        var job = new EnrichmentJob
        {
            Id = "batch-1",
            Scope = "Catalog:WorkerLease:worker-1",
            Total = 1,
            Status = "Processing",
            StartedAt = DateTime.UtcNow
        };
        db.EnrichmentJobs.Add(job);

        // Expired lease
        var expiredItem = new EnrichmentJobItem
        {
            Id = 1,
            JobId = "batch-1",
            MediaFileId = 101,
            WorkerNodeId = "worker-1",
            Status = "Leased",
            LeasedAt = DateTime.UtcNow.AddMinutes(-30),
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(-15) // Expired!
        };
        db.EnrichmentJobItems.Add(expiredItem);
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        // Submit from expired lease
        var submitResult = await controller.SubmitBatch(new WorkerSubmitBatchRequest
        {
            BatchId = "batch-1",
            WorkerNodeId = "worker-1",
            Results = new List<WorkerItemSubmission>
            {
                new WorkerItemSubmission
                {
                    ItemId = 1,
                    MediaFileId = 101,
                    Outcome = "MatchedWithoutAssets",
                    Confidence = 0.95
                }
            }
        });

        var okSubmit = Assert.IsType<OkObjectResult>(submitResult);
        var submitResp = Assert.IsType<WorkerSubmitBatchResponse>(okSubmit.Value);

        // Expired item should be ignored and not counted as processed
        Assert.Equal(0, submitResp.Processed);
        Assert.Equal(1, submitResp.IgnoredOrExpired);

        // Verify that database attempt was not written
        var attempts = await db.EnrichmentAttempts.Where(a => a.JobId == "batch-1").ToListAsync();
        Assert.Empty(attempts);
    }

    [Fact]
    public async Task SubmitBatch_EnforcesIdempotency_AndRejectsDuplicate()
    {
        var dbName = Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var source = new ScanSource { Id = 1, Name = "S", Path = "/m", Type = "local" };
        db.ScanSources.Add(source);
        db.MediaFiles.Add(new MediaFile { Id = 201, ScanSourceId = 1, Title = "Song", Artist = "Artist", FilePath = "/m/s.mp3" });

        var job = new EnrichmentJob
        {
            Id = "batch-2",
            Scope = "Catalog:WorkerLease:worker-1",
            Total = 1,
            Status = "Processing",
            StartedAt = DateTime.UtcNow
        };
        db.EnrichmentJobs.Add(job);

        var activeItem = new EnrichmentJobItem
        {
            Id = 2,
            JobId = "batch-2",
            MediaFileId = 201,
            WorkerNodeId = "worker-1",
            Status = "Leased",
            LeasedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
        db.EnrichmentJobItems.Add(activeItem);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var submissionId = Guid.NewGuid().ToString();

        var request = new WorkerSubmitBatchRequest
        {
            BatchId = "batch-2",
            WorkerNodeId = "worker-1",
            SubmissionId = submissionId,
            Results = new List<WorkerItemSubmission>
            {
                new WorkerItemSubmission
                {
                    ItemId = 2,
                    MediaFileId = 201,
                    Outcome = "MatchedWithoutAssets",
                    RecordingId = "rec-1",
                    Confidence = 0.99
                }
            }
        };

        // First submit should succeed
        var res1 = await controller.SubmitBatch(request);
        var submit1 = Assert.IsType<WorkerSubmitBatchResponse>(Assert.IsType<OkObjectResult>(res1).Value);
        Assert.Equal(1, submit1.Processed);
        Assert.Equal(0, submit1.IgnoredOrExpired);

        // Immediate duplicate submit with same submission ID should be ignored
        var res2 = await controller.SubmitBatch(request);
        var submit2 = Assert.IsType<WorkerSubmitBatchResponse>(Assert.IsType<OkObjectResult>(res2).Value);
        Assert.Equal(0, submit2.Processed);
        Assert.Equal(1, submit2.IgnoredOrExpired);

        // Counters must remain strictly 1
        var refreshedJob = await db.EnrichmentJobs.FindAsync("batch-2");
        Assert.Equal(1, refreshedJob!.Processed);
    }
}
