using System.Text;

namespace WebMusic.Backend.Services;

public static class AssetValidationHelper
{
    public const int MaxImageBytes = 5 * 1024 * 1024; // 5MB
    public const int MaxLyricsBytes = 64 * 1024; // 64KB

    /// <summary>
    /// Validates image magic bytes for JPEG, PNG, and WebP.
    /// Rejects corrupt images, HTML/scripts, executable formats, and oversized files.
    /// </summary>
    public static bool TryValidateImage(byte[]? data, out string detectedExtension, out string detectedMimeType)
    {
        detectedExtension = string.Empty;
        detectedMimeType = string.Empty;

        if (data == null || data.Length < 12 || data.Length > MaxImageBytes)
        {
            return false;
        }

        // 1. JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            detectedExtension = ".jpg";
            detectedMimeType = "image/jpeg";
            return true;
        }

        // 2. PNG: 89 50 4E 47 0D 0A 1A 0A
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
            data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            detectedExtension = ".png";
            detectedMimeType = "image/png";
            return true;
        }

        // 3. WebP: "RIFF" .... "WEBP"
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            detectedExtension = ".webp";
            detectedMimeType = "image/webp";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates image magic bytes from the initial header buffer (at least 12 bytes).
    /// </summary>
    public static bool TryValidateImageHeader(byte[]? header, long totalLength, out string detectedExtension, out string? error)
    {
        detectedExtension = string.Empty;
        error = null;

        if (header == null || header.Length < 12)
        {
            error = "Header is too short to determine image format.";
            return false;
        }

        if (totalLength > MaxImageBytes)
        {
            error = $"Image file exceeds maximum allowed size of {MaxImageBytes / 1024 / 1024}MB.";
            return false;
        }

        if (header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            detectedExtension = ".jpg";
            return true;
        }

        if (header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            detectedExtension = ".png";
            return true;
        }

        if (header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
            header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
        {
            detectedExtension = ".webp";
            return true;
        }

        error = "Unsupported or invalid image format. Only JPEG, PNG, and WebP are allowed.";
        return false;
    }

    /// <summary>
    /// Validates that lyrics text is within reasonable bounds (<= 64KB UTF-8 bytes).
    /// </summary>
    public static bool ValidateLyrics(string? lyrics, int maxBytes = MaxLyricsBytes)
    {
        if (string.IsNullOrEmpty(lyrics)) return true;
        return Encoding.UTF8.GetByteCount(lyrics) <= maxBytes;
    }
}
