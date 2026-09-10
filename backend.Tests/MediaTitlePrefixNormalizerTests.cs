using Xunit;
using WebMusic.Backend.Services;

namespace WebMusic.Backend.Tests;

public class MediaTitlePrefixNormalizerTests
{
    [Theory]
    [InlineData("Track 01 - 晴天", "晴天")]
    [InlineData("Track01. 晴天", "晴天")]
    [InlineData("Track 12 晴天", "晴天")]
    [InlineData("track 03 - 《晴天》", "晴天")]
    public void Normalize_ExplicitTrackKeyword_AdmitsAndCleans(string input, string expected)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.True(result.IsAdmitted);
        Assert.Equal("ExplicitTrackKeyword", result.Rule);
        Assert.Equal(expected, result.NewTitle);
        Assert.Equal(1.0, result.Confidence);
    }

    [Theory]
    [InlineData("Track1234 Song")]
    [InlineData("Track01 123")]
    [InlineData("Track 01 - 123")]
    public void Normalize_ExplicitTrackKeyword_RejectsInvalidNumberOrTrailingDigits(string input)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.False(result.IsAdmitted);
        Assert.Equal(input, result.NewTitle);
    }

    [Theory]
    [InlineData("01. 晴天", "晴天")]
    [InlineData("03 - 韻母歌", "韻母歌")]
    [InlineData("1、1812序曲", "1812序曲")]
    [InlineData("2、《E调前奏曲（巴哈）》", "E调前奏曲（巴哈）")]
    [InlineData("12. 我是不是你最疼爱的人-潘越云", "我是不是你最疼爱的人-潘越云")]
    public void Normalize_ExplicitDelimiterIndex_AdmitsAndCleans(string input, string expected)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.True(result.IsAdmitted);
        Assert.Equal("ExplicitDelimiterIndex", result.Rule);
        Assert.Equal(expected, result.NewTitle);
        Assert.Equal(1.0, result.Confidence);
    }

    [Theory]
    [InlineData("[01] 晴天", "晴天")]
    [InlineData("(01) 晴天", "晴天")]
    [InlineData("【01】晴天", "晴天")]
    [InlineData("（1）《平沙落雁》", "平沙落雁")]
    public void Normalize_BracketedTrackNumber_AdmitsAndCleans(string input, string expected)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.True(result.IsAdmitted);
        Assert.Equal("BracketedTrackNumber", result.Rule);
        Assert.Equal(expected, result.NewTitle);
        Assert.Equal(1.0, result.Confidence);
    }

    [Theory]
    [InlineData("《平湖秋月》", "平湖秋月")]
    [InlineData("《1812序曲》", "1812序曲")]
    public void Normalize_FullBookTitleBrackets_AdmitsAndCleans(string input, string expected)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.True(result.IsAdmitted);
        Assert.Equal("FullBookTitleBrackets", result.Rule);
        Assert.Equal(expected, result.NewTitle);
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public void Normalize_PureDigitsTitle_FallsBackToFilename()
    {
        var result = MediaTitlePrefixNormalizer.Normalize("287", "/music/往事.mp3");
        Assert.True(result.IsAdmitted);
        Assert.Equal("PureNumberFallbackToFilename", result.Rule);
        Assert.Equal("往事", result.NewTitle);
        Assert.Equal(0.9, result.Confidence);
    }

    [Fact]
    public void Normalize_PureDigitsTitle_WithPrefixInFilename_CleansProperly()
    {
        var result = MediaTitlePrefixNormalizer.Normalize("287", "/music/01. 往事.mp3");
        Assert.True(result.IsAdmitted);
        Assert.Equal("PureNumberFallbackToFilename", result.Rule);
        Assert.Equal("往事", result.NewTitle);
    }

    [Theory]
    [InlineData("7 Years")]
    [InlineData("21 Guns")]
    [InlineData("99 Luftballons")]
    [InlineData("50 Ways to Say Goodbye")]
    public void Normalize_SingleSpaceAfterNumber_StrictlyForbidden(string input)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.False(result.IsAdmitted);
        Assert.Equal("SingleSpaceAfterNumber", result.Rule);
        Assert.Equal(input, result.NewTitle);
    }

    [Theory]
    [InlineData("99.9")]
    [InlineData("1-800-273-8255")]
    [InlineData("10.10")]
    public void Normalize_DelimiterFollowedByDigits_Rejected(string input)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.False(result.IsAdmitted);
        Assert.Contains("digits", result.RejectionReason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_MultipleSpaces_RequiresManualReviewOnly()
    {
        var result = MediaTitlePrefixNormalizer.Normalize("3     F调旋律(鲁宾斯坦)");
        Assert.False(result.IsAdmitted);
        Assert.True(result.RequiresManualReview);
        Assert.Equal("MultipleSpacesCandidate", result.Rule);
        Assert.Equal("F调旋律(鲁宾斯坦)", result.NewTitle);
    }

    [Theory]
    [InlineData("#1 Record")]
    [InlineData("@Night")]
    [InlineData("!Alert!")]
    public void Normalize_PunctuationLikeHashAtExclamation_Preserved(string input)
    {
        var result = MediaTitlePrefixNormalizer.Normalize(input);
        Assert.False(result.IsAdmitted);
        Assert.Equal(input, result.NewTitle);
    }

    [Fact]
    public void Normalize_WithTrailingOrLeadingWhitespace_PreservesRawOldTitleAndCleansNewTitle()
    {
        // Regression test for production scenario (MediaFile ID 4750)
        var rawInput = "  136.Know Oneself And Each Other  (Cocteau Twins cover of ''Know Who You Are At Every Age'') \t";
        var result = MediaTitlePrefixNormalizer.Normalize(rawInput);

        Assert.True(result.IsAdmitted);
        Assert.Equal("ExplicitDelimiterIndex", result.Rule);
        Assert.Equal(rawInput, result.OldTitle); // Raw input strictly preserved without premature trimming
        Assert.Equal("Know Oneself And Each Other  (Cocteau Twins cover of ''Know Who You Are At Every Age'')", result.NewTitle);
    }
}
