using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class MusicEnrichmentServiceTests
{
    [Fact]
    public void ComputeFingerprint_NormalizesCaseAndWhitespace()
    {
        var fp1 = MusicEnrichmentService.ComputeFingerprint("Hotel California", "Eagles", "Hotel California");
        var fp2 = MusicEnrichmentService.ComputeFingerprint("  hotel california  ", " EAGLES ", "HOTEL CALIFORNIA");
        var fp3 = MusicEnrichmentService.ComputeFingerprint("hotel california", "eagles", "hotel california");

        Assert.Equal(fp1, fp2);
        Assert.Equal(fp1, fp3);
        Assert.Equal(16, fp1.Length);
    }

    [Fact]
    public void ComputeFingerprint_NormalizesDiacriticsAndAccents()
    {
        // Beyoncé vs Beyonce
        var fp1 = MusicEnrichmentService.ComputeFingerprint("Halo", "Beyoncé", "I Am... Sasha Fierce");
        var fp2 = MusicEnrichmentService.ComputeFingerprint("Halo", "Beyonce", "I Am... Sasha Fierce");

        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void ComputeFingerprint_ChangesWhenMetadataChanges()
    {
        var fpOriginal = MusicEnrichmentService.ComputeFingerprint("Despacito", "Luis Fonsi", "Vida");
        var fpRemix = MusicEnrichmentService.ComputeFingerprint("Despacito (Remix)", "Luis Fonsi", "Vida");
        var fpDifferentArtist = MusicEnrichmentService.ComputeFingerprint("Despacito", "Daddy Yankee", "Vida");

        Assert.NotEqual(fpOriginal, fpRemix);
        Assert.NotEqual(fpOriginal, fpDifferentArtist);
    }

    [Fact]
    public void ComputeFingerprint_HandlesNullAndEmptyGracefully()
    {
        var fpEmpty = MusicEnrichmentService.ComputeFingerprint(null, null, null);
        Assert.NotNull(fpEmpty);
        Assert.Equal(16, fpEmpty.Length);
    }
}
