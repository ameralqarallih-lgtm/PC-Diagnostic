using System.Globalization;
using PcDiag.Core.Events;

namespace PcDiag.Engine.Events;

/// <summary>
/// Limits how many events one rule records per minute of event time, so a failing disk or a misbehaving driver
/// that logs thousands of events cannot flood storage. Events over the limit are counted, and when the minute is
/// over one summary event says how many were not recorded. The summary is explicitly the recorder's own
/// (provider "PcDiagnostic"), never presented as a Windows event. Not thread-safe; callers serialize access.
/// </summary>
public sealed class EventStormGuard
{
    /// <summary>Provider name of summary events.</summary>
    public const string SummaryProvider = "PcDiagnostic";

    private readonly int _maxPerMinute;
    private readonly Dictionary<(string Rule, long Minute), Window> _windows = [];

    /// <summary>Creates a guard.</summary>
    public EventStormGuard(int maxPerMinute) => _maxPerMinute = Math.Max(1, maxPerMinute);

    /// <summary>Records an event; false when its rule is over the limit for that minute.</summary>
    public bool Admit(string ruleId, SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        var key = (ruleId, systemEvent.Timestamp.ToUnixTimeSeconds() / 60);
        if (!_windows.TryGetValue(key, out var window))
        {
            _windows[key] = window = new Window(systemEvent);
        }

        if (window.Recorded < _maxPerMinute)
        {
            window.Recorded++;
            return true;
        }

        window.Suppressed++;
        window.Last = systemEvent.Timestamp;
        return false;
    }

    /// <summary>
    /// Closes minutes that ended before <paramref name="now"/> minus a grace period (or all, with
    /// <paramref name="all"/>) and returns one summary per minute in which events were not recorded.
    /// </summary>
    public IReadOnlyList<SystemEvent> Flush(DateTimeOffset now, bool all = false)
    {
        var cutoffMinute = (now - TimeSpan.FromSeconds(10)).ToUnixTimeSeconds() / 60;
        List<SystemEvent>? summaries = null;
        foreach (var (key, window) in _windows.Where(w => all || w.Key.Minute < cutoffMinute).ToList())
        {
            _windows.Remove(key);
            if (window.Suppressed > 0)
            {
                (summaries ??= []).Add(Summary(window));
            }
        }

        return summaries ?? (IReadOnlyList<SystemEvent>)[];
    }

    private SystemEvent Summary(Window window)
    {
        var first = window.First;
        var count = window.Suppressed.ToString(CultureInfo.InvariantCulture);
        return new SystemEvent(
            window.Last,
            first.Channel,
            null,
            SummaryProvider,
            first.EventId,
            first.Level,
            first.Category,
            first.Severity,
            $"{count} more \"{first.Title}\" events in this minute were not recorded (event storm)",
            $"{first.Provider} logged more than {_maxPerMinute.ToString(CultureInfo.InvariantCulture)} events matching rule '{first.RuleId}' within one minute. " +
            $"The first {_maxPerMinute.ToString(CultureInfo.InvariantCulture)} were recorded; {count} more were counted but not stored. The Windows Event Log still has all of them.",
            RelatedDevice: first.RelatedDevice,
            RuleId: first.RuleId,
            Data: new Dictionary<string, string>
            {
                ["suppressed"] = count,
                ["provider"] = first.Provider,
                ["eventId"] = first.EventId.ToString(CultureInfo.InvariantCulture),
            });
    }

    private sealed class Window(SystemEvent first)
    {
        public SystemEvent First { get; } = first;

        public int Recorded { get; set; }

        public int Suppressed { get; set; }

        public DateTimeOffset Last { get; set; } = first.Timestamp;
    }
}
