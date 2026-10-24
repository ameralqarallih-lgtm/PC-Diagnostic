using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Abstractions;

/// <summary>
/// Receives every sample frame from the sample bus (storage writer, ring buffer, anomaly engine, live feed).
/// Each consumer gets its own bounded queue, so a slow consumer never blocks sampling or the other consumers.
/// </summary>
public interface ISampleConsumer
{
    /// <summary>Name used in logs and diagnostics.</summary>
    string Name { get; }

    /// <summary>Queue capacity in frames. When full, the oldest queued frame is dropped and counted.</summary>
    int QueueCapacity { get; }

    /// <summary>Handles one frame. Called sequentially for a given consumer.</summary>
    ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken);

    /// <summary>Persists or releases anything buffered. Called when recording stops and at shutdown.</summary>
    ValueTask FlushAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether this consumer gets frames whole during a custom recording (the live view). Every other consumer (storage,
    /// the ring buffer, the anomaly rules) gets only the selected data.
    /// </summary>
    bool SeesUnrecordedData => false;

    /// <summary>
    /// Whether this consumer stores the watched minutes kept in memory when a problem found while not recording makes
    /// them part of an incident (<c>SampleBus.PublishBackfillAsync</c>): the telemetry store.
    /// </summary>
    bool StoresBackfill => false;
}

/// <summary>Records the periods during which the recorder was recording.</summary>
public interface IRecorderSessionStore
{
    /// <summary>Records the start of a recording session and returns its ID.</summary>
    /// <param name="selection">What a custom recording stores; null records everything.</param>
    Task<long> StartSessionAsync(DateTimeOffset startedAt, RecordingStartReason reason, bool elevated, string appVersion, RecordingSelection? selection, CancellationToken cancellationToken);

    /// <summary>Records the end of a recording session.</summary>
    Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, RecordingEndReason reason, CancellationToken cancellationToken);

    /// <summary>Closes sessions left open by a crash or power loss, ending them at their last recorded sample.</summary>
    Task<int> CloseAbandonedSessionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Accepts classified events from any source (event log collectors, or polled collectors that detect a change,
/// such as a network link going down). Events reach the sample consumers with the next frame. Never blocks.
/// </summary>
public interface ISystemEventSink
{
    /// <summary>Queues an event for the next frame.</summary>
    void Publish(Events.SystemEvent systemEvent);
}

/// <summary>Heartbeat of the Agent process (telemetry.db) and boot-recovery findings (incidents.db).</summary>
public interface IAgentHeartbeatStore
{
    /// <summary>The previous run's last heartbeat; null on first run.</summary>
    Task<Recording.AgentHeartbeatState?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Records that the Agent is alive (and whether it is recording); clears the clean-shutdown flag.</summary>
    Task BeatAsync(DateTimeOffset at, string bootId, bool recording, CancellationToken cancellationToken);

    /// <summary>Records that the Agent is stopping normally.</summary>
    Task MarkCleanShutdownAsync(DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Stores a boot-recovery finding for the incident engine. Returns its ID.</summary>
    Task<long> RecordBootRecoveryAsync(Recording.BootRecovery recovery, CancellationToken cancellationToken);
}

/// <summary>Windows events that explain how a previous run ended (Kernel-Power 41, EventLog 6008, bug check, shutdown requests).</summary>
public interface IBootEvidenceSource
{
    /// <summary>When Windows last booted.</summary>
    DateTimeOffset BootTime { get; }

    /// <summary>Classified shutdown/restart-related events logged since <paramref name="since"/>.</summary>
    Task<IReadOnlyList<Events.SystemEvent>> ReadSinceAsync(DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>Sleep/resume and end-of-session notifications from Windows.</summary>
public interface ISystemPowerEvents
{
    /// <summary>The computer is going to sleep or hibernate.</summary>
    event EventHandler? Suspending;

    /// <summary>The computer woke up.</summary>
    event EventHandler? Resumed;

    /// <summary>
    /// Windows is ending the session (log off, restart, shut down). Handlers run on the notification thread and
    /// should finish within a few seconds; Windows may end the process soon after.
    /// </summary>
    event EventHandler? SessionEnding;
}

/// <summary>Whether the process runs with an elevated (administrator) token.</summary>
/// <param name="IsElevated">True when elevated.</param>
public sealed record ElevationInfo(bool IsElevated);
