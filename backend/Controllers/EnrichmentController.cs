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

    public EnrichmentController(AppDbContext context, BackgroundTaskQueue queue)
    {
        _context = context;
        _queue = queue;
    }

    [HttpGet("favorites/preview")]
    public async Task<IActionResult> PreviewFavorites()
    {
        var userId = GetUserId();
        var total = await EligibleFavorites(userId).CountAsync();
        return Ok(new { total, scope = "Favorites missing a cover or lyric" });
    }

    [HttpPost("favorites/start")]
    public async Task<IActionResult> StartFavorites([FromQuery] int? batchSize = null)
    {
        var userId = GetUserId();
        var query = EligibleFavorites(userId).Select(f => f.MediaFileId).Distinct();
        if (batchSize.HasValue && batchSize.Value > 0)
        {
            query = query.Take(batchSize.Value);
        }

        var songIds = await query.ToListAsync();
        if (songIds.Count == 0) return Ok(new { batchId = (string?)null, total = 0, message = "No favorite songs need enrichment." });

        var batchId = Guid.NewGuid().ToString("N");
        var dbJob = new WebMusic.Backend.Models.EnrichmentJob
        {
            Id = batchId,
            Scope = "Favorites",
            RequestedByUserId = userId,
            Total = songIds.Count,
            Status = "Queued",
            SongIdsJson = System.Text.Json.JsonSerializer.Serialize(songIds),
            StartedAt = DateTime.UtcNow
        };
        _context.EnrichmentJobs.Add(dbJob);
        await _context.SaveChangesAsync();

        _queue.Enqueue(new FavoritesEnrichmentJob(batchId, songIds));
        return Ok(new { batchId, total = songIds.Count, message = $"Favorites enrichment started for {songIds.Count} song(s)." });
    }

    [HttpPost("favorites/retry-failed")]
    public async Task<IActionResult> RetryRecentFailures([FromQuery] int? batchSize = null)
    {
        var userId = GetUserId();
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var query = _context.MusicEnrichments
            .Where(e => e.Status == "Failed" && e.CreatedAt >= cutoff)
            .Join(_context.Favorites.Where(f => f.UserId == userId), e => e.MediaFileId, f => f.MediaFileId, (e, _) => e)
            .Where(e => !_context.MusicEnrichments.Any(later => later.MediaFileId == e.MediaFileId && later.CreatedAt > e.CreatedAt && later.Status == "Matched"))
            .Select(e => e.MediaFileId)
            .Distinct();

        if (batchSize.HasValue && batchSize.Value > 0)
        {
            query = query.Take(batchSize.Value);
        }

        var songIds = await query.ToListAsync();
        if (songIds.Count == 0) return Ok(new { batchId = (string?)null, total = 0, message = "No recent external failures need retrying." });

        var batchId = Guid.NewGuid().ToString("N");
        var dbJob = new WebMusic.Backend.Models.EnrichmentJob
        {
            Id = batchId,
            Scope = "RetryFailed",
            RequestedByUserId = userId,
            Total = songIds.Count,
            Status = "Queued",
            SongIdsJson = System.Text.Json.JsonSerializer.Serialize(songIds),
            StartedAt = DateTime.UtcNow
        };
        _context.EnrichmentJobs.Add(dbJob);
        await _context.SaveChangesAsync();

        _queue.Enqueue(new FavoritesEnrichmentJob(batchId, songIds));
        return Ok(new { batchId, total = songIds.Count, message = $"Retrying recent external failures for {songIds.Count} song(s)." });
    }

    [HttpGet("{batchId}")]
    public async Task<IActionResult> GetStatus(string batchId)
    {
        var dbJob = await _context.EnrichmentJobs.FindAsync(batchId);
        if (dbJob != null)
        {
            return Ok(new
            {
                batchId = dbJob.Id,
                scope = dbJob.Scope,
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
    public async Task<IActionResult> GetJobs([FromQuery] int limit = 20)
    {
        var jobs = await _context.EnrichmentJobs
            .OrderByDescending(j => j.StartedAt)
            .Take(limit)
            .Select(j => new
            {
                j.Id,
                j.Scope,
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
    public async Task<IActionResult> GetAttempts(string batchId, [FromQuery] int limit = 50)
    {
        var attempts = await _context.EnrichmentAttempts
            .Where(a => a.JobId == batchId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync();
        return Ok(attempts);
    }

    private IQueryable<WebMusic.Backend.Models.Favorite> EligibleFavorites(int userId) => _context.Favorites
        .Where(f => f.UserId == userId)
        .Where(f => f.MediaFile != null &&
                    (string.IsNullOrEmpty(f.MediaFile.CoverArt) || !_context.Lyrics.Any(l => l.MediaFileId == f.MediaFileId)));

    private int GetUserId() => int.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) ? userId : 0;
}

