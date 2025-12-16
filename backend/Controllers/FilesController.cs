using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;
using WebMusic.Backend.Models;
using System.IO;
using System.Threading.Tasks;

namespace WebMusic.Backend.Controllers;

[ApiController]
[Route("api/files")]
public class FilesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ISmbService _smbService;
    private readonly ScannerService _scannerService;
    private readonly ILogger<FilesController> _logger;

    public FilesController(AppDbContext context, ISmbService smbService, ScannerService scannerService, ILogger<FilesController> logger)
    {
        _context = context;
        _smbService = smbService;
        _scannerService = scannerService;
        _logger = logger;
    }

    [HttpGet("browse")]
    public async Task<IActionResult> Browse([FromQuery] int? sourceId, [FromQuery] string path = "")
    {
        if (sourceId == null || sourceId == 0)
        {
            // List Sources as root
            var sources = await _context.ScanSources.ToListAsync();
            var items = sources.Select(s => new {
                Name = s.Name,
                Type = "Source",
                Path = s.Path, // Return actual Source Path for display
                SourceId = s.Id
            }).ToList();
            return Ok(items);
        }

        var source = await _context.ScanSources
            .Include(s => s.StorageCredential)
            .FirstOrDefaultAsync(s => s.Id == sourceId);

        if (source == null) return NotFound("Source not found");

        try
        {
            // List contents relative to source path
            // path is relative to source.Path
            
            // SmbService.ListContents takes full config. We need to adapt it.
            // SmbService.ListContents logic relies on credential, and treats path as Share/Folder
            // But source.Path defines the root.
            // We need a method in SmbService that lists relative to ScanSource.
            // Current ListContents is "Raw SMB" (Share/Folder).
            // Current ListFiles is "Relative to Source".
            
            // Let's use ListContents but we must resolve the path correctly.
            // Actually, for "File Browser", raw SMB view might be confusing if user expects Source Root.
            // But we implemented "ListFiles" (recursive string list) and "ListContents" (one level detailed).
            // ListContents returns BrowsableItem. 
            // We need to resolve the full relative path if Source has a subfolder.
            // SmbService.cs:83 ParseSourcePath(source, out server, out share, out baseDir);
            
            // Re-implement path logic here or use SmbService helper?
            // Let's rely on SmbService logic.
            // We need to pass the FULL path (BaseDir + RelativePath) to ListContents?
            // No, ListContents takes (Credential, Path). Path = Share/Base/Rel.
            
            // Hacky solution: Construct full path
            string fullPath = GetFullPath(source, path);
            var items = _smbService.ListContents(source.StorageCredential!, fullPath);
            
            return Ok(items.Select(i => new {
                i.Name,
                i.Type,
                // Client only cares about relative path from Source Root?
                // Or we keep full path state in client? 
                // Let's return the Relative Path that Client should send next.
                Path = string.IsNullOrEmpty(path) ? i.Name : $"{path}/{i.Name}",
                SourceId = sourceId
            }));
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] FileOpRequest request)
    {
        var source = await _context.ScanSources
            .Include(s => s.StorageCredential)
            .FirstOrDefaultAsync(s => s.Id == request.SourceId);
        if (source == null) return NotFound("Source not found");

        try
        {
             string properPath = GetRelativePathToShare(source, request.Path);
             _logger.LogInformation($"Delete Request - Source: {source.Name}, RelPath: {request.Path}, FullRel: {properPath}, IsDir: {request.IsDirectory}");
             
             if (_smbService.Delete(source, properPath, request.IsDirectory)) return Ok(new { success = true });
             return BadRequest("Failed to delete item (It might be non-empty or locked)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete Failed");
            return StatusCode(500, ex.Message);
        }
    }
    
    [HttpPost("mkdir")]
    public async Task<IActionResult> Mkdir([FromBody] FileOpRequest request)
    {
        var source = await _context.ScanSources
            .Include(s => s.StorageCredential)
            .FirstOrDefaultAsync(s => s.Id == request.SourceId);
        if (source == null) return NotFound();

        try
        {
            _logger.LogInformation($"Mkdir Request - Source: {source.Name}, RawPath: {source.Path}, RequestPath: {request.Path}");
            
            string properPath = GetRelativePathToShare(source, request.Path);
            _logger.LogInformation($"Computed Relative Path to Share: {properPath}");
            
            if (_smbService.CreateDirectory(source, properPath)) return Ok();
            return BadRequest("Failed to create directory");
        }
        catch (Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Upload([FromQuery] int sourceId, [FromQuery] string path, [FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest("No file");

        var source = await _context.ScanSources
            .Include(s => s.StorageCredential)
            .FirstOrDefaultAsync(s => s.Id == sourceId);
        if (source == null) return NotFound("Source not found");
        
        // Target Path
        string properPath = GetRelativePathToShare(source, path);
        // properPath is directory. Append filename.
        string filePath;
        if (string.IsNullOrEmpty(properPath)) filePath = file.FileName;
        else filePath = $"{properPath}/{file.FileName}";
        
        _logger.LogInformation($"Uploading to {filePath}...");

        using var smbStream = _smbService.OpenWriteFile(source, filePath);
        if (smbStream == null) 
        {
             _logger.LogError($"OpenWriteFile returned null for {filePath}");
             return StatusCode(500, "Failed to open SMB stream (Null)");
        }

        try {
            // Copy
            await file.CopyToAsync(smbStream);
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, $"Upload CopyToAsync Failed: {ex.Message}");
             return StatusCode(500, $"Upload Failed: {ex.Message}");
        }
        finally
        {
            smbStream.Close();
        }
        
        // Smart Indexing
        if (IsMusicFile(file.FileName))
        {
            // Fire and forget indexing? Or await?
            // Await is prompt.
            // We need to index this file.
            // ScanFileAsync expects path relative to what?
            // SmbService.OpenFile takes path. It does Connect(source).
            // So we pass filePath (Relative to Share).
            await _scannerService.IndexSingleFileAsync(sourceId, filePath);
        }

        return Ok(new { success = true });
    }

    // Helper to resolve paths
    // Source Path: smb://server/share/base
    // Browse Path (from client): sub/folder
    // We need: base/sub/folder (Relative to Share) OR Share/base/sub/folder (Full SMB)
    // SmbService methods vary.
    // ListContents takes (Credential, FullPathIncludingShare).
    // OpenWriteFile/CreateDirectory takes (ScanSource, PathRelativeToShare).
    
    private string GetFullPath(ScanSource source, string relativePath)
    {
        // For ListContents (Raw SMB)
        string baseUri = source.Path;
        if (baseUri.StartsWith("smb://")) {
             baseUri = baseUri.Substring(6); // server/share/base
             var parts = baseUri.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
             // parts[0] is server. 
             // We need Share/...
             string sharePath = string.Join("/", parts.Skip(1)); // share/base
             baseUri = sharePath;
        }
        else 
        {
             // If "Download", it is share. return as is.
             // If "Download/Sub", return as is.
             baseUri = baseUri.Replace('\\', '/');
        }

        string full = baseUri;
        if (!string.IsNullOrEmpty(relativePath))
        {
             full = string.IsNullOrEmpty(full) ? relativePath : $"{full}/{relativePath}";
        }
        return full;
    }

    private string GetRelativePathToShare(ScanSource source, string relativePath)
    {
        // Source: smb://server/share/base
        // Request: sub
        // Result: base/sub
        
        string raw = source.Path;
        string baseDir = "";
        
        if (raw.StartsWith("smb://"))
        {
             var uri = new Uri(raw);
             if (uri.Segments.Length > 2)
             {
                 baseDir = string.Join("", uri.Segments.Skip(2)).Trim('/');
             }
        }
        else
        {
            // Handle "Share/Dir" format to match SmbService logic
            // Normalize slashes
            var norm = raw.Replace('\\', '/');
            if (norm.Contains('/'))
            {
                var parts = norm.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1) 
                {
                    baseDir = parts[1];
                }
            }
        }

        baseDir = baseDir.Replace('\\', '/'); // Ensure forward slash for composition


        if (string.IsNullOrEmpty(baseDir)) return relativePath;
        if (string.IsNullOrEmpty(relativePath)) return baseDir;
        
        return $"{baseDir}/{relativePath}";
    }
    
    private bool IsMusicFile(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLower();
        return ext == ".mp3" || ext == ".flac" || ext == ".m4a" || ext == ".wav";
    }
}

public class FileOpRequest
{
    public int SourceId { get; set; }
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; } // Added for Delete operation
}
