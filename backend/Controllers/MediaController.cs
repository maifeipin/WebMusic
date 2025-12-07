using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ISmbService _smbService;

    public MediaController(AppDbContext context, ISmbService smbService)
    {
        _context = context;
        _smbService = smbService;
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

        // Path Filtering (Directory Playback)
        if (!string.IsNullOrEmpty(path))
        {
             var targetPath = path.Replace('\\', '/').TrimEnd('/');
             var cleanTargetPath = targetPath.TrimStart('/'); 

             // Resolve "Share Name" mapping (Same as GetDirectory)
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
             
             // Apply Path Filter
             if (recursive)
             {
                 if (string.IsNullOrEmpty(dbPath))
                 {
                     // Root of share: Return everything in this share?
                     // Actually logic should be: ParentPath starts with dbPath...
                     // But dbPath is empty. ParentPath can be anything.
                     // But we should limit to the MATCHED SOURCE if possible?
                     // Or just everything?
                     // If path provided is just "ShareName", then Source Match found it.
                     // We probably want all files belonging to that Share (effectively query by SourceId?)
                     // OR just any file.
                     // But wait, if multiple sources share same folder names (unlikely but possible).
                     // Best to filter by SourceId if we matched a source?
                     // For now, naive ParentPath prefix match.
                     // If dbPath is "", `StartsWith` "" is true.
                     // But we really only want files "under" that path.
                     // If dbPath is empty, it means we are at Share Root.
                     // We should filter files that belong to this Share.
                     // `matchedSource` gives us `ScanSourceId`.
                     if (matchedSource != null)
                     {
                         query = query.Where(m => m.ScanSourceId == matchedSource.s.Id);
                     }
                 }
                 else
                 {
                     // Recursive: ParentPath == dbPath OR ParentPath starts with dbPath + "/"
                     // Note: SQLite contains/startswith logic
                     query = query.Where(m => m.ParentPath == dbPath || m.ParentPath.Replace("\\", "/").StartsWith(dbPath + "/"));
                 }
             }
             else
             {
                 // Non-Recursive: ParentPath == dbPath
                 // Handle exact match.
                 // dbPath might be empty.
                 var queryDbPath = dbPath.Replace('/', '\\'); // fallback? no, standardize on / for check if possible.
                 // We rely on standardizing query side.
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
        // Normalize Request Path
        // If path is empty, we return Roots (Sources)
        // If path is present, we return children
        
        var targetPath = (path ?? "").Replace('\\', '/').TrimEnd('/');
        // Ensure no leading slash for consistent processing in C#, but handle DB mismatch later
        var cleanTargetPath = targetPath.TrimStart('/'); 

        if (string.IsNullOrEmpty(targetPath))
        {
            // Root Level: Return Configured Sources as "Roots"
            var sources = await _context.ScanSources
                .Select(s => new { s.Id, s.Name, s.Path })
                .ToListAsync();
                
            var rootList = new List<object>();
            
            foreach (var s in sources)
            {
                var count = await _context.MediaFiles.CountAsync(m => m.ScanSourceId == s.Id);
                var normPath = s.Path.Replace('\\', '/').TrimEnd('/');
                // Use Source Name if available, otherwise Path
                var displayName = string.IsNullOrWhiteSpace(s.Name) ? normPath : s.Name;
                
                rootList.Add(new { 
                    Type = "Folder", 
                    Id = 0, 
                    Name = displayName, 
                    Path = normPath, // Actual path for next query
                    Artist = "", 
                    Album = "", 
                    Count = count 
                });
            }
            
            return Ok(rootList);
        }
        else
        {
            // Sub-Folder Level: Find children
            
            // Resolve "Share Name" mapping
            // ScanSource Path includes Share Name (e.g. "DataSync/Music")
            // MediaFile ParentPath excludes Share Name (e.g. "Music")
            
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
            
            // 1. Folders: In-Memory aggregation
            // Filter coarsely by dbPath to reduce memory load
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
                    Path = (cleanTargetPath + "/" + kvp.Key), // Frontend Path includes Share
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
    
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var totalSongs = await _context.MediaFiles.CountAsync();
        var totalArtists = await _context.MediaFiles.Select(m => m.Artist).Distinct().CountAsync();
        var totalAlbums = await _context.MediaFiles.Select(m => m.Album).Distinct().CountAsync();
        var totalSize = await _context.MediaFiles.SumAsync(m => m.SizeBytes);
        
        return Ok(new { totalSongs, totalArtists, totalAlbums, totalSize });
    }
}
