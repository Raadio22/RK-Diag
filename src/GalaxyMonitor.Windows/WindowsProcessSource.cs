using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GalaxyMonitor.Core;

namespace GalaxyMonitor.Windows;

public sealed class WindowsProcessSource : IProcessSource
{
    private readonly Dictionary<int, ProcessState> _previous = [];
    private readonly ProcessGpuTracker _gpuTracker = new();
    private long _lastSampleTimestamp;

    public ValueTask<ProcessSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.Now;
        var elapsedSeconds = _lastSampleTimestamp == 0
            ? 0
            : Stopwatch.GetElapsedTime(_lastSampleTimestamp, started).TotalSeconds;
        _lastSampleTimestamp = started;

        var gpu = _gpuTracker.Sample();
        var current = new Dictionary<int, ProcessState>();
        var readings = new List<ProcessReading>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var id = process.Id;
                    var state = ReadState(process);
                    current[id] = state;
                    _previous.TryGetValue(id, out var previous);

                    var cpu = previous is not null && elapsedSeconds > 0 && state.CpuTime >= previous.CpuTime
                        ? (state.CpuTime - previous.CpuTime).TotalSeconds / elapsedSeconds / Environment.ProcessorCount * 100
                        : 0;
                    var readRate = IoRate(state.ReadBytes, previous?.ReadBytes, elapsedSeconds);
                    var writeRate = IoRate(state.WriteBytes, previous?.WriteBytes, elapsedSeconds);
                    gpu.TryGetValue(id, out var gpuReading);

                    readings.Add(new ProcessReading(
                        id,
                        FriendlyName(process.ProcessName),
                        Math.Clamp(cpu, 0, 100),
                        state.WorkingSetBytes,
                        state.PrivateBytes,
                        Math.Clamp(gpuReading?.Overall ?? 0, 0, 100),
                        Math.Clamp(gpuReading?.ThreeD ?? 0, 0, 100),
                        Math.Clamp(gpuReading?.Video ?? 0, 0, 100),
                        Math.Max(0, gpuReading?.SharedMemoryBytes ?? 0),
                        readRate,
                        writeRate));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (NotSupportedException) { }
            }
        }

        _previous.Clear();
        foreach (var item in current)
            _previous[item.Key] = item.Value;

        var duration = Stopwatch.GetElapsedTime(started);
        return ValueTask.FromResult(new ProcessSnapshot(now, readings, duration));
    }

    public ValueTask DisposeAsync()
    {
        _gpuTracker.Dispose();
        _previous.Clear();
        return ValueTask.CompletedTask;
    }

    private static ProcessState ReadState(Process process)
    {
        ulong readBytes = 0;
        ulong writeBytes = 0;
        try
        {
            if (GetProcessIoCounters(process.Handle, out var counters))
            {
                readBytes = counters.ReadTransferCount;
                writeBytes = counters.WriteTransferCount;
            }
        }
        catch (System.ComponentModel.Win32Exception) { }
        catch (InvalidOperationException) { }

        return new ProcessState(
            SafeRead(() => process.TotalProcessorTime, TimeSpan.Zero),
            SafeRead(() => process.WorkingSet64, 0L),
            SafeRead(() => process.PrivateMemorySize64, 0L),
            readBytes,
            writeBytes);
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (InvalidOperationException) { return fallback; }
        catch (System.ComponentModel.Win32Exception) { return fallback; }
        catch (NotSupportedException) { return fallback; }
    }

    private static double IoRate(ulong current, ulong? previous, double elapsedSeconds) =>
        previous.HasValue && current >= previous.Value && elapsedSeconds > 0
            ? (current - previous.Value) / elapsedSeconds
            : 0;

    private static string FriendlyName(string processName) => processName switch
    {
        "System" => "Systém Windows",
        "Idle" => "Nečinný proces",
        "dwm" => "Správce oken (dwm)",
        _ => processName
    };

    private sealed record ProcessState(
        TimeSpan CpuTime,
        long WorkingSetBytes,
        long PrivateBytes,
        ulong ReadBytes,
        ulong WriteBytes);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    private sealed class ProcessGpuTracker : IDisposable
    {
        private readonly List<GpuCounter> _engineCounters = [];
        private readonly List<GpuMemoryCounter> _memoryCounters = [];
        private DateTimeOffset _lastDiscovery = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSample = DateTimeOffset.MinValue;
        private IReadOnlyDictionary<int, GpuReading> _cached = new Dictionary<int, GpuReading>();

        public IReadOnlyDictionary<int, GpuReading> Sample()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastDiscovery >= TimeSpan.FromMinutes(1))
            {
                Discover();
                _lastSample = now;
                return _cached;
            }

            if (now - _lastSample < TimeSpan.FromSeconds(5))
                return _cached;
            _lastSample = now;

            var result = new Dictionary<int, GpuReading>();
            foreach (var entry in _engineCounters)
            {
                var value = SafeNext(entry.Counter);
                if (!value.HasValue || value.Value <= 0)
                    continue;

                result.TryGetValue(entry.ProcessId, out var existing);
                existing ??= new GpuReading(0, 0, 0, 0);
                var threeD = entry.EngineType == GpuEngineType.ThreeD
                    ? Math.Max(existing.ThreeD, value.Value)
                    : existing.ThreeD;
                var video = entry.EngineType == GpuEngineType.Video
                    ? Math.Max(existing.Video, value.Value)
                    : existing.Video;
                result[entry.ProcessId] = new GpuReading(
                    Math.Max(existing.Overall, value.Value), threeD, video, existing.SharedMemoryBytes);
            }

            foreach (var entry in _memoryCounters)
            {
                var value = SafeNext(entry.Counter);
                if (!value.HasValue || value.Value <= 0)
                    continue;

                result.TryGetValue(entry.ProcessId, out var existing);
                existing ??= new GpuReading(0, 0, 0, 0);
                result[entry.ProcessId] = existing with
                {
                    SharedMemoryBytes = existing.SharedMemoryBytes + value.Value
                };
            }
            _cached = result;
            return _cached;
        }

        public void Dispose()
        {
            foreach (var entry in _engineCounters)
                entry.Counter.Dispose();
            foreach (var entry in _memoryCounters)
                entry.Counter.Dispose();
            _engineCounters.Clear();
            _memoryCounters.Clear();
        }

        private void Discover()
        {
            _lastDiscovery = DateTimeOffset.UtcNow;
            ReplaceEngineCounters(DiscoverEngineCounters());
            ReplaceMemoryCounters(DiscoverMemoryCounters());
        }

        private static List<GpuCounter> DiscoverEngineCounters()
        {
            var replacements = new List<GpuCounter>();
            try
            {
                if (!PerformanceCounterCategory.Exists("GPU Engine") ||
                    !PerformanceCounterCategory.CounterExists("Utilization Percentage", "GPU Engine"))
                    return replacements;

                var candidates = new List<(string Instance, int ProcessId, GpuEngineType EngineType)>();
                foreach (var instance in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                {
                    if (!TryParseProcessId(instance, out var processId))
                        continue;
                    var engineType = EngineType(instance);
                    if (engineType != GpuEngineType.Other)
                        candidates.Add((instance, processId, engineType));
                }

                // One counter per process and engine type is enough for a low-overhead ranking.
                foreach (var candidate in candidates
                             .GroupBy(x => (x.ProcessId, x.EngineType))
                             .Select(x => x.First())
                             .Take(96))
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", candidate.Instance, readOnly: true);
                    _ = SafeNext(counter);
                    replacements.Add(new GpuCounter(candidate.ProcessId, candidate.EngineType, counter));
                }
            }
            catch (InvalidOperationException) { }
            catch (UnauthorizedAccessException) { }
            return replacements;
        }

        private static List<GpuMemoryCounter> DiscoverMemoryCounters()
        {
            var replacements = new List<GpuMemoryCounter>();
            try
            {
                const string category = "GPU Process Memory";
                const string counterName = "Shared Usage";
                if (!PerformanceCounterCategory.Exists(category) ||
                    !PerformanceCounterCategory.CounterExists(counterName, category))
                    return replacements;

                var activeAdapters = DiscoverActiveAdapters();
                var instances = new PerformanceCounterCategory(category).GetInstanceNames()
                    .Where(instance => activeAdapters.Count == 0 ||
                                       activeAdapters.Any(adapter => instance.EndsWith(adapter, StringComparison.OrdinalIgnoreCase)))
                    .Take(256);
                foreach (var instance in instances)
                {
                    if (!TryParseProcessId(instance, out var processId))
                        continue;
                    var counter = new PerformanceCounter(category, counterName, instance, readOnly: true);
                    _ = SafeNext(counter);
                    replacements.Add(new GpuMemoryCounter(processId, counter));
                }
            }
            catch (InvalidOperationException) { }
            catch (UnauthorizedAccessException) { }
            return replacements;
        }

        private static IReadOnlyList<string> DiscoverActiveAdapters()
        {
            var active = new List<string>();
            try
            {
                const string category = "GPU Adapter Memory";
                const string counterName = "Shared Usage";
                if (!PerformanceCounterCategory.Exists(category) ||
                    !PerformanceCounterCategory.CounterExists(counterName, category))
                    return active;

                foreach (var instance in new PerformanceCounterCategory(category).GetInstanceNames())
                {
                    using var counter = new PerformanceCounter(category, counterName, instance, readOnly: true);
                    if (SafeNext(counter) is > 0)
                        active.Add(instance);
                }
            }
            catch (InvalidOperationException) { }
            catch (UnauthorizedAccessException) { }
            return active;
        }

        private void ReplaceEngineCounters(List<GpuCounter> replacements)
        {
            foreach (var entry in _engineCounters)
                entry.Counter.Dispose();
            _engineCounters.Clear();
            _engineCounters.AddRange(replacements);
        }

        private void ReplaceMemoryCounters(List<GpuMemoryCounter> replacements)
        {
            foreach (var entry in _memoryCounters)
                entry.Counter.Dispose();
            _memoryCounters.Clear();
            _memoryCounters.AddRange(replacements);
        }

        private static GpuEngineType EngineType(string instance)
        {
            if (instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                return GpuEngineType.ThreeD;
            if (instance.Contains("engtype_Video", StringComparison.OrdinalIgnoreCase))
                return GpuEngineType.Video;
            if (instance.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase))
                return GpuEngineType.Compute;
            return GpuEngineType.Other;
        }

        private static bool TryParseProcessId(string instance, out int processId)
        {
            processId = 0;
            var marker = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;
            var start = marker + 4;
            var end = instance.IndexOf('_', start);
            if (end <= start)
                return false;
            return int.TryParse(instance.AsSpan(start, end - start), NumberStyles.None,
                CultureInfo.InvariantCulture, out processId);
        }

        private static float? SafeNext(PerformanceCounter counter)
        {
            try { return counter.NextValue(); }
            catch (InvalidOperationException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private enum GpuEngineType { ThreeD, Video, Compute, Other }
        private sealed record GpuCounter(int ProcessId, GpuEngineType EngineType, PerformanceCounter Counter);
        private sealed record GpuMemoryCounter(int ProcessId, PerformanceCounter Counter);
    }

    private sealed record GpuReading(double Overall, double ThreeD, double Video, double SharedMemoryBytes);
}
