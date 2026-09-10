using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public sealed record ExternalSignalRefreshRequest(
    string Provider,
    int Count = 100,
    int? AfterMediaFileId = null,
    bool Force = false,
    bool DryRun = false);

public sealed record ExternalSignalRefreshItem(
    int MediaFileId,
    string Mbid,
    string Status,
    string? Message);

public sealed record ExternalSignalRefreshReport(
    string Provider,
    int RequestedCount,
    int Evaluated,
    int Updated,
    int Skipped,
    int Failed,
    int? ContinuationAfterMediaFileId,
    bool DryRun,
    IReadOnlyList<ExternalSignalRefreshItem> Items,
    bool Aborted = false,
    string? StopReason = null);

public interface IExternalSignalRefreshService
{
    Task<ExternalSignalRefreshReport> RefreshAsync(ExternalSignalRefreshRequest request, CancellationToken cancellationToken = default);
}

public sealed class ExternalSignalRefreshService : IExternalSignalRefreshService
{
    private static readonly SemaphoreSlim ProcessLock = new(1, 1);

    private readonly AppDbContext _db;
    private readonly ILocalMusicBrainzDetailService? _mbDetails;
    private readonly IMusicBrainzCommunitySignalService? _mbSignalService;
    private readonly ILastFmTrackInfoClient? _lastFmClient;
    private readonly ILastFmGlobalPopularityService? _lastFmPopularityService;
    private readonly ILogger<ExternalSignalRefreshService> _logger;

    public ExternalSignalRefreshService(
        AppDbContext db,
        ILocalMusicBrainzDetailService? mbDetails,
        IMusicBrainzCommunitySignalService? mbSignalService,
        ILastFmTrackInfoClient? lastFmClient,
        ILastFmGlobalPopularityService? lastFmPopularityService,
        ILogger<ExternalSignalRefreshService> logger)
    {
        _db = db;
        _mbDetails = mbDetails;
        _mbSignalService = mbSignalService;
        _lastFmClient = lastFmClient;
        _lastFmPopularityService = lastFmPopularityService;
        _logger = logger;
    }

    public async Task<ExternalSignalRefreshReport> RefreshAsync(ExternalSignalRefreshRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Count <= 0 || request.Count > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Count), request.Count, "Count must be between 1 and 500.");
        }

        var provider = request.Provider.Trim();
        if (!provider.Equals("MusicBrainz", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("LastFm", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unsupported provider '{request.Provider}'. Must be 'MusicBrainz' or 'LastFm'.", nameof(request));
        }

        provider = provider.Equals("MusicBrainz", StringComparison.OrdinalIgnoreCase) ? "MusicBrainz" : "LastFm";

        if (!await ProcessLock.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException("An external signal refresh is already in progress.");
        }

        IDbContextTransaction? readOnlyTransaction = null;
        try
        {
            if (request.DryRun && _db.Database.IsNpgsql())
            {
                readOnlyTransaction = await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
                await _db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY;", cancellationToken);
            }

            var now = DateTime.UtcNow;
            var currentCursor = request.AfterMediaFileId ?? 0;
            int? lastCompletedId = request.AfterMediaFileId;
            bool aborted = false;
            string? stopReason = null;

            // Fetch distinct MediaFileIds > currentCursor with approved MusicBrainz identities
            var mediaFileIds = await _db.MediaIdentities
                .AsNoTracking()
                .Where(i => i.MediaFileId > currentCursor &&
                    !string.IsNullOrEmpty(i.RecordingId) &&
                    (i.Provider == "MusicBrainz" || i.Provider == "MusicBrainzLocal") &&
                    i.Status == "approved")
                .Select(i => i.MediaFileId)
                .Distinct()
                .OrderBy(id => id)
                .Take(request.Count)
                .ToListAsync(cancellationToken);

            var allIdentities = await _db.MediaIdentities
                .AsNoTracking()
                .Include(i => i.MediaFile)
                .Where(i => mediaFileIds.Contains(i.MediaFileId) &&
                    !string.IsNullOrEmpty(i.RecordingId) &&
                    (i.Provider == "MusicBrainz" || i.Provider == "MusicBrainzLocal") &&
                    i.Status == "approved")
                .ToListAsync(cancellationToken);

            var identitiesByMedia = allIdentities.GroupBy(i => i.MediaFileId).ToDictionary(g => g.Key, g => g.ToList());

            var items = new List<ExternalSignalRefreshItem>(mediaFileIds.Count);
            var updated = 0;
            var skipped = 0;
            var failed = 0;

            foreach (var mediaFileId in mediaFileIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var group = identitiesByMedia[mediaFileId];
                var distinctMbids = group
                    .Select(i => i.RecordingId!.Trim().ToLowerInvariant())
                    .Distinct()
                    .ToList();

                // Identity conflict check: if the same media file has approved identities with differing MBIDs
                if (distinctMbids.Count > 1)
                {
                    failed++;
                    items.Add(new ExternalSignalRefreshItem(
                        mediaFileId,
                        "CONFLICT",
                        "IdentityConflict",
                        $"Multiple conflicting approved MusicBrainz recording IDs detected: {string.Join(", ", distinctMbids)}. Manual review required."));
                    lastCompletedId = mediaFileId;
                    continue;
                }

                var canonicalIdentity = group.FirstOrDefault(i => i.Provider == "MusicBrainzLocal") ?? group.First();
                var recordingId = canonicalIdentity.RecordingId!;
                var signalKey = provider == "MusicBrainz" ? "CommunityScore" : "GlobalPopularity";

                var existingSignal = await _db.MediaExternalSignals
                    .Include(s => s.ExternalReference)
                    .FirstOrDefaultAsync(s => s.ExternalReference!.MediaFileId == mediaFileId &&
                        s.ExternalReference.Provider == provider &&
                        s.SignalKey == signalKey, cancellationToken);

                if (!request.Force && existingSignal is not null && existingSignal.RefreshAfter.HasValue && existingSignal.RefreshAfter.Value > now)
                {
                    skipped++;
                    items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Skipped",
                        $"Signal is current until {existingSignal.RefreshAfter.Value:u}."));
                    lastCompletedId = mediaFileId;
                    continue;
                }

                if (provider == "MusicBrainz")
                {
                    if (_mbDetails is null || (!request.DryRun && _mbSignalService is null))
                    {
                        failed++;
                        if (!request.DryRun)
                        {
                            await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, "MusicBrainz detail or signal service is not registered.", now, cancellationToken);
                        }
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Failed",
                            "MusicBrainz detail or signal service is not registered."));
                        lastCompletedId = mediaFileId;
                        continue;
                    }

                    try
                    {
                        var details = await _mbDetails.GetRecordingAsync(recordingId, cancellationToken);
                        if (details is null)
                        {
                            failed++;
                            if (!request.DryRun)
                            {
                                await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, "Local MusicBrainz returned no details for recording.", now, cancellationToken);
                            }
                            items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Failed",
                                "Local MusicBrainz returned no details for recording."));
                            lastCompletedId = mediaFileId;
                            continue;
                        }

                        if (request.DryRun)
                        {
                            updated++;
                            items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Planned",
                                $"Dry-run: candidate is eligible for {provider} signal refresh."));
                            lastCompletedId = mediaFileId;
                            continue;
                        }

                        await _mbSignalService!.UpsertAsync(canonicalIdentity, details, cancellationToken);
                        updated++;
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Updated", null));
                        lastCompletedId = mediaFileId;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (request.DryRun && ex is Npgsql.PostgresException) throw;
                        failed++;
                        _logger.LogWarning(ex, "Failed to refresh MusicBrainz signals for MediaFileId {Id}", mediaFileId);
                        if (!request.DryRun)
                        {
                            await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, ex.Message, now, cancellationToken);
                        }
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Failed", ex.Message));
                        lastCompletedId = mediaFileId;
                    }
                }
                else // LastFm
                {
                    if (_lastFmClient is null || (!request.DryRun && _lastFmPopularityService is null))
                    {
                        failed++;
                        if (!request.DryRun)
                        {
                            await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, "Last.fm client or popularity service is not registered.", now, cancellationToken);
                        }
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Failed",
                            "Last.fm client or popularity service is not registered."));
                        lastCompletedId = mediaFileId;
                        continue;
                    }

                    try
                    {
                        var trackInfo = await _lastFmClient.GetTrackByMbidAsync(recordingId, cancellationToken);
                        if (trackInfo is null)
                        {
                            failed++;
                            if (!request.DryRun)
                            {
                                await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, "Last.fm lookup returned no data.", now, cancellationToken);
                            }
                            items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Failed",
                                "Last.fm lookup returned no data."));
                            lastCompletedId = mediaFileId;
                            continue;
                        }

                        if (request.DryRun)
                        {
                            updated++;
                            items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Planned",
                                $"Dry-run: candidate is eligible for {provider} signal refresh."));
                            lastCompletedId = mediaFileId;
                            continue;
                        }

                        await _lastFmPopularityService!.UpsertAsync(mediaFileId, trackInfo, cancellationToken);
                        updated++;
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Updated", null));
                        lastCompletedId = mediaFileId;
                    }
                    catch (LastFmRateLimitException ex)
                    {
                        failed++;
                        aborted = true;
                        stopReason = $"RateLimited: {ex.Message}";
                        _logger.LogWarning(ex, "Last.fm rate limit reached during signal refresh: {Message}", ex.Message);
                        if (!request.DryRun)
                        {
                            await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, ex.Message, now, cancellationToken, retryAfter: ex.RetryAfter, status: "RateLimited");
                        }
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "RateLimited", ex.Message));
                        // Do NOT advance lastCompletedId: continuation cursor stays at previous finished record
                        break;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (request.DryRun && ex is Npgsql.PostgresException) throw;
                        failed++;
                        _logger.LogWarning(ex, "Failed to refresh Last.fm popularity for MediaFileId {Id}", mediaFileId);
                        if (!request.DryRun)
                        {
                            await RecordFailureAsync(mediaFileId, canonicalIdentity, provider, signalKey, ex.Message, now, cancellationToken);
                        }
                        items.Add(new ExternalSignalRefreshItem(mediaFileId, recordingId, "Failed", ex.Message));
                        lastCompletedId = mediaFileId;
                    }
                }
            }

            if (readOnlyTransaction is not null)
            {
                await readOnlyTransaction.RollbackAsync(cancellationToken);
            }

            return new ExternalSignalRefreshReport(
                provider,
                request.Count,
                items.Count,
                updated,
                skipped,
                failed,
                lastCompletedId,
                request.DryRun,
                items,
                aborted,
                stopReason);
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

    private async Task RecordFailureAsync(
        int mediaFileId,
        MediaIdentity canonicalIdentity,
        string provider,
        string signalKey,
        string errorMessage,
        DateTime now,
        CancellationToken cancellationToken,
        TimeSpan? retryAfter = null,
        string status = "failed")
    {
        var subjectType = provider == "MusicBrainz" ? "Recording" : "Track";
        var reference = await _db.MediaExternalReferences
            .Include(r => r.Signals)
            .SingleOrDefaultAsync(r => r.MediaFileId == mediaFileId && r.Provider == provider && r.SubjectType == subjectType, cancellationToken);

        if (reference is null)
        {
            reference = new MediaExternalReference
            {
                MediaFileId = mediaFileId,
                Provider = provider,
                SubjectType = subjectType,
                ExternalId = canonicalIdentity.RecordingId!,
                MatchMethod = canonicalIdentity.MatchMethod ?? "MusicBrainzMbid:v1",
                MatchConfidence = canonicalIdentity.Confidence,
                Status = status,
                VerifiedAt = now
            };
            _db.MediaExternalReferences.Add(reference);
        }
        else
        {
            reference.Status = status;
        }

        var signal = reference.Signals.SingleOrDefault(s => s.SignalKey == signalKey);
        if (signal is null)
        {
            signal = new MediaExternalSignal
            {
                SignalKey = signalKey,
                AlgorithmVersion = provider == "MusicBrainz" ? MusicBrainzCommunitySignalService.AlgorithmVersion : LastFmGlobalPopularityService.AlgorithmVersion
            };
            reference.Signals.Add(signal);
        }

        signal.Status = status;
        signal.LastError = errorMessage;
        signal.RefreshAfter = now.Add(retryAfter ?? (provider == "MusicBrainz" ? TimeSpan.FromHours(6) : TimeSpan.FromHours(12)));
        signal.ObservedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
