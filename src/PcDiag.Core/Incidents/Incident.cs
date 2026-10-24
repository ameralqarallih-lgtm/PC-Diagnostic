using PcDiag.Core.Events;

namespace PcDiag.Core.Incidents;

/// <summary>A preserved incident: a window of evidence around a center timestamp.</summary>
/// <param name="Id">Database ID (equals the key's sequence number).</param>
/// <param name="Key">Human-readable key, e.g. INC-20260928-00027.</param>
/// <param name="CreatedAt">When the incident was created.</param>
/// <param name="CenterAt">The incident's center point (user report time, or trigger time).</param>
/// <param name="PreWindow">How much history before <paramref name="CenterAt"/> is preserved.</param>
/// <param name="PostWindow">How long recording continues after <paramref name="CenterAt"/>.</param>
/// <param name="State">Lifecycle state.</param>
/// <param name="Source">What created the incident.</param>
/// <param name="UserType">The type the user reported, if any.</param>
/// <param name="Title">Display title.</param>
/// <param name="Notes">Free-text user notes.</param>
/// <param name="Severity">Overall severity.</param>
public sealed record Incident(
    long Id,
    IncidentKey Key,
    DateTimeOffset CreatedAt,
    DateTimeOffset CenterAt,
    TimeSpan PreWindow,
    TimeSpan PostWindow,
    IncidentState State,
    IncidentSource Source,
    UserIncidentType? UserType,
    string Title,
    string? Notes,
    Severity Severity);

/// <summary>Something that asks for an incident to be created (or merged into an open one).</summary>
/// <param name="Timestamp">When the trigger fired; becomes the center point of a new incident.</param>
/// <param name="Source">Trigger source.</param>
/// <param name="UserType">User-reported type (user triggers only).</param>
/// <param name="RuleId">Anomaly or event rule that fired (automatic triggers only).</param>
/// <param name="Detail">Human-readable detail, e.g. the rule's reasoning summary.</param>
/// <param name="Note">Optional user note.</param>
/// <param name="Severity">Severity for a new incident (automatic triggers); Medium when null.</param>
public sealed record IncidentTrigger(
    DateTimeOffset Timestamp,
    IncidentSource Source,
    UserIncidentType? UserType = null,
    string? RuleId = null,
    string? Detail = null,
    string? Note = null,
    Severity? Severity = null);

/// <summary>Incident lifecycle state.</summary>
public enum IncidentState
{
    /// <summary>Pre-window preserved; still recording the post-window.</summary>
    Recording = 0,

    /// <summary>Copying the post-window and backfilling late events.</summary>
    Finalizing = 1,

    /// <summary>Running diagnostic analyzers.</summary>
    Analyzing = 2,

    /// <summary>Complete.</summary>
    Complete = 3,

    /// <summary>The recorder stopped before the post-window finished; evidence is incomplete.</summary>
    Partial = 4,

    /// <summary>Capture failed; see logs.</summary>
    Failed = 5,
}

/// <summary>What created an incident.</summary>
public enum IncidentSource
{
    /// <summary>The Capture button in the UI.</summary>
    User = 0,

    /// <summary>The global hotkey.</summary>
    Hotkey = 1,

    /// <summary>The tray menu.</summary>
    Tray = 2,

    /// <summary>An anomaly rule.</summary>
    Rule = 3,

    /// <summary>A classified Windows event (e.g. GPU reset, crash).</summary>
    Event = 4,

    /// <summary>Detected at startup after an unexpected shutdown or bug check.</summary>
    BootRecovery = 5,
}

/// <summary>Quick incident types the user can report.</summary>
public enum UserIncidentType
{
    /// <summary>Screen Flicker.</summary>
    ScreenFlicker = 0,

    /// <summary>Screen Artifact.</summary>
    ScreenArtifact = 1,

    /// <summary>System Freeze.</summary>
    SystemFreeze = 2,

    /// <summary>Application Crash.</summary>
    ApplicationCrash = 3,

    /// <summary>Game Crash.</summary>
    GameCrash = 4,

    /// <summary>Slowdown.</summary>
    Slowdown = 5,

    /// <summary>High CPU.</summary>
    HighCpu = 6,

    /// <summary>High RAM.</summary>
    HighRam = 7,

    /// <summary>High GPU.</summary>
    HighGpu = 8,

    /// <summary>Disk Problem.</summary>
    DiskProblem = 9,

    /// <summary>Network Problem.</summary>
    NetworkProblem = 10,

    /// <summary>Other.</summary>
    Other = 11,
}

/// <summary>Display helpers for <see cref="UserIncidentType"/>.</summary>
public static class UserIncidentTypeExtensions
{
    /// <summary>The label shown to the user, e.g. "Screen Flicker".</summary>
    public static string ToDisplayName(this UserIncidentType type) => type switch
    {
        UserIncidentType.ScreenFlicker => "Screen Flicker",
        UserIncidentType.ScreenArtifact => "Screen Artifact",
        UserIncidentType.SystemFreeze => "System Freeze",
        UserIncidentType.ApplicationCrash => "Application Crash",
        UserIncidentType.GameCrash => "Game Crash",
        UserIncidentType.Slowdown => "Slowdown",
        UserIncidentType.HighCpu => "High CPU",
        UserIncidentType.HighRam => "High RAM",
        UserIncidentType.HighGpu => "High GPU",
        UserIncidentType.DiskProblem => "Disk Problem",
        UserIncidentType.NetworkProblem => "Network Problem",
        UserIncidentType.Other => "Other",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown incident type."),
    };
}
