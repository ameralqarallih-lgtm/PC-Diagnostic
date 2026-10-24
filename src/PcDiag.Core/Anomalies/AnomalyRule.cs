using PcDiag.Core.Events;

namespace PcDiag.Core.Anomalies;

/// <summary>How a rule decides that something is abnormal.</summary>
public enum AnomalyRuleKind
{
    /// <summary>The value crosses a threshold.</summary>
    Threshold = 0,

    /// <summary>The value stays across a threshold for a minimum duration.</summary>
    ThresholdDuration = 1,

    /// <summary>The value drops (or rises) by a percentage within a short window.</summary>
    RateOfChange = 2,

    /// <summary>The value stays more than N standard deviations from its moving baseline for a minimum duration.</summary>
    BaselineDeviation = 3,

    /// <summary>A per-sample count adds up to a threshold within a window.</summary>
    CounterSpike = 4,

    /// <summary>A classified Windows event, a process crash or hang, or a display change.</summary>
    Event = 5,
}

/// <summary>Which side of the threshold is abnormal.</summary>
public enum ThresholdDirection
{
    /// <summary>Values above the threshold (or a rise, for rate of change).</summary>
    Above = 0,

    /// <summary>Values below the threshold (or a drop, for rate of change).</summary>
    Below = 1,
}

/// <summary>
/// One anomaly rule. The shipped rules are in anomaly-rules.json; the user changes them with overrides in the
/// "Triggers" settings (enabled, incident creation, threshold, duration, window, cooldown).
/// </summary>
public sealed record AnomalyRule
{
    /// <summary>Stable ID, e.g. "gpu-clock-drop".</summary>
    public required string Id { get; init; }

    /// <summary>Name shown in Settings, e.g. "Sudden GPU clock drop".</summary>
    public required string Name { get; init; }

    /// <summary>Classification shown with each anomaly, e.g. "GPU utilization anomaly".</summary>
    public required string Classification { get; init; }

    /// <summary>How the rule decides.</summary>
    public required AnomalyRuleKind Kind { get; init; }

    /// <summary>The watched metric (see <see cref="AnomalyMetrics"/>); null for event rules.</summary>
    public string? Metric { get; init; }

    /// <summary>Which side is abnormal.</summary>
    public ThresholdDirection Direction { get; init; } = ThresholdDirection.Above;

    /// <summary>
    /// The threshold: a metric value (Threshold, ThresholdDuration), a z-score (BaselineDeviation), a percentage
    /// change (RateOfChange) or a count (CounterSpike).
    /// </summary>
    public double Threshold { get; init; }

    /// <summary>How long the condition must hold (ThresholdDuration, BaselineDeviation).</summary>
    public double DurationSeconds { get; init; }

    /// <summary>The window a change or count is measured over (RateOfChange, CounterSpike).</summary>
    public double WindowSeconds { get; init; }

    /// <summary>RateOfChange: only while this metric (same device) is at least <see cref="GuardMinimum"/>.</summary>
    public string? GuardMetric { get; init; }

    /// <summary>RateOfChange: the minimum of <see cref="GuardMetric"/>.</summary>
    public double GuardMinimum { get; init; }

    /// <summary>BaselineDeviation: the baseline's time span (an exponentially weighted moving average).</summary>
    public double BaselineMinutes { get; init; } = 30;

    /// <summary>BaselineDeviation: samples needed before the baseline is trusted, in minutes.</summary>
    public double WarmupMinutes { get; init; } = 5;

    /// <summary>BaselineDeviation: smallest standard deviation used, so a flat baseline does not make every blip an anomaly.</summary>
    public double MinStdDev { get; init; } = 1;

    /// <summary>Event rules: classified event rule IDs; a trailing "*" matches a prefix.</summary>
    public IReadOnlyList<string> EventRules { get; init; } = [];

    /// <summary>Event rules: process transitions ("Crashed", "Hung").</summary>
    public IReadOnlyList<ProcessEventKind> ProcessEvents { get; init; } = [];

    /// <summary>Event rules: display changes (e.g. "Disconnected", "RefreshRateChanged").</summary>
    public IReadOnlyList<DisplayEventKind> DisplayChanges { get; init; } = [];

    /// <summary>What an event rule watches, in plain words.</summary>
    public string? Description { get; init; }

    /// <summary>A reading's name, for a rule that watches one of the system's readings ("reading:…").</summary>
    public string? ReadingLabel { get; init; }

    /// <summary>A reading's unit, for a rule that watches one of the system's readings.</summary>
    public string? ReadingUnit { get; init; }

    /// <summary>Event rules: only these programs' crashes and hangs ("game.exe", "chrome*"); empty: any program.</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = [];

    /// <summary>Event rules: only these displays' changes (by name, e.g. "PG32UCDP"); empty: any display.</summary>
    public IReadOnlyList<string> DisplayTargets { get; init; } = [];

    /// <summary>The category it is listed under (see <see cref="AnomalyCategories"/>).</summary>
    public string Category { get; init; } = "";

    /// <summary>Why the default is what it is (shown in Settings).</summary>
    public string? Note { get; init; }

    /// <summary>Severity of its anomalies (and of incidents it creates).</summary>
    public Severity Severity { get; init; } = Severity.Medium;

    /// <summary>Whether the rule is evaluated.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether an anomaly creates an incident (the rule is then an automatic trigger).</summary>
    public bool CreateIncident { get; init; }

    /// <summary>After creating an incident, the rule does not create another for this long.</summary>
    public int CooldownMinutes { get; init; } = 30;

    /// <summary>The metric definition (null for event rules).</summary>
    public AnomalyMetric? MetricInfo => AnomalyMetrics.IsReading(Metric)
        ? new AnomalyMetric(Metric!, ReadingLabel ?? Metric![AnomalyMetrics.ReadingPrefix.Length..], ReadingUnit ?? "", false, -AnomalyMetrics.ReadingLimit, AnomalyMetrics.ReadingLimit)
        : AnomalyMetrics.Find(Metric);

    /// <summary>The unit of <see cref="Threshold"/>.</summary>
    public string ThresholdUnit => Kind switch
    {
        AnomalyRuleKind.BaselineDeviation => "σ",
        AnomalyRuleKind.RateOfChange => "%",
        AnomalyRuleKind.Event => "",
        _ => MetricInfo?.Unit ?? "",
    };

    /// <summary>The allowed threshold range.</summary>
    public (double Min, double Max) ThresholdRange => Kind switch
    {
        AnomalyRuleKind.BaselineDeviation => (1, 10),
        AnomalyRuleKind.RateOfChange => (10, 100),
        AnomalyRuleKind.CounterSpike => (1, 100_000),
        AnomalyRuleKind.Event => (0, 0),
        _ => MetricInfo is { } m ? (m.Min, m.Max) : (0, 0),
    };

    /// <summary>Whether <see cref="DurationSeconds"/> applies.</summary>
    public bool UsesDuration => Kind is AnomalyRuleKind.ThresholdDuration or AnomalyRuleKind.BaselineDeviation;

    /// <summary>Whether <see cref="WindowSeconds"/> applies.</summary>
    public bool UsesWindow => Kind is AnomalyRuleKind.RateOfChange or AnomalyRuleKind.CounterSpike;

    /// <summary>The rule in plain words, e.g. "CPU utilization above 95 % for at least 30 s".</summary>
    public string Describe()
    {
        var metric = MetricInfo;
        var label = metric?.Label ?? Metric ?? "";
        var value = metric?.Format(Threshold) ?? Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var side = Direction == ThresholdDirection.Above ? "above" : "below";
        return Kind switch
        {
            AnomalyRuleKind.Threshold => $"{label} {side} {value}",
            AnomalyRuleKind.ThresholdDuration => $"{label} {side} {value} for at least {Seconds(DurationSeconds)}",
            AnomalyRuleKind.RateOfChange =>
                $"{label} {(Direction == ThresholdDirection.Below ? "drops" : "rises")} by {AnomalyMetrics.Format(Threshold, "%")} or more within {Seconds(WindowSeconds)}"
                + (AnomalyMetrics.Find(GuardMetric) is { } guard ? $" while {guard.Label} is at least {guard.Format(GuardMinimum)}" : ""),
            AnomalyRuleKind.BaselineDeviation =>
                $"{label} more than {AnomalyMetrics.Format(Threshold, "σ")} {side} its {Minutes(BaselineMinutes)} baseline for at least {Seconds(DurationSeconds)}",
            AnomalyRuleKind.CounterSpike => $"{label}: {AnomalyMetrics.Format(Threshold, "")} or more within {Seconds(WindowSeconds)}",
            _ => Description ?? Name,
        };
    }

    private static string Seconds(double seconds) => AnomalyReasoning.FormatDuration(TimeSpan.FromSeconds(seconds));

    private static string Minutes(double minutes) => AnomalyReasoning.FormatDuration(TimeSpan.FromMinutes(minutes));
}
