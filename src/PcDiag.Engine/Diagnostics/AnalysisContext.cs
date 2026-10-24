using System.Globalization;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Incidents;

namespace PcDiag.Engine.Diagnostics;

/// <summary>
/// What every analyzer gets: the evidence, the rules in effect (for "configured" limits) and helpers that keep the
/// wording consistent ("about 4 s before the reported screen flicker", local clock times).
/// </summary>
public sealed class AnalysisContext
{
    /// <summary>"Around the incident" for activity: this long before the incident moment…</summary>
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(60);

    /// <summary>…to this long after it.</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(15);

    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, AnomalyRule> _rules;
    private readonly List<Notable> _notables = [];

    /// <summary>Creates the context.</summary>
    public AnalysisContext(IncidentEvidence evidence, IReadOnlyList<AnomalyRule> rules, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(rules);
        Evidence = evidence;
        _zone = zone;
        _rules = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        Incident = evidence.UserType is { } type
            ? $"the reported {type.ToDisplayName().ToLowerInvariant()}"
            : evidence.Source switch
            {
                IncidentSource.Rule or IncidentSource.Event => "the automatic capture",
                IncidentSource.BootRecovery => "the last recorded moment before the restart",
                _ => "the capture",
            };
    }

    /// <summary>The evidence.</summary>
    public IncidentEvidence Evidence { get; }

    /// <summary>Notable activity found by the analyzers, for the correlation step.</summary>
    public IReadOnlyList<Notable> Notables => _notables;

    /// <summary>How the incident moment is referred to, e.g. "the reported screen flicker".</summary>
    public string Incident { get; }

    /// <summary>The incident moment.</summary>
    public DateTimeOffset Center => Evidence.CenterAt;

    /// <summary>Start of "around the incident".</summary>
    public DateTimeOffset AroundFrom => Center - Before;

    /// <summary>End of "around the incident".</summary>
    public DateTimeOffset AroundTo => Center + After;

    /// <summary>Records notable activity in a subsystem (see <see cref="Subsystems"/>) for the correlation step.</summary>
    public void Note(string subsystem, DateTimeOffset at, string label, EvidenceRef evidence) => _notables.Add(new Notable(subsystem, at, label, evidence));

    /// <summary>A rule's threshold as configured, or the fallback when the rule is unknown.</summary>
    public double Threshold(string ruleId, double fallback) => _rules.TryGetValue(ruleId, out var rule) ? rule.Threshold : fallback;

    /// <summary>Local clock time, e.g. "12:30:54".</summary>
    public string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"about 4 s before the reported screen flicker", "at the moment of the capture", "about 2 min after the capture".</summary>
    public string Relative(DateTimeOffset at)
    {
        var delta = at - Center;
        if (delta.Duration() < TimeSpan.FromSeconds(1))
        {
            return $"at the moment of {Incident}";
        }

        var amount = AnomalyReasoning.FormatDuration(delta.Duration());
        return delta < TimeSpan.Zero ? $"about {amount} before {Incident}" : $"about {amount} after {Incident}";
    }

    /// <summary>A short relative time, e.g. "4 s before", "2 s after", "at the same moment".</summary>
    public string Offset(DateTimeOffset at)
    {
        var delta = at - Center;
        return delta.Duration() < TimeSpan.FromSeconds(1)
            ? "at the same moment"
            : $"{AnomalyReasoning.FormatDuration(delta.Duration())} {(delta < TimeSpan.Zero ? "before" : "after")}";
    }

    /// <summary>Whether a moment is "around the incident".</summary>
    public bool IsAround(DateTimeOffset at) => at >= AroundFrom && at <= AroundTo;

    /// <summary>A link to a metric value.</summary>
    public static EvidenceRef Metric(MetricTrack track, DateTimeOffset at) => new("metric", at, track.SeriesId);

    /// <summary>A link to a metric over the window (for negative observations).</summary>
    public EvidenceRef Range(MetricTrack track) => new("range", Evidence.From, track.SeriesId);

    /// <summary>A value with its unit, e.g. "99 %", "4.2 GB", "2100 MHz".</summary>
    public static string Format(double value, string unit) => unit switch
    {
        "B" => FormatBytes(value),
        "B/s" => FormatBytes(value) + "/s",
        _ => AnomalyMetrics.Format(value, unit),
    };

    /// <summary>"4.2 GB", "512 MB".</summary>
    public static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_073_741_824 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_073_741_824:0.0} GB"),
        >= 1_048_576 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1_048_576:0} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024:0} KB"),
    };

    /// <summary>A Windows event's ID and provider, e.g. "Event 4101, Display".</summary>
    public static string EventSource(EvidenceEvent e) =>
        e.EventId is { } id ? string.Create(CultureInfo.InvariantCulture, $"Event {id}{(e.Provider is { Length: > 0 } p ? $", {p}" : "")}") : e.Kind;
}

/// <summary>Notable activity in a subsystem.</summary>
/// <param name="Subsystem">One of <see cref="Subsystems"/>.</param>
/// <param name="At">When.</param>
/// <param name="Label">Short label, e.g. "GPU clock drop from 2100 MHz to 300 MHz".</param>
/// <param name="Evidence">The data behind it.</param>
public sealed record Notable(string Subsystem, DateTimeOffset At, string Label, EvidenceRef Evidence);

/// <summary>Subsystem names used for correlations.</summary>
public static class Subsystems
{
    /// <summary>GPU, display and graphics drivers.</summary>
    public const string Gpu = "GPU and display";

    /// <summary>Processor load.</summary>
    public const string Cpu = "CPU";

    /// <summary>Memory.</summary>
    public const string Memory = "Memory";

    /// <summary>Disks and file systems.</summary>
    public const string Storage = "Storage";

    /// <summary>Network adapters.</summary>
    public const string Network = "Network";

    /// <summary>Applications (crashes, hangs) and services.</summary>
    public const string Application = "Application";

    /// <summary>Temperatures and power or thermal limits.</summary>
    public const string Thermal = "Thermal";

    /// <summary>Windows itself (bug checks, hardware errors, kernel reports).</summary>
    public const string System = "Windows and hardware";
}
