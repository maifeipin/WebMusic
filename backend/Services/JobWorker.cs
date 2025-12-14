using WebMusic.Backend.Services;
using Newtonsoft.Json;
using Microsoft.EntityFrameworkCore;

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
}
