namespace PcDiag.Core.Recording;

/// <summary>The last heartbeat the previous Agent run wrote.</summary>
/// <param name="LastBeat">When the Agent was last known to be alive.</param>
/// <param name="BootId">Identifies the Windows boot the heartbeat belongs to.</param>
/// <param name="CleanShutdown">The Agent stopped normally (or saw Windows end the session).</param>
/// <param name="Recording">The recorder was recording at the last heartbeat.</param>
public sealed record AgentHeartbeatState(DateTimeOffset LastBeat, string? BootId, bool CleanShutdown, bool Recording);

/// <summary>How the previous Agent run ended when it did not stop cleanly.</summary>
public enum UncleanStopKind
{
    /// <summary>The Agent stopped unexpectedly while Windows kept running (killed, crashed or hung).</summary>
    RecorderStopped = 0,

    /// <summary>Windows restarted or shut down normally, but the Agent was not stopped cleanly first.</summary>
    WindowsRestarted = 1,

    /// <summary>Windows restarted without a clean shutdown (Kernel-Power 41 / EventLog 6008): a hang, a crash without a dump, or a power loss.</summary>
    UnexpectedShutdown = 2,

    /// <summary>Windows restarted after a bug check (blue screen).</summary>
    BugCheck = 3,
}

/// <summary>
/// What the Agent found at startup about the end of its previous run. Stored in incidents.db until the incident
/// engine (Phase 9) turns it into an incident centered on <see cref="LastHeartbeat"/>.
/// </summary>
/// <param name="DetectedAt">When this run found it.</param>
/// <param name="LastHeartbeat">The previous run's last heartbeat.</param>
/// <param name="BootTime">When Windows last booted.</param>
/// <param name="Kind">The conclusion.</param>
/// <param name="WasRecording">Whether the recorder was recording at the last heartbeat.</param>
/// <param name="Summary">One sentence for people, worded as an observation.</param>
/// <param name="Details">Evidence, e.g. the bug check code, dump file and the Windows events used.</param>
public sealed record BootRecovery(
    DateTimeOffset DetectedAt,
    DateTimeOffset LastHeartbeat,
    DateTimeOffset BootTime,
    UncleanStopKind Kind,
    bool WasRecording,
    string Summary,
    IReadOnlyDictionary<string, string> Details);
