using PcDiag.Core.Events;

namespace PcDiag.Core.Ipc;

/// <summary>A value, or why there is none (e.g. "RequiresElevation", "Unsupported", "Pending").</summary>
/// <param name="Value">The value.</param>
/// <param name="Unavailable">The reason when <paramref name="Value"/> is null.</param>
public readonly record struct LiveMetric(double? Value, string? Unavailable = null);

/// <summary>
/// What the dashboard shows live: the latest values the recorder collected, top processes and recent events.
/// Lists are bounded so a snapshot stays far below the pipe's message limit.
/// </summary>
/// <param name="Timestamp">When the newest values were collected (null before the first sample).</param>
/// <param name="Recording">Whether the recorder is collecting; when not, values are the last ones seen (or none).</param>
/// <param name="Anomalies">Ongoing anomalies and those that ended in the last minutes, newest first.</param>
public sealed record LiveSnapshot(
    DateTimeOffset? Timestamp,
    bool Recording,
    LiveCpu? Cpu,
    LiveMemory? Memory,
    IReadOnlyList<LiveGpu> Gpus,
    IReadOnlyList<LiveDisk> Disks,
    IReadOnlyList<LiveNetwork> Networks,
    LiveProcesses? Processes,
    IReadOnlyList<LiveEvent> RecentEvents,
    LiveDisplay? Display,
    LiveAgent Agent,
    IReadOnlyList<Anomalies.AnomalyInfo>? Anomalies = null);

/// <summary>CPU.</summary>
public sealed record LiveCpu(LiveMetric TotalPercent, LiveMetric FrequencyMhz, LiveMetric PackageTemperatureC, LiveMetric PackagePowerW, int LogicalProcessors);

/// <summary>Memory.</summary>
public sealed record LiveMemory(LiveMetric UsedPercent, LiveMetric UsedBytes, LiveMetric TotalBytes, LiveMetric CommitPercent, LiveMetric HardFaultsPerSec);

/// <summary>One GPU.</summary>
public sealed record LiveGpu(string Key, string Name, LiveMetric UtilizationPercent, LiveMetric VramUsedBytes, LiveMetric VramTotalBytes, LiveMetric TemperatureC, LiveMetric HotspotC, LiveMetric PowerW, LiveMetric CoreClockMhz);

/// <summary>One physical disk.</summary>
public sealed record LiveDisk(string Name, string Volumes, LiveMetric ActivePercent, LiveMetric ReadBytesPerSec, LiveMetric WriteBytesPerSec, LiveMetric TemperatureC);

/// <summary>One network interface.</summary>
public sealed record LiveNetwork(string Name, string Kind, bool Up, LiveMetric ReceiveBytesPerSec, LiveMetric SendBytesPerSec, LiveMetric LinkBitsPerSec, LiveMetric WifiSignalPercent);

/// <summary>
/// A process in a top list. Disk and network rates are known only in Enhanced mode (null otherwise); the I/O rate
/// is every transfer together (files, devices and network).
/// </summary>
public sealed record LiveProcess(
    int Pid,
    string Name,
    double? CpuPercent,
    long? WorkingSetBytes,
    long? PrivateBytes,
    double? GpuPercent,
    double? IoBytesPerSec,
    bool NotResponding,
    double? DiskBytesPerSec = null,
    double? NetBytesPerSec = null,
    long? GpuMemoryBytes = null);

/// <summary>Top processes by resource; <paramref name="ByNetwork"/> only in Enhanced mode.</summary>
public sealed record LiveProcesses(
    int Count,
    IReadOnlyList<LiveProcess> ByCpu,
    IReadOnlyList<LiveProcess> ByMemory,
    IReadOnlyList<LiveProcess> ByGpu,
    IReadOnlyList<LiveProcess> ByIo,
    IReadOnlyList<LiveProcess>? ByNetwork = null,
    IReadOnlyList<LiveProcess>? ByGpuMemory = null);

/// <summary>A recent event (Windows event, display change, process crash/hang, service failure).</summary>
public sealed record LiveEvent(DateTimeOffset Timestamp, string Source, string Title, string Category, Severity Severity);

/// <summary>Displays.</summary>
public sealed record LiveDisplay(bool RemoteSession, IReadOnlyList<string> Displays);

/// <summary>
/// Recent trend of the headline values, columnar (one entry per sample in every list) to stay compact.
/// Percentages, bytes per second and degrees Celsius; null where the value was unavailable.
/// </summary>
/// <param name="Times">Sample times, Unix milliseconds.</param>
/// <param name="Cpu">CPU utilization %.</param>
/// <param name="Memory">Memory in use %.</param>
/// <param name="Gpu">Busiest GPU utilization %.</param>
/// <param name="Vram">Fullest GPU memory %.</param>
/// <param name="Disk">Busiest disk active time %.</param>
/// <param name="NetReceive">Received bytes/s over connected adapters.</param>
/// <param name="NetSend">Sent bytes/s over connected adapters.</param>
/// <param name="CpuTemperature">CPU package temperature.</param>
/// <param name="GpuTemperature">Hottest GPU core temperature.</param>
/// <param name="GpuHotspot">Hottest GPU hotspot temperature.</param>
public sealed record LiveTrend(
    IReadOnlyList<long> Times,
    IReadOnlyList<double?> Cpu,
    IReadOnlyList<double?> Memory,
    IReadOnlyList<double?> Gpu,
    IReadOnlyList<double?> Vram,
    IReadOnlyList<double?> Disk,
    IReadOnlyList<double?> NetReceive,
    IReadOnlyList<double?> NetSend,
    IReadOnlyList<double?> CpuTemperature,
    IReadOnlyList<double?> GpuTemperature,
    IReadOnlyList<double?> GpuHotspot);

/// <summary>The recorder's own cost and buffer.</summary>
public sealed record LiveAgent(LiveMetric CpuPercent, LiveMetric WorkingSetBytes, LiveMetric TickMs, int RingFrames, double RingMegabytes, LiveMetric? ManagedHeapBytes = null);
