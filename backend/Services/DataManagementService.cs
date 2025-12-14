using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;

namespace WebMusic.Backend.Services;

public class DataManagementService
{
    private readonly AppDbContext _context;
    private readonly ILogger<DataManagementService> _logger;

    public DataManagementService(AppDbContext context, ILogger<DataManagementService> logger)
    {
        _context = context;
        _logger = logger;
    }

    // --- Deletion Logic ---

    public async Task<(bool success, object? dependencyDetails)> DeleteMediaAsync(int id, bool force)
    {
        var media = await _context.MediaFiles.FindAsync(id);
        if (media == null) return (false, null);

        // Check dependencies
        var inPlaylists = await _context.PlaylistSongs.CountAsync(p => p.MediaFileId == id);
        var inHistory = await _context.PlayHistories.CountAsync(h => h.MediaFileId == id);
        var inFavorites = await _context.Favorites.CountAsync(f => f.MediaFileId == id);
        
        var usageCount = inPlaylists + inHistory + inFavorites;

        if (usageCount > 0 && !force)
        {
            return (false, new { 
                Playlists = inPlaylists, 
                History = inHistory, 
                Favorites = inFavorites 
            });
        }

        // Proceed to delete
        _context.MediaFiles.Remove(media);
        await _context.SaveChangesAsync();

        return (true, null);
    }
    
    public async Task<int> BatchDeleteMediaAsync(List<int> ids, bool force)
    {
        // Simple loop for now, optimize later if needed
        int deletedCount = 0;
        foreach (var id in ids)
        {
            var (success, _) = await DeleteMediaAsync(id, force);
            if (success) deletedCount++;
        }
        return deletedCount;
    }

    // --- Export Logic ---

    public async Task<string> ExportMetadataAsync()
    {
        var data = await _context.MediaFiles
            .AsNoTracking()
            .Select(m => new MediaFileExportDto
            {
                FilePath = m.FilePath,
                Title = m.Title,
                Artist = m.Artist,
                Album = m.Album,
                Genre = m.Genre,
                Year = m.Year,
                DurationSeconds = m.Duration.TotalSeconds,
                ScanSourceId = m.ScanSourceId,
                AddedAt = m.AddedAt
            })
            .ToListAsync();

        return JsonConvert.SerializeObject(data, Formatting.Indented);
    }

    // --- Import Logic ---

    public async Task<ImportResult> ImportMetadataAsync(string json, ImportMode mode)
    {
        var result = new ImportResult();
        List<MediaFileExportDto> importedItems;

        try
        {
            importedItems = JsonConvert.DeserializeObject<List<MediaFileExportDto>>(json) ?? new();
        }
        catch (Exception)
        {
            result.Success = false;
            result.Message = "Invalid JSON format";
            return result;
        }

        if (mode == ImportMode.ClearAndOverwrite)
        {
            // Dangerous: Clear Table
            _context.MediaFiles.RemoveRange(_context.MediaFiles);
            await _context.SaveChangesAsync();
        }

        foreach (var item in importedItems)
        {
            var existing = await _context.MediaFiles.FirstOrDefaultAsync(m => m.FilePath == item.FilePath);
            
            if (existing != null)
            {
                if (mode == ImportMode.AppendOrSkip)
                {
                    result.Skipped++;
                    continue; // Skip
                }
                else if (mode == ImportMode.UpdateExisting)
                {
                    // Update metadata
                    existing.Title = item.Title;
                    existing.Artist = item.Artist;
                    existing.Album = item.Album;
                    existing.Genre = item.Genre;
                    existing.Year = item.Year;
                    // existing.AddedAt = item.AddedAt; // Optional: restore original add time?
                    result.Updated++;
                }
            }
            else
            {
                // Create new
                var newMedia = new MediaFile
                {
                    FilePath = item.FilePath,
                    Title = item.Title ?? Path.GetFileNameWithoutExtension(item.FilePath),
                    Artist = item.Artist ?? "Unknown",
                    Album = item.Album ?? "Unknown",
                    Genre = item.Genre ?? "",
                    Year = item.Year,
                    Duration = TimeSpan.FromSeconds(item.DurationSeconds),
                    ScanSourceId = item.ScanSourceId,
                    ParentPath = Path.GetDirectoryName(item.FilePath) ?? "",
                    AddedAt = item.AddedAt != default ? item.AddedAt : DateTime.UtcNow
                };
                
                _context.MediaFiles.Add(newMedia);
                result.Added++;
            }
        }

        await _context.SaveChangesAsync();
        result.Success = true;
        result.Message = $"Import Complete. Added: {result.Added}, Updated: {result.Updated}, Skipped: {result.Skipped}";
        
        return result;
    }
}

public enum ImportMode
{
    AppendOrSkip,
    UpdateExisting,
    ClearAndOverwrite
}

public class ImportResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
}

public class MediaFileExportDto
{
    public string FilePath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public int Year { get; set; }
    public double DurationSeconds { get; set; }
    public int ScanSourceId { get; set; }
    public DateTime AddedAt { get; set; }
}
