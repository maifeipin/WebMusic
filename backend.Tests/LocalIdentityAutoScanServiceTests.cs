using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class LocalIdentityAutoScanServiceTests
{
    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        var db = new AppDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        db.ScanSources.Add(new ScanSource { Id = 1, Name = "Test", Path = "smb://test" });
        db.SaveChanges();
        return db;
    }

    private static ILocalMusicBrainzService LocalService(Func<MediaFile, LocalMusicBrainzScanResult> factory)
    {
        var mock = new Mock<ILocalMusicBrainzService>();
        mock.SetupGet(service => service.BaseUrl).Returns("http://192.168.2.18:5050");
        mock.Setup(service => service.ScanMediaIdentityAsync(It.IsAny<MediaFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaFile media, CancellationToken _) => factory(media));
        return mock.Object;
    }

    private static LocalMusicBrainzScanResult Exact(MediaFile media, double confidence = 1) => new(
        true,
        new LocalMusicBrainzCandidate("a0c34a5c-523a-4d58-b5ec-8d6ed95596cc", null, null, media.Title, media.Artist, media.Duration, null, confidence, false),
        200,
        null,
        1);

    [Fact]
    public async Task DryRun_UsesNoPersonalPriority_WritesNoScanStateOrIdentity()
    {
        await using var db = CreateDb();
        db.MediaFiles.AddRange(
            new MediaFile { Id = 1, ScanSourceId = 1, FilePath = "smb://test/1.mp3", Title = "No identity", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 2, ScanSourceId = 1, FilePath = "smb://test/2.mp3", Title = "Already identity", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 3, ScanSourceId = 1, FilePath = "smb://test/3.mp3", Title = "Live Track", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) });
        db.MediaIdentities.Add(new MediaIdentity { MediaFileId = 2, Provider = "MusicBrainz", RecordingId = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();

        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance);
        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(MaxItems: 10, DryRun: true));

        Assert.Equal(new[] { 1, 3 }, report.Items.Select(item => item.MediaFileId));
        Assert.Equal(new[] { "Matched", "Skipped" }, report.Items.Select(item => item.Outcome));
        Assert.Equal(0, await db.MediaIdentityScanStates.CountAsync());
        Assert.Equal(1, await db.MediaIdentities.CountAsync());
        Assert.Equal(1, report.Matched);
    }

    [Fact]
    public async Task Scan_PersistState_RetriesOnlyWhenMetadataFingerprintChanges()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) };
        db.MediaFiles.Add(media);
        await db.SaveChangesAsync();
        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance);

        var first = await service.ScanAsync(new LocalIdentityAutoScanRequest(LocalIdentityScanMode.Incremental, 1, DryRun: false, PersistState: true, MirrorVersion: "mirror:v1"));
        Assert.Equal(1, first.Matched);
        var state = await db.MediaIdentityScanStates.SingleAsync();
        Assert.Equal(1, state.AttemptCount);

        var second = await service.ScanAsync(new LocalIdentityAutoScanRequest(LocalIdentityScanMode.Incremental, 1, DryRun: false, PersistState: true, MirrorVersion: "mirror:v1"));
        Assert.Equal(0, second.Evaluated);

        media.Title = "Track Renamed";
        await db.SaveChangesAsync();
        var third = await service.ScanAsync(new LocalIdentityAutoScanRequest(LocalIdentityScanMode.Incremental, 1, DryRun: false, PersistState: true, MirrorVersion: "mirror:v1"));
        Assert.Equal(1, third.Evaluated);
        Assert.Equal(2, (await db.MediaIdentityScanStates.SingleAsync()).AttemptCount);
    }

    [Fact]
    public async Task Scan_PersistState_RequiresNonEmptyMirrorVersion()
    {
        await using var db = CreateDb();
        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScanAsync(new LocalIdentityAutoScanRequest(DryRun: false, PersistState: true, MirrorVersion: null)));
    }

    [Fact]
    public async Task Scan_AllowsDuplicateMbidAcrossMultipleFiles()
    {
        await using var db = CreateDb();
        var duplicateMbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        db.MediaFiles.AddRange(
            new MediaFile { Id = 1, ScanSourceId = 1, FilePath = "smb://test/1.mp3", Title = "Hello", Artist = "Adele", Album = "25", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 2, ScanSourceId = 1, FilePath = "smb://test/2.mp3", Title = "Hello", Artist = "Adele", Album = "25 Deluxe", Duration = TimeSpan.FromMinutes(3) });
        await db.SaveChangesAsync();

        var service = new LocalIdentityAutoScanService(db, LocalService(media => new LocalMusicBrainzScanResult(true,
            new LocalMusicBrainzCandidate(duplicateMbid, null, null, media.Title, media.Artist, media.Duration, null, 1, false), 200)), NullLogger<LocalIdentityAutoScanService>.Instance);

        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(MaxItems: 10));
        Assert.Equal(2, report.Matched);
        Assert.All(report.Items, item => Assert.Equal("Matched", item.Outcome));
    }

    [Fact]
    public async Task Scan_RejectsLowConfidence_AndDerivativeCandidate()
    {
        await using var db = CreateDb();
        db.MediaFiles.AddRange(
            new MediaFile { Id = 1, ScanSourceId = 1, FilePath = "smb://test/1.mp3", Title = "One", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 2, ScanSourceId = 1, FilePath = "smb://test/2.mp3", Title = "Two", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 3, ScanSourceId = 1, FilePath = "smb://test/3.mp3", Title = "Three", Artist = "Artist", Album = "A", Duration = TimeSpan.FromMinutes(3) });
        await db.SaveChangesAsync();
        var service = new LocalIdentityAutoScanService(db, LocalService(media => new LocalMusicBrainzScanResult(true,
            new LocalMusicBrainzCandidate(
                media.Id == 1 ? "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc" : media.Id == 2 ? "c0c34a5c-523a-4d58-b5ec-8d6ed95596cc" : "d0c34a5c-523a-4d58-b5ec-8d6ed95596cc",
                null, null, media.Title, media.Artist, media.Duration,
                media.Id == 3 ? "radio mix" : null,
                media.Id == 2 ? 0.99 : 1,
                false), 200)), NullLogger<LocalIdentityAutoScanService>.Instance);

        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(MaxItems: 3));
        Assert.Equal(new[] { "Matched", "Unmatched", "Skipped" }, report.Items.Select(item => item.Outcome));
        Assert.Equal(0, await db.MediaIdentities.CountAsync());
    }

    [Fact]
    public async Task DryRun_RejectsPersistState()
    {
        await using var db = CreateDb();
        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ScanAsync(new LocalIdentityAutoScanRequest(DryRun: true, PersistState: true)));
    }

    [Fact]
    public async Task DryRun_IncludesLocalMetadataAndCommunitySnapshotInReportHash()
    {
        await using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile { Id = 1, ScanSourceId = 1, FilePath = "smb://test/track.mp3", Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(200) });
        await db.SaveChangesAsync();
        var details = new Mock<ILocalMusicBrainzDetailService>();
        details.Setup(service => service.GetRecordingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalMusicBrainzRecordingDetails(
                "a0c34a5c-523a-4d58-b5ec-8d6ed95596cc", "Track", new[] { "USABC1234567" }, 4.5, 10,
                "http://192.168.2.18:5050/recording/a", "payload", ReleaseIds: new[] { "release" }, ReleaseGroupIds: new[] { "group" }));
        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance, details.Object);

        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(MaxItems: 1));
        var item = Assert.Single(report.Items);
        Assert.Equal(new[] { "USABC1234567" }, item.Isrcs);
        Assert.Equal(4.5 * Math.Log(11), item.MbCommunityScore!.Value, 8);
        Assert.Equal(64, report.ResultSha256.Length);
        Assert.Equal(0, await db.MediaIdentityScanStates.CountAsync());
    }

    [Fact]
    public async Task ApplyMatchedIdentities_AtomicallyPersistsIdentityMetadataScoreAndState()
    {
        await using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile
        {
            Id = 1,
            ScanSourceId = 1,
            FilePath = "smb://test/apply.mp3",
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            Duration = TimeSpan.FromSeconds(200)
        });
        await db.SaveChangesAsync();

        var details = new Mock<ILocalMusicBrainzDetailService>();
        details.Setup(service => service.GetRecordingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalMusicBrainzRecordingDetails(
                "a0c34a5c-523a-4d58-b5ec-8d6ed95596cc",
                "Track",
                new[] { "USABC1234567" },
                4.5,
                10,
                "http://192.168.2.18:5050/recording/a",
                "payload-hash",
                "Artist",
                200,
                new[] { "release-id" },
                new[] { "release-group-id" },
                new[] { "tag" },
                new[] { "genre" }));
        var signals = new MusicBrainzCommunitySignalService(db);
        var service = new LocalIdentityAutoScanService(
            db,
            LocalService(media => Exact(media)),
            NullLogger<LocalIdentityAutoScanService>.Instance,
            details.Object,
            signals);

        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(
            MaxItems: 1,
            DryRun: false,
            PersistState: true,
            MirrorVersion: "mirror:v1",
            ApplyMatchedIdentities: true));

        Assert.Equal(1, report.IdentitiesCreated);
        Assert.Equal(1, report.SignalsUpdated);
        var identity = await db.MediaIdentities.SingleAsync();
        Assert.Equal("MusicBrainzLocal", identity.Provider);
        Assert.Equal(LocalIdentityAutoEligibilityPolicy.Version, identity.MatchMethod);
        Assert.Equal("USABC1234567", identity.ISRC);
        Assert.Equal("approved", identity.Status);
        Assert.Equal("Matched", (await db.MediaIdentityScanStates.SingleAsync()).Outcome);
        var reference = await db.MediaExternalReferences.Include(value => value.Signals).SingleAsync();
        Assert.Equal("a0c34a5c-523a-4d58-b5ec-8d6ed95596cc", reference.ExternalId);
        Assert.Contains("release-group-id", reference.MetadataJson);
        Assert.Equal(2, reference.Signals.Count);
        Assert.Equal(4.5 * Math.Log(11), reference.Signals.Single(value => value.SignalKey == "CommunityScore").RawValue!.Value, 8);
    }

    [Fact]
    public async Task ApplyMatchedIdentities_LowConfidencePersistsOnlyScanState()
    {
        await using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile
        {
            Id = 1,
            ScanSourceId = 1,
            FilePath = "smb://test/low.mp3",
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            Duration = TimeSpan.FromSeconds(200)
        });
        await db.SaveChangesAsync();
        var service = new LocalIdentityAutoScanService(
            db,
            LocalService(media => Exact(media, 0.9)),
            NullLogger<LocalIdentityAutoScanService>.Instance,
            Mock.Of<ILocalMusicBrainzDetailService>(),
            Mock.Of<IMusicBrainzCommunitySignalService>());

        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(
            MaxItems: 1,
            DryRun: false,
            PersistState: true,
            MirrorVersion: "mirror:v1",
            ApplyMatchedIdentities: true));

        Assert.Equal(0, report.IdentitiesCreated);
        Assert.Equal(0, await db.MediaIdentities.CountAsync());
        Assert.Equal("Unmatched", (await db.MediaIdentityScanStates.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task ApplyMatchedIdentities_WhenSignalPersistenceFails_RollsBackEntireBatch()
    {
        await using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile
        {
            Id = 1,
            ScanSourceId = 1,
            FilePath = "smb://test/rollback.mp3",
            Title = "Track",
            Artist = "Artist",
            Album = "Album",
            Duration = TimeSpan.FromSeconds(200)
        });
        await db.SaveChangesAsync();
        var details = new Mock<ILocalMusicBrainzDetailService>();
        details.Setup(service => service.GetRecordingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalMusicBrainzRecordingDetails(
                "a0c34a5c-523a-4d58-b5ec-8d6ed95596cc", "Track", Array.Empty<string>(), null, null,
                "http://192.168.2.18:5050/recording/a", "payload", "Artist", 200));
        var failingSignals = new Mock<IMusicBrainzCommunitySignalService>();
        failingSignals.Setup(service => service.UpsertAsync(It.IsAny<MediaIdentity>(), It.IsAny<LocalMusicBrainzRecordingDetails>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated signal failure"));
        var service = new LocalIdentityAutoScanService(
            db,
            LocalService(media => Exact(media)),
            NullLogger<LocalIdentityAutoScanService>.Instance,
            details.Object,
            failingSignals.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ScanAsync(new LocalIdentityAutoScanRequest(
            MaxItems: 1,
            DryRun: false,
            PersistState: true,
            MirrorVersion: "mirror:v1",
            ApplyMatchedIdentities: true)));

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.MediaIdentities.CountAsync());
        Assert.Equal(0, await db.MediaIdentityScanStates.CountAsync());
        Assert.Equal(0, await db.MediaExternalReferences.CountAsync());
    }

    [Fact]
    public async Task IncrementalScan_ChunksPastPreviouslyScannedItems_AndAdvancesCursor()
    {
        await using var db = CreateDb();
        var files = new List<MediaFile>();
        var states = new List<MediaIdentityScanState>();

        // Create 850 media files. First 820 are already scanned and up to date.
        for (int i = 1; i <= 850; i++)
        {
            var media = new MediaFile
            {
                Id = i,
                ScanSourceId = 1,
                FilePath = $"smb://test/{i}.mp3",
                Title = $"Track {i}",
                Artist = "Artist",
                Album = "Album",
                Duration = TimeSpan.FromMinutes(3)
            };
            files.Add(media);

            if (i <= 820)
            {
                states.Add(new MediaIdentityScanState
                {
                    MediaFileId = i,
                    InputFingerprint = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(media),
                    PolicyVersion = LocalIdentityAutoEligibilityPolicy.Version,
                    Outcome = "Matched",
                    LastScannedAt = DateTime.UtcNow,
                    RetryAfter = null
                });
            }
        }

        db.MediaFiles.AddRange(files);
        db.MediaIdentityScanStates.AddRange(states);
        await db.SaveChangesAsync();

        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance);

        // Run scan with MaxItems = 10 from the beginning
        var report = await service.ScanAsync(new LocalIdentityAutoScanRequest(
            Mode: LocalIdentityScanMode.Incremental,
            MaxItems: 10,
            DryRun: true));

        // It must NOT stall on the first 800 items; it must find 10 new items from 821 to 830!
        Assert.Equal(10, report.Evaluated);
        Assert.Equal(821, report.Items.First().MediaFileId);
        Assert.Equal(830, report.Items.Last().MediaFileId);
        Assert.Equal(830, report.ContinuationAfterMediaFileId);
    }

    [Fact]
    public async Task FullScan_ConsecutiveBatches_NeverSkipsIntermediateMediaFiles()
    {
        await using var db = CreateDb();
        var files = new List<MediaFile>();

        // Create 250 media files (IDs 1-250)
        for (int i = 1; i <= 250; i++)
        {
            files.Add(new MediaFile
            {
                Id = i,
                ScanSourceId = 1,
                FilePath = $"smb://test/{i}.mp3",
                Title = $"Song {i}",
                Artist = "Artist",
                Album = "Album",
                Duration = TimeSpan.FromMinutes(3)
            });
        }
        db.MediaFiles.AddRange(files);
        await db.SaveChangesAsync();

        var service = new LocalIdentityAutoScanService(db, LocalService(media => Exact(media)), NullLogger<LocalIdentityAutoScanService>.Instance);

        // Batch 1: MaxItems = 100, AfterMediaFileId = null
        var batch1 = await service.ScanAsync(new LocalIdentityAutoScanRequest(Mode: LocalIdentityScanMode.Full, MaxItems: 100, DryRun: true));
        Assert.Equal(100, batch1.Evaluated);
        Assert.Equal(1, batch1.Items.First().MediaFileId);
        Assert.Equal(100, batch1.Items.Last().MediaFileId);
        Assert.Equal(100, batch1.ContinuationAfterMediaFileId);

        // Batch 2: MaxItems = 100, AfterMediaFileId = batch1.ContinuationAfterMediaFileId (100)
        var batch2 = await service.ScanAsync(new LocalIdentityAutoScanRequest(Mode: LocalIdentityScanMode.Full, MaxItems: 100, AfterMediaFileId: batch1.ContinuationAfterMediaFileId, DryRun: true));
        Assert.Equal(100, batch2.Evaluated);
        Assert.Equal(101, batch2.Items.First().MediaFileId);
        Assert.Equal(200, batch2.Items.Last().MediaFileId);
        Assert.Equal(200, batch2.ContinuationAfterMediaFileId);

        // Batch 3: MaxItems = 100, AfterMediaFileId = batch2.ContinuationAfterMediaFileId (200)
        var batch3 = await service.ScanAsync(new LocalIdentityAutoScanRequest(Mode: LocalIdentityScanMode.Full, MaxItems: 100, AfterMediaFileId: batch2.ContinuationAfterMediaFileId, DryRun: true));
        Assert.Equal(50, batch3.Evaluated);
        Assert.Equal(201, batch3.Items.First().MediaFileId);
        Assert.Equal(250, batch3.Items.Last().MediaFileId);
        Assert.Equal(250, batch3.ContinuationAfterMediaFileId);
    }
}
