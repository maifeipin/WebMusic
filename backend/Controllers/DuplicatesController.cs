using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using WebMusic.Backend.Services;
using System.Security.Claims;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class DuplicatesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ISmbService _smbService;
    private readonly PathResolver _pathResolver;
    private readonly ILogger<DuplicatesController> _logger;

    public DuplicatesController(AppDbContext context, ISmbService smbService, PathResolver pathResolver, ILogger<DuplicatesController> logger)
    {
        _context = context;
        _smbService = smbService;
        _pathResolver = pathResolver;
        _logger = logger;
    }

    private int GetUserId()
    {
        var claim = User.FindFirst("sub") ?? User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && int.TryParse(claim.Value, out int userId))
        {
            return userId;
        }
        return 0;
    }

    [HttpGet]
    public async Task<IActionResult> GetDuplicates([FromQuery] string? path = null)
    {
        var userId = GetUserId();
        // Restrict to Admin (ID 1)
        if (userId != 1)
        {
            return StatusCode(403, "Only the admin can manage duplicates.");
        }
        
        // 1. Determine Scope (Source/Path)
        var sources = await _context.ScanSources.ToListAsync(); // Admin sees all sources
        var allowedSourceIds = sources.Select(s => s.Id).ToList();

        var query = _context.MediaFiles
            .Where(m => allowedSourceIds.Contains(m.ScanSourceId))
            .AsQueryable();

        // 2. Apply Path Filter (Recursive)
        if (!string.IsNullOrEmpty(path))
        {
            var resolved = _pathResolver.ResolveFrontendToDbPath(path, sources);
            if (!string.IsNullOrEmpty(resolved.DbPath))
            {
                var dbPath = resolved.DbPath.Replace("\\", "/");
                query = query.Where(m => 
                    m.ParentPath.Replace("\\", "/") == dbPath || 
                    m.ParentPath.Replace("\\", "/").StartsWith(dbPath + "/"));
            }
            else if (resolved.HasMatch && resolved.MatchedSource != null)
            {
                query = query.Where(m => m.ScanSourceId == resolved.MatchedSource.Id);
            }
        }

        // 3. Find Duplicates
        // Fetch necessary data for "Smart Keep" strategy: Date, Cover, Favorites
        var items = await query
            .Select(m => new { 
                m.Id, 
                m.FileHash, 
                m.SizeBytes, 
                m.FilePath, 
                m.Title, 
                m.Artist, 
                m.Album, 
                m.AddedAt,
                m.CoverArt,
                IsFavorite = m.Favorites.Any(), // If favorited by ANY user
                PlaylistCount = m.PlaylistSongs.Count // If in ANY playlist
            })
            .ToListAsync();

        var duplicates = items
            .Where(x => !string.IsNullOrEmpty(x.FileHash) && !x.FileHash.StartsWith("nohash"))
            .GroupBy(x => new { x.FileHash, x.SizeBytes }) 
            .Where(g => g.Count() > 1)
            .Select(g => new 
            {
                Hash = g.Key.FileHash,
                Size = g.Key.SizeBytes,
                FileCount = g.Count(),
                Files = g.OrderByDescending(f => f.IsFavorite) // Smart Sort: Favorites first
                         .ThenByDescending(f => !string.IsNullOrEmpty(f.CoverArt)) // Then cover art
                         .ThenByDescending(f => f.PlaylistCount > 0) // Then in playlists
                         .ThenBy(f => f.AddedAt) // Then oldest
                         .ToList()
            })
            .OrderByDescending(g => g.Size * (g.FileCount - 1)) // Sort groups by wasted space
            .ToList();

        return Ok(duplicates);
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup([FromBody] List<int> ids)
    {
        var userId = GetUserId();
        if (userId != 1) return StatusCode(403, "Only the admin can manage duplicates.");

        if (ids == null || ids.Count == 0) return BadRequest("No IDs provided");

        // 1. Get Media Files with Source details
        var songs = await _context.MediaFiles
            .Include(m => m.ScanSource)
            .ThenInclude(s => s!.StorageCredential)
            .Where(m => ids.Contains(m.Id))
            .ToListAsync();

        int successCount = 0;
        int failedCount = 0;

        // Perform sequentially
        foreach (var song in songs)
        {
            // Permission Check: Admin (1) can delete anything. 
            // Others are already blocked by top-level check, but keeping logic safe:
            if (userId != 1 && song.ScanSource?.UserId != null && song.ScanSource.UserId != userId)
            {
                _logger.LogWarning($"User {userId} attempted to delete song {song.Id} owned by {song.ScanSource.UserId}");
                failedCount++;
                continue;
            }

            bool deleteSuccess = false;

            // A. Physical Delete
            try
            {
                if (song.ScanSource != null)
                {
                    // Check if file still exists? 
                    // SmbService.Delete returns false if fails.
                    // Note: If false, maybe file already gone? We should allow DB delete if file gone?
                    // SmbService.Delete logic: Open -> SetDisposition -> Close.
                    // If Open fails (not found), it logs.
                    // We should treat "File Not Found" as "Physical Delete Success" (outcome achieved).
                    // But standard Delete returns false.
                    // Let's try Delete.
                    
                    bool physDeleted = _smbService.Delete(song.ScanSource, song.FilePath, false);
                    if (physDeleted)
                    {
                        deleteSuccess = true;
                    }
                    else
                    {
                        // Check if file exists to decide if we should proceed
                        // Optimization: Just assume strict mode for now. Safe deletion.
                        _logger.LogWarning($"Physical delete failed for {song.FilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, $"Exception during physical delete: {song.FilePath}");
            }

            // B. DB Delete (Only if physical success OR if forced/consistent)
            // For this feature "Cleanup", we assume user wants to free space.
            // If physical delete failed, we should NOT delete DB record, otherwise user thinks space is freed but it's not.
            if (deleteSuccess)
            {
                try 
                {
                    // Manual Cascade
                    var lyrics = _context.Lyrics.Where(l => l.MediaFileId == song.Id);
                    _context.Lyrics.RemoveRange(lyrics);

                    var history = _context.PlayHistories.Where(h => h.MediaFileId == song.Id);
                    _context.PlayHistories.RemoveRange(history);
                    
                    var favs = _context.Favorites.Where(f => f.MediaFileId == song.Id);
                    _context.Favorites.RemoveRange(favs);

                    var plSongs = _context.PlaylistSongs.Where(ps => ps.MediaFileId == song.Id);
                    _context.PlaylistSongs.RemoveRange(plSongs);

                    _context.MediaFiles.Remove(song);
                    successCount++;
                }
                catch (Exception dbEx)
                {
                    _logger.LogError(dbEx, $"DB Delete failed for {song.Id}");
                    // Resume? Transaction? 
                    // We are in loop, context tracks changes.
                    // If we fail here, SaveChanges might fail for everything.
                    // Ideally we should process batches or individual SaveChanges if we want partial success.
                    // But individual SaveChanges is slow.
                    // Let's mark this song as Detached/Unchanged to not break others?
                    // Reload context?
                }
            }
            else
            {
                failedCount++;
            }
        }

        await _context.SaveChangesAsync();

        return Ok(new { successCount, failedCount });
    }
}
