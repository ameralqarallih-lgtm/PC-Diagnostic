using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Abstractions;

/// <summary>
/// Base contract for every collector plugin. Collectors read the OS and return typed samples;
/// they hold no history, raise no incidents, and must not throw into the scheduler for an unavailable metric.
/// </summary>
public interface ICollector
{
    /// <summary>Stable identifier, e.g. "cpu", "gpu.pdh", "sensors.lhm".</summary>
    string Id { get; }

    /// <summary>Describes the collector (display name, privilege, cost).</summary>
    CollectorInfo Describe();

    /// <summary>Probes capabilities and prepares OS handles. Called once before the first collection.</summary>
    ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken);
}

/// <summary>A collector the scheduler polls on a fixed tier.</summary>
/// <typeparam name="TSample">The sample type produced each tick.</typeparam>
public interface IPolledCollector<TSample> : ICollector
{
    /// <summary>The sampling tier this collector runs on.</summary>
    SamplingTier Tier { get; }

    /// <summary>Collects one sample.</summary>
    ValueTask<TSample> CollectAsync(SampleContext context, CancellationToken cancellationToken);
}

/// <summary>A push-based collector that produces events as they happen.</summary>
public interface IEventCollector : ICollector
{
    /// <summary>Streams classified events until cancelled.</summary>
    IAsyncEnumerable<SystemEvent> ReadEventsAsync(CancellationToken cancellationToken);
}

/// <summary>CPU collector.</summary>
public interface ICpuCollector : IPolledCollector<CpuSample>;

/// <summary>Memory collector.</summary>
public interface IMemoryCollector : IPolledCollector<MemorySample>;

/// <summary>GPU collector (all adapters in one sample).</summary>
public interface IGpuCollector : IPolledCollector<GpuSample>;

/// <summary>Physical disk collector.</summary>
public interface IDiskCollector : IPolledCollector<DiskSample>;

/// <summary>Network interface collector.</summary>
public interface INetworkCollector : IPolledCollector<NetworkSample>;

/// <summary>Process collector.</summary>
public interface IProcessCollector : IPolledCollector<ProcessSample>;

/// <summary>Hardware sensor collector; merges all registered <see cref="IHardwareSensorProvider"/>s.</summary>
public interface ISensorCollector : IPolledCollector<SensorSample>;

/// <summary>
/// Windows service collector: polled on the inventory tier (one EnumServicesStatusEx call) with change detection;
/// exact timestamps come from Service Control Manager events in the event engine.
/// </summary>
public interface IServiceCollector : IPolledCollector<ServiceSample>
{
    /// <summary>Takes a full snapshot of all services.</summary>
    Task<ServiceSnapshot> SnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Display collector: polled every tick for changes that Windows reported since the previous tick (with their own
/// timestamps), plus full snapshots on demand.
/// </summary>
public interface IDisplayCollector : IPolledCollector<DisplaySample?>
{
    /// <summary>Takes a snapshot of the active display configuration.</summary>
    Task<DisplaySnapshot> SnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A source of hardware sensor readings (LibreHardwareMonitor, storage reliability counters, HWiNFO...).
/// Keeps the application independent of any one monitoring library. Readings that several providers can supply use
/// shared logical keys (e.g. "cpu/package/temperature") so the collector can merge them by priority.
/// </summary>
public interface IHardwareSensorProvider
{
    /// <summary>Provider identifier, e.g. "lhm", "storage".</summary>
    string ProviderId { get; }

    /// <summary>When two providers report the same logical sensor, the higher priority wins.</summary>
    int Priority { get; }

    /// <summary>How often the provider is read; slow sources (WMI, SMART) use longer intervals.</summary>
    TimeSpan RefreshInterval { get; }

    /// <summary>Opens the provider. Returns a status line for the log (e.g. which prerequisites are missing).</summary>
    ValueTask<string> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Reads all sensors this provider exposes.</summary>
    ValueTask<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Sampling tiers. Intervals are configured in <c>SamplingOptions</c>.</summary>
public enum SamplingTier
{
    /// <summary>System metrics (default 1 s).</summary>
    System = 0,

    /// <summary>Hardware sensors (default 1 s).</summary>
    Sensors = 1,

    /// <summary>Process metrics (default 2 s).</summary>
    Process = 2,

    /// <summary>Detailed inventory (default 10 s).</summary>
    Inventory = 3,
}

/// <summary>Privilege a collector needs for full functionality.</summary>
public enum RequiredPrivilege
{
    /// <summary>Works as a standard user.</summary>
    StandardUser = 0,

    /// <summary>Some metrics need elevation; others still work.</summary>
    PartialWithoutElevation = 1,

    /// <summary>Needs elevation for anything useful.</summary>
    Elevated = 2,
}

/// <summary>Rough cost of one collection, used for overhead budgeting.</summary>
public enum CostClass
{
    /// <summary>Microseconds; a single API call.</summary>
    Cheap = 0,

    /// <summary>Around a millisecond.</summary>
    Moderate = 1,

    /// <summary>Several milliseconds or more; avoid on the fast tier.</summary>
    Expensive = 2,
}

/// <summary>Static description of a collector.</summary>
public sealed record CollectorInfo(string Id, string DisplayName, RequiredPrivilege RequiredPrivilege, CostClass Cost);

/// <summary>Result of collector initialization.</summary>
/// <param name="Availability">Overall availability; <see cref="Availability.Available"/> may still include unavailable individual metrics.</param>
/// <param name="Message">Human-readable detail, e.g. why the collector is unavailable.</param>
public sealed record CollectorStatus(Availability Availability, string? Message = null);

/// <summary>Per-tick context passed to collectors.</summary>
/// <param name="Sequence">Monotonic tick number.</param>
/// <param name="Timestamp">The tick's timestamp (shared by every collector in the tick).</param>
/// <param name="Elapsed">Time since the previous tick of the same tier, for rate calculations.</param>
/// <param name="IsElevated">Whether the Agent runs elevated.</param>
public sealed record SampleContext(long Sequence, DateTimeOffset Timestamp, TimeSpan Elapsed, bool IsElevated);
