using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;
using WebMusic.Backend.Models;
using Newtonsoft.Json;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TagsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly TagService _tagService;
    private readonly BackgroundTaskQueue _queue;

    public TagsController(AppDbContext context, TagService tagService, BackgroundTaskQueue queue)
    {
        _context = context;
        _tagService = tagService;
        _queue = queue;
    }

    public class SuggestRequest
    {
        public List<int> SongIds { get; set; } = new();
        public string Prompt { get; set; } = string.Empty;
        public string Model { get; set; } = "gemini-2.0-flash-exp";
    }

    [HttpPost("suggest")]
    public async Task<IActionResult> SuggestTags([FromBody] SuggestRequest req)
    {
        if (req.SongIds.Count == 0) return BadRequest("No songs selected");
        if (req.SongIds.Count > 50) return BadRequest("Batch size limit is 50");

        var songs = await _context.MediaFiles
            .Where(m => req.SongIds.Contains(m.Id))
            .ToListAsync(); // Fetch all first to do path manipulation in memory

        if (songs.Count == 0) return NotFound("Songs not found");

        var contextData = songs.Select(m => {
             // Manual path parsing to be cross-platform safe
             var path = m.FilePath.Replace('\\', '/');
             var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
             var fileName = segments.LastOrDefault() ?? "";
             var parentFolder = segments.Length > 1 ? segments[segments.Length - 2] : "";

             return new {
                m.Id,
                m.Title,
                m.Artist,
                m.Album,
                m.Genre,
                m.Year,
                // Context: Give AI the filename AND the immediate parent folder name
                // This helps if the folder is named "Artist - Album"
                FileName = fileName,
                FolderName = parentFolder
            };
        }).ToList();

        try 
        {
            var jsonResult = await _tagService.GenerateTagsAsync(req.Prompt, contextData, req.Model);
            var suggestions = JsonConvert.DeserializeObject<List<SuggestedTag>>(jsonResult);
            return Ok(suggestions);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "AI processing failed", details = ex.Message });
        }
    }

    [HttpPost("batch/start")]
    public IActionResult StartBatch([FromBody] SuggestRequest req)
    {
        if (req.SongIds.Count == 0) return BadRequest("No songs selected");
        if (req.SongIds.Count > 1000) return BadRequest("Batch size limit is 1000. Please select fewer songs.");
        
        var batchId = Guid.NewGuid().ToString("N");
        var job = new AiBatchJob(batchId, req.SongIds, req.Prompt, req.Model);
        
        _queue.Enqueue(job);
        
        return Ok(new { batchId, message = "Batch started" });
    }

    [HttpGet("batch/{batchId}")]
    public IActionResult GetBatchStatus(string batchId)
    {
        var status = _queue.GetAiStatus(batchId);
        if (status == null) return NotFound();
        return Ok(status);
    }

    public class SuggestedTag
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public string Genre { get; set; } = string.Empty;
        public int Year { get; set; }
    }

    [HttpPost("apply")]
    public async Task<IActionResult> ApplyTags([FromBody] List<SuggestedTag> updates)
    {
        if (updates == null || updates.Count == 0) return Ok(0);

        var ids = updates.Select(u => u.Id).ToList();
        var songs = await _context.MediaFiles.Where(m => ids.Contains(m.Id)).ToListAsync();

        int applied = 0;
        foreach (var update in updates)
        {
            var song = songs.FirstOrDefault(s => s.Id == update.Id);
            if (song != null)
            {
                // Only update if not empty, or business rule?
                // For now, trust the AI/User explicit accept
                song.Title = update.Title;
                song.Artist = update.Artist;
                song.Album = update.Album;
                song.Genre = update.Genre;
                song.Year = update.Year;
                applied++;
            }
        }

        await _context.SaveChangesAsync();
        return Ok(new { applied });
    }
}
