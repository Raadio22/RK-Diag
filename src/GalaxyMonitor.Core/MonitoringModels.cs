namespace GalaxyMonitor.Core;

public enum MetricStatus
{
    Available,
    Unavailable,
    PermissionRequired,
    Error
}

public sealed record MetricReading(
    string Id,
    string Category,
    string Name,
    double? Value,
    string Unit,
    MetricStatus Status = MetricStatus.Available,
    string? Detail = null);

public sealed record MonitorSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyList<MetricReading> Metrics,
    TimeSpan CollectionDuration);

public sealed record SystemIdentity(
    string Manufacturer,
    string Model,
    string Processor,
    ulong PhysicalMemoryBytes,
    IReadOnlyList<string> Graphics,
    IReadOnlyList<string> Disks);

public interface IMetricSource : IAsyncDisposable
{
    string Name { get; }
    ValueTask<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken);
}

public interface IHistoryStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask AppendAsync(MonitorSnapshot snapshot, CancellationToken cancellationToken = default);
}
