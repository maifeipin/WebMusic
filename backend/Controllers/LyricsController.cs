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
    private readonly TagService _tagService;

    public LyricsController(LyricsService lyricsService, BackgroundTaskQueue queue, TagService tagService)
    {
        _lyricsService = lyricsService;
        _queue = queue;
        _tagService = tagService;
    }

    [HttpGet("{mediaId}")]
    public async Task<IActionResult> GetLyrics(int mediaId)
    {
        var lyric = await _lyricsService.GetLyricsAsync(mediaId);
        if (lyric == null)
        {
            return NotFound(new { message = "Lyrics not found" });
        }
        return Ok(new 
        {
            lyric.Id,
            lyric.Content,
            lyric.Language,
            lyric.Source,
            lyric.Version,
            Title = lyric.MediaFile?.Title ?? "Unknown Title",
            Artist = lyric.MediaFile?.Artist ?? "Unknown Artist"
        });
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
    public async Task<IActionResult> GenerateLyrics(int mediaId, [FromQuery] string lang = null, [FromQuery] string prompt = null)
    {
        try
        {
            var lyrics = await _lyricsService.GenerateLyricsAsync(mediaId, lang, prompt);
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
    }

    [HttpPost("batch/start")]
    public IActionResult StartBatch([FromBody] BatchLyricsRequest request)
    {
        var batchId = Guid.NewGuid().ToString();
        _queue.Enqueue(new LyricsBatchJob(batchId, request.SongIds, request.Force, request.Language));
        return Ok(new { batchId });
    }

    /// <summary>
    /// Uses AI (Gemini) to polish/correct LRC text while preserving timestamps.
    /// </summary>
    [HttpPost("optimize")]

    public async Task<IActionResult> OptimizeLyrics([FromBody] OptimizeLyricsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.LrcContent))
        {
            return BadRequest("LRC content is required.");
        }

        try
        {
            string contextInfo = "";
            if (request.MediaId.HasValue && request.MediaId.Value > 0)
            {
                var currentLyric = await _lyricsService.GetLyricsAsync(request.MediaId.Value);
                if (currentLyric?.MediaFile != null)
                {
                    contextInfo = $"Song Title: {currentLyric.MediaFile.Title}, Artist: {currentLyric.MediaFile.Artist}, Album: {currentLyric.MediaFile.Album}";
                }
            }

            var optimizedLrc = await _tagService.PolishLyricsAsync(request.LrcContent, contextInfo);
            
            // If MediaId provided, Save it!
            if (request.MediaId.HasValue && request.MediaId.Value > 0)
            {
                await _lyricsService.UpdateLyricsAsync(request.MediaId.Value, optimizedLrc, "Gemini (Polished)", "v2-gemini");
            }

            return Ok(new { content = optimizedLrc });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    public class OptimizeLyricsRequest
    {
        public int? MediaId { get; set; }
        public string LrcContent { get; set; } = string.Empty;
    }
}

public class BatchLyricsRequest
{
    public List<int> SongIds { get; set; } = new();
    public bool Force { get; set; } = false;
    public string Language { get; set; } = "en";
}
