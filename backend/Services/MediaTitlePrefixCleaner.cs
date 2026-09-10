using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public record TitlePrefixCandidateItem(
    int MediaFileId,
    string FilePath,
    string OldTitle,
    string NewTitle,
    string Rule,
    double Confidence,
    string OldInputFingerprint,
    string InputFingerprint
);

public record TitlePrefixManualCandidateItem(
    int MediaFileId,
    string FilePath,
    string OldTitle,
    string ProposedNewTitle,
    string Rule,
    double Confidence,
    string Reason
);

public record TitlePrefixCleanReport(
    string Scope,
    int TotalEvaluated,
    int AdmittedCount,
    int ManualReviewCandidateCount,
    int SkippedOrNormalCount,
    Dictionary<string, int> RuleBreakdown,
    Dictionary<string, int> SkippedBreakdown,
    List<TitlePrefixCandidateItem> AdmittedItems,
    List<TitlePrefixManualCandidateItem> ManualReviewCandidates
);

public record TitlePrefixApplyResult(
    int UpdatedCount,
    string ReportSha256,
    string RollbackManifestPath
);

public static class MediaTitlePrefixCleaner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static async Task<TitlePrefixCleanReport> RunDryRunAsync(
        AppDbContext db,
        int count = 1750,
        string filterAlbum = "Unknown Album",
        int? afterId = null,
        CancellationToken cancellationToken = default)
    {
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? readOnlyTx = null;
        try
        {
            // Enforce database-level physical read-only guarantee when on PostgreSQL
            if (db.Database.IsNpgsql())
            {
                readOnlyTx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }

            var query = db.MediaFiles
                .AsNoTracking()
                .Where(m => m.Album.ToLower() == filterAlbum.ToLower());

            if (afterId.HasValue)
            {
                query = query.Where(m => m.Id > afterId.Value);
            }

            var candidateFiles = await query
                .OrderBy(m => m.Id)
                .Take(count)
                .ToListAsync(cancellationToken);

            var candidateIds = candidateFiles.Select(m => m.Id).ToList();

            // Load manual identity locks across candidates
            var manualLockedIds = (await db.MediaIdentities
                .AsNoTracking()
                .Where(mi => candidateIds.Contains(mi.MediaFileId) &&
                             (mi.MatchMethod.ToLower().Contains("manual") || mi.Status.ToLower() == "manual"))
                .Select(mi => mi.MediaFileId)
                .ToListAsync(cancellationToken)).ToHashSet();

            var admittedItems = new List<TitlePrefixCandidateItem>();
            var manualReviewCandidates = new List<TitlePrefixManualCandidateItem>();
            var ruleBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var skippedBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var media in candidateFiles)
            {
                if (manualLockedIds.Contains(media.Id))
                {
                    skippedBreakdown["ManualIdentityLocked"] = skippedBreakdown.GetValueOrDefault("ManualIdentityLocked") + 1;
                    continue;
                }

                var norm = MediaTitlePrefixNormalizer.Normalize(media.Title, media.FilePath);
                if (norm.IsAdmitted)
                {
                    var oldFp = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(media);
                    var clone = new MediaFile
                    {
                        Id = media.Id,
                        Title = norm.NewTitle,
                        Artist = media.Artist,
                        Album = media.Album,
                        Duration = media.Duration,
                        FileHash = media.FileHash,
                        FilePath = media.FilePath
                    };
                    var newFp = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(clone);

                    admittedItems.Add(new TitlePrefixCandidateItem(
                        MediaFileId: media.Id,
                        FilePath: media.FilePath,
                        OldTitle: media.Title,
                        NewTitle: norm.NewTitle,
                        Rule: norm.Rule,
                        Confidence: norm.Confidence,
                        OldInputFingerprint: oldFp,
                        InputFingerprint: newFp
                    ));

                    ruleBreakdown[norm.Rule] = ruleBreakdown.GetValueOrDefault(norm.Rule) + 1;
                }
                else if (norm.RequiresManualReview)
                {
                    manualReviewCandidates.Add(new TitlePrefixManualCandidateItem(
                        MediaFileId: media.Id,
                        FilePath: media.FilePath,
                        OldTitle: norm.OldTitle,
                        ProposedNewTitle: norm.NewTitle,
                        Rule: norm.Rule,
                        Confidence: norm.Confidence,
                        Reason: norm.RejectionReason ?? "Requires manual review."
                    ));
                }
                else
                {
                    skippedBreakdown[norm.Rule] = skippedBreakdown.GetValueOrDefault(norm.Rule) + 1;
                }
            }

            var report = new TitlePrefixCleanReport(
                Scope: $"{filterAlbum} (Count: {candidateFiles.Count}, IDs: {candidateFiles.FirstOrDefault()?.Id} to {candidateFiles.LastOrDefault()?.Id})",
                TotalEvaluated: candidateFiles.Count,
                AdmittedCount: admittedItems.Count,
                ManualReviewCandidateCount: manualReviewCandidates.Count,
                SkippedOrNormalCount: skippedBreakdown.Values.Sum(),
                RuleBreakdown: ruleBreakdown,
                SkippedBreakdown: skippedBreakdown,
                AdmittedItems: admittedItems,
                ManualReviewCandidates: manualReviewCandidates
            );

            return report;
        }
        finally
        {
            if (readOnlyTx != null)
            {
                await readOnlyTx.RollbackAsync(cancellationToken);
            }
        }
    }

    public static async Task<string> SaveReportWithSha256Async(TitlePrefixCleanReport report, string outFile, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(outFile, json, cancellationToken);

        var fileBytes = await File.ReadAllBytesAsync(outFile, cancellationToken);
        var sha256Hex = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

        var shaFile = outFile + ".sha256";
        await File.WriteAllTextAsync(shaFile, $"{sha256Hex}  {Path.GetFileName(outFile)}\n", cancellationToken);

        return sha256Hex;
    }

    public static async Task<TitlePrefixApplyResult> ApplyAsync(
        AppDbContext db,
        string reportPath,
        string expectedReportSha,
        int expectedCount,
        string? rollbackManifestPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException($"Dry run report file not found: {reportPath}");
        }

        var fileBytes = await File.ReadAllBytesAsync(reportPath, cancellationToken);
        var actualSha = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();

        if (!string.Equals(actualSha, expectedReportSha.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Report SHA-256 verification failed! Expected '{expectedReportSha}', actual '{actualSha}'.");
        }

        var report = JsonSerializer.Deserialize<TitlePrefixCleanReport>(fileBytes, JsonOptions)
                     ?? throw new InvalidOperationException("Failed to deserialize dry run report.");

        if (report.AdmittedItems.Count != expectedCount)
        {
            throw new InvalidOperationException($"Admitted item count in report ({report.AdmittedItems.Count}) does not match expected count ({expectedCount}).");
        }

        if (report.AdmittedCount != report.AdmittedItems.Count ||
            report.AdmittedItems.Select(item => item.MediaFileId).Distinct().Count() != report.AdmittedItems.Count)
        {
            throw new InvalidOperationException("Dry run report admitted counts or MediaFileIds are inconsistent.");
        }

        foreach (var item in report.AdmittedItems)
        {
            var normalized = MediaTitlePrefixNormalizer.Normalize(item.OldTitle, item.FilePath);
            if (!normalized.IsAdmitted ||
                !string.Equals(normalized.NewTitle, item.NewTitle, StringComparison.Ordinal) ||
                !string.Equals(normalized.Rule, item.Rule, StringComparison.Ordinal) ||
                Math.Abs(normalized.Confidence - item.Confidence) > 0.000001)
            {
                throw new InvalidOperationException($"Report candidate for MediaFile ID {item.MediaFileId} does not match the current title normalization policy.");
            }
        }

        var targetIds = report.AdmittedItems.Select(x => x.MediaFileId).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);

        // In PostgreSQL, acquire pessimistic row-level locks (FOR UPDATE) to eliminate any TOCTOU window
        if (db.Database.IsNpgsql() && targetIds.Count > 0)
        {
            // Prevent a concurrent identity writer from inserting a manual lock
            // between the conflict check and the title update.
            await db.Database.ExecuteSqlRawAsync(
                "LOCK TABLE \"MediaIdentities\" IN SHARE ROW EXCLUSIVE MODE;",
                cancellationToken);
            var p0 = new Npgsql.NpgsqlParameter("p0", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
            {
                Value = targetIds.ToArray()
            };
            await db.Database.ExecuteSqlRawAsync("SELECT \"Id\" FROM \"MediaFiles\" WHERE \"Id\" = ANY(@p0) FOR UPDATE", new[] { p0 }, cancellationToken);
        }

        // Pre-check for manual identity locks in current database (with manual Status or manual MatchMethod)
        var manualConflictIds = await db.MediaIdentities
            .Where(mi => targetIds.Contains(mi.MediaFileId) &&
                         ((mi.MatchMethod != null && mi.MatchMethod.ToLower().Contains("manual")) ||
                          (mi.Status != null && mi.Status.ToLower() == "manual")))
            .Select(mi => mi.MediaFileId)
            .ToListAsync(cancellationToken);

        if (manualConflictIds.Count > 0)
        {
            throw new InvalidOperationException($"Cannot apply changes: Manual identity lock exists on MediaFileIds: {string.Join(", ", manualConflictIds)}");
        }

        var mediaList = await db.MediaFiles
            .Where(m => targetIds.Contains(m.Id))
            .ToListAsync(cancellationToken);

        if (mediaList.Count != expectedCount)
        {
            throw new InvalidOperationException($"Found {mediaList.Count} MediaFiles in database, expected exactly {expectedCount}.");
        }

        var mediaById = mediaList.ToDictionary(m => m.Id);
        var rollbackEntries = new List<object>();

        foreach (var item in report.AdmittedItems)
        {
            if (!mediaById.TryGetValue(item.MediaFileId, out var media))
            {
                throw new InvalidOperationException($"MediaFile ID {item.MediaFileId} not found in database.");
            }

            if (!string.Equals(media.Title, item.OldTitle, StringComparison.Ordinal) &&
                !string.Equals(media.Title.Trim(), item.OldTitle.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Title drift detected on MediaFile ID {item.MediaFileId}! Database has '{media.Title}', report expected '{item.OldTitle}'.");
            }


            if (!string.Equals(media.FilePath, item.FilePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"File path drift detected on MediaFile ID {item.MediaFileId}.");
            }

            var currentFp = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(media);
            if (!string.Equals(currentFp, item.OldInputFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Fingerprint drift detected on MediaFile ID {item.MediaFileId}! Database has '{currentFp}', report expected '{item.OldInputFingerprint}'.");
            }


            var normalized = MediaTitlePrefixNormalizer.Normalize(media.Title, media.FilePath);
            if (!normalized.IsAdmitted ||
                !string.Equals(normalized.NewTitle, item.NewTitle, StringComparison.Ordinal) ||
                !string.Equals(normalized.Rule, item.Rule, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Normalization policy drift detected on MediaFile ID {item.MediaFileId}.");
            }

            var normalizedMedia = new MediaFile
            {
                Id = media.Id,
                Title = normalized.NewTitle,
                Artist = media.Artist,
                Album = media.Album,
                Duration = media.Duration,
                FileHash = media.FileHash,
                FilePath = media.FilePath
            };
            var expectedNewFingerprint = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(normalizedMedia);
            if (!string.Equals(expectedNewFingerprint, item.InputFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Normalized fingerprint mismatch on MediaFile ID {item.MediaFileId}.");
            }

            // Apply title normalization (Artist and Album are strictly unmodified)
            media.Title = item.NewTitle;

            rollbackEntries.Add(new
            {
                MediaFileId = item.MediaFileId,
                FilePath = item.FilePath,
                PreviousTitle = item.OldTitle,
                AppliedTitle = item.NewTitle,
                PreviousInputFingerprint = item.OldInputFingerprint,
                NewInputFingerprint = item.InputFingerprint
            });
        }

        var updatedRows = await db.SaveChangesAsync(cancellationToken);
        if (updatedRows != expectedCount)
        {
            throw new InvalidOperationException($"Database updated {updatedRows} rows, expected exactly {expectedCount}. Aborting transaction.");
        }

        // Commit transaction first so that database changes are guaranteed persisted before manifest is saved
        await tx.CommitAsync(cancellationToken);

        var manifestPath = rollbackManifestPath ?? Path.Combine(
            Path.GetDirectoryName(reportPath) ?? "/tmp",
            $"title_prefix_rollback_manifest_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json"
        );

        var manifestDir = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrWhiteSpace(manifestDir))
        {
            Directory.CreateDirectory(manifestDir);
        }

        var manifestJson = JsonSerializer.Serialize(new
        {
            AppliedAt = DateTime.UtcNow,
            ReportSha256 = actualSha,
            TotalApplied = updatedRows,
            RollbackEntries = rollbackEntries
        }, JsonOptions);

        var tempManifestPath = manifestPath + ".tmp." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(tempManifestPath, manifestJson, cancellationToken);
        File.Move(tempManifestPath, manifestPath, overwrite: true);

        return new TitlePrefixApplyResult(updatedRows, actualSha, manifestPath);
    }
}
