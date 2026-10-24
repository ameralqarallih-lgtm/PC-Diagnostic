using System.Globalization;
using PcDiag.Core.Events;

namespace PcDiag.Core.Anomalies;

/// <summary>A detected deviation from normal behaviour, with the reasoning that produced it.</summary>
/// <param name="Id">Database ID (0 before it is stored).</param>
/// <param name="RuleId">The rule that detected it.</param>
/// <param name="Series">The metric (e.g. "gpu.util"), or "event:&lt;event rule&gt;" for event rules.</param>
/// <param name="StartedAt">When the condition started.</param>
/// <param name="EndedAt">When the condition ended; null while ongoing.</param>
/// <param name="Observed">The most extreme value seen while the condition held (the event ID for event rules).</param>
/// <param name="Severity">Severity.</param>
/// <param name="Reasoning">Why the rule fired, shown to the user verbatim.</param>
/// <param name="Device">The device the value belongs to (a GPU, disk or network adapter name), when per device.</param>
/// <param name="IncidentKey">The incident created (or joined) for it, if any.</param>
public sealed record Anomaly(
    long Id,
    string RuleId,
    string Series,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    double Observed,
    Severity Severity,
    AnomalyReasoning Reasoning,
    string? Device = null,
    string? IncidentKey = null)
{
    /// <summary>The anomaly as the API and the live feed show it.</summary>
    public AnomalyInfo ToInfo(string unit) => new(
        Id,
        RuleId,
        Reasoning.Classification,
        Series,
        Device,
        StartedAt.ToUnixTimeMilliseconds(),
        EndedAt?.ToUnixTimeMilliseconds(),
        Observed,
        unit,
        Severity,
        Reasoning.RuleDescription,
        Reasoning.Baseline,
        Reasoning.Current,
        Math.Round(Reasoning.Duration.TotalSeconds, 1),
        IncidentKey,
        Reasoning.Summary());
}

/// <summary>Transparent explanation of an anomaly, e.g. "GPU normally 40–70 %, current 99 %, for 18 s".</summary>
/// <param name="Classification">Classification, e.g. "GPU utilization anomaly".</param>
/// <param name="RuleDescription">The rule in plain words, e.g. "GPU util above baseline + 3σ for at least 15 s".</param>
/// <param name="Baseline">Normal range description, when the rule uses a baseline.</param>
/// <param name="BaselineMean">Baseline mean, when applicable.</param>
/// <param name="BaselineStdDev">Baseline standard deviation, when applicable.</param>
/// <param name="Current">Current value description.</param>
/// <param name="Duration">How long the condition has held.</param>
public sealed record AnomalyReasoning(
    string Classification,
    string RuleDescription,
    string? Baseline,
    double? BaselineMean,
    double? BaselineStdDev,
    string Current,
    TimeSpan Duration)
{
    /// <summary>One line, e.g. "Normally 40–70 %; current 99 %; for 18 s. Rule: … Classification: GPU utilization anomaly."</summary>
    public string Summary()
    {
        var parts = new List<string>(4);
        if (Baseline is not null)
        {
            parts.Add($"Normally {Baseline}");
        }

        parts.Add($"current {Current}");
        if (Duration >= TimeSpan.FromSeconds(1))
        {
            parts.Add($"for {FormatDuration(Duration)}");
        }

        var text = string.Join("; ", parts);
        return $"{char.ToUpperInvariant(text[0])}{text[1..]}. Rule: {RuleDescription}. Classification: {Classification}.";
    }

    /// <summary>"18 s", "2 min 5 s", "1 h 3 min".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)Math.Round(duration.TotalSeconds))} s");
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return duration.Seconds == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} min")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalMinutes} min {duration.Seconds} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours} h {duration.Minutes} min");
    }
}

/// <summary>An anomaly as the API and the live feed show it. Times are Unix milliseconds.</summary>
/// <param name="Id">Database ID.</param>
/// <param name="RuleId">The rule.</param>
/// <param name="Classification">E.g. "GPU utilization anomaly".</param>
/// <param name="Series">The metric, or "event:…".</param>
/// <param name="Device">The device, when per device.</param>
/// <param name="StartedAt">When the condition started.</param>
/// <param name="EndedAt">When it ended; null while ongoing.</param>
/// <param name="Observed">Most extreme value.</param>
/// <param name="Unit">Unit of <paramref name="Observed"/> ("" for events).</param>
/// <param name="Severity">Severity.</param>
/// <param name="Rule">The rule in plain words.</param>
/// <param name="Baseline">Normal range, when the rule uses a baseline.</param>
/// <param name="Current">Current value description.</param>
/// <param name="DurationSeconds">How long the condition held.</param>
/// <param name="IncidentKey">The incident created or joined for it.</param>
/// <param name="Summary">The reasoning in one line.</param>
public sealed record AnomalyInfo(
    long Id,
    string RuleId,
    string Classification,
    string Series,
    string? Device,
    long StartedAt,
    long? EndedAt,
    double Observed,
    string Unit,
    Severity Severity,
    string Rule,
    string? Baseline,
    string Current,
    double DurationSeconds,
    string? IncidentKey,
    string Summary);

/// <summary>Persists anomalies in telemetry.db, from where incidents copy the ones in their window.</summary>
public interface IAnomalyStore
{
    /// <summary>Stores a new anomaly and returns its ID.</summary>
    Task<long> AddAsync(Anomaly anomaly, CancellationToken cancellationToken);

    /// <summary>Updates an anomaly's end, observed value and reasoning.</summary>
    Task UpdateAsync(Anomaly anomaly, CancellationToken cancellationToken);

    /// <summary>Records the incident created or joined for an anomaly.</summary>
    Task LinkIncidentAsync(long anomalyId, string incidentKey, CancellationToken cancellationToken);

    /// <summary>
    /// Ends anomalies that started before <paramref name="before"/> and were left open by a previous run, at their
    /// last stored duration.
    /// </summary>
    Task<int> CloseAbandonedAsync(DateTimeOffset before, CancellationToken cancellationToken);
}
