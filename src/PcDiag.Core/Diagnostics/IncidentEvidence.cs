using PcDiag.Core.Anomalies;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;

namespace PcDiag.Core.Diagnostics;

/// <summary>
/// Everything the diagnostic analyzers look at for one incident, read from its incident.db: metric tracks at
/// (about) one-second resolution, the timeline, anomalies and triggers.
/// </summary>
/// <param name="Key">The incident.</param>
/// <param name="CenterAt">The incident moment (the user's report or the trigger).</param>
/// <param name="From">Window start.</param>
/// <param name="To">Window end.</param>
/// <param name="Source">What created the incident.</param>
/// <param name="UserType">What the user said happened, if anything.</param>
/// <param name="SnapshotOnly">Captured while not recording: no pre-window.</param>
/// <param name="Metrics">Metric tracks, by series ID.</param>
/// <param name="Events">Timeline events (Windows events, process, service, display and recorder changes), oldest first.</param>
/// <param name="Anomalies">Anomalies that overlap the window.</param>
/// <param name="Triggers">What created or joined the incident.</param>
/// <param name="RecordedItems">
/// What a custom recording selected when the incident was captured (see <see cref="Recording.RecordingItems"/>); null
/// when everything was recorded. Data outside it was never stored, so its absence proves nothing.
/// </param>
public sealed record IncidentEvidence(
    IncidentKey Key,
    DateTimeOffset CenterAt,
    DateTimeOffset From,
    DateTimeOffset To,
    IncidentSource Source,
    UserIncidentType? UserType,
    bool SnapshotOnly,
    IReadOnlyList<MetricTrack> Metrics,
    IReadOnlyList<EvidenceEvent> Events,
    IReadOnlyList<Anomaly> Anomalies,
    IReadOnlyList<IncidentTrigger> Triggers,
    IReadOnlySet<string>? RecordedItems = null)
{
    /// <summary>Whether every one of <paramref name="items"/> was recorded (always true without a custom selection).</summary>
    public bool Recorded(params string[] items) => RecordedItems is null || items.All(RecordedItems.Contains);

    /// <summary>Tracks of a metric kind (e.g. every GPU's "gpu.util").</summary>
    public IEnumerable<MetricTrack> Tracks(string metric) => Metrics.Where(m => m.Metric == metric && m.Points.Count > 0);

    /// <summary>The single (system-wide) track of a metric, or null.</summary>
    public MetricTrack? Track(string metric) => Tracks(metric).FirstOrDefault();
}

/// <summary>One metric over the incident window.</summary>
/// <param name="SeriesId">The chartable series ID, e.g. "gpu.3.util" (used in evidence links).</param>
/// <param name="Metric">The metric kind, e.g. "gpu.util".</param>
/// <param name="Label">Label, e.g. "NVIDIA GeForce RTX 4090 utilization".</param>
/// <param name="Unit">Unit.</param>
/// <param name="Device">The device name, for per-device metrics.</param>
/// <param name="Points">Values, oldest first.</param>
public sealed record MetricTrack(string SeriesId, string Metric, string Label, string Unit, string? Device, IReadOnlyList<MetricPoint> Points)
{
    /// <summary>Points within [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public IEnumerable<MetricPoint> Between(DateTimeOffset from, DateTimeOffset to) => Points.Where(p => p.Timestamp >= from && p.Timestamp <= to);
}

/// <summary>One value.</summary>
public readonly record struct MetricPoint(DateTimeOffset Timestamp, double Value);

/// <summary>A timeline event.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Kind">"windows-event", "process", "service", "display", "recorder", "anomaly" or "trigger".</param>
/// <param name="Title">Title as shown in the timeline.</param>
/// <param name="Importance">0 (context) to 4 (critical).</param>
/// <param name="SourceTable">The table the event came from.</param>
/// <param name="SourceId">Row ID in that table.</param>
/// <param name="Category">Windows events: the category.</param>
/// <param name="Provider">Windows events: the provider.</param>
/// <param name="EventId">Windows events: the event ID.</param>
/// <param name="RuleId">Windows events: the classification rule.</param>
public sealed record EvidenceEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string Title,
    int Importance,
    string? SourceTable,
    long? SourceId,
    EventCategory? Category = null,
    string? Provider = null,
    int? EventId = null,
    string? RuleId = null)
{
    /// <summary>A link to this event.</summary>
    public EvidenceRef ToRef() => new("event", Timestamp, SourceTable is null || SourceId is null ? null : $"{SourceTable}:{SourceId}");
}

/// <summary>Reads an incident's evidence for the analyzers.</summary>
public interface IIncidentEvidenceSource
{
    /// <summary>The evidence, or null when the incident or its database does not exist.</summary>
    Task<IncidentEvidence?> LoadAsync(IncidentKey key, CancellationToken cancellationToken);
}
