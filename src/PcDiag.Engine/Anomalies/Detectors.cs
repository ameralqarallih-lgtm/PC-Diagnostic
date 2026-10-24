using System.Globalization;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Anomalies;

/// <summary>What a detector reports.</summary>
internal enum SignalKind
{
    /// <summary>An anomaly began (its condition held long enough).</summary>
    Started,

    /// <summary>A running anomaly continues (new value, longer duration).</summary>
    Ongoing,

    /// <summary>A running anomaly ended.</summary>
    Ended,

    /// <summary>A one-off anomaly (an event, a sudden change).</summary>
    Instant,
}

/// <summary>A detector's report for one device.</summary>
/// <param name="Rule">The rule that produced it.</param>
/// <param name="Kind">What happened.</param>
/// <param name="DeviceKey">Device key ("" for system-wide; the subject for event rules).</param>
/// <param name="Device">Device or subject name.</param>
/// <param name="Series">The metric, or "event:…", "process:…", "display:…".</param>
/// <param name="StartedAt">When the condition started.</param>
/// <param name="At">The sample time (the end, for Ended).</param>
/// <param name="Observed">Most extreme value so far.</param>
/// <param name="Reasoning">Why.</param>
internal sealed record Signal(AnomalyRule Rule, SignalKind Kind, string DeviceKey, string? Device, string Series, DateTimeOffset StartedAt, DateTimeOffset At, double Observed, AnomalyReasoning Reasoning);

/// <summary>Evaluates one rule over the frames. Not thread-safe; the engine serializes calls.</summary>
internal abstract class Detector(AnomalyRule rule)
{
    /// <summary>Longest gap between samples of a device before its condition is reset (recording stopped, sleep).</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(15);

    /// <summary>The rule.</summary>
    public AnomalyRule Rule { get; } = rule;

    /// <summary>Creates the detector for a rule.</summary>
    public static Detector Create(AnomalyRule rule) => rule.Kind switch
    {
        AnomalyRuleKind.Threshold or AnomalyRuleKind.ThresholdDuration => new ThresholdDetector(rule),
        AnomalyRuleKind.BaselineDeviation => new BaselineDetector(rule),
        AnomalyRuleKind.RateOfChange => new RateOfChangeDetector(rule),
        AnomalyRuleKind.CounterSpike => new CounterSpikeDetector(rule),
        AnomalyRuleKind.Event => new EventDetector(rule),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Kind, "Unknown rule kind."),
    };

    /// <summary>Evaluates a frame.</summary>
    public abstract void Observe(SampleFrame frame, IReadOnlyList<MetricValue> values, List<Signal> signals);

    /// <summary>Ends running anomalies whose data stopped arriving before <paramref name="now"/> − <see cref="MaxGap"/>.</summary>
    public virtual void Expire(DateTimeOffset now, List<Signal> signals)
    {
    }

    /// <summary>Ends every running anomaly (the rule changed or was turned off).</summary>
    public virtual void EndAll(List<Signal> signals)
    {
    }

    /// <summary>A value with the metric's unit.</summary>
    protected string Format(double value) => Rule.MetricInfo?.Format(value) ?? value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Reasoning without a baseline.</summary>
    protected AnomalyReasoning Reasoning(string current, TimeSpan duration, string? baseline = null, double? mean = null, double? sd = null) =>
        new(Rule.Classification, Rule.Describe(), baseline, mean, sd, current, duration);
}

/// <summary>Per-device detector state.</summary>
internal class DeviceState
{
    /// <summary>Device key.</summary>
    public string DeviceKey { get; init; } = "";

    /// <summary>Device name.</summary>
    public string? Device { get; init; }

    /// <summary>Last sample time.</summary>
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.MinValue;
}

/// <summary>State of a "condition held for a duration" detector.</summary>
internal class SustainedState : DeviceState
{
    /// <summary>When the condition began.</summary>
    public DateTimeOffset? Since { get; set; }

    /// <summary>When the condition last became clear while active.</summary>
    public DateTimeOffset? ClearSince { get; set; }

    /// <summary>Whether the anomaly has started.</summary>
    public bool Active { get; set; }

    /// <summary>Most extreme value while the condition held.</summary>
    public double Peak { get; set; }

    /// <summary>Latest value.</summary>
    public double Last { get; set; }

    /// <summary>Forgets the condition.</summary>
    public void Reset()
    {
        Since = null;
        ClearSince = null;
        Active = false;
    }
}

/// <summary>A detector that keeps state per device for one metric.</summary>
internal abstract class MetricDetector<TState>(AnomalyRule rule) : Detector(rule)
    where TState : DeviceState, new()
{
    private readonly Dictionary<string, TState> _devices = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override void Observe(SampleFrame frame, IReadOnlyList<MetricValue> values, List<Signal> signals)
    {
        foreach (var value in values)
        {
            if (value.Metric != Rule.Metric)
            {
                continue;
            }

            if (!_devices.TryGetValue(value.DeviceKey, out var state))
            {
                state = new TState { DeviceKey = value.DeviceKey, Device = value.Device };
                _devices[value.DeviceKey] = state;
            }
            else if (frame.Timestamp - state.LastSeen > MaxGap)
            {
                Interrupt(state, signals);
            }

            Observe(state, frame.Timestamp, value.Value, values, signals);
            state.LastSeen = frame.Timestamp;
        }
    }

    /// <inheritdoc />
    public override void Expire(DateTimeOffset now, List<Signal> signals)
    {
        foreach (var state in _devices.Values)
        {
            if (now - state.LastSeen > MaxGap)
            {
                Interrupt(state, signals);
            }
        }
    }

    /// <inheritdoc />
    public override void EndAll(List<Signal> signals)
    {
        foreach (var state in _devices.Values)
        {
            Interrupt(state, signals);
        }
    }

    /// <summary>Evaluates one value.</summary>
    protected abstract void Observe(TState state, DateTimeOffset ts, double value, IReadOnlyList<MetricValue> frameValues, List<Signal> signals);

    /// <summary>The data stopped: end a running anomaly at the last sample and forget the condition (baselines stay).</summary>
    protected abstract void Interrupt(TState state, List<Signal> signals);

    /// <summary>Whether a value is on the abnormal side of a limit.</summary>
    protected bool Breaches(double value, double limit) => Rule.Direction == ThresholdDirection.Above ? value > limit : value < limit;

    /// <summary>The more abnormal of two values.</summary>
    protected double Extreme(double a, double b) => Rule.Direction == ThresholdDirection.Above ? Math.Max(a, b) : Math.Min(a, b);
}

/// <summary>
/// Shared state machine for "condition held for a duration" rules: the anomaly starts (dated from when the
/// condition began) once the condition has held for <see cref="AnomalyRule.DurationSeconds"/>, and ends after the
/// condition has been clear for <see cref="ClearAfter"/>, so a value hovering at the threshold is one anomaly.
/// </summary>
internal abstract class SustainedDetector<TState>(AnomalyRule rule) : MetricDetector<TState>(rule)
    where TState : SustainedState, new()
{
    /// <summary>How long the condition must be clear before a running anomaly ends.</summary>
    public static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(5);

    /// <summary>Steps the state machine; <paramref name="describe"/> builds the reasoning.</summary>
    protected void Step(TState state, DateTimeOffset ts, double value, bool condition, List<Signal> signals, Func<TState, DateTimeOffset, AnomalyReasoning> describe)
    {
        if (condition)
        {
            state.ClearSince = null;
            if (state.Since is null)
            {
                state.Since = ts;
                state.Peak = value;
            }
            else
            {
                state.Peak = Extreme(state.Peak, value);
            }

            state.Last = value;
            if (state.Active)
            {
                signals.Add(Emit(SignalKind.Ongoing, state, ts, describe));
            }
            else if (ts - state.Since.Value >= TimeSpan.FromSeconds(Rule.DurationSeconds))
            {
                state.Active = true;
                signals.Add(Emit(SignalKind.Started, state, ts, describe));
            }

            return;
        }

        if (!state.Active)
        {
            state.Since = null;
            return;
        }

        state.ClearSince ??= ts;
        state.Last = value;
        if (ts - state.ClearSince.Value >= ClearAfter)
        {
            signals.Add(Emit(SignalKind.Ended, state, state.ClearSince.Value, describe));
            state.Reset();
        }
        else
        {
            signals.Add(Emit(SignalKind.Ongoing, state, ts, describe));
        }
    }

    /// <inheritdoc />
    protected override void Interrupt(TState state, List<Signal> signals)
    {
        if (state.Active && state.LastSeen != DateTimeOffset.MinValue)
        {
            signals.Add(Emit(SignalKind.Ended, state, state.ClearSince ?? state.LastSeen, Describe));
        }

        state.Reset();
    }

    /// <summary>The reasoning for the current state.</summary>
    protected abstract AnomalyReasoning Describe(TState state, DateTimeOffset at);

    private Signal Emit(SignalKind kind, TState state, DateTimeOffset at, Func<TState, DateTimeOffset, AnomalyReasoning> describe) =>
        new(Rule, kind, state.DeviceKey, state.Device, Rule.Metric!, state.Since!.Value, at, state.Peak, describe(state, at));
}

/// <summary>Threshold and ThresholdDuration rules.</summary>
internal sealed class ThresholdDetector(AnomalyRule rule) : SustainedDetector<ThresholdDetector.State>(rule)
{
    /// <inheritdoc />
    protected override void Observe(State state, DateTimeOffset ts, double value, IReadOnlyList<MetricValue> frameValues, List<Signal> signals) =>
        Step(state, ts, value, Breaches(value, Rule.Threshold), signals, Describe);

    /// <inheritdoc />
    protected override AnomalyReasoning Describe(State state, DateTimeOffset at)
    {
        var current = Format(state.Last);
        if (Math.Abs(state.Peak - state.Last) > double.Epsilon)
        {
            current += $" (peak {Format(state.Peak)})";
        }

        return Reasoning(current, at - state.Since!.Value);
    }

    /// <summary>State.</summary>
    internal sealed class State : SustainedState;
}

/// <summary>
/// BaselineDeviation rules: an exponentially weighted moving average and variance with a time constant of
/// <see cref="AnomalyRule.BaselineMinutes"/>. The baseline is trusted after <see cref="AnomalyRule.WarmupMinutes"/>
/// and is not updated while the condition holds (for at most <see cref="MaxFreeze"/>), so an anomaly is not
/// absorbed into "normal" while it is happening, yet a lasting change in load becomes the new normal.
/// </summary>
internal sealed class BaselineDetector(AnomalyRule rule) : SustainedDetector<BaselineDetector.State>(rule)
{
    /// <summary>Longest the baseline is frozen while the condition holds.</summary>
    public static readonly TimeSpan MaxFreeze = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    protected override void Observe(State state, DateTimeOffset ts, double value, IReadOnlyList<MetricValue> frameValues, List<Signal> signals)
    {
        if (state.First is null || ts - state.LastSeen > TimeSpan.FromMinutes(Rule.BaselineMinutes))
        {
            // First sample, or the data stopped for longer than the baseline spans: start learning again.
            state.First = ts;
            state.Mean = value;
            state.Variance = 0;
            state.Reset();
            return;
        }

        var trusted = ts - state.First.Value >= TimeSpan.FromMinutes(Rule.WarmupMinutes);
        var sd = Math.Max(Math.Sqrt(state.Variance), Rule.MinStdDev);
        var z = (Rule.Direction == ThresholdDirection.Above ? value - state.Mean : state.Mean - value) / sd;
        state.Z = z;
        Step(state, ts, value, trusted && z > Rule.Threshold, signals, Describe);

        var frozen = state.Since is { } since && ts - since < MaxFreeze;
        if (!frozen)
        {
            var dt = Math.Max(0, (ts - state.LastSeen).TotalSeconds);
            var alpha = 1 - Math.Exp(-dt / (Rule.BaselineMinutes * 60));
            var diff = value - state.Mean;
            var increment = alpha * diff;
            state.Mean += increment;
            state.Variance = (1 - alpha) * (state.Variance + (diff * increment));
        }
    }

    /// <inheritdoc />
    protected override AnomalyReasoning Describe(State state, DateTimeOffset at)
    {
        var sd = Math.Max(Math.Sqrt(state.Variance), Rule.MinStdDev);
        var unit = Rule.MetricInfo?.Unit ?? "";
        var low = Math.Max(0, state.Mean - (2 * sd));
        var high = unit == "%" ? Math.Min(100, state.Mean + (2 * sd)) : state.Mean + (2 * sd);
        var baseline = string.Create(CultureInfo.InvariantCulture,
            $"{AnomalyMetrics.Format(low, "")}–{Format(high)} (mean {state.Mean:0.#}, sd {sd:0.#}, {AnomalyReasoning.FormatDuration(TimeSpan.FromMinutes(Rule.BaselineMinutes))})");
        var current = string.Create(CultureInfo.InvariantCulture, $"{Format(state.Last)} (z = {state.Z:0.#})");
        if (Math.Abs(state.Peak - state.Last) > double.Epsilon)
        {
            current += $", peak {Format(state.Peak)}";
        }

        return Reasoning(current, at - state.Since!.Value, baseline, Math.Round(state.Mean, 2), Math.Round(sd, 2));
    }

    /// <summary>State.</summary>
    internal sealed class State : SustainedState
    {
        /// <summary>When learning started.</summary>
        public DateTimeOffset? First { get; set; }

        /// <summary>Moving average.</summary>
        public double Mean { get; set; }

        /// <summary>Moving variance.</summary>
        public double Variance { get; set; }

        /// <summary>Latest z-score.</summary>
        public double Z { get; set; }
    }
}

/// <summary>
/// RateOfChange rules: the value fell (or rose) by at least <see cref="AnomalyRule.Threshold"/> percent compared
/// with the highest (lowest) value in the previous <see cref="AnomalyRule.WindowSeconds"/>, optionally only while
/// a guard metric of the same device is high enough. Reported once per change.
/// </summary>
internal sealed class RateOfChangeDetector(AnomalyRule rule) : MetricDetector<RateOfChangeDetector.State>(rule)
{
    /// <inheritdoc />
    protected override void Observe(State state, DateTimeOffset ts, double value, IReadOnlyList<MetricValue> frameValues, List<Signal> signals)
    {
        var window = TimeSpan.FromSeconds(Rule.WindowSeconds);
        while (state.Window.Count > 0 && state.Window.Peek().Timestamp < ts - window)
        {
            state.Window.Dequeue();
        }

        if (state.Window.Count > 0 && ts - state.LastFired > window)
        {
            var drop = Rule.Direction == ThresholdDirection.Below;
            // The latest of equal extremes, so the change is dated as closely as possible.
            var recent = state.Window.Reverse();
            var reference = drop ? recent.MaxBy(p => p.Value) : recent.MinBy(p => p.Value);
            var change = reference.Value > 0 ? (drop ? reference.Value - value : value - reference.Value) / reference.Value * 100 : 0;
            if (change >= Rule.Threshold && GuardHolds(state.DeviceKey, frameValues, out var guard))
            {
                state.LastFired = ts;
                var current = string.Create(CultureInfo.InvariantCulture,
                    $"{Format(reference.Value)} → {Format(value)} ({(drop ? "−" : "+")}{change:0} % in {AnomalyReasoning.FormatDuration(ts - reference.Timestamp)})");
                if (guard is { } g && AnomalyMetrics.Find(Rule.GuardMetric) is { } guardMetric)
                {
                    current += $"; {guardMetric.Label} {guardMetric.Format(g)}";
                }

                signals.Add(new Signal(Rule, SignalKind.Instant, state.DeviceKey, state.Device, Rule.Metric!, reference.Timestamp, ts, value, Reasoning(current, ts - reference.Timestamp)));
            }
        }

        state.Window.Enqueue(new MetricPoint(ts, value));
    }

    /// <inheritdoc />
    protected override void Interrupt(State state, List<Signal> signals) => state.Window.Clear();

    private bool GuardHolds(string deviceKey, IReadOnlyList<MetricValue> frameValues, out double? guard)
    {
        guard = null;
        if (Rule.GuardMetric is null)
        {
            return true;
        }

        foreach (var value in frameValues)
        {
            if (value.Metric == Rule.GuardMetric && value.DeviceKey == deviceKey)
            {
                guard = value.Value;
                return value.Value >= Rule.GuardMinimum;
            }
        }

        return false;
    }

    /// <summary>State.</summary>
    internal sealed class State : DeviceState
    {
        /// <summary>Recent values.</summary>
        public Queue<MetricPoint> Window { get; } = new();

        /// <summary>When the rule last fired for this device.</summary>
        public DateTimeOffset LastFired { get; set; } = DateTimeOffset.MinValue;
    }
}

/// <summary>CounterSpike rules: per-sample counts add up to at least the threshold within the window.</summary>
internal sealed class CounterSpikeDetector(AnomalyRule rule) : MetricDetector<CounterSpikeDetector.State>(rule)
{
    /// <inheritdoc />
    protected override void Observe(State state, DateTimeOffset ts, double value, IReadOnlyList<MetricValue> frameValues, List<Signal> signals)
    {
        var window = TimeSpan.FromSeconds(Rule.WindowSeconds);
        state.Window.Enqueue(new MetricPoint(ts, value));
        while (state.Window.Peek().Timestamp <= ts - window)
        {
            state.Window.Dequeue();
        }

        var sum = state.Window.Sum(p => p.Value);
        if (sum >= Rule.Threshold)
        {
            if (!state.Active)
            {
                state.Active = true;
                state.Since = state.Window.FirstOrDefault(p => p.Value > 0).Timestamp;
                if (state.Since == default)
                {
                    state.Since = ts;
                }

                state.Peak = sum;
                signals.Add(Emit(SignalKind.Started, state, ts, sum));
            }
            else
            {
                state.Peak = Math.Max(state.Peak, sum);
                signals.Add(Emit(SignalKind.Ongoing, state, ts, sum));
            }
        }
        else if (state.Active)
        {
            signals.Add(Emit(SignalKind.Ended, state, ts, sum));
            state.Active = false;
        }
    }

    /// <inheritdoc />
    protected override void Interrupt(State state, List<Signal> signals)
    {
        if (state.Active)
        {
            signals.Add(Emit(SignalKind.Ended, state, state.LastSeen, state.Window.Sum(p => p.Value)));
        }

        state.Active = false;
        state.Window.Clear();
    }

    private Signal Emit(SignalKind kind, State state, DateTimeOffset at, double sum)
    {
        var current = string.Create(CultureInfo.InvariantCulture, $"{AnomalyMetrics.Format(sum, "")} in the last {AnomalyReasoning.FormatDuration(TimeSpan.FromSeconds(Rule.WindowSeconds))}");
        if (state.Peak > sum)
        {
            current += string.Create(CultureInfo.InvariantCulture, $" (peak {state.Peak:0})");
        }

        return new Signal(Rule, kind, state.DeviceKey, state.Device, Rule.Metric!, state.Since, at, state.Peak, Reasoning(current, at - state.Since));
    }

    /// <summary>State.</summary>
    internal sealed class State : DeviceState
    {
        /// <summary>Counts in the window.</summary>
        public Queue<MetricPoint> Window { get; } = new();

        /// <summary>Whether an anomaly is running.</summary>
        public bool Active { get; set; }

        /// <summary>When it started.</summary>
        public DateTimeOffset Since { get; set; }

        /// <summary>Highest sum.</summary>
        public double Peak { get; set; }
    }
}

/// <summary>
/// Event rules: classified Windows events (by rule ID), process crashes and hangs, and display changes. Events
/// older than <see cref="MaxEventAge"/> (backfilled when recording starts) are ignored, and the same subject (a
/// process, provider or display) is reported once per <see cref="DedupeWindow"/>, so a crash seen both as a
/// process exit and as a Windows Error Reporting event is one anomaly.
/// </summary>
internal sealed class EventDetector(AnomalyRule rule) : Detector(rule)
{
    /// <summary>Oldest event (relative to the frame) that still counts.</summary>
    public static readonly TimeSpan MaxEventAge = TimeSpan.FromMinutes(5);

    /// <summary>The same subject is reported once within this window.</summary>
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override void Observe(SampleFrame frame, IReadOnlyList<MetricValue> values, List<Signal> signals)
    {
        foreach (var e in frame.Events ?? [])
        {
            if (e.RuleId is { } id && Matches(id) && e.Timestamp >= frame.Timestamp - MaxEventAge)
            {
                var subject = e.ProcessName ?? e.RelatedDevice ?? e.Provider;
                Report(signals, subject, $"event:{id}", e.Timestamp, e.EventId,
                    string.Create(CultureInfo.InvariantCulture, $"{e.Title} (Event {e.EventId}, {e.Provider})"));
            }
        }

        foreach (var p in frame.Processes?.Events ?? [])
        {
            if (Rule.ProcessEvents.Contains(p.Kind) && p.Timestamp >= frame.Timestamp - MaxEventAge && NameMatches(Rule.ProcessNames, p.Name))
            {
                var what = p.Kind switch
                {
                    ProcessEventKind.Crashed => "crashed",
                    ProcessEventKind.Hung => "stopped responding",
                    _ => p.Kind.ToString().ToLowerInvariant(),
                };
                Report(signals, p.Name, $"process:{p.Kind}", p.Timestamp, (int)p.Kind,
                    p.Detail is { Length: > 0 } detail ? $"{p.Name} {what} ({detail})" : $"{p.Name} {what}");
            }
        }

        foreach (var c in frame.Display?.Changes ?? [])
        {
            if (Rule.DisplayChanges.Contains(c.Kind) && c.Timestamp >= frame.Timestamp - MaxEventAge && NameMatches(Rule.DisplayTargets, c.Target))
            {
                var target = c.Target ?? "Display";
                var change = c.OldValue is not null && c.NewValue is not null ? $" ({c.OldValue} → {c.NewValue})" : "";
                Report(signals, $"{target}/{c.Kind}", $"display:{c.Kind}", c.Timestamp, (int)c.Kind, $"{target}: {Describe(c.Kind)}{change}", target);
            }
        }
    }

    private static string Describe(DisplayEventKind kind) => kind switch
    {
        DisplayEventKind.Connected => "connected",
        DisplayEventKind.Disconnected => "disconnected",
        DisplayEventKind.TopologyChanged => "layout changed",
        DisplayEventKind.ResolutionChanged => "resolution changed",
        DisplayEventKind.RefreshRateChanged => "refresh rate changed",
        DisplayEventKind.HdrChanged => "HDR changed",
        DisplayEventKind.OrientationChanged => "orientation changed",
        DisplayEventKind.PowerStateChanged => "power state changed",
        DisplayEventKind.Reconfigured => "display change with no configuration change",
        _ => "changed",
    };

    /// <summary>
    /// Whether a program or display is one of <paramref name="names"/> (none: any). Case-insensitive; "*" matches any
    /// text; a program also matches without its ".exe".
    /// </summary>
    internal static bool NameMatches(IReadOnlyList<string> names, string? name)
    {
        if (names.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        foreach (var pattern in names)
        {
            var p = pattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? pattern[..^4] : pattern;
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            if (System.Text.RegularExpressions.Regex.IsMatch(bare, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
                || System.Text.RegularExpressions.Regex.IsMatch(name, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
            {
                return true;
            }
        }

        return false;
    }

    private bool Matches(string eventRuleId)
    {
        foreach (var pattern in Rule.EventRules)
        {
            if (pattern.EndsWith('*')
                    ? eventRuleId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(eventRuleId, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void Report(List<Signal> signals, string subject, string series, DateTimeOffset at, double observed, string current, string? device = null)
    {
        if (_recent.TryGetValue(subject, out var last) && (at - last).Duration() < DedupeWindow)
        {
            return;
        }

        _recent[subject] = at;
        if (_recent.Count > 256)
        {
            foreach (var old in _recent.Where(r => at - r.Value > DedupeWindow).Select(r => r.Key).ToList())
            {
                _recent.Remove(old);
            }
        }

        signals.Add(new Signal(Rule, SignalKind.Instant, subject, device ?? subject, series, at, at, observed, Reasoning(current, TimeSpan.Zero)));
    }
}
