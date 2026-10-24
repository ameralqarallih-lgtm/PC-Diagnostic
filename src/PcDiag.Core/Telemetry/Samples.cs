namespace PcDiag.Core.Telemetry;

/// <summary>System-wide CPU metrics for one tick.</summary>
/// <param name="TotalPercent">Usage like Task Manager ("% Processor Time": the share of time the processors were busy, 0-100).</param>
/// <param name="FrequencyMhz">Effective frequency: nominal frequency × "% Processor Performance".</param>
/// <param name="MaxFrequencyMhz">
/// Rated (base) frequency as reported by Windows. Effective frequency exceeds it under turbo; the turbo ceiling
/// is not exposed by Windows APIs and needs a hardware sensor provider.
/// </param>
/// <param name="PerformancePercent">Current performance relative to base frequency (can exceed 100).</param>
/// <param name="PerformanceLimitPercent">Share of performance allowed by power/thermal/firmware limits (100 = no limit).</param>
public sealed record CpuSample(
    DateTimeOffset Timestamp,
    Reading<double> TotalPercent,
    Reading<double> UserPercent,
    Reading<double> KernelPercent,
    Reading<double> FrequencyMhz,
    Reading<double> MaxFrequencyMhz,
    Reading<double> PerformancePercent,
    Reading<double> PerformanceLimitPercent,
    Reading<long> PerformanceLimitFlags,
    Reading<double> ProcessorQueueLength,
    Reading<double> ContextSwitchesPerSec,
    Reading<double> InterruptsPerSec,
    Reading<double> PackageTemperatureC,
    Reading<double> PackagePowerW,
    IReadOnlyList<CpuCoreSample> Cores);

/// <summary>Metrics for one logical processor.</summary>
public sealed record CpuCoreSample(int Index, Reading<double> UtilizationPercent, Reading<double> FrequencyMhz);

/// <summary>System-wide memory metrics for one tick.</summary>
public sealed record MemorySample(
    DateTimeOffset Timestamp,
    Reading<long> TotalBytes,
    Reading<long> AvailableBytes,
    Reading<long> FreeBytes,
    Reading<long> CacheBytes,
    Reading<long> CommittedBytes,
    Reading<long> CommitLimitBytes,
    Reading<long> PagedPoolBytes,
    Reading<long> NonPagedPoolBytes,
    Reading<long> CompressedBytes,
    Reading<double> PageFaultsPerSec,
    Reading<double> HardFaultsPerSec,
    Reading<double> PagesPerSec,
    Reading<double> Pressure);

/// <summary>Metrics for all GPU adapters for one tick, plus per-process GPU usage.</summary>
/// <param name="Processes">Processes using a GPU this tick (bounded list, highest usage first).</param>
public sealed record GpuSample(DateTimeOffset Timestamp, IReadOnlyList<GpuAdapterSample> Adapters, IReadOnlyList<GpuProcessUsage> Processes);

/// <summary>Identity and static facts of a GPU adapter.</summary>
/// <param name="AdapterKey">Stable key across reboots (vendor/device/subsystem ids, plus an ordinal for identical cards).</param>
/// <param name="Name">Adapter description from the driver.</param>
/// <param name="Vendor">Vendor name (NVIDIA, AMD, Intel, Microsoft...) or the hex vendor id.</param>
/// <param name="DedicatedMemoryBytes">Dedicated video memory (VRAM) size.</param>
/// <param name="SharedMemoryBytes">System memory the adapter may share.</param>
/// <param name="TemperatureMaxC">Driver-reported maximum temperature.</param>
/// <param name="TemperatureWarningC">Driver-reported warning temperature.</param>
/// <param name="DetailSources">Which detail providers supply data for this adapter (e.g. "nvml", "d3dkmt").</param>
public sealed record GpuAdapterDescriptor(
    string AdapterKey,
    string Name,
    string Vendor,
    uint VendorId,
    uint DeviceId,
    long DedicatedMemoryBytes,
    long SharedMemoryBytes,
    Reading<double> TemperatureMaxC,
    Reading<double> TemperatureWarningC,
    IReadOnlyList<string> DetailSources);

/// <summary>Metrics for one GPU adapter.</summary>
/// <param name="UtilizationPercent">Task Manager-style utilization: the busiest engine.</param>
/// <param name="VramUsedBytes">Dedicated memory in use, system-wide.</param>
/// <param name="VramTotalBytes">Dedicated memory size.</param>
/// <param name="PowerW">Board power in watts (vendor API).</param>
/// <param name="PowerPercent">Power as a percentage of the board limit (vendor-neutral D3DKMT).</param>
/// <param name="ThrottleFlags">Vendor-specific clock-limit reason flags (NVIDIA: clock event reasons).</param>
public sealed record GpuAdapterSample(
    GpuAdapterDescriptor Adapter,
    Reading<double> UtilizationPercent,
    Reading<double> Utilization3dPercent,
    Reading<double> UtilizationComputePercent,
    Reading<double> UtilizationDecodePercent,
    Reading<double> UtilizationEncodePercent,
    Reading<double> UtilizationCopyPercent,
    Reading<long> VramUsedBytes,
    Reading<long> VramTotalBytes,
    Reading<long> SharedUsedBytes,
    Reading<double> TemperatureC,
    Reading<double> HotspotTemperatureC,
    Reading<double> CoreClockMhz,
    Reading<double> MemoryClockMhz,
    Reading<double> PowerW,
    Reading<double> PowerPercent,
    Reading<double> PowerLimitW,
    Reading<long> ThrottleFlags,
    Reading<double> FanRpm);

/// <summary>One process's use of one GPU.</summary>
/// <param name="ProcessId">Process ID.</param>
/// <param name="AdapterKey">The adapter.</param>
/// <param name="UtilizationPercent">The process's busiest engine on this adapter.</param>
/// <param name="Engine">That engine's type, e.g. "3D", "VideoDecode".</param>
/// <param name="DedicatedMemoryBytes">Dedicated memory the process holds on this adapter.</param>
public sealed record GpuProcessUsage(int ProcessId, string AdapterKey, double UtilizationPercent, string? Engine, Reading<long> DedicatedMemoryBytes);

/// <summary>Metrics for all physical disks and mounted volumes for one tick.</summary>
/// <param name="Volumes">Volumes with capacity and free space (refreshed on the inventory tier; carried forward in between).</param>
public sealed record DiskSample(DateTimeOffset Timestamp, IReadOnlyList<PhysicalDiskSample> Disks, IReadOnlyList<VolumeSample> Volumes);

/// <summary>Kind of storage medium.</summary>
public enum StorageMediaKind
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>Solid-state (no seek penalty).</summary>
    Ssd = 1,

    /// <summary>Rotational (seek penalty).</summary>
    Hdd = 2,
}

/// <summary>Identity and static facts of a physical disk.</summary>
/// <param name="DiskKey">Stable key: a hash of model and serial number (the raw serial is not stored).</param>
/// <param name="Number">Windows disk number (PhysicalDriveN); can change when disks are added or removed.</param>
/// <param name="Model">Vendor and product id.</param>
/// <param name="BusType">Bus, e.g. NVMe, SATA, USB, SAS, RAID, Virtual.</param>
/// <param name="Health">Windows storage health (Healthy, Warning, Unhealthy, Unknown).</param>
public sealed record DiskDescriptor(
    string DiskKey,
    int Number,
    string Model,
    string BusType,
    StorageMediaKind Media,
    long SizeBytes,
    bool IsRemovable,
    string Health);

/// <summary>Metrics for one physical disk.</summary>
/// <param name="ActivePercent">100 − "% Idle Time", the Task Manager "active time".</param>
public sealed record PhysicalDiskSample(
    DiskDescriptor Disk,
    IReadOnlyList<string> Volumes,
    Reading<double> ReadBytesPerSec,
    Reading<double> WriteBytesPerSec,
    Reading<double> ReadIops,
    Reading<double> WriteIops,
    Reading<double> ReadLatencyMs,
    Reading<double> WriteLatencyMs,
    Reading<double> QueueLength,
    Reading<double> ActivePercent,
    Reading<double> TemperatureC);

/// <summary>One mounted volume.</summary>
/// <param name="Mount">Mount point, e.g. "C:\".</param>
/// <param name="DiskKey">The physical disk, when known.</param>
/// <param name="DriveType">Fixed, Removable, Network, CDRom, Ram.</param>
public sealed record VolumeSample(
    string Mount,
    string? DiskKey,
    string? Label,
    string? FileSystem,
    string DriveType,
    Reading<long> TotalBytes,
    Reading<long> FreeBytes);

/// <summary>Metrics for all network interfaces for one tick.</summary>
public sealed record NetworkSample(DateTimeOffset Timestamp, IReadOnlyList<NetworkInterfaceSample> Interfaces);

/// <summary>Identity of a network interface.</summary>
/// <param name="InterfaceKey">Interface GUID (stable across reboots).</param>
/// <param name="Name">Connection name, e.g. "Ethernet 3" (generic when network details are disabled).</param>
/// <param name="Description">Adapter description (empty when network details are disabled).</param>
/// <param name="WifiNetworkName">SSID, only when the user opted in to Wi-Fi network names.</param>
public sealed record NetworkInterfaceDescriptor(
    string InterfaceKey,
    string Name,
    string Description,
    NetworkInterfaceKind Kind,
    string? WifiNetworkName);

/// <summary>Metrics for one network interface. Error and discard counts are deltas since the previous tick.</summary>
/// <param name="UtilizationPercent">Busier direction relative to link speed.</param>
/// <param name="WifiSignalQualityPercent">0–100 %, derived from RSSI (−100 dBm = 0 %, −50 dBm = 100 %).</param>
public sealed record NetworkInterfaceSample(
    NetworkInterfaceDescriptor Interface,
    Reading<double> ReceiveBytesPerSec,
    Reading<double> SendBytesPerSec,
    Reading<double> ReceivePacketsPerSec,
    Reading<double> SendPacketsPerSec,
    Reading<long> ReceiveErrors,
    Reading<long> SendErrors,
    Reading<long> ReceiveDiscards,
    Reading<long> SendDiscards,
    Reading<long> LinkSpeedBitsPerSec,
    Reading<double> UtilizationPercent,
    bool IsUp,
    Reading<double> WifiSignalQualityPercent,
    Reading<double> WifiRssiDbm);

/// <summary>Kind of network interface.</summary>
public enum NetworkInterfaceKind
{
    /// <summary>Unknown or other.</summary>
    Other = 0,

    /// <summary>Wired Ethernet.</summary>
    Ethernet = 1,

    /// <summary>Wi-Fi.</summary>
    Wifi = 2,

    /// <summary>VPN or tunnel adapter.</summary>
    Vpn = 3,

    /// <summary>Virtual adapter (Hyper-V, WSL, VirtualBox and similar).</summary>
    Virtual = 4,

    /// <summary>Cellular / mobile broadband.</summary>
    Cellular = 5,

    /// <summary>Bluetooth personal area network.</summary>
    Bluetooth = 6,
}

/// <summary>Per-process metrics for one tick, plus lifecycle changes since the previous process tick.</summary>
/// <param name="Processes">Every running process (the storage writer persists only the top consumers).</param>
/// <param name="Identities">New processes, and processes whose details (path, user...) became known this tick.</param>
/// <param name="Events">Started, stopped, hung and recovered transitions detected this tick.</param>
public sealed record ProcessSample(
    DateTimeOffset Timestamp,
    IReadOnlyList<ProcessMetrics> Processes,
    IReadOnlyList<ProcessIdentityDetails> Identities,
    IReadOnlyList<ProcessLifecycleEvent> Events);

/// <summary>
/// Metrics for one process. GPU use per process is in <see cref="GpuSample.Processes"/> (collected every second by the
/// GPU collector) and is joined when stored.
/// </summary>
/// <param name="CpuPercent">Share of total machine CPU capacity since the previous process tick (Task Manager style).</param>
/// <param name="WorkingSetBytes">Working set.</param>
/// <param name="PrivateBytes">Private working set (Task Manager "Memory").</param>
/// <param name="CommitBytes">Private commit charge (Task Manager "Commit size").</param>
/// <param name="IoReadBytesPerSec">All read I/O (files, devices and network), not disk-only.</param>
public sealed record ProcessMetrics(
    ProcessKey Key,
    string Name,
    Reading<double> CpuPercent,
    Reading<long> CpuTimeMs,
    Reading<long> WorkingSetBytes,
    Reading<long> PrivateBytes,
    Reading<long> CommitBytes,
    Reading<double> IoReadBytesPerSec,
    Reading<double> IoWriteBytesPerSec,
    Reading<int> ThreadCount,
    Reading<int> HandleCount,
    Reading<int> BasePriority,
    ProcessState State,
    ProcessTrueIo? TrueIo = null);

/// <summary>
/// True per-process disk and network transfer rates from kernel events (Enhanced mode); without it only the
/// process's total I/O (files, devices and network together) is known.
/// </summary>
public sealed record ProcessTrueIo(double DiskReadBytesPerSec, double DiskWriteBytesPerSec, double NetReceiveBytesPerSec, double NetSendBytesPerSec)
{
    /// <summary>No transfers in the period.</summary>
    public static readonly ProcessTrueIo None = new(0, 0, 0, 0);
}

/// <summary>Identity details of a process instance.</summary>
/// <param name="Path">Executable path, when the process can be queried.</param>
/// <param name="CommandLine">Only when Privacy:CaptureCommandLines is enabled (known secret patterns redacted).</param>
/// <param name="User">Only when Privacy:CaptureUserNames is enabled.</param>
/// <param name="Signer">Who signed the program file (for catalog-signed Windows files, the catalog's signer).</param>
/// <param name="Signature">The program file's signature; null until it has been checked.</param>
public sealed record ProcessIdentityDetails(
    ProcessKey Key,
    int ParentPid,
    string Name,
    DateTimeOffset StartedAt,
    int SessionId,
    string? Path,
    string? CommandLine,
    string? User,
    string? Signer = null,
    SignatureStatus? Signature = null);

/// <summary>A program file's Authenticode signature (stored as ProcessIdentity.sig_status).</summary>
public enum SignatureStatus
{
    /// <summary>Could not be checked (file unreadable, too large, or gone).</summary>
    Unknown = 0,

    /// <summary>Signed in the file itself, and the signature is valid and trusted.</summary>
    Embedded = 1,

    /// <summary>Listed in a signed Windows catalog (how most of Windows is signed).</summary>
    Catalog = 2,

    /// <summary>No signature.</summary>
    NotSigned = 3,

    /// <summary>Signed, but the signature is not valid or not trusted (e.g. the file was changed after signing).</summary>
    NotTrusted = 4,

    /// <summary>Part of an installed app package (Microsoft Store, MSIX): the package is signed and Windows checks it.</summary>
    Package = 5,
}

/// <summary>A process lifecycle transition.</summary>
public sealed record ProcessLifecycleEvent(DateTimeOffset Timestamp, ProcessKey Key, string Name, Events.ProcessEventKind Kind, string? Detail = null);

/// <summary>Observed state of a process.</summary>
public enum ProcessState
{
    /// <summary>Running normally.</summary>
    Running = 0,

    /// <summary>A top-level window stopped responding.</summary>
    NotResponding = 1,

    /// <summary>All threads suspended.</summary>
    Suspended = 2,

    /// <summary>State could not be determined.</summary>
    Unknown = 3,
}

/// <summary>Well-known logical sensor keys shared across providers.</summary>
public static class SensorKeys
{
    /// <summary>CPU package temperature (Intel "CPU Package", AMD "Tctl/Tdie").</summary>
    public const string CpuPackageTemperature = "cpu/package/temperature";

    /// <summary>CPU package power.</summary>
    public const string CpuPackagePower = "cpu/package/power";

    /// <summary>GPU hotspot temperature for an adapter, by adapter name.</summary>
    public static string GpuHotspot(string adapterName) => $"gpu/{adapterName}/temperature/hotspot";

    /// <summary>
    /// GPU power for an adapter, by adapter name: fills in when the graphics driver reports none (Intel integrated
    /// graphics, whose power the sensor provider reads from the processor's energy counters).
    /// </summary>
    public static string GpuPower(string adapterName) => $"gpu/{adapterName}/power";

    /// <summary>Disk temperature by Windows disk number.</summary>
    public static string DiskTemperature(int diskNumber) => $"disk/{diskNumber}/temperature";

    /// <summary>
    /// Prefers a collector's own reading; when it has no value, uses the sensor provider's reading for the logical key
    /// (a value, or at least the more specific reason such as DriverMissing).
    /// </summary>
    public static Reading<double> Prefer(Reading<double> own, IReadOnlyDictionary<string, SensorReading>? sensors, string key) =>
        own.HasValue || sensors is null || !sensors.TryGetValue(key, out var sensor) ? own : sensor.Value;
}

/// <summary>Hardware sensor readings for one tick, merged across providers.</summary>
public sealed record SensorSample(DateTimeOffset Timestamp, IReadOnlyList<SensorReading> Readings);

/// <summary>One sensor reading.</summary>
public sealed record SensorReading(SensorDescriptor Sensor, Reading<double> Value);

/// <summary>Describes a hardware sensor independently of the provider library.</summary>
/// <param name="LogicalKey">Provider-independent key used to merge providers, e.g. "gpu/0/temperature/core".</param>
/// <param name="AliasOf">
/// Set when this reading repeats another reading under a shared logical key (e.g. "cpu/package/temperature");
/// aliases feed the CPU/GPU/disk tables but are not stored as separate sensor series.
/// </param>
public sealed record SensorDescriptor(
    string ProviderId,
    string HardwareName,
    SensorKind Kind,
    string Name,
    string Unit,
    string LogicalKey,
    string? AliasOf = null);

/// <summary>Kind of hardware sensor.</summary>
public enum SensorKind
{
    /// <summary>Temperature in °C.</summary>
    Temperature,

    /// <summary>Clock in MHz.</summary>
    Clock,

    /// <summary>Power in W.</summary>
    Power,

    /// <summary>Fan speed in RPM.</summary>
    Fan,

    /// <summary>Voltage in V.</summary>
    Voltage,

    /// <summary>Load in %.</summary>
    Load,

    /// <summary>Any other sensor.</summary>
    Other,
}

/// <summary>A snapshot of readings from one hardware sensor provider.</summary>
public sealed record HardwareSnapshot(DateTimeOffset Timestamp, string ProviderId, IReadOnlyList<SensorReading> Readings);

/// <summary>Everything collected in one scheduler tick. Collectors that did not run in this tick leave their slot null.</summary>
/// <param name="Events">Classified events that arrived since the previous tick (each carries its own timestamp); null when none.</param>
/// <param name="Display">Display changes since the previous tick; null when nothing changed.</param>
/// <param name="Recorded">Stored (a recording); otherwise kept in memory only.</param>
/// <param name="Watched">
/// Not recorded but watched: the problem rules run on it and the recent minutes are kept in memory, so a problem found
/// while the recorder is not recording starts a recording and its incident keeps the minutes before it.
/// </param>
public sealed record SampleFrame(
    long Sequence,
    DateTimeOffset Timestamp,
    CpuSample? Cpu = null,
    MemorySample? Memory = null,
    GpuSample? Gpu = null,
    DiskSample? Disk = null,
    NetworkSample? Network = null,
    ProcessSample? Processes = null,
    SensorSample? Sensors = null,
    TickDiagnostics? Diagnostics = null,
    ServiceSample? Services = null,
    InventorySample? Inventory = null,
    IReadOnlyList<Events.SystemEvent>? Events = null,
    DisplaySample? Display = null,
    bool Recorded = true,
    bool Watched = false);

/// <summary>Display changes since the previous tick, and the configuration when it changed.</summary>
/// <param name="Snapshot">The configuration after the changes; null when it did not change.</param>
/// <param name="Changes">Changes, each with the time Windows reported it.</param>
public sealed record DisplaySample(DateTimeOffset Timestamp, Snapshots.DisplaySnapshot? Snapshot, IReadOnlyList<DisplayChange> Changes);

/// <summary>One display change.</summary>
/// <param name="Timestamp">When it happened (notification time, or when the change was detected).</param>
/// <param name="Kind">What changed.</param>
/// <param name="Target">The display, by friendly name (or the monitor's PnP ID when it has none).</param>
/// <param name="OldValue">Previous value, e.g. "144 Hz".</param>
/// <param name="NewValue">New value, e.g. "60 Hz".</param>
/// <param name="TargetKey">The display's stable key (see <see cref="Snapshots.DisplayTarget.TargetKey"/>).</param>
public sealed record DisplayChange(DateTimeOffset Timestamp, Events.DisplayEventKind Kind, string? Target, string? OldValue, string? NewValue, string? TargetKey = null);

/// <summary>
/// Hardware, system and driver inventory, collected at startup and every few hours. It is only present in a frame
/// when a new inventory has been read.
/// </summary>
/// <param name="System">Key facts, e.g. "os.version", "bios.version", "board.product" (no serial numbers).</param>
/// <param name="HardwareJson">Structured hardware description (CPU, memory modules, board, BIOS, GPUs, disks).</param>
/// <param name="Drivers">Installed device drivers for the relevant device classes.</param>
/// <param name="IoDevices">The device register: I/O devices connected now or before, and devices with a problem (null: not read).</param>
public sealed record InventorySample(DateTimeOffset Timestamp, IReadOnlyDictionary<string, string> System, string HardwareJson, IReadOnlyList<DriverInfo> Drivers, IReadOnlyList<Devices.IoDevice>? IoDevices = null);

/// <summary>One device driver.</summary>
public sealed record DriverInfo(string DeviceClass, string DeviceName, string? HardwareId, string? Provider, string? Version, string? DriverDate, string? InfName, string? Signer);

/// <summary>Windows services: a full snapshot at start and periodically, and changes detected between polls.</summary>
/// <param name="Snapshot">All services, present on the first poll and on each reconcile; null otherwise.</param>
/// <param name="Changes">Services that started, stopped, restarted, changed start type, or were installed or removed.</param>
public sealed record ServiceSample(DateTimeOffset Timestamp, IReadOnlyList<Snapshots.ServiceInfo>? Snapshot, IReadOnlyList<ServiceChange> Changes);

/// <summary>A detected service change.</summary>
public sealed record ServiceChange(DateTimeOffset Timestamp, string Name, string DisplayName, Events.ServiceEventKind Kind, string? OldValue, string? NewValue, int? ProcessId = null);

/// <summary>How healthy sampling was in one tick, including the recorder's own overhead.</summary>
/// <param name="TickDuration">Time spent collecting this tick.</param>
/// <param name="CollectorsRun">Collectors that ran in this tick.</param>
/// <param name="CollectorsMissed">Collectors that were due but failed, timed out or were still busy.</param>
/// <param name="AgentCpuPercent">The Agent's own CPU use, as a share of total machine capacity (Task Manager style).</param>
/// <param name="AgentWorkingSetBytes">The Agent's own working set.</param>
public sealed record TickDiagnostics(
    TimeSpan TickDuration,
    int CollectorsRun,
    int CollectorsMissed,
    Reading<double> AgentCpuPercent,
    long AgentWorkingSetBytes);
