using PcDiag.Core.Events;

namespace PcDiag.Core.Incidents;

/// <summary>One row of the incident list (read from the incident index).</summary>
/// <param name="Key">Incident key, e.g. INC-20261006-00001.</param>
/// <param name="CreatedAt">When the incident was created.</param>
/// <param name="CenterAt">The incident's center point.</param>
/// <param name="PreSeconds">Seconds preserved before the center.</param>
/// <param name="PostSeconds">Seconds recorded after the center.</param>
/// <param name="State">Lifecycle state.</param>
/// <param name="Source">What created it.</param>
/// <param name="UserType">The type the user reported, if any.</param>
/// <param name="Title">Display title.</param>
/// <param name="Notes">User notes.</param>
/// <param name="Severity">Overall severity.</param>
/// <param name="SnapshotOnly">Captured while the recorder was not running (no pre-window).</param>
/// <param name="Pinned">Kept regardless of retention.</param>
/// <param name="TriggerCount">How many triggers were merged into it.</param>
public sealed record IncidentSummary(
    string Key,
    DateTimeOffset CreatedAt,
    DateTimeOffset CenterAt,
    int PreSeconds,
    int PostSeconds,
    IncidentState State,
    IncidentSource Source,
    UserIncidentType? UserType,
    string Title,
    string? Notes,
    Severity Severity,
    bool SnapshotOnly,
    bool Pinned,
    int TriggerCount);

/// <summary>A page of incidents.</summary>
/// <param name="Total">All incidents.</param>
/// <param name="Items">The requested page, newest first.</param>
public sealed record IncidentPage(int Total, IReadOnlyList<IncidentSummary> Items);
