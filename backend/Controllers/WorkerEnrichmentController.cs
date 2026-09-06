using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;

namespace WebMusic.Backend.Controllers;

[ApiController]
[Route("api/enrichment/worker")]
[Authorize(Roles = "Worker")]
public class WorkerEnrichmentController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IWebHostEnvironment _environment;

    public const int MaxBatchSize = 100;
    public const int DefaultBatchSize = 20;

    // Provider Quota Definitions
    public const int MusicBrainzDailyLimit = 2000;
    public const int MusicBrainzWorstCaseUnits = 2; // 1 search + 1 release detail / retry

    public const int CoverArtArchiveDailyLimit = 2000;
    public const int CoverArtArchiveWorstCaseUnits = 1; // 1 fetch per track needing cover

    public const int LrclibDailyLimit = 5000;
    public const int LrclibWorstCaseUnits = 1; // 1 query per track needing lyrics

    public WorkerEnrichmentController(AppDbContext context, IWebHostEnvironment environment)
    {
        _context = context;
        _environment = environment;
    }

    [HttpGet("preview")]
    public async Task<IActionResult> PreviewCatalog()
    {
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var ledgers = await _context.ProviderQuotaLedgers
            .Where(l => l.Date == todayStr)
            .ToListAsync();

        var mbLedger = ledgers.FirstOrDefault(l => l.Provider == "MusicBrainz");
        var caaLedger = ledgers.FirstOrDefault(l => l.Provider == "CoverArtArchive");
        var lrcLedger = ledgers.FirstOrDefault(l => l.Provider == "LRCLIB");

        var mbDailyLimit = mbLedger?.DailyLimit ?? MusicBrainzDailyLimit;
        var mbReserved = mbLedger?.ReservedUnits ?? 0;
        var mbConsumed = mbLedger?.ConsumedUnits ?? 0;

        var caaDailyLimit = caaLedger?.DailyLimit ?? CoverArtArchiveDailyLimit;
        var caaReserved = caaLedger?.ReservedUnits ?? 0;
        var caaConsumed = caaLedger?.ConsumedUnits ?? 0;

        var lrcDailyLimit = lrcLedger?.DailyLimit ?? LrclibDailyLimit;
        var lrcReserved = lrcLedger?.ReservedUnits ?? 0;
        var lrcConsumed = lrcLedger?.ConsumedUnits ?? 0;

        var cutoff30d = DateTime.UtcNow.AddDays(-30);

        // Fetch active cooldown fingerprints to allow bypass when metadata changes
        var activeCooldowns = await _context.EnrichmentAttempts
            .Where(a => a.RetryAfter != null && a.RetryAfter > DateTime.UtcNow && a.InputFingerprint != null)
            .Select(a => new { a.MediaFileId, a.InputFingerprint })
            .ToListAsync();
        var cooldownLookup = activeCooldowns
            .GroupBy(a => a.MediaFileId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.InputFingerprint!).ToHashSet());

        var candidatesBase = await _context.MediaFiles
            .Where(m => !string.IsNullOrEmpty(m.Title) && !string.IsNullOrEmpty(m.Artist)
                     && !m.Title.StartsWith("Unknown") && !m.Artist.StartsWith("Unknown")
                     && (string.IsNullOrEmpty(m.CoverArt) || !_context.Lyrics.Any(l => l.MediaFileId == m.Id))
                     && !_context.EnrichmentJobItems.Any(i => i.MediaFileId == m.Id && ((i.Status == "Leased" || i.Status == "AwaitingAssets") && i.LeaseExpiresAt > DateTime.UtcNow)))
            .Select(m => new
            {
                Media = m,
                IsFav = m.Favorites.Any(),
                PlayCount = _context.PlayHistories.Count(p => p.MediaFileId == m.Id),
                RecentPlay = _context.PlayHistories.Any(p => p.MediaFileId == m.Id && p.PlayedAt >= cutoff30d)
            })
            .ToListAsync();

        var eligibleCandidates = candidatesBase.Where(c =>
        {
            if (cooldownLookup.TryGetValue(c.Media.Id, out var fps))
            {
                var currentFp = MusicEnrichmentService.ComputeFingerprint(c.Media.Title, c.Media.Artist, c.Media.Album);
                if (fps.Contains(currentFp)) return false;
            }
            return true;
        }).ToList();

        var totalEligible = eligibleCandidates.Count;

        var sampleTracks = eligibleCandidates
            .OrderByDescending(x => (x.IsFav ? 1000 : 0) + (x.RecentPlay ? 200 : 0) + Math.Min(x.PlayCount * 10, 500))
            .ThenBy(x => x.Media.Id)
            .Take(10)
            .Select(x => new
            {
                x.Media.Id,
                x.Media.Title,
                x.Media.Artist,
                x.Media.Album,
                NeedsCover = string.IsNullOrEmpty(x.Media.CoverArt),
                NeedsLyrics = !_context.Lyrics.Any(l => l.MediaFileId == x.Media.Id),
                Score = (x.IsFav ? 1000 : 0) + (x.RecentPlay ? 200 : 0) + Math.Min(x.PlayCount * 10, 500)
            })
            .ToList();

        return Ok(new
        {
            totalEligible,
            dailyQuota = mbDailyLimit,
            dailyLimit = mbDailyLimit,
            completedToday = mbConsumed,
            consumedUnits = mbConsumed,
            reservedUnits = mbReserved,
            remainingToday = Math.Max(0, mbDailyLimit - mbReserved),
            remainingUnits = Math.Max(0, mbDailyLimit - mbReserved),
            providers = new
            {
                MusicBrainz = new { dailyLimit = mbDailyLimit, reserved = mbReserved, consumed = mbConsumed, remaining = Math.Max(0, mbDailyLimit - mbReserved) },
                CoverArtArchive = new { dailyLimit = caaDailyLimit, reserved = caaReserved, consumed = caaConsumed, remaining = Math.Max(0, caaDailyLimit - caaReserved) },
                LRCLIB = new { dailyLimit = lrcDailyLimit, reserved = lrcReserved, consumed = lrcConsumed, remaining = Math.Max(0, lrcDailyLimit - lrcReserved) }
            },
            samplePrioritizedTracks = sampleTracks
        });
    }

    [HttpPost("lease-batch")]
    public async Task<IActionResult> LeaseBatch([FromBody] WorkerLeaseRequest request)
    {
        var callerUserId = GetUserId();
        var workerNodeId = GetWorkerNodeId();
        var requestedBatchSize = request.SpecificMediaFileId.HasValue
            ? 1
            : Math.Clamp(request.BatchSize ?? DefaultBatchSize, 1, MaxBatchSize);

        // Reconcile orphan covers and abandoned staged files
        await CoverPromotionReconciler.ReconcileOrphanCoversAsync(_context, _environment);

        using var tx = await _context.Database.BeginTransactionAsync();

        // 1. Reclaim expired leases atomically and remove abandoned staged cover files
        var expiredItems = await _context.EnrichmentJobItems
            .Where(i => (i.Status == "Leased" || i.Status == "AwaitingAssets" || i.Status == "ProcessingSubmit") && i.LeaseExpiresAt < DateTime.UtcNow)
            .ToListAsync();
        foreach (var exp in expiredItems)
        {
            if (!string.IsNullOrEmpty(exp.StagedCoverPath) && System.IO.File.Exists(exp.StagedCoverPath))
            {
                try { System.IO.File.Delete(exp.StagedCoverPath); } catch { }
                exp.StagedCoverPath = null;
            }
            exp.Status = "Pending";
            exp.CoverStatus = "Pending";
            exp.LyricsStatus = "Pending";
            exp.WorkerNodeId = null;
            exp.LeasedAt = null;
            exp.LeaseExpiresAt = null;
        }
        if (expiredItems.Count > 0)
        {
            await _context.SaveChangesAsync();
        }

        // 2. Persistent Multi-Provider Quota Ledgers with row locking (MusicBrainz, CAA, LRCLIB)
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var providerSpecs = new (string Provider, int DailyLimit, int WorstCaseUnits)[]
        {
            ("MusicBrainz", MusicBrainzDailyLimit, MusicBrainzWorstCaseUnits),
            ("CoverArtArchive", CoverArtArchiveDailyLimit, CoverArtArchiveWorstCaseUnits),
            ("LRCLIB", LrclibDailyLimit, LrclibWorstCaseUnits)
        };

        var ledgers = new Dictionary<string, ProviderQuotaLedger>();

        if (_context.Database.IsNpgsql())
        {
            foreach (var spec in providerSpecs)
            {
                await _context.Database.ExecuteSqlRawAsync(
                    @"INSERT INTO ""ProviderQuotaLedgers"" (""Provider"", ""Date"", ""DailyLimit"", ""ReservedUnits"", ""ConsumedUnits"", ""UpdatedAt"")
                      VALUES ({0}, {1}, {2}, 0, 0, {3})
                      ON CONFLICT (""Provider"", ""Date"") DO NOTHING;",
                    spec.Provider, todayStr, spec.DailyLimit, DateTime.UtcNow
                );

                var ledger = await _context.ProviderQuotaLedgers
                    .FromSqlRaw(@"SELECT * FROM ""ProviderQuotaLedgers"" WHERE ""Provider"" = {0} AND ""Date"" = {1} FOR UPDATE", spec.Provider, todayStr)
                    .FirstOrDefaultAsync();

                if (ledger != null) ledgers[spec.Provider] = ledger;
            }
        }
        else
        {
            foreach (var spec in providerSpecs)
            {
                var ledger = _context.ProviderQuotaLedgers.Local
                    .FirstOrDefault(l => l.Provider == spec.Provider && l.Date == todayStr)
                    ?? await _context.ProviderQuotaLedgers
                        .FirstOrDefaultAsync(l => l.Provider == spec.Provider && l.Date == todayStr);

                if (ledger == null)
                {
                    ledger = new ProviderQuotaLedger
                    {
                        Provider = spec.Provider,
                        Date = todayStr,
                        DailyLimit = spec.DailyLimit,
                        ReservedUnits = 0,
                        ConsumedUnits = 0,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _context.ProviderQuotaLedgers.Add(ledger);
                    try { await _context.SaveChangesAsync(); } catch { /* Ignore race in non-PG */ }
                }
                ledgers[spec.Provider] = ledger;
            }
        }

        var mbLedger = ledgers["MusicBrainz"];
        var caaLedger = ledgers["CoverArtArchive"];
        var lrcLedger = ledgers["LRCLIB"];

        var remainingMb = Math.Max(0, mbLedger.DailyLimit - mbLedger.ReservedUnits);
        var remainingCaa = Math.Max(0, caaLedger.DailyLimit - caaLedger.ReservedUnits);
        var remainingLrc = Math.Max(0, lrcLedger.DailyLimit - lrcLedger.ReservedUnits);

        if (remainingMb < MusicBrainzWorstCaseUnits || remainingCaa < CoverArtArchiveWorstCaseUnits || remainingLrc < LrclibWorstCaseUnits)
        {
            await tx.RollbackAsync();
            return StatusCode(429, new
            {
                message = "Daily provider request quota reached.",
                musicBrainz = new { dailyLimit = mbLedger.DailyLimit, reservedUnits = mbLedger.ReservedUnits, consumedUnits = mbLedger.ConsumedUnits, remaining = remainingMb },
                coverArtArchive = new { dailyLimit = caaLedger.DailyLimit, reservedUnits = caaLedger.ReservedUnits, consumedUnits = caaLedger.ConsumedUnits, remaining = remainingCaa },
                lrclib = new { dailyLimit = lrcLedger.DailyLimit, reservedUnits = lrcLedger.ReservedUnits, consumedUnits = lrcLedger.ConsumedUnits, remaining = remainingLrc }
            });
        }

        var maxMbCanLease = remainingMb / MusicBrainzWorstCaseUnits;
        var maxCaaCanLease = remainingCaa / CoverArtArchiveWorstCaseUnits;
        var maxLrcCanLease = remainingLrc / LrclibWorstCaseUnits;

        var maxCanLease = Math.Min(requestedBatchSize, Math.Min(maxMbCanLease, Math.Min(maxCaaCanLease, maxLrcCanLease)));
        if (maxCanLease <= 0)
        {
            await tx.RollbackAsync();
            return StatusCode(429, new
            {
                message = "Insufficient remaining daily quota to reserve a batch.",
                remainingMb,
                remainingCaa,
                remainingLrc
            });
        }

        // 3. Dynamic candidate selection: asset need + active lease check + cooldown/fingerprint bypass
        var cutoff30d = DateTime.UtcNow.AddDays(-30);
        var activeCooldowns = await _context.EnrichmentAttempts
            .Where(a => a.RetryAfter != null && a.RetryAfter > DateTime.UtcNow && a.InputFingerprint != null)
            .Select(a => new { a.MediaFileId, a.InputFingerprint })
            .ToListAsync();
        var cooldownLookup = activeCooldowns
            .GroupBy(a => a.MediaFileId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.InputFingerprint!).ToHashSet());

        List<MediaFile> candidates;
        if (_context.Database.IsNpgsql())
        {
            List<int> rawCandidateIds;
            if (request.SpecificMediaFileId.HasValue)
            {
                rawCandidateIds = await _context.Database.SqlQueryRaw<int>(
                    @"SELECT m.""Id"" FROM ""MediaFiles"" m
                      WHERE m.""Id"" = {0}
                        AND m.""Title"" IS NOT NULL AND m.""Title"" != '' AND m.""Artist"" IS NOT NULL AND m.""Artist"" != ''
                        AND NOT (m.""Title"" LIKE 'Unknown%') AND NOT (m.""Artist"" LIKE 'Unknown%')
                        AND (
                            m.""CoverArt"" IS NULL OR m.""CoverArt"" = ''
                            OR NOT EXISTS (SELECT 1 FROM ""Lyrics"" l WHERE l.""MediaFileId"" = m.""Id"")
                        )
                        AND NOT EXISTS (
                            SELECT 1 FROM ""EnrichmentJobItems"" i
                            WHERE i.""MediaFileId"" = m.""Id""
                              AND (i.""Status"" IN ('Leased', 'AwaitingAssets', 'ProcessingSubmit') AND i.""LeaseExpiresAt"" > {1})
                        )
                      LIMIT 1
                      FOR UPDATE SKIP LOCKED",
                    request.SpecificMediaFileId.Value, DateTime.UtcNow
                ).ToListAsync();
            }
            else
            {
                rawCandidateIds = await _context.Database.SqlQueryRaw<int>(
                    @"SELECT m.""Id"" FROM ""MediaFiles"" m
                      WHERE m.""Title"" IS NOT NULL AND m.""Title"" != '' AND m.""Artist"" IS NOT NULL AND m.""Artist"" != ''
                        AND NOT (m.""Title"" LIKE 'Unknown%') AND NOT (m.""Artist"" LIKE 'Unknown%')
                        AND (
                            m.""CoverArt"" IS NULL OR m.""CoverArt"" = ''
                            OR NOT EXISTS (SELECT 1 FROM ""Lyrics"" l WHERE l.""MediaFileId"" = m.""Id"")
                        )
                        AND NOT EXISTS (
                            SELECT 1 FROM ""EnrichmentJobItems"" i
                            WHERE i.""MediaFileId"" = m.""Id""
                              AND (i.""Status"" IN ('Leased', 'AwaitingAssets', 'ProcessingSubmit') AND i.""LeaseExpiresAt"" > {0})
                        )
                      ORDER BY
                        (CASE WHEN EXISTS (SELECT 1 FROM ""Favorites"" f WHERE f.""MediaFileId"" = m.""Id"") THEN 1000 ELSE 0 END)
                        + (CASE WHEN EXISTS (SELECT 1 FROM ""PlayHistories"" p WHERE p.""MediaFileId"" = m.""Id"" AND p.""PlayedAt"" >= {1}) THEN 200 ELSE 0 END)
                        + LEAST(COALESCE((SELECT COUNT(*) * 10 FROM ""PlayHistories"" p WHERE p.""MediaFileId"" = m.""Id""), 0), 500) DESC,
                        m.""Id"" ASC
                      LIMIT {2}
                      FOR UPDATE SKIP LOCKED",
                    DateTime.UtcNow, cutoff30d, maxCanLease * 3
                ).ToListAsync();
            }

            var mediaList = await _context.MediaFiles.Where(m => rawCandidateIds.Contains(m.Id)).ToListAsync();
            var mediaDict = mediaList.ToDictionary(m => m.Id);
            candidates = rawCandidateIds
                .Where(id => mediaDict.ContainsKey(id))
                .Select(id => mediaDict[id])
                .Where(m =>
                {
                    if (cooldownLookup.TryGetValue(m.Id, out var fps))
                    {
                        var currentFp = MusicEnrichmentService.ComputeFingerprint(m.Title, m.Artist, m.Album);
                        if (fps.Contains(currentFp)) return false;
                    }
                    return true;
                })
                .Take(maxCanLease)
                .ToList();
        }
        else
        {
            var query = _context.MediaFiles
                .Where(m => !string.IsNullOrEmpty(m.Title) && !string.IsNullOrEmpty(m.Artist)
                         && !m.Title.StartsWith("Unknown") && !m.Artist.StartsWith("Unknown")
                         && (string.IsNullOrEmpty(m.CoverArt) || !_context.Lyrics.Any(l => l.MediaFileId == m.Id))
                         && !_context.EnrichmentJobItems.Any(i => i.MediaFileId == m.Id && ((i.Status == "Leased" || i.Status == "AwaitingAssets" || i.Status == "ProcessingSubmit") && i.LeaseExpiresAt > DateTime.UtcNow)));

            if (request.SpecificMediaFileId.HasValue)
            {
                var targetId = request.SpecificMediaFileId.Value;
                query = query.Where(m => m.Id == targetId);
            }

            var candidatesBase = await query
                .Select(m => new
                {
                    Media = m,
                    IsFav = m.Favorites.Any(),
                    PlayCount = _context.PlayHistories.Count(p => p.MediaFileId == m.Id),
                    RecentPlay = _context.PlayHistories.Any(p => p.MediaFileId == m.Id && p.PlayedAt >= cutoff30d)
                })
                .ToListAsync();

            candidates = candidatesBase
                .Where(c =>
                {
                    if (cooldownLookup.TryGetValue(c.Media.Id, out var fps))
                    {
                        var currentFp = MusicEnrichmentService.ComputeFingerprint(c.Media.Title, c.Media.Artist, c.Media.Album);
                        if (fps.Contains(currentFp)) return false;
                    }
                    return true;
                })
                .OrderByDescending(x => (x.IsFav ? 1000 : 0) + (x.RecentPlay ? 200 : 0) + Math.Min(x.PlayCount * 10, 500))
                .ThenBy(x => x.Media.Id)
                .Take(maxCanLease)
                .Select(x => x.Media)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            await tx.RollbackAsync();
            return Ok(new WorkerLeaseBatchResponse
            {
                BatchId = null,
                WorkerNodeId = workerNodeId,
                Total = 0,
                Message = request.SpecificMediaFileId.HasValue
                    ? $"Specific media file {request.SpecificMediaFileId.Value} is ineligible (complete, in cooldown, or unknown tags), unavailable, or currently leased."
                    : "No eligible tracks available for leasing."
            });
        }

        var batchId = Guid.NewGuid().ToString("N");
        var leaseExpiresAt = DateTime.UtcNow.AddMinutes(15);
        var mediaIds = candidates.Select(c => c.Id).ToList();

        var job = new EnrichmentJob
        {
            Id = batchId,
            Scope = $"Catalog:WorkerLease:{workerNodeId}",
            RequestedByUserId = callerUserId > 0 ? callerUserId : null,
            Total = candidates.Count,
            CatalogBookmarkId = mediaIds.Max(),
            Cursor = 0,
            Status = "Processing",
            SongIdsJson = System.Text.Json.JsonSerializer.Serialize(mediaIds),
            StartedAt = DateTime.UtcNow
        };
        _context.EnrichmentJobs.Add(job);

        var leasedItems = new List<EnrichmentJobItem>();

        foreach (var m in candidates)
        {
            var needsCover = string.IsNullOrEmpty(m.CoverArt);
            var hasLyrics = await _context.Lyrics.AnyAsync(l => l.MediaFileId == m.Id);
            var needsLyrics = !hasLyrics;
            var fingerprint = MusicEnrichmentService.ComputeFingerprint(m.Title, m.Artist, m.Album);

            var item = new EnrichmentJobItem
            {
                JobId = batchId,
                MediaFileId = m.Id,
                Status = "Leased",
                InputFingerprint = fingerprint,
                CoverStatus = needsCover ? "Pending" : "Skipped",
                LyricsStatus = needsLyrics ? "Pending" : "Skipped",
                WorkerNodeId = workerNodeId,
                LeasedAt = DateTime.UtcNow,
                LeaseExpiresAt = leaseExpiresAt
            };
            _context.EnrichmentJobItems.Add(item);
            leasedItems.Add(item);
        }

        // Atomically reserve worst-case units in all provider ledgers
        var neededCoverCount = leasedItems.Count(i => i.CoverStatus == "Pending");
        var neededLyricsCount = leasedItems.Count(i => i.LyricsStatus == "Pending");

        mbLedger.ReservedUnits += leasedItems.Count * MusicBrainzWorstCaseUnits;
        caaLedger.ReservedUnits += neededCoverCount * CoverArtArchiveWorstCaseUnits;
        lrcLedger.ReservedUnits += neededLyricsCount * LrclibWorstCaseUnits;

        mbLedger.UpdatedAt = DateTime.UtcNow;
        caaLedger.UpdatedAt = DateTime.UtcNow;
        lrcLedger.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        var itemsResponse = leasedItems.Select(item =>
        {
            var media = candidates.First(c => c.Id == item.MediaFileId);
            return new WorkerLeaseItemDto
            {
                ItemId = item.Id,
                MediaFileId = media.Id,
                Title = media.Title,
                Artist = media.Artist,
                Album = media.Album,
                DurationSeconds = media.Duration.TotalSeconds,
                NeedsCover = item.CoverStatus == "Pending",
                NeedsLyrics = item.LyricsStatus == "Pending",
                InputFingerprint = item.InputFingerprint
            };
        }).ToList();

        return Ok(new WorkerLeaseBatchResponse
        {
            BatchId = batchId,
            WorkerNodeId = workerNodeId,
            Total = leasedItems.Count,
            LeaseExpiresAt = leaseExpiresAt,
            Items = itemsResponse
        });
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] WorkerHeartbeatRequest request)
    {
        if (string.IsNullOrEmpty(request.BatchId) || request.ItemIds == null || request.ItemIds.Count == 0)
        {
            return BadRequest(new { message = "BatchId and ItemIds are required." });
        }

        var workerNodeId = GetWorkerNodeId();
        var newExpiration = DateTime.UtcNow.AddMinutes(15);
        var items = await _context.EnrichmentJobItems
            .Where(i => i.JobId == request.BatchId
                     && i.WorkerNodeId == workerNodeId
                     && (i.Status == "Leased" || i.Status == "AwaitingAssets")
                     && request.ItemIds.Contains(i.Id))
            .ToListAsync();

        foreach (var item in items)
        {
            item.LeaseExpiresAt = newExpiration;
        }

        await _context.SaveChangesAsync();
        return Ok(new { renewedCount = items.Count, leaseExpiresAt = newExpiration });
    }

    [HttpPost("items/{itemId}/upload-cover")]
    [RequestSizeLimit(5 * 1024 * 1024)] // Strict 5MB limit
    public async Task<IActionResult> UploadCover([FromRoute] int itemId)
    {
        var callerNodeId = GetWorkerNodeId();

        var item = await _context.EnrichmentJobItems
            .FirstOrDefaultAsync(i => i.Id == itemId
                                   && (i.WorkerNodeId == callerNodeId || i.WorkerNodeId == null));

        if (item == null)
        {
            return NotFound(new { message = $"Item {itemId} not found for worker {callerNodeId}." });
        }

        if (item.Status != "Leased" && item.Status != "AwaitingAssets")
        {
            return Conflict(new { message = $"Item is in status '{item.Status}', cannot upload cover in this state." });
        }

        if (item.LeaseExpiresAt < DateTime.UtcNow)
        {
            return StatusCode(410, new { message = "Lease has expired. Item must be re-leased." });
        }

        var stagedDir = Path.Combine(_environment.ContentRootPath, "data", "covers", "staged");
        Directory.CreateDirectory(stagedDir);
        var tempFile = Path.Combine(stagedDir, $"staged-{itemId}-{Guid.NewGuid():N}.tmp");

        byte[] header = new byte[32];
        int headerBytesRead = 0;
        long totalBytesRead = 0;
        const long maxAllowedBytes = 5 * 1024 * 1024; // 5 MB

        // Stream directly from request body to disk
        await using (var fileStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
        {
            var buffer = new byte[8192];
            int bytesRead;
            while ((bytesRead = await Request.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalBytesRead += bytesRead;
                if (totalBytesRead > maxAllowedBytes)
                {
                    fileStream.Close();
                    if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
                    return StatusCode(413, new { message = "Cover image exceeds 5MB limit." });
                }
                if (headerBytesRead < 32)
                {
                    var toCopy = Math.Min(bytesRead, 32 - headerBytesRead);
                    Array.Copy(buffer, 0, header, headerBytesRead, toCopy);
                    headerBytesRead += toCopy;
                }
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            }
        }

        if (totalBytesRead == 0)
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
            return BadRequest(new { message = "Cover stream is empty." });
        }

        if (!AssetValidationHelper.TryValidateImageHeader(header, totalBytesRead, out var ext, out var error))
        {
            if (System.IO.File.Exists(tempFile)) System.IO.File.Delete(tempFile);
            return BadRequest(new { message = $"Invalid cover image: {error}" });
        }

        // Clean up previous staged file if any
        if (!string.IsNullOrEmpty(item.StagedCoverPath) && System.IO.File.Exists(item.StagedCoverPath))
        {
            try { System.IO.File.Delete(item.StagedCoverPath); } catch { }
        }

        var stagedFileWithExt = Path.ChangeExtension(tempFile, ext);
        System.IO.File.Move(tempFile, stagedFileWithExt, overwrite: true);

        // Record staged cover in database item only; do NOT touch MediaFile.CoverArt yet
        item.StagedCoverPath = stagedFileWithExt;
        item.CoverStatus = "Matched";
        item.Status = "AwaitingAssets"; // Explicit state transition
        await _context.SaveChangesAsync();

        return Ok(new { success = true, staged = true, size = totalBytesRead });
    }

    [HttpPost("submit-batch")]
    [RequestSizeLimit(1_048_576)] // 1MB limit for metadata JSON
    public async Task<IActionResult> SubmitBatch([FromBody] WorkerSubmitBatchRequest request)
    {
        if (string.IsNullOrEmpty(request.BatchId) || request.Results == null)
        {
            return BadRequest(new { message = "BatchId and Results are required." });
        }

        var callerNodeId = GetWorkerNodeId();

        var job = await _context.EnrichmentJobs.FindAsync(request.BatchId);
        if (job == null) return NotFound(new { message = "Job not found." });

        var submissionId = string.IsNullOrWhiteSpace(request.SubmissionId) ? Guid.NewGuid().ToString() : request.SubmissionId.Trim();

        using var tx = await _context.Database.BeginTransactionAsync();

        int updatedCount = 0;
        int unmatchedCount = 0;
        int skippedCount = 0;
        int failedCount = 0;
        int actuallyProcessedCount = 0;
        int ignoredOrExpiredCount = 0;

        int actualMbUnitsConsumed = 0;
        int actualCaaUnitsConsumed = 0;
        int actualLrcUnitsConsumed = 0;

        var promotedFiles = new List<string>();
        var stagedFilesToCleanup = new List<string>();

        foreach (var res in request.Results)
        {
            // 1. Idempotency check on (ItemId, SubmissionId)
            var isDuplicateSubmission = await _context.WorkerSubmissions
                .AnyAsync(s => s.ItemId == res.ItemId && s.SubmissionId == submissionId);

            if (isDuplicateSubmission)
            {
                ignoredOrExpiredCount++;
                continue;
            }

            // 2. Atomic state transition to acquire completion right ("领取完成权")
            bool acquiredRight = false;
            if (_context.Database.IsNpgsql())
            {
                var acquired = await _context.Database.SqlQueryRaw<int>(
                    @"UPDATE ""EnrichmentJobItems""
                      SET ""Status"" = 'ProcessingSubmit'
                      WHERE ""Id"" = {0}
                        AND ""JobId"" = {1}
                        AND ""WorkerNodeId"" = {2}
                        AND ""Status"" IN ('Leased', 'AwaitingAssets')
                        AND ""LeaseExpiresAt"" >= {3}
                      RETURNING ""Id"";",
                    res.ItemId, request.BatchId, callerNodeId, DateTime.UtcNow
                ).ToListAsync();

                acquiredRight = acquired.Count > 0;
            }
            else
            {
                var rows = await _context.Database.ExecuteSqlRawAsync(
                    @"UPDATE ""EnrichmentJobItems""
                      SET ""Status"" = 'ProcessingSubmit'
                      WHERE ""Id"" = {0}
                        AND ""JobId"" = {1}
                        AND ""WorkerNodeId"" = {2}
                        AND ""Status"" IN ('Leased', 'AwaitingAssets')
                        AND ""LeaseExpiresAt"" >= {3}",
                    res.ItemId, request.BatchId, callerNodeId, DateTime.UtcNow
                );
                acquiredRight = rows > 0;
            }

            if (!acquiredRight)
            {
                // Lost the race or lease expired or node mismatch
                ignoredOrExpiredCount++;
                continue;
            }

            // Successfully acquired exclusive right to complete this item
            var payloadStr = $"{res.Outcome}|{res.RecordingId}|{res.Confidence}";
            var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadStr)));

            _context.WorkerSubmissions.Add(new WorkerSubmission
            {
                ItemId = res.ItemId,
                SubmissionId = submissionId,
                PayloadHash = payloadHash,
                SubmittedAt = DateTime.UtcNow
            });

            actuallyProcessedCount++;

            var item = await _context.EnrichmentJobItems
                .Include(i => i.MediaFile)
                .FirstAsync(i => i.Id == res.ItemId);

            // Record provider request counts
            actualMbUnitsConsumed += res.MbRequestsCount ?? Math.Max(1, res.RetryCount + 1);
            actualCaaUnitsConsumed += res.CaaRequestsCount ?? (item.CoverStatus == "Matched" || !string.IsNullOrEmpty(item.StagedCoverPath) ? 1 : 0);
            actualLrcUnitsConsumed += res.LrcRequestsCount ?? (!string.IsNullOrEmpty(res.LyricsContent) ? 1 : 0);

            var media = item.MediaFile ?? await _context.MediaFiles.FindAsync(res.MediaFileId);
            if (media == null)
            {
                item.Status = "Failed";
                item.Detail = "Media file not found.";
                item.CompletedAt = DateTime.UtcNow;
                failedCount++;
                continue;
            }

            var changedAssets = new List<string>();
            string coverStatus = item.CoverStatus;
            string lyricsStatus = item.LyricsStatus;

            // Two-phase cover promotion with rollback compensation tracking
            if (!string.IsNullOrEmpty(item.StagedCoverPath) && System.IO.File.Exists(item.StagedCoverPath))
            {
                var ext = Path.GetExtension(item.StagedCoverPath);
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                var coversDir = Path.Combine(_environment.ContentRootPath, "data", "covers");
                Directory.CreateDirectory(coversDir);
                var finalFileName = $"enriched-{Guid.NewGuid():N}{ext}";
                var finalFilePath = Path.Combine(coversDir, finalFileName);
                System.IO.File.Copy(item.StagedCoverPath, finalFilePath, overwrite: true);
                promotedFiles.Add(finalFilePath);
                stagedFilesToCleanup.Add(item.StagedCoverPath);
                media.CoverArt = $"/api/media/cover/{finalFileName}";
                item.StagedCoverPath = null;
                changedAssets.Add("cover");
                coverStatus = "Matched";
            }
            else if (item.CoverStatus == "Matched" && !string.IsNullOrEmpty(media.CoverArt))
            {
                changedAssets.Add("cover");
            }
            else if (item.CoverStatus == "Pending")
            {
                coverStatus = res.Outcome == "Matched" || res.Outcome == "Updated" ? "NoAsset" : res.Outcome;
            }

            // Process Lyrics Asset with Length Validation (<= 64KB)
            if (!string.IsNullOrEmpty(res.LyricsContent))
            {
                if (AssetValidationHelper.ValidateLyrics(res.LyricsContent))
                {
                    var hasLyrics = await _context.Lyrics.AnyAsync(l => l.MediaFileId == media.Id);
                    if (!hasLyrics)
                    {
                        _context.Lyrics.Add(new Lyric
                        {
                            MediaFileId = media.Id,
                            Content = res.LyricsContent,
                            Language = "unknown",
                            Source = "LRCLIB",
                            Version = res.LyricsSynced ? "synced" : "plain",
                            CreatedAt = DateTime.UtcNow
                        });
                        changedAssets.Add("lyrics");
                        lyricsStatus = "Matched";
                    }
                }
                else
                {
                    lyricsStatus = "Failed";
                }
            }
            else if (item.LyricsStatus == "Pending")
            {
                lyricsStatus = res.Outcome == "Matched" || res.Outcome == "Updated" ? "NoAsset" : res.Outcome;
            }

            // Upsert MediaIdentity
            if (!string.IsNullOrEmpty(res.RecordingId))
            {
                var existingIdentity = await _context.MediaIdentities
                    .FirstOrDefaultAsync(i => i.MediaFileId == media.Id && i.Provider == "MusicBrainz");
                if (existingIdentity == null)
                {
                    _context.MediaIdentities.Add(new MediaIdentity
                    {
                        MediaFileId = media.Id,
                        Provider = "MusicBrainz",
                        RecordingId = res.RecordingId,
                        ReleaseId = res.ReleaseId,
                        MatchMethod = "MetadataFuzzy",
                        Confidence = Math.Round(res.Confidence, 4),
                        Status = "approved",
                        CoverStatus = coverStatus,
                        LyricsStatus = lyricsStatus,
                        MatchedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    existingIdentity.RecordingId = res.RecordingId;
                    existingIdentity.ReleaseId = res.ReleaseId;
                    existingIdentity.Confidence = Math.Round(res.Confidence, 4);
                    existingIdentity.CoverStatus = coverStatus;
                    existingIdentity.LyricsStatus = lyricsStatus;
                    existingIdentity.LastVerifiedAt = DateTime.UtcNow;
                }
            }

            // Calculate RetryAfter and outcome
            var finalOutcome = res.Outcome;
            DateTime? retryAfter = null;
            if (changedAssets.Count > 0)
            {
                finalOutcome = "Matched";
                updatedCount++;
            }
            else if (res.Outcome == "MatchedWithoutAssets" || (!string.IsNullOrEmpty(res.RecordingId) && changedAssets.Count == 0))
            {
                finalOutcome = "MatchedWithoutAssets";
                retryAfter = DateTime.UtcNow.AddDays(14);
                unmatchedCount++;
            }
            else if (res.Outcome == "Unmatched")
            {
                retryAfter = DateTime.UtcNow.AddDays(30);
                unmatchedCount++;
            }
            else if (res.Outcome == "Failed")
            {
                retryAfter = DateTime.UtcNow.AddHours(6);
                failedCount++;
            }
            else if (res.Outcome == "Skipped")
            {
                skippedCount++;
            }

            var fingerprint = item.InputFingerprint ?? MusicEnrichmentService.ComputeFingerprint(media.Title, media.Artist, media.Album);

            // Record EnrichmentAttempt
            _context.EnrichmentAttempts.Add(new EnrichmentAttempt
            {
                JobId = request.BatchId,
                MediaFileId = media.Id,
                Provider = "MusicBrainz+CoverArtArchive+LRCLIB",
                RequestKey = res.RecordingId,
                InputFingerprint = fingerprint,
                HTTPStatus = res.HttpStatus,
                Outcome = finalOutcome,
                Confidence = Math.Round(res.Confidence, 4),
                RetryCount = res.RetryCount,
                Detail = res.Detail ?? (changedAssets.Count > 0 ? $"Saved {string.Join(" and ", changedAssets)}." : "No asset added."),
                RetryAfter = retryAfter,
                CreatedAt = DateTime.UtcNow
            });

            // Record MusicEnrichment
            _context.MusicEnrichments.Add(new MusicEnrichment
            {
                MediaFileId = media.Id,
                Provider = "MusicBrainz+CoverArtArchive+LRCLIB",
                ExternalId = res.RecordingId,
                Confidence = Math.Round(res.Confidence, 4),
                Status = finalOutcome,
                Details = res.Detail ?? (changedAssets.Count > 0 ? $"Saved {string.Join(" and ", changedAssets)}." : "No asset added."),
                CreatedAt = DateTime.UtcNow
            });

            // State machine: finalize to Completed
            item.Status = "Completed";
            item.CoverStatus = coverStatus;
            item.LyricsStatus = lyricsStatus;
            item.Outcome = finalOutcome;
            item.Detail = res.Detail;
            item.CompletedAt = DateTime.UtcNow;
        }

        // Update Job counters strictly based on actually transitioned items
        job.Updated += updatedCount;
        job.Unmatched += unmatchedCount;
        job.Skipped += skippedCount;
        job.Failed += failedCount;
        job.Processed += actuallyProcessedCount;
        job.Cursor = job.Processed;

        var allJobItems = await _context.EnrichmentJobItems
            .Where(i => i.JobId == request.BatchId)
            .ToListAsync();

        if (allJobItems.Count > 0 && allJobItems.All(i => i.Status == "Completed" || i.Status == "Failed" || i.Status == "Skipped"))
        {
            job.Status = "Completed";
            job.FinishedAt = DateTime.UtcNow;
        }

        // Update Provider Quota Ledgers Consumed Units for today
        var todayStr = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var ledgers = await _context.ProviderQuotaLedgers
            .Where(l => l.Date == todayStr)
            .ToListAsync();

        var mbLedger = ledgers.FirstOrDefault(l => l.Provider == "MusicBrainz");
        if (mbLedger != null)
        {
            mbLedger.ConsumedUnits += actualMbUnitsConsumed;
            mbLedger.UpdatedAt = DateTime.UtcNow;
        }

        var caaLedger = ledgers.FirstOrDefault(l => l.Provider == "CoverArtArchive");
        if (caaLedger != null)
        {
            caaLedger.ConsumedUnits += actualCaaUnitsConsumed;
            caaLedger.UpdatedAt = DateTime.UtcNow;
        }

        var lrcLedger = ledgers.FirstOrDefault(l => l.Provider == "LRCLIB");
        if (lrcLedger != null)
        {
            lrcLedger.ConsumedUnits += actualLrcUnitsConsumed;
            lrcLedger.UpdatedAt = DateTime.UtcNow;
        }

        try
        {
            await _context.SaveChangesAsync();
            await tx.CommitAsync();

            // Post-commit cleanup of source staged files
            foreach (var staged in stagedFilesToCleanup)
            {
                try { if (System.IO.File.Exists(staged)) System.IO.File.Delete(staged); } catch { }
            }
        }
        catch (Exception)
        {
            // Atomic compensation: delete newly copied destination files if DB transaction fails
            foreach (var promoted in promotedFiles)
            {
                try { if (System.IO.File.Exists(promoted)) System.IO.File.Delete(promoted); } catch { }
            }
            await tx.RollbackAsync();
            throw;
        }

        return Ok(new WorkerSubmitBatchResponse
        {
            BatchId = request.BatchId,
            Processed = actuallyProcessedCount,
            IgnoredOrExpired = ignoredOrExpiredCount,
            Updated = updatedCount,
            Unmatched = unmatchedCount,
            Skipped = skippedCount,
            Failed = failedCount,
            JobStatus = job.Status
        });
    }

    private string GetWorkerNodeId()
    {
        var workerNode = User?.FindFirst("worker_node")?.Value;
        if (!string.IsNullOrWhiteSpace(workerNode)) return workerNode.Trim();
        var sub = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User?.FindFirst("sub")?.Value;
        if (!string.IsNullOrWhiteSpace(sub)) return $"node-sub-{sub.Trim()}";
        var name = User?.FindFirst(ClaimTypes.Name)?.Value ?? User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        return "worker-node-unknown";
    }

    private int GetUserId() => int.TryParse(User?.FindFirst("sub")?.Value ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) ? userId : 0;
}
