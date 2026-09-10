using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public enum LocalIdentityScanMode
{
    Full,
    Incremental
}

public sealed record LocalIdentityAutoScanRequest(
    LocalIdentityScanMode Mode = LocalIdentityScanMode.Full,
    int MaxItems = 100,
    int? AfterMediaFileId = null,
    bool DryRun = true,
    bool PersistState = false,
    string? MirrorVersion = null);

public sealed record LocalIdentityAutoScanItem(
    int MediaFileId,
    string Title,
    string Artist,
    string? Album,
    string InputFingerprint,
    string Outcome,
    string Reason,
    double? Confidence,
    string? RecordingId,
    string? ReleaseId,
    string? ArtistId,
    IReadOnlyList<string>? Isrcs,
    IReadOnlyList<string>? ReleaseIds,
    IReadOnlyList<string>? ReleaseGroupIds,
    double? MbRating,
    long? MbRatingCount,
    double? MbCommunityScore,
    double? DurationSeconds,
    long ElapsedMs);

public sealed record LocalIdentityAutoScanReport(
    string Mode,
    string PolicyVersion,
    string? MirrorVersion,
    string MirrorUrl,
    bool DryRun,
    int MaxItems,
    int Evaluated,
    int Matched,
    int Unmatched,
    int Skipped,
    int Failed,
    int? ContinuationAfterMediaFileId,
    string InputSha256,
    string ResultSha256,
    IReadOnlyList<LocalIdentityAutoScanItem> Items);

public interface ILocalIdentityAutoScanService
{
    Task<LocalIdentityAutoScanReport> ScanAsync(LocalIdentityAutoScanRequest request, CancellationToken cancellationToken = default);
}

public sealed class LocalIdentityAutoScanService : ILocalIdentityAutoScanService
{
    private static readonly SemaphoreSlim ProcessLock = new(1, 1);

    private readonly AppDbContext _db;
    private readonly ILocalMusicBrainzService _localMusicBrainz;
    private readonly ILocalMusicBrainzDetailService? _localMusicBrainzDetails;
    private readonly ILogger<LocalIdentityAutoScanService> _logger;

    public LocalIdentityAutoScanService(
        AppDbContext db,
        ILocalMusicBrainzService localMusicBrainz,
        ILogger<LocalIdentityAutoScanService> logger,
        ILocalMusicBrainzDetailService? localMusicBrainzDetails = null)
    {
        _db = db;
        _localMusicBrainz = localMusicBrainz;
        _localMusicBrainzDetails = localMusicBrainzDetails;
        _logger = logger;
    }

    public async Task<LocalIdentityAutoScanReport> ScanAsync(LocalIdentityAutoScanRequest request, CancellationToken cancellationToken = default)
    {
        if (request.MaxItems <= 0 || request.MaxItems > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request.MaxItems), request.MaxItems, "MaxItems must be between 1 and 100.");
        }

        if (request.DryRun && request.PersistState)
        {
            throw new ArgumentException("Dry-run scans cannot persist scan state.", nameof(request));
        }

        if (request.PersistState && string.IsNullOrWhiteSpace(request.MirrorVersion))
        {
            throw new InvalidOperationException("Persisting scan state requires a non-empty MirrorVersion.");
        }

        if (!await ProcessLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("A local identity auto scan is already in progress.");
        }

        IDbContextTransaction? readOnlyTransaction = null;
        try
        {
            if (request.DryRun && _db.Database.IsNpgsql())
            {
                readOnlyTransaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await _db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }

            var (candidates, highestEvaluatedId) = await SelectCandidatesAsync(request, cancellationToken);
            var items = new List<LocalIdentityAutoScanItem>(candidates.Count);

            foreach (var media in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Keep rejected source metadata in the report/state so an ID
                // cursor always advances, but never waste a mirror request on it.
                var scan = !LocalIdentityAutoEligibilityPolicy.HasUsableMetadata(media)
                    ? new LocalMusicBrainzScanResult(false, null, 200, "Title or artist is empty/unknown.")
                    : LocalIdentityAutoEligibilityPolicy.ContainsVersionOrDerivativeToken(media.Title)
                        ? new LocalMusicBrainzScanResult(false, null, 200, "Source metadata declares a version or derivative.")
                        : await _localMusicBrainz.ScanMediaIdentityAsync(media, cancellationToken);
                var decision = LocalIdentityAutoEligibilityPolicy.Evaluate(media, scan);
                var candidate = scan.BestCandidate;
                LocalMusicBrainzRecordingDetails? details = null;
                if (decision.Eligible && candidate is not null && _localMusicBrainzDetails is not null)
                {
                    try
                    {
                        details = await _localMusicBrainzDetails.GetRecordingAsync(candidate.RecordingId, cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Local MusicBrainz detail snapshot failed for MediaFileId {MediaFileId}; identity candidate remains review-only.", media.Id);
                    }
                }

                items.Add(new LocalIdentityAutoScanItem(
                    media.Id,
                    media.Title,
                    media.Artist,
                    media.Album,
                    decision.InputFingerprint,
                    decision.Outcome,
                    decision.Reason,
                    candidate?.Confidence,
                    decision.Eligible ? candidate?.RecordingId : null,
                    decision.Eligible ? candidate?.ReleaseId : null,
                    decision.Eligible ? candidate?.ArtistId : null,
                    details?.Isrcs,
                    details?.ReleaseIds,
                    details?.ReleaseGroupIds,
                    details?.Rating,
                    details?.RatingCount,
                    MusicBrainzCommunitySignalService.ComputeCommunityScore(details?.Rating, details?.RatingCount),
                    candidate?.MatchedDuration.TotalSeconds,
                    scan.ElapsedMs));
            }

            var inputHash = ComputeInputHash(items);
            var reportHash = ComputeResultHash(items);
            if (request.PersistState)
            {
                await PersistStateAsync(items, request, reportHash, cancellationToken);
            }

            var report = new LocalIdentityAutoScanReport(
                request.Mode.ToString(),
                LocalIdentityAutoEligibilityPolicy.Version,
                request.MirrorVersion,
                _localMusicBrainz.BaseUrl,
                request.DryRun,
                request.MaxItems,
                items.Count,
                items.Count(i => i.Outcome == "Matched"),
                items.Count(i => i.Outcome == "Unmatched"),
                items.Count(i => i.Outcome == "Skipped"),
                items.Count(i => i.Outcome == "Failed"),
                highestEvaluatedId,
                inputHash,
                reportHash,
                items);

            if (readOnlyTransaction is not null)
            {
                await readOnlyTransaction.RollbackAsync(cancellationToken);
            }

            return report;
        }
        finally
        {
            if (readOnlyTransaction is not null)
            {
                await readOnlyTransaction.DisposeAsync();
            }
            ProcessLock.Release();
        }
    }

    private async Task<(List<MediaFile> Candidates, int? HighestEvaluatedId)> SelectCandidatesAsync(
        LocalIdentityAutoScanRequest request,
        CancellationToken cancellationToken)
    {
        var candidates = new List<MediaFile>();
        var currentCursor = request.AfterMediaFileId ?? 0;
        int? highestEvaluatedId = request.AfterMediaFileId;
        var now = DateTime.UtcNow;

        while (candidates.Count < request.MaxItems)
        {
            var remaining = request.MaxItems - candidates.Count;
            var fetchSize = request.Mode == LocalIdentityScanMode.Full
                ? remaining
                : Math.Min(Math.Max(remaining * 2, 200), 500);

            var raw = await _db.MediaFiles
                .AsNoTracking()
                .Where(m => m.Id > currentCursor)
                .Where(m => !string.IsNullOrWhiteSpace(m.Title) && !string.IsNullOrWhiteSpace(m.Artist))
                .Where(m => !_db.MediaIdentities.Any(identity => identity.MediaFileId == m.Id))
                .OrderBy(m => m.Id)
                .Take(fetchSize)
                .ToListAsync(cancellationToken);

            if (raw.Count == 0)
            {
                break;
            }

            if (request.Mode == LocalIdentityScanMode.Full)
            {
                candidates.AddRange(raw);
                highestEvaluatedId = raw.Last().Id;
                currentCursor = highestEvaluatedId.Value;
            }
            else // Incremental
            {
                var ids = raw.Select(m => m.Id).ToList();
                var states = await _db.MediaIdentityScanStates.AsNoTracking()
                    .Where(s => ids.Contains(s.MediaFileId))
                    .ToDictionaryAsync(s => s.MediaFileId, cancellationToken);

                foreach (var media in raw)
                {
                    highestEvaluatedId = media.Id;
                    currentCursor = media.Id;

                    var fingerprint = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(media);
                    var needsScan = !states.TryGetValue(media.Id, out var state) ||
                                    !string.Equals(state.InputFingerprint, fingerprint, StringComparison.Ordinal) ||
                                    !string.Equals(state.PolicyVersion, LocalIdentityAutoEligibilityPolicy.Version, StringComparison.Ordinal) ||
                                    (!string.IsNullOrWhiteSpace(request.MirrorVersion) && !string.Equals(state.MirrorVersion, request.MirrorVersion, StringComparison.Ordinal)) ||
                                    (state.RetryAfter.HasValue && state.RetryAfter <= now);

                    if (needsScan)
                    {
                        candidates.Add(media);
                        if (candidates.Count >= request.MaxItems)
                        {
                            break;
                        }
                    }
                }
            }

            if (candidates.Count >= request.MaxItems)
            {
                break;
            }
        }

        return (candidates, highestEvaluatedId);
    }

    private async Task PersistStateAsync(
        IReadOnlyCollection<LocalIdentityAutoScanItem> items,
        LocalIdentityAutoScanRequest request,
        string reportHash,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var state = await _db.MediaIdentityScanStates
                .SingleOrDefaultAsync(s => s.MediaFileId == item.MediaFileId, cancellationToken);

            if (state is null)
            {
                state = new MediaIdentityScanState { MediaFileId = item.MediaFileId };
                _db.MediaIdentityScanStates.Add(state);
            }

            state.InputFingerprint = item.InputFingerprint;
            state.Outcome = item.Outcome;
            state.Confidence = item.Confidence;
            state.RecordingId = item.RecordingId;
            state.PolicyVersion = LocalIdentityAutoEligibilityPolicy.Version;
            state.MirrorVersion = request.MirrorVersion;
            state.LastReportSha256 = reportHash;
            state.LastScannedAt = DateTime.UtcNow;
            state.AttemptCount++;
            state.LastError = item.Outcome == "Failed" ? item.Reason : null;
            state.RetryAfter = item.Outcome switch
            {
                "Failed" => DateTime.UtcNow.AddHours(6),
                "Unmatched" => DateTime.UtcNow.AddDays(14),
                "Skipped" => DateTime.UtcNow.AddDays(30),
                _ => null
            };
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is not null)
        {
            _logger.LogWarning(ex, "Concurrent scan-state update was rejected by the unique MediaFileId constraint.");
            throw new InvalidOperationException("Concurrent local identity scan state update detected; retry the bounded scan.", ex);
        }
    }

    private static string ComputeInputHash(IEnumerable<LocalIdentityAutoScanItem> items)
    {
        var canonical = items.OrderBy(i => i.MediaFileId).Select(i => new
        {
            i.MediaFileId,
            i.Title,
            i.Artist,
            i.Album,
            i.InputFingerprint
        });
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string ComputeResultHash(IEnumerable<LocalIdentityAutoScanItem> items)
    {
        var canonical = items.OrderBy(i => i.MediaFileId).Select(i => new
        {
            i.MediaFileId,
            i.InputFingerprint,
            i.Outcome,
            i.RecordingId,
            i.Confidence,
            i.Isrcs,
            i.ReleaseIds,
            i.ReleaseGroupIds,
            i.MbRating,
            i.MbRatingCount,
            i.MbCommunityScore
        });
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
