using PcDiag.Core.Events;
using PcDiag.Core.Recording;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Incidents;

/// <summary>What is known when an incident is created; the store assigns its ID and key.</summary>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="CenterAt">The incident time (the trigger, or the last heartbeat for boot recovery).</param>
/// <param name="PreWindow">Evidence kept before <paramref name="CenterAt"/> (zero for snapshot-only incidents).</param>
/// <param name="PostWindow">Evidence kept after <paramref name="CenterAt"/>.</param>
/// <param name="Source">What created it.</param>
/// <param name="UserType">The quick type the user picked, if any.</param>
/// <param name="Title">Title.</param>
/// <param name="Notes">User notes.</param>
/// <param name="Severity">Severity.</param>
/// <param name="SnapshotOnly">Captured while the recorder was not recording: there is no pre-window.</param>
/// <param name="Trigger">The first trigger.</param>
/// <param name="AppVersion">Agent version.</param>
public sealed record IncidentDraft(
    DateTimeOffset CreatedAt,
    DateTimeOffset CenterAt,
    TimeSpan PreWindow,
    TimeSpan PostWindow,
    IncidentSource Source,
    UserIncidentType? UserType,
    string Title,
    string? Notes,
    Severity Severity,
    bool SnapshotOnly,
    IncidentTrigger Trigger,
    string AppVersion,
    Recording.RecordingSelection? Selection = null);

/// <summary>A boot-recovery finding that has not been turned into an incident yet.</summary>
public sealed record PendingBootRecovery(long Id, BootRecovery Recovery);

/// <summary>
/// Persists incidents: the index row in incidents.db and the self-contained incident folder
/// (<c>incidents\&lt;key&gt;\incident.db</c>, the telemetry schema plus timeline and findings).
/// </summary>
public interface IIncidentStore
{
    /// <summary>Allocates the next key, writes the index row and first trigger, and creates the folder and incident.db.</summary>
    Task<Incident> CreateAsync(IncidentDraft draft, CancellationToken cancellationToken);

    /// <summary>Adds a trigger to an incident (a merged capture).</summary>
    Task AddTriggerAsync(long incidentId, IncidentTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the recorded telemetry for [<paramref name="windowStart"/>, <paramref name="windowEnd"/>] from telemetry.db, and the
    /// full process lists from <paramref name="frames"/> (the in-memory ring keeps every process; telemetry.db only the top ones).
    /// </summary>
    Task CaptureWindowAsync(Incident incident, DateTimeOffset windowStart, DateTimeOffset windowEnd, IReadOnlyList<SampleFrame> frames, CancellationToken cancellationToken);

    /// <summary>Stores full snapshots taken at capture time.</summary>
    Task SaveSnapshotsAsync(Incident incident, DisplaySnapshot? display, ServiceSnapshot? services, CancellationToken cancellationToken);

    /// <summary>Builds the timeline and headline numbers and sets the final state.</summary>
    Task FinalizeAsync(Incident incident, IncidentState state, BootRecovery? recovery, CancellationToken cancellationToken);

    /// <summary>Incidents left in Recording or Finalizing (the Agent stopped before they were finished).</summary>
    Task<IReadOnlyList<Incident>> ListUnfinishedAsync(CancellationToken cancellationToken);

    /// <summary>Boot recoveries not yet handled.</summary>
    Task<IReadOnlyList<PendingBootRecovery>> PendingBootRecoveriesAsync(CancellationToken cancellationToken);

    /// <summary>Marks a boot recovery as handled, with the incident created for it (if any).</summary>
    Task MarkBootRecoveryAsync(long recoveryId, long? incidentId, CancellationToken cancellationToken);

    /// <summary>Changes what the user can edit (title, notes, type, pinned). False when the incident does not exist.</summary>
    Task<bool> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes the incident: its index row and its folder. False when it does not exist.</summary>
    Task<bool> DeleteAsync(IncidentKey key, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the incident's diagnostic findings and records the analyzer version that produced them. False when
    /// the incident or its database does not exist.
    /// </summary>
    Task<bool> SaveFindingsAsync(IncidentKey key, IReadOnlyList<Diagnostics.Finding> findings, int analyzerVersion, CancellationToken cancellationToken);

    /// <summary>Finished incidents analyzed by an older analyzer version (or never), newest first.</summary>
    Task<IReadOnlyList<IncidentKey>> ListUnanalyzedAsync(int analyzerVersion, int limit, CancellationToken cancellationToken);
}

/// <summary>User edits to an incident; null leaves a field unchanged (an empty string clears the notes).</summary>
/// <param name="Title">New title (at most 120 characters).</param>
/// <param name="Notes">New notes (at most 2000 characters).</param>
/// <param name="UserType">What the user says it was.</param>
/// <param name="Pinned">Kept regardless of the incident limit.</param>
public sealed record IncidentUpdate(string? Title = null, string? Notes = null, UserIncidentType? UserType = null, bool? Pinned = null);

/// <summary>Result of an incident edit.</summary>
public enum IncidentEditResult
{
    /// <summary>Done.</summary>
    Done = 0,

    /// <summary>No such incident.</summary>
    NotFound = 1,

    /// <summary>The incident is still recording; it cannot be deleted yet.</summary>
    StillRecording = 2,
}

/// <summary>Creates incidents and manages their lifecycle.</summary>
public interface IIncidentCapture
{
    /// <summary>The incident whose post-window is still being recorded, if any.</summary>
    Incident? Active { get; }

    /// <summary>When the active incident's post-window ends.</summary>
    DateTimeOffset? ActiveUntil { get; }

    /// <summary>Raised after an incident was created or a trigger was merged into the active one.</summary>
    event EventHandler<IncidentCapturedEventArgs>? Captured;

    /// <summary>Raised when an incident is complete (or partial).</summary>
    event EventHandler<IncidentCapturedEventArgs>? Finished;

    /// <summary>
    /// Captures an incident for <paramref name="trigger"/>, or merges it into the incident still recording its
    /// post-window. The pre-window is saved before this method returns.
    /// </summary>
    Task<CaptureOutcome> CaptureAsync(IncidentTrigger trigger, CancellationToken cancellationToken);

    /// <summary>Applies user edits to an incident.</summary>
    Task<IncidentEditResult> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes an incident (not while it is recording).</summary>
    Task<IncidentEditResult> DeleteAsync(IncidentKey key, CancellationToken cancellationToken);
}

/// <summary>The result of a capture request.</summary>
/// <param name="Incident">The incident (new or the one merged into).</param>
/// <param name="Merged">The trigger was added to an incident that was still recording.</param>
/// <param name="SnapshotOnly">The recorder was not recording, so there is no pre-window.</param>
/// <param name="RecordingUntil">When the post-window ends.</param>
public sealed record CaptureOutcome(Incident Incident, bool Merged, bool SnapshotOnly, DateTimeOffset RecordingUntil);

/// <summary>Event data for incident notifications.</summary>
public sealed class IncidentCapturedEventArgs(CaptureOutcome outcome) : EventArgs
{
    /// <summary>The outcome.</summary>
    public CaptureOutcome Outcome { get; } = outcome;
}
