using GalaxyMonitor.Core;
using GalaxyMonitor.History;
using GalaxyMonitor.Windows;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.Title = "RK-Diag";
Console.WriteLine("RK-Diag — diagnostic prototype");
Console.WriteLine(new string('═', 56));

try
{
    var identity = SystemProbe.ReadIdentity();
    Console.WriteLine($"Computer : {identity.Manufacturer} {identity.Model}");
    Console.WriteLine($"CPU      : {identity.Processor}");
    Console.WriteLine($"RAM      : {identity.PhysicalMemoryBytes / 1024d / 1024 / 1024:F1} GB");
    Console.WriteLine($"Graphics : {string.Join(", ", identity.Graphics)}");
    Console.WriteLine($"Disks    : {string.Join(", ", identity.Disks)}");
    Console.WriteLine();
}
catch (Exception ex)
{
    Console.WriteLine($"System identity could not be read: {ex.Message}");
}

await using var native = new NativeSystemSource();
await using var counters = new PerformanceCounterSource();
HardwareSensorSource? hardware = null;
try
{
    hardware = new HardwareSensorSource();
}
catch (Exception ex)
{
    Console.WriteLine($"Hardware sensor library could not start: {ex.Message}");
}

var sources = new List<IMetricSource> { native, counters };
if (hardware is not null)
    sources.Add(hardware);

var sampler = new AdaptiveSampler(sources, AdaptiveSamplingOptions.Default);
var snapshot = await sampler.CaptureAsync();

var databaseOption = Array.IndexOf(args, "--database");
if (databaseOption >= 0 && databaseOption + 1 < args.Length)
{
    await using var history = new SqliteHistoryStore(args[databaseOption + 1]);
    await history.InitializeAsync();
    await history.AppendAsync(snapshot);
    Console.WriteLine($"Snapshot saved to SQLite: {Path.GetFullPath(args[databaseOption + 1])}");
    Console.WriteLine();
}

foreach (var group in snapshot.Metrics.OrderBy(x => x.Category).ThenBy(x => x.Name).GroupBy(x => x.Category))
{
    Console.WriteLine($"[{group.Key}]");
    foreach (var metric in group)
    {
        var value = metric.Value.HasValue ? $"{metric.Value.Value,11:0.###} {metric.Unit}" : $"{metric.Status}";
        Console.WriteLine($"  {value,-20} {metric.Name}");
    }
    Console.WriteLine();
}

var available = snapshot.Metrics.Where(x => x.Status == MetricStatus.Available).ToArray();
Console.WriteLine("[Coverage summary]");
Report("CPU load", available, "CPU", "% Processor", "CPU Total");
Report("CPU clocks", available, "CPU", "Clock");
Report("CPU temperature", available, "CPU", "Temperature");
Report("CPU power", available, "CPU", "Power");
Report("RAM available / commit / pressure", available, "Memory", "Available", "Commit", "pressure");
Report("Intel Arc 3D", available, "GPU", "3D");
Report("Intel Arc video engines", available, "GPU", "Video");
Report("Intel Arc shared memory", available, "GPU", "Shared");
Report("SSD activity / throughput / latency", available, "Storage", "Disk Time", "Disk Read", "sec/Transfer");
Report("SSD temperature", available, "Storage", "Temperature");
Report("SSD SMART / health", available, "Storage", "Remaining Life", "Data Units", "Percentage Used");
Report("Battery", available, "Battery", "Charge");
Console.WriteLine("  ○ NPU: not exposed by the current Windows/LHM providers (planned ETW/ComputeDriver probe)");
Console.WriteLine("  ○ Per-process metrics: CPU/RAM/disk/GPU sampler comes next");
Console.WriteLine();
Console.WriteLine($"Collected {snapshot.Metrics.Count} readings in {snapshot.CollectionDuration.TotalMilliseconds:0} ms.");
Console.WriteLine("Tip: run once normally, then once as Administrator only to compare sensor coverage.");

if (hardware is not null)
    await hardware.DisposeAsync();

static void Report(string label, MetricReading[] metrics, string category, params string[] alternatives)
{
    var found = alternatives.Any(needle => metrics.Any(metric =>
        metric.Category.Equals(category, StringComparison.OrdinalIgnoreCase) &&
        metric.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)));
    Console.WriteLine($"  {(found ? '●' : '○')} {label}");
}
