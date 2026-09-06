using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GalaxyMonitor.Core;
using GalaxyMonitor.Windows;

namespace RKDiag.App;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<IMetricSource> _sources = [];
    private readonly ObservableCollection<HardwareGroupViewModel> _hardwareGroups = [];
    private readonly Dictionary<string, HardwareGroupViewModel> _hardwareGroupViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HardwareMetricViewModel> _hardwareMetricViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ProcessRowViewModel> _processRows = [];
    private readonly WindowsProcessSource _processSource = new();
    private IReadOnlyList<ProcessReading> _latestProcesses = [];
    private AdaptiveSampler? _sampler;
    private Task? _collectionTask;

    public MainWindow()
    {
        InitializeComponent();
        HardwareGroupsItems.ItemsSource = _hardwareGroups;
        ProcessItems.ItemsSource = _processRows;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var identity = SystemProbe.ReadIdentity();
            DeviceSubtitleText.Text = $"{identity.Manufacturer.Replace("ELECTRONICS CO., LTD.", string.Empty).Trim()} {identity.Model}  •  {identity.Processor.Replace("Intel(R) Core(TM)", "Intel Core").Trim()}";
            HardwareModelText.Text = $"{identity.Manufacturer.Replace("ELECTRONICS CO., LTD.", string.Empty).Trim()} {identity.Model}";
            HardwareCpuText.Text = identity.Processor;
            HardwareMemoryText.Text = $"{identity.PhysicalMemoryBytes / 1024d / 1024 / 1024:0.0} GB LPDDR5X";
            HardwareGpuText.Text = string.Join("  •  ", identity.Graphics);
            HardwareStorageText.Text = string.Join("  •  ", identity.Disks.Where(x => !x.Contains("USB", StringComparison.OrdinalIgnoreCase)));

            _sources.Add(new NativeSystemSource());
            _sources.Add(new PerformanceCounterSource());
            try { _sources.Add(new HardwareSensorSource()); }
            catch { /* Native metrics still provide a useful non-admin dashboard. */ }

            _sampler = new AdaptiveSampler(_sources, new AdaptiveSamplingOptions(
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)));
            _collectionTask = RunCollectionLoopAsync(_shutdown.Token);
            await _collectionTask;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CollectorDot.Fill = new SolidColorBrush(Color.FromRgb(213, 71, 63));
            CollectorStatusText.Text = "Sběr byl přerušen";
            LastUpdatedText.Text = ex.Message;
        }
    }

    private async Task RunCollectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _sampler is not null)
        {
            var hardwareTask = Task.Run(
                async () => await _sampler.CaptureAsync(cancellationToken), cancellationToken);
            var processTask = Task.Run(
                async () => await _processSource.ReadAsync(cancellationToken), cancellationToken);
            await Task.WhenAll(hardwareTask, processTask);

            var snapshot = await hardwareTask;
            UpdateDashboard(snapshot);
            UpdateProcessPage(await processTask);

            var onBattery = Find(snapshot, "battery.ac") is < 0.5;
            var visible = WindowState != WindowState.Minimized && IsActive;
            var delay = _sampler.ChooseNextInterval(snapshot, visible, onBattery);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private void UpdateDashboard(MonitorSnapshot snapshot)
    {
        var cpu = Find(snapshot, "cpu.total.load") ?? FindByName(snapshot, "CPU", "CPU Total") ?? 0;
        var memory = Find(snapshot, "memory.pressure") ?? 0;
        var gpu = Find(snapshot, "gpu.3d") ?? FindByName(snapshot, "GPU", "D3D 3D") ?? 0;
        var disk = Find(snapshot, "disk.total.active") ?? 0;
        var battery = Find(snapshot, "battery.level") ?? 0;
        var availableGb = Find(snapshot, "memory.physical.available") ?? 0;
        var usedGb = Find(snapshot, "memory.physical.used") ?? 0;
        var sharedGpuBytes = Find(snapshot, "gpu.shared memory used") ?? 0;
        var readBytes = Find(snapshot, "disk.total.read") ?? 0;
        var writeBytes = Find(snapshot, "disk.total.write") ?? 0;
        var acConnected = Find(snapshot, "battery.ac") is >= 0.5;

        var cpuTemperature = FindPreferredCpuTemperature(snapshot);
        SetMetric(CpuValueText, CpuDetailText, CpuProgress, cpu, $"{cpu:0} %", TemperatureDetail(cpuTemperature));
        CpuDetailText.Foreground = new SolidColorBrush(cpuTemperature is >= 95
            ? Color.FromRgb(205, 64, 56)
            : Color.FromRgb(116, 119, 125));
        var sharedGpuGb = sharedGpuBytes / 1024 / 1024 / 1024;
        var pcUsedGb = Math.Max(0, usedGb - sharedGpuGb);
        SetMetric(MemoryValueText, MemoryDetailText, MemoryProgress, memory, $"{usedGb:0.0} GB",
            sharedGpuGb > 0
                ? $"PC {pcUsedGb:0.0} GB  •  grafika {sharedGpuGb:0.0} GB\nDostupná {availableGb:0.0} GB"
                : $"PC {usedGb:0.0} GB\nDostupná {availableGb:0.0} GB");
        SetMetric(GpuValueText, GpuDetailText, GpuProgress, gpu, $"{gpu:0} %", sharedGpuGb > 0 ? $"{sharedGpuGb:0.0} GB sdílené RAM" : "Intel Arc Graphics");
        SetMetric(DiskValueText, DiskDetailText, DiskProgress, disk, $"{Math.Clamp(disk, 0, 100):0} %", $"Čtení {FormatRate(readBytes)}  •  Zápis {FormatRate(writeBytes)}");
        SetMetric(BatteryValueText, BatteryDetailText, BatteryProgress, battery, $"{battery:0} %", acConnected ? "Napájení ze sítě" : "Provoz na baterii");

        CpuSparkline.AddValue(cpu);
        SetAvailability(CpuTemperatureStatus, cpuTemperature.HasValue);
        SetAvailability(GpuSensorStatus, Has(snapshot, "GPU", "3D") && Has(snapshot, "GPU", "Video"));
        SetAvailability(SsdSensorStatus, Has(snapshot, "Storage", "Temperature") || Has(snapshot, "Storage", "Remaining Life"));
        SetAvailability(PagingSensorStatus, snapshot.Metrics.Any(x => x.Id == "memory.pages.input"));

        CollectorDot.Fill = new SolidColorBrush(Color.FromRgb(36, 179, 107));
        CollectorStatusText.Text = "Sběr je aktivní";
        LastUpdatedText.Text = $"Aktualizováno {snapshot.CapturedAt:HH:mm:ss}";
        UpdateMemoryBreakdown(usedGb, availableGb, sharedGpuGb);
        UpdateHardwarePage(snapshot);
    }

    private void UpdateMemoryBreakdown(double usedGb, double availableGb, double sharedGpuGb)
    {
        var withoutGpuGb = Math.Max(0, usedGb - sharedGpuGb);
        HardwareMemoryUsedText.Text = $"{usedGb:0.0} GB";
        HardwareMemoryWithoutGpuText.Text = $"{withoutGpuGb:0.0} GB";
        HardwareMemoryGpuText.Text = sharedGpuGb > 0 ? $"{sharedGpuGb:0.0} GB" : "Nedostupné";
        HardwareMemoryAvailableText.Text = $"{availableGb:0.0} GB";
    }

    private void UpdateHardwarePage(MonitorSnapshot snapshot)
    {
        foreach (var reading in snapshot.Metrics
                     .OrderBy(x => CategoryOrder(x.Category))
                     .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_hardwareMetricViews.TryGetValue(reading.Id, out var metricView))
            {
                if (!_hardwareGroupViews.TryGetValue(reading.Category, out var groupView))
                {
                    groupView = new HardwareGroupViewModel(
                        reading.Category, CategoryName(reading.Category), CategoryBrush(reading.Category));
                    _hardwareGroupViews.Add(reading.Category, groupView);
                    _hardwareGroups.Add(groupView);
                }

                metricView = new HardwareMetricViewModel(reading.Id, reading.Name, reading.Category);
                _hardwareMetricViews.Add(reading.Id, metricView);
                groupView.Metrics.Add(metricView);
            }

            metricView.Update(reading);
        }

        foreach (var group in _hardwareGroups)
            group.Summary = HardwareGroupSummary(group.Category, snapshot);

        var available = snapshot.Metrics.Count(x => x.Status == MetricStatus.Available && x.Value.HasValue);
        var unavailable = snapshot.Metrics.Count - available;
        HardwareSummaryText.Text = unavailable > 0
            ? $"{available} dostupných hodnot  •  {unavailable} nedostupných"
            : $"{available} dostupných hodnot";
        HardwareLastUpdateText.Text = $"{snapshot.CapturedAt:HH:mm:ss}";
        ApplyHardwareFilter(HardwareSearchBox.Text);
    }

    private void UpdateProcessPage(ProcessSnapshot snapshot)
    {
        _latestProcesses = snapshot.Processes;
        var measurable = snapshot.Processes.Where(x => x.WorkingSetBytes > 0).ToArray();
        ProcessesSummaryText.Text = $"{measurable.Length} běžících procesů  •  měření {snapshot.CollectionDuration.TotalMilliseconds:0} ms";

        var candidates = measurable.Where(x => !x.Name.Equals("Nečinný proces", StringComparison.OrdinalIgnoreCase)).ToArray();
        SetTopProcess(TopCpuProcessText, TopCpuProcessDetail,
            candidates.MaxBy(x => x.CpuPercent), x => $"{x.CpuPercent:0.0} %");
        SetTopProcess(TopMemoryProcessText, TopMemoryProcessDetail,
            candidates.MaxBy(x => x.WorkingSetBytes), x => FormatMemoryGb(x.WorkingSetBytes));
        SetTopProcess(TopGpuProcessText, TopGpuProcessDetail,
            candidates.MaxBy(x => x.GpuPercent),
            x => $"{x.GpuPercent:0.0} %  •  {FormatMemoryGb(x.GpuSharedMemoryBytes)} RAM");
        SetTopProcess(TopGpuMemoryProcessText, TopGpuMemoryProcessDetail,
            candidates.MaxBy(x => x.GpuSharedMemoryBytes), x => FormatMemoryGb(x.GpuSharedMemoryBytes));
        SetTopProcess(TopDiskProcessText, TopDiskProcessDetail,
            candidates.MaxBy(x => x.DiskReadBytesPerSecond + x.DiskWriteBytesPerSecond),
            x => FormatRate(x.DiskReadBytesPerSecond + x.DiskWriteBytesPerSecond));

        ApplyProcessFilter();
    }

    private static void SetTopProcess(TextBlock nameText, TextBlock detailText, ProcessReading? process,
        Func<ProcessReading, string> detail)
    {
        nameText.Text = process?.Name ?? "—";
        detailText.Text = process is null ? "—" : detail(process);
    }

    private static void SetMetric(TextBlock valueText, TextBlock detailText, ProgressBar progress, double value, string display, string detail)
    {
        valueText.Text = display;
        progress.Value = Math.Clamp(value, 0, 100);
        detailText.Text = detail;
    }

    private static void SetAvailability(TextBlock text, bool available)
    {
        text.Text = available ? "Dostupné" : "Nedostupné";
        text.Foreground = new SolidColorBrush(available ? Color.FromRgb(31, 150, 88) : Color.FromRgb(163, 106, 0));
    }

    private static double? Find(MonitorSnapshot snapshot, string id) =>
        snapshot.Metrics.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Value;

    private static double? FindByName(MonitorSnapshot snapshot, string category, string name) =>
        snapshot.Metrics.FirstOrDefault(x => x.Category == category && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static bool Has(MonitorSnapshot snapshot, string category, string name) =>
        snapshot.Metrics.Any(x => x.Category == category && x.Status == MetricStatus.Available && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    private static double? FindPreferredCpuTemperature(MonitorSnapshot snapshot)
    {
        return snapshot.Metrics
            .Where(x => x.Category == "CPU" && x.Unit == "°C" && x.Value.HasValue &&
                        !x.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => CpuTemperaturePriority(x.Name))
            .Select(x => x.Value)
            .FirstOrDefault();
    }

    private static int CpuTemperaturePriority(string name)
    {
        if (name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("Core Average", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("Core Max", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private static string TemperatureDetail(double? temperature) => temperature.HasValue
        ? temperature >= 95 ? $"Vysoká teplota {temperature:0} °C" : $"Teplota {temperature:0} °C"
        : "Teplota není dostupná";

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024 / 1024:0.0} GB/s";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0} kB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    private static string FormatMemoryGb(double bytes) => $"{bytes / 1024 / 1024 / 1024:0.00} GB";

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton selected)
            return;

        if (selected == OverviewNav)
        {
            OverviewPage.Visibility = Visibility.Visible;
            HardwarePage.Visibility = Visibility.Collapsed;
            ProcessesPage.Visibility = Visibility.Collapsed;
            PlaceholderPage.Visibility = Visibility.Collapsed;
            return;
        }

        if (selected == HardwareNav)
        {
            OverviewPage.Visibility = Visibility.Collapsed;
            HardwarePage.Visibility = Visibility.Visible;
            ProcessesPage.Visibility = Visibility.Collapsed;
            PlaceholderPage.Visibility = Visibility.Collapsed;
            return;
        }

        if (selected == ProcessesNav)
        {
            OverviewPage.Visibility = Visibility.Collapsed;
            HardwarePage.Visibility = Visibility.Collapsed;
            ProcessesPage.Visibility = Visibility.Visible;
            PlaceholderPage.Visibility = Visibility.Collapsed;
            return;
        }

        OverviewPage.Visibility = Visibility.Collapsed;
        HardwarePage.Visibility = Visibility.Collapsed;
        ProcessesPage.Visibility = Visibility.Collapsed;
        PlaceholderPage.Visibility = Visibility.Visible;
        (PlaceholderTitle.Text, PlaceholderText.Text) = selected.Name switch
        {
            nameof(HistoryNav) => ("Historie", "Denní, týdenní a měsíční grafy se napojí na připravenou SQLite historii."),
            _ => ("Nastavení", "Tady nastavíme overlay, intervaly sběru, motiv a spuštění s Windows.")
        };
    }

    private void HardwareSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (HardwareSearchHint is not null)
        {
            HardwareSearchHint.Visibility = string.IsNullOrWhiteSpace(HardwareSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        ApplyHardwareFilter(HardwareSearchBox.Text);
    }

    private void HardwareGroupHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HardwareGroupViewModel group })
            group.ToggleExpanded();
    }

    private void ApplyHardwareFilter(string? searchText)
    {
        var query = searchText?.Trim() ?? string.Empty;
        var anyGroupVisible = false;
        foreach (var group in _hardwareGroups)
        {
            var groupMatch = group.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            var anyVisible = false;
            foreach (var metric in group.Metrics)
            {
                var visible = string.IsNullOrEmpty(query) || groupMatch ||
                              metric.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                              metric.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
                metric.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                anyVisible |= visible;
            }
            group.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
            group.SetFilterOpen(!string.IsNullOrEmpty(query) && anyVisible);
            anyGroupVisible |= anyVisible;
        }
        if (HardwareEmptyFilterPanel is not null)
            HardwareEmptyFilterPanel.Visibility = !string.IsNullOrEmpty(query) && !anyGroupVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ProcessSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ProcessSearchHint is not null)
            ProcessSearchHint.Visibility = string.IsNullOrWhiteSpace(ProcessSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        ApplyProcessFilter();
    }

    private void ProcessSortBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyProcessFilter();

    private void ApplyProcessFilter()
    {
        if (ProcessItems is null || ProcessSortBox is null)
            return;

        var query = ProcessSearchBox?.Text.Trim() ?? string.Empty;
        var sort = (ProcessSortBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "CPU";
        var filtered = _latestProcesses.Where(x =>
            x.WorkingSetBytes > 0 &&
            (string.IsNullOrEmpty(query) || x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             x.ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)));

        filtered = sort switch
        {
            "RAM" => filtered.OrderByDescending(x => x.WorkingSetBytes),
            "GPU" => filtered.OrderByDescending(x => x.GpuPercent),
            "GpuRam" => filtered.OrderByDescending(x => x.GpuSharedMemoryBytes),
            "Disk" => filtered.OrderByDescending(x => x.DiskReadBytesPerSecond + x.DiskWriteBytesPerSecond),
            _ => filtered.OrderByDescending(x => x.CpuPercent).ThenByDescending(x => x.WorkingSetBytes)
        };

        var selected = filtered.Take(40).ToArray();
        for (var index = 0; index < selected.Length; index++)
        {
            if (index < _processRows.Count)
                _processRows[index].Update(selected[index]);
            else
                _processRows.Add(new ProcessRowViewModel(selected[index]));
        }
        while (_processRows.Count > selected.Length)
            _processRows.RemoveAt(_processRows.Count - 1);

        ProcessesEmptyPanel.Visibility = selected.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string HardwareGroupSummary(string category, MonitorSnapshot snapshot)
    {
        var parts = new List<string>();
        switch (category)
        {
            case "CPU":
                AddPart(parts, Find(snapshot, "cpu.total.load"), "%", "0");
                AddPart(parts, FindPreferredCpuTemperature(snapshot), "°C", "0");
                AddPart(parts, FindPreferred(snapshot, "CPU", "W", "Package"), "W", "0.0");
                AddPart(parts, FindPreferred(snapshot, "CPU", "MHz", "Core Average"), "MHz", "0");
                break;
            case "Memory":
                AddPart(parts, Find(snapshot, "memory.pressure"), "% využito", "0");
                AddPart(parts, Find(snapshot, "memory.physical.available"), "GB volno", "0.0");
                break;
            case "GPU":
                AddPart(parts, Find(snapshot, "gpu.3d"), "% 3D", "0");
                var shared = Find(snapshot, "gpu.shared memory used");
                if (shared.HasValue) parts.Add($"{shared.Value / 1024 / 1024 / 1024:0.0} GB sdílená RAM");
                break;
            case "Storage":
                AddPart(parts, Find(snapshot, "disk.total.active"), "% aktivita", "0");
                parts.Add($"čtení {FormatRate(Find(snapshot, "disk.total.read") ?? 0)}");
                parts.Add($"zápis {FormatRate(Find(snapshot, "disk.total.write") ?? 0)}");
                break;
            case "Battery":
                AddPart(parts, Find(snapshot, "battery.level"), "%", "0");
                parts.Add(Find(snapshot, "battery.ac") is >= 0.5 ? "napájení ze sítě" : "provoz na baterii");
                break;
        }
        return parts.Count > 0 ? string.Join("  •  ", parts) : "Kliknutím zobrazíte podrobnosti";
    }

    private static double? FindPreferred(MonitorSnapshot snapshot, string category, string unit, string preferredName)
    {
        var values = snapshot.Metrics.Where(x => x.Category == category && x.Unit == unit && x.Value.HasValue).ToArray();
        return values.FirstOrDefault(x => x.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase))?.Value
               ?? values.FirstOrDefault()?.Value;
    }

    private static void AddPart(List<string> parts, double? value, string suffix, string format)
    {
        if (value.HasValue)
            parts.Add($"{value.Value.ToString(format, System.Globalization.CultureInfo.CurrentCulture)} {suffix}");
    }

    private static int CategoryOrder(string category) => category switch
    {
        "CPU" => 0,
        "Memory" => 1,
        "GPU" => 2,
        "Storage" => 3,
        "Battery" => 4,
        "Hardware" => 5,
        _ => 6
    };

    private static string CategoryName(string category) => category switch
    {
        "Memory" => "Paměť",
        "Storage" => "Úložiště",
        "Battery" => "Baterie",
        "Hardware" => "Ostatní hardware",
        "Diagnostics" => "Diagnostika",
        _ => category
    };

    private static Brush CategoryBrush(string category) => new SolidColorBrush(category switch
    {
        "CPU" => Color.FromRgb(3, 129, 254),
        "Memory" => Color.FromRgb(0, 166, 178),
        "GPU" => Color.FromRgb(118, 87, 232),
        "Storage" => Color.FromRgb(240, 154, 49),
        "Battery" => Color.FromRgb(36, 179, 107),
        _ => Color.FromRgb(96, 125, 139)
    });

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _shutdown.Cancel();
        if (_collectionTask is not null)
        {
            try { await _collectionTask; }
            catch (OperationCanceledException) { }
        }
        foreach (var source in _sources)
            await source.DisposeAsync();
        await _processSource.DisposeAsync();
        _shutdown.Dispose();
    }
}

public sealed class HardwareGroupViewModel : INotifyPropertyChanged
{
    private Visibility _visibility = Visibility.Visible;
    private bool _isExpanded;
    private bool _filterOpen;
    private string _summary = "Načítám souhrn";

    public HardwareGroupViewModel(string category, string displayName, Brush accent)
    {
        Category = category;
        DisplayName = displayName;
        Accent = accent;
        _isExpanded = category != "CPU";
    }

    public string Category { get; }
    public string DisplayName { get; }
    public Brush Accent { get; }
    public ObservableCollection<HardwareMetricViewModel> Metrics { get; } = [];

    public string Summary
    {
        get => _summary;
        set { if (_summary != value) { _summary = value; OnPropertyChanged(); } }
    }

    public Visibility DetailsVisibility => _isExpanded || _filterOpen ? Visibility.Visible : Visibility.Collapsed;
    public string ToggleText => _isExpanded ? "Skrýt" : "Rozbalit";

    public Visibility Visibility
    {
        get => _visibility;
        set { if (_visibility != value) { _visibility = value; OnPropertyChanged(); } }
    }

    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        OnPropertyChanged(nameof(DetailsVisibility));
        OnPropertyChanged(nameof(ToggleText));
    }

    public void SetFilterOpen(bool filterOpen)
    {
        if (_filterOpen == filterOpen)
            return;
        _filterOpen = filterOpen;
        OnPropertyChanged(nameof(DetailsVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class HardwareMetricViewModel : INotifyPropertyChanged
{
    private string _displayValue = "—";
    private string _detail = string.Empty;
    private Brush _valueBrush = new SolidColorBrush(Color.FromRgb(23, 25, 28));
    private Visibility _visibility = Visibility.Visible;

    public HardwareMetricViewModel(string id, string name, string category)
    {
        Id = id;
        Name = name;
        Category = category;
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }

    public string DisplayValue
    {
        get => _displayValue;
        private set { if (_displayValue != value) { _displayValue = value; OnPropertyChanged(); } }
    }

    public string Detail
    {
        get => _detail;
        private set { if (_detail != value) { _detail = value; OnPropertyChanged(); } }
    }

    public Brush ValueBrush
    {
        get => _valueBrush;
        private set { _valueBrush = value; OnPropertyChanged(); }
    }

    public Visibility Visibility
    {
        get => _visibility;
        set { if (_visibility != value) { _visibility = value; OnPropertyChanged(); } }
    }

    public void Update(MetricReading reading)
    {
        DisplayValue = FormatValue(reading);
        Detail = reading.Status == MetricStatus.Available ? string.Empty : reading.Detail ?? StatusText(reading.Status);
        ValueBrush = new SolidColorBrush(reading.Status switch
        {
            MetricStatus.Available => Color.FromRgb(23, 25, 28),
            MetricStatus.PermissionRequired => Color.FromRgb(163, 106, 0),
            MetricStatus.Unavailable => Color.FromRgb(163, 106, 0),
            _ => Color.FromRgb(205, 64, 56)
        });
    }

    private static string FormatValue(MetricReading reading)
    {
        if (!reading.Value.HasValue)
            return StatusText(reading.Status);

        var value = reading.Value.Value;
        if (IsMemoryReading(reading))
        {
            var gigabytes = reading.Unit switch
            {
                "B" => value / 1024 / 1024 / 1024,
                "MB" => value / 1024,
                _ => value
            };
            return $"{gigabytes:0.00} GB";
        }

        return reading.Unit switch
        {
            "%" => $"{value:0.0} %",
            "°C" => $"{value:0.0} °C",
            "W" => $"{value:0.00} W",
            "MHz" => $"{value:0} MHz",
            "GB" => $"{value:0.00} GB",
            "MB" => $"{value:0} MB",
            "B" => FormatBytes(value),
            "B/s" => FormatRate(value),
            "pages/s" => $"{value:0.0} str./s",
            "s" when value < 1 => $"{value * 1000:0.00} ms",
            "s" => $"{value:0.00} s",
            "min" => $"{value:0} min",
            "bool" => value >= 0.5 ? "Ano" : "Ne",
            "V" => $"{value:0.000} V",
            "A" => $"{value:0.000} A",
            "RPM" => $"{value:0} RPM",
            _ => string.IsNullOrEmpty(reading.Unit) ? $"{value:0.###}" : $"{value:0.###} {reading.Unit}"
        };
    }

    private static bool IsMemoryReading(MetricReading reading) =>
        (reading.Unit is "B" or "MB" or "GB") &&
        (reading.Category == "Memory" ||
         (reading.Category == "GPU" &&
          (reading.Name.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
           reading.Name.Contains("VRAM", StringComparison.OrdinalIgnoreCase))));

    private static string FormatBytes(double bytes)
    {
        if (bytes >= 1024 * 1024 * 1024) return $"{bytes / 1024 / 1024 / 1024:0.00} GB";
        if (bytes >= 1024 * 1024) return $"{bytes / 1024 / 1024:0.0} MB";
        if (bytes >= 1024) return $"{bytes / 1024:0.0} kB";
        return $"{bytes:0} B";
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024 / 1024:0.00} GB/s";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0.0} kB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    private static string StatusText(MetricStatus status) => status switch
    {
        MetricStatus.Unavailable => "Nedostupné",
        MetricStatus.PermissionRequired => "Vyžaduje správce",
        MetricStatus.Error => "Chyba",
        _ => "—"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ProcessRowViewModel : INotifyPropertyChanged
{
    private int _processId;
    private string _name = string.Empty;
    private string _cpuText = "0 %";
    private string _memoryText = "0,00 GB";
    private string _gpuText = "0 %";
    private string _gpuMemoryText = "0,00 GB";
    private string _diskText = "0 B/s";
    private string _activityDetail = string.Empty;

    public ProcessRowViewModel(ProcessReading reading) => Update(reading);

    public int ProcessId { get => _processId; private set => Set(ref _processId, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string GpuMemoryText { get => _gpuMemoryText; private set => Set(ref _gpuMemoryText, value); }
    public string DiskText { get => _diskText; private set => Set(ref _diskText, value); }
    public string ActivityDetail { get => _activityDetail; private set => Set(ref _activityDetail, value); }

    public void Update(ProcessReading reading)
    {
        ProcessId = reading.ProcessId;
        Name = reading.Name;
        CpuText = $"{reading.CpuPercent:0.0} %";
        MemoryText = FormatMemoryGb(reading.WorkingSetBytes);
        GpuText = $"{reading.GpuPercent:0.0} %";
        GpuMemoryText = FormatMemoryGb(reading.GpuSharedMemoryBytes);
        DiskText = FormatRate(reading.DiskReadBytesPerSecond + reading.DiskWriteBytesPerSecond);
        ActivityDetail = reading.GpuPercent > 0.05
            ? $"3D {reading.Gpu3DPercent:0.0} %  •  video {reading.GpuVideoPercent:0.0} %"
            : $"Soukromá paměť {FormatMemoryGb(reading.PrivateBytes)}";
    }

    private static string FormatMemoryGb(double bytes) => $"{bytes / 1024 / 1024 / 1024:0.00} GB";

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024 / 1024:0.0} GB/s";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0} kB/s";
        return $"{bytesPerSecond:0} B/s";
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
