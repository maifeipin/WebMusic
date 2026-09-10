using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class ExternalSignalRefreshServiceTests
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

    private static LocalMusicBrainzRecordingDetails SampleDetails(string recordingId) =>
        new(recordingId, "Sample Track", new[] { "USABC1234567" }, 4.5, 10, $"http://mb/{recordingId}", "payloadhash");

    [Fact]
    public async Task Refresh_DualMusicBrainzIdentities_WithSameMbid_MergesIntoSingleCandidate()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        db.MediaFiles.Add(media);
        db.MediaIdentities.AddRange(
            new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainz", RecordingId = mbid, Status = "approved" },
            new MediaIdentity { Id = 2, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" });
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        mbDetails.Setup(d => d.GetRecordingAsync(mbid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleDetails(mbid));

        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10));

        Assert.Equal(1, report.Evaluated);
        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Failed);
        Assert.Equal(1, await db.MediaExternalReferences.CountAsync());
        Assert.Equal(2, await db.MediaExternalSignals.CountAsync());
    }

    [Fact]
    public async Task Refresh_DualMusicBrainzIdentities_WithDifferentMbid_FlagsIdentityConflict_ZeroWrites()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        db.MediaFiles.Add(media);
        // Conflicting recording IDs for the same media file
        db.MediaIdentities.AddRange(
            new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainz", RecordingId = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc", Status = "approved" },
            new MediaIdentity { Id = 2, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = "c0c34a5c-523a-4d58-b5ec-8d6ed95596cc", Status = "approved" });
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10));

        Assert.Equal(1, report.Evaluated);
        Assert.Equal(0, report.Updated);
        Assert.Equal(1, report.Failed);
        var item = Assert.Single(report.Items);
        Assert.Equal("IdentityConflict", item.Status);
        Assert.Contains("Multiple conflicting approved MusicBrainz", item.Message);

        // Zero writes to external references or signals
        Assert.Equal(0, await db.MediaExternalReferences.CountAsync());
        Assert.Equal(0, await db.MediaExternalSignals.CountAsync());
    }

    [Fact]
    public async Task Refresh_CursorProgression_AcrossBatches()
    {
        await using var db = CreateDb();
        for (int i = 1; i <= 25; i++)
        {
            var media = new MediaFile { Id = i, ScanSourceId = 1, FilePath = $"smb://test/track_{i}.mp3", Title = $"Track {i}", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
            var identity = new MediaIdentity { Id = i, MediaFileId = i, Provider = "MusicBrainzLocal", RecordingId = Guid.NewGuid().ToString(), Status = "approved" };
            db.MediaFiles.Add(media);
            db.MediaIdentities.Add(identity);
        }
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        mbDetails.Setup(d => d.GetRecordingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => SampleDetails(id));

        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        // Batch 1: Count = 10, AfterMediaFileId = null
        var batch1 = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10));
        Assert.Equal(10, batch1.Evaluated);
        Assert.Equal(10, batch1.ContinuationAfterMediaFileId);

        // Batch 2: Count = 10, AfterMediaFileId = 10
        var batch2 = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10, AfterMediaFileId: 10));
        Assert.Equal(10, batch2.Evaluated);
        Assert.Equal(20, batch2.ContinuationAfterMediaFileId);

        // Batch 3: Count = 10, AfterMediaFileId = 20
        var batch3 = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10, AfterMediaFileId: 20));
        Assert.Equal(5, batch3.Evaluated);
        Assert.Equal(25, batch3.ContinuationAfterMediaFileId);
    }

    [Fact]
    public async Task Refresh_FreshSignal_SkippedWithoutForce_AndRefreshedWithForce()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        var identity = new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" };
        var reference = new MediaExternalReference { Id = 1, MediaFileId = 1, Provider = "MusicBrainz", SubjectType = "Recording", ExternalId = mbid, Status = "approved", MatchMethod = "test" };
        var signal = new MediaExternalSignal { Id = 1, MediaExternalReferenceId = 1, SignalKey = "CommunityScore", AlgorithmVersion = "v1", RefreshAfter = DateTime.UtcNow.AddDays(7), Status = "observed" };
        reference.Signals.Add(signal);

        db.MediaFiles.Add(media);
        db.MediaIdentities.Add(identity);
        db.MediaExternalReferences.Add(reference);
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        mbDetails.Setup(d => d.GetRecordingAsync(mbid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleDetails(mbid));

        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        // Run without force: skipped
        var report1 = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10, Force: false));
        Assert.Equal(1, report1.Skipped);
        Assert.Equal(0, report1.Updated);
        Assert.Equal("Skipped", report1.Items.Single().Status);

        // Run with force: refreshed
        var report2 = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10, Force: true));
        Assert.Equal(0, report2.Skipped);
        Assert.Equal(1, report2.Updated);
        Assert.Equal("Updated", report2.Items.Single().Status);
    }

    [Fact]
    public async Task Refresh_MusicBrainz_DryRun_ProbesProvider_RecordsPlannedOnSuccess_WithZeroWrites()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        db.MediaFiles.Add(media);
        db.MediaIdentities.Add(new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" });
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        mbDetails.Setup(d => d.GetRecordingAsync(mbid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SampleDetails(mbid));

        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10, DryRun: true));
        Assert.Equal(1, report.Updated);
        Assert.Equal("Planned", report.Items.Single().Status);
        Assert.Equal(0, await db.MediaExternalReferences.CountAsync());
        Assert.Equal(0, await db.MediaExternalSignals.CountAsync());
    }

    [Fact]
    public async Task Refresh_LastFm_DryRun_ProbesProvider_RecordsPlannedOnSuccess_WithZeroWrites()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        db.MediaFiles.Add(media);
        db.MediaIdentities.Add(new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" });
        await db.SaveChangesAsync();

        var lastFmClient = new Mock<ILastFmTrackInfoClient>();
        lastFmClient.Setup(c => c.GetTrackByMbidAsync(mbid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LastFmTrackInfo(mbid, 5000, 20000, "http://last.fm/track", "hash"));

        var lastFmService = new LastFmGlobalPopularityService(db);
        var service = new ExternalSignalRefreshService(db, null, null, lastFmClient.Object, lastFmService, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("lastfm", Count: 10, DryRun: true));
        Assert.Equal(1, report.Updated);
        Assert.Equal("Planned", report.Items.Single().Status);
        Assert.Equal(0, await db.MediaExternalReferences.CountAsync());
        Assert.Equal(0, await db.MediaExternalSignals.CountAsync());
    }

    [Fact]
    public async Task Refresh_DryRun_WhenProviderReturnsNull_MarksAsFailed_WithZeroWrites()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        db.MediaFiles.Add(media);
        db.MediaIdentities.Add(new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" });
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        mbDetails.Setup(d => d.GetRecordingAsync(mbid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LocalMusicBrainzRecordingDetails?)null);

        var service = new ExternalSignalRefreshService(db, mbDetails.Object, null, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10, DryRun: true));
        Assert.Equal(1, report.Failed);
        Assert.Equal("Failed", report.Items.Single().Status);
        Assert.Equal(0, await db.MediaExternalReferences.CountAsync());
        Assert.Equal(0, await db.MediaExternalSignals.CountAsync());
    }

    [Fact]
    public async Task Refresh_InitialFailure_CreatesDurableFailedRecord_WithRefreshAfter()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        db.MediaFiles.Add(media);
        db.MediaIdentities.Add(new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" });
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        // Returns null to simulate failure
        mbDetails.Setup(d => d.GetRecordingAsync(mbid, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LocalMusicBrainzRecordingDetails?)null);

        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10));
        Assert.Equal(1, report.Failed);
        Assert.Equal("Failed", report.Items.Single().Status);

        // A durable failed record must exist in DB with RefreshAfter set
        var reference = await db.MediaExternalReferences.Include(r => r.Signals).SingleAsync();
        Assert.Equal("failed", reference.Status);
        var signal = Assert.Single(reference.Signals);
        Assert.Equal("failed", signal.Status);
        Assert.NotNull(signal.RefreshAfter);
        Assert.True(signal.RefreshAfter > DateTime.UtcNow);
        Assert.Contains("returned no details", signal.LastError);
    }

    [Fact]
    public async Task Refresh_On429RateLimit_AbortsEarly_KeepsCursorAtPreviousItem_AndRecordsRateLimitedStatus()
    {
        await using var db = CreateDb();
        var mbid1 = "11111111-1111-1111-1111-111111111111";
        var mbid2 = "22222222-2222-2222-2222-222222222222";
        var mbid3 = "33333333-3333-3333-3333-333333333333";

        db.MediaFiles.AddRange(
            new MediaFile { Id = 1, ScanSourceId = 1, FilePath = "smb://test/1.mp3", Title = "Track 1", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 2, ScanSourceId = 1, FilePath = "smb://test/2.mp3", Title = "Track 2", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) },
            new MediaFile { Id = 3, ScanSourceId = 1, FilePath = "smb://test/3.mp3", Title = "Track 3", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) }
        );
        db.MediaIdentities.AddRange(
            new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid1, Status = "approved" },
            new MediaIdentity { Id = 2, MediaFileId = 2, Provider = "MusicBrainzLocal", RecordingId = mbid2, Status = "approved" },
            new MediaIdentity { Id = 3, MediaFileId = 3, Provider = "MusicBrainzLocal", RecordingId = mbid3, Status = "approved" }
        );
        await db.SaveChangesAsync();

        var lastFmClient = new Mock<ILastFmTrackInfoClient>();
        lastFmClient.Setup(c => c.GetTrackByMbidAsync(mbid1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LastFmTrackInfo(mbid1, 1000, 5000, "http://last.fm/track1", "hash1"));
        lastFmClient.Setup(c => c.GetTrackByMbidAsync(mbid2, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new LastFmRateLimitException(TimeSpan.FromSeconds(120), "Rate limit exceeded"));

        var lastFmService = new LastFmGlobalPopularityService(db);
        var service = new ExternalSignalRefreshService(db, null, null, lastFmClient.Object, lastFmService, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("lastfm", Count: 10));

        Assert.True(report.Aborted);
        Assert.NotNull(report.StopReason);
        Assert.Contains("RateLimited", report.StopReason);
        Assert.Equal(2, report.Evaluated);
        Assert.Equal(2, report.Items.Count);
        Assert.Equal(1, report.Updated);
        Assert.Equal(1, report.Failed);
        // CRUCIAL: continuation cursor stopped at 1 (the last completed item), so item 2 is not skipped next time!
        Assert.Equal(1, report.ContinuationAfterMediaFileId);

        // Check database state
        var ref1 = await db.MediaExternalReferences.Include(r => r.Signals).SingleAsync(r => r.MediaFileId == 1);
        Assert.Equal("observed", ref1.Signals.Single(s => s.SignalKey == "GlobalPopularity").Status);

        var ref2 = await db.MediaExternalReferences.Include(r => r.Signals).SingleAsync(r => r.MediaFileId == 2);
        Assert.Equal("RateLimited", ref2.Status);
        var sig2 = ref2.Signals.Single();
        Assert.Equal("RateLimited", sig2.Status);
        Assert.NotNull(sig2.RefreshAfter);
        Assert.True(sig2.RefreshAfter > DateTime.UtcNow.AddSeconds(60));

        // Item 3 was never touched because batch was aborted
        Assert.Null(await db.MediaExternalReferences.FirstOrDefaultAsync(r => r.MediaFileId == 3));
    }

    [Fact]
    public async Task Refresh_WhenUpdateFails_PreservesExistingScoreValues()
    {
        await using var db = CreateDb();
        var mbid = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var identity = new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" };

        var reference = new MediaExternalReference { Id = 1, MediaFileId = 1, Provider = "MusicBrainz", SubjectType = "Recording", ExternalId = mbid, Status = "approved", MatchMethod = "test" };
        var signal = new MediaExternalSignal
        {
            Id = 1,
            MediaExternalReferenceId = 1,
            SignalKey = "CommunityScore",
            AlgorithmVersion = "v1",
            RawValue = 4.8,
            NormalizedScore = 88.5,
            SampleSize = 25,
            RefreshAfter = DateTime.UtcNow.AddMinutes(-5), // Expired, so eligible for refresh
            Status = "observed"
        };
        reference.Signals.Add(signal);

        db.MediaFiles.Add(media);
        db.MediaIdentities.Add(identity);
        db.MediaExternalReferences.Add(reference);
        await db.SaveChangesAsync();

        var mbDetails = new Mock<ILocalMusicBrainzDetailService>();
        mbDetails.Setup(d => d.GetRecordingAsync(mbid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection timed out"));

        var mbSignalService = new MusicBrainzCommunitySignalService(db);
        var service = new ExternalSignalRefreshService(db, mbDetails.Object, mbSignalService, null, null, NullLogger<ExternalSignalRefreshService>.Instance);

        var report = await service.RefreshAsync(new ExternalSignalRefreshRequest("musicbrainz", Count: 10));

        Assert.Equal(1, report.Failed);
        Assert.Equal("Failed", report.Items.Single().Status);

        // Verify that existing scores are PRESERVED and not wiped out by failure
        var reloadedSignal = await db.MediaExternalSignals.SingleAsync(s => s.Id == 1);
        Assert.Equal(88.5, reloadedSignal.NormalizedScore);
        Assert.Equal(4.8, reloadedSignal.RawValue);
        Assert.Equal(25, reloadedSignal.SampleSize);
        Assert.Equal("failed", reloadedSignal.Status);
        Assert.Contains("Connection timed out", reloadedSignal.LastError);
        Assert.True(reloadedSignal.RefreshAfter > DateTime.UtcNow);
    }
}
