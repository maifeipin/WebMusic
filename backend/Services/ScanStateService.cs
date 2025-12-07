namespace WebMusic.Backend.Services;

public class ScanStateService
{
    public bool IsScanning { get; private set; }
    public int? CurrentSourceId { get; private set; }
    public int ItemsProcessed { get; private set; }
    public string? LastError { get; private set; }
    public string StatusMessage { get; private set; } = "Idle";

    public void StartScan(int sourceId)
    {
        IsScanning = true;
        CurrentSourceId = sourceId;
        ItemsProcessed = 0;
        LastError = null;
        StatusMessage = "Scanning...";
    }

    public void UpdateProgress(int count)
    {
        ItemsProcessed = count;
        StatusMessage = $"Scanning... ({count} items)";
    }

    public void FinishScan(int totalCount)
    {
        IsScanning = false;
        CurrentSourceId = null;
        ItemsProcessed = totalCount;
        StatusMessage = $"Idle (Last scan added {totalCount} items)";
    }

    public void FailScan(string error)
    {
        IsScanning = false;
        CurrentSourceId = null;
        LastError = error;
        StatusMessage = $"Failed: {error}";
    }
}
