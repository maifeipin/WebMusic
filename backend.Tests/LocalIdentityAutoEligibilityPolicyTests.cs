using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class LocalIdentityAutoEligibilityPolicyTests
{
    private static readonly MediaFile Media = new() { Title = "Hello", Artist = "Adele", Album = "25", Duration = TimeSpan.FromSeconds(295) };

    private static LocalMusicBrainzScanResult Result(string title = "Hello", string artist = "Adele", double confidence = 1, double seconds = 295, string? disambiguation = null) => new(
        true, new LocalMusicBrainzCandidate("b0c34a5c-523a-4d58-b5ec-8d6ed95596cc", null, null, title, artist, TimeSpan.FromSeconds(seconds), disambiguation, confidence, false), 200);

    [Fact]
    public void Accepts_ExactHighConfidenceCandidate_WithinDurationGate()
    {
        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(Media, Result(seconds: 297.9));
        Assert.True(decision.Eligible);
        Assert.Equal("Matched", decision.Outcome);
    }

    [Theory]
    [InlineData("Hello (Radio Mix)", "25", "Hello", null)]
    [InlineData("Hello", "25", "Hello", "live")]
    [InlineData("Hello", "25", "Hello", "DJ mix")]
    public void Rejects_VersionTokens_InSourceTitleOrCandidate(string title, string album, string candidateTitle, string? disambiguation)
    {
        var media = new MediaFile { Title = title, Artist = "Adele", Album = album, Duration = TimeSpan.FromSeconds(295) };
        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, Result(candidateTitle, disambiguation: disambiguation));
        Assert.False(decision.Eligible);
        Assert.Equal("Skipped", decision.Outcome);
    }

    [Theory]
    [InlineData("Hello", "Greatest Hits 精选")]
    [InlineData("Theme", "Movie OST 原声")]
    [InlineData("Song", "Single 单曲")]
    [InlineData("Song", "Album (Deluxe Edition)")]
    public void Accepts_VersionTokens_InAlbum_WhenTitleIsClean(string title, string album)
    {
        var media = new MediaFile { Title = title, Artist = "Adele", Album = album, Duration = TimeSpan.FromSeconds(295) };
        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, Result(title: title));
        Assert.True(decision.Eligible);
        Assert.Equal("Matched", decision.Outcome);
    }

    [Fact]
    public void Rejects_TruncatedArtist_SuchAsEarthWindAndFire()
    {
        // Must reject when raw artist is "Earth, Wind & Fire" and matched artist is truncated to "Earth"
        var media = new MediaFile { Title = "September", Artist = "Earth, Wind & Fire", Duration = TimeSpan.FromSeconds(215) };
        var candidate = Result(title: "September", artist: "Earth", seconds: 215);

        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, candidate);
        Assert.False(decision.Eligible);
        Assert.Equal("Unmatched", decision.Outcome);
        Assert.Contains("not an exact normalized match", decision.Reason);
    }

    [Theory]
    [InlineData("mono")]
    [InlineData("reissue")]
    [InlineData("bonus track")]
    [InlineData("alternate")]
    public void Rejects_AnyNonEmptyDisambiguation(string disambiguation)
    {
        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(Media, Result(disambiguation: disambiguation));
        Assert.False(decision.Eligible);
        Assert.Equal("Skipped", decision.Outcome);
    }

    [Theory]
    [InlineData("Song Ä", "Artist", "Album")]
    [InlineData("Song", "Artist É", "Album")]
    public void Rejects_MojibakeTitleOrArtist(string title, string artist, string album)
    {
        var media = new MediaFile { Title = title, Artist = artist, Album = album, Duration = TimeSpan.FromSeconds(295) };
        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, Result(title: "Clean", artist: "Clean"));
        Assert.False(decision.Eligible);
        Assert.Equal("Skipped", decision.Outcome);
        Assert.Contains("mojibake", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ignores_MojibakeInAlbum_WhenTitleAndArtistAreClean()
    {
        var media = new MediaFile { Title = "Hello", Artist = "Adele", Album = "Album Ü", Duration = TimeSpan.FromSeconds(295) };
        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, Result());
        Assert.True(decision.Eligible);
        Assert.Equal("Matched", decision.Outcome);
    }

    [Fact]
    public void Accepts_MultipleMediaFiles_WithSameRecordingId()
    {
        var media1 = new MediaFile { Id = 1, Title = "Hello", Artist = "Adele", Album = "25", Duration = TimeSpan.FromSeconds(295) };
        var media2 = new MediaFile { Id = 2, Title = "Hello", Artist = "Adele", Album = "25 Reissue", Duration = TimeSpan.FromSeconds(295) };
        var result = Result();

        var decision1 = LocalIdentityAutoEligibilityPolicy.Evaluate(media1, result);
        var decision2 = LocalIdentityAutoEligibilityPolicy.Evaluate(media2, result);

        Assert.True(decision1.Eligible);
        Assert.Equal("Matched", decision1.Outcome);
        Assert.True(decision2.Eligible);
        Assert.Equal("Matched", decision2.Outcome);
    }

    [Fact]
    public void Rejects_SubstringTitleAndMissingDuration()
    {
        var substring = LocalIdentityAutoEligibilityPolicy.Evaluate(Media, Result(title: "Hello (instrumental)"));
        Assert.False(substring.Eligible);
        var noDuration = LocalIdentityAutoEligibilityPolicy.Evaluate(Media, Result(seconds: 0));
        Assert.False(noDuration.Eligible);
    }

    [Fact]
    public void Accepts_NormalizedPunctuationAndQuotes()
    {
        // Curly vs straight quotes, extra spaces
        var media = new MediaFile { Title = "Don’t Stop   Believin’", Artist = "Journey", Duration = TimeSpan.FromSeconds(250) };
        var result = Result(title: "Don't Stop Believin'", artist: "Journey", seconds: 251);

        var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, result);
        Assert.True(decision.Eligible);
        Assert.Equal("Matched", decision.Outcome);
    }
}
