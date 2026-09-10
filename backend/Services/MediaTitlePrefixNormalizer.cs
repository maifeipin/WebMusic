using System;
using System.IO;
using System.Text.RegularExpressions;

namespace WebMusic.Backend.Services;

public record TitlePrefixNormalizationResult(
    bool IsAdmitted,
    string OldTitle,
    string NewTitle,
    string Rule,
    double Confidence,
    string? RejectionReason = null,
    bool RequiresManualReview = false
);

public static class MediaTitlePrefixNormalizer
{
    // Rule 1: Explicit Track keyword prefix (e.g., "Track 01 - Song", "Track01. Song", "Track 01 Song")
    private static readonly Regex TrackKeywordRegex = new(
        @"^(?:track\s*)(\d{1,3})[\s\.\-_、—–\t]+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Rule 3: Bracketed numbers (e.g., "[01] Song", "(01) Song", "【01】Song")
    private static readonly Regex BracketedNumberRegex = new(
        @"^[\[\(（【]\s*(\d{1,3})\s*[\]\)）】][\s\.\-_、—–\t]*(.+)$",
        RegexOptions.Compiled);

    // Rule 4: Full book title brackets wrapping the title (e.g., "《平湖秋月》")
    private static readonly Regex FullBookBracketsRegex = new(
        @"^《\s*(.+?)\s*》$",
        RegexOptions.Compiled);

    // Pure digits title
    private static readonly Regex PureDigitsRegex = new(
        @"^\d+$",
        RegexOptions.Compiled);

    // Multiple spaces following digits (e.g., "3     F调旋律") -> Manual Review Candidate only
    private static readonly Regex MultipleSpacesRegex = new(
        @"^(\d{1,3})\s{2,}(.+)$",
        RegexOptions.Compiled);

    // Single space following 1-3 digits (e.g., "7 Years", "21 Guns") -> Forbidden
    private static readonly Regex SingleSpaceNumberRegex = new(
        @"^(\d{1,3})\s[^\s].*$",
        RegexOptions.Compiled);

    // Rule 2: Explicit delimiters following 1-3 digits (e.g., "01. Song", "01 - Song", "1、Song", "2、《Song》")
    private static readonly Regex ExplicitDelimiterRegex = new(
        @"^(\d{1,3})\s*([\.\-_、—–\:]|、《)\s*(.+)$",
        RegexOptions.Compiled);

    public static TitlePrefixNormalizationResult Normalize(string? title, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return new TitlePrefixNormalizationResult(
                IsAdmitted: false,
                OldTitle: title ?? string.Empty,
                NewTitle: title ?? string.Empty,
                Rule: "None",
                Confidence: 0.0,
                RejectionReason: "Title is empty or whitespace.");
        }

        var originalTitle = title;
        var trimmed = title.Trim();

        // 1. Full wrap book title brackets: 《歌名》 -> 歌名
        var bookMatch = FullBookBracketsRegex.Match(trimmed);
        if (bookMatch.Success)
        {
            var inner = bookMatch.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(inner) || PureDigitsRegex.IsMatch(inner))
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "FullBookTitleBrackets",
                    Confidence: 0.0,
                    RejectionReason: "Inner content of book brackets is empty or pure digits.");
            }
            return new TitlePrefixNormalizationResult(
                IsAdmitted: true,
                OldTitle: originalTitle,
                NewTitle: inner,
                Rule: "FullBookTitleBrackets",
                Confidence: 1.0);
        }

        // 2. Pure digits title fallback to filename
        if (PureDigitsRegex.IsMatch(trimmed))
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "PureNumberFallbackToFilename",
                    Confidence: 0.0,
                    RejectionReason: "No file path provided to recover title from filename.");
            }

            var fnWithoutExt = Path.GetFileNameWithoutExtension(filePath).Trim();
            if (string.IsNullOrWhiteSpace(fnWithoutExt) || PureDigitsRegex.IsMatch(fnWithoutExt))
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "PureNumberFallbackToFilename",
                    Confidence: 0.0,
                    RejectionReason: "Filename without extension is empty or pure digits.");
            }

            // Recursively normalize filename in case it has its own prefix, e.g. "01. 往事.mp3"
            var fnNormalized = Normalize(fnWithoutExt);
            var candidateTitle = fnNormalized.IsAdmitted ? fnNormalized.NewTitle : fnWithoutExt;

            if (PureDigitsRegex.IsMatch(candidateTitle) || candidateTitle.Length < 1)
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "PureNumberFallbackToFilename",
                    Confidence: 0.0,
                    RejectionReason: "Resolved title from filename is still invalid.");
            }

            return new TitlePrefixNormalizationResult(
                IsAdmitted: true,
                OldTitle: originalTitle,
                NewTitle: candidateTitle,
                Rule: "PureNumberFallbackToFilename",
                Confidence: 0.9);
        }

        // 3. Explicit Track keyword: e.g. "Track 01 - Song", "Track01. Song", "Track 01 Song"
        var trackMatch = TrackKeywordRegex.Match(trimmed);
        if (trackMatch.Success)
        {
            var rawRest = trackMatch.Groups[2].Value;
            var trimmedRaw = rawRest.TrimStart();
            if (trimmedRaw.Length > 0 && char.IsDigit(trimmedRaw[0]))
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "ExplicitTrackKeyword",
                    Confidence: 0.0,
                    RejectionReason: "Track keyword delimiter is followed immediately by digits.");
            }

            var rest = CleanPostNumberText(rawRest);
            var valid = ValidateCandidate(trimmed, rest);
            if (!valid.IsValid)
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "ExplicitTrackKeyword",
                    Confidence: 0.0,
                    RejectionReason: valid.Reason);
            }

            return new TitlePrefixNormalizationResult(
                IsAdmitted: true,
                OldTitle: originalTitle,
                NewTitle: rest,
                Rule: "ExplicitTrackKeyword",
                Confidence: 1.0);
        }

        // 4. Bracketed track number: e.g. "[01] Song", "(01) Song", "【01】Song"
        var bracketMatch = BracketedNumberRegex.Match(trimmed);
        if (bracketMatch.Success)
        {
            var rawRest = bracketMatch.Groups[2].Value;
            var trimmedRest = rawRest.TrimStart();
            if (trimmedRest.Length > 0 && char.IsDigit(trimmedRest[0]))
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "BracketedTrackNumber",
                    Confidence: 0.0,
                    RejectionReason: "Content following bracketed number immediately starts with a digit.",
                    RequiresManualReview: true);
            }

            var rest = CleanPostNumberText(rawRest);
            var valid = ValidateCandidate(trimmed, rest);
            if (!valid.IsValid)
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "BracketedTrackNumber",
                    Confidence: 0.0,
                    RejectionReason: valid.Reason);
            }

            return new TitlePrefixNormalizationResult(
                IsAdmitted: true,
                OldTitle: originalTitle,
                NewTitle: rest,
                Rule: "BracketedTrackNumber",
                Confidence: 1.0);
        }

        // 5. Multiple spaces candidate (e.g., "3     F调旋律") -> Manual Review ONLY
        var multiSpaceMatch = MultipleSpacesRegex.Match(trimmed);
        if (multiSpaceMatch.Success)
        {
            var rest = CleanPostNumberText(multiSpaceMatch.Groups[2].Value);
            return new TitlePrefixNormalizationResult(
                IsAdmitted: false,
                OldTitle: originalTitle,
                NewTitle: rest,
                Rule: "MultipleSpacesCandidate",
                Confidence: 0.7,
                RejectionReason: "Multiple spaces following number requires manual verification.",
                RequiresManualReview: true);
        }

        // 6. Explicit delimiter prefix: e.g. "01. Song", "01 - Song", "1、Song", "2、《Song》"
        var delimMatch = ExplicitDelimiterRegex.Match(trimmed);
        if (delimMatch.Success)
        {
            var delim = delimMatch.Groups[2].Value;
            var rawRest = delimMatch.Groups[3].Value;

            var trimmedRaw = rawRest.TrimStart();
            // Anti-false-positive: For decimal/hyphen-like delimiters (e.g., '.', '-'), do not allow trailing digits (e.g. 99.9, 1-800)
            if (delim != "、" && delim != "、《" && trimmedRaw.Length > 0 && char.IsDigit(trimmedRaw[0]))
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "ExplicitDelimiterIndex",
                    Confidence: 0.0,
                    RejectionReason: "Delimiter is followed immediately by digits (e.g., 99.9, 1-800).");
            }

            var rest = CleanPostNumberText(rawRest);
            if (delim == "、《" && rest.EndsWith("》"))
            {
                rest = rest[..^1].Trim();
            }

            var valid = ValidateCandidate(trimmed, rest);
            if (!valid.IsValid)
            {
                return new TitlePrefixNormalizationResult(
                    IsAdmitted: false,
                    OldTitle: originalTitle,
                    NewTitle: trimmed,
                    Rule: "ExplicitDelimiterIndex",
                    Confidence: 0.0,
                    RejectionReason: valid.Reason);
            }

            return new TitlePrefixNormalizationResult(
                IsAdmitted: true,
                OldTitle: originalTitle,
                NewTitle: rest,
                Rule: "ExplicitDelimiterIndex",
                Confidence: 1.0);
        }

        // 7. Single space after number (e.g. "7 Years", "21 Guns", "99 Luftballons") -> STRICTLY FORBIDDEN
        if (SingleSpaceNumberRegex.IsMatch(trimmed))
        {
            return new TitlePrefixNormalizationResult(
                IsAdmitted: false,
                OldTitle: originalTitle,
                NewTitle: trimmed,
                Rule: "SingleSpaceAfterNumber",
                Confidence: 0.0,
                RejectionReason: "Single space after number is strictly forbidden from auto-stripping (e.g. '7 Years', '21 Guns').");
        }

        // No admitted prefix found
        return new TitlePrefixNormalizationResult(
            IsAdmitted: false,
            OldTitle: originalTitle,
            NewTitle: trimmed,
            Rule: "None",
            Confidence: 0.0,
            RejectionReason: "No eligible title prefix pattern matched.");
    }

    private static string CleanPostNumberText(string text)
    {
        var clean = text.Trim();
        // If wrapped in book brackets, unwrap: 《平湖秋月》 -> 平湖秋月
        var bookMatch = FullBookBracketsRegex.Match(clean);
        if (bookMatch.Success)
        {
            clean = bookMatch.Groups[1].Value.Trim();
        }
        return clean;
    }

    private static (bool IsValid, string? Reason) ValidateCandidate(string oldTitle, string newTitle)
    {
        if (string.IsNullOrWhiteSpace(newTitle))
            return (false, "Cleaned title is empty or whitespace.");

        if (PureDigitsRegex.IsMatch(newTitle))
            return (false, "Cleaned title is still pure digits.");

        if (string.Equals(oldTitle, newTitle, StringComparison.Ordinal))
            return (false, "Cleaned title is identical to original title.");

        return (true, null);
    }
}
