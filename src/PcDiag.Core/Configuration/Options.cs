using PcDiag.Core.Recording;

namespace PcDiag.Core.Configuration;

/// <summary>Storage location and data retention ("Storage" section).</summary>
public sealed class StorageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Storage";

    /// <summary>
    /// Data directory. Null or empty uses <c>%LOCALAPPDATA%\PcDiagnostic</c>.
    /// Must be a fully qualified local path (no UNC or device paths).
    /// </summary>
    public string? DataDirectory { get; set; }

    /// <summary>How long full-resolution telemetry is kept in the rolling store (1–72 hours).</summary>
    public int TelemetryRetentionHours { get; set; } = 24;

    /// <summary>How long 1-minute rollups are kept (1–365 days).</summary>
    public int RollupRetentionDays { get; set; } = 30;

    /// <summary>
    /// Seconds between batched writes to disk (1–60). This is the most telemetry that can be lost
    /// if the PC crashes or loses power.
    /// </summary>
    public int WriteBehindSeconds { get; set; } = 5;

    /// <summary>Maximum incidents kept; 0 keeps all. The oldest unpinned incidents are removed beyond it; pinned incidents never are.</summary>
    public int MaxIncidents { get; set; }

    /// <summary>
    /// Most space the data folder may use, in GB (0–10000; 0 = no limit). Above it, full detail is kept for a shorter
    /// time (down to 2 hours), then one-minute summaries (down to 7 days), then the oldest unpinned incidents are removed.
    /// </summary>
    public int DataLimitGb { get; set; } = 10;

    /// <summary>
    /// Free space to keep on the data folder's drive, in GB (0–1000; 0 = only the 1 GB below which nothing is written).
    /// Below it, full detail and summaries are kept for a shorter time; incidents are never removed for it.
    /// </summary>
    public int KeepFreeGb { get; set; } = 5;
}

/// <summary>Recorder behaviour ("Recorder" section).</summary>
public sealed class RecorderOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Recorder";

    /// <summary>Allowed pre-incident window lengths, in minutes.</summary>
    public static IReadOnlyList<int> AllowedPreIncidentMinutes { get; } = [1, 5, 10, 15, 30];

    /// <summary>When to record.</summary>
    public RecordingMode Mode { get; set; } = RecordingMode.Manual;

    /// <summary>Weekly windows used when <see cref="Mode"/> is <see cref="RecordingMode.Scheduled"/>.</summary>
    public IList<RecordingWindow> Schedule { get; init; } = [];

    /// <summary>
    /// The last custom recording the user started from the dashboard ("Custom recording"), offered again next time.
    /// Null until one was chosen.
    /// </summary>
    public RecordingSelection? CustomSelection { get; set; }

    /// <summary>
    /// While not recording (and not paused), watch for problems in memory: a problem whose rule creates incidents starts
    /// a recording, and its incident keeps the minutes before it. Nothing is stored until then.
    /// </summary>
    public bool WatchWhenNotRecording { get; set; } = true;

    /// <summary>Whether the Agent starts automatically at logon (applied to the scheduled task).</summary>
    public bool StartAgentAtLogon { get; set; }

    /// <summary>History preserved before an incident, in minutes (1, 5, 10, 15 or 30).</summary>
    public int PreIncidentMinutes { get; set; } = 10;

    /// <summary>Recording after an incident, in seconds (15–900).</summary>
    public int PostIncidentSeconds { get; set; } = 120;

    /// <summary>Upper bound for the in-memory ring buffer, in megabytes (16–512).</summary>
    public int RingBufferMaxMegabytes { get; set; } = 64;
}

/// <summary>Sampling intervals and overhead budget ("Sampling" section).</summary>
public sealed class SamplingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Sampling";

    /// <summary>System metrics interval in milliseconds (250–10000).</summary>
    public int SystemIntervalMs { get; set; } = 1000;

    /// <summary>Hardware sensor interval in milliseconds (250–10000).</summary>
    public int SensorIntervalMs { get; set; } = 1000;

    /// <summary>Process metrics interval in milliseconds (500–30000).</summary>
    public int ProcessIntervalMs { get; set; } = 2000;

    /// <summary>Detailed inventory interval in seconds (5–300).</summary>
    public int InventoryIntervalSeconds { get; set; } = 10;

    /// <summary>Number of processes persisted per ranking (CPU, RAM, GPU, I/O) each tick (5–200).</summary>
    public int TopProcessCount { get; set; } = 25;

    /// <summary>Average CPU budget for the Agent itself, in percent (0.5–10). Sampling backs off above it.</summary>
    public double CpuBudgetPercent { get; set; } = 2.0;
}

/// <summary>Windows event collection ("Events" section). Classification rules live in event-rules.json.</summary>
public sealed class EventOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Events";

    /// <summary>How far back to read matching events when recording starts, in minutes (0–1440).</summary>
    public int BackfillMinutes { get; set; } = 30;

    /// <summary>
    /// Rules marked "sinceBoot" (unexpected shutdown, bug check) are read back to the last boot, capped at this many
    /// hours (0–168), so a crash before a late start is still recorded.
    /// </summary>
    public int BootBackfillMaxHours { get; set; } = 24;

    /// <summary>Events recorded per rule per minute (1–1000); further events in an event storm are counted, not stored.</summary>
    public int MaxPerRulePerMinute { get; set; } = 20;

    /// <summary>Maximum stored message length in characters (200–32000).</summary>
    public int MaxMessageLength { get; set; } = 2000;
}

/// <summary>Privacy settings ("Privacy" section). Sensitive collection is off by default.</summary>
public sealed class PrivacyOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Privacy";

    /// <summary>
    /// Capture process command lines. Command lines can contain file paths, URLs and occasionally credentials;
    /// known secret patterns are redacted, but review exports before sharing them.
    /// </summary>
    public bool CaptureCommandLines { get; set; }

    /// <summary>Capture the user account each process runs as.</summary>
    public bool CaptureUserNames { get; set; }

    /// <summary>Capture Wi-Fi network names (SSID/BSSID). Windows may also require location permission.</summary>
    public bool CaptureWifiNetworkNames { get; set; }

    /// <summary>Collect classified Windows Event Log entries.</summary>
    public bool CollectWindowsEvents { get; set; } = true;

    /// <summary>Collect network interface details (names, addresses, link information).</summary>
    public bool CollectNetworkDetails { get; set; } = true;
}

/// <summary>
/// Automatic incident triggers ("Triggers" section): the user's changes to the shipped anomaly rules, plus limits
/// that keep automatic capture from flooding the incident list.
/// </summary>
public sealed class TriggerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Triggers";

    /// <summary>Master switch: when off, rules still record anomalies but never create incidents.</summary>
    public bool AutomaticIncidents { get; set; } = true;

    /// <summary>Most incidents all rules together may create per hour (1–60).</summary>
    public int MaxIncidentsPerHour { get; set; } = 4;

    /// <summary>Changes to individual rules, by rule ID. Unknown IDs are ignored.</summary>
    public Dictionary<string, TriggerRuleSettings> Rules { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's own rules, evaluated like the shipped ones (IDs start with "my-").</summary>
    public List<CustomTriggerRule> Custom { get; init; } = [];

    /// <summary>Shipped rules the user deleted (taking an ID off the list brings the rule back).</summary>
    public List<string> Removed { get; init; } = [];

    /// <summary>The categories rules are listed under, in order; empty: the built-in ones.</summary>
    public List<TriggerCategory> Categories { get; init; } = [];

    /// <summary>Shipped rules moved to another category, by rule ID.</summary>
    public Dictionary<string, string> RuleCategories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A category anomaly rules are listed under.</summary>
public sealed class TriggerCategory
{
    /// <summary>Lowercase letters, digits and dashes.</summary>
    public string Id { get; set; } = "";

    /// <summary>The name shown.</summary>
    public string Name { get; set; } = "";
}

/// <summary>A Windows event a user rule watches, described by the user (not one of the classified ones).</summary>
public sealed class CustomEventMatch
{
    /// <summary>The event log, e.g. "System", "Application" or "Microsoft-Windows-DxgKrnl-Admin".</summary>
    public string Channel { get; set; } = "System";

    /// <summary>The source (provider), e.g. "Microsoft-Windows-Kernel-Power"; empty: any (then levels must be set).</summary>
    public string? Provider { get; set; }

    /// <summary>Event IDs; empty: any.</summary>
    public List<int> EventIds { get; init; } = [];

    /// <summary>Levels (1 critical, 2 error, 3 warning, 4 information); empty: any.</summary>
    public List<int> Levels { get; init; } = [];
}

/// <summary>
/// One of the user's own anomaly rules ("Triggers:Custom"): a full definition, with the same meaning as a shipped
/// rule's (see <see cref="Anomalies.AnomalyRule"/>). Settable members, so configuration binding fills it.
/// </summary>
public sealed class CustomTriggerRule
{
    /// <summary>"my-" followed by lowercase letters, digits and dashes.</summary>
    public string Id { get; set; } = "";

    /// <summary>Name shown in Settings and with each anomaly.</summary>
    public string Name { get; set; } = "";

    /// <summary>How the rule decides.</summary>
    public Anomalies.AnomalyRuleKind Kind { get; set; } = Anomalies.AnomalyRuleKind.ThresholdDuration;

    /// <summary>The watched metric (measurement rules): a shipped metric or one of the system's readings ("reading:…").</summary>
    public string? Metric { get; set; }

    /// <summary>The reading's name, when it watches one of the system's readings.</summary>
    public string? ReadingLabel { get; set; }

    /// <summary>The reading's unit, when it watches one of the system's readings.</summary>
    public string? ReadingUnit { get; set; }

    /// <summary>The category it is listed under; null: by what it watches.</summary>
    public string? Category { get; set; }

    /// <summary>Which side is abnormal.</summary>
    public Anomalies.ThresholdDirection Direction { get; set; } = Anomalies.ThresholdDirection.Above;

    /// <summary>The threshold (a value, a z-score, a percentage change or a count, by kind).</summary>
    public double Threshold { get; set; }

    /// <summary>How long the condition must hold (seconds).</summary>
    public double DurationSeconds { get; set; } = 60;

    /// <summary>The window a change or count is measured over (seconds).</summary>
    public double WindowSeconds { get; set; } = 10;

    /// <summary>Event rules: classified Windows event rule IDs ("gpu.tdr", or a prefix ending in "*").</summary>
    public List<string> EventRules { get; init; } = [];

    /// <summary>Event rules: process transitions.</summary>
    public List<Events.ProcessEventKind> ProcessEvents { get; init; } = [];

    /// <summary>Event rules: only these programs ("game.exe", "chrome*"); empty: any.</summary>
    public List<string> ProcessNames { get; init; } = [];

    /// <summary>Event rules: display changes.</summary>
    public List<Events.DisplayEventKind> DisplayChanges { get; init; } = [];

    /// <summary>Event rules: only these displays (by name); empty: any.</summary>
    public List<string> DisplayTargets { get; init; } = [];

    /// <summary>Event rules: Windows events the user describes (log, source, IDs, levels).</summary>
    public List<CustomEventMatch> WindowsEvents { get; init; } = [];

    /// <summary>Severity of its anomalies and incidents.</summary>
    public Events.Severity Severity { get; set; } = Events.Severity.Medium;

    /// <summary>Whether it is evaluated.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether its anomalies create incidents.</summary>
    public bool CreateIncident { get; set; } = true;

    /// <summary>Minutes before it may create another incident.</summary>
    public int CooldownMinutes { get; set; } = 30;
}

/// <summary>The user's change to one anomaly rule; null keeps the shipped value.</summary>
public sealed class TriggerRuleSettings
{
    /// <summary>Whether the rule is evaluated.</summary>
    public bool? Enabled { get; set; }

    /// <summary>Whether its anomalies create incidents.</summary>
    public bool? CreateIncident { get; set; }

    /// <summary>The threshold (value, z-score, percentage change or count, depending on the rule).</summary>
    public double? Threshold { get; set; }

    /// <summary>Minimum duration (1–3600 s), for rules with a duration.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Measurement window (1–600 s), for rate-of-change and counter rules.</summary>
    public double? WindowSeconds { get; set; }

    /// <summary>Minutes before the rule may create another incident (0–1440).</summary>
    public int? CooldownMinutes { get; set; }
}

/// <summary>
/// Enhanced mode ("Enhanced" section): deeper evidence from Event Tracing for Windows, off by default. Both parts
/// need the recorder to run as administrator and run only while recording.
/// </summary>
public sealed class EnhancedOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Enhanced";

    /// <summary>
    /// A Windows Performance Recorder flight recorder: recent detailed system, GPU and desktop composition activity,
    /// kept in memory (at most 384 MB) and saved compressed as traces\incident.etl with each new incident (open it
    /// with Windows Performance Analyzer). Saving takes about half a minute in the background; traces are typically
    /// around 100 MB.
    /// </summary>
    public bool FlightRecorder { get; set; }

    /// <summary>
    /// True per-process disk and network transfer rates (instead of all I/O together), from kernel events: on by
    /// default, measured whenever the recorder samples (recording, watching or showing the dashboard).
    /// </summary>
    public bool ProcessIo { get; set; } = true;
}
