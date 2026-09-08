using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public record ShadowRunAuditItem(
    int MediaId,
    string Title,
    string Artist,
    string? Album,
    double DurationSeconds,
    string Tier,
    int CompositeScore,
    string Outcome,
    double Confidence,
    long ElapsedMs,
    string? MatchedMbid,
    string? MatchedTitle,
    string? MatchedArtist,
    double? MatchedDurationSeconds,
    string? Disambiguation,
    bool IsDerivativeOrClip,
    string? ErrorDetail
);

public record ShadowRunSummary(
    int TotalEvaluated,
    int HighConfidence,
    double HighConfidenceRate,
    int Proposed,
    double ProposedRate,
    int Unmatched,
    double UnmatchedRate,
    int Failed,
    int DerivativeOrClip,
    double AverageElapsedMs
);

public record ShadowRunReport(
    string Mode,
    string TargetNode,
    DateTime Timestamp,
    ShadowRunSummary Summary,
    Dictionary<string, List<ShadowRunAuditItem>> Samples,
    List<ShadowRunAuditItem>? AllItems = null,
    string? HighConfidenceSha256 = null
);

public static class LocalMusicBrainzShadowRunner
{
    private static readonly SemaphoreSlim _globalShadowRunLock = new(1, 1);

    public static bool IsRunning => _globalShadowRunLock.CurrentCount == 0;

    public static async Task<ShadowRunReport> RunAsync(
        AppDbContext db,
        ILocalMusicBrainzService localMb,
        int count = 1000,
        bool onlyUnidentified = true,
        bool includeAllItemsInReport = false,
        Action<int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (count <= 0 || count > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Shadow Run batch count must be between 1 and 1000.");
        }

        if (!await _globalShadowRunLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A local MusicBrainz shadow run is already in progress. Concurrent runs are forbidden.");
        }

        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? readOnlyTx = null;
        try
        {
            // Enforce database-level physical read-only guarantee when on PostgreSQL
            if (db.Database.IsNpgsql())
            {
                readOnlyTx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }

            // 1. Fetch eligible candidates (read-only, AsNoTracking)
            var query = db.MediaFiles
                .AsNoTracking()
                .Where(m => !string.IsNullOrEmpty(m.Title) && !string.IsNullOrEmpty(m.Artist)
                         && !m.Title.StartsWith("Unknown") && !m.Artist.StartsWith("Unknown"));

            if (onlyUnidentified)
            {
                query = query.Where(m => !db.MediaIdentities.Any(i => i.MediaFileId == m.Id && i.Provider == LocalMusicBrainzService.ProviderName));
            }

        var cutoff30d = DateTime.UtcNow.AddDays(-30);
        var rawCandidates = await query
            .Select(m => new
            {
                Media = m,
                IsFav = m.Favorites.Any(),
                PlayCount = db.PlayHistories.Count(p => p.MediaFileId == m.Id),
                RecentPlay = db.PlayHistories.Any(p => p.MediaFileId == m.Id && p.PlayedAt >= cutoff30d),
                HasLocalMBIdentity = db.MediaIdentities.Any(i => i.MediaFileId == m.Id && i.Provider == LocalMusicBrainzService.ProviderName && i.Status == "approved"),
                LocalMBConfidence = db.MediaIdentities.Where(i => i.MediaFileId == m.Id && i.Provider == LocalMusicBrainzService.ProviderName).Select(i => (double?)i.Confidence).FirstOrDefault(),
                NeedsCover = string.IsNullOrEmpty(m.CoverArt),
                NeedsLyrics = !db.Lyrics.Any(l => l.MediaFileId == m.Id)
            })
            .ToListAsync(cancellationToken);

        // 2. Score and sort candidates by P3 Priority
        var scoredList = rawCandidates.Select(c => EnrichmentPriorityScorer.Score(
            c.Media, c.IsFav, c.PlayCount, c.RecentPlay, c.HasLocalMBIdentity, c.LocalMBConfidence, c.NeedsCover, c.NeedsLyrics
        ));

        var prioritizedCandidates = EnrichmentPriorityScorer.OrderByPriority(scoredList)
            .Take(count)
            .ToList();

        // 3. Perform ZERO-WRITE Shadow Run (Pure memory evaluation, zero SaveChanges)
        int matchedHigh = 0;
        int matchedProposed = 0;
        int unmatched = 0;
        int failed = 0;
        int derivativeOrClipCount = 0;
        long totalElapsedMs = 0;

        var allItems = new List<ShadowRunAuditItem>();
        var highSamples = new List<ShadowRunAuditItem>();
        var mediumSamples = new List<ShadowRunAuditItem>();
        var lowOrUnmatchedSamples = new List<ShadowRunAuditItem>();
        var derivativeSamples = new List<ShadowRunAuditItem>();

        int processed = 0;
        foreach (var item in prioritizedCandidates)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var scanRes = await localMb.ScanMediaIdentityAsync(item.Media, cancellationToken);
            totalElapsedMs += scanRes.ElapsedMs;

            var cand = scanRes.BestCandidate;
            double conf = cand?.Confidence ?? 0.0;
            bool isDeriv = cand?.IsDerivativeOrClip ?? item.IsClipOrDerivation;
            if (isDeriv) derivativeOrClipCount++;

            string tierOutcome;
            if (conf >= LocalMusicBrainzService.HighConfidenceThreshold)
            {
                matchedHigh++;
                tierOutcome = "HighConfidence";
            }
            else if (conf >= LocalMusicBrainzService.ProposedConfidenceThreshold)
            {
                matchedProposed++;
                tierOutcome = "ProposedMatch";
            }
            else if (scanRes.StatusCode != 200)
            {
                failed++;
                tierOutcome = "Failed";
            }
            else
            {
                unmatched++;
                tierOutcome = "Unmatched";
            }

            var auditEntry = new ShadowRunAuditItem(
                item.Media.Id,
                item.Media.Title,
                item.Media.Artist,
                item.Media.Album,
                item.Media.Duration.TotalSeconds,
                item.Tier.ToString(),
                item.CompositeScore,
                tierOutcome,
                conf,
                scanRes.ElapsedMs,
                cand?.RecordingId,
                cand?.MatchedTitle,
                cand?.MatchedArtist,
                cand != null && cand.MatchedDuration > TimeSpan.Zero ? cand.MatchedDuration.TotalSeconds : null,
                cand?.Disambiguation,
                isDeriv,
                scanRes.ErrorDetail
            );

            if (includeAllItemsInReport)
            {
                allItems.Add(auditEntry);
            }

            if (tierOutcome == "HighConfidence")
                highSamples.Add(auditEntry);
            else if (tierOutcome == "ProposedMatch" && mediumSamples.Count < 500)
                mediumSamples.Add(auditEntry);
            else if ((tierOutcome == "Unmatched" || tierOutcome == "Failed") && lowOrUnmatchedSamples.Count < 100)
                lowOrUnmatchedSamples.Add(auditEntry);

            if (isDeriv && derivativeSamples.Count < 500)
                derivativeSamples.Add(auditEntry);

            processed++;
            progressCallback?.Invoke(processed, prioritizedCandidates.Count);
        }

        int totalEvaluated = matchedHigh + matchedProposed + unmatched + failed;

        var summary = new ShadowRunSummary(
            totalEvaluated,
            matchedHigh,
            totalEvaluated > 0 ? Math.Round((double)matchedHigh / totalEvaluated, 4) : 0,
            matchedProposed,
            totalEvaluated > 0 ? Math.Round((double)matchedProposed / totalEvaluated, 4) : 0,
            unmatched,
            totalEvaluated > 0 ? Math.Round((double)unmatched / totalEvaluated, 4) : 0,
            failed,
            derivativeOrClipCount,
            totalEvaluated > 0 ? Math.Round((double)totalElapsedMs / totalEvaluated, 1) : 0
        );

        var samples = new Dictionary<string, List<ShadowRunAuditItem>>
        {
            ["highConfidence"] = highSamples,
            ["mediumConfidence"] = mediumSamples,
            ["lowOrUnmatched"] = lowOrUnmatchedSamples,
            ["derivativeOrClip"] = derivativeSamples
        };

        // Compute stable SHA-256 checksum across all high confidence candidates
        string? highConfidenceSha256 = null;
        if (highSamples.Count > 0)
        {
            var sortedHigh = highSamples
                .OrderBy(h => h.MediaId)
                .Select(h => new
                {
                    h.MediaId,
                    h.Title,
                    h.Artist,
                    h.DurationSeconds,
                    h.MatchedMbid,
                    h.MatchedTitle,
                    h.MatchedArtist,
                    h.Confidence
                })
                .ToList();
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(sortedHigh));
            using var sha = System.Security.Cryptography.SHA256.Create();
            highConfidenceSha256 = Convert.ToHexString(sha.ComputeHash(jsonBytes)).ToLowerInvariant();
        }

            return new ShadowRunReport(
                "SHADOW_RUN_ZERO_WRITE",
                localMb.BaseUrl,
                DateTime.UtcNow,
                summary,
                samples,
                includeAllItemsInReport ? allItems : null,
                highConfidenceSha256
            );
        }
        finally
        {
            if (readOnlyTx != null)
            {
                try
                {
                    await readOnlyTx.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // Ignore rollback exception on clean teardown
                }
                await readOnlyTx.DisposeAsync();
            }
            _globalShadowRunLock.Release();
        }
    }
}
