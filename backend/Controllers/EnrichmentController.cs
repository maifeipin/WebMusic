using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;

namespace WebMusic.Backend.Controllers;

[ApiController]
[Route("api/enrichment")]
[Authorize(Roles = "Admin")]
public class EnrichmentController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly BackgroundTaskQueue _queue;

    public const int MaxBatchSize = 100;
    public const int DefaultBatchSize = 20;

    public EnrichmentController(AppDbContext context, BackgroundTaskQueue queue)
    {
        _context = context;
        _queue = queue;
    }

    [HttpGet("favorites/preview")]
    public async Task<IActionResult> PreviewFavorites([FromQuery] int? targetUserId = null)
    {
        var currentUserId = GetUserId();
        var effectiveUserId = (targetUserId.HasValue && User.IsInRole("Admin")) ? targetUserId.Value : currentUserId;
        var total = await EligibleFavorites(effectiveUserId).CountAsync();
        var scope = effectiveUserId > 0 
            ? $"Favorites missing a cover or lyric (user: {effectiveUserId})" 
            : "All users' favorites missing a cover or lyric (not full library)";
        return Ok(new { total, scope, targetUserId = effectiveUserId, maxBatchSize = MaxBatchSize, defaultBatchSize = DefaultBatchSize });
    }

    [HttpPost("favorites/start")]
    public async Task<IActionResult> StartFavorites([FromQuery] int? batchSize = null, [FromQuery] int? targetUserId = null)
    {
        var callerUserId = GetUserId();
        var effectiveUserId = (targetUserId.HasValue && User.IsInRole("Admin")) ? targetUserId.Value : callerUserId;
        var effectiveBatchSize = Math.Clamp(batchSize ?? DefaultBatchSize, 1, MaxBatchSize);
        var query = EligibleFavorites(effectiveUserId).Select(f => f.MediaFileId).Distinct().Take(effectiveBatchSize);

        var songIds = await query.ToListAsync();
        if (songIds.Count == 0) return Ok(new { batchId = (string?)null, total = 0, message = "No favorite songs need enrichment." });

        var batchId = Guid.NewGuid().ToString("N");
        var scopeDesc = effectiveUserId > 0 ? $"Favorites:User{effectiveUserId}" : "Favorites:AllUsers";
        var dbJob = new WebMusic.Backend.Models.EnrichmentJob
        {
            Id = batchId,
            Scope = scopeDesc,
            RequestedByUserId = callerUserId,
            Total = songIds.Count,
            Status = "Queued",
            SongIdsJson = System.Text.Json.JsonSerializer.Serialize(songIds),
            StartedAt = DateTime.UtcNow
        };
        _context.EnrichmentJobs.Add(dbJob);
        await _context.SaveChangesAsync();

        _queue.Enqueue(new FavoritesEnrichmentJob(batchId, songIds));
        return Ok(new { batchId, total = songIds.Count, batchSize = effectiveBatchSize, targetUserId = effectiveUserId, message = $"Favorites enrichment started for {songIds.Count} favorite song(s) of {(effectiveUserId > 0 ? $"user {effectiveUserId}" : "all users")} (batch size clamped to max {MaxBatchSize})." });
    }

    [HttpPost("favorites/retry-failed")]
    public async Task<IActionResult> RetryRecentFailures([FromQuery] int? batchSize = null, [FromQuery] int? targetUserId = null)
    {
        var callerUserId = GetUserId();
        var effectiveUserId = (targetUserId.HasValue && User.IsInRole("Admin")) ? targetUserId.Value : callerUserId;
        var effectiveBatchSize = Math.Clamp(batchSize ?? DefaultBatchSize, 1, MaxBatchSize);
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var favoritesQuery = _context.Favorites.AsQueryable();
        if (effectiveUserId > 0)
        {
            favoritesQuery = favoritesQuery.Where(f => f.UserId == effectiveUserId);
        }

        var query = _context.MusicEnrichments
            .Where(e => e.Status == "Failed" && e.CreatedAt >= cutoff)
            .Join(favoritesQuery, e => e.MediaFileId, f => f.MediaFileId, (e, _) => e)
            .Where(e => !_context.MusicEnrichments.Any(later => later.MediaFileId == e.MediaFileId && later.CreatedAt > e.CreatedAt && later.Status == "Matched"))
            .Select(e => e.MediaFileId)
            .Distinct()
            .Take(effectiveBatchSize);

        var songIds = await query.ToListAsync();
        if (songIds.Count == 0) return Ok(new { batchId = (string?)null, total = 0, message = "No recent external failures need retrying." });

        var batchId = Guid.NewGuid().ToString("N");
        var scopeDesc = effectiveUserId > 0 ? $"RetryFailed:User{effectiveUserId}" : "RetryFailed:AllUsers";
        var dbJob = new WebMusic.Backend.Models.EnrichmentJob
        {
            Id = batchId,
            Scope = scopeDesc,
            RequestedByUserId = callerUserId,
            Total = songIds.Count,
            Status = "Queued",
            SongIdsJson = System.Text.Json.JsonSerializer.Serialize(songIds),
            StartedAt = DateTime.UtcNow
        };
        _context.EnrichmentJobs.Add(dbJob);
        await _context.SaveChangesAsync();

        _queue.Enqueue(new FavoritesEnrichmentJob(batchId, songIds));
        return Ok(new { batchId, total = songIds.Count, batchSize = effectiveBatchSize, targetUserId = effectiveUserId, message = $"Retrying recent external failures for {songIds.Count} song(s) (batch size clamped to max {MaxBatchSize})." });
    }

    [HttpGet("{batchId}")]
    public async Task<IActionResult> GetStatus(string batchId, [FromQuery] bool allUsers = false)
    {
        var dbJob = await _context.EnrichmentJobs.FindAsync(batchId);
        if (dbJob != null)
        {
            var userId = GetUserId();
            if (!allUsers && dbJob.RequestedByUserId.HasValue && dbJob.RequestedByUserId.Value != userId)
            {
                return Forbid();
            }

            return Ok(new
            {
                batchId = dbJob.Id,
                scope = dbJob.Scope,
                requestedByUserId = dbJob.RequestedByUserId,
                total = dbJob.Total,
                processed = dbJob.Processed,
                updated = dbJob.Updated,
                unmatched = dbJob.Unmatched,
                skipped = dbJob.Skipped,
                failed = dbJob.Failed,
                cursor = dbJob.Cursor,
                status = dbJob.Status,
                startedAt = dbJob.StartedAt,
                finishedAt = dbJob.FinishedAt
            });
        }

        var status = _queue.GetAiStatus(batchId);
        return status == null ? NotFound() : Ok(status);
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> GetJobs([FromQuery] int limit = 20, [FromQuery] bool allUsers = false)
    {
        var userId = GetUserId();
        var query = _context.EnrichmentJobs.AsQueryable();

        // Default to filtering by current user; allow all users only if explicitly requested by Admin
        if (!allUsers || !User.IsInRole("Admin"))
        {
            query = query.Where(j => j.RequestedByUserId == userId);
        }

        var jobs = await query
            .OrderByDescending(j => j.StartedAt)
            .Take(Math.Min(Math.Max(limit, 1), 100))
            .Select(j => new
            {
                j.Id,
                j.Scope,
                j.RequestedByUserId,
                j.Total,
                j.Processed,
                j.Updated,
                j.Unmatched,
                j.Skipped,
                j.Failed,
                j.Cursor,
                j.Status,
                j.StartedAt,
                j.FinishedAt
            })
            .ToListAsync();
        return Ok(jobs);
    }

    [HttpGet("attempts/{batchId}")]
    public async Task<IActionResult> GetAttempts(string batchId, [FromQuery] int limit = 50, [FromQuery] bool allUsers = false)
    {
        var userId = GetUserId();
        var job = await _context.EnrichmentJobs.FindAsync(batchId);
        if (job == null) return NotFound(new { message = "Job not found" });

        // Strict isolation: if not viewing all users and caller is not the job's requester, forbid access
        if (!allUsers && job.RequestedByUserId.HasValue && job.RequestedByUserId.Value != userId)
        {
            return Forbid();
        }

        var attempts = await _context.EnrichmentAttempts
            .Where(a => a.JobId == batchId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Min(Math.Max(limit, 1), 200))
            .ToListAsync();
        return Ok(attempts);
    }

    private IQueryable<WebMusic.Backend.Models.Favorite> EligibleFavorites(int userId)
    {
        var query = _context.Favorites.AsQueryable();
        if (userId > 0)
        {
            query = query.Where(f => f.UserId == userId);
        }
        return query.Where(f => f.MediaFile != null &&
                    (string.IsNullOrEmpty(f.MediaFile.CoverArt) || !_context.Lyrics.Any(l => l.MediaFileId == f.MediaFileId)));
    }

    private int GetUserId() => int.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) ? userId : 0;
}

