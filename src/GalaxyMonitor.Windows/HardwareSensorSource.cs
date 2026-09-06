using GalaxyMonitor.Core;
using LibreHardwareMonitor.Hardware;

namespace GalaxyMonitor.Windows;

public sealed class HardwareSensorSource : IMetricSource
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsStorageEnabled = true,
        IsControllerEnabled = true,
        IsMotherboardEnabled = true,
        IsNetworkEnabled = false
    };

    public HardwareSensorSource() => _computer.Open();

    public string Name => "Hardware sensors";

    public ValueTask<IReadOnlyList<MetricReading>> ReadAsync(CancellationToken cancellationToken)
    {
        var readings = new List<MetricReading>();
        foreach (var hardware in _computer.Hardware)
            Visit(hardware, readings, cancellationToken);
        return ValueTask.FromResult<IReadOnlyList<MetricReading>>(readings);
    }

    public ValueTask DisposeAsync()
    {
        _computer.Close();
        return ValueTask.CompletedTask;
    }

    private static void Visit(IHardware hardware, List<MetricReading> readings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        hardware.Update();
        var category = Category(hardware.HardwareType);
        foreach (var sensor in hardware.Sensors.Where(s => s.Value.HasValue))
        {
            readings.Add(new(
                $"lhm.{hardware.Identifier}.{sensor.Identifier}".Replace(' ', '-').ToLowerInvariant(),
                category,
                $"{hardware.Name} / {sensor.Name}",
                sensor.Value,
                Unit(sensor.SensorType)));
        }

        foreach (var child in hardware.SubHardware)
            Visit(child, readings, cancellationToken);
    }

    private static string Category(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuIntel or HardwareType.GpuNvidia or HardwareType.GpuAmd => "GPU",
        HardwareType.Memory => "Memory",
        HardwareType.Storage => "Storage",
        _ => "Hardware"
    };

    private static string Unit(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        SensorType.Energy => "mWh",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Fan => "RPM",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.TimeSpan => "s",
        SensorType.Frequency => "Hz",
        _ => ""
    };
}
