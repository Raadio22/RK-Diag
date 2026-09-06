using System.Diagnostics;
using GalaxyMonitor.Core;

namespace GalaxyMonitor.Windows;

public sealed class PerformanceCounterSource : IMetricSource
{
    private readonly List<PerformanceCounter> _counters = [];
    private readonly Dictionary<string, List<PerformanceCounter>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public string Name => "Windows performance counters";

    public async ValueTask<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            Discover();
            foreach (var counter in _counters)
                _ = SafeNext(counter);
            foreach (var counter in _groups.Values.SelectMany(x => x))
                _ = SafeNext(counter);
            _initialized = true;
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        var readings = _counters.Select(counter => new MetricReading(
            Id(counter), Category(counter.CategoryName), FriendlyName(counter), SafeNext(counter), Unit(counter.CounterName)))
            .ToList();
        readings.AddRange(_groups.Select(group => new MetricReading(
            $"gpu.{group.Key.ToLowerInvariant()}", "GPU", group.Key,
            GroupValue(group.Key, group.Value), GroupUnit(group.Key))));
        return readings;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var counter in _counters)
            counter.Dispose();
        foreach (var counter in _groups.Values.SelectMany(x => x))
            counter.Dispose();
        return ValueTask.CompletedTask;
    }

    private void Discover()
    {
        Add("Processor Information", "% Processor Utility", "_Total");
        Add("Memory", "Pages Input/sec", null);
        Add("Memory", "Pages Output/sec", null);
        Add("PhysicalDisk", "% Disk Time", "_Total");
        Add("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
        Add("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
        Add("PhysicalDisk", "Avg. Disk sec/Transfer", "_Total");

        AddEngineGroup("3D", "engtype_3D");
        AddEngineGroup("Video Decode", "engtype_VideoDecode");
        AddEngineGroup("Video Processing", "engtype_VideoProcessing");
        AddEngineGroup("Compute", "engtype_Compute");
        AddGroup("Shared memory used", "GPU Adapter Memory", "Shared Usage", _ => true);
        AddGroup("Dedicated memory used", "GPU Adapter Memory", "Dedicated Usage", _ => true);
    }

    private void Add(string category, string counter, string? instance)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(category) || !PerformanceCounterCategory.CounterExists(counter, category))
                return;
            _counters.Add(instance is null
                ? new PerformanceCounter(category, counter, readOnly: true)
                : new PerformanceCounter(category, counter, instance, readOnly: true));
        }
        catch (InvalidOperationException) { }
    }

    private void AddEngineGroup(string name, string engineType) =>
        AddGroup(name, "GPU Engine", "Utilization Percentage",
            instance => instance.Contains(engineType, StringComparison.OrdinalIgnoreCase));

    private void AddGroup(string name, string category, string counter, Func<string, bool> predicate)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(category) || !PerformanceCounterCategory.CounterExists(counter, category))
                return;
            var matches = new PerformanceCounterCategory(category).GetInstanceNames().Where(predicate)
                .Select(instance => new PerformanceCounter(category, counter, instance, readOnly: true)).ToList();
            if (matches.Count > 0)
                _groups[name] = matches;
        }
        catch (InvalidOperationException) { }
    }

    private static float? SafeNext(PerformanceCounter counter)
    {
        try { return counter.NextValue(); }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string Id(PerformanceCounter c) => (c.CategoryName, c.CounterName) switch
    {
        ("Processor Information", "% Processor Utility") => "cpu.total.load",
        ("PhysicalDisk", "% Disk Time") => "disk.total.active",
        ("PhysicalDisk", "Disk Read Bytes/sec") => "disk.total.read",
        ("PhysicalDisk", "Disk Write Bytes/sec") => "disk.total.write",
        ("PhysicalDisk", "Avg. Disk sec/Transfer") => "disk.total.latency",
        ("Memory", "Pages Input/sec") => "memory.pages.input",
        ("Memory", "Pages Output/sec") => "memory.pages.output",
        _ => $"perf.{c.CategoryName}.{c.CounterName}.{c.InstanceName}".Replace(' ', '-').Replace('/', '-').ToLowerInvariant()
    };

    private static string Category(string category) => category.StartsWith("GPU", StringComparison.Ordinal) ? "GPU" :
        category == "PhysicalDisk" ? "Storage" : category == "Processor Information" ? "CPU" : category;

    private static string FriendlyName(PerformanceCounter c) => string.IsNullOrEmpty(c.InstanceName)
        ? c.CounterName
        : $"{c.CounterName} [{c.InstanceName}]";

    private static string Unit(string counter) => counter.Contains("Bytes/sec", StringComparison.Ordinal) ? "B/s" :
        counter.Contains("Usage", StringComparison.Ordinal) ? "B" :
        counter.Contains("sec/", StringComparison.Ordinal) ? "s" :
        counter.Contains('%') || counter.Contains("Utilization", StringComparison.Ordinal) ? "%" : "pages/s";

    private static string GroupUnit(string name) => name.Contains("memory", StringComparison.OrdinalIgnoreCase) ? "B" : "%";

    private static double GroupValue(string name, IEnumerable<PerformanceCounter> counters)
    {
        var values = counters.Select(SafeNext).Where(x => x.HasValue).Select(x => (double)x!.Value).ToArray();
        if (values.Length == 0)
            return 0;
        return name.Contains("memory", StringComparison.OrdinalIgnoreCase) ? values.Sum() : values.Max();
    }
}
