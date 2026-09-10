using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public sealed record LocalMusicBrainzRecordingDetails(
    string RecordingId,
    string Title,
    IReadOnlyList<string> Isrcs,
    double? Rating,
    long? RatingCount,
    string? CanonicalUrl,
    string? RawPayloadHash,
    string? CanonicalArtist = null,
    double? DurationSeconds = null,
    IReadOnlyList<string>? ReleaseIds = null,
    IReadOnlyList<string>? ReleaseGroupIds = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Genres = null);

public interface ILocalMusicBrainzDetailService
{
    Task<LocalMusicBrainzRecordingDetails?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken = default);
}

/// <summary>Reads only from the private MusicBrainz mirror; it has no public fallback.</summary>
public sealed class LocalMusicBrainzDetailService : ILocalMusicBrainzDetailService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public LocalMusicBrainzDetailService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _baseUrl = (configuration["MusicBrainz:LocalBaseUrl"] ?? "http://192.168.2.18:5050").TrimEnd('/');
        LocalMusicBrainzService.ValidatePrivateOrLoopbackEndpoint(_baseUrl);
    }

    public async Task<LocalMusicBrainzRecordingDetails?> GetRecordingAsync(string recordingId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(recordingId, out _))
        {
            throw new ArgumentException("MusicBrainz recording IDs must be UUIDs.", nameof(recordingId));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _httpClient.GetAsync(
            $"{_baseUrl}/ws/2/recording/{recordingId}?inc=ratings+isrcs+tags+genres+releases+release-groups+artist-credits&fmt=json",
            timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(timeout.Token);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var title = root.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? string.Empty : string.Empty;
        var isrcs = root.TryGetProperty("isrcs", out var isrcProp) && isrcProp.ValueKind == JsonValueKind.Array
            ? isrcProp.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList()
            : new List<string>();
        var artist = root.TryGetProperty("artist-credit", out var artistProp) && artistProp.ValueKind == JsonValueKind.Array
            ? string.Concat(artistProp.EnumerateArray().Select(value => value.TryGetProperty("name", out var name) ? name.GetString() : null))
            : null;
        var durationSeconds = root.TryGetProperty("length", out var lengthProp) && lengthProp.TryGetDouble(out var milliseconds)
            ? milliseconds / 1000d
            : (double?)null;

        double? rating = null;
        long? ratingCount = null;
        if (root.TryGetProperty("rating", out var ratingProp) && ratingProp.ValueKind == JsonValueKind.Object)
        {
            if (ratingProp.TryGetProperty("value", out var valueProp))
            {
                if (valueProp.ValueKind == JsonValueKind.Number && valueProp.TryGetDouble(out var num))
                {
                    rating = num;
                }
                else if (valueProp.ValueKind == JsonValueKind.String && double.TryParse(valueProp.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var strNum))
                {
                    rating = strNum;
                }
            }
            if (ratingProp.TryGetProperty("votes-count", out var countProp))
            {
                if (countProp.ValueKind == JsonValueKind.Number && countProp.TryGetInt64(out var cnt))
                {
                    ratingCount = cnt;
                }
                else if (countProp.ValueKind == JsonValueKind.String && long.TryParse(countProp.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var strCnt))
                {
                    ratingCount = strCnt;
                }
            }
        }

        return new LocalMusicBrainzRecordingDetails(
            recordingId,
            title,
            isrcs,
            rating,
            ratingCount,
            $"{_baseUrl}/recording/{recordingId}",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant(),
            artist,
            durationSeconds,
            ExtractIds(root, "releases", "id"),
            ExtractNestedIds(root, "releases", "release-group", "id"),
            ExtractNames(root, "tags", "name"),
            ExtractNames(root, "genres", "name"));
    }

    private static IReadOnlyList<string> ExtractIds(JsonElement root, string property, string idProperty) =>
        root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.TryGetProperty(idProperty, out var id) ? id.GetString() : null).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct().ToList()
            : Array.Empty<string>();

    private static IReadOnlyList<string> ExtractNestedIds(JsonElement root, string property, string nestedProperty, string idProperty) =>
        root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.TryGetProperty(nestedProperty, out var nested) && nested.TryGetProperty(idProperty, out var id) ? id.GetString() : null).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!).Distinct().ToList()
            : Array.Empty<string>();

    private static IReadOnlyList<string> ExtractNames(JsonElement root, string property, string nameProperty) =>
        root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.TryGetProperty(nameProperty, out var name) ? name.GetString() : null).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct().ToList()
            : Array.Empty<string>();
}

public interface IMusicBrainzCommunitySignalService
{
    Task UpsertAsync(MediaIdentity identity, LocalMusicBrainzRecordingDetails details, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores MusicBrainz's community rating separately from popularity.  A rating
/// with fewer than three votes is retained as evidence but is not normalized for
/// default ranking.
/// </summary>
public sealed class MusicBrainzCommunitySignalService : IMusicBrainzCommunitySignalService
{
    public const string Provider = "MusicBrainz";
    public const string SubjectType = "Recording";
    public const string AlgorithmVersion = "MusicBrainzCommunityScore:v1";

    private readonly AppDbContext _db;

    public MusicBrainzCommunitySignalService(AppDbContext db) => _db = db;

    public async Task UpsertAsync(MediaIdentity identity, LocalMusicBrainzRecordingDetails details, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(identity.RecordingId) || !string.Equals(identity.RecordingId, details.RecordingId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supplied MusicBrainz details do not belong to the approved media identity.");
        }

        var reference = await _db.MediaExternalReferences
            .Include(reference => reference.Signals)
            .SingleOrDefaultAsync(reference => reference.MediaFileId == identity.MediaFileId && reference.Provider == Provider && reference.SubjectType == SubjectType, cancellationToken);

        if (reference is null)
        {
            reference = new MediaExternalReference
            {
                MediaFileId = identity.MediaFileId,
                Provider = Provider,
                SubjectType = SubjectType
            };
            _db.MediaExternalReferences.Add(reference);
        }

        reference.ExternalId = details.RecordingId;
        reference.CanonicalUrl = details.CanonicalUrl;
        reference.MatchMethod = identity.MatchMethod;
        reference.MatchConfidence = identity.Confidence;
        reference.Status = identity.Status;
        reference.MetadataFingerprint = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(identity.MediaFile ?? await _db.MediaFiles.FindAsync(new object[] { identity.MediaFileId }, cancellationToken)
            ?? throw new InvalidOperationException("Media file does not exist."));
        reference.MetadataJson = JsonSerializer.Serialize(new
        {
            details.Title,
            details.CanonicalArtist,
            details.DurationSeconds,
            details.Isrcs,
            details.ReleaseIds,
            details.ReleaseGroupIds,
            details.Tags,
            details.Genres
        });
        reference.VerifiedAt = DateTime.UtcNow;

        UpsertSignal(reference, "CommunityRating", details.Rating, null, details.RatingCount, details, null);

        var score = ComputeCommunityScore(details.Rating, details.RatingCount);
        double? normalized = details.RatingCount.GetValueOrDefault() >= 3 ? Math.Min(100d, score ?? 0d) : null;
        UpsertSignal(reference, "CommunityScore", score, normalized, details.RatingCount, details, null);

        await _db.SaveChangesAsync(cancellationToken);
    }

    public static double? ComputeCommunityScore(double? rating, long? ratingCount) =>
        rating.HasValue && ratingCount.GetValueOrDefault() > 0
            ? rating.Value * Math.Log(1 + ratingCount.GetValueOrDefault())
            : null;

    private static void UpsertSignal(
        MediaExternalReference reference,
        string key,
        double? rawValue,
        double? normalizedScore,
        long? sampleSize,
        LocalMusicBrainzRecordingDetails details,
        string? error)
    {
        var signal = reference.Signals.SingleOrDefault(signal => signal.SignalKey == key);
        if (signal is null)
        {
            signal = new MediaExternalSignal { SignalKey = key };
            reference.Signals.Add(signal);
        }

        signal.RawValue = rawValue;
        signal.NormalizedScore = normalizedScore;
        signal.SampleSize = sampleSize;
        signal.RawMetricsJson = JsonSerializer.Serialize(new { details.Rating, details.RatingCount });
        signal.AlgorithmVersion = AlgorithmVersion;
        signal.SourceUrl = details.CanonicalUrl;
        signal.SourcePayloadHash = details.RawPayloadHash;
        signal.ObservedAt = DateTime.UtcNow;
        signal.RefreshAfter = DateTime.UtcNow.AddDays(30);
        signal.Status = rawValue.HasValue ? "observed" : "unavailable";
        signal.LastError = error;
    }
}

public sealed record LastFmTrackInfo(string Mbid, long Listeners, long Playcount, string? Url, string PayloadHash);

public sealed class LastFmRateLimitException : Exception
{
    public TimeSpan? RetryAfter { get; }
    public LastFmRateLimitException(TimeSpan? retryAfter, string message) : base(message)
    {
        RetryAfter = retryAfter;
    }
}

public interface ILastFmTrackInfoClient
{
    Task<LastFmTrackInfo?> GetTrackByMbidAsync(string mbid, CancellationToken cancellationToken = default);
}

/// <summary>
/// This client is inert until a user supplies an API key and explicitly invokes
/// a score refresh. No hosted service or background scheduling is registered.
/// </summary>
public sealed class LastFmTrackInfoClient : ILastFmTrackInfoClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public LastFmTrackInfoClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<LastFmTrackInfo?> GetTrackByMbidAsync(string mbid, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(mbid, out _)) throw new ArgumentException("Last.fm lookups require a MusicBrainz UUID.", nameof(mbid));
        if (!bool.TryParse(_configuration["LastFm:Enabled"], out var enabled) || !enabled)
        {
            throw new InvalidOperationException("Last.fm refresh is disabled. Set LastFm:Enabled=true only after explicit approval.");
        }
        var apiKey = _configuration["LastFm:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("LastFm:ApiKey is required for a refresh.");

        // Rate-limiting pause between requests (200ms)
        await Task.Delay(200, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var url = $"2.0/?method=track.getInfo&mbid={Uri.EscapeDataString(mbid)}&api_key={Uri.EscapeDataString(apiKey)}&format=json";
        using var response = await _httpClient.GetAsync(url, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    retryAfter = response.Headers.RetryAfter.Delta.Value;
                }
                else if (response.Headers.RetryAfter.Date.HasValue)
                {
                    var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    retryAfter = diff > TimeSpan.Zero ? diff : TimeSpan.FromSeconds(30);
                }
            }
            throw new LastFmRateLimitException(retryAfter, $"Last.fm 429 Too Many Requests. Retry-After: {retryAfter?.TotalSeconds ?? 30:F0}s");
        }
        if (!response.IsSuccessStatusCode) return null;
        var payload = await response.Content.ReadAsStringAsync(timeout.Token);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("track", out var track)) return null;
        var listeners = ParseLong(track, "listeners");
        var playcount = ParseLong(track, "playcount");
        var canonicalUrl = track.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
        return new LastFmTrackInfo(mbid, listeners, playcount, canonicalUrl,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant());
    }

    private static long ParseLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && long.TryParse(value.GetString(), out var result) ? result : 0;
}

public interface ILastFmGlobalPopularityService
{
    Task UpsertAsync(int mediaFileId, LastFmTrackInfo track, CancellationToken cancellationToken = default);
}

public sealed class LastFmGlobalPopularityService : ILastFmGlobalPopularityService
{
    public const string Provider = "LastFm";
    public const string SubjectType = "Track";
    public const string AlgorithmVersion = "LastFmGlobalPopularity:v1";
    private readonly AppDbContext _db;

    public LastFmGlobalPopularityService(AppDbContext db) => _db = db;

    public async Task UpsertAsync(int mediaFileId, LastFmTrackInfo track, CancellationToken cancellationToken = default)
    {
        var approvedIdentities = await _db.MediaIdentities
            .AsNoTracking()
            .Where(i => i.MediaFileId == mediaFileId &&
                (i.Provider == "MusicBrainz" || i.Provider == "MusicBrainzLocal") &&
                i.Status == "approved")
            .ToListAsync(cancellationToken);

        if (approvedIdentities.Count == 0)
        {
            throw new InvalidOperationException($"Cannot upsert Last.fm signals: MediaFileId {mediaFileId} does not have an approved MusicBrainz identity.");
        }

        var distinctMbids = approvedIdentities
            .Where(i => !string.IsNullOrWhiteSpace(i.RecordingId))
            .Select(i => i.RecordingId!.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();

        if (distinctMbids.Count != 1)
        {
            if (distinctMbids.Count == 0)
            {
                throw new InvalidOperationException($"Cannot upsert Last.fm signals: MediaFileId {mediaFileId} has approved MusicBrainz identity but no valid RecordingId.");
            }
            throw new InvalidOperationException($"IdentityConflict: MediaFileId {mediaFileId} has conflicting approved MusicBrainz identities ({string.Join(", ", distinctMbids)}). Manual review required.");
        }

        var canonicalIdentity = approvedIdentities.FirstOrDefault(i => i.Provider == "MusicBrainzLocal") ?? approvedIdentities.First();
        var canonicalMbid = canonicalIdentity.RecordingId!;

        if (!string.Equals(canonicalMbid, track.Mbid, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Last.fm result MBID '{track.Mbid}' does not match the approved MusicBrainz identity '{canonicalMbid}'.");
        }

        var reference = await _db.MediaExternalReferences.Include(reference => reference.Signals)
            .SingleOrDefaultAsync(reference => reference.MediaFileId == mediaFileId && reference.Provider == Provider && reference.SubjectType == SubjectType, cancellationToken);
        if (reference is null)
        {
            reference = new MediaExternalReference { MediaFileId = mediaFileId, Provider = Provider, SubjectType = SubjectType };
            _db.MediaExternalReferences.Add(reference);
        }

        reference.ExternalId = canonicalMbid;
        reference.CanonicalUrl = track.Url;
        reference.MatchMethod = "MusicBrainzMbid:v1";
        reference.MatchConfidence = 1;
        reference.Status = "approved";
        reference.VerifiedAt = DateTime.UtcNow;

        Upsert(reference, "Listeners", track.Listeners, null, track.Listeners, track);
        Upsert(reference, "Playcount", track.Playcount, null, track.Playcount, track);
        Upsert(reference, "GlobalPopularity", null, ComputePopularity(track.Playcount, track.Listeners), track.Listeners, track);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public static double ComputePopularity(long playcount, long listeners)
    {
        static double Scale(long value, double cap) => Math.Min(1d, Math.Log(1d + Math.Max(0, value)) / Math.Log(1d + cap));
        return Math.Round(100d * ((0.55d * Scale(playcount, 1_000_000_000d)) + (0.45d * Scale(listeners, 100_000_000d))), 4);
    }

    private static void Upsert(MediaExternalReference reference, string key, double? rawValue, double? normalizedScore, long sampleSize, LastFmTrackInfo track)
    {
        var signal = reference.Signals.SingleOrDefault(signal => signal.SignalKey == key);
        if (signal is null)
        {
            signal = new MediaExternalSignal { SignalKey = key };
            reference.Signals.Add(signal);
        }
        signal.RawValue = rawValue;
        signal.NormalizedScore = normalizedScore;
        signal.SampleSize = sampleSize;
        signal.RawMetricsJson = JsonSerializer.Serialize(new { track.Listeners, track.Playcount });
        signal.AlgorithmVersion = AlgorithmVersion;
        signal.SourceUrl = track.Url;
        signal.SourcePayloadHash = track.PayloadHash;
        signal.ObservedAt = DateTime.UtcNow;
        signal.RefreshAfter = DateTime.UtcNow.AddDays(7);
        signal.Status = "observed";
        signal.LastError = null;
    }
}
