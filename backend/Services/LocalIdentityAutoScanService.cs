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
    string? MirrorVersion = null,
    bool ApplyMatchedIdentities = false);

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
    long ElapsedMs,
    string? CanonicalTitle = null,
    string? CanonicalArtist = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Genres = null,
    string? CanonicalUrl = null,
    string? SourcePayloadHash = null,
    string? PersistenceStatus = null);

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
    int IdentitiesCreated,
    int SignalsUpdated,
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
    private readonly IMusicBrainzCommunitySignalService? _communitySignals;
    private readonly ILogger<LocalIdentityAutoScanService> _logger;

    public LocalIdentityAutoScanService(
        AppDbContext db,
        ILocalMusicBrainzService localMusicBrainz,
        ILogger<LocalIdentityAutoScanService> logger,
        ILocalMusicBrainzDetailService? localMusicBrainzDetails = null,
        IMusicBrainzCommunitySignalService? communitySignals = null)
    {
        _db = db;
        _localMusicBrainz = localMusicBrainz;
        _localMusicBrainzDetails = localMusicBrainzDetails;
        _communitySignals = communitySignals;
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

        if (request.ApplyMatchedIdentities && (request.DryRun || !request.PersistState))
        {
            throw new ArgumentException("Applying matched identities requires a non-dry-run scan with persisted state.", nameof(request));
        }

        if (request.ApplyMatchedIdentities && (_localMusicBrainzDetails is null || _communitySignals is null))
        {
            throw new InvalidOperationException("Applying identities requires the local MusicBrainz detail and community signal services.");
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
                string? detailError = null;
                if (decision.Eligible && candidate is not null && _localMusicBrainzDetails is not null)
                {
                    try
                    {
                        details = await _localMusicBrainzDetails.GetRecordingAsync(candidate.RecordingId, cancellationToken);
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        detailError = ex.Message;
                        _logger.LogWarning(ex, "Local MusicBrainz detail snapshot failed for MediaFileId {MediaFileId}; identity candidate remains review-only.", media.Id);
                    }
                }

                var outcome = decision.Outcome;
                var reason = decision.Reason;
                if (request.ApplyMatchedIdentities && decision.Eligible && details is null)
                {
                    outcome = "Failed";
                    reason = detailError is null
                        ? "Local MusicBrainz detail snapshot was unavailable; identity was not persisted."
                        : $"Local MusicBrainz detail snapshot failed: {detailError}";
                }

                items.Add(new LocalIdentityAutoScanItem(
                    media.Id,
                    media.Title,
                    media.Artist,
                    media.Album,
                    decision.InputFingerprint,
                    outcome,
                    reason,
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
                    scan.ElapsedMs,
                    details?.Title,
                    details?.CanonicalArtist,
                    details?.Tags,
                    details?.Genres,
                    details?.CanonicalUrl,
                    details?.RawPayloadHash));
            }

            var identitiesCreated = 0;
            var signalsUpdated = 0;
            if (request.PersistState)
            {
                await using var writeTransaction = await _db.Database.BeginTransactionAsync(
                    System.Data.IsolationLevel.Serializable,
                    cancellationToken);

                if (_db.Database.IsNpgsql())
                {
                    await _db.Database.ExecuteSqlRawAsync(
                        "LOCK TABLE \"MediaIdentities\" IN SHARE ROW EXCLUSIVE MODE;",
                        cancellationToken);
                }

                items = await RevalidateForPersistenceAsync(items, cancellationToken);
                items = items.Select(item => item with
                {
                    PersistenceStatus = item.PersistenceStatus ?? (request.ApplyMatchedIdentities && item.Outcome == "Matched"
                        ? "IdentityAndSignalsApplied"
                        : "ScanStatePersisted")
                }).ToList();
                var transactionalReportHash = ComputeResultHash(items);
                await PersistStateAsync(items, request, transactionalReportHash, cancellationToken);

                if (request.ApplyMatchedIdentities)
                {
                    (identitiesCreated, signalsUpdated) = await PersistMatchedIdentitiesAsync(items, cancellationToken);
                }

                await writeTransaction.CommitAsync(cancellationToken);
            }

            var inputHash = ComputeInputHash(items);
            var reportHash = ComputeResultHash(items);

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
                identitiesCreated,
                signalsUpdated,
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

    private async Task<List<LocalIdentityAutoScanItem>> RevalidateForPersistenceAsync(
        IReadOnlyList<LocalIdentityAutoScanItem> items,
        CancellationToken cancellationToken)
    {
        var ids = items.Select(item => item.MediaFileId).ToList();
        var mediaById = await _db.MediaFiles
            .Where(media => ids.Contains(media.Id))
            .ToDictionaryAsync(media => media.Id, cancellationToken);
        var mediaIdsWithIdentity = await _db.MediaIdentities
            .Where(identity => ids.Contains(identity.MediaFileId))
            .Select(identity => identity.MediaFileId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var identitySet = mediaIdsWithIdentity.ToHashSet();

        return items.Select(item =>
        {
            if (!mediaById.TryGetValue(item.MediaFileId, out var media))
            {
                return item with { Outcome = "Failed", Reason = "Media file disappeared before persistence.", PersistenceStatus = "RejectedMissingMedia" };
            }

            var currentFingerprint = LocalIdentityAutoEligibilityPolicy.ComputeInputFingerprint(media);
            if (!string.Equals(currentFingerprint, item.InputFingerprint, StringComparison.Ordinal))
            {
                return item with { Outcome = "Failed", Reason = "Media metadata changed during the scan; retry with the new fingerprint.", PersistenceStatus = "RejectedFingerprintDrift" };
            }

            if (identitySet.Contains(item.MediaFileId))
            {
                return item with { Outcome = "Skipped", Reason = "An identity already exists for this media file.", PersistenceStatus = "SkippedExistingIdentity" };
            }

            return item;
        }).ToList();
    }

    private async Task<(int IdentitiesCreated, int SignalsUpdated)> PersistMatchedIdentitiesAsync(
        IReadOnlyCollection<LocalIdentityAutoScanItem> items,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var signals = 0;

        foreach (var item in items.Where(item => item.Outcome == "Matched"))
        {
            if (string.IsNullOrWhiteSpace(item.RecordingId) || string.IsNullOrWhiteSpace(item.CanonicalTitle))
            {
                throw new InvalidOperationException($"Matched MediaFileId {item.MediaFileId} is missing its verified MusicBrainz detail snapshot.");
            }

            var identity = new MediaIdentity
            {
                MediaFileId = item.MediaFileId,
                Provider = "MusicBrainzLocal",
                RecordingId = item.RecordingId,
                ReleaseId = item.ReleaseId ?? item.ReleaseIds?.FirstOrDefault(),
                ArtistId = item.ArtistId,
                ISRC = item.Isrcs?.FirstOrDefault(),
                MatchMethod = LocalIdentityAutoEligibilityPolicy.Version,
                Confidence = item.Confidence ?? 0,
                Status = "approved",
                CoverStatus = "Pending",
                LyricsStatus = "Pending",
                MatchedAt = DateTime.UtcNow,
                LastVerifiedAt = DateTime.UtcNow
            };
            _db.MediaIdentities.Add(identity);
            await _db.SaveChangesAsync(cancellationToken);

            var details = new LocalMusicBrainzRecordingDetails(
                item.RecordingId,
                item.CanonicalTitle,
                item.Isrcs ?? Array.Empty<string>(),
                item.MbRating,
                item.MbRatingCount,
                item.CanonicalUrl,
                item.SourcePayloadHash,
                item.CanonicalArtist,
                item.DurationSeconds,
                item.ReleaseIds,
                item.ReleaseGroupIds,
                item.Tags,
                item.Genres);
            await _communitySignals!.UpsertAsync(identity, details, cancellationToken);
            created++;
            signals++;
        }

        return (created, signals);
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
            i.MbCommunityScore,
            i.CanonicalTitle,
            i.CanonicalArtist,
            i.Tags,
            i.Genres,
            i.CanonicalUrl,
            i.SourcePayloadHash,
            i.PersistenceStatus
        });
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
