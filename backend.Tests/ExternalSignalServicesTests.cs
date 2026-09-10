using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class ExternalSignalServicesTests
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

    [Fact]
    public async Task MusicBrainzCommunityScore_StoresRatingAndSampleWithoutPersonalSignals()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var identity = new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = "c0c34a5c-523a-4d58-b5ec-8d6ed95596cc", MatchMethod = "Audited", Confidence = 1, Status = "approved", MediaFile = media };
        db.Add(media);
        db.Add(identity);
        await db.SaveChangesAsync();

        var service = new MusicBrainzCommunitySignalService(db);
        await service.UpsertAsync(identity, new LocalMusicBrainzRecordingDetails(identity.RecordingId!, "Track", new[] { "USABC1234567" }, 4.5, 10, "http://192.168.2.18:5050/recording/x", "payload"));

        var reference = await db.MediaExternalReferences.Include(reference => reference.Signals).SingleAsync();
        Assert.Equal("MusicBrainz", reference.Provider);
        Assert.Equal("Recording", reference.SubjectType);
        Assert.Equal(2, reference.Signals.Count);
        var score = Assert.Single(reference.Signals.Where(signal => signal.SignalKey == "CommunityScore"));
        Assert.Equal(4.5 * Math.Log(11), score.RawValue!.Value, 8);
        Assert.NotNull(score.NormalizedScore);
        Assert.Equal(10, score.SampleSize);
    }

    [Fact]
    public async Task MusicBrainzCommunityScore_LowSampleIsStoredButNotRanked()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var identity = new MediaIdentity { MediaFileId = 1, RecordingId = "c0c34a5c-523a-4d58-b5ec-8d6ed95596cc", MediaFile = media };
        db.AddRange(media, identity);
        await db.SaveChangesAsync();

        await new MusicBrainzCommunitySignalService(db).UpsertAsync(identity,
            new LocalMusicBrainzRecordingDetails(identity.RecordingId!, "Track", Array.Empty<string>(), 5, 2, null, "payload"));

        var score = await db.MediaExternalSignals.SingleAsync(signal => signal.SignalKey == "CommunityScore");
        Assert.Null(score.NormalizedScore);
        Assert.Equal(2, score.SampleSize);
    }

    [Fact]
    public async Task LocalMusicBrainzDetailService_ParsesNumericRatingsAndStringRatingsFromJson()
    {
        var recordingId = "b0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        var json = @"{
            ""id"": """ + recordingId + @""",
            ""title"": ""Test Recording"",
            ""length"": 210000,
            ""rating"": {
                ""value"": 4.75,
                ""votes-count"": 15
            },
            ""isrcs"": [""USABC1234567""]
        }";

        var handler = new MockHttpMessageHandler(json);
        var client = new HttpClient(handler);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MusicBrainz:LocalBaseUrl"] = "http://127.0.0.1:5050"
        }).Build();

        var service = new LocalMusicBrainzDetailService(client, config);
        var details = await service.GetRecordingAsync(recordingId);

        Assert.NotNull(details);
        Assert.Equal("Test Recording", details.Title);
        Assert.Equal(4.75, details.Rating);
        Assert.Equal(15, details.RatingCount);
        Assert.Equal(210.0, details.DurationSeconds);
    }

    [Fact]
    public async Task LastFmPopularity_Rejects_WhenNoApprovedMusicBrainzIdentityExists()
    {
        await using var db = CreateDb();
        db.MediaFiles.Add(new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) });
        await db.SaveChangesAsync();

        var service = new LastFmGlobalPopularityService(db);
        var track = new LastFmTrackInfo("c0c34a5c-523a-4d58-b5ec-8d6ed95596cc", 1_000_000, 10_000_000, "https://www.last.fm/music/x", "payload");

        // Should throw because no approved identity exists for MediaFileId = 1
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertAsync(1, track));
        Assert.Contains("does not have an approved MusicBrainz identity", ex.Message);
    }

    [Fact]
    public async Task LastFmPopularity_UsesOnlyProviderMetricsAndUpsertsGenericSignals()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var mbid = "c0c34a5c-523a-4d58-b5ec-8d6ed95596cc";
        var identity = new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = mbid, Status = "approved" };
        db.AddRange(media, identity);
        await db.SaveChangesAsync();

        var service = new LastFmGlobalPopularityService(db);
        var track = new LastFmTrackInfo(mbid, 1_000_000, 10_000_000, "https://www.last.fm/music/x", "payload");

        await service.UpsertAsync(1, track);
        await service.UpsertAsync(1, track);

        var reference = await db.MediaExternalReferences.Include(reference => reference.Signals).SingleAsync();
        Assert.Equal("LastFm", reference.Provider);
        Assert.Equal(3, reference.Signals.Count);
        var popularity = Assert.Single(reference.Signals.Where(signal => signal.SignalKey == "GlobalPopularity"));
        Assert.InRange(popularity.NormalizedScore!.Value, 0, 100);
        Assert.Equal(1_000_000, popularity.SampleSize);
        Assert.Equal(1, await db.MediaExternalReferences.CountAsync());
    }

    [Fact]
    public void LastFmPopularity_IsMonotonic_AndIndependentOfPersonalData()
    {
        Assert.True(LastFmGlobalPopularityService.ComputePopularity(1_000_000, 100_000) >
                    LastFmGlobalPopularityService.ComputePopularity(10_000, 1_000));
    }

    [Fact]
    public async Task LastFmPopularity_Rejects_WhenApprovedIdentityHasNoValidRecordingId()
    {
        await using var db = CreateDb();
        var media = new MediaFile { Id = 1, ScanSourceId = 1, Title = "Track", Artist = "Artist", Album = "Album", Duration = TimeSpan.FromMinutes(3) };
        var identity = new MediaIdentity { Id = 1, MediaFileId = 1, Provider = "MusicBrainzLocal", RecordingId = "   ", Status = "approved" };
        db.AddRange(media, identity);
        await db.SaveChangesAsync();

        var service = new LastFmGlobalPopularityService(db);
        var track = new LastFmTrackInfo("c0c34a5c-523a-4d58-b5ec-8d6ed95596cc", 1_000, 10_000, "https://www.last.fm/music/x", "payload");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpsertAsync(1, track));
        Assert.Contains("no valid RecordingId", ex.Message);
    }

    [Fact]
    public async Task LastFmClient_ParsesRetryAfter_WithDeltaSeconds()
    {
        var handler = new CustomResponseHandler(req =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return res;
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ws.audioscrobbler.com/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LastFm:Enabled"] = "true",
            ["LastFm:ApiKey"] = "test_key"
        }).Build();

        var lastFmClient = new LastFmTrackInfoClient(client, config);
        var ex = await Assert.ThrowsAsync<LastFmRateLimitException>(() =>
            lastFmClient.GetTrackByMbidAsync("c0c34a5c-523a-4d58-b5ec-8d6ed95596cc"));

        Assert.NotNull(ex.RetryAfter);
        Assert.Equal(45, ex.RetryAfter.Value.TotalSeconds);
    }

    [Fact]
    public async Task LastFmClient_ParsesRetryAfter_WithHttpDate()
    {
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(5);
        var handler = new CustomResponseHandler(req =>
        {
            var res = new HttpResponseMessage((HttpStatusCode)429);
            res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(futureDate);
            return res;
        });
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://ws.audioscrobbler.com/") };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["LastFm:Enabled"] = "true",
            ["LastFm:ApiKey"] = "test_key"
        }).Build();

        var lastFmClient = new LastFmTrackInfoClient(client, config);
        var ex = await Assert.ThrowsAsync<LastFmRateLimitException>(() =>
            lastFmClient.GetTrackByMbidAsync("c0c34a5c-523a-4d58-b5ec-8d6ed95596cc"));

        Assert.NotNull(ex.RetryAfter);
        Assert.InRange(ex.RetryAfter.Value.TotalSeconds, 280, 310);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly string _response;
        public MockHttpMessageHandler(string response) => _response = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CustomResponseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public CustomResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }
}
