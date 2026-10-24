using System.Text.Json;
using PcDiag.Core.Incidents;

namespace PcDiag.Core.History;

/// <summary>A series that can be charted.</summary>
/// <param name="Id">Stable id used in URLs, e.g. "cpu", "gpu.3.temp".</param>
/// <param name="Label">Display name, e.g. "NVIDIA GeForce RTX 4090 temperature".</param>
/// <param name="Unit">"%", "°C", "W", "MHz", "B", "B/s", "ms", "RPM", "/s" or "".</param>
/// <param name="Group">Grouping in pickers: System, CPU, Memory, GPU, Disk, Network, Recorder, "Temperature sensors"….</param>
/// <param name="Device">The device the series belongs to, if any.</param>
/// <param name="LongTerm">Whether one-minute summaries exist beyond the raw retention.</param>
/// <param name="Key">
/// What the series stays attached to across databases: the device's stable key (the adapter key the live feed also
/// reports, a disk hash, an interface GUID) or a hardware sensor's logical key. Null for whole-PC series.
/// </param>
/// <param name="LastSeen">When the device was last seen (Unix ms), for device series.</param>
public sealed record SeriesInfo(string Id, string Label, string Unit, string Group, string? Device, bool LongTerm, string? Key = null, long? LastSeen = null);

/// <summary>One series over a range, in buckets.</summary>
/// <param name="Id">Series id.</param>
/// <param name="Points">Buckets as [time (Unix ms), min, average, max], oldest first. Buckets without data are omitted.</param>
public sealed record SeriesData(string Id, IReadOnlyList<double[]> Points);

/// <summary>A chart query result.</summary>
/// <param name="From">Range start (Unix ms).</param>
/// <param name="To">Range end (Unix ms).</param>
/// <param name="BucketMs">Bucket width.</param>
/// <param name="Series">Requested series that exist (unknown ids are left out).</param>
public sealed record HistoryResult(long From, long To, long BucketMs, IReadOnlyList<SeriesData> Series);

/// <summary>Something that happened: a Windows event, a display, process or service change, recording start/stop.</summary>
/// <param name="Ts">When (Unix ms).</param>
/// <param name="Kind">windows-event | process | service | display | recorder | trigger | user | anomaly.</param>
/// <param name="Title">What happened.</param>
/// <param name="Importance">0 (context) to 4 (critical).</param>
/// <param name="Detail">Extra fields, when any.</param>
/// <param name="SourceId">Row id in the source table.</param>
public sealed record TimelineEntry(long Ts, string Kind, string Title, int Importance, JsonElement? Detail, long? SourceId);

/// <summary>
/// One process at a moment. Disk and network rates (read and write together) are known only when Enhanced mode
/// measured them. <paramref name="Signature"/> is a <see cref="Telemetry.SignatureStatus"/> name, null until the
/// program file was checked. Outside incidents only the busiest processes have their figures stored
/// (<paramref name="Measured"/>); the others are known to have been running, from their start and end times.
/// <paramref name="FiguresTs"/> is when the figures were measured, when that is not the moment's own sample: every
/// process's figures are also stored every 30 s when they changed (and every 10 minutes when not).
/// </summary>
public sealed record ProcessAtTime(
    long ProcKey,
    int Pid,
    string Name,
    string? Path,
    double? CpuPercent,
    long? WorkingSetBytes,
    long? PrivateBytes,
    double? IoReadBytesPerSec,
    double? IoWriteBytesPerSec,
    double? GpuPercent,
    long? GpuMemoryBytes,
    int? Threads,
    int? Handles,
    string State,
    double? DiskBytesPerSec = null,
    double? NetBytesPerSec = null,
    string? Signer = null,
    string? Signature = null,
    long? StartTs = null,
    bool Measured = true,
    long? FiguresTs = null);

/// <summary>Processes at the sample nearest to a moment.</summary>
/// <param name="SampleTs">The sample used (Unix ms); null when the incident has none.</param>
/// <param name="Processes">Busiest first.</param>
public sealed record ProcessesAtTime(long? SampleTs, IReadOnlyList<ProcessAtTime> Processes);

/// <summary>Everything recorded about one moment of the rolling history, incident or not.</summary>
/// <param name="At">The moment asked for (Unix ms).</param>
/// <param name="Recorded">Whether the recorder was recording then; nothing else is known otherwise.</param>
/// <param name="Values">Every series' value around the moment (catalog series, then hardware sensors).</param>
/// <param name="ProcessSampleTs">The process sample used for the figures (Unix ms), null when none is near.</param>
/// <param name="Processes">Processes running at the moment: measured ones busiest first, then the others by name.</param>
/// <param name="Services">Windows services then, with the process each ran in.</param>
public sealed record HistoryMoment(
    long At, bool Recorded, IReadOnlyList<MomentValue> Values, long? ProcessSampleTs, IReadOnlyList<ProcessAtTime> Processes, IReadOnlyList<MomentService>? Services = null);

/// <summary>A Windows service at a moment.</summary>
/// <param name="Name">Service name.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Status">Running, Stopped... (a <see cref="Snapshots.ServiceStatus"/> name).</param>
/// <param name="StartType">Automatic, Manual... (a <see cref="Snapshots.ServiceStartType"/> name).</param>
/// <param name="Pid">The process it ran in, when running and known.</param>
/// <param name="ProcKey">That process's key in the moment's process list, when it is there.</param>
public sealed record MomentService(string Name, string DisplayName, string Status, string StartType, int? Pid, long? ProcKey);

/// <summary>A series' value around a moment (the average of the samples within <see cref="Window"/> of it).</summary>
/// <param name="Id">Series id (catalog id, or sensor.{id} for a hardware sensor).</param>
/// <param name="Label">What it is.</param>
/// <param name="Unit">Unit.</param>
/// <param name="Group">Group for display.</param>
/// <param name="Value">The value.</param>
public sealed record MomentValue(string Id, string Label, string Unit, string Group, double Value)
{
    /// <summary>How far from the moment samples count (one-minute summaries beyond the raw data).</summary>
    public const long Window = 1500;
}

/// <summary>A diagnostic statement with its evidence level.</summary>
public sealed record FindingEntry(int Ordinal, string Level, string Text, JsonElement? Evidence);

/// <summary>A trigger merged into an incident.</summary>
public sealed record TriggerEntry(long Ts, IncidentSource Source, UserIncidentType? UserType, string? RuleId, string? Detail, string? Note);

/// <summary>Everything about an incident except the bulk data.</summary>
/// <param name="Summary">The index row.</param>
/// <param name="From">Window start (Unix ms).</param>
/// <param name="To">Window end (Unix ms).</param>
/// <param name="Triggers">What created it and what was merged into it.</param>
/// <param name="Info">The incident folder's own metadata (IncidentInfo).</param>
/// <param name="HasData">Whether the incident folder and database exist.</param>
public sealed record IncidentDetail(IncidentSummary Summary, long From, long To, IReadOnlyList<TriggerEntry> Triggers, IReadOnlyDictionary<string, string?> Info, bool HasData);

/// <summary>Filters for the incident list.</summary>
public sealed record IncidentQuery(
    int Limit = 50,
    int Offset = 0,
    UserIncidentType? Type = null,
    IncidentSource? Source = null,
    long? From = null,
    long? To = null,
    string? Text = null);

/// <summary>A device seen by the recorder.</summary>
public sealed record DeviceInfo(long Id, string Kind, string? Name, string? Vendor, JsonElement? Details, long FirstSeen, long LastSeen, string? Key = null);

/// <summary>An installed driver (latest inventory).</summary>
public sealed record InstalledDriver(string? DeviceClass, string? DeviceName, string? HardwareId, string? Provider, string? Version, string? Date, string? Inf, string? Signer);

/// <summary>Whether a metric can be read, and why not.</summary>
public sealed record AvailabilityInfo(string Series, string Availability, string? Reason, long Since);

/// <summary>A hardware sensor and its latest value.</summary>
public sealed record SensorInfo(string Provider, string Kind, string Name, string Unit, string Key, double? Value);

/// <summary>The System page: inventory, drivers, displays and sensor availability.</summary>
public sealed record SystemOverview(
    IReadOnlyDictionary<string, string?> Information,
    IReadOnlyList<DeviceInfo> Devices,
    long? DriversTs,
    IReadOnlyList<InstalledDriver> Drivers,
    long? DisplayTs,
    JsonElement? Display,
    long? HardwareTs,
    JsonElement? Hardware,
    IReadOnlyList<AvailabilityInfo> Availability,
    IReadOnlyList<SensorInfo> Sensors,
    IReadOnlyList<RegisteredDevice>? IoDevices = null);

/// <summary>A device in the register, with when the recorder first listed it and last found it connected.</summary>
/// <param name="Device">The device as the newest inventory described it.</param>
/// <param name="FirstSeen">The first inventory that listed it (Unix ms).</param>
/// <param name="LastSeen">The newest inventory that found it connected, or noticed it gone (Unix ms).</param>
public sealed record RegisteredDevice(Devices.IoDevice Device, long FirstSeen, long LastSeen);

/// <summary>A device connected, disconnected, dropping out or failing (the recorder's own events, channel pcdiag.devices).</summary>
/// <param name="Ts">When (Unix ms).</param>
/// <param name="EventId">See <see cref="Devices.DeviceEvents.Id"/>.</param>
/// <param name="Title">Short title.</param>
/// <param name="Message">What happened and what to do.</param>
/// <param name="Severity">Diagnostic severity (0 info … 4 critical).</param>
/// <param name="Device">The device's name.</param>
/// <param name="Data">Instance ID, bus, vendor and product IDs, problem code.</param>
public sealed record DeviceEventInfo(long Ts, int EventId, string Title, string? Message, int Severity, string? Device, JsonElement? Data);

/// <summary>The recorder's own cost over a range (from its self-monitoring in every sample).</summary>
/// <param name="From">Range start (Unix ms).</param>
/// <param name="To">Range end (Unix ms).</param>
/// <param name="Samples">Samples recorded in the range.</param>
/// <param name="CpuAvgPercent">Average CPU, as a share of the whole machine.</param>
/// <param name="CpuP95Percent">95th percentile CPU.</param>
/// <param name="CpuMaxPercent">Highest CPU.</param>
/// <param name="WorkingSetAvgBytes">Average working set.</param>
/// <param name="WorkingSetMaxBytes">Highest working set.</param>
/// <param name="TickAvgMs">Average time to collect one sample.</param>
/// <param name="TickMaxMs">Longest time to collect one sample.</param>
/// <param name="SamplesWithMissedCollectors">Samples in which a collector was late or failed.</param>
public sealed record RecorderOverhead(
    long From,
    long To,
    int Samples,
    double? CpuAvgPercent,
    double? CpuP95Percent,
    double? CpuMaxPercent,
    double? WorkingSetAvgBytes,
    double? WorkingSetMaxBytes,
    double? TickAvgMs,
    double? TickMaxMs,
    int SamplesWithMissedCollectors);
