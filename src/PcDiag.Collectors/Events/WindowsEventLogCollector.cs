using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Events;
using PcDiag.Platform.Windows.EventLog;

namespace PcDiag.Collectors.Events;

/// <summary>
/// Records classified Windows Event Log entries. For each channel the rules use (System, Application,
/// WLAN-AutoConfig/Operational) it subscribes with a query generated from the rules, so Windows only delivers
/// candidate events. It subscribes first and then reads back the last <see cref="EventOptions.BackfillMinutes"/>
/// (and, for "sinceBoot" rules, back to the last boot), so nothing between the two is lost; records seen twice
/// are de-duplicated by (channel, record ID). Reading these channels needs no administrator rights.
/// </summary>
public sealed partial class WindowsEventLogCollector : IEventCollector
{
    private const int OutputCapacity = 16_384;
    private static readonly TimeSpan StormFlushInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] ServiceAccounts = ["SYSTEM", "LOCAL SERVICE", "NETWORK SERVICE"];

    private readonly EventRuleProvider _rules;
    private readonly IOptionsMonitor<EventOptions> _options;
    private readonly IOptionsMonitor<PrivacyOptions> _privacy;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WindowsEventLogCollector> _logger;

    /// <summary>Creates the collector.</summary>
    public WindowsEventLogCollector(
        EventRuleProvider rules,
        IOptionsMonitor<EventOptions> options,
        IOptionsMonitor<PrivacyOptions> privacy,
        TimeProvider timeProvider,
        ILogger<WindowsEventLogCollector> logger)
    {
        _rules = rules;
        _options = options;
        _privacy = privacy;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "windows-events";

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Windows events", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_privacy.CurrentValue.CollectWindowsEvents)
        {
            return ValueTask.FromResult(new CollectorStatus(Availability.Disabled, "Windows event collection is turned off in the privacy settings"));
        }

        var rules = _rules.Load();
        return ValueTask.FromResult(new CollectorStatus(Availability.Available, $"{rules.Rules.Count} rules on {string.Join(", ", rules.Channels)}"));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SystemEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_privacy.CurrentValue.CollectWindowsEvents)
        {
            yield break;
        }

        var options = _options.CurrentValue;
        var version = _rules.Version;
        var rules = _rules.Load();
        var session = new Session(new EventClassifier(rules, CreateRedactor(_privacy.CurrentValue), options.MaxMessageLength), new EventStormGuard(options.MaxPerRulePerMinute), this);
        var subscriptions = new List<EventLogSubscription>();
        try
        {
            foreach (var channel in rules.Channels)
            {
                if (EventQueryBuilder.Build(rules, channel) is not { } query)
                {
                    continue;
                }

                var subscription = new EventLogSubscription(channel, query, session.Handle, ex => LogSubscriptionError(_logger, ex, channel));
                try
                {
                    subscription.Start();
                    subscriptions.Add(subscription);
                }
                catch (EventChannelException ex)
                {
                    subscription.Dispose();
                    LogChannelUnavailable(_logger, channel, ex.Problem, ex.InnerException?.Message ?? ex.Message);
                }
            }

            LogSubscribed(_logger, subscriptions.Count, rules.Channels.Count, rules.Rules.Count);

            // Backfill after subscribing, off this thread; overlapping records are de-duplicated.
            var backfill = Task.Run(() => Backfill(rules, options, session, cancellationToken), cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                // The user's own rules changed which Windows events are watched: subscribe again with the new rules.
                if (_rules.Version != version)
                {
                    version = _rules.Version;
                    rules = _rules.Load();
                    foreach (var subscription in subscriptions)
                    {
                        subscription.Dispose();
                    }

                    subscriptions.Clear();
                    session = new Session(new EventClassifier(rules, CreateRedactor(_privacy.CurrentValue), options.MaxMessageLength), new EventStormGuard(options.MaxPerRulePerMinute), this);
                    foreach (var channel in rules.Channels)
                    {
                        if (EventQueryBuilder.Build(rules, channel) is not { } query)
                        {
                            continue;
                        }

                        var subscription = new EventLogSubscription(channel, query, session.Handle, ex => LogSubscriptionError(_logger, ex, channel));
                        try
                        {
                            subscription.Start();
                            subscriptions.Add(subscription);
                        }
                        catch (EventChannelException ex)
                        {
                            subscription.Dispose();
                            LogChannelUnavailable(_logger, channel, ex.Problem, ex.InnerException?.Message ?? ex.Message);
                        }
                    }

                    LogSubscribed(_logger, subscriptions.Count, rules.Channels.Count, rules.Rules.Count);
                }

                while (session.Output.Reader.TryRead(out var systemEvent))
                {
                    yield return systemEvent;
                }

                foreach (var summary in session.FlushStorms(_timeProvider.GetUtcNow(), all: false))
                {
                    yield return summary;
                }

                await WaitForEventsAsync(session.Output.Reader, cancellationToken).ConfigureAwait(false);
            }

            await Task.WhenAny(backfill).ConfigureAwait(false);
        }
        finally
        {
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    /// <summary>The account and computer names to hide, unless user-name capture is on.</summary>
    internal static EventRedactor CreateRedactor(PrivacyOptions privacy)
    {
        if (privacy.CaptureUserNames)
        {
            return EventRedactor.None;
        }

        var profileFolder = Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'));
        var names = new[] { Environment.UserName, profileFolder }
            .Where(n => !string.IsNullOrWhiteSpace(n) && !n.EndsWith('$') && !ServiceAccounts.Contains(n, StringComparer.OrdinalIgnoreCase));
        return EventRedactor.For(names, Environment.MachineName);
    }

    private async Task WaitForEventsAsync(ChannelReader<SystemEvent> reader, CancellationToken cancellationToken)
    {
        // Wake up for new events, or every few seconds to close storm windows.
        using var timeout = new CancellationTokenSource(StormFlushInterval, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout: loop to flush storm summaries.
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    private void Backfill(EventRuleSet rules, EventOptions options, Session session, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var window = TimeSpan.FromMinutes(options.BackfillMinutes);
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64) + TimeSpan.FromMinutes(2);
        var sinceBoot = uptime < TimeSpan.FromHours(options.BootBackfillMaxHours) ? uptime : TimeSpan.FromHours(options.BootBackfillMaxHours);
        var read = 0;
        var before = session.Recorded;

        foreach (var channel in rules.Channels)
        {
            try
            {
                if (window > TimeSpan.Zero && EventQueryBuilder.Build(rules, channel, window) is { } recent)
                {
                    read += EventLogQueries.Read(channel, recent, session.Handle, cancellationToken);
                }

                if (sinceBoot > window && EventQueryBuilder.Build(rules, channel, sinceBoot, sinceBootRulesOnly: true) is { } boot)
                {
                    read += EventLogQueries.Read(channel, boot, session.Handle, cancellationToken);
                }
            }
            catch (EventChannelException ex)
            {
                LogBackfillFailed(_logger, channel, ex.Problem, ex.InnerException?.Message ?? ex.Message);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        LogBackfilled(_logger, read, session.Recorded - before, options.BackfillMinutes, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscribed to {Subscribed} of {Channels} event log channels ({Rules} rules)")]
    private static partial void LogSubscribed(ILogger logger, int subscribed, int channels, int rules);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log channel {Channel} is not available: {Problem} ({Detail})")]
    private static partial void LogChannelUnavailable(ILogger logger, string channel, ChannelProblem problem, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event log subscription on {Channel} reported an error")]
    private static partial void LogSubscriptionError(ILogger logger, Exception exception, string channel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read back events from {Channel}: {Problem} ({Detail})")]
    private static partial void LogBackfillFailed(ILogger logger, string channel, ChannelProblem problem, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "Read back {Read} candidate events ({Recorded} recorded; window {Minutes} min plus since-boot rules) in {Ms:F0} ms")]
    private static partial void LogBackfilled(ILogger logger, int read, long recorded, int minutes, double ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event storm: {Count} events for rule {Rule} were counted but not stored")]
    private static partial void LogStorm(ILogger logger, string count, string rule);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not classify an event from {Channel}")]
    private static partial void LogClassifyFailed(ILogger logger, Exception exception, string channel);

    /// <summary>Per-recording state shared by the subscription callbacks and the backfill thread.</summary>
    private sealed class Session
    {
        private const int DedupeCapacity = 32_768;

        private readonly EventClassifier _classifier;
        private readonly EventStormGuard _guard;
        private readonly WindowsEventLogCollector _owner;
        private readonly Lock _gate = new();
        private readonly HashSet<(string Channel, long RecordId)> _seen = [];
        private readonly Queue<(string Channel, long RecordId)> _seenOrder = new();
        private long _recorded;

        public Session(EventClassifier classifier, EventStormGuard guard, WindowsEventLogCollector owner)
        {
            _classifier = classifier;
            _guard = guard;
            _owner = owner;
            Output = Channel.CreateBounded<SystemEvent>(new BoundedChannelOptions(OutputCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public Channel<SystemEvent> Output { get; }

        public long Recorded => Interlocked.Read(ref _recorded);

        public void Handle(EventRecordView view)
        {
            EventRecordData? record = null;
            try
            {
                record = EventXml.Parse(view.ToXml());
                if (record is null || !FirstSighting(record))
                {
                    return;
                }

                if (_classifier.Classify(record, view.FormatMessage) is not { } classified)
                {
                    return;
                }

                bool admitted;
                lock (_gate)
                {
                    admitted = _guard.Admit(classified.Rule.Id, classified.Event);
                }

                if (admitted && Output.Writer.TryWrite(classified.Event))
                {
                    Interlocked.Increment(ref _recorded);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogClassifyFailed(_owner._logger, ex, record?.Channel ?? "unknown");
            }
        }

        public IReadOnlyList<SystemEvent> FlushStorms(DateTimeOffset now, bool all)
        {
            IReadOnlyList<SystemEvent> summaries;
            lock (_gate)
            {
                summaries = _guard.Flush(now, all);
            }

            foreach (var summary in summaries)
            {
                LogStorm(_owner._logger, summary.Data?["suppressed"] ?? "?", summary.RuleId ?? "?");
            }

            return summaries;
        }

        private bool FirstSighting(EventRecordData record)
        {
            if (record.RecordId is not { } id)
            {
                return true;
            }

            var key = (record.Channel, id);
            lock (_gate)
            {
                if (!_seen.Add(key))
                {
                    return false;
                }

                _seenOrder.Enqueue(key);
                if (_seenOrder.Count > DedupeCapacity)
                {
                    _seen.Remove(_seenOrder.Dequeue());
                }

                return true;
            }
        }
    }
}
