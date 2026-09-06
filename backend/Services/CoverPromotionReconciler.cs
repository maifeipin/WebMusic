using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;

namespace WebMusic.Backend.Services;

public static class CoverPromotionReconciler
{
    /// <summary>
    /// Reconciles cover assets on startup or lease recovery.
    /// Detects and removes orphaned data/covers/enriched-* files that were physically copied
    /// but never committed to the database due to a process crash or hard shutdown.
    /// </summary>
    public static async Task<int> ReconcileOrphanCoversAsync(AppDbContext db, IWebHostEnvironment env, int gracePeriodSeconds = 60)
    {
        var cleanedCount = 0;
        var coversDir = Path.Combine(env.ContentRootPath, "data", "covers");
        if (!Directory.Exists(coversDir)) return 0;

        try
        {
            // 1. Gather all active cover filenames referenced in the database
            var activeMediaCovers = await db.MediaFiles
                .Where(m => !string.IsNullOrEmpty(m.CoverArt))
                .Select(m => Path.GetFileName(m.CoverArt!))
                .ToListAsync();

            var activePlaylistCovers = await db.Playlists
                .Where(p => !string.IsNullOrEmpty(p.CoverArt))
                .Select(p => Path.GetFileName(p.CoverArt!))
                .ToListAsync();

            var validCoverNames = new HashSet<string>(activeMediaCovers.Concat(activePlaylistCovers), StringComparer.OrdinalIgnoreCase);

            var threshold = DateTime.UtcNow.AddSeconds(-gracePeriodSeconds);

            // 2. Enumerate covers directory for enriched-* files
            foreach (var filePath in Directory.EnumerateFiles(coversDir, "enriched-*.*"))
            {
                var fileName = Path.GetFileName(filePath);
                if (!validCoverNames.Contains(fileName))
                {
                    var fileInfo = new FileInfo(filePath);
                    if (fileInfo.LastWriteTimeUtc <= threshold)
                    {
                        try
                        {
                            fileInfo.Delete();
                            cleanedCount++;
                        }
                        catch
                        {
                            // Ignored if file locked or in use
                        }
                    }
                }
            }

            // 3. Clean abandoned staged files older than 1 hour
            var stagedDir = Path.Combine(coversDir, "staged");
            if (Directory.Exists(stagedDir))
            {
                var stagedThreshold = DateTime.UtcNow.AddHours(-1);
                foreach (var stagedFile in Directory.EnumerateFiles(stagedDir))
                {
                    var fi = new FileInfo(stagedFile);
                    if (fi.LastWriteTimeUtc <= stagedThreshold)
                    {
                        try { fi.Delete(); } catch { }
                    }
                }
            }
        }
        catch
        {
            // Fail-safe to ensure startup/requests never fail from filesystem enumeration
        }

        return cleanedCount;
    }
}
