using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Services;
using WebMusic.Backend.Models;
using System.Security.Claims;
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
    private readonly DataManagementService _dataService;

    public MediaController(AppDbContext context, ISmbService smbService, PathResolver pathResolver, DataManagementService dataService)
    {
        _context = context;
        _smbService = smbService;
        _pathResolver = pathResolver;
        _dataService = dataService;
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

    // ... (GetFiles and others remain unchanged, skipping to DeleteMedia)

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteMedia(int id, [FromQuery] bool force = false)
    {
        var (success, details) = await _dataService.DeleteMediaAsync(id, force);

        if (!success && details != null)
        {
            return StatusCode(409, new { 
                message = "This song is currently in use.", 
                details = details 
            });
        }
        else if (!success)
        {
            return NotFound();
        }

        return Ok(new { id, deleted = true });
    }

    [HttpGet]
    public async Task<ActionResult<PagedResult<MediaFile>>> GetFiles(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 50, 
        [FromQuery] string? search = null,
        [FromQuery] string? filterBy = null,
        [FromQuery] string? filterValue = null,
        [FromQuery] string? path = null,
        [FromQuery] bool recursive = false,
        [FromQuery] List<string>? criteria = null)
    {
        var userId = GetUserId();

        // Robust Filtering: Get Allowed Source IDs first
        var allowedSourceIds = await _context.ScanSources
            .Where(s => s.UserId == null || s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync();

        var query = _context.MediaFiles
            .Where(m => allowedSourceIds.Contains(m.ScanSourceId))
            .AsQueryable();

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

        // Advanced Criteria Filtering
        // Format: "Field:Operator:Value"
        if (criteria != null && criteria.Count > 0)
        {
            foreach (var c in criteria)
            {
                var parts = c.Split(':', 3);
                if (parts.Length < 2) continue;
                
                var field = parts[0].ToLower();
                var op = parts[1].ToLower();
                var val = parts.Length > 2 ? parts[2].ToLower() : "";

                switch (field)
                {
                    case "artist":
                        if (op == "isempty") query = query.Where(m => m.Artist == null || m.Artist == "" || m.Artist == "Unknown" || m.Artist == "Unknown Artist");
                        else if (op == "contains") query = query.Where(m => m.Artist != null && m.Artist.ToLower().Contains(val));
                        else if (op == "is") query = query.Where(m => m.Artist.ToLower() == val);
                        break;
                    case "album":
                        if (op == "isempty") query = query.Where(m => m.Album == null || m.Album == "" || m.Album == "Unknown" || m.Album == "Unknown Album");
                        else if (op == "contains") query = query.Where(m => m.Album != null && m.Album.ToLower().Contains(val));
                        break;
                    case "genre":
                        if (op == "isempty") query = query.Where(m => m.Genre == null || m.Genre == "" || m.Genre == "Unknown");
                        else if (op == "contains") query = query.Where(m => m.Genre != null && m.Genre.ToLower().Contains(val));
                        break;
                    case "title":
                        if (op == "contains") query = query.Where(m => m.Title != null && m.Title.ToLower().Contains(val));
                        else if (op == "startswith") query = query.Where(m => m.Title != null && m.Title.ToLower().StartsWith(val));
                        break;
                    case "filename":
                         // FilePath match
                         if (op == "contains") query = query.Where(m => m.FilePath.ToLower().Contains(val));
                         break;
                    case "encoding":
                        if (op == "is" && val == "mojibake")
                        {
                            // Heuristic: Search for common Latin-1 Supplement characters (0xA1-0xFF) 
                            // that appear when GBK/Big5 is misinterpreted as Latin-1.
                            // We check for a subset of the most frequent "lead byte" mappings.
                            var badChars = new[] { "À", "Á", "Â", "Ã", "Ä", "Å", "Æ", "Ç", "È", "É", "Ê", "Ë", "Ì", "Í", "Î", "Ï", "Ð", "Ñ", "Ò", "Ó", "Ô", "Õ", "Ö", "×", "Ø", "Ù", "Ú", "Û", "Ü", "Ý", "Þ", "ß", "à", "á", "â", "ã", "ä", "å", "æ", "ç", "è", "é", "ê", "ë", "ì", "í", "î", "ï", "ð", "ñ", "ò", "ó", "ô", "õ", "ö", "÷", "ø", "ù", "ú", "û", "ü", "ý", "þ", "ÿ", "°", "±", "²", "³", "´", "µ", "¶", "·", "¸", "¹", "º", "»", "¼", "½", "¾", "¿" };
                            
                            // Optimization: Check for just the most reliable indicators to avoid massive SQL
                            // "º" (0xBA), "»" (0xBB), "¼" (0xBC), "½" (0xBD), "¾" (0xBE), "¿" (0xBF) are very common in GBK 
                            // "À" (0xC0) - "Ï" (0xCF) are also very common.
                            query = query.Where(m => 
                                m.Title.Contains("º") || m.Title.Contains("»") || m.Title.Contains("¼") || 
                                m.Title.Contains("½") || m.Title.Contains("¾") || m.Title.Contains("¿") ||
                                m.Title.Contains("À") || m.Title.Contains("Á") || m.Title.Contains("Â") || 
                                m.Title.Contains("Ã") || m.Title.Contains("Ä") || m.Title.Contains("Å") || 
                                m.Title.Contains("Æ") || m.Title.Contains("Ç") || m.Title.Contains("È") ||
                                m.Artist.Contains("º") || m.Artist.Contains("»") || m.Artist.Contains("¾") ||
                                m.Artist.Contains("À") || m.Artist.Contains("Á") || m.Artist.Contains("Ã")
                            );
                        }
                        break;
                }
            }
        }

        // Legacy Filter Support (keep backward compatibility if needed)
        if (!string.IsNullOrEmpty(filterBy) && !string.IsNullOrEmpty(filterValue))
        {
            var val = filterValue.ToLower();
            switch (filterBy.ToLower())
            {
                case "artist": query = query.Where(m => m.Artist.ToLower() == val); break;
                case "album": query = query.Where(m => m.Album.ToLower() == val); break;
                case "genre": query = query.Where(m => m.Genre.ToLower() == val); break;
            }
        }

        var total = await query.CountAsync();
        var files = await query
            .OrderBy(m => m.Title) // Consistent numbering
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new { 
                m.Id, m.Title, m.Artist, m.Album, m.Genre, m.Duration.TotalSeconds, m.Year, m.FilePath, m.CoverArt 
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, files });
    }

    [HttpPost("list/ids")]
    public async Task<ActionResult<List<object>>> GetSongsByIds([FromBody] List<int> ids)
    {
        if (ids == null || ids.Count == 0) return Ok(new List<object>());

        var userId = GetUserId();
        var allowedSourceIds = await _context.ScanSources
            .Where(s => s.UserId == null || s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync();

        var songs = await _context.MediaFiles
            .Where(m => ids.Contains(m.Id))
            .Where(m => allowedSourceIds.Contains(m.ScanSourceId))
            .Select(m => new { 
                m.Id, m.Title, m.Artist, m.Album, m.Genre, duration = m.Duration.TotalSeconds, m.Year, m.FilePath, m.CoverArt 
            })
            .ToListAsync();

        // Reorder to match input IDs
        var orderedSongs = ids
            .Select(id => songs.FirstOrDefault(s => s.Id == id))
            .Where(s => s != null)
            .ToList();

        return Ok(orderedSongs);
    }

    [HttpGet("groups")]
    public async Task<IActionResult> GetGroups([FromQuery] string groupBy)
    {
        // Returns list of unique values for a column + count
        var userId = GetUserId();
        var allowedSourceIds = await _context.ScanSources
            .Where(s => s.UserId == null || s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync();

        var query = _context.MediaFiles
            .Where(m => allowedSourceIds.Contains(m.ScanSourceId))
            .AsQueryable();
        
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
        var userId = GetUserId();
        var cleanPath = _pathResolver.NormalizePath(path);

        if (string.IsNullOrEmpty(cleanPath))
        {
            // Root Level: Return Configured Sources as "Roots"
            // Filter Sources by User
            var sources = await _context.ScanSources
                .Where(s => s.UserId == null || s.UserId == userId)
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
            // Filter sources first for resolver
            var sources = await _context.ScanSources
                .Where(s => s.UserId == null || s.UserId == userId)
                .ToListAsync();
            var resolved = _pathResolver.ResolveFrontendToDbPath(path, sources);
            var dbPath = resolved.DbPath;
            
            var allowedSourceIds = await _context.ScanSources
                .Where(s => s.UserId == null || s.UserId == userId)
                .Select(s => s.Id)
                .ToListAsync();

            var query = _context.MediaFiles
                .Where(m => allowedSourceIds.Contains(m.ScanSourceId))
                .AsQueryable();
            
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
                .Include(m => m.ScanSource)
                .Where(m => m.ScanSource != null && (m.ScanSource.UserId == null || m.ScanSource.UserId == userId))
                .Where(m => m.ParentPath.Replace("\\", "/") == dbPath || m.ParentPath.Replace("\\", "/") == targetWithSlash)
                .OrderBy(m => m.Title)
                .Select(m => new { Type = "File", m.Id, Name = m.Title, Path = m.FilePath, m.Artist, m.Album, Duration = m.Duration.TotalSeconds, m.CoverArt })
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
                    Duration = 0.0,
                    Count = kvp.Value,
                    CoverArt = (string?)null
                });
            
            return Ok( folderList.Concat(files.Select(f => new { f.Type, f.Id, f.Name, f.Path, f.Artist, f.Album, f.Duration, Count = 0, f.CoverArt })) );
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
            return StartTranscode(stream, startTime, media.Id);
        }
        
        return File(stream, contentType, enableRangeProcessing: true);
    }

    /// <summary>
    /// Stream audio for shared playlists (PUBLIC - no authentication required).
    /// </summary>
    [AllowAnonymous]
    [HttpGet("stream/shared/{shareToken}/{songId}")]
    public async Task<IActionResult> StreamShared(string shareToken, int songId, [FromQuery] bool transcode = false, [FromQuery] string? pwd = null)
    {
        // 1. Validate Share Token
        var playlist = await _context.Playlists
            .Include(p => p.PlaylistSongs)
            .FirstOrDefaultAsync(p => p.ShareToken == shareToken);

        if (playlist == null) return NotFound("Share link invalid");

        // 2. Check Expiry
        if (playlist.ShareExpiresAt.HasValue && DateTime.UtcNow > playlist.ShareExpiresAt.Value)
            return StatusCode(410, "Share link expired");

        // 3. Check Password
        if (!string.IsNullOrEmpty(playlist.SharePassword))
        {
            if (string.IsNullOrEmpty(pwd) || pwd != playlist.SharePassword)
                return Forbid("Invalid password");
        }

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
            return StartTranscode(stream, 0, media.Id);
        }

        return File(stream, contentType, enableRangeProcessing: true);
    }

    private IActionResult StartTranscode(Stream inputStream, double startTime = 0, int? mediaId = null)
    {
        try 
        {
             // Construct args
             string seekArg = startTime > 0 ? $"-ss {startTime} " : "";
             
             var process = new System.Diagnostics.Process
             {
                 StartInfo = new System.Diagnostics.ProcessStartInfo
                 {
                     FileName = "ffmpeg",
                     Arguments = $"{seekArg}-i pipe:0 -f mp3 -ab 192k -", 
                     RedirectStandardInput = true,
                     RedirectStandardOutput = true,
                     RedirectStandardError = true, // Capture Logs
                     UseShellExecute = false,
                     CreateNoWindow = true
                 }
             };
             
             process.Start();
             
             // Background Task 1: Pump data from SMB Stream -> FFmpeg Stdin
             _ = Task.Run(async () => 
             {
                 using (inputStream) // Ensure SMB resources are freed
                 {
                     try 
                     {
                         await inputStream.CopyToAsync(process.StandardInput.BaseStream);
                         process.StandardInput.Close(); // Signal EOF to FFmpeg
                     } 
                     catch(Exception ex) 
                     {
                         // If process is disposed/dead, input stream write will fail. This is expected if ffmpeg exits early.
                         if (ex is ObjectDisposedException || ex.Message.Contains("disposed")) 
                         {
                             return;
                         }

                         Console.WriteLine($"Transcode Input Error: {ex.Message}");
                         try { process.Kill(); } catch {} 
                     }
                 }
             });

             // Background Task 2: Parse Duration from Stderr (Metadata & Progress)
             if (mediaId.HasValue)
             {
                 _ = Task.Run(async () => 
                 {
                     try
                     {
                        // Use a separate scope for DB operations in background thread
                        using var scope = HttpContext.RequestServices.CreateScope();
                        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        
                        // Check if we actually need to update duration
                        var media = await dbContext.MediaFiles.FindAsync(mediaId.Value);
                        if (media != null && media.Duration.TotalSeconds < 1)
                        {
                            var buffer = new char[4096];
                            int read;
                            string pending = "";
                            TimeSpan? capturedDuration = null;
                            TimeSpan? lastProgressTime = null;

                            // Read stderr
                            while ((read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                var chunk = new string(buffer, 0, read);
                                pending += chunk;
                                
                                // TEMP DEBUG REMOVED
                                // Console.WriteLine($"[FFmpeg-Debug] Chunk: {chunk}");

                                // 1. Try Find "Duration: HH:MM:SS" (Header)
                                if (capturedDuration == null)
                                {
                                    var matchDur = System.Text.RegularExpressions.Regex.Match(pending, @"Duration:\s(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
                                    if (matchDur.Success)
                                    {
                                        var h = int.Parse(matchDur.Groups[1].Value);
                                        var m = int.Parse(matchDur.Groups[2].Value);
                                        var s = int.Parse(matchDur.Groups[3].Value);
                                        var ms = int.Parse(matchDur.Groups[4].Value);
                                        capturedDuration = new TimeSpan(0, h, m, s, ms * 10);
                                        // Console.WriteLine($"[FFmpeg] Found Header Duration: {capturedDuration}");
                                    }
                                }

                                // 2. Try Track "time=HH:MM:SS.ss" (Progress)
                                // Regex matches last occurrence in chunk
                                var matchesTime = System.Text.RegularExpressions.Regex.Matches(chunk, @"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})");
                                if (matchesTime.Count > 0)
                                {
                                    var lastMatch = matchesTime[matchesTime.Count - 1];
                                    var h = int.Parse(lastMatch.Groups[1].Value);
                                    var m = int.Parse(lastMatch.Groups[2].Value);
                                    var s = int.Parse(lastMatch.Groups[3].Value);
                                    var ms = int.Parse(lastMatch.Groups[4].Value);
                                    lastProgressTime = new TimeSpan(0, h, m, s, ms * 10);
                                }

                                // Avoid infinite string growth
                                if (pending.Length > 2000) pending = pending.Substring(pending.Length - 1000);
                            }

                            // Process finished reading stderr (means FFmpeg is closing/closed)
                            await process.WaitForExitAsync();
                            
                            // Check exit code safely
                            int exitCode = -1;
                            try { exitCode = process.ExitCode; } catch {}

                            // Only update if success (ExitCode 0) to ensure we have the FULL duration
                            if (exitCode == 0)
                            {
                                var finalDuration = capturedDuration ?? lastProgressTime;
                                
                                if (finalDuration.HasValue && finalDuration.Value.TotalSeconds > 0)
                                {
                                     Console.WriteLine($"[FFmpeg] Process Complete. Updating Duration for ID {mediaId} to: {finalDuration.Value}");
                                     media.Duration = finalDuration.Value;
                                     await dbContext.SaveChangesAsync();
                                }
                                else
                                {
                                     Console.WriteLine($"[FFmpeg] Finished but no valid duration found.");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[FFmpeg] Process exited with code {exitCode}. Duration not updated.");
                            }
                        }
                     }
                     catch (Exception ex)
                     {
                         Console.WriteLine($"[FFmpeg] Metadata Parse Error: {ex.Message}");
                     }
                     finally 
                     {
                         // Ensure we clean up the process object finally
                         try { process.Dispose(); } catch {}
                     }
                 });
             }
             else 
             {
                  // If we are not parsing metadata, we still need to dispose process eventually.
                  // But ProcessCleanupStream kills it. We can leave it to GC or handle better?
                  // For now, let's just let it be killed.
             }
             
             // Wrap the stdout in a custom stream that Kills the process when Disposed/Closed
             var wrapperStream = new ProcessCleanupStream(process.StandardOutput.BaseStream, process);
             
             return File(wrapperStream, "audio/mpeg", enableRangeProcessing: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FFmpeg Start Error: {ex.Message}");
            inputStream.Dispose();
            return BadRequest("Transcoding failed. Is FFmpeg installed?");
        }
    }

    // Helper Class to ensure Process is killed when the Response Stream is closed
    public class ProcessCleanupStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly System.Diagnostics.Process _process;
        private bool _disposed;

        public ProcessCleanupStream(Stream baseStream, System.Diagnostics.Process process)
        {
            _baseStream = baseStream;
            _process = process;
        }

        public override int Read(byte[] buffer, int offset, int count) => _baseStream.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);
        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => _baseStream.CanWrite;
        public override long Length => _baseStream.Length;
        public override long Position { get => _baseStream.Position; set => _baseStream.Position = value; }
        public override void Flush() => _baseStream.Flush();

        public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
        public override void SetLength(long value) => _baseStream.SetLength(value);

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _baseStream.Dispose();
                try 
                {
                    if (!_process.HasExited) 
                    {
                        // Console.WriteLine("Killing FFmpeg process (Stream Disposed)");
                        _process.Kill(); 
                    }
                    // REMOVED: _process.Dispose(); -> Moved to Parsing Task or GC
                } 
                catch {}
            }
            base.Dispose(disposing);
        }
    }
    
    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var userId = GetUserId();
        var allowedSourceIds = await _context.ScanSources
            .Where(s => s.UserId == null || s.UserId == userId)
            .Select(s => s.Id)
            .ToListAsync();

        var query = _context.MediaFiles
            .Where(m => allowedSourceIds.Contains(m.ScanSourceId));

        var totalSongs = await query.CountAsync();
        var totalArtists = await query.Select(m => m.Artist).Distinct().CountAsync();
        var totalAlbums = await query.Select(m => m.Album).Distinct().CountAsync();
        var totalSize = await query.SumAsync(m => m.SizeBytes);
        
        return Ok(new { totalSongs, totalArtists, totalAlbums, totalSize });
    }

    /// <summary>
    /// Update song metadata (title, artist, album, genre)
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateMedia(int id, [FromBody] UpdateMediaDto dto)
    {
        var userId = GetUserId();
        var media = await _context.MediaFiles
            .Include(m => m.ScanSource)
            .FirstOrDefaultAsync(m => m.Id == id); // Need to include Source to check permission
            
        if (media == null) return NotFound();
        
        // Security Check
        if (media.ScanSource != null)
        {
             if (media.ScanSource.UserId != null && media.ScanSource.UserId != userId) return Forbid();
        }

        if (!string.IsNullOrEmpty(dto.Title))
            media.Title = dto.Title;
        if (!string.IsNullOrEmpty(dto.Artist))
            media.Artist = dto.Artist;
        if (!string.IsNullOrEmpty(dto.Album))
            media.Album = dto.Album;
        if (dto.Genre != null)
            media.Genre = dto.Genre;
        if (dto.CoverArt != null) // Allow empty string to clear? Assuming null means "no change"
            media.CoverArt = dto.CoverArt;

        await _context.SaveChangesAsync();

        return Ok(new { 
            media.Id, 
            media.Title, 
            media.Artist, 
            media.Album, 
            media.Genre,
            media.CoverArt 
        });
    }

    public class UpdateMediaDto
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Genre { get; set; }
        public string? CoverArt { get; set; }
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

    // [Deleted duplicate DeleteMedia method]

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
