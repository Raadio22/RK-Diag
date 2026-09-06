using System.Diagnostics;

namespace GalaxyMonitor.Core;

public sealed record AdaptiveSamplingOptions(
    TimeSpan ActiveInterval,
    TimeSpan IdleInterval,
    TimeSpan BackgroundInterval,
    double CpuActiveThreshold = 20,
    double DiskActiveThreshold = 10)
{
    public static AdaptiveSamplingOptions Default { get; } = new(
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15));
}

public sealed class AdaptiveSampler(IEnumerable<IMetricSource> sources, AdaptiveSamplingOptions options)
{
    private readonly IMetricSource[] _sources = sources.ToArray();

    public async ValueTask<MonitorSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        var tasks = _sources.Select(source => ReadSafeAsync(source, cancellationToken)).ToArray();
        var groups = await Task.WhenAll(tasks).ConfigureAwait(false);
        timer.Stop();
        return new MonitorSnapshot(DateTimeOffset.Now, groups.SelectMany(x => x).ToArray(), timer.Elapsed);
    }

    public TimeSpan ChooseNextInterval(MonitorSnapshot snapshot, bool overlayVisible, bool onBattery)
    {
        if (overlayVisible)
            return options.ActiveInterval;

        var cpu = FindValue(snapshot, "cpu.total.load");
        var disk = FindValue(snapshot, "disk.total.active");
        if (cpu >= options.CpuActiveThreshold || disk >= options.DiskActiveThreshold)
            return options.IdleInterval;

        return onBattery ? options.BackgroundInterval : options.IdleInterval;
    }

    private static double FindValue(MonitorSnapshot snapshot, string id) =>
        snapshot.Metrics.FirstOrDefault(x => x.Id == id)?.Value ?? 0;

    private static async Task<IReadOnlyList<MetricReading>> ReadSafeAsync(
        IMetricSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await source.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new($"source.{source.Name}.error", "Diagnostics", source.Name, null, "", MetricStatus.Error, ex.Message)];
        }
    }
}
