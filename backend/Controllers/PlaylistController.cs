using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using System.Security.Claims;

namespace WebMusic.Backend.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PlaylistController : ControllerBase
{
    private readonly AppDbContext _context;

    public PlaylistController(AppDbContext context)
    {
        _context = context;
    }

    private int GetUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (int.TryParse(sub, out int id)) return id;
        // Fallback for dev/legacy
        return 1;
    }

    // GET: api/playlist
    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetPlaylists()
    {
        var userId = GetUserId();
        var playlists = await _context.Playlists
            .Where(p => p.UserId == userId)
            .Select(p => new 
            {
                p.Id,
                p.Name,
                p.CoverArt,
                p.CreatedAt,
                count = p.PlaylistSongs.Count()
            })
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();

        return Ok(playlists);
    }

    // POST: api/playlist
    [HttpPost]
    public async Task<ActionResult<Playlist>> CreatePlaylist([FromBody] CreatePlaylistDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name required");
        
        var userId = GetUserId();
        var playlist = new Playlist 
        { 
            Name = dto.Name,
            UserId = userId,
            CoverArt = dto.CoverArt,
            CreatedAt = DateTime.UtcNow
        };

        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetPlaylist), new { id = playlist.Id }, playlist);
    }

    // GET: api/playlist/5
    [HttpGet("{id}")]
    public async Task<ActionResult<object>> GetPlaylist(int id)
    {
        var userId = GetUserId();
        var playlist = await _context.Playlists
            .Where(p => p.Id == id && p.UserId == userId)
            .Include(p => p.PlaylistSongs)
            .ThenInclude(ps => ps.MediaFile)
            .FirstOrDefaultAsync();

        if (playlist == null) return NotFound();

        var songs = playlist.PlaylistSongs
            .Where(ps => ps.MediaFile != null)
            .OrderBy(ps => ps.AddedAt)
            .Select(ps => new 
            {
                ps.Id, // PlaylistSong ID (for removal)
                Song = ps.MediaFile,
                ps.AddedAt
            });

        return Ok(new 
        {
            playlist.Id,
            playlist.Name,
            coverArt = playlist.CoverArt,
            playlist.CreatedAt,
            Songs = songs
        });
    }

    // POST: api/playlist/5/songs
    [HttpPost("{id}/songs")]
    public async Task<IActionResult> AddSongs(int id, [FromBody] List<int> mediaFileIds)
    {
        var userId = GetUserId();
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (playlist == null) return NotFound();

        // Filter valid songs
        var validFiles = await _context.MediaFiles
            .Where(m => mediaFileIds.Contains(m.Id))
            .ToListAsync();

        foreach (var file in validFiles)
        {
            // Check duplicate
            var exists = await _context.PlaylistSongs
                .AnyAsync(ps => ps.PlaylistId == id && ps.MediaFileId == file.Id);
            
            if (!exists)
            {
                _context.PlaylistSongs.Add(new PlaylistSong
                {
                    PlaylistId = id,
                    MediaFileId = file.Id,
                    AddedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();
        return Ok();
    }

    // DELETE: api/playlist/5/songs?ids=1,2,3...
    // Only removing from playlist, not deleting the MediaFile
    [HttpDelete("{id}/songs")]
    public async Task<IActionResult> RemoveSongs(int id, [FromQuery] string ids) // ids: PlaylistSong IDs or MediaFile IDs?
    {
        // Let's assume we pass mediaFile IDs to be consistent with 'Add', 
        // OR we pass PlaylistSong IDs. 
        // User request says "multi select delete songs".
        // Passing MediaFile IDs is easier for the client if they are reusing song lists.
        
        var userId = GetUserId();
        var playlist = await _context.Playlists.FindAsync(id);
        if (playlist == null || playlist.UserId != userId) return NotFound();

        if (string.IsNullOrEmpty(ids)) return BadRequest();

        var mediaFileIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
                              .Select(int.Parse)
                              .ToList();

        var toRemove = await _context.PlaylistSongs
            .Where(ps => ps.PlaylistId == id && mediaFileIds.Contains(ps.MediaFileId))
            .ToListAsync();

        _context.PlaylistSongs.RemoveRange(toRemove);
        await _context.SaveChangesAsync();

        return NoContent();
    }
    
    // DELETE: api/playlist/5
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePlaylist(int id)
    {
        var userId = GetUserId();
        var playlist = await _context.Playlists
            .Include(p => p.PlaylistSongs)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
            
        if (playlist == null) return NotFound();

        _context.PlaylistSongs.RemoveRange(playlist.PlaylistSongs);
        _context.Playlists.Remove(playlist);
        await _context.SaveChangesAsync();

        return NoContent();
    }

    // PUT: api/playlist/5
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePlaylist(int id, [FromBody] UpdatePlaylistDto dto)
    {
        var userId = GetUserId();
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (playlist == null) return NotFound();
        
        if (!string.IsNullOrWhiteSpace(dto.Name)) playlist.Name = dto.Name;
        if (dto.CoverArt != null) playlist.CoverArt = dto.CoverArt;
        
        await _context.SaveChangesAsync();
        return Ok(playlist);
    }
}

public class CreatePlaylistDto
{
    public string Name { get; set; } = string.Empty;
    public string? CoverArt { get; set; }
}

public class UpdatePlaylistDto
{
    public string? Name { get; set; }
    public string? CoverArt { get; set; }
}
