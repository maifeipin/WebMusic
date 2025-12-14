using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WebMusic.Backend.Services;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class LyricsController : ControllerBase
{
    private readonly LyricsService _lyricsService;
    private readonly BackgroundTaskQueue _queue;

    public LyricsController(LyricsService lyricsService, BackgroundTaskQueue queue)
    {
        _lyricsService = lyricsService;
        _queue = queue;
    }

    [HttpGet("{mediaId}")]
    public async Task<IActionResult> GetLyrics(int mediaId)
    {
        var lyrics = await _lyricsService.GetLyricsAsync(mediaId);
        if (lyrics == null)
        {
            return NotFound(new { message = "Lyrics not found" });
        }
        return Ok(lyrics);
    }


    [HttpGet("status")]
    public async Task<IActionResult> GetAiStatus()
    {
        // Simple health check to the Python service
        try
        {
            var isAvailable = await _lyricsService.CheckAiServiceHealthAsync();
            return Ok(new { available = isAvailable });
        }
        catch
        {
            return Ok(new { available = false }); // Don't crash, just report unavailable
        }
    }

    [HttpPost("{mediaId}/generate")]
    public async Task<IActionResult> GenerateLyrics(int mediaId)
    {
        try
        {
            var lyrics = await _lyricsService.GenerateLyricsAsync(mediaId);
            return Ok(lyrics);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { message = "Media file not found" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "AI Generation failed", error = ex.Message });
        }
    [HttpPost("batch/start")]
    public IActionResult StartBatch([FromBody] BatchLyricsRequest request)
    {
        var batchId = Guid.NewGuid().ToString();
        _queue.Enqueue(new LyricsBatchJob(batchId, request.SongIds, request.Force));
        return Ok(new { batchId });
    }
}

public class BatchLyricsRequest
{
    public List<int> SongIds { get; set; } = new();
    public bool Force { get; set; } = false;
}
