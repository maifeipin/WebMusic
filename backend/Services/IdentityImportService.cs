using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public class IdentityImportService : IIdentityImportService
{
    private const int MaxBatchLimit = 500;
    private readonly AppDbContext _db;
    private readonly ILogger<IdentityImportService> _logger;
    private readonly IWebHostEnvironment? _environment;
    private readonly IConfiguration? _configuration;

    public IdentityImportService(
        AppDbContext db,
        ILogger<IdentityImportService> logger,
        IWebHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        _db = db;
        _logger = logger;
        _environment = environment;
        _configuration = configuration;
    }

    public static string ComputeFingerprint(string title, string artist, string? album, double durationSeconds)
    {
        var canonical = $"{title.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}|{(album ?? string.Empty).Trim().ToLowerInvariant()}|{durationSeconds:F2}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    public static string NormalizeReportId(string reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            throw new ArgumentException("Report ID cannot be empty.", nameof(reportId));

        var clean = reportId.Trim();
        if (clean.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^5];
        }

        if (clean.Contains('/') || clean.Contains('\\') || clean.Contains(':') || clean.Contains(".."))
        {
            throw new ArgumentException($"Invalid Report ID '{reportId}': path separators, colons, and directory traversal sequences are strictly prohibited.", nameof(reportId));
        }

        if (!Regex.IsMatch(clean, "^[a-zA-Z0-9_-]+$"))
        {
            throw new ArgumentException($"Invalid Report ID '{reportId}'. Report ID must only contain alphanumeric characters, underscores, or hyphens, without any path separators or extensions.", nameof(reportId));
        }

        return clean;
    }

    public string GetReportsDirectory()
    {
        if (!string.IsNullOrEmpty(_configuration?["IdentityImport:ReportsDirectory"]))
        {
            var configuredPath = Path.GetFullPath(_configuration["IdentityImport:ReportsDirectory"]!);
            if (!Directory.Exists(configuredPath)) Directory.CreateDirectory(configuredPath);
            return configuredPath;
        }

        // Fixed data/reports directory ONLY: resolved from ContentRootPath or current working directory
        var basePath = !string.IsNullOrEmpty(_environment?.ContentRootPath)
            ? _environment.ContentRootPath
            : Directory.GetCurrentDirectory();

        var fixedReportsDir = Path.GetFullPath(Path.Combine(basePath, "data", "reports"));
        if (!Directory.Exists(fixedReportsDir))
        {
            Directory.CreateDirectory(fixedReportsDir);
        }
        return fixedReportsDir;
    }

    public static HashSet<int> ParseApprovedWhitelist(string auditMarkdownContent)
    {
        var approvedIds = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(auditMarkdownContent)) return approvedIds;

        var match = Regex.Match(
            auditMarkdownContent,
            @"(?:##\s*(?:可写入候选|Approved Candidates|Approved)[\s\S]*?```(?:text)?\s*([\s\S]*?)```)",
            RegexOptions.IgnoreCase
        );

        if (match.Success)
        {
            var rawBlock = match.Groups[1].Value;
            var tokens = Regex.Matches(rawBlock, @"\b\d+\b");
            foreach (Match token in tokens)
            {
                if (int.TryParse(token.Value, out var id))
                {
                    approvedIds.Add(id);
                }
            }
        }

        return approvedIds;
    }

    private (string reportPath, string auditPath) ResolveReportFiles(string reportId)
    {
        var cleanId = NormalizeReportId(reportId);
        var reportsDir = GetReportsDirectory();

        var reportPath = Path.GetFullPath(Path.Combine(reportsDir, $"{cleanId}.json"));
        var auditPath = Path.GetFullPath(Path.Combine(reportsDir, $"{cleanId}_audit.md"));

        var normalizedReportsDir = reportsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!reportPath.StartsWith(normalizedReportsDir, StringComparison.Ordinal) ||
            !auditPath.StartsWith(normalizedReportsDir, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Access to path outside fixed reports directory '{reportsDir}' is strictly forbidden.");
        }

        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException($"Shadow run report '{cleanId}.json' was not found in fixed reports directory '{reportsDir}'.");
        }

        if (!File.Exists(auditPath))
        {
            throw new FileNotFoundException($"Audit manifest '{cleanId}_audit.md' was not found in fixed reports directory '{reportsDir}'. Every batch import strictly requires an audit manifest.");
        }

        return (reportPath, auditPath);
    }

    private async Task<(ShadowRunReport report, string reportSha, string auditSha, Dictionary<int, ShadowRunAuditItem> highConfidenceLookup, HashSet<int> approvedWhitelist)>
        LoadAndVerifyReportAsync(string reportId, CancellationToken cancellationToken)
    {
        var (reportPath, auditPath) = ResolveReportFiles(reportId);

        var reportBytes = await File.ReadAllBytesAsync(reportPath, cancellationToken);
        var reportSha = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant();

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var report = JsonSerializer.Deserialize<ShadowRunReport>(reportBytes, jsonOptions);
        if (report == null)
        {
            throw new InvalidOperationException($"Failed to deserialize ShadowRunReport from {reportPath}.");
        }

        var auditBytes = await File.ReadAllBytesAsync(auditPath, cancellationToken);
        var auditSha = Convert.ToHexString(SHA256.HashData(auditBytes)).ToLowerInvariant();
        var auditText = Encoding.UTF8.GetString(auditBytes);
        var approvedWhitelist = ParseApprovedWhitelist(auditText);

        if (approvedWhitelist.Count == 0)
        {
            throw new InvalidOperationException($"Audit manifest '{Path.GetFileName(auditPath)}' does not contain any approved candidates under '## 可写入候选'.");
        }

        var lookup = new Dictionary<int, ShadowRunAuditItem>();
        if (report.AllItems != null)
        {
            foreach (var item in report.AllItems)
            {
                if (item.Outcome == "HighConfidence" || item.Confidence >= 0.85)
                {
                    lookup[item.MediaId] = item;
                }
            }
        }
        if (report.Samples != null && report.Samples.TryGetValue("highConfidence", out var samples))
        {
            foreach (var item in samples)
            {
                lookup[item.MediaId] = item;
            }
        }

        return (report, reportSha, auditSha, lookup, approvedWhitelist);
    }

    private record BatchVerificationContext(
        IdentityImportPreviewResult PreviewResult,
        string ReportSha,
        string AuditSha,
        Dictionary<int, ShadowRunAuditItem> Lookup,
        Dictionary<int, MediaFile> MediaFiles,
        List<int> DistinctIds
    );

    private async Task<BatchVerificationContext> VerifyBatchCandidatesAsync(
        string reportId,
        List<int> approvedItemIds,
        CancellationToken cancellationToken)
    {
        if (approvedItemIds == null || approvedItemIds.Count == 0)
            throw new ArgumentException("ApprovedItemIds cannot be empty.", nameof(approvedItemIds));

        var distinctIds = approvedItemIds.Distinct().ToList();
        if (distinctIds.Count > MaxBatchLimit)
            throw new ArgumentException($"ApprovedItemIds count ({distinctIds.Count}) exceeds maximum allowed limit of {MaxBatchLimit}.", nameof(approvedItemIds));

        var (report, reportSha, auditSha, lookup, approvedWhitelist) = await LoadAndVerifyReportAsync(reportId, cancellationToken);

        var mediaFiles = await _db.MediaFiles
            .AsNoTracking()
            .Where(m => distinctIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var existingIdentities = await _db.MediaIdentities
            .AsNoTracking()
            .Where(i => distinctIds.Contains(i.MediaFileId))
            .ToListAsync(cancellationToken);

        const string provider = "MusicBrainzLocal";
        var validations = new List<IdentityImportItemValidation>();
        int validCount = 0;
        int invalidCount = 0;

        foreach (var mediaId in distinctIds)
        {
            var exists = mediaFiles.TryGetValue(mediaId, out var mf);
            var title = mf?.Title ?? string.Empty;
            var artist = mf?.Artist ?? string.Empty;
            var album = mf?.Album;
            var duration = mf?.Duration.TotalSeconds ?? 0.0;
            var fp = mf != null ? ComputeFingerprint(title, artist, album, duration) : string.Empty;

            var localExists = existingIdentities.Any(i => i.MediaFileId == mediaId && i.Provider == provider);
            var manualExists = existingIdentities.Any(i => i.MediaFileId == mediaId &&
                (i.Status == "manual" || i.MatchMethod.Contains("manual", StringComparison.OrdinalIgnoreCase)));

            var inReport = lookup.TryGetValue(mediaId, out var candidate);
            var isApprovedInAudit = approvedWhitelist.Contains(mediaId);
            var mbid = candidate?.MatchedMbid ?? string.Empty;
            var conf = candidate?.Confidence ?? 0.0;

            string? errorMsg = null;
            if (!isApprovedInAudit)
            {
                errorMsg = $"MediaFile {mediaId} is not approved in audit manifest.";
            }
            else if (!inReport)
            {
                errorMsg = $"MediaFile {mediaId} is not in report's high-confidence candidate set.";
            }
            else if (!exists)
            {
                errorMsg = $"MediaFile with ID {mediaId} not found in database.";
            }
            else if (localExists)
            {
                errorMsg = $"MediaFile {mediaId} already has a '{provider}' identity.";
            }
            else if (manualExists)
            {
                errorMsg = $"MediaFile {mediaId} has a conflicting manual identity.";
            }
            else if (string.IsNullOrWhiteSpace(mbid))
            {
                errorMsg = $"Candidate for MediaFile {mediaId} has no MatchedMbid.";
            }
            else if (conf < 0.85)
            {
                errorMsg = $"Candidate confidence {conf} is below high-confidence threshold (0.85).";
            }

            if (errorMsg != null) invalidCount++;
            else validCount++;

            validations.Add(new IdentityImportItemValidation(
                mediaId,
                exists,
                localExists,
                manualExists,
                title,
                artist,
                album,
                duration,
                fp,
                mbid,
                conf,
                errorMsg
            ));
        }

        var previewResult = new IdentityImportPreviewResult(
            reportId,
            reportSha,
            auditSha,
            distinctIds.Count,
            validCount,
            invalidCount,
            validations
        );

        return new BatchVerificationContext(
            previewResult,
            reportSha,
            auditSha,
            lookup,
            mediaFiles,
            distinctIds
        );
    }

    public async Task<IdentityImportPreviewResult> PreviewBatchAsync(PreviewIdentityImportBatchRequest request, CancellationToken cancellationToken = default)
    {
        var context = await VerifyBatchCandidatesAsync(request.ReportId, request.ApprovedItemIds, cancellationToken);
        return context.PreviewResult;
    }

    public async Task<IdentityImportBatch> CreateDraftBatchAsync(CreateIdentityImportBatchRequest request, string username, CancellationToken cancellationToken = default)
    {
        // Single read & verification of report and audit manifest:
        var context = await VerifyBatchCandidatesAsync(request.ReportId, request.ApprovedItemIds, cancellationToken);
        if (context.PreviewResult.InvalidCount > 0)
        {
            var firstError = context.PreviewResult.Validations.First(v => v.ValidationErrorMessage != null);
            throw new InvalidOperationException($"Cannot create batch: validation failed for MediaFile {firstError.MediaFileId}: {firstError.ValidationErrorMessage}");
        }

        var batchTag = $"Batch-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8]}";

        var batch = new IdentityImportBatch
        {
            BatchTag = batchTag,
            SourceReportSha256 = context.ReportSha,
            AuditManifestSha256 = context.AuditSha,
            Provider = "MusicBrainzLocal", // Server enforced
            Status = "Draft",
            ItemCount = context.DistinctIds.Count,
            AppliedCount = 0,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = username
        };

        foreach (var mediaId in context.DistinctIds)
        {
            var mf = context.MediaFiles[mediaId];
            var candidate = context.Lookup[mediaId];
            var durationSec = mf.Duration.TotalSeconds;
            var fp = ComputeFingerprint(mf.Title, mf.Artist, mf.Album, durationSec);

            batch.Items.Add(new IdentityImportItem
            {
                MediaFileId = mediaId,
                TargetTitle = mf.Title,
                TargetArtist = mf.Artist,
                TargetAlbum = mf.Album,
                TargetDurationSeconds = durationSec,
                MetadataFingerprint = fp,
                MatchedRecordingId = candidate.MatchedMbid!,
                MatchedReleaseId = null,
                MatchedArtistId = null,
                MatchedTitle = candidate.MatchedTitle ?? candidate.Title,
                MatchedArtist = candidate.MatchedArtist ?? candidate.Artist,
                MatchedDurationSeconds = candidate.MatchedDurationSeconds,
                Confidence = candidate.Confidence,
                Status = "Pending"
            });
        }

        _db.IdentityImportBatches.Add(batch);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Identity import batch {BatchTag} created as Draft with {Count} items from report {ReportId} by {User}",
            batch.BatchTag, batch.ItemCount, request.ReportId, username);
        return batch;
    }

    public async Task<IdentityImportBatch> ApproveBatchAsync(int batchId, string username, CancellationToken cancellationToken = default)
    {
        var batch = await _db.IdentityImportBatches
            .Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

        if (batch == null)
            throw new KeyNotFoundException($"Identity import batch with ID {batchId} not found.");

        if (batch.Status != "Draft")
            throw new InvalidOperationException($"Only 'Draft' batches can be approved. Current status: '{batch.Status}'.");

        batch.Status = "Approved";
        batch.ApprovedBy = username;
        batch.ApprovedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Identity import batch {BatchTag} (ID {Id}) approved by {User}", batch.BatchTag, batch.Id, username);
        return batch;
    }

    public async Task<IdentityImportBatch> ApplyBatchAsync(int batchId, string username, CancellationToken cancellationToken = default)
    {
        var batch = await _db.IdentityImportBatches
            .Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

        if (batch == null)
            throw new KeyNotFoundException($"Identity import batch with ID {batchId} not found.");

        if (batch.Status != "Approved")
            throw new InvalidOperationException($"Only 'Approved' batches can be applied. Current status: '{batch.Status}'.");

        // Serializable transaction with 5s lock timeout and SHARE ROW EXCLUSIVE table lock
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        try
        {
            if (_db.Database.IsNpgsql())
            {
                await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s';", cancellationToken);
                await _db.Database.ExecuteSqlRawAsync("LOCK TABLE \"MediaIdentities\" IN SHARE ROW EXCLUSIVE MODE;", cancellationToken);
            }

            var fileIds = batch.Items.Select(i => i.MediaFileId).ToList();

            var currentFiles = await _db.MediaFiles
                .AsNoTracking()
                .Where(m => fileIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, cancellationToken);

            var existingIdentities = await _db.MediaIdentities
                .AsNoTracking()
                .Where(i => fileIds.Contains(i.MediaFileId))
                .ToListAsync(cancellationToken);

            var now = DateTime.UtcNow;
            int appliedCount = 0;

            foreach (var item in batch.Items)
            {
                if (!currentFiles.TryGetValue(item.MediaFileId, out var mf))
                {
                    throw new InvalidOperationException($"Apply aborted: MediaFile {item.MediaFileId} no longer exists.");
                }

                // Verify metadata fingerprint has not drifted (title, artist, album, duration)
                var currentFp = ComputeFingerprint(mf.Title, mf.Artist, mf.Album, mf.Duration.TotalSeconds);
                if (currentFp != item.MetadataFingerprint)
                {
                    throw new InvalidOperationException($"Apply aborted: MediaFile {item.MediaFileId} metadata fingerprint changed since audit (drift detected).");
                }

                // Verify no pre-existing provider identity
                if (existingIdentities.Any(i => i.MediaFileId == item.MediaFileId && i.Provider == batch.Provider))
                {
                    throw new InvalidOperationException($"Apply aborted: MediaFile {item.MediaFileId} already has a '{batch.Provider}' identity.");
                }

                // Verify no conflicting manual identity
                if (existingIdentities.Any(i => i.MediaFileId == item.MediaFileId &&
                    (i.Status == "manual" || i.MatchMethod.Contains("manual", StringComparison.OrdinalIgnoreCase))))
                {
                    throw new InvalidOperationException($"Apply aborted: MediaFile {item.MediaFileId} has a conflicting manual identity.");
                }

                // Pre-snapshot existing identities for audit
                var snapshots = existingIdentities
                    .Where(i => i.MediaFileId == item.MediaFileId)
                    .Select(i => new { i.Id, i.Provider, i.RecordingId, i.Status, i.MatchMethod })
                    .ToList();
                item.PreSnapshotJson = JsonSerializer.Serialize(snapshots);

                // Insert into MediaIdentities
                var identity = new MediaIdentity
                {
                    MediaFileId = item.MediaFileId,
                    Provider = batch.Provider,
                    RecordingId = item.MatchedRecordingId,
                    ReleaseId = item.MatchedReleaseId,
                    ArtistId = item.MatchedArtistId,
                    MatchMethod = batch.BatchTag,
                    Confidence = item.Confidence,
                    Status = "approved",
                    CoverStatus = "Pending",
                    LyricsStatus = "Pending",
                    MatchedAt = now,
                    LastVerifiedAt = now
                };

                _db.MediaIdentities.Add(identity);
                await _db.SaveChangesAsync(cancellationToken);

                item.CreatedIdentityId = identity.Id;
                item.Status = "Applied";
                item.ErrorDetail = null;
                appliedCount++;
            }

            if (appliedCount != batch.ItemCount)
            {
                throw new InvalidOperationException($"Apply aborted: Expected to apply {batch.ItemCount} items, but applied {appliedCount}.");
            }

            batch.AppliedCount = appliedCount;
            batch.Status = "Applied";
            batch.AppliedAt = now;
            batch.AppliedBy = username;

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("Identity import batch {BatchTag} applied successfully ({Count} items) by {User}", batch.BatchTag, appliedCount, username);
            return batch;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to apply identity import batch {BatchTag}", batch.BatchTag);
            throw;
        }
    }

    public async Task<IdentityImportBatch> RollbackBatchAsync(int batchId, string username, CancellationToken cancellationToken = default)
    {
        var batch = await _db.IdentityImportBatches
            .Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

        if (batch == null)
            throw new KeyNotFoundException($"Identity import batch with ID {batchId} not found.");

        if (batch.Status == "LegacyApplied" || batch.Status == "Immutable")
        {
            throw new InvalidOperationException($"Rollback aborted: Batch '{batch.BatchTag}' has status '{batch.Status}' and cannot be rolled back.");
        }

        if (batch.Status != "Applied")
            throw new InvalidOperationException($"Only 'Applied' batches can be rolled back. Current status: '{batch.Status}'.");

        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        try
        {
            if (_db.Database.IsNpgsql())
            {
                await _db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s';", cancellationToken);
                await _db.Database.ExecuteSqlRawAsync("LOCK TABLE \"MediaIdentities\" IN SHARE ROW EXCLUSIVE MODE;", cancellationToken);
            }

            var itemsWithIdentity = batch.Items
                .Where(i => i.CreatedIdentityId.HasValue)
                .ToList();

            if (itemsWithIdentity.Count != batch.AppliedCount)
            {
                throw new InvalidOperationException($"Rollback aborted: Found only {itemsWithIdentity.Count} created identity IDs, expected {batch.AppliedCount}.");
            }

            var itemByCreatedId = itemsWithIdentity.ToDictionary(i => i.CreatedIdentityId!.Value);
            var createdIds = itemByCreatedId.Keys.ToList();

            var identitiesToDelete = await _db.MediaIdentities
                .Where(i => createdIds.Contains(i.Id))
                .ToListAsync(cancellationToken);

            if (identitiesToDelete.Count != batch.AppliedCount)
            {
                throw new InvalidOperationException($"Rollback aborted: Matching identities in database is {identitiesToDelete.Count}, expected exactly {batch.AppliedCount}.");
            }

            // Strict Safety Assertions: Status=approved, recording/release/artist/confidence exact match, provider/batchTag match, cover/lyrics pending
            foreach (var iden in identitiesToDelete)
            {
                var item = itemByCreatedId[iden.Id];

                if (iden.Provider != batch.Provider)
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} provider mismatch ('{iden.Provider}' vs '{batch.Provider}').");
                }
                if (iden.MatchMethod != batch.BatchTag)
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} batch tag mismatch ('{iden.MatchMethod}' vs '{batch.BatchTag}').");
                }
                if (iden.Status != "approved")
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} status is '{iden.Status}' (expected 'approved'). Rollback refused to prevent overwriting manual review.");
                }
                if (iden.RecordingId != item.MatchedRecordingId)
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} RecordingId mismatch ('{iden.RecordingId}' vs '{item.MatchedRecordingId}').");
                }
                if (iden.ReleaseId != item.MatchedReleaseId)
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} ReleaseId mismatch ('{iden.ReleaseId}' vs '{item.MatchedReleaseId}').");
                }
                if (iden.ArtistId != item.MatchedArtistId)
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} ArtistId mismatch ('{iden.ArtistId}' vs '{item.MatchedArtistId}').");
                }
                if (Math.Abs(iden.Confidence - item.Confidence) > 0.0001)
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} Confidence mismatch ({iden.Confidence} vs {item.Confidence}).");
                }
                if (iden.CoverStatus != "Pending" || iden.LyricsStatus != "Pending")
                {
                    throw new InvalidOperationException($"Rollback aborted: Identity {iden.Id} resources have been modified (Cover: {iden.CoverStatus}, Lyrics: {iden.LyricsStatus}). Rollback refused to prevent resource inconsistency.");
                }
            }

            _db.MediaIdentities.RemoveRange(identitiesToDelete);

            foreach (var item in batch.Items)
            {
                item.Status = "RolledBack";
            }

            batch.Status = "RolledBack";
            batch.RolledBackAt = DateTime.UtcNow;
            batch.RolledBackBy = username;

            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation("Identity import batch {BatchTag} rolled back successfully ({Count} items) by {User}", batch.BatchTag, identitiesToDelete.Count, username);
            return batch;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogError(ex, "Failed to rollback identity import batch {BatchTag}", batch.BatchTag);
            throw;
        }
    }

    public async Task<IdentityImportBatch> BackfillLegacyPilotRound2Async(string username = "admin_pilot", CancellationToken cancellationToken = default)
    {
        const string batchTag = "LegacyPilotRound2_20260908";
        var existingBatch = await _db.IdentityImportBatches
            .Include(b => b.Items)
            .FirstOrDefaultAsync(b => b.BatchTag == batchTag, cancellationToken);
        if (existingBatch != null)
        {
            return existingBatch;
        }

        var pilotItemsData = new[]
        {
            (MediaId: 3, IdentityId: 20, Mbid: "d1b9c306-ea8c-4b1e-baca-24d93701e70d", Confidence: 1.0000, Title: "9 Crimes", Artist: "Damien Rice"),
            (MediaId: 5, IdentityId: 21, Mbid: "453f8ecf-e853-45ec-8335-d240a15cd75f", Confidence: 1.0000, Title: "Chasing Pavements", Artist: "Adele"),
            (MediaId: 12, IdentityId: 22, Mbid: "2e2e66bd-a016-4713-bd7f-dbb4037cc9b8", Confidence: 1.0000, Title: "I Don't Want to Miss a Thing", Artist: "Aerosmith"),
            (MediaId: 19, IdentityId: 23, Mbid: "ce7c1d28-b716-4e42-bd30-40612d6241f8", Confidence: 1.0000, Title: "Now You're Gone", Artist: "basshunter"),
            (MediaId: 36, IdentityId: 24, Mbid: "307ce9da-5690-4e21-ab71-9d12ea106e52", Confidence: 0.9950, Title: "Viva La Vida", Artist: "Coldplay"),
            (MediaId: 53, IdentityId: 25, Mbid: "b6175cb0-6730-4975-b66b-c38ff5d80db2", Confidence: 1.0000, Title: "Finally", Artist: "Fergie"),
            (MediaId: 55, IdentityId: 26, Mbid: "58558a25-f4a4-4c6f-a6e6-0d04b1a8419d", Confidence: 1.0000, Title: "big big world", Artist: "emilia"),
            (MediaId: 65, IdentityId: 27, Mbid: "1ec5f8bb-f073-46f1-95c0-f0dd0a1664b2", Confidence: 0.9950, Title: "1973", Artist: "James blunt"),
            (MediaId: 67, IdentityId: 28, Mbid: "8c8fa617-91ce-4872-a4ec-1d58d628af9a", Confidence: 1.0000, Title: "Beautiful Girl", Artist: "INXS"),
            (MediaId: 71, IdentityId: 29, Mbid: "b4c986df-547c-441c-b77d-55b88cc200ae", Confidence: 0.9950, Title: "You're Beautiful", Artist: "James Blunt")
        };

        var pilotIdentityIds = pilotItemsData.Select(p => p.IdentityId).ToList();
        var existingIdentities = await _db.MediaIdentities
            .Where(i => pilotIdentityIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        if (existingIdentities.Count != 10)
        {
            throw new InvalidOperationException($"Backfill aborted: Expected 10 existing MediaIdentities with IDs 20-29, but found {existingIdentities.Count}.");
        }

        // Strict verification of each pilot identity's fields to prevent registering wrong identities
        foreach (var p in pilotItemsData)
        {
            if (!existingIdentities.TryGetValue(p.IdentityId, out var iden))
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} does not exist in database.");
            }

            if (iden.MediaFileId != p.MediaId)
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} MediaFileId mismatch. Expected: {p.MediaId}, Found: {iden.MediaFileId}.");
            }

            if (iden.Provider != "MusicBrainzLocal")
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} Provider mismatch. Expected: 'MusicBrainzLocal', Found: '{iden.Provider}'.");
            }

            if (iden.MatchMethod != "AuditedPilotRound2_20260908")
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} MatchMethod mismatch. Expected: 'AuditedPilotRound2_20260908', Found: '{iden.MatchMethod}'.");
            }

            if (iden.RecordingId != p.Mbid)
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} RecordingId mismatch. Expected: '{p.Mbid}', Found: '{iden.RecordingId}'.");
            }

            if (Math.Abs(iden.Confidence - p.Confidence) > 0.0001)
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} Confidence mismatch. Expected: {p.Confidence}, Found: {iden.Confidence}.");
            }

            if (iden.Status != "approved")
            {
                throw new InvalidOperationException($"Backfill aborted: MediaIdentity ID {p.IdentityId} Status mismatch. Expected: 'approved', Found: '{iden.Status}'.");
            }
        }

        var mediaIds = pilotItemsData.Select(p => p.MediaId).ToList();
        var mediaFiles = await _db.MediaFiles
            .Where(m => mediaIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, cancellationToken);

        var pilotTime = new DateTime(2026, 9, 8, 21, 0, 0, DateTimeKind.Utc);
        var batch = new IdentityImportBatch
        {
            BatchTag = batchTag,
            SourceReportSha256 = "a71adbb59d7e8efe204329c56515604b919de968b371a86e9b9d123d88fc43e4",
            AuditManifestSha256 = "e072c6e4142e07126feb6fb0992b41a397378de5899ea0355564eebd36bfd92d",
            Provider = "MusicBrainzLocal",
            Status = "LegacyApplied", // Immutable
            ItemCount = 10,
            AppliedCount = 10,
            CreatedAt = pilotTime,
            CreatedBy = username,
            ApprovedAt = pilotTime,
            ApprovedBy = username,
            AppliedAt = pilotTime,
            AppliedBy = username
        };

        foreach (var p in pilotItemsData)
        {
            mediaFiles.TryGetValue(p.MediaId, out var mf);
            var title = mf?.Title ?? p.Title;
            var artist = mf?.Artist ?? p.Artist;
            var album = mf?.Album;
            var duration = mf?.Duration.TotalSeconds ?? 0.0;
            var fp = ComputeFingerprint(title, artist, album, duration);

            batch.Items.Add(new IdentityImportItem
            {
                MediaFileId = p.MediaId,
                TargetTitle = title,
                TargetArtist = artist,
                TargetAlbum = album,
                TargetDurationSeconds = duration,
                MetadataFingerprint = fp,
                MatchedRecordingId = p.Mbid,
                MatchedTitle = p.Title,
                MatchedArtist = p.Artist,
                MatchedDurationSeconds = duration > 0 ? duration : null,
                Confidence = p.Confidence,
                CreatedIdentityId = p.IdentityId,
                Status = "Applied"
            });
        }

        _db.IdentityImportBatches.Add(batch);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Legacy pilot batch {BatchTag} backfilled into ledger ({Count} items) with status LegacyApplied", batchTag, batch.ItemCount);
        return batch;
    }

    public async Task<List<IdentityImportBatch>> GetBatchesAsync(CancellationToken cancellationToken = default)
    {
        return await _db.IdentityImportBatches
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IdentityImportBatch?> GetBatchAsync(int batchId, CancellationToken cancellationToken = default)
    {
        return await _db.IdentityImportBatches
            .Include(b => b.Items)
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);
    }
}
