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
    Unmatched,
    Skipped,
    Failed,
    NoChange
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

    public async Task<MusicEnrichmentOutcome> EnrichMissingAssetsAsync(int mediaId, CancellationToken cancellationToken, string? jobId = null)
    {
        var media = await _context.MediaFiles.FindAsync(new object[] { mediaId }, cancellationToken);
        if (media == null)
        {
            await RecordAsync(mediaId, "MusicBrainz", null, 0, "Skipped", "Media file no longer exists.", cancellationToken, jobId);
            return MusicEnrichmentOutcome.Skipped;
        }

        var needsCover = string.IsNullOrWhiteSpace(media.CoverArt);
        var needsLyrics = !await _context.Lyrics.AnyAsync(l => l.MediaFileId == mediaId, cancellationToken);
        if (!needsCover && !needsLyrics)
        {
            await RecordAsync(mediaId, "WebMusic", null, 1, "Skipped", "Cover and lyrics already exist.", cancellationToken, jobId);
            return MusicEnrichmentOutcome.Skipped;
        }

        if (IsUnknown(media.Title) || IsUnknown(media.Artist))
        {
            await RecordAsync(mediaId, "MusicBrainz", null, 0, "Skipped", "Title or artist is missing, so no automatic match was attempted.", cancellationToken, jobId);
            return MusicEnrichmentOutcome.Skipped;
        }

        var currentFingerprint = ComputeFingerprint(media.Title, media.Artist, media.Album);

        // Check cooldown: bypass if metadata fingerprint has changed
        var lastAttempt = await _context.EnrichmentAttempts
            .Where(a => a.MediaFileId == mediaId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastAttempt?.RetryAfter.HasValue == true && lastAttempt.RetryAfter.Value > DateTime.UtcNow)
        {
            if (lastAttempt.InputFingerprint == currentFingerprint)
            {
                await RecordAsync(mediaId, "MusicBrainz", null, 0, "Skipped",
                    $"Under cooldown until {lastAttempt.RetryAfter.Value:yyyy-MM-dd HH:mm:ss} UTC (outcome: {lastAttempt.Outcome}).",
                    cancellationToken, jobId, 200, 0, currentFingerprint, lastAttempt.RetryAfter);
                return MusicEnrichmentOutcome.Skipped;
            }
            _logger.LogInformation("Media {MediaId} metadata changed from fingerprint {Old} to {New}; bypassing cooldown.",
                mediaId, lastAttempt.InputFingerprint, currentFingerprint);
        }

        int httpStatusCode = 200;
        int retryCount = 0;

        try
        {
            var mbResult = await FindMusicBrainzCandidateAsync(media, cancellationToken);
            httpStatusCode = mbResult.StatusCode;
            retryCount = mbResult.RetryCount;

            if (mbResult.StatusCode != 200)
            {
                var retryAfter = DateTime.UtcNow.AddHours(6);
                await RecordAsync(mediaId, "MusicBrainz", null, 0, "Failed", mbResult.ErrorDetail ?? $"HTTP {mbResult.StatusCode}", cancellationToken, jobId, mbResult.StatusCode, retryCount, currentFingerprint, retryAfter);
                return MusicEnrichmentOutcome.Failed;
            }

            var candidate = mbResult.Candidate;
            if (candidate == null || candidate.Confidence < MinimumConfidence)
            {
                var retryAfter = DateTime.UtcNow.AddDays(30);
                await RecordAsync(mediaId, "MusicBrainz", candidate?.RecordingId, candidate?.Confidence ?? 0, "Unmatched", "No candidate met the automatic-match threshold.", cancellationToken, jobId, 200, retryCount, currentFingerprint, retryAfter);
                return MusicEnrichmentOutcome.Unmatched;
            }

            string coverStatus = needsCover ? "Pending" : "Skipped";
            string lyricsStatus = needsLyrics ? "Pending" : "Skipped";

            var changed = new List<string>();
            if (needsCover && !string.IsNullOrEmpty(candidate.ReleaseId))
            {
                var coverUrl = await DownloadCoverAsync(candidate.ReleaseId, cancellationToken);
                if (coverUrl != null)
                {
                    media.CoverArt = coverUrl;
                    changed.Add("cover");
                    coverStatus = "Matched";
                }
                else
                {
                    coverStatus = "NoAsset";
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
                    lyricsStatus = "Matched";
                }
                else
                {
                    lyricsStatus = "NoAsset";
                }
            }

            // Record MediaIdentity if matched with high confidence
            if (!string.IsNullOrEmpty(candidate.RecordingId))
            {
                var existingIdentity = await _context.MediaIdentities
                    .FirstOrDefaultAsync(i => i.MediaFileId == media.Id && i.Provider == "MusicBrainz", cancellationToken);
                if (existingIdentity == null)
                {
                    _context.MediaIdentities.Add(new MediaIdentity
                    {
                        MediaFileId = media.Id,
                        Provider = "MusicBrainz",
                        RecordingId = candidate.RecordingId,
                        ReleaseId = candidate.ReleaseId,
                        MatchMethod = "MetadataFuzzy",
                        Confidence = Math.Round(candidate.Confidence, 4),
                        Status = "approved",
                        CoverStatus = coverStatus,
                        LyricsStatus = lyricsStatus,
                        MatchedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    existingIdentity.RecordingId = candidate.RecordingId;
                    existingIdentity.ReleaseId = candidate.ReleaseId;
                    existingIdentity.Confidence = Math.Round(candidate.Confidence, 4);
                    existingIdentity.CoverStatus = coverStatus;
                    existingIdentity.LyricsStatus = lyricsStatus;
                    existingIdentity.LastVerifiedAt = DateTime.UtcNow;
                }
            }

            await _context.SaveChangesAsync(cancellationToken);

            var finalOutcome = changed.Count > 0 ? "Matched" : "MatchedWithoutAssets";
            DateTime? finalRetryAfter = changed.Count > 0 ? null : DateTime.UtcNow.AddDays(14);

            await RecordAsync(
                mediaId,
                "MusicBrainz+CoverArtArchive+LRCLIB",
                candidate.RecordingId,
                candidate.Confidence,
                finalOutcome,
                changed.Count > 0 ? $"Wrote {string.Join(" and ", changed)} without overwriting existing data." : "Recording was matched, but no missing asset was available from the selected providers.",
                cancellationToken,
                jobId,
                200,
                retryCount,
                currentFingerprint,
                finalRetryAfter);
            return changed.Count > 0 ? MusicEnrichmentOutcome.Updated : MusicEnrichmentOutcome.Unmatched;
        }
        catch (HttpRequestException ex)
        {
            var status = (int?)ex.StatusCode ?? 500;
            _logger.LogWarning(ex, "Automatic enrichment HTTP error for media {MediaId}", mediaId);
            await RecordAsync(mediaId, "MusicBrainz+CoverArtArchive+LRCLIB", null, 0, "Failed", $"HTTP {status}: {ex.Message}", cancellationToken, jobId, status, retryCount, currentFingerprint, DateTime.UtcNow.AddHours(6));
            return MusicEnrichmentOutcome.Failed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Automatic enrichment failed for media {MediaId}", mediaId);
            await RecordAsync(mediaId, "MusicBrainz+CoverArtArchive+LRCLIB", null, 0, "Failed", "Provider request failed; existing data was left unchanged.", cancellationToken, jobId, httpStatusCode != 200 ? httpStatusCode : 500, retryCount, currentFingerprint, DateTime.UtcNow.AddHours(6));
            return MusicEnrichmentOutcome.Failed;
        }
    }

    private async Task<MusicBrainzResult> FindMusicBrainzCandidateAsync(MediaFile media, CancellationToken cancellationToken)
    {
        var title = EscapeLucenePhrase(media.Title);
        var artist = EscapeLucenePhrase(media.Artist);
        var query = $"recording:\"{title}\" AND artist:\"{artist}\"";
        var url = $"https://musicbrainz.org/ws/2/recording/?fmt=json&limit=5&query={Uri.EscapeDataString(query)}";

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WebMusic", "1.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(https://music.maifeipin.com)"));

        int lastStatusCode = 200;
        int retryCount = 0;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Task.Delay(attempt == 1 ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromSeconds(20), cancellationToken);
            using var response = await client.GetAsync(url, cancellationToken);
            lastStatusCode = (int)response.StatusCode;

            if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                retryCount = 1;
                _logger.LogInformation("MusicBrainz returned {StatusCode}; retrying once after a cooldown.", response.StatusCode);
                if (attempt == 2)
                {
                    return new MusicBrainzResult(null, lastStatusCode, retryCount, $"MusicBrainz returned {response.StatusCode} after retry cooldown.");
                }
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return new MusicBrainzResult(null, lastStatusCode, retryCount, $"MusicBrainz request failed with HTTP {lastStatusCode}.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("recordings", out var recordings))
            {
                return new MusicBrainzResult(null, 200, retryCount, "No recordings field in response payload.");
            }

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

            return new MusicBrainzResult(best, 200, retryCount);
        }

        return new MusicBrainzResult(null, lastStatusCode, retryCount, "Max retry attempts reached.");
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

    public static string ComputeFingerprint(string? title, string? artist, string? album)
    {
        var normalizedTitle = Normalize(title ?? string.Empty);
        var normalizedArtist = Normalize(artist ?? string.Empty);
        var normalizedAlbum = Normalize(album ?? string.Empty);
        var raw = $"{normalizedTitle}|{normalizedArtist}|{normalizedAlbum}";
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private async Task RecordAsync(
        int mediaId,
        string provider,
        string? externalId,
        double confidence,
        string status,
        string details,
        CancellationToken cancellationToken,
        string? jobId = null,
        int? httpStatus = null,
        int retryCount = 0,
        string? inputFingerprint = null,
        DateTime? retryAfter = null)
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

        if (!string.IsNullOrEmpty(jobId))
        {
            _context.EnrichmentAttempts.Add(new EnrichmentAttempt
            {
                JobId = jobId,
                MediaFileId = mediaId,
                Provider = provider,
                RequestKey = externalId,
                InputFingerprint = inputFingerprint,
                HTTPStatus = httpStatus ?? (status == "Failed" ? 500 : 200),
                Outcome = status,
                Confidence = Math.Round(confidence, 4),
                RetryCount = retryCount,
                Detail = details,
                RetryAfter = retryAfter,
                CreatedAt = DateTime.UtcNow
            });
        }

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
    private sealed record MusicBrainzResult(MusicBrainzCandidate? Candidate, int StatusCode, int RetryCount, string? ErrorDetail = null);
    private sealed record LrclibLyrics(string Content, bool IsSynchronized);
}
