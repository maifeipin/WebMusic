using WebMusic.Backend.Services;

namespace WebMusic.Backend.Services;

public class ScanWorker : BackgroundService
{
    private readonly BackgroundQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScanWorker> _logger;
    private readonly ScanStateService _scanState;

    public ScanWorker(BackgroundQueue queue, IServiceScopeFactory scopeFactory, ILogger<ScanWorker> logger, ScanStateService scanState)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _scanState = scanState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanWorker started.");

        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var scanner = scope.ServiceProvider.GetRequiredService<ScannerService>();

                _logger.LogInformation($"Processing scan job for SourceId: {job.SourceId}");
                _scanState.StartScan(job.SourceId);

                // Pass the cancellation token to the scanner if supported, or just await
                int count = await scanner.ScanSourceAsync(job.SourceId);

                _scanState.FinishScan(count);
                _logger.LogInformation($"Scan job completed. Added {count} files.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing scan job.");
                _scanState.FailScan(ex.Message);
            }
        }
    }
}
