using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class MediaDuplicateCleanerTests : IDisposable
{
    private readonly string _tempDir;

    public MediaDuplicateCleanerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "webmusic_dedupe_tests_" + Guid.NewGuid().ToString("N"));
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
        context.Users.Add(new User { Id = 1, Username = "u" + Guid.NewGuid().ToString("N")[..12], PasswordHash = "x", IsAdmin = false });
        context.SaveChanges();
        return context;
    }

    private MediaFile NewMedia(int id, string title, string artist, string album, int seconds, long size, string hash, string path = "")
        => new()
        {
            Id = id,
            ScanSourceId = 1,
            FilePath = path.Length > 0 ? path : $"/music/{id}.mp3",
            Title = title,
            Artist = artist,
            Album = album,
            Duration = TimeSpan.FromSeconds(seconds),
            SizeBytes = size,
            FileHash = hash,
            Genre = "Pop",
            Year = 2020
        };

    [Fact]
    public async Task ByteHash_IdenticalHash_ClustersAndPicksHighestScore()
    {
        var db = CreateInMemoryDbContext("dedupe_byte_" + Guid.NewGuid().ToString("N"));
        var low = NewMedia(1, "Song", "A", "Album", 200, 3_200_000, "hash1"); // 128kbps
        var high = NewMedia(2, "Song", "A", "Album", 200, 8_000_000, "hash1"); // 320kbps
        db.MediaFiles.AddRange(low, high);
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "byte-hash");

        Assert.Equal(1, report.TotalGroups);
        Assert.Equal(1, report.RemoveCount);
        Assert.Equal(2, report.KeptTotalMembers());
        var group = report.Groups[0];
        Assert.Equal(high.Id, group.KeptId); // keep higher bitrate copy
        Assert.Contains(group.Members, m => m.MediaFileId == low.Id && m.Reason.StartsWith("REMOVE"));
    }

    [Fact]
    public async Task ByteHash_ReferencedMember_SurvivesAndUnreferencedCopyRemoved()
    {
        var db = CreateInMemoryDbContext("dedupe_prot_" + Guid.NewGuid().ToString("N"));
        var loser = NewMedia(1, "Song", "A", "Album", 200, 3_200_000, "hash1");
        var winner = NewMedia(2, "Song", "A", "Album", 200, 8_000_000, "hash1");
        db.MediaFiles.AddRange(loser, winner);
        db.Favorites.Add(new Favorite { UserId = 1, MediaFileId = loser.Id });
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "byte-hash");

        // The favorited copy survives (protection), the unreferenced byte-identical copy is removed.
        Assert.Equal(1, report.TotalGroups);
        Assert.Equal(1, report.RemoveCount);
        var group = report.Groups[0];
        Assert.Equal(loser.Id, group.KeptId);
        Assert.Contains(group.Members, m => m.MediaFileId == winner.Id && m.Reason.StartsWith("REMOVE"));
    }

    [Fact]
    public async Task ExactMetadata_SameTitleArtistAlbum_DurationClusterWithin3s()
    {
        var db = CreateInMemoryDbContext("dedupe_exact_" + Guid.NewGuid().ToString("N"));
        db.MediaFiles.AddRange(
            NewMedia(1, "晴天", "周杰伦", "叶惠美", 200, 6_400_000, "h1"),
            NewMedia(2, "晴天", "周杰伦", "叶惠美", 202, 3_200_000, "h2"), // within 3s -> same cluster
            NewMedia(3, "晴天", "周杰伦", "叶惠美", 250, 6_400_000, "h3")  // 50s away -> separate cluster
        );
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "exact-metadata");

        Assert.Equal(1, report.TotalGroups);
        Assert.Equal(1, report.RemoveCount); // only 1 loser from the {1,2} cluster
        var group = report.Groups[0];
        Assert.Equal(2, group.GroupSize);
        Assert.DoesNotContain(group.Members, m => m.MediaFileId == 3);
    }

    [Fact]
    public async Task MbId_SameRecording_ConservativeAlbumConstraint()
    {
        var db = CreateInMemoryDbContext("dedupe_mbid_" + Guid.NewGuid().ToString("N"));
        var f1 = NewMedia(1, "Song", "A", "Album X", 200, 6_400_000, "h1");
        var f2 = NewMedia(2, "Song", "A", "Unknown Album", 200, 3_200_000, "h2");
        var f3 = NewMedia(3, "Song", "A", "Album Y", 200, 6_400_000, "h3"); // different named album
        db.MediaFiles.AddRange(f1, f2, f3);
        foreach (var f in new[] { f1, f2, f3 })
        {
            db.MediaIdentities.Add(new MediaIdentity
            {
                MediaFileId = f.Id,
                Provider = "MusicBrainzLocal",
                RecordingId = "same-recording",
                MatchMethod = "test",
                Status = "approved"
            });
        }
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "mbid");

        // Only {f1, f2} cluster (one named album + one unknown). f3 (Album Y) excluded.
        Assert.Equal(1, report.TotalGroups);
        Assert.Equal(2, report.Groups[0].GroupSize);
        Assert.Equal(1, report.RemoveCount);
    }

    [Fact]
    public async Task Fuzzy_NormalizedTitleArtist_BucketsBy5s()
    {
        var db = CreateInMemoryDbContext("dedupe_fuzzy_" + Guid.NewGuid().ToString("N"));
        db.MediaFiles.AddRange(
            NewMedia(1, "Sunny Day!", "THE artist", "Album1", 200, 6_400_000, "h1"),
            NewMedia(2, "sunny day", "The Artist", "Album2", 201, 3_200_000, "h2") // normalized identical, dur within 5s
        );
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "fuzzy");

        Assert.Equal(1, report.TotalGroups);
        Assert.Equal(1, report.RemoveCount);
    }

    [Fact]
    public async Task Apply_RemovesOnlyLoserRowsAndDependants_AndWritesManifest()
    {
        var db = CreateInMemoryDbContext("dedupe_apply_" + Guid.NewGuid().ToString("N"));
        var loser = NewMedia(1, "Song", "A", "Album", 200, 3_200_000, "hash1");
        var winner = NewMedia(2, "Song", "A", "Album", 200, 8_000_000, "hash1");
        db.MediaFiles.AddRange(loser, winner);
        db.MediaIdentities.Add(new MediaIdentity
        {
            MediaFileId = loser.Id,
            Provider = "MusicBrainzLocal",
            RecordingId = "rec1",
            MatchMethod = "test",
            Status = "approved"
        });
        db.MediaIdentityScanStates.Add(new MediaIdentityScanState { MediaFileId = loser.Id, Outcome = "Matched" });
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "byte-hash");
        Assert.Equal(1, report.RemoveCount);

        var reportPath = Path.Combine(_tempDir, "dedupe_report.json");
        var sha = await MediaDuplicateCleaner.SaveReportWithSha256Async(report, reportPath);
        var manifestPath = Path.Combine(_tempDir, "rollback.json");

        var result = await MediaDuplicateCleaner.ApplyAsync(db, reportPath, sha, 1, manifestPath);

        Assert.Equal(1, result.RemovedCount);
        Assert.True(File.Exists(manifestPath));
        Assert.Null(await db.MediaFiles.FindAsync(loser.Id));
        Assert.NotNull(await db.MediaFiles.FindAsync(winner.Id));
        Assert.Equal(0, await db.MediaIdentities.CountAsync(i => i.MediaFileId == loser.Id));
        Assert.Equal(0, await db.MediaIdentityScanStates.CountAsync(s => s.MediaFileId == loser.Id));
        var manifest = await File.ReadAllTextAsync(manifestPath);
        Assert.Contains("\"KeptId\": 2", manifest);
        Assert.Contains("/music/1.mp3", manifest);
    }

    [Fact]
    public async Task Apply_ShaMismatch_Aborts()
    {
        var db = CreateInMemoryDbContext("dedupe_sha_" + Guid.NewGuid().ToString("N"));
        db.MediaFiles.AddRange(
            NewMedia(1, "Song", "A", "Album", 200, 3_200_000, "hash1"),
            NewMedia(2, "Song", "A", "Album", 200, 8_000_000, "hash1"));
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "byte-hash");
        var reportPath = Path.Combine(_tempDir, "dedupe_report.json");
        await MediaDuplicateCleaner.SaveReportWithSha256Async(report, reportPath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaDuplicateCleaner.ApplyAsync(db, reportPath, "bad_sha", 1, Path.Combine(_tempDir, "rb.json")));
        Assert.Contains("SHA-256 verification failed", ex.Message);
    }

    [Fact]
    public async Task Apply_GainedReferenceAfterDryRun_Aborts()
    {
        var db = CreateInMemoryDbContext("dedupe_ref_" + Guid.NewGuid().ToString("N"));
        db.MediaFiles.AddRange(
            NewMedia(1, "Song", "A", "Album", 200, 3_200_000, "hash1"),
            NewMedia(2, "Song", "A", "Album", 200, 8_000_000, "hash1"));
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "byte-hash");
        Assert.Equal(1, report.RemoveCount);
        var reportPath = Path.Combine(_tempDir, "dedupe_report.json");
        var sha = await MediaDuplicateCleaner.SaveReportWithSha256Async(report, reportPath);

        // Simulate a favorite added after the dry run on the removal candidate.
        db.Favorites.Add(new Favorite { UserId = 1, MediaFileId = 1 });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaDuplicateCleaner.ApplyAsync(db, reportPath, sha, 1, Path.Combine(_tempDir, "rb.json")));
        Assert.Contains("gained references", ex.Message);

        // Nothing was removed.
        Assert.NotNull(await db.MediaFiles.FindAsync(1));
    }

    [Fact]
    public async Task Apply_FuzzyStage_IsRejected()
    {
        var db = CreateInMemoryDbContext("dedupe_fzapply_" + Guid.NewGuid().ToString("N"));
        db.MediaFiles.AddRange(
            NewMedia(1, "Sunny Day!", "THE artist", "Album1", 200, 6_400_000, "h1"),
            NewMedia(2, "sunny day", "The Artist", "Album2", 201, 3_200_000, "h2"));
        await db.SaveChangesAsync();

        var report = await MediaDuplicateCleaner.RunDryRunAsync(db, "fuzzy");
        var reportPath = Path.Combine(_tempDir, "dedupe_fuzzy.json");
        var sha = await MediaDuplicateCleaner.SaveReportWithSha256Async(report, reportPath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MediaDuplicateCleaner.ApplyAsync(db, reportPath, sha, report.RemoveCount, Path.Combine(_tempDir, "rb.json")));
        Assert.Contains("report-only", ex.Message);
    }
}

internal static class DedupeReportTestExtensions
{
    public static int KeptTotalMembers(this DedupeReport report) => report.Groups.Sum(g => g.Members.Count);
}
