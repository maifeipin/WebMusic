using System.Threading.Channels;

namespace WebMusic.Backend.Services;

public record ScanJob(int SourceId, bool Force);

public class BackgroundQueue
{
    private readonly Channel<ScanJob> _queue;

    public BackgroundQueue()
    {
        // Unbounded to allow queuing multiple scans if needed, though usually one at a time.
        _queue = Channel.CreateUnbounded<ScanJob>();
    }

    public void Enqueue(ScanJob job)
    {
        _queue.Writer.TryWrite(job);
    }

    public  IAsyncEnumerable<ScanJob> ReadAllAsync(CancellationToken ct)
    {
        return _queue.Reader.ReadAllAsync(ct);
    }
}
