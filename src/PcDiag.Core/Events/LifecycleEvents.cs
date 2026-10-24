namespace PcDiag.Core.Events;

/// <summary>Process lifecycle transitions (PROCESS_STARTED, PROCESS_STOPPED, PROCESS_CRASHED, PROCESS_HUNG).</summary>
public enum ProcessEventKind
{
    /// <summary>PROCESS_STARTED.</summary>
    Started = 0,

    /// <summary>PROCESS_STOPPED.</summary>
    Stopped = 1,

    /// <summary>PROCESS_CRASHED.</summary>
    Crashed = 2,

    /// <summary>PROCESS_HUNG.</summary>
    Hung = 3,

    /// <summary>A hung process became responsive again.</summary>
    Recovered = 4,
}

/// <summary>Windows service transitions.</summary>
public enum ServiceEventKind
{
    /// <summary>Service started.</summary>
    Started = 0,

    /// <summary>Service stopped.</summary>
    Stopped = 1,

    /// <summary>Service stopped and started again within the restart window.</summary>
    Restarted = 2,

    /// <summary>Startup type changed.</summary>
    StartTypeChanged = 3,

    /// <summary>Service failed or terminated unexpectedly.</summary>
    Failed = 4,

    /// <summary>A new service was installed.</summary>
    Installed = 5,

    /// <summary>A service was removed.</summary>
    Removed = 6,
}

/// <summary>Display configuration transitions detected by diffing display snapshots.</summary>
public enum DisplayEventKind
{
    /// <summary>A display was connected.</summary>
    Connected = 0,

    /// <summary>A display was disconnected.</summary>
    Disconnected = 1,

    /// <summary>Topology (which displays are active, extend/duplicate, primary) changed.</summary>
    TopologyChanged = 2,

    /// <summary>Resolution changed.</summary>
    ResolutionChanged = 3,

    /// <summary>Refresh rate changed.</summary>
    RefreshRateChanged = 4,

    /// <summary>HDR / advanced color state changed.</summary>
    HdrChanged = 5,

    /// <summary>Orientation changed.</summary>
    OrientationChanged = 6,

    /// <summary>The session's displays were turned off, on or dimmed by power management.</summary>
    PowerStateChanged = 7,

    /// <summary>
    /// Windows reported a display change, but the active configuration was the same afterwards (a mode set, as seen
    /// during display driver recovery or a brief signal loss). An observation, not a cause.
    /// </summary>
    Reconfigured = 8,

    /// <summary>The recorder's session was connected to or disconnected from the console or Remote Desktop.</summary>
    SessionChanged = 9,
}
