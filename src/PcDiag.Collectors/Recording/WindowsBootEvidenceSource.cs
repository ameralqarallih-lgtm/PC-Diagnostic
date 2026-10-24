using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Engine.Events;
using PcDiag.Platform.Windows.EventLog;

namespace PcDiag.Collectors.Recording;

/// <summary>
/// Reads the System log since a point in time and classifies it with the event rules (Phase 7), keeping what
/// explains how a previous run ended: unexpected shutdowns, bug checks and power events (shutdown requested,
/// Windows started/stopped, sleep). The boot time comes from the system uptime.
/// </summary>
public sealed class WindowsBootEvidenceSource(EventRuleProvider rules, TimeProvider timeProvider) : IBootEvidenceSource
{
    /// <summary>Longest look-back (a previous run that ended long ago is not worth reading the whole log for).</summary>
    public static readonly TimeSpan MaxLookBack = TimeSpan.FromDays(30);

    /// <inheritdoc />
    public DateTimeOffset BootTime => timeProvider.GetUtcNow() - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <inheritdoc />
    public Task<IReadOnlyList<SystemEvent>> ReadSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<SystemEvent>>(() => Read(since, cancellationToken), cancellationToken);

    private List<SystemEvent> Read(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var ruleSet = rules.Load();
        var lookBack = timeProvider.GetUtcNow() - since;
        lookBack = lookBack < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : lookBack > MaxLookBack ? MaxLookBack : lookBack;
        if (EventQueryBuilder.Build(ruleSet, "System", lookBack) is not { } query)
        {
            return [];
        }

        var classifier = new EventClassifier(ruleSet, EventRedactor.None, 500);
        var events = new List<SystemEvent>();
        EventLogQueries.Read("System", query, view =>
        {
            if (EventXml.Parse(view.ToXml()) is { } record
                && record.Timestamp >= since
                && classifier.Classify(record, null) is { Event: var e }
                && e.Category is EventCategory.UnexpectedShutdown or EventCategory.BugCheck or EventCategory.Power)
            {
                events.Add(e);
            }
        }, cancellationToken);

        return events;
    }
}
