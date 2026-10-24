namespace PcDiag.Core.Events;

/// <summary>
/// A classified, diagnostically relevant event (from the Windows Event Log, SCM, display, network or process monitoring).
/// Only events matched by a classification rule become a <see cref="SystemEvent"/>; the event log is never dumped wholesale.
/// </summary>
/// <param name="Timestamp">When Windows recorded the event.</param>
/// <param name="Channel">Source channel, e.g. "System", "Application", or an internal source such as "pcdiag.display".</param>
/// <param name="RecordId">Event log record ID, used to de-duplicate; null for internal events.</param>
/// <param name="Provider">Event provider, e.g. "Display" or "Microsoft-Windows-WHEA-Logger".</param>
/// <param name="EventId">Provider event ID.</param>
/// <param name="Level">Windows event level.</param>
/// <param name="Category">Diagnostic category assigned by the classification rules.</param>
/// <param name="Severity">Diagnostic severity assigned by the classification rules.</param>
/// <param name="Title">Short human-readable title.</param>
/// <param name="Message">Full event message (may be truncated for storage).</param>
/// <param name="ProcessId">Related process ID, when known.</param>
/// <param name="ProcessName">Related process name, when known.</param>
/// <param name="RelatedDevice">Related device (driver, disk, adapter), when known.</param>
/// <param name="RuleId">The classification rule that matched.</param>
/// <param name="Process">The related process including its start time, when the event identifies both (links to process history).</param>
/// <param name="ServiceName">The related Windows service (key name), when known.</param>
/// <param name="Data">Selected event fields extracted by the rule (for example an exception code or bug check code).</param>
public sealed record SystemEvent(
    DateTimeOffset Timestamp,
    string Channel,
    long? RecordId,
    string Provider,
    int EventId,
    EventLevel Level,
    EventCategory Category,
    Severity Severity,
    string Title,
    string Message,
    int? ProcessId = null,
    string? ProcessName = null,
    string? RelatedDevice = null,
    string? RuleId = null,
    Telemetry.ProcessKey? Process = null,
    string? ServiceName = null,
    IReadOnlyDictionary<string, string>? Data = null);

/// <summary>Windows event level (matches the Event Log level values).</summary>
public enum EventLevel
{
    /// <summary>Level 0 (LogAlways).</summary>
    LogAlways = 0,

    /// <summary>Critical.</summary>
    Critical = 1,

    /// <summary>Error.</summary>
    Error = 2,

    /// <summary>Warning.</summary>
    Warning = 3,

    /// <summary>Information.</summary>
    Information = 4,

    /// <summary>Verbose.</summary>
    Verbose = 5,
}

/// <summary>Diagnostic severity used across events, anomalies and incidents.</summary>
public enum Severity
{
    /// <summary>Informational context only.</summary>
    Info = 0,

    /// <summary>Low.</summary>
    Low = 1,

    /// <summary>Medium.</summary>
    Medium = 2,

    /// <summary>High.</summary>
    High = 3,

    /// <summary>Critical.</summary>
    Critical = 4,
}

/// <summary>Diagnostic category of an event.</summary>
public enum EventCategory
{
    /// <summary>Not otherwise classified.</summary>
    Other = 0,

    /// <summary>Display configuration or display driver.</summary>
    Display = 1,

    /// <summary>GPU driver, TDR / GPU reset, graphics kernel.</summary>
    Gpu = 2,

    /// <summary>Application crash.</summary>
    ApplicationCrash = 3,

    /// <summary>Application hang.</summary>
    ApplicationHang = 4,

    /// <summary>Windows Error Reporting.</summary>
    WindowsErrorReporting = 5,

    /// <summary>Kernel.</summary>
    Kernel = 6,

    /// <summary>Disk / storage controller.</summary>
    Disk = 7,

    /// <summary>File system (NTFS).</summary>
    FileSystem = 8,

    /// <summary>Network adapter or connectivity.</summary>
    Network = 9,

    /// <summary>Device / Plug and Play.</summary>
    Device = 10,

    /// <summary>Windows service.</summary>
    Service = 11,

    /// <summary>Unexpected shutdown.</summary>
    UnexpectedShutdown = 12,

    /// <summary>Bug check (BSOD).</summary>
    BugCheck = 13,

    /// <summary>WHEA hardware error.</summary>
    HardwareError = 14,

    /// <summary>Power / sleep / resume.</summary>
    Power = 15,

    /// <summary>System time change.</summary>
    TimeChange = 16,

    /// <summary>Resource exhaustion (low virtual memory).</summary>
    ResourceExhaustion = 17,

    /// <summary>Process lifecycle.</summary>
    Process = 18,
}
