namespace GalaxyMonitor.Core;

public sealed record ProcessReading(
    int ProcessId,
    string Name,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateBytes,
    double GpuPercent,
    double Gpu3DPercent,
    double GpuVideoPercent,
    double GpuSharedMemoryBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond);

public sealed record ProcessSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<ProcessReading> Processes,
    TimeSpan CollectionDuration);

public interface IProcessSource : IAsyncDisposable
{
    ValueTask<ProcessSnapshot> ReadAsync(CancellationToken cancellationToken);
}
