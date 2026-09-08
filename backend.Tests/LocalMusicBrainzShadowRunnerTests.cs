using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class LocalMusicBrainzShadowRunnerTests
{
    private AppDbContext CreateInMemoryDbContext()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source=file:{dbName}?mode=memory&cache=shared")
            .Options;
        var context = new AppDbContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private ILocalMusicBrainzService CreateMockLocalMbService(double confidence = 0.90)
    {
        var mock = new Mock<ILocalMusicBrainzService>();
        mock.Setup(m => m.BaseUrl).Returns("http://192.168.2.18:5050");
        mock.Setup(m => m.ScanMediaIdentityAsync(It.IsAny<MediaFile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MediaFile m, CancellationToken ct) =>
            {
                var cand = new LocalMusicBrainzCandidate(
                    RecordingId: Guid.NewGuid().ToString(),
                    ReleaseId: Guid.NewGuid().ToString(),
                    ArtistId: Guid.NewGuid().ToString(),
                    MatchedTitle: m.Title,
                    MatchedArtist: m.Artist,
                    MatchedDuration: m.Duration,
                    Disambiguation: null,
                    Confidence: confidence,
                    IsDerivativeOrClip: false
                );
                return new LocalMusicBrainzScanResult(true, cand, 200, null, 15);
            });
        return mock.Object;
    }

    [Fact]
    public async Task RunAsync_Rejects_CountOver1000_AndZeroOrNegative()
    {
        using var db = CreateInMemoryDbContext();
        var localMb = CreateMockLocalMbService();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LocalMusicBrainzShadowRunner.RunAsync(db, localMb, count: 0));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LocalMusicBrainzShadowRunner.RunAsync(db, localMb, count: -5));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LocalMusicBrainzShadowRunner.RunAsync(db, localMb, count: 1001));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            LocalMusicBrainzShadowRunner.RunAsync(db, localMb, count: 5000));
    }

    [Fact]
    public async Task RunAsync_Guarantees_ZeroWrite_To_Database_Cover_Lyrics_Identities()
    {
        using var db = CreateInMemoryDbContext();

        var source = new ScanSource { Id = 1, Name = "Test Source", Path = "smb://server/music" };
        db.ScanSources.Add(source);
        await db.SaveChangesAsync();

        // 1. Seed database with tracks, existing identities, lyrics, etc.
        for (int i = 1; i <= 15; i++)
        {
            db.MediaFiles.Add(new MediaFile
            {
                Id = i,
                ScanSourceId = 1,
                FilePath = $"smb://server/music/{i}.mp3",
                Title = $"Track {i}",
                Artist = $"Artist {i}",
                Album = $"Album {i}",
                Duration = TimeSpan.FromMinutes(3),
                CoverArt = i % 2 == 0 ? $"/api/media/cover/{i}.jpg" : null
            });
        }

        db.Lyrics.Add(new Lyric { Id = 1, MediaFileId = 1, Content = "[00:00.00] test", Version = "plain", Language = "en" });
        db.MediaIdentities.Add(new MediaIdentity { Id = 1, MediaFileId = 2, Provider = "MusicBrainz", RecordingId = "existing-mbid", Confidence = 0.95 });

        await db.SaveChangesAsync();

        // 2. Capture DB snapshot before shadow run
        var preMediaCount = await db.MediaFiles.CountAsync();
        var preLyricsCount = await db.Lyrics.CountAsync();
        var preIdentityCount = await db.MediaIdentities.CountAsync();
        var preEnrichmentCount = await db.MusicEnrichments.CountAsync();
        var preAttemptCount = await db.EnrichmentAttempts.CountAsync();

        var preMediaTitles = await db.MediaFiles.OrderBy(m => m.Id).Select(m => $"{m.Id}:{m.Title}:{m.CoverArt}").ToListAsync();
        var preStateHash = ComputeStateHash(preMediaTitles);

        var localMb = CreateMockLocalMbService(0.92);

        // 3. Run Shadow Run for 10 tracks
        var report = await LocalMusicBrainzShadowRunner.RunAsync(db, localMb, count: 10);

        Assert.NotNull(report);
        Assert.Equal("SHADOW_RUN_ZERO_WRITE", report.Mode);
        Assert.True(report.Summary.TotalEvaluated > 0);

        // 4. Capture DB snapshot after shadow run and assert absolute zero write
        var postMediaCount = await db.MediaFiles.CountAsync();
        var postLyricsCount = await db.Lyrics.CountAsync();
        var postIdentityCount = await db.MediaIdentities.CountAsync();
        var postEnrichmentCount = await db.MusicEnrichments.CountAsync();
        var postAttemptCount = await db.EnrichmentAttempts.CountAsync();

        var postMediaTitles = await db.MediaFiles.OrderBy(m => m.Id).Select(m => $"{m.Id}:{m.Title}:{m.CoverArt}").ToListAsync();
        var postStateHash = ComputeStateHash(postMediaTitles);

        Assert.Equal(preMediaCount, postMediaCount);
        Assert.Equal(preLyricsCount, postLyricsCount);
        Assert.Equal(preIdentityCount, postIdentityCount);
        Assert.Equal(preEnrichmentCount, postEnrichmentCount);
        Assert.Equal(preAttemptCount, postAttemptCount);
        Assert.Equal(preStateHash, postStateHash);
    }

    [Fact]
    public async Task RunAsync_Prevents_ConcurrentExecutions()
    {
        using var db = CreateInMemoryDbContext();
        var source = new ScanSource { Id = 1, Name = "Test Source", Path = "smb://server/music" };
        db.ScanSources.Add(source);
        db.MediaFiles.Add(new MediaFile { Id = 1, ScanSourceId = 1, Title = "Song", Artist = "Artist", Duration = TimeSpan.FromMinutes(3) });
        await db.SaveChangesAsync();

        var localMbMock = new Mock<ILocalMusicBrainzService>();
        localMbMock.Setup(m => m.BaseUrl).Returns("http://192.168.2.18:5050");
        localMbMock.Setup(m => m.ScanMediaIdentityAsync(It.IsAny<MediaFile>(), It.IsAny<CancellationToken>()))
            .Returns(async (MediaFile m, CancellationToken ct) =>
            {
                await Task.Delay(500, ct); // simulate in-flight work
                return new LocalMusicBrainzScanResult(true, null, 200, null, 500);
            });

        // Launch first shadow run (takes ~500ms)
        var task1 = LocalMusicBrainzShadowRunner.RunAsync(db, localMbMock.Object, count: 1);

        // Give task1 time to acquire the single-task lock
        await Task.Delay(50);

        // Launch second concurrent shadow run - must be rejected with InvalidOperationException
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LocalMusicBrainzShadowRunner.RunAsync(db, localMbMock.Object, count: 1));

        Assert.Contains("already in progress", ex.Message);

        // Wait for first task to finish cleanly
        var report1 = await task1;
        Assert.NotNull(report1);
    }

    private static string ComputeStateHash(IEnumerable<string> rows)
    {
        using var sha = SHA256.Create();
        var combined = string.Join(";", rows);
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(combined)));
    }
}
