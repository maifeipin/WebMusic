namespace WebMusic.Backend.Services;

/// <summary>Central rules for supported media files, video formats, and MIME resolution.</summary>
public static class MediaFileFilter
{
    public static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".opus"
    };

    public static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".flv", ".ts", ".m4v"
    };

    public static bool IsIgnoredSystemFile(string? pathOrName)
    {
        var name = Path.GetFileName((pathOrName ?? string.Empty).Replace('\\', '/'));
        return name.StartsWith("._", StringComparison.Ordinal) ||
               name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSupportedAudioFile(string? pathOrName) =>
        !IsIgnoredSystemFile(pathOrName) &&
        SupportedAudioExtensions.Contains(Path.GetExtension(pathOrName ?? string.Empty));

    public static bool IsSupportedVideoFile(string? pathOrName) =>
        !IsIgnoredSystemFile(pathOrName) &&
        SupportedVideoExtensions.Contains(Path.GetExtension(pathOrName ?? string.Empty));

    public static bool IsSupportedMediaFile(string? pathOrName) =>
        IsSupportedAudioFile(pathOrName) || IsSupportedVideoFile(pathOrName);

    public static string GetMediaType(string? pathOrName)
    {
        if (IsSupportedVideoFile(pathOrName)) return "video";
        if (IsSupportedAudioFile(pathOrName)) return "audio";
        return "unknown";
    }

    public static string GetMimeType(string? pathOrName)
    {
        var ext = Path.GetExtension(pathOrName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            // Audio
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".opus" => "audio/opus",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",

            // Video
            ".mp4" => "video/mp4",
            ".m4v" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".flv" => "video/x-flv",
            ".ts" => "video/mp2t",

            // Fallback
            _ => "application/octet-stream"
        };
    }
}
