using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;
using WebMusic.Backend.Models;
using System.IO;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ISmbService _smbService;
    private readonly PathResolver _pathResolver;

    public MediaController(AppDbContext context, ISmbService smbService, PathResolver pathResolver)
    {
        _context = context;
        _smbService = smbService;
        _pathResolver = pathResolver;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<MediaFile>>> GetFiles(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50, 
        [FromQuery] string? search = null,
        [FromQuery] string? filterBy = null,
        [FromQuery] string? filterValue = null,
        [FromQuery] string? path = null,
        [FromQuery] bool recursive = false)
    {
        var query = _context.MediaFiles.AsQueryable();

        // Path Filtering (Directory Playback) - Using PathResolver for consistent path handling
        if (!string.IsNullOrEmpty(path))
        {
            var sources = await _context.ScanSources.ToListAsync();
            var resolved = _pathResolver.ResolveFrontendToDbPath(path, sources);
            
            if (recursive)
            {
                if (string.IsNullOrEmpty(resolved.DbPath) && resolved.HasMatch)
                {
                    // Root of share: filter by source ID
                    query = query.Where(m => m.ScanSourceId == resolved.MatchedSource!.Id);
                }
                else if (!string.IsNullOrEmpty(resolved.DbPath))
                {
                    // Recursive: ParentPath == dbPath OR starts with dbPath + "/"
                    var dbPath = resolved.DbPath;
                    query = query.Where(m => 
                        m.ParentPath.Replace("\\", "/") == dbPath || 
                        m.ParentPath.Replace("\\", "/").StartsWith(dbPath + "/"));
                }
            }
            else
            {
                // Non-Recursive: exact match
                var dbPath = resolved.DbPath;
                query = query.Where(m => m.ParentPath.Replace("\\", "/") == dbPath);
            }
        }

        if (!string.IsNullOrEmpty(search))
        {
            search = search.ToLower();
            query = query.Where(m => 
                m.Title.ToLower().Contains(search) || 
                m.Artist.ToLower().Contains(search) ||
                m.Album.ToLower().Contains(search));
        }

        if (!string.IsNullOrEmpty(filterBy) && !string.IsNullOrEmpty(filterValue))
        {
            switch (filterBy.ToLower())
            {
                case "artist": query = query.Where(m => m.Artist == filterValue); break;
                case "album": query = query.Where(m => m.Album == filterValue); break;
                case "genre": query = query.Where(m => m.Genre == filterValue); break;
            }
        }

        var total = await query.CountAsync();
        var files = await query
            .OrderBy(m => m.Title) // Consistent numbering
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new { 
                m.Id, m.Title, m.Artist, m.Album, m.Genre, m.Duration.TotalSeconds, m.Year, m.FilePath 
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, files });
    }

    [HttpGet("groups")]
    public async Task<IActionResult> GetGroups([FromQuery] string groupBy)
    {
        // Returns list of unique values for a column + count
        var query = _context.MediaFiles.AsQueryable();
        
        // This dynamic grouping is limited in EF Core without raw SQL or expression trees.
        // For MVP, simple switch is safest.
        
        switch (groupBy.ToLower())
        {
            case "artist":
                var artists = await query.GroupBy(m => m.Artist)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Key)
                    .ToListAsync();
                return Ok(artists);
            
            case "album":
                 var albums = await query.GroupBy(m => m.Album)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Key)
                    .ToListAsync();
                return Ok(albums);

            case "genre":
                 var genres = await query.GroupBy(m => m.Genre)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Key)
                    .ToListAsync();
                return Ok(genres);
                
            case "year":
                 var years = await query.GroupBy(m => m.Year)
                    .Select(g => new { Key = g.Key.ToString(), Count = g.Count() })
                    .OrderByDescending(x => x.Key)
                    .ToListAsync();
                return Ok(years);

            case "directory":
                 var dirs = await query.GroupBy(m => m.ParentPath)
                    .Select(g => new { Key = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Key)
                    .ToListAsync();
                return Ok(dirs);

            default:
                return BadRequest("Invalid group by column");
        }
    }

    [HttpGet("directory")]
    public async Task<IActionResult> GetDirectory([FromQuery] string? path = "")
    {
        var cleanPath = _pathResolver.NormalizePath(path);

        if (string.IsNullOrEmpty(cleanPath))
        {
            // Root Level: Return Configured Sources as "Roots"
            var sources = await _context.ScanSources
                .Select(s => new { s.Id, s.Name, s.Path })
                .ToListAsync();
                
            var rootList = new List<object>();
            
            foreach (var s in sources)
            {
                var count = await _context.MediaFiles.CountAsync(m => m.ScanSourceId == s.Id);
                var normPath = _pathResolver.NormalizePath(s.Path);
                var displayName = string.IsNullOrWhiteSpace(s.Name) ? normPath : s.Name;
                
                rootList.Add(new { 
                    Type = "Folder", 
                    Id = 0, 
                    Name = displayName, 
                    Path = normPath,
                    Artist = "", 
                    Album = "", 
                    Count = count 
                });
            }
            
            return Ok(rootList);
        }
        else
        {
            // Sub-Folder Level: Find children using PathResolver
            var sources = await _context.ScanSources.ToListAsync();
            var resolved = _pathResolver.ResolveFrontendToDbPath(path, sources);
            var dbPath = resolved.DbPath;
            
            // 1. Folders: In-Memory aggregation
            var query = _context.MediaFiles.AsQueryable();
            if (!string.IsNullOrEmpty(dbPath))
            {
                query = query.Where(m => m.ParentPath.Contains(dbPath) || m.ParentPath.Replace("\\", "/").Contains(dbPath));
            }
            
            var pathCounts = await query
                .GroupBy(m => m.ParentPath)
                .Select(g => new { Path = g.Key, Count = g.Count() })
                .ToListAsync();
                
            var foldersMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var pc in pathCounts)
            {
                 if (pc.Path == null) continue; // Safety
                 var p = pc.Path.Replace('\\', '/').TrimEnd('/');
                 var pClean = p.TrimStart('/');
                 
                 if (string.IsNullOrEmpty(dbPath))
                 {
                     // Root of Share: Take first segment
                     if (!string.IsNullOrEmpty(pClean))
                     {
                         var parts = pClean.Split('/');
                         if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0])) 
                         {
                            var rootFolder = parts[0];
                            if (!foldersMap.ContainsKey(rootFolder)) foldersMap[rootFolder] = 0;
                            foldersMap[rootFolder] += pc.Count;
                         }
                     }
                 }
                 else
                 {
                     // Nested: Check if pClean is child of dbPath
                     if (pClean.StartsWith(dbPath + "/", StringComparison.OrdinalIgnoreCase))
                     {
                         var rel = pClean.Substring(dbPath.Length + 1);
                         var parts = rel.Split('/');
                         if (parts.Length > 0 && !string.IsNullOrEmpty(parts[0])) 
                         {
                            var childFolder = parts[0];
                            if (!foldersMap.ContainsKey(childFolder)) foldersMap[childFolder] = 0;
                            foldersMap[childFolder] += pc.Count;
                         }
                     }
                 }
            }
            
            // 2. Files: Direct children (Exact Match of DB Path)
            var targetWithSlash = "/" + dbPath;
            if (string.IsNullOrEmpty(dbPath)) targetWithSlash = "/"; // Edge case
            
            var files = await _context.MediaFiles
                .Where(m => m.ParentPath.Replace("\\", "/") == dbPath || m.ParentPath.Replace("\\", "/") == targetWithSlash)
                .OrderBy(m => m.Title)
                .Select(m => new { Type = "File", m.Id, Name = m.Title, Path = m.FilePath, m.Artist, m.Album })
                .ToListAsync();

            var folderList = foldersMap
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => new { 
                    Type = "Folder", 
                    Id = 0, 
                    Name = kvp.Key, 
                    Path = (cleanPath + "/" + kvp.Key), // Frontend Path includes Share
                    Artist = "", 
                    Album = "", 
                    Count = kvp.Value 
                });
            
            return Ok( folderList.Concat(files.Select(f => new { f.Type, f.Id, f.Name, f.Path, f.Artist, f.Album, Count = 0 })) );
        }
    }

    [HttpGet("stream/{id}")]
    public async Task<IActionResult> StreamFile(int id, [FromQuery] bool transcode = false, [FromQuery] double startTime = 0)
    {
        var media = await _context.MediaFiles
            .Include(m => m.ScanSource)
            .ThenInclude(s => s!.StorageCredential)
            .FirstOrDefaultAsync(m => m.Id == id);
            
        if (media == null || media.ScanSource == null) return NotFound();

        var stream = _smbService.OpenFile(media.ScanSource, media.FilePath);
        if (stream == null) return NotFound("File not found on SMB share");

        string contentType = "audio/mpeg"; 
        if (media.FilePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) contentType = "audio/flac";
        if (media.FilePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) contentType = "audio/wav";
        if (media.FilePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) contentType = "audio/ogg";
        if (media.FilePath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) contentType = "audio/mp4";

        if (transcode)
        {
            try 
            {
                 // Construct args
                 // -ss before -i for input seeking (read-discard for pipe)
                 string seekArg = startTime > 0 ? $"-ss {startTime} " : "";
                 
                 var process = new System.Diagnostics.Process
                 {
                     StartInfo = new System.Diagnostics.ProcessStartInfo
                     {
                         FileName = "ffmpeg",
                         Arguments = $"{seekArg}-i pipe:0 -f mp3 -ab 192k -", 
                         RedirectStandardInput = true,
                         RedirectStandardOutput = true,
                         UseShellExecute = false,
                         CreateNoWindow = true
                     }
                 };
                 
                 process.Start();
                 
                 // Copy SMB stream to FFMpeg Stdin in background
                 _ = Task.Run(async () => 
                 {
                     try 
                     {
                         await stream.CopyToAsync(process.StandardInput.BaseStream);
                         process.StandardInput.Close();
                     } 
                     catch(Exception ex) 
                     {
                         Console.WriteLine($"Transcode Input Error: {ex.Message}");
                     }
                     finally
                     {
                         stream.Dispose(); // SMB stream
                     }
                 });
                 
                 // Return FFMpeg Stdout as the file stream
                 // Note: Does not support Range processing (seeking) easily.
                 return File(process.StandardOutput.BaseStream, "audio/mpeg", enableRangeProcessing: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FFempg Error: {ex.Message}");
                return BadRequest("Transcoding failed. Is FFmpeg installed?");
            }
        }
        
        return File(stream, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Stream audio for shared playlists (PUBLIC - no authentication required).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("stream/shared/{shareToken}/{songId}")]
    public async Task<IActionResult> StreamShared(string shareToken, int songId, [FromQuery] bool transcode = false)
    {
        // Validate the share token and song belongs to that playlist
        var playlist = await _context.Playlists
            .Include(p => p.PlaylistSongs)
            .FirstOrDefaultAsync(p => p.ShareToken == shareToken);

        if (playlist == null)
            return NotFound("Share link invalid");

        var belongsToPlaylist = playlist.PlaylistSongs.Any(ps => ps.MediaFileId == songId);
        if (!belongsToPlaylist)
            return Forbid("Song not in shared playlist");

        // Get the media file
        var media = await _context.MediaFiles
            .Include(m => m.ScanSource)
            .ThenInclude(s => s!.StorageCredential)
            .FirstOrDefaultAsync(m => m.Id == songId);

        if (media == null || media.ScanSource == null)
            return NotFound("Song not found");

        var stream = _smbService.OpenFile(media.ScanSource, media.FilePath);
        if (stream == null)
            return NotFound("File not available");

        string contentType = "audio/mpeg";
        if (media.FilePath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) contentType = "audio/flac";
        if (media.FilePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) contentType = "audio/wav";
        if (media.FilePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) contentType = "audio/ogg";
        if (media.FilePath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) contentType = "audio/mp4";

        if (transcode)
        {
            try
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = "-i pipe:0 -f mp3 -ab 192k -",
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await stream.CopyToAsync(process.StandardInput.BaseStream);
                        process.StandardInput.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Shared Transcode Error: {ex.Message}");
                    }
                    finally
                    {
                        stream.Dispose();
                    }
                });

                return File(process.StandardOutput.BaseStream, "audio/mpeg", enableRangeProcessing: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Shared Stream FFmpeg Error: {ex.Message}");
                return BadRequest("Transcoding failed");
            }
        }

        return File(stream, contentType, enableRangeProcessing: true);
    }
    
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var totalSongs = await _context.MediaFiles.CountAsync();
        var totalArtists = await _context.MediaFiles.Select(m => m.Artist).Distinct().CountAsync();
        var totalAlbums = await _context.MediaFiles.Select(m => m.Album).Distinct().CountAsync();
        var totalSize = await _context.MediaFiles.SumAsync(m => m.SizeBytes);
        
        return Ok(new { totalSongs, totalArtists, totalAlbums, totalSize });
    }

    /// <summary>
    /// Update song metadata (title, artist, album, genre)
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateMedia(int id, [FromBody] UpdateMediaDto dto)
    {
        var media = await _context.MediaFiles.FindAsync(id);
        if (media == null) return NotFound();

        if (!string.IsNullOrEmpty(dto.Title))
            media.Title = dto.Title;
        if (!string.IsNullOrEmpty(dto.Artist))
            media.Artist = dto.Artist;
        if (!string.IsNullOrEmpty(dto.Album))
            media.Album = dto.Album;
        if (dto.Genre != null)
            media.Genre = dto.Genre;

        await _context.SaveChangesAsync();

        return Ok(new { 
            media.Id, 
            media.Title, 
            media.Artist, 
            media.Album, 
            media.Genre 
        });
    }

    public class UpdateMediaDto
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Genre { get; set; }
    }

    [HttpGet("directory/ids")]
    public async Task<ActionResult<List<int>>> GetDirectoryIds([FromQuery] string? path = null)
    {
         var query = _context.MediaFiles.AsQueryable();

         // Path Resolution Logic (Same as GetFiles/GetDirectory)
         if (!string.IsNullOrEmpty(path))
         {
             var targetPath = path.Replace('\\', '/').TrimEnd('/');
             var cleanTargetPath = targetPath.TrimStart('/'); 
             var sources = await _context.ScanSources.ToListAsync();
             var matchedSource = sources
                    .Select(s => new { s, NormPath = s.Path.Replace('\\', '/').Trim('/') })
                    .Where(x => cleanTargetPath.StartsWith(x.NormPath, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.NormPath.Length)
                    .FirstOrDefault();

             var dbPath = cleanTargetPath;
             if (matchedSource != null)
             {
                 var shareName = matchedSource.NormPath.Split('/')[0];
                 if (dbPath.StartsWith(shareName + "/", StringComparison.OrdinalIgnoreCase))
                 {
                     dbPath = dbPath.Substring(shareName.Length + 1);
                 }
                 else if (string.Equals(dbPath, shareName, StringComparison.OrdinalIgnoreCase))
                 {
                     dbPath = "";
                 }
             }

             if (string.IsNullOrEmpty(dbPath))
             {
                 // Filter by Source
                 if (matchedSource != null)
                 {
                     query = query.Where(m => m.ScanSourceId == matchedSource.s.Id);
                 }
             }
             else
             {
                 // Filter by ParentPath (Recursive)
                 // ParentPath == dbPath OR ParentPath starts with dbPath + "/"
                 query = query.Where(m => m.ParentPath == dbPath || m.ParentPath.Replace("\\", "/").StartsWith(dbPath + "/"));
             }
         }

         var ids = await query.Select(m => m.Id).ToListAsync();
         return Ok(ids);
    }

    [HttpPost("cover")]
    public async Task<IActionResult> UploadCover([FromForm] IFormFile file)
    {
        if (file == null || file.Length == 0) return BadRequest("No file uploaded");
        
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "covers");
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
        
        var ext = Path.GetExtension(file.FileName).ToLower();
        // Allow basic image types
        if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp") 
            return BadRequest("Invalid image type");

        var fileName = Guid.NewGuid().ToString() + ext;
        var filePath = Path.Combine(folder, fileName);
        
        using (var stream = new FileStream(filePath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        
        // Return relative URL
        return Ok(new { url = $"/api/media/cover/{fileName}" });
    }

    [HttpGet("cover/{fileName}")]
    public IActionResult GetCover(string fileName)
    {
        var folder = Path.Combine(Directory.GetCurrentDirectory(), "data", "covers");
        var filePath = Path.Combine(folder, fileName);
        
        if (!System.IO.File.Exists(filePath)) return NotFound();
        
        var ext = Path.GetExtension(fileName).ToLower();
        var mime = ext == ".png" ? "image/png" : 
                   ext == ".webp" ? "image/webp" : "image/jpeg";
                   
        return PhysicalFile(filePath, mime);
    }

    /// <summary>
    /// Proxy endpoint to fetch images from SMB paths.
    /// Usage: GET /api/media/smb-image?path=smb://server/share/path/image.jpg
    /// </summary>
    [HttpGet("smb-image")]
    public async Task<IActionResult> GetSmbImage([FromQuery] string path)
    {
        if (string.IsNullOrEmpty(path)) return BadRequest("Path required");

        // Parse SMB path to find matching ScanSource
        // SMB path format: smb://host/share/path/to/file.ext
        var normalizedPath = path.Replace('\\', '/');
        if (!normalizedPath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            return BadRequest("Invalid SMB path");

        // Extract host and path parts
        var pathWithoutScheme = normalizedPath.Substring(6); // Remove "smb://"
        var slashIdx = pathWithoutScheme.IndexOf('/');
        if (slashIdx < 0) return BadRequest("Invalid SMB path format");

        var host = pathWithoutScheme.Substring(0, slashIdx);
        var fullSharePath = pathWithoutScheme.Substring(slashIdx + 1); // e.g., "PT/opencd/folder/cover.jpg"

        // Split into share name and relative path
        var pathParts = fullSharePath.Split('/', 2);
        if (pathParts.Length < 2) return BadRequest($"Invalid path - need share and file path. Got: {fullSharePath}");
        
        var shareName = pathParts[0];
        var relativePath = pathParts[1];

        Console.WriteLine($"[SMB-Image] Host: {host}, Share: {shareName}, RelPath: {relativePath}");

        // Find a StorageCredential by host
        var credentials = await _context.StorageCredentials.ToListAsync();

        var matchedCredential = credentials.FirstOrDefault(c =>
        {
            var credHost = c.Host.Replace('\\', '/');
            if (credHost.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
                credHost = credHost.Substring(6);
            credHost = credHost.Trim('/');
            
            // Handle IP or hostname match
            return string.Equals(credHost, host, StringComparison.OrdinalIgnoreCase);
        });

        if (matchedCredential == null)
            return NotFound($"No matching storage credential found for host: {host}");

        // Attempt to open the file via SMB service using SmbService helper
        try
        {
            // Create a temporary ScanSource-like structure for OpenFile
            // We need to use the Connect method with credential and share
            var smbService = (WebMusic.Backend.Services.SmbService)_smbService;
            
            if (!smbService.Connect(matchedCredential, shareName, out var client, out var fileStore))
            {
                return NotFound($"Failed to connect to SMB share: {shareName}");
            }

            try
            {
                // Convert path to backslash for SMB and open file
                var smbFilePath = relativePath.Replace('/', '\\');
                var stream = new WebMusic.Backend.Services.SmbFileStream(client, fileStore, smbFilePath);

                // Determine content type
                var ext = Path.GetExtension(path).ToLower();
                var mime = ext switch
                {
                    ".png" => "image/png",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    _ => "image/jpeg"
                };

                return File(stream, mime);
            }
            catch (Exception ex)
            {
                client.Disconnect();
                Console.Error.WriteLine($"SMB File Open Error: {ex.Message}");
                return NotFound($"Image not found on SMB share: {relativePath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMB Image Error: {ex.Message}");
            return NotFound("Failed to load image from SMB");
        }
    }
}
