using System.Collections.Concurrent;
using System.Threading.Channels;

namespace WebMusic.Backend.Services;

// A generic job processor to handle multiple types of background tasks (Scanning, AI Tagging)
public interface IJobPayload { }

public record ScanJob(int SourceId, bool Force) : IJobPayload;

public record AiBatchJob(
    string BatchId, 
    List<int> SongIds, 
    string Prompt, 
    string Model
) : IJobPayload;

public record LyricsBatchJob(
    string BatchId, 
    List<int> SongIds, 
    bool Force,
    string Language // Added language
) : IJobPayload;

public class BackgroundTaskQueue
{
    private readonly Channel<IJobPayload> _queue;
    
    // Simple in-memory status tracker for AI jobs
    // In production, use database or Redis
    private readonly ConcurrentDictionary<string, AiJobStatus> _aiJobStatus = new();

    public BackgroundTaskQueue()
    {
        _queue = Channel.CreateUnbounded<IJobPayload>();
    }

    public void Enqueue(IJobPayload job)
    {
        _queue.Writer.TryWrite(job);
        
        if (job is AiBatchJob aiJob)
        {
            _aiJobStatus[aiJob.BatchId] = new AiJobStatus 
            { 
                BatchId = aiJob.BatchId, 
                Total = aiJob.SongIds.Count,
                Status = "Queued" 
            };
        }
        else if (job is LyricsBatchJob lyricsJob)
        {
            _aiJobStatus[lyricsJob.BatchId] = new AiJobStatus 
            { 
                BatchId = lyricsJob.BatchId, 
                Total = lyricsJob.SongIds.Count,
                Status = "Queued" 
            };
        }
    }

    public IAsyncEnumerable<IJobPayload> ReadAllAsync(CancellationToken ct)
    {
        return _queue.Reader.ReadAllAsync(ct);
    }
    
    // Status Helpers
    public void UpdateAiStatus(string batchId, int processed, int success, int failed, string status)
    {
        if (_aiJobStatus.ContainsKey(batchId))
        {
            var s = _aiJobStatus[batchId];
            s.Processed = processed;
            s.Success = success;
            s.Failed = failed;
            s.Status = status;
            if (status == "Completed") s.CompletedAt = DateTime.UtcNow;
        }
    }

    public AiJobStatus? GetAiStatus(string batchId)
    {
        return _aiJobStatus.TryGetValue(batchId, out var status) ? status : null;
    }
}

public class AiJobStatus
{
    public string BatchId { get; set; } = "";
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Success { get; set; }
    public int Failed { get; set; }
    public string Status { get; set; } = "Pending"; // Queued, Processing, Completed, Failed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
