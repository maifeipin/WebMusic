using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class MediaTitlePrefixCleanerIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MediaTitlePrefixCleanerIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "webmusic_cleaner_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private AppDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;

        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();

        context.ScanSources.Add(new ScanSource { Id = 1, Name = "Test", Path = "/music", Type = "local" });
        context.SaveChanges();

        return context;
    }

    private (string ReportPath, string Sha256Hex) CreateSyntheticReportFile(
        List<TitlePrefixCandidateItem> admittedItems,
        int evaluated = 10,
        int skipped = 8)
    {
        var report = new TitlePrefixCleanReport(
            Scope: "TestScope",
            TotalEvaluated: evaluated,
            AdmittedCount: admittedItems.Count,
            ManualReviewCandidateCount: 0,
            SkippedOrNormalCount: skipped,
            RuleBreakdown: new Dictionary<string, int>(),
            SkippedBreakdown: new Dictionary<string, int>(),
            AdmittedItems: admittedItems,
            ManualReviewCandidates: new List<TitlePrefixManualCandidateItem>()
        );

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var reportPath = Path.Combine(_tempDir, $"report_{Guid.NewGuid():N}.json");
        File.WriteAllText(reportPath, json);

        var bytes = File.ReadAllBytes(reportPath);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        return (reportPath, sha256Hex);
    }

    [Fact]
    public async Task ApplyAsync_WhenShaMismatch_AbortsImmediatelyWithoutDatabaseRead()
    {
        var dbName = "db_sha_mismatch_" + Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var admitted = new List<TitlePrefixCandidateItem>
        {
            new(1, "/music/01.mp3", "01. Song", "Song", "ExplicitDelimiterIndex", 1.0, "fp1", "fp2")
        };

        var (reportPath, realSha) = CreateSyntheticReportFile(admitted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaTitlePrefixCleaner.ApplyAsync(db, reportPath, "bad_sha_hash", 1));

        Assert.Contains("Report SHA-256 verification failed", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WhenExpectedCountMismatch_AbortsBeforeTransaction()
    {
        var dbName = "db_count_mismatch_" + Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var admitted = new List<TitlePrefixCandidateItem>
        {
            new(1, "/music/01.mp3", "01. Song", "Song", "ExplicitDelimiterIndex", 1.0, "fp1", "fp2")
        };

        var (reportPath, realSha) = CreateSyntheticReportFile(admitted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaTitlePrefixCleaner.ApplyAsync(db, reportPath, realSha, expectedCount: 99));

        Assert.Contains("does not match expected count", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_WhenTitleDriftOccurs_AbortsAndRollsBack()
    {
        var dbName = "db_title_drift_" + Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var media = new MediaFile
        {
            Id = 10,
            ScanSourceId = 1,
            FilePath = "/music/10.mp3",
            Title = "Changed Concurrent Title", // Drifted from report
            Artist = "Artist A",
            Album = "Unknown Album",
            Duration = TimeSpan.FromSeconds(180)
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var admitted = new List<TitlePrefixCandidateItem>
        {
            new(10, "/music/10.mp3", "01. Original Song", "Original Song", "ExplicitDelimiterIndex", 1.0, "old_fp", "new_fp")
        };

        var (reportPath, realSha) = CreateSyntheticReportFile(admitted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaTitlePrefixCleaner.ApplyAsync(db, reportPath, realSha, expectedCount: 1));

        Assert.Contains("Title drift detected", ex.Message);

        // Assert database was not changed
        var freshMedia = await db.MediaFiles.FindAsync(10);
        Assert.NotNull(freshMedia);
        Assert.Equal("Changed Concurrent Title", freshMedia.Title);
    }

    [Fact]
    public async Task ApplyAsync_WhenFingerprintDriftOccurs_AbortsAndRollsBack()
    {
        var dbName = "db_fp_drift_" + Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var media = new MediaFile
        {
            Id = 20,
            ScanSourceId = 1,
            FilePath = "/music/20.mp3",
            Title = "01. Song",
            Artist = "New Artist Altered",
            Album = "Unknown Album",
            Duration = TimeSpan.FromSeconds(180)
        };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();

        var admitted = new List<TitlePrefixCandidateItem>
        {
            new(20, "/music/20.mp3", "01. Song", "Song", "ExplicitDelimiterIndex", 1.0, "stale_expected_fp", "new_fp")
        };

        var (reportPath, realSha) = CreateSyntheticReportFile(admitted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaTitlePrefixCleaner.ApplyAsync(db, reportPath, realSha, expectedCount: 1));

        Assert.Contains("Fingerprint drift detected", ex.Message);

        var freshMedia = await db.MediaFiles.FindAsync(20);
        Assert.NotNull(freshMedia);
        Assert.Equal("01. Song", freshMedia.Title);
    }

    [Fact]
    public async Task ApplyAsync_WhenManualIdentityLockExists_AbortsSafely()
    {
        var dbName = "db_manual_lock_" + Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var media = new MediaFile
        {
            Id = 30,
            ScanSourceId = 1,
            FilePath = "/music/30.mp3",
            Title = "01. Manual Protected",
            Artist = "Artist",
            Album = "Unknown Album",
            Duration = TimeSpan.FromSeconds(180)
        };
        db.MediaFiles.Add(media);

        // Add identity with Status = 'manual'
        db.MediaIdentities.Add(new MediaIdentity
        {
            MediaFileId = 30,
            Status = "manual",
            MatchMethod = "auto_scan"
        });
        await db.SaveChangesAsync();

        var currentFp = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(media);

        var admitted = new List<TitlePrefixCandidateItem>
        {
            new(30, "/music/30.mp3", "01. Manual Protected", "Manual Protected", "ExplicitDelimiterIndex", 1.0, currentFp, "new_fp")
        };

        var (reportPath, realSha) = CreateSyntheticReportFile(admitted);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaTitlePrefixCleaner.ApplyAsync(db, reportPath, realSha, expectedCount: 1));

        Assert.Contains("Manual identity lock exists", ex.Message);

        var freshMedia = await db.MediaFiles.FindAsync(30);
        Assert.NotNull(freshMedia);
        Assert.Equal("01. Manual Protected", freshMedia.Title);
    }

    [Fact]
    public async Task ApplyAsync_HappyPath_UpdatesTitlesAndGeneratesRollbackManifestAfterCommit()
    {
        var dbName = "db_happy_path_" + Guid.NewGuid().ToString("N");
        using var db = CreateInMemoryDbContext(dbName);

        var m1 = new MediaFile
        {
            Id = 101,
            ScanSourceId = 1,
            FilePath = "/music/01.mp3",
            Title = "01. 晴天",
            Artist = "周杰伦",
            Album = "Unknown Album",
            Duration = TimeSpan.FromSeconds(200)
        };
        var m2 = new MediaFile
        {
            Id = 102,
            ScanSourceId = 1,
            FilePath = "/music/02.mp3",
            Title = "Track 02 - 七里香",
            Artist = "周杰伦",
            Album = "Unknown Album",
            Duration = TimeSpan.FromSeconds(240)
        };
        db.MediaFiles.AddRange(m1, m2);
        await db.SaveChangesAsync();

        var fp1 = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(m1);
        var fp2 = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(m2);

        var admitted = new List<TitlePrefixCandidateItem>
        {
            new(101, m1.FilePath, "01. 晴天", "晴天", "ExplicitDelimiterIndex", 1.0, fp1, "new_fp1"),
            new(102, m2.FilePath, "Track 02 - 七里香", "七里香", "ExplicitDelimiterIndex", 1.0, fp2, "new_fp2")
        };

        var (reportPath, realSha) = CreateSyntheticReportFile(admitted);
        var customManifestPath = Path.Combine(_tempDir, "rollback_custom.json");

        var result = await MediaTitlePrefixCleaner.ApplyAsync(
            db,
            reportPath,
            realSha,
            expectedCount: 2,
            rollbackManifestPath: customManifestPath);

        Assert.Equal(2, result.UpdatedCount);
        Assert.Equal(realSha, result.ReportSha256);
        Assert.Equal(customManifestPath, result.RollbackManifestPath);

        // Verify database state: Titles updated, Artist and Album untouched
        var fresh1 = await db.MediaFiles.FindAsync(101);
        var fresh2 = await db.MediaFiles.FindAsync(102);

        Assert.NotNull(fresh1);
        Assert.Equal("晴天", fresh1.Title);
        Assert.Equal("周杰伦", fresh1.Artist);
        Assert.Equal("Unknown Album", fresh1.Album);

        Assert.NotNull(fresh2);
        Assert.Equal("七里香", fresh2.Title);
        Assert.Equal("周杰伦", fresh2.Artist);
        Assert.Equal("Unknown Album", fresh2.Album);

        // Verify rollback manifest exists on disk and is valid
        Assert.True(File.Exists(customManifestPath));
        var manifestContent = await File.ReadAllTextAsync(customManifestPath);
        Assert.Contains("TotalApplied", manifestContent);
        Assert.Contains("晴天", manifestContent);
        Assert.Contains("七里香", manifestContent);
    }
}
