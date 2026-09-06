using System.Management;
using GalaxyMonitor.Core;

namespace GalaxyMonitor.Windows;

public static class SystemProbe
{
    public static SystemIdentity ReadIdentity() => new(
        First("Win32_ComputerSystem", "Manufacturer"),
        First("Win32_ComputerSystem", "Model"),
        First("Win32_Processor", "Name"),
        ParseUlong(First("Win32_ComputerSystem", "TotalPhysicalMemory")),
        All("Win32_VideoController", "Name"),
        All("Win32_DiskDrive", "Model"));

    private static string First(string className, string property) =>
        All(className, property).FirstOrDefault() ?? "Unknown";

    private static IReadOnlyList<string> All(string className, string property)
    {
        using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {className}");
        using var results = searcher.Get();
        return results.Cast<ManagementObject>()
            .Select(x => x[property]?.ToString()?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ulong ParseUlong(string value) => ulong.TryParse(value, out var parsed) ? parsed : 0;
}
