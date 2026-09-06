using WebMusic.Backend.Services;
using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore;
using WebMusic.Backend.Data;
using WebMusic.Backend.Models;
using System.Text.Json;

namespace WebMusic.Backend.Services;

public class JobWorker : BackgroundService
{
    private readonly BackgroundTaskQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobWorker> _logger;
    private readonly ScanStateService _scanState;

    public JobWorker(BackgroundTaskQueue queue, IServiceScopeFactory scopeFactory, ILogger<JobWorker> logger, ScanStateService scanState)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _scanState = scanState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobWorker started.");

        // Recover unfinished enrichment jobs from database on startup
        try
        {
            using var startupScope = _scopeFactory.CreateScope();
            var db = startupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var unfinishedJobs = await db.EnrichmentJobs
                .Where(j => j.Status == "Processing" || j.Status == "Queued")
                .ToListAsync(stoppingToken);

            foreach (var unfinishedJob in unfinishedJobs)
            {
                _logger.LogInformation("Resuming unfinished enrichment job {JobId} from cursor {Cursor}/{Total}", unfinishedJob.Id, unfinishedJob.Cursor, unfinishedJob.Total);
                List<int> songIds = new();
                try
                {
                    songIds = System.Text.Json.JsonSerializer.Deserialize<List<int>>(unfinishedJob.SongIdsJson) ?? new();
                }
                catch { }

                if (songIds.Count > 0)
                {
                    _queue.Enqueue(new FavoritesEnrichmentJob(unfinishedJob.Id, songIds));
                }
                else
                {
                    unfinishedJob.Status = "Failed";
                    unfinishedJob.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to recover unfinished enrichment jobs on startup.");
        }

        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();


                if (job is ScanJob scanJob)
                {
                    await ProcessScanJob(scope, scanJob);
                }
                else if (job is AiBatchJob aiJob)
                {
                    await ProcessAiBatchJob(scope, aiJob, stoppingToken);
                }
                else if (job is LyricsBatchJob lyricsJob)
                {
                    await ProcessLyricsBatchJob(scope, lyricsJob, stoppingToken);
                }
                else if (job is FavoritesEnrichmentJob enrichmentJob)
                {
                    await ProcessFavoritesEnrichmentJob(scope, enrichmentJob, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing background job.");
            }
        }
    }

    private async Task ProcessScanJob(IServiceScope scope, ScanJob job)
    {
        var scanner = scope.ServiceProvider.GetRequiredService<ScannerService>();
        _logger.LogInformation($"Processing scan job for SourceId: {job.SourceId}");
        _scanState.StartScan(job.SourceId);
        try 
        {
            int count = await scanner.ScanSourceAsync(job.SourceId);
            _scanState.FinishScan(count);
            _logger.LogInformation($"Scan job completed. Added {count} files.");
        }
        catch (Exception ex)
        {
             _scanState.FailScan(ex.Message);
             throw;
        }
    }

    private async Task ProcessAiBatchJob(IServiceScope scope, AiBatchJob job, CancellationToken ct)
    {
        // ... (existing code for Gemini tags)
        var tagService = scope.ServiceProvider.GetRequiredService<TagService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<WebMusic.Backend.Data.AppDbContext>();
        
        _logger.LogInformation($"Starting AI Batch {job.BatchId} with {job.SongIds.Count} songs. Model: {job.Model}");
        _queue.UpdateAiStatus(job.BatchId, 0, 0, 0, "Processing");

        int processed = 0;
        int success = 0;
        int failed = 0;
        const int BATCH_SIZE = 15;

        for (int i = 0; i < job.SongIds.Count; i += BATCH_SIZE)
        {
            if (ct.IsCancellationRequested) break;

            var chunkIds = job.SongIds.Skip(i).Take(BATCH_SIZE).ToList();
            var songs = dbContext.MediaFiles
                .Where(m => chunkIds.Contains(m.Id))
                .Select(m => new { 
                    m.Id, m.Title, m.Artist, m.Album, m.Genre, m.Year, 
                    FilePath = m.FilePath
                })
                .ToList();

            if (songs.Count == 0) continue;

            var contextData = songs.Select(m => {
                 var path = m.FilePath.Replace('\\', '/');
                 var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                 var fileName = segments.LastOrDefault() ?? "";
                 var parentFolder = segments.Length > 1 ? segments[segments.Length - 2] : "";
                 return new {
                    m.Id, m.Title, m.Artist, m.Album, m.Genre, m.Year,
                    FileName = fileName, FolderName = parentFolder
                };
            }).ToList();

            try
            {
                var jsonResult = await tagService.GenerateTagsAsync(job.Prompt, contextData, job.Model);
                var suggestions = JsonConvert.DeserializeObject<List<WebMusic.Backend.Controllers.TagsController.SuggestedTag>>(jsonResult);

                if (suggestions != null)
                {
                    foreach (var sug in suggestions)
                    {
                        var target = await dbContext.MediaFiles.FindAsync(sug.Id);
                        if (target != null)
                        {
                            if (!string.IsNullOrEmpty(sug.Title)) target.Title = sug.Title;
                            if (!string.IsNullOrEmpty(sug.Artist)) target.Artist = sug.Artist;
                            if (!string.IsNullOrEmpty(sug.Album)) target.Album = sug.Album;
                            if (!string.IsNullOrEmpty(sug.Genre)) target.Genre = sug.Genre;
                            if (sug.Year > 0) target.Year = sug.Year;
                            success++;
                        }
                        else 
                        {
                            failed++;
                        }
                    }
                    await dbContext.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Batch chunk failed");
                failed += chunkIds.Count;
            }

            processed += chunkIds.Count;
            _queue.UpdateAiStatus(job.BatchId, processed, success, failed, "Processing");
            await Task.Delay(2000, ct); 
        }

        _queue.UpdateAiStatus(job.BatchId, processed, success, failed, "Completed");
        _logger.LogInformation($"AI Batch {job.BatchId} Finished.");
    }

    private async Task ProcessLyricsBatchJob(IServiceScope scope, LyricsBatchJob job, CancellationToken ct)
    {
        var lyricsService = scope.ServiceProvider.GetRequiredService<LyricsService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<WebMusic.Backend.Data.AppDbContext>();
        
        _logger.LogInformation($"Starting Lyrics Batch {job.BatchId} with {job.SongIds.Count} songs.");
        _queue.UpdateAiStatus(job.BatchId, 0, 0, 0, "Processing");

        int processed = 0;
        int success = 0;
        int failed = 0;

        foreach (var songId in job.SongIds)
        {
            if (ct.IsCancellationRequested) break;
            
            try
            {
                // Check if exists first? Or just try generate
                if (!job.Force)
                {
                    var existing = await dbContext.Lyrics.AnyAsync(l => l.MediaFileId == songId);
                    if (existing) 
                    {
                        success++; // Already done
                        processed++;
                        _queue.UpdateAiStatus(job.BatchId, processed, success, failed, "Processing");
                        continue;
                    }
                }

                await lyricsService.GenerateLyricsAsync(songId, job.Language);
                success++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lyrics generation failed for song {songId}");
                failed++;
            }

            processed++;
            _queue.UpdateAiStatus(job.BatchId, processed, success, failed, "Processing");
            
            // Wait 2s between songs to avoid overloading CPU/ASR
            await Task.Delay(2000, ct);
        }

        _queue.UpdateAiStatus(job.BatchId, processed, success, failed, "Completed");
        _logger.LogInformation($"Lyrics Batch {job.BatchId} Finished.");
    }

    private async Task ProcessFavoritesEnrichmentJob(IServiceScope scope, FavoritesEnrichmentJob job, CancellationToken ct)
    {
        var enrichmentService = scope.ServiceProvider.GetRequiredService<MusicEnrichmentService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Load or initialize DB job record
        var dbJob = await db.EnrichmentJobs.FindAsync(new object[] { job.BatchId }, ct);
        if (dbJob == null)
        {
            dbJob = new EnrichmentJob
            {
                Id = job.BatchId,
                Scope = "Favorites",
                Total = job.SongIds.Count,
                Status = "Processing",
                SongIdsJson = System.Text.Json.JsonSerializer.Serialize(job.SongIds),
                StartedAt = DateTime.UtcNow
            };
            db.EnrichmentJobs.Add(dbJob);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            dbJob.Status = "Processing";
            await db.SaveChangesAsync(ct);
        }

        _queue.UpdateAiStatus(job.BatchId, dbJob.Processed, dbJob.Updated, dbJob.Failed, "Processing");

        // Resume from cursor
        var startIndex = dbJob.Cursor;
        for (int i = startIndex; i < job.SongIds.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                dbJob.Status = "Paused";
                await db.SaveChangesAsync(CancellationToken.None);
                break;
            }

            var songId = job.SongIds[i];

            try
            {
                var outcome = await enrichmentService.EnrichMissingAssetsAsync(songId, ct, job.BatchId);
                if (outcome == MusicEnrichmentOutcome.Updated) dbJob.Updated++;
                else if (outcome == MusicEnrichmentOutcome.Unmatched) dbJob.Unmatched++;
                else if (outcome == MusicEnrichmentOutcome.Skipped) dbJob.Skipped++;
                else if (outcome == MusicEnrichmentOutcome.Failed) dbJob.Failed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Music enrichment failed for song {SongId}", songId);
                dbJob.Failed++;
            }

            dbJob.Processed++;
            dbJob.Cursor = i + 1;

            await db.SaveChangesAsync(ct);
            _queue.UpdateAiStatus(job.BatchId, dbJob.Processed, dbJob.Updated, dbJob.Failed, "Processing");
        }

        if (!ct.IsCancellationRequested)
        {
            dbJob.Status = "Completed";
            dbJob.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            _queue.UpdateAiStatus(job.BatchId, dbJob.Processed, dbJob.Updated, dbJob.Failed, "Completed");
            _logger.LogInformation("Favorites enrichment batch {BatchId} finished: {Updated} updated, {Unmatched} unmatched, {Skipped} skipped, {Failed} failed.",
                job.BatchId, dbJob.Updated, dbJob.Unmatched, dbJob.Skipped, dbJob.Failed);
        }
    }
}

