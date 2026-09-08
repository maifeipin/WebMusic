using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public record LocalMusicBrainzCandidate(
    string RecordingId,
    string? ReleaseId,
    string? ArtistId,
    string MatchedTitle,
    string MatchedArtist,
    TimeSpan MatchedDuration,
    string? Disambiguation,
    double Confidence,
    bool IsDerivativeOrClip
);

public record LocalMusicBrainzScanResult(
    bool Matched,
    LocalMusicBrainzCandidate? BestCandidate,
    int StatusCode,
    string? ErrorDetail = null,
    long ElapsedMs = 0
);

public interface ILocalMusicBrainzService
{
    string BaseUrl { get; }
    Task<LocalMusicBrainzScanResult> SearchRecordingAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken = default
    );

    Task<LocalMusicBrainzScanResult> ScanMediaIdentityAsync(
        MediaFile media,
        CancellationToken cancellationToken = default
    );
}

public class LocalMusicBrainzService : ILocalMusicBrainzService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<LocalMusicBrainzService> _logger;
    private readonly string _baseUrl;

    public const string ProviderName = "MusicBrainzLocal";
    public const string MatchMethodV1 = "LocalMetadataFuzzy:v1";
    public const double HighConfidenceThreshold = 0.85;
    public const double ProposedConfidenceThreshold = 0.70;

    public string BaseUrl => _baseUrl;

    public LocalMusicBrainzService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<LocalMusicBrainzService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        
        // DSM private network local endpoint (e.g., http://192.168.2.18:5050)
        _baseUrl = (configuration["MusicBrainz:LocalBaseUrl"] ?? "http://192.168.2.18:5050").TrimEnd('/');
        ValidatePrivateOrLoopbackEndpoint(_baseUrl);
    }

    public static void ValidatePrivateOrLoopbackEndpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("MusicBrainz:LocalBaseUrl must not be empty.", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid URL format for MusicBrainz:LocalBaseUrl: '{url}'", nameof(url));
        }

        var host = uri.Host.Trim().ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            host = host.Substring(1, host.Length - 2);
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            throw new InvalidOperationException(
                $"Hostnames ('{uri.Host}') are not permitted for MusicBrainz:LocalBaseUrl to eliminate DNS rebinding risks. " +
                "Only explicit private or loopback IP literals (e.g. 192.168.2.18, 127.0.0.1, 100.91.3.53, ::1) are allowed."
            );
        }

        if (!IsPrivateOrLoopbackIp(ip))
        {
            throw new InvalidOperationException(
                $"MusicBrainz:LocalBaseUrl '{url}' resolves to public or unauthorized IP '{ip}'. Only private (RFC1918), loopback (127.0.0.0/8, ::1), or Tailscale (100.64.0.0/10) addresses are allowed to prevent outbound public API requests."
            );
        }
    }

    public static bool IsPrivateOrLoopbackIp(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // RFC 1918: 10.0.0.0/8
            if (bytes[0] == 10) return true;
            // RFC 1918: 172.16.0.0/12 (172.16.0.0 - 172.31.255.255)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // RFC 1918: 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // Tailscale / CGNAT: 100.64.0.0/10 (100.64.0.0 - 100.127.255.255)
            if (bytes[0] == 100 && (bytes[1] & 0xC0) == 64) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || System.Net.IPAddress.IsLoopback(ip)) return true;
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true; // ULA fc00::/7
        }

        return false;
    }

    public async Task<LocalMusicBrainzScanResult> ScanMediaIdentityAsync(
        MediaFile media,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(media.Title) || string.IsNullOrWhiteSpace(media.Artist) ||
            media.Title.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ||
            media.Artist.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return new LocalMusicBrainzScanResult(false, null, 200, "Incomplete or unknown title/artist metadata.");
        }

        return await SearchRecordingAsync(media.Title, media.Artist, media.Duration, cancellationToken);
    }

    public async Task<LocalMusicBrainzScanResult> SearchRecordingAsync(
        string title,
        string artist,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var escapedTitle = EscapeLucenePhrase(title.Trim());
        var escapedArtist = EscapeLucenePhrase(artist.Trim());
        var query = $"recording:\"{escapedTitle}\" AND artist:\"{escapedArtist}\"";
        var requestUrl = $"{_baseUrl}/ws/2/recording/?fmt=json&limit=5&query={Uri.EscapeDataString(query)}";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.UserAgent.Clear();
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WebMusic-LocalScanner", "2.0"));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            var statusCode = (int)response.StatusCode;

            if (statusCode >= 300 && statusCode < 400)
            {
                sw.Stop();
                var redirectLocation = response.Headers.Location?.ToString() ?? "unknown";
                _logger.LogWarning("Local MusicBrainz node returned unexpected redirect HTTP {StatusCode} to {Location}. Redirects are strictly prohibited.", statusCode, redirectLocation);
                return new LocalMusicBrainzScanResult(
                    false,
                    null,
                    statusCode,
                    $"Local MusicBrainz node returned redirect HTTP {statusCode} to {redirectLocation}. Redirects are strictly prohibited to prevent public internet access.",
                    sw.ElapsedMilliseconds
                );
            }

            if (!response.IsSuccessStatusCode)
            {
                sw.Stop();
                return new LocalMusicBrainzScanResult(
                    false,
                    null,
                    statusCode,
                    $"Local MusicBrainz node returned HTTP {statusCode}",
                    sw.ElapsedMilliseconds
                );
            }

            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = JsonDocument.Parse(stream);

            if (!document.RootElement.TryGetProperty("recordings", out var recordings) || recordings.GetArrayLength() == 0)
            {
                sw.Stop();
                return new LocalMusicBrainzScanResult(false, null, 200, "No recordings found.", sw.ElapsedMilliseconds);
            }

            LocalMusicBrainzCandidate? best = null;

            foreach (var item in recordings.EnumerateArray())
            {
                var candidateId = GetString(item, "id");
                var candidateTitle = GetString(item, "title");
                var disambiguation = GetNullableString(item, "disambiguation");

                // Extract artist
                var candidateArtist = item.TryGetProperty("artist-credit", out var credit)
                    ? string.Join(", ", credit.EnumerateArray().Select(c => GetString(c, "name")))
                    : string.Empty;

                string? artistId = null;
                if (item.TryGetProperty("artist-credit", out var creditArr) && creditArr.GetArrayLength() > 0)
                {
                    var firstArtist = creditArr[0];
                    if (firstArtist.TryGetProperty("artist", out var artistObj))
                    {
                        artistId = GetNullableString(artistObj, "id");
                    }
                }

                // Extract duration
                var candidateDuration = item.TryGetProperty("length", out var length) && length.TryGetInt64(out var ms)
                    ? TimeSpan.FromMilliseconds(ms)
                    : TimeSpan.Zero;

                // Extract release id
                string? releaseId = null;
                if (item.TryGetProperty("releases", out var releases) && releases.GetArrayLength() > 0)
                {
                    releaseId = GetNullableString(releases[0], "id");
                }

                // Detect if clip or derivative
                var isDerivative = IsDerivativeOrClip(title, candidateTitle, disambiguation, duration);

                var confidence = CalculateConfidence(title, artist, duration, candidateTitle, candidateArtist, candidateDuration, isDerivative);

                var candidate = new LocalMusicBrainzCandidate(
                    candidateId,
                    releaseId,
                    artistId,
                    candidateTitle,
                    candidateArtist,
                    candidateDuration,
                    disambiguation,
                    confidence,
                    isDerivative
                );

                if (best == null || candidate.Confidence > best.Confidence)
                {
                    best = candidate;
                }
            }

            sw.Stop();
            var matched = best != null && best.Confidence >= ProposedConfidenceThreshold;
            return new LocalMusicBrainzScanResult(matched, best, 200, null, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning("Local MusicBrainz request timed out after 5s for query {Query}", query);
            return new LocalMusicBrainzScanResult(false, null, 408, "Local MusicBrainz request timed out after 5s", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Local MusicBrainz request failed for query {Query}", query);
            return new LocalMusicBrainzScanResult(false, null, 500, ex.Message, sw.ElapsedMilliseconds);
        }
    }

    public static double CalculateConfidence(
        string targetTitle,
        string targetArtist,
        TimeSpan targetDuration,
        string candidateTitle,
        string candidateArtist,
        TimeSpan candidateDuration,
        bool isDerivative)
    {
        var titleScore = Similarity(targetTitle, candidateTitle);
        var artistScore = Similarity(targetArtist, candidateArtist);

        var durationDiff = targetDuration > TimeSpan.Zero && candidateDuration > TimeSpan.Zero
            ? Math.Abs((targetDuration - candidateDuration).TotalSeconds)
            : 0;

        var durationScore = (targetDuration == TimeSpan.Zero || candidateDuration == TimeSpan.Zero)
            ? 0.70
            : durationDiff switch
            {
                <= 2 => 1.0,
                <= 5 => 0.95,
                <= 10 => 0.85,
                <= 20 => 0.65,
                <= 35 => 0.40,
                _ => 0.10
            };

        // Strict thresholds for high quality match
        if (titleScore < 0.80 || artistScore < 0.75) return 0.0;
        if (targetDuration > TimeSpan.Zero && candidateDuration > TimeSpan.Zero && durationDiff > 35) return 0.0;

        var baseScore = titleScore * 0.55 + artistScore * 0.35 + durationScore * 0.10;

        if (isDerivative)
        {
            // Penalize unintended derivative matches (e.g., target is standard song, but MB returned live/remix)
            baseScore *= 0.80;
        }

        return Math.Round(Math.Clamp(baseScore, 0.0, 1.0), 4);
    }

    public static bool IsDerivativeOrClip(
        string targetTitle,
        string candidateTitle,
        string? disambiguation,
        TimeSpan duration)
    {
        var targetNorm = Normalize(targetTitle);
        var candNorm = Normalize(candidateTitle);
        var disNorm = Normalize(disambiguation ?? string.Empty);

        string[] derivativeKeywords = { "live", "remix", "karaoke", "instrumental", "acoustic", "demo", "edit", "clip", "preview", "ringtone", "short" };

        bool targetHas = derivativeKeywords.Any(k => targetNorm.Contains(k, StringComparison.OrdinalIgnoreCase));
        bool candHas = derivativeKeywords.Any(k => candNorm.Contains(k, StringComparison.OrdinalIgnoreCase) || disNorm.Contains(k, StringComparison.OrdinalIgnoreCase));

        // Mismatch: candidate is derivative/live but target was original release
        if (!targetHas && candHas) return true;

        // Clip by duration (under 45 seconds)
        if (duration > TimeSpan.Zero && duration.TotalSeconds < 45) return true;

        return false;
    }

    public static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.92;

        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)
                );
            }
            previous = current;
        }

        return Math.Max(0.0, 1.0 - (double)previous[b.Length] / Math.Max(a.Length, b.Length));
    }

    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    public static string EscapeLucenePhrase(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string GetString(JsonElement element, string property) =>
        GetNullableString(element, property) ?? string.Empty;

    private static string? GetNullableString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
