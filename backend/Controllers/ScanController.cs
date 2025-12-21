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
public class ScanController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly BackgroundTaskQueue _queue;
    private readonly ScanStateService _scanState;
    private readonly ISmbService _smbService;

    public ScanController(AppDbContext context, BackgroundTaskQueue queue, ScanStateService scanState, ISmbService smbService)
    {
        _context = context;
        _queue = queue;
        _scanState = scanState;
        _smbService = smbService;
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

    [HttpGet("sources")]
    public async Task<ActionResult<IEnumerable<ScanSource>>> GetSources()
    {
        var userId = GetUserId();
        // Return Public (UserId is null) OR Owned (UserId == current)
        return await _context.ScanSources
            .Where(s => s.UserId == null || s.UserId == userId)
            .ToListAsync();
    }

    [HttpPost("sources")]
    public async Task<ActionResult<ScanSource>> AddSource(ScanSource source, [FromQuery] bool force = false)
    {
        // 1. Validate duplicates/nesting within the same credential
        if (source.StorageCredentialId.HasValue)
        {
            var existingSources = await _context.ScanSources
                .Where(s => s.StorageCredentialId == source.StorageCredentialId)
                .ToListAsync();

            var newPath = NormalizePath(source.Path);

            foreach (var existing in existingSources)
            {
                var existingPath = NormalizePath(existing.Path);

                // Exact match - Always Block
                if (existingPath.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("This path is already added as a source.");
                }

                // Check nesting - Warn if not forced
                bool isNested = IsSubPathOf(newPath, existingPath) || IsSubPathOf(existingPath, newPath);
                
                if (isNested && !force)
                {
                     return Conflict(new { 
                        message = $"Path overlap detected with: {existing.Path}. Continue?",
                        existingSource = existing.Path
                     });
                }
            }
        }

        // Auto-assign owner
        var userId = GetUserId();
        // If user is Admin (Id=1), maybe allow creating public sources? 
        // For now, let's make all created sources Private by default to ensure isolation.
        // Or if you want Admin's sources to be Public by default, check userId == 1.
        // Let's stick to Private for All for consistency in this "Multi-User" phase.
        source.UserId = userId;

        _context.ScanSources.Add(source);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetSources), new { id = source.Id }, source);
    }
    
    private string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }
    
    // Returns true if 'child' is inside 'parent'
    private bool IsSubPathOf(string child, string parent)
    {
        if (string.IsNullOrEmpty(parent)) return true; // Parent is root, Child is sub
        if (child.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
        {
            if (child.Length == parent.Length) return true; // Same
            if (child[parent.Length] == '/') return true; 
        }
        return false;
    }
    
    [HttpDelete("sources/{id}")]
    public async Task<IActionResult> DeleteSource(int id)
    {
        var userId = GetUserId();
        var source = await _context.ScanSources.FindAsync(id);
        
        if (source == null) return NotFound();
        
        // Security Check: Can only delete own source. 
        // If source.UserId is NULL (Public), only Admin (Id=1) should delete it? (Assuming Admin is 1)
        if (source.UserId != null && source.UserId != userId) return Forbid();
        if (source.UserId == null && userId != 1) return Forbid(); // Only admin deletes public sources
        
        _context.ScanSources.Remove(source);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("start/{id}")]
    public async Task<IActionResult> StartScan(int id)
    {
        var userId = GetUserId();
        var source = await _context.ScanSources.FindAsync(id);
        if (source == null) return NotFound();
        
        // Check access
        if (source.UserId != null && source.UserId != userId) return Forbid();

        _queue.Enqueue(new ScanJob(id, false));
        
        return Accepted(new { message = "Scan started in background" });
    }

    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        return Ok(new 
        {
            isScanning = _scanState.IsScanning,
            currentSourceId = _scanState.CurrentSourceId,
            itemsProcessed = _scanState.ItemsProcessed,
            statusMessage = _scanState.StatusMessage,
            error = _scanState.LastError
        });
    }

    [HttpPost("browse")]
    public async Task<IActionResult> Browse([FromBody] BrowseRequest request)
    {
        var cred = await _context.StorageCredentials.FindAsync(request.CredentialId);
        if (cred == null) return NotFound("Credential not found");

        var items = ((SmbService)_smbService).ListContents(cred, request.Path ?? "");
        return Ok(items);
    }
}

public class BrowseRequest
{
    public int CredentialId { get; set; }
    public string? Path { get; set; }
}
