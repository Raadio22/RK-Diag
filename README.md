# RK-Diag

Low-overhead Windows hardware diagnostics and monitor designed first for Samsung Galaxy Book4 Pro 360
NP960QGK-KG3DE (Core Ultra 7 155H, Intel Arc, 16 GB LPDDR5X, Samsung 990 PRO 2 TB).

## Download

The current packaged application is available from the repository's **Releases** page. Extract the
ZIP and run `RK-Diag.exe`. Run it normally for everyday monitoring; use **Run as administrator** only
when CPU temperature, power, clock, or SSD SMART sensors are needed.

The current milestone is a read-only diagnostic monitor. It discovers what Windows and the
notebook firmware expose, shows a live Hardware page, and ranks processes by CPU, RAM, GPU and
disk activity. It installs no service and changes no system setting.

## Run the diagnostic

```powershell
dotnet run --project src/GalaxyMonitor.Diagnostics -c Release
```

## Run the Samsung One UI-inspired desktop dashboard

```powershell
dotnet run --project src/RKDiag.App -c Release
```

To verify SQLite persistence while running the probe, add `-- --database path-to-file.db`.

Run normally first. A second run from an Administrator terminal is useful only as a comparison:
LibreHardwareMonitor may expose additional temperature, clock, power, or SSD SMART sensors.
The packaged prototype also contains `Run diagnostics.cmd` for a simple double-click launch.
Its Windows executable is named `RK-Diag.exe` and embeds the RK-Diag application icon.

## Architecture

- `GalaxyMonitor.Core` — stable metric model and adaptive sampling policy.
- `GalaxyMonitor.Windows` — native Windows, performance-counter and hardware-sensor providers.
- `GalaxyMonitor.History` — compact SQLite/WAL time-series persistence foundation.
- `GalaxyMonitor.Diagnostics` — current sensor discovery and live-value console.
- `RKDiag.App` — the live WPF dashboard and future overlay host.

The Hardware page identifies the current notebook and keeps a searchable, grouped live view of
every metric exposed by the active providers. Metrics are created only when discovered; subsequent
samples update existing rows to avoid rebuilding the visual tree. CPU details are collapsed into a
live summary by default and can be expanded. A separate memory card shows used RAM, current shared
Intel Arc memory, the remaining Windows/application portion, and available RAM without double-counting.

The Processes page uses a separate Windows sampler and updates the visible rows in place. CPU, RAM
and disk stay live; the more expensive per-process GPU counters are sampled less often to keep the
monitor overhead low. The GPU RAM column shows shared memory mapped to each process. Those rows are
not additive because the same composed surface can be mapped by both an application and Windows DWM.
Planned next layers are data retention/aggregation, anomaly detection and a click-through WPF overlay.
No resident Windows service is planned unless later measurements prove one is necessary.
