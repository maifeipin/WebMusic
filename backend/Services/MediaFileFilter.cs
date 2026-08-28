namespace WebMusic.Backend.Services;

/// <summary>Central rules for files that must never enter the music library.</summary>
public static class MediaFileFilter
{
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".opus"
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
}
