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
    public async Task<IActionResult> StartFavorites()
    {
        var userId = GetUserId();
        var songIds = await EligibleFavorites(userId).Select(f => f.MediaFileId).Distinct().ToListAsync();
        if (songIds.Count == 0) return Ok(new { batchId = (string?)null, total = 0, message = "No favorite songs need enrichment." });

        var batchId = Guid.NewGuid().ToString("N");
        _queue.Enqueue(new FavoritesEnrichmentJob(batchId, songIds));
        return Ok(new { batchId, total = songIds.Count, message = "Favorites enrichment started." });
    }

    [HttpPost("favorites/retry-failed")]
    public async Task<IActionResult> RetryRecentFailures()
    {
        var userId = GetUserId();
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var songIds = await _context.MusicEnrichments
            .Where(e => e.Status == "Failed" && e.CreatedAt >= cutoff)
            .Join(_context.Favorites.Where(f => f.UserId == userId), e => e.MediaFileId, f => f.MediaFileId, (e, _) => e)
            .Where(e => !_context.MusicEnrichments.Any(later => later.MediaFileId == e.MediaFileId && later.CreatedAt > e.CreatedAt && later.Status == "Matched"))
            .Select(e => e.MediaFileId)
            .Distinct()
            .ToListAsync();
        if (songIds.Count == 0) return Ok(new { batchId = (string?)null, total = 0, message = "No recent external failures need retrying." });

        var batchId = Guid.NewGuid().ToString("N");
        _queue.Enqueue(new FavoritesEnrichmentJob(batchId, songIds));
        return Ok(new { batchId, total = songIds.Count, message = "Retrying recent external failures." });
    }

    [HttpGet("{batchId}")]
    public IActionResult GetStatus(string batchId)
    {
        var status = _queue.GetAiStatus(batchId);
        return status == null ? NotFound() : Ok(status);
    }

    private IQueryable<WebMusic.Backend.Models.Favorite> EligibleFavorites(int userId) => _context.Favorites
        .Where(f => f.UserId == userId)
        .Where(f => f.MediaFile != null &&
                    (string.IsNullOrEmpty(f.MediaFile.CoverArt) || !_context.Lyrics.Any(l => l.MediaFileId == f.MediaFileId)));

    private int GetUserId() => int.TryParse(User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId) ? userId : 0;
}
