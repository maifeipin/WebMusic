using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public sealed record LocalIdentityEligibilityDecision(
    bool Eligible,
    string Outcome,
    string Reason,
    string InputFingerprint);

/// <summary>
/// C# conservative candidate eligibility policy, fully equivalent to the
/// audited Python rules in scripts/musicbrainz/extract_shadow_run_candidates.py.
/// Preferring false negatives to silently incorrect recording identities.
/// </summary>
public static class LocalIdentityAutoEligibilityPolicy
{
    public const string Version = "LocalAutoEligibility:v2";
    public const double MinimumConfidence = 0.995;
    public const double MaximumDurationDifferenceSeconds = 3.0;

    private static readonly string[] EnKeywords = new[]
    {
        "clean", "explicit", "live", "remix", "mix", "remastered", "remaster",
        "deluxe", "acoustic", "instrumental", "karaoke", "demo", "bonus", "edit",
        "re-recording", "rerecorded", "re-recorded", "version", "ver", "extended",
        "radio", "single", "promo", "ost"
    };

    private static readonly string[] ZhKeywords = new[]
    {
        "演唱会", "现场", "现场版", "音乐会", "重录", "翻唱", "混音", "伴奏", "伴唱",
        "纯音乐", "特别版", "纪念版", "重制", "重置", "原声", "单曲", "加长版", "精选"
    };

    private static readonly Regex VersionRegex = new(
        @"(?i)(?:^|[^a-z0-9])(" + string.Join("|", EnKeywords.Select(Regex.Escape)) + @")(?:[^a-z0-9]|$)|(" + string.Join("|", ZhKeywords.Select(Regex.Escape)) + @")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MojibakeRegex = new(
        @"[ÄÅÆÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûüýÿ¸µ¶·º»¼½¾¿ÀÁÂÃÈÊËÌÍÎÏÐÒÓÔÕØÙÚÛÝÞß]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LocalIdentityEligibilityDecision Evaluate(
        MediaFile media,
        LocalMusicBrainzScanResult scan,
        ISet<string>? alreadySelectedRecordingIds = null)
    {
        var fingerprint = ComputeInputFingerprint(media);

        if (!HasUsableMetadata(media))
        {
            return Reject("Skipped", "Title or artist is empty or unknown.", fingerprint);
        }

        // 1. Mojibake gate (source metadata)
        if (IsMojibake(media.Title) || IsMojibake(media.Album) || IsMojibake(media.Artist))
        {
            return Reject("Skipped", "Source metadata contains mojibake/garbled text.", fingerprint);
        }

        // 2. Source version token gate (raw Title and raw Album)
        if (HasVersionTerm(media.Title, media.Album, null, null, out var sourceVersionWord))
        {
            return Reject("Skipped", $"Source metadata declares a version or derivative token: {sourceVersionWord}.", fingerprint);
        }

        if (!scan.Matched || scan.BestCandidate is null)
        {
            return Reject(scan.StatusCode is >= 200 and < 300 ? "Unmatched" : "Failed",
                scan.ErrorDetail ?? "No local MusicBrainz candidate.", fingerprint);
        }

        var candidate = scan.BestCandidate;
        if (!Guid.TryParse(candidate.RecordingId, out _))
        {
            return Reject("Failed", "Candidate RecordingId is not a valid UUID.", fingerprint);
        }

        if (string.IsNullOrWhiteSpace(candidate.MatchedTitle) || string.IsNullOrWhiteSpace(candidate.MatchedArtist))
        {
            return Reject("Failed", "Candidate MatchedTitle or MatchedArtist is empty.", fingerprint);
        }

        if (candidate.Confidence < MinimumConfidence)
        {
            return Reject("Unmatched", $"Confidence {candidate.Confidence:F4} is below {MinimumConfidence:F3}.", fingerprint);
        }

        // 3. Version & Disambiguation gate (including candidate title & any non-empty disambiguation)
        string candidateVersionWord = candidate.IsDerivativeOrClip ? "derivative_flag" : string.Empty;
        if (candidate.IsDerivativeOrClip || HasVersionTerm(media.Title, media.Album, candidate.MatchedTitle, candidate.Disambiguation, out candidateVersionWord))
        {
            return Reject("Skipped", $"Candidate is flagged as derivative, versioned, or has disambiguation: {candidateVersionWord}.", fingerprint);
        }

        // 4. Artist match gate (full string normalized match, no naive truncation at '&' or ',')
        if (!CheckArtistMatch(media.Artist, candidate.MatchedArtist))
        {
            return Reject("Unmatched", $"Candidate artist '{candidate.MatchedArtist}' is not an exact normalized match for '{media.Artist}'.", fingerprint);
        }

        // 5. Title match gate
        if (!CheckTitleMatch(media.Title, candidate.MatchedTitle))
        {
            return Reject("Unmatched", $"Candidate title '{candidate.MatchedTitle}' is not an exact normalized match for '{media.Title}'.", fingerprint);
        }

        // 6. Duration difference gate
        if (media.Duration <= TimeSpan.Zero || candidate.MatchedDuration <= TimeSpan.Zero)
        {
            return Reject("Unmatched", "Source or candidate duration is missing.", fingerprint);
        }

        var difference = Math.Abs((media.Duration - candidate.MatchedDuration).TotalSeconds);
        if (difference > MaximumDurationDifferenceSeconds)
        {
            return Reject("Unmatched", $"Duration difference {difference:F1}s exceeds {MaximumDurationDifferenceSeconds:F1}s.", fingerprint);
        }

        // 7. Deduplication gate
        if (alreadySelectedRecordingIds?.Contains(candidate.RecordingId) == true)
        {
            return Reject("Skipped", "RecordingId is duplicated within this scan batch.", fingerprint);
        }

        alreadySelectedRecordingIds?.Add(candidate.RecordingId);
        return new LocalIdentityEligibilityDecision(true, "Matched", "Conservative policy accepted candidate.", fingerprint);
    }

    public static string ComputeInputFingerprint(MediaFile media)
    {
        var payload = string.Join("\n", new[]
        {
            NormalizeString(media.Title),
            NormalizeString(media.Artist),
            NormalizeString(media.Album),
            Math.Round(media.Duration.TotalSeconds, 3).ToString("F3", CultureInfo.InvariantCulture),
            media.FileHash?.Trim() ?? string.Empty
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    public static bool HasUsableMetadata(MediaFile media) =>
        !string.IsNullOrWhiteSpace(media.Title) &&
        !string.IsNullOrWhiteSpace(media.Artist) &&
        !media.Title.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) &&
        !media.Artist.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase);

    public static bool ContainsVersionOrDerivativeToken(string? value) =>
        HasVersionTerm(value, null, null, null, out _);

    public static bool HasVersionTerm(string? rawTitle, string? rawAlbum, string? matchedTitle, string? disambiguation, out string matchedWord)
    {
        var combined = $"{rawTitle ?? string.Empty} | {rawAlbum ?? string.Empty} | {matchedTitle ?? string.Empty} | {disambiguation ?? string.Empty}";
        var match = VersionRegex.Match(combined);
        if (match.Success)
        {
            matchedWord = match.Value.Trim();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(disambiguation))
        {
            matchedWord = $"disambig:{disambiguation.Trim()}";
            return true;
        }

        matchedWord = string.Empty;
        return false;
    }

    public static bool IsMojibake(string? value) =>
        !string.IsNullOrEmpty(value) && MojibakeRegex.IsMatch(value);

    public static string NormalizeString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var normalized = s.Trim().ToLowerInvariant();
        normalized = normalized.Replace("’", "'").Replace("‘", "'").Replace("“", "\"").Replace("”", "\"").Replace("…", "...");
        return Regex.Replace(normalized, @"\s+", " ");
    }

    public static bool CheckArtistMatch(string? rawArtist, string? matchedArtist)
    {
        if (string.IsNullOrWhiteSpace(rawArtist) || string.IsNullOrWhiteSpace(matchedArtist)) return false;
        return string.Equals(NormalizeString(rawArtist), NormalizeString(matchedArtist), StringComparison.Ordinal);
    }

    public static bool CheckTitleMatch(string? rawTitle, string? matchedTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle) || string.IsNullOrWhiteSpace(matchedTitle)) return false;
        return string.Equals(NormalizeString(rawTitle), NormalizeString(matchedTitle), StringComparison.Ordinal);
    }

    private static LocalIdentityEligibilityDecision Reject(string outcome, string reason, string fingerprint) =>
        new(false, outcome, reason, fingerprint);
}
