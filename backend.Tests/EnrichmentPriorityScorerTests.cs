using System;
using System.Collections.Generic;
using System.Linq;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using Xunit;

namespace WebMusic.Backend.Tests;

public class EnrichmentPriorityScorerTests
{
    [Fact]
    public void Score_AssignsTier1_ToFavoriteActiveTracks()
    {
        var media = new MediaFile
        {
            Id = 1,
            Title = "Favorite Song",
            Artist = "Great Artist",
            Duration = TimeSpan.FromMinutes(3)
        };

        var scored = EnrichmentPriorityScorer.Score(
            media,
            isFav: true,
            playCount: 10,
            recentPlay: true,
            hasLocalMBIdentity: false,
            localMBConfidence: null,
            needsCover: true,
            needsLyrics: true
        );

        Assert.Equal(EnrichmentPriorityTier.Tier1_FavoriteActive, scored.Tier);
        Assert.True(scored.CompositeScore >= 1300); // 1000 (fav) + 300 (recent) + 100 (playCount*10) + 100 (missing)
    }

    [Fact]
    public void Score_AssignsTier2_ToActiveNotFavoriteTracks()
    {
        var media = new MediaFile
        {
            Id = 2,
            Title = "Active Track",
            Artist = "Popular Artist",
            Duration = TimeSpan.FromMinutes(4)
        };

        var scored = EnrichmentPriorityScorer.Score(
            media,
            isFav: false,
            playCount: 5,
            recentPlay: true,
            hasLocalMBIdentity: false,
            localMBConfidence: null,
            needsCover: true,
            needsLyrics: false
        );

        Assert.Equal(EnrichmentPriorityTier.Tier2_ActiveNotFavorite, scored.Tier);
        Assert.True(scored.CompositeScore > 0);
    }

    [Fact]
    public void Score_AssignsTier3_ToIdentifiedMissingAssetsTracks()
    {
        var media = new MediaFile
        {
            Id = 3,
            Title = "Identified Track",
            Artist = "Recognized Artist",
            Duration = TimeSpan.FromMinutes(3.5)
        };

        var scored = EnrichmentPriorityScorer.Score(
            media,
            isFav: false,
            playCount: 0,
            recentPlay: false,
            hasLocalMBIdentity: true,
            localMBConfidence: 0.95,
            needsCover: true,
            needsLyrics: false
        );

        Assert.Equal(EnrichmentPriorityTier.Tier3_IdentifiedMissingAssets, scored.Tier);
        Assert.True(scored.CompositeScore >= 350); // 250 (identity) + 100 (missing)
    }

    [Fact]
    public void Score_AssignsTier5_ToShortClipsOrDormantLongTail()
    {
        var shortClip = new MediaFile
        {
            Id = 4,
            Title = "Phone Ringtone Preview",
            Artist = "Sound Effect",
            Duration = TimeSpan.FromSeconds(25)
        };

        var scored = EnrichmentPriorityScorer.Score(
            shortClip,
            isFav: false,
            playCount: 0,
            recentPlay: false,
            hasLocalMBIdentity: false,
            localMBConfidence: null,
            needsCover: false,
            needsLyrics: false
        );

        Assert.Equal(EnrichmentPriorityTier.Tier5_LongTailOrClip, scored.Tier);
        Assert.True(scored.IsClipOrDerivation);
        Assert.True(scored.CompositeScore < 0); // heavily penalized
    }

    [Fact]
    public void OrderByPriority_OrdersStrictlyByTiers()
    {
        var t1Media = new MediaFile { Id = 10, Title = "T1", Artist = "A1", Duration = TimeSpan.FromMinutes(3) };
        var t2Media = new MediaFile { Id = 20, Title = "T2", Artist = "A2", Duration = TimeSpan.FromMinutes(3) };
        var t3Media = new MediaFile { Id = 30, Title = "T3", Artist = "A3", Duration = TimeSpan.FromMinutes(3) };
        var t4Media = new MediaFile { Id = 40, Title = "T4", Artist = "A4", Duration = TimeSpan.FromMinutes(3) };
        var t5Media = new MediaFile { Id = 50, Title = "T5", Artist = "A5", Duration = TimeSpan.FromSeconds(20) };

        var candidates = new List<ScoredCandidate>
        {
            EnrichmentPriorityScorer.Score(t5Media, false, 0, false, false, null, false, false),
            EnrichmentPriorityScorer.Score(t3Media, false, 0, false, true, 0.92, true, false),
            EnrichmentPriorityScorer.Score(t1Media, true, 20, true, true, 0.98, true, true),
            EnrichmentPriorityScorer.Score(t4Media, false, 0, false, false, null, true, true),
            EnrichmentPriorityScorer.Score(t2Media, false, 15, true, false, null, false, true)
        };

        var ordered = EnrichmentPriorityScorer.OrderByPriority(candidates).ToList();

        Assert.Equal(10, ordered[0].Media.Id); // Tier 1 (Favorite Active)
        Assert.Equal(20, ordered[1].Media.Id); // Tier 2 (Active Not Favorite)
        Assert.Equal(30, ordered[2].Media.Id); // Tier 3 (Identified Missing Assets)
        Assert.Equal(40, ordered[3].Media.Id); // Tier 4 (Standard Media)
        Assert.Equal(50, ordered[4].Media.Id); // Tier 5 (Long Tail / Clip)
    }

    [Fact]
    public void Score_AppliesLastFmBonus_OnlyWhenLocalMBIdentityPresent()
    {
        var withId = new MediaFile { Id = 60, Title = "Song With Id", Artist = "Artist", Duration = TimeSpan.FromMinutes(3) };
        var withoutId = new MediaFile { Id = 61, Title = "Song Without Id", Artist = "Artist", Duration = TimeSpan.FromMinutes(3) };

        var scoredWithId = EnrichmentPriorityScorer.Score(
            withId, false, 5, false, hasLocalMBIdentity: true, localMBConfidence: 0.90, needsCover: false, needsLyrics: false, lastFmPopularity: 80);

        var scoredWithoutId = EnrichmentPriorityScorer.Score(
            withoutId, false, 5, false, hasLocalMBIdentity: false, localMBConfidence: null, needsCover: false, needsLyrics: false, lastFmPopularity: 80);

        Assert.Equal(80, scoredWithId.LastFmPopularity);
        Assert.True(scoredWithId.CompositeScore > scoredWithoutId.CompositeScore);
    }
}
