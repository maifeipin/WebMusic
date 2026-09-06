using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

/// <summary>
/// Conservative metadata enrichment for a small, explicitly selected set of
/// tracks. It never overwrites existing covers or lyrics and records every
/// outcome for review.
/// </summary>
public enum MusicEnrichmentOutcome
{
    Updated,
    NoChange,
    Failed
}

public class MusicEnrichmentService
{
    private const double MinimumConfidence = 0.90;
    private const long MaxCoverBytes = 5 * 1024 * 1024;
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<MusicEnrichmentService> _logger;

    public MusicEnrichmentService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IWebHostEnvironment environment,
        ILogger<MusicEnrichmentService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _environment = environment;
        _logger = logger;
    }

    public async Task<MusicEnrichmentOutcome> EnrichMissingAssetsAsync(int mediaId, CancellationToken cancellationToken)
    {
        var media = await _context.MediaFiles.FindAsync(new object[] { mediaId }, cancellationToken);
        if (media == null)
        {
            await RecordAsync(mediaId, "MusicBrainz", null, 0, "Skipped", "Media file no longer exists.", cancellationToken);
            return MusicEnrichmentOutcome.NoChange;
        }

        var needsCover = string.IsNullOrWhiteSpace(media.CoverArt);
        var needsLyrics = !await _context.Lyrics.AnyAsync(l => l.MediaFileId == mediaId, cancellationToken);
        if (!needsCover && !needsLyrics)
        {
            await RecordAsync(mediaId, "WebMusic", null, 1, "Skipped", "Cover and lyrics already exist.", cancellationToken);
            return MusicEnrichmentOutcome.NoChange;
        }

        if (IsUnknown(media.Title) || IsUnknown(media.Artist))
        {
            await RecordAsync(mediaId, "MusicBrainz", null, 0, "Skipped", "Title or artist is missing, so no automatic match was attempted.", cancellationToken);
            return MusicEnrichmentOutcome.NoChange;
        }

        try
        {
            var candidate = await FindMusicBrainzCandidateAsync(media, cancellationToken);
            if (candidate == null || candidate.Confidence < MinimumConfidence)
            {
                await RecordAsync(mediaId, "MusicBrainz", candidate?.RecordingId, candidate?.Confidence ?? 0, "Unmatched", "No candidate met the automatic-match threshold.", cancellationToken);
                return MusicEnrichmentOutcome.NoChange;
            }

            var changed = new List<string>();
            if (needsCover && !string.IsNullOrEmpty(candidate.ReleaseId))
            {
                var coverUrl = await DownloadCoverAsync(candidate.ReleaseId, cancellationToken);
                if (coverUrl != null)
                {
                    media.CoverArt = coverUrl;
                    changed.Add("cover");
                }
            }

            if (needsLyrics)
            {
                var lyric = await FindLrclibLyricsAsync(media, cancellationToken);
                if (lyric != null)
                {
                    _context.Lyrics.Add(new Lyric
                    {
                        MediaFileId = media.Id,
                        Content = lyric.Content,
                        Language = "unknown",
                        Source = "LRCLIB",
                        Version = lyric.IsSynchronized ? "synced" : "plain",
                        CreatedAt = DateTime.UtcNow
                    });
                    changed.Add("lyrics");
                }
            }

            await _context.SaveChangesAsync(cancellationToken);
            await RecordAsync(
                mediaId,
                "MusicBrainz+CoverArtArchive+LRCLIB",
                candidate.RecordingId,
                candidate.Confidence,
                changed.Count > 0 ? "Matched" : "MatchedWithoutAssets",
                changed.Count > 0 ? $"Wrote {string.Join(" and ", changed)} without overwriting existing data." : "Recording was matched, but no missing asset was available from the selected providers.",
                cancellationToken);
            return changed.Count > 0 ? MusicEnrichmentOutcome.Updated : MusicEnrichmentOutcome.NoChange;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic enrichment failed for media {MediaId}", mediaId);
            await RecordAsync(mediaId, "MusicBrainz+CoverArtArchive+LRCLIB", null, 0, "Failed", "Provider request failed; existing data was left unchanged.", cancellationToken);
            return MusicEnrichmentOutcome.Failed;
        }
    }

    private async Task<MusicBrainzCandidate?> FindMusicBrainzCandidateAsync(MediaFile media, CancellationToken cancellationToken)
    {
        // MusicBrainz requires clients to stay at or below one request per second.
        var title = EscapeLucenePhrase(media.Title);
        var artist = EscapeLucenePhrase(media.Artist);
        var query = $"recording:\"{title}\" AND artist:\"{artist}\"";
        var url = $"https://musicbrainz.org/ws/2/recording/?fmt=json&limit=5&query={Uri.EscapeDataString(query)}";

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WebMusic", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://music.maifeipin.com)"));
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Task.Delay(attempt == 1 ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromSeconds(20), cancellationToken);
            using var response = await client.GetAsync(url, cancellationToken);
            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                if (attempt == 2) response.EnsureSuccessStatusCode();
                _logger.LogInformation("MusicBrainz returned {StatusCode}; retrying once after a cooldown.", response.StatusCode);
                continue;
            }
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("recordings", out var recordings)) return null;

            MusicBrainzCandidate? best = null;
            foreach (var item in recordings.EnumerateArray())
            {
                var candidateTitle = GetString(item, "title");
                var candidateArtist = item.TryGetProperty("artist-credit", out var credit)
                    ? string.Join(", ", credit.EnumerateArray().Select(entry => GetString(entry, "name")))
                    : string.Empty;
                var candidateDuration = item.TryGetProperty("length", out var length) && length.TryGetInt64(out var milliseconds)
                    ? TimeSpan.FromMilliseconds(milliseconds)
                    : TimeSpan.Zero;
                var confidence = CalculateConfidence(media, candidateTitle, candidateArtist, candidateDuration);
                var releaseId = item.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0
                    ? GetString(releases[0], "id")
                    : null;
                var candidate = new MusicBrainzCandidate(GetString(item, "id"), releaseId, confidence);
                if (best == null || candidate.Confidence > best.Confidence) best = candidate;
            }

            return best;
        }

        return null;
    }

    private async Task<string?> DownloadCoverAsync(string releaseId, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WebMusic", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://music.maifeipin.com)"));
        var imageUrl = $"https://coverartarchive.org/release/{releaseId}/front-250";
        using var response = await client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxCoverBytes) return null;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not "image/jpeg" and not "image/png" and not "image/webp") return null;

        var extension = mediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".jpg"
        };
        var folder = Path.Combine(_environment.ContentRootPath, "data", "covers");
        Directory.CreateDirectory(folder);
        var fileName = $"enriched-{Guid.NewGuid():N}{extension}";
        var temporaryPath = Path.Combine(folder, $".{fileName}.tmp");
        var targetPath = Path.Combine(folder, fileName);

        var tooLarge = false;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxCoverBytes)
                {
                    tooLarge = true;
                    break;
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
        }
        if (tooLarge)
        {
            File.Delete(temporaryPath);
            return null;
        }
        File.Move(temporaryPath, targetPath);
        return $"/api/media/cover/{fileName}";
    }

    private async Task<LrclibLyrics?> FindLrclibLyricsAsync(MediaFile media, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>
        {
            ["track_name"] = media.Title,
            ["artist_name"] = media.Artist,
            ["duration"] = Math.Round(media.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture)
        };
        if (!IsUnknown(media.Album)) parameters["album_name"] = media.Album;
        var query = string.Join("&", parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WebMusic", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://music.maifeipin.com)"));
        using var response = await client.GetAsync($"https://lrclib.net/api/get?{query}", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var root = document.RootElement;
        var synced = GetNullableString(root, "syncedLyrics");
        var plain = GetNullableString(root, "plainLyrics");
        var content = !string.IsNullOrWhiteSpace(synced) ? synced : plain;
        return string.IsNullOrWhiteSpace(content) ? null : new LrclibLyrics(content, !string.IsNullOrWhiteSpace(synced));
    }

    private async Task RecordAsync(int mediaId, string provider, string? externalId, double confidence, string status, string details, CancellationToken cancellationToken)
    {
        _context.MusicEnrichments.Add(new MusicEnrichment
        {
            MediaFileId = mediaId,
            Provider = provider,
            ExternalId = externalId,
            Confidence = Math.Round(confidence, 4),
            Status = status,
            Details = details,
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(cancellationToken);
    }

    private static double CalculateConfidence(MediaFile media, string title, string artist, TimeSpan duration)
    {
        var titleScore = Similarity(media.Title, title);
        var artistScore = Similarity(media.Artist, artist);
        var durationDifference = Math.Abs((media.Duration - duration).TotalSeconds);
        var durationScore = duration == TimeSpan.Zero ? 0.6 : durationDifference switch
        {
            <= 2 => 1,
            <= 5 => 0.95,
            <= 10 => 0.85,
            <= 20 => 0.65,
            _ => 0
        };
        if (titleScore < 0.85 || artistScore < 0.85 || durationDifference > 10) return 0;
        return titleScore * 0.55 + artistScore * 0.35 + durationScore * 0.10;
    }

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.9;

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            previous = current;
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static bool IsUnknown(string? value) => string.IsNullOrWhiteSpace(value) || value.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);
    private static string EscapeLucenePhrase(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string GetString(JsonElement element, string property) => GetNullableString(element, property) ?? string.Empty;
    private static string? GetNullableString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record MusicBrainzCandidate(string RecordingId, string? ReleaseId, double Confidence);
    private sealed record LrclibLyrics(string Content, bool IsSynchronized);
}
