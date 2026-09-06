using System.ComponentModel;
using System.Runtime.InteropServices;
using GalaxyMonitor.Core;

namespace GalaxyMonitor.Windows;

public sealed class NativeSystemSource : IMetricSource
{
    public string Name => "Windows native";

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metrics = new List<MetricReading>();
        ReadMemory(metrics);
        ReadBattery(metrics);
        return ValueTask.FromResult<IReadOnlyList<MetricReading>>(metrics);
    }

    private static void ReadMemory(List<MetricReading> metrics)
    {
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var perf = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!GetPerformanceInfo(ref perf, perf.Size))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        double gb = 1024d * 1024 * 1024;
        metrics.Add(new("memory.physical.used", "Memory", "Physical used", (memory.TotalPhysical - memory.AvailPhysical) / gb, "GB"));
        metrics.Add(new("memory.physical.available", "Memory", "Available", memory.AvailPhysical / gb, "GB"));
        metrics.Add(new("memory.pressure", "Memory", "Memory pressure", memory.MemoryLoad, "%"));
        metrics.Add(new("memory.commit.used", "Memory", "Commit used", perf.CommitTotal.ToUInt64() * perf.PageSize.ToUInt64() / gb, "GB"));
        metrics.Add(new("memory.commit.limit", "Memory", "Commit limit", perf.CommitLimit.ToUInt64() * perf.PageSize.ToUInt64() / gb, "GB"));
        metrics.Add(new("memory.commit.peak", "Memory", "Commit peak", perf.CommitPeak.ToUInt64() * perf.PageSize.ToUInt64() / gb, "GB"));
    }

    private static void ReadBattery(List<MetricReading> metrics)
    {
        if (!GetSystemPowerStatus(out var status))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        metrics.Add(new("battery.level", "Battery", "Charge", status.BatteryLifePercent == 255 ? null : status.BatteryLifePercent, "%",
            status.BatteryLifePercent == 255 ? MetricStatus.Unavailable : MetricStatus.Available));
        metrics.Add(new("battery.ac", "Battery", "AC connected", status.AcLineStatus == 255 ? null : status.AcLineStatus, "bool",
            status.AcLineStatus == 255 ? MetricStatus.Unavailable : MetricStatus.Available));
        metrics.Add(new("battery.remaining", "Battery", "Estimated remaining", status.BatteryLifeTime == uint.MaxValue ? null : status.BatteryLifeTime / 60d, "min",
            status.BatteryLifeTime == uint.MaxValue ? MetricStatus.Unavailable : MetricStatus.Available));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailPhysical;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable;
        public UIntPtr SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(ref PerformanceInformation information, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
