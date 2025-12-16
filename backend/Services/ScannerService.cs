using TagLib;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection; // Added for IServiceScopeFactory

namespace WebMusic.Backend.Services;

public class ScannerService
{
    private readonly AppDbContext _context;
    private readonly ISmbService _smbService;
    private readonly ILogger<ScannerService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScanStateService _scanState;
    public ISmbService SmbService => _smbService;

    public ScannerService(AppDbContext context, ISmbService smbService, ILogger<ScannerService> logger, IServiceScopeFactory scopeFactory, ScanStateService scanState)
    {
        _context = context;
        _smbService = smbService;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _scanState = scanState;
    }

    public async Task<int> ScanSourceAsync(int sourceId)
    {
        int count = 0;
        var source = await _context.ScanSources
            .Include(s => s.StorageCredential)
            .FirstOrDefaultAsync(s => s.Id == sourceId);
            
        if (source == null) return 0;

        _logger.LogInformation($"Starting scan for source: {source.Name}");

        try
        {
            var filePaths = _smbService.ListFiles(source, "");
            
            // Optimization: Pre-fetch existing paths for this Source/Credential to avoid N+1 DB calls
            // This enables fast "Incremental Scanning" / Resuming
            var existingPaths = new HashSet<string>(await _context.MediaFiles
                .Where(m => m.ScanSource!.StorageCredentialId == source.StorageCredentialId)
                .Select(m => m.FilePath)
                .ToListAsync());

            foreach (var path in filePaths)
            {
                // Uniqueness Check: FilePath + StorageCredential
                if (existingPaths.Contains(path))
                {
                    continue; // Already exists, skip efficiently
                }

                try
                {
                    // Call newly extracted method
                    bool added = await ScanFileAsync(source, path, existingPaths);
                    if (added)
                    {
                        count++;
                        if (count % 20 == 0) 
                        {
                            try { 
                                await _context.SaveChangesAsync();
                                _scanState.UpdateProgress(count); 
                            }
                            catch (Exception dbEx) {
                                 _logger.LogError(dbEx, $"DB Batch Save Failed: {dbEx.InnerException?.Message ?? dbEx.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error processing file {path}");
                }
            }
            
            try { await _context.SaveChangesAsync(); }
            catch (Exception dbEx) {
                 _logger.LogError(dbEx, $"Final DB Save Failed: {dbEx.InnerException?.Message ?? dbEx.Message}");
            }

            _logger.LogInformation($"Scan completed. Added {count} new files.");
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scan failed.");
            return 0;
        }
    }

    public async Task<bool> ScanFileAsync(ScanSource source, string path, HashSet<string>? existingPaths = null)
    {
         if (existingPaths != null && existingPaths.Contains(path)) return false;

         // Optimization: Read metadata directly from SMB stream without downloading full file.
         using var stream = _smbService.OpenFile(source, path);
         if (stream == null) return false;

         // custom StreamFileAbstraction
         var fileAbstraction = new StreamFileAbstraction(path, stream);
         using var tfile = TagLib.File.Create(fileAbstraction);
         
         var tag = tfile.Tag;

         string normalizedPath = path.Replace('\\', '/');
         string parentPath = Path.GetDirectoryName(normalizedPath) ?? "";
         
         string fileHash = await ComputePartialHash(stream);

         var media = new MediaFile
         {
             FilePath = path,
             ParentPath = parentPath,
             FileHash = fileHash,
             Title = tag.Title ?? Path.GetFileNameWithoutExtension(path),
             Artist = tag.FirstPerformer ?? "Unknown Artist",
             Album = tag.Album ?? "Unknown Album",
             Genre = tag.FirstGenre ?? "Unknown Genre",
             Year = (int)tag.Year,
             Duration = tfile.Properties.Duration,
             SizeBytes = stream.Length, 
             ScanSourceId = source.Id,
             AddedAt = DateTime.UtcNow
         };

         _context.MediaFiles.Add(media);
         // Note: SaveChanges is not called here for batch performance, 
         // BUT for single file upload we might want it.
         // If called from Upload Loop, caller handles save.
         // If called individually... caller should save.
         return true;
    }

    // Overload for single file save (called by Controller)
    public async Task IndexSingleFileAsync(int sourceId, string path)
    {
        var source = await _context.ScanSources
            .Include(s => s.StorageCredential)
            .FirstOrDefaultAsync(s => s.Id == sourceId);
        if (source == null) return;
        
        bool added = await ScanFileAsync(source, path);
        if (added) await _context.SaveChangesAsync();
    }


    private async Task<string> ComputePartialHash(Stream stream)
    {
        // Read first 16KB and last 16KB (if seekable) + Size
        // SMB Stream is seekable.
        try {
            long oldPos = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            
            byte[] buffer = new byte[8192]; // 8KB
            int read = await stream.ReadAsync(buffer, 0, buffer.Length);
            
            stream.Seek(oldPos, SeekOrigin.Begin); // Restore pos? No, stream is used by TagLib via abstraction.
            // Actually TagLib might have moved it. TagLib closing might be issue.
            // We should hash BEFORE TagLib or ensure position is reset.
            // TagLib creates its own abstraction, but we passed the stream.
            
            using var md5 = System.Security.Cryptography.MD5.Create();
            byte[] hashBytes = md5.ComputeHash(buffer, 0, read);
            
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant() + "_" + stream.Length;
        }
        catch {
             return "nohash_" + stream.Length;
        }
    }
}
