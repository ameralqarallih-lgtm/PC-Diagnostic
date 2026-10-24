namespace PcDiag.Core.Snapshots;

/// <summary>All Windows services at one point in time.</summary>
public sealed record ServiceSnapshot(DateTimeOffset Timestamp, IReadOnlyList<ServiceInfo> Services);

/// <summary>One Windows service.</summary>
/// <param name="Name">Service (key) name.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Status">Current status.</param>
/// <param name="StartType">Configured startup type.</param>
/// <param name="ProcessId">Hosting process ID when running, otherwise null.</param>
/// <param name="ServiceType">Raw SERVICE_* type flags.</param>
/// <param name="StartedAt">Start time (from the hosting process), when known.</param>
public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    ServiceStatus Status,
    ServiceStartType StartType,
    int? ProcessId,
    int ServiceType,
    DateTimeOffset? StartedAt);

/// <summary>Service status (SERVICE_* state values).</summary>
public enum ServiceStatus
{
    /// <summary>Unknown.</summary>
    Unknown = 0,

    /// <summary>SERVICE_STOPPED.</summary>
    Stopped = 1,

    /// <summary>SERVICE_START_PENDING.</summary>
    StartPending = 2,

    /// <summary>SERVICE_STOP_PENDING.</summary>
    StopPending = 3,

    /// <summary>SERVICE_RUNNING.</summary>
    Running = 4,

    /// <summary>SERVICE_CONTINUE_PENDING.</summary>
    ContinuePending = 5,

    /// <summary>SERVICE_PAUSE_PENDING.</summary>
    PausePending = 6,

    /// <summary>SERVICE_PAUSED.</summary>
    Paused = 7,
}

/// <summary>Service startup type.</summary>
public enum ServiceStartType
{
    /// <summary>Boot.</summary>
    Boot = 0,

    /// <summary>System.</summary>
    System = 1,

    /// <summary>Automatic.</summary>
    Automatic = 2,

    /// <summary>Manual.</summary>
    Manual = 3,

    /// <summary>Disabled.</summary>
    Disabled = 4,

    /// <summary>Automatic (delayed start).</summary>
    AutomaticDelayed = 5,
}
