using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using System.Security.Claims;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class UserController : ControllerBase
{
    private readonly AppDbContext _context;

    public UserController(AppDbContext context)
    {
        _context = context;
    }

    private int GetUserId()
    {
        // With DefaultInboundClaimTypeMap.Clear(), the "sub" claim is NOT mapped to ClaimTypes.NameIdentifier
        var claim = User.FindFirst("sub") ?? User.FindFirst(ClaimTypes.NameIdentifier);
        
        if (claim != null && int.TryParse(claim.Value, out int userId))
        {
            return userId;
        }
        
        // Return 0 or throw? Return 0 will likely result in empty data, which is fail-safe but maybe confusing.
        // For debugging, let's log.
        Console.WriteLine($"[AuthWarning] Could not parse UserId. Claims: {string.Join(", ", User.Claims.Select(c => $"{c.Type}={c.Value}"))}");
        return 0;
    }

    // --- History ---

    [HttpPost("history/{mediaId}")]
    public async Task<IActionResult> AddHistory(int mediaId)
    {
        var userId = GetUserId();
        
        // Upsert: If exists, update time. If not, add.
        var existing = await _context.PlayHistories
            .FirstOrDefaultAsync(h => h.UserId == userId && h.MediaFileId == mediaId);

        if (existing != null)
        {
            existing.PlayedAt = DateTime.UtcNow;
            _context.PlayHistories.Update(existing);
        }
        else
        {
            _context.PlayHistories.Add(new PlayHistory
            {
                UserId = userId,
                MediaFileId = mediaId,
                PlayedAt = DateTime.UtcNow
            });
        }

        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var userId = GetUserId();
        var query = _context.PlayHistories
            .Include(h => h.MediaFile)
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(h => new 
            {
                h.MediaFileId,
                h.PlayedAt,
                MediaFile = new {
                    h.MediaFile!.Id,
                    h.MediaFile.Title,
                    h.MediaFile.Artist,
                    h.MediaFile.Album,
                    h.MediaFile.Genre,
                    Duration = h.MediaFile.Duration.TotalSeconds,
                    h.MediaFile.FilePath
                }
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    // --- Favorites ---

    [HttpPost("favorite/{mediaId}")]
    public async Task<IActionResult> ToggleFavorite(int mediaId)
    {
        var userId = GetUserId();
        
        var existing = await _context.Favorites
            .FirstOrDefaultAsync(f => f.UserId == userId && f.MediaFileId == mediaId);

        if (existing != null)
        {
            _context.Favorites.Remove(existing);
            await _context.SaveChangesAsync();
            return Ok(new { isFavorite = false });
        }
        else
        {
            _context.Favorites.Add(new Favorite
            {
                UserId = userId,
                MediaFileId = mediaId,
                CreatedAt = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();
            return Ok(new { isFavorite = true });
        }
    }

    [HttpGet("favorites")]
    public async Task<IActionResult> GetFavorites([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var userId = GetUserId();
        var query = _context.Favorites
            .Include(f => f.MediaFile)
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt);

        var total = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(f => new 
            {
                f.MediaFileId,
                f.CreatedAt,
                MediaFile = new {
                    f.MediaFile!.Id,
                    f.MediaFile.Title,
                    f.MediaFile.Artist,
                    f.MediaFile.Album,
                    f.MediaFile.Genre,
                    Duration = f.MediaFile.Duration.TotalSeconds,
                    f.MediaFile.FilePath
                }
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }
    
    [HttpGet("favorites/ids")]
    public async Task<IActionResult> GetFavoriteIds()
    {
        var userId = GetUserId();
        var ids = await _context.Favorites
            .Where(f => f.UserId == userId)
            .Select(f => f.MediaFileId)
            .ToListAsync();
        return Ok(ids);
    }

    // --- Dashboard Stats ---

    [HttpGet("stats")]
    public async Task<IActionResult> GetDashboardStats()
    {
        var userId = GetUserId();

        var historyCount = await _context.PlayHistories.CountAsync(h => h.UserId == userId);
        var favoriteCount = await _context.Favorites.CountAsync(f => f.UserId == userId);

        var historyTop10 = await _context.PlayHistories
            .Include(h => h.MediaFile)
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt)
            .Take(50)
            .Select(h => new {
                h.MediaFile!.Id,
                h.MediaFile.Title,
                h.MediaFile.Artist,
                h.MediaFile.Album,
                h.MediaFile.Genre,
                Duration = h.MediaFile.Duration.TotalSeconds,
                h.MediaFile.FilePath
            })
            .ToListAsync();

        var favoritesTop10 = await _context.Favorites
            .Include(f => f.MediaFile)
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Take(50)
            .Select(f => new {
                f.MediaFile!.Id,
                f.MediaFile.Title,
                f.MediaFile.Artist,
                f.MediaFile.Album,
                f.MediaFile.Genre,
                Duration = f.MediaFile.Duration.TotalSeconds,
                f.MediaFile.FilePath
            })
            .ToListAsync();

        return Ok(new 
        { 
            history = new { count = historyCount, top10 = historyTop10 },
            favorites = new { count = favoriteCount, top10 = favoritesTop10 }
        });
    }
    // --- Import / Export ---
    
    [HttpGet("favorites/export")]
    public async Task<IActionResult> ExportFavorites()
    {
        var userId = GetUserId();
        var paths = await _context.Favorites
            .Include(f => f.MediaFile)
            .Where(f => f.UserId == userId && f.MediaFile != null)
            .Select(f => f.MediaFile!.FilePath)
            .ToListAsync();
            
        var content = string.Join("\n", paths);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return File(bytes, "text/plain", $"favorites_export_{DateTime.Now:yyyyMMdd}.txt");
    }

    [HttpPost("favorites/import")]
    public async Task<IActionResult> ImportFavorites([FromForm] IFormFile file)
    {
        var userId = GetUserId();
        if (file == null || file.Length == 0) return BadRequest("File empty");

        using var reader = new StreamReader(file.OpenReadStream());
        var content = await reader.ReadToEndAsync();
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        int imported = 0;
        foreach (var path in lines)
        {
            var trimmedPath = path.Trim();
            if (string.IsNullOrWhiteSpace(trimmedPath)) continue;

            // Find MediaFile by path
            var media = await _context.MediaFiles
                .FirstOrDefaultAsync(m => m.FilePath == trimmedPath);

            if (media != null)
            {
                // Check if already favorite
                var exists = await _context.Favorites
                    .AnyAsync(f => f.UserId == userId && f.MediaFileId == media.Id);

                if (!exists)
                {
                    _context.Favorites.Add(new Favorite
                    {
                        UserId = userId,
                        MediaFileId = media.Id,
                        CreatedAt = DateTime.UtcNow
                    });
                    imported++;
                }
            }
        }
        
        if (imported > 0) await _context.SaveChangesAsync();

        return Ok(new { imported, total = lines.Length });
    }
}
