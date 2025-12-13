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

    // GET: api/playlist?type=normal|shared|all
    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetPlaylists([FromQuery] string type = "normal")
    {
        var userId = GetUserId();
        var query = _context.Playlists.Where(p => p.UserId == userId);
        
        // Filter by type
        if (type != "all")
        {
            query = query.Where(p => p.Type == type);
        }
        
        var playlists = await query
            .Select(p => new 
            {
                p.Id,
                p.Name,
                p.CoverArt,
                p.CreatedAt,
                p.Type,
                p.ShareToken,
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

    // ================== SHARING ENDPOINTS ==================

    /// <summary>
    /// Share a playlist or selected songs.
    /// If songIds provided, creates a new "shared" type playlist with those songs.
    /// If no songIds, shares the existing playlist directly.
    /// </summary>
    [HttpPost("{id}/share")]
    public async Task<IActionResult> SharePlaylist(int id, [FromBody] SharePlaylistDto dto)
    {
        var userId = GetUserId();
        var playlist = await _context.Playlists
            .Include(p => p.PlaylistSongs)
            .FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        
        if (playlist == null) return NotFound();

        // Case 1: Share selected songs -> create new "shared" playlist
        if (dto.SongIds != null && dto.SongIds.Count > 0)
        {
            // Validate songs belong to this playlist
            var validSongIds = playlist.PlaylistSongs
                .Where(ps => dto.SongIds.Contains(ps.MediaFileId))
                .Select(ps => ps.MediaFileId)
                .ToList();
            
            if (validSongIds.Count == 0) return BadRequest("No valid songs selected");

            // Create new shared playlist
            var sharedPlaylist = new Playlist
            {
                Name = string.IsNullOrWhiteSpace(dto.Name) ? $"{playlist.Name} - Share" : dto.Name.Trim(),
                UserId = userId,
                Type = "shared",
                ShareToken = Guid.NewGuid().ToString("N"),
                CreatedAt = DateTime.UtcNow
            };
            _context.Playlists.Add(sharedPlaylist);
            await _context.SaveChangesAsync();

            // Add songs
            foreach (var songId in validSongIds)
            {
                _context.PlaylistSongs.Add(new PlaylistSong
                {
                    PlaylistId = sharedPlaylist.Id,
                    MediaFileId = songId,
                    AddedAt = DateTime.UtcNow
                });
            }
            await _context.SaveChangesAsync();

            // Set share properties
            DateTime? expiresAt = null;
            if (dto.ExpiresInDays.HasValue && dto.ExpiresInDays.Value > 0)
            {
                expiresAt = DateTime.UtcNow.AddDays(dto.ExpiresInDays.Value);
            }
            
            sharedPlaylist.SharePassword = string.IsNullOrEmpty(dto.Password) ? null : dto.Password;
            sharedPlaylist.ShareExpiresAt = expiresAt;
            await _context.SaveChangesAsync();

            return Ok(new
            {
                shareToken = sharedPlaylist.ShareToken,
                shareUrl = $"/share/{sharedPlaylist.ShareToken}",
                name = sharedPlaylist.Name,
                songCount = validSongIds.Count,
                isNewPlaylist = true
            });
        }
        
        // Case 2: Share entire playlist
        if (string.IsNullOrEmpty(playlist.ShareToken))
        {
            playlist.ShareToken = Guid.NewGuid().ToString("N");
        }

        // Update share settings (always update if provided)
        DateTime? expiresAtGlobal = null;
        if (dto.ExpiresInDays.HasValue && dto.ExpiresInDays.Value > 0)
        {
            expiresAtGlobal = DateTime.UtcNow.AddDays(dto.ExpiresInDays.Value);
        }
        playlist.ShareExpiresAt = expiresAtGlobal;
        playlist.SharePassword = string.IsNullOrEmpty(dto.Password) ? null : dto.Password;

        await _context.SaveChangesAsync();

        return Ok(new
        {
            shareToken = playlist.ShareToken,
            shareUrl = $"/share/{playlist.ShareToken}",
            name = playlist.Name,
            songCount = playlist.PlaylistSongs.Count,
            isNewPlaylist = false
        });
    }

    /// <summary>
    /// Revoke sharing for a playlist.
    /// </summary>
    [HttpDelete("{id}/share")]
    public async Task<IActionResult> RevokeShare(int id)
    {
        var userId = GetUserId();
        var playlist = await _context.Playlists.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (playlist == null) return NotFound();

        playlist.ShareToken = null;
        playlist.ShareExpiresAt = null;
        playlist.SharePassword = null;
        await _context.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// Get shared playlist by token (PUBLIC - no authentication required).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("shared/{token}")]
    public async Task<IActionResult> GetSharedPlaylist(string token, [FromQuery] string? password = null)
    {
        if (string.IsNullOrEmpty(token)) return BadRequest("Token required");

        var playlist = await _context.Playlists
            .Where(p => p.ShareToken == token)
            .Include(p => p.PlaylistSongs)
            .ThenInclude(ps => ps.MediaFile)
            .FirstOrDefaultAsync();

        if (playlist == null) return NotFound("Shared playlist not found");

        // Check Expiry
        if (playlist.ShareExpiresAt.HasValue && DateTime.UtcNow > playlist.ShareExpiresAt.Value)
        {
            return StatusCode(410, "Shared playlist has expired");
        }

        // Check Password
        if (!string.IsNullOrEmpty(playlist.SharePassword))
        {
            if (string.IsNullOrEmpty(password) || password != playlist.SharePassword)
            {
                return StatusCode(401, new { code = "PASSWORD_REQUIRED", hint = "This playlist is password protected" });
            }
        }

        var songs = playlist.PlaylistSongs
            .Where(ps => ps.MediaFile != null)
            .OrderBy(ps => ps.AddedAt)
            .Select(ps => new
            {
                id = ps.MediaFile!.Id,
                title = ps.MediaFile.Title,
                artist = ps.MediaFile.Artist,
                album = ps.MediaFile.Album,
                duration = ps.MediaFile.Duration.TotalSeconds
            });

        return Ok(new
        {
            playlist.Name,
            coverArt = playlist.CoverArt,
            shareToken = token,
            songs
        });
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

public class SharePlaylistDto
{
    public string? Name { get; set; }
    public List<int>? SongIds { get; set; }
    public string? Password { get; set; }
    public int? ExpiresInDays { get; set; } // -1 or null means never
}

