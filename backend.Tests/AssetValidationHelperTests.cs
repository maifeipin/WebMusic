using System.Text;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class AssetValidationHelperTests
{
    [Fact]
    public void TryValidateImage_ValidatesJpegMagicBytes()
    {
        var jpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
        var valid = AssetValidationHelper.TryValidateImage(jpegBytes, out var ext, out var mime);
        Assert.True(valid);
        Assert.Equal(".jpg", ext);
        Assert.Equal("image/jpeg", mime);
    }

    [Fact]
    public void TryValidateImage_ValidatesPngMagicBytes()
    {
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        var valid = AssetValidationHelper.TryValidateImage(pngBytes, out var ext, out var mime);
        Assert.True(valid);
        Assert.Equal(".png", ext);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void TryValidateImage_ValidatesWebpMagicBytes()
    {
        var webpBytes = new byte[12];
        webpBytes[0] = (byte)'R'; webpBytes[1] = (byte)'I'; webpBytes[2] = (byte)'F'; webpBytes[3] = (byte)'F';
        webpBytes[4] = 0x24; webpBytes[5] = 0x00; webpBytes[6] = 0x00; webpBytes[7] = 0x00;
        webpBytes[8] = (byte)'W'; webpBytes[9] = (byte)'E'; webpBytes[10] = (byte)'B'; webpBytes[11] = (byte)'P';

        var valid = AssetValidationHelper.TryValidateImage(webpBytes, out var ext, out var mime);
        Assert.True(valid);
        Assert.Equal(".webp", ext);
        Assert.Equal("image/webp", mime);
    }

    [Fact]
    public void TryValidateImage_RejectsInvalidMagicBytes()
    {
        // Random text or HTML pretending to be image
        var htmlBytes = Encoding.UTF8.GetBytes("<html><head></head><body>evil script</body></html>");
        var valid = AssetValidationHelper.TryValidateImage(htmlBytes, out _, out _);
        Assert.False(valid);

        // Windows PE executable header "MZ"
        var exeBytes = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 };
        Assert.False(AssetValidationHelper.TryValidateImage(exeBytes, out _, out _));
    }

    [Fact]
    public void TryValidateImage_RejectsOversizedImage()
    {
        var largeBytes = new byte[5 * 1024 * 1024 + 1];
        largeBytes[0] = 0xFF; largeBytes[1] = 0xD8; largeBytes[2] = 0xFF;
        var valid = AssetValidationHelper.TryValidateImage(largeBytes, out _, out _);
        Assert.False(valid);
    }

    [Fact]
    public void ValidateLyrics_RejectsLyricsExceeding64KB()
    {
        var smallLyrics = "[00:12.34] Hello world";
        Assert.True(AssetValidationHelper.ValidateLyrics(smallLyrics));

        var largeLyrics = new string('a', 65 * 1024);
        Assert.False(AssetValidationHelper.ValidateLyrics(largeLyrics));
    }
}
