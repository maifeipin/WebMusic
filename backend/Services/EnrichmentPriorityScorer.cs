using System;
using System.Collections.Generic;
using System.Linq;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public enum EnrichmentPriorityTier
{
    Tier1_FavoriteActive = 1,          // 收藏常听
    Tier2_ActiveNotFavorite = 2,        // 常听未收藏
    Tier3_IdentifiedMissingAssets = 3,  // 有明确身份但缺资源
    Tier4_StandardMedia = 4,            // 普通媒体
    Tier5_LongTailOrClip = 5            // 无播放且低置信/剪辑类文件
}

public record ScoredCandidate
{
    public required MediaFile Media { get; init; }
    public bool IsFav { get; init; }
    public int PlayCount { get; init; }
    public bool RecentPlay { get; init; }
    public bool HasLocalMBIdentity { get; init; }
    public double? LocalMBConfidence { get; init; }
    public bool NeedsCover { get; init; }
    public bool NeedsLyrics { get; init; }
    public int? LastFmPopularity { get; init; }
    public bool IsClipOrDerivation { get; init; }

    public EnrichmentPriorityTier Tier { get; init; }
    public int CompositeScore { get; init; }
}

public static class EnrichmentPriorityScorer
{
    public static ScoredCandidate Score(
        MediaFile media,
        bool isFav,
        int playCount,
        bool recentPlay,
        bool hasLocalMBIdentity,
        double? localMBConfidence,
        bool needsCover,
        bool needsLyrics,
        int? lastFmPopularity = null)
    {
        var isClipOrDerivation = DetectClipOrDerivation(media);

        // 1. Determine Tier
        EnrichmentPriorityTier tier;
        if (isFav && (recentPlay || playCount > 0))
        {
            tier = EnrichmentPriorityTier.Tier1_FavoriteActive;
        }
        else if (!isFav && (recentPlay || playCount > 0))
        {
            tier = EnrichmentPriorityTier.Tier2_ActiveNotFavorite;
        }
        else if (hasLocalMBIdentity && (localMBConfidence ?? 0) >= 0.85 && (needsCover || needsLyrics))
        {
            tier = EnrichmentPriorityTier.Tier3_IdentifiedMissingAssets;
        }
        else if (isClipOrDerivation || (playCount == 0 && !isFav && !recentPlay && !hasLocalMBIdentity))
        {
            tier = EnrichmentPriorityTier.Tier5_LongTailOrClip;
        }
        else
        {
            tier = EnrichmentPriorityTier.Tier4_StandardMedia;
        }

        // 2. Compute Composite Score
        // Base score signals
        int baseScore = (isFav ? 1000 : 0) + (recentPlay ? 300 : 0) + Math.Min(playCount * 10, 500);

        // Identity bonus (only for high confidence local identity)
        int identityBonus = (hasLocalMBIdentity && (localMBConfidence ?? 0) >= 0.85) ? 250 : 0;

        // Resource need bonus
        int missingAssetsBonus = (needsCover || needsLyrics) ? 100 : 0;

        // Last.fm popularity bonus: ONLY if track already has validated MBID (range 0-300)
        int lastFmBonus = (hasLocalMBIdentity && lastFmPopularity.HasValue)
            ? Math.Clamp(lastFmPopularity.Value, 0, 300)
            : 0;

        // Penalty for clips or derivations
        int clipPenalty = isClipOrDerivation ? -1000 : 0;

        int compositeScore = baseScore + identityBonus + missingAssetsBonus + lastFmBonus + clipPenalty;

        return new ScoredCandidate
        {
            Media = media,
            IsFav = isFav,
            PlayCount = playCount,
            RecentPlay = recentPlay,
            HasLocalMBIdentity = hasLocalMBIdentity,
            LocalMBConfidence = localMBConfidence,
            NeedsCover = needsCover,
            NeedsLyrics = needsLyrics,
            LastFmPopularity = lastFmPopularity,
            IsClipOrDerivation = isClipOrDerivation,
            Tier = tier,
            CompositeScore = compositeScore
        };
    }

    public static bool DetectClipOrDerivation(MediaFile media)
    {
        // 1. Very short duration (< 45s) is likely a clip or ringtone
        if (media.Duration > TimeSpan.Zero && media.Duration.TotalSeconds < 45)
        {
            return true;
        }

        // 2. Check title / album keywords
        var title = media.Title ?? string.Empty;
        var path = media.FilePath ?? string.Empty;

        string[] clipKeywords = { "clip", "preview", "ringtone", "edit", "short ver", "cut", "sample" };
        foreach (var kw in clipKeywords)
        {
            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static IOrderedEnumerable<ScoredCandidate> OrderByPriority(IEnumerable<ScoredCandidate> candidates)
    {
        return candidates
            .OrderBy(c => c.Tier) // Tier 1 first, then Tier 2, etc.
            .ThenByDescending(c => c.CompositeScore)
            .ThenBy(c => c.Media.Id);
    }
}
