using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Anomalies;

/// <summary>An anomaly whose rule creates incidents, waiting for <see cref="TriggerService"/>.</summary>
/// <param name="Anomaly">The anomaly (stored).</param>
/// <param name="Rule">The rule as it was when it fired.</param>
/// <param name="FiredAt">When the rule fired (a rule with a duration fires that long after the anomaly started).</param>
public sealed record AnomalyTrigger(Anomaly Anomaly, AnomalyRule Rule, DateTimeOffset? FiredAt = null);

/// <summary>
/// Hands anomalies from the engine (a sample consumer) to the trigger service. Incident capture flushes the sample
/// bus, so it must never run on a consumer's own queue; this hop also keeps the engine free of a dependency on the
/// incident manager (which depends on the bus).
/// </summary>
public sealed class AnomalyTriggerChannel
{
    private readonly Channel<AnomalyTrigger> _channel = Channel.CreateBounded<AnomalyTrigger>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>The triggers.</summary>
    public ChannelReader<AnomalyTrigger> Reader => _channel.Reader;

    /// <summary>Queues a trigger (drops the oldest when 32 are waiting).</summary>
    public void Post(AnomalyTrigger trigger) => _channel.Writer.TryWrite(trigger);
}

/// <summary>
/// The anomaly engine (ARCHITECTURE §10): evaluates the anomaly rules on every recorded frame, stores anomalies in
/// telemetry.db with their reasoning, keeps the current ones for the live feed and queues those whose rule creates
/// incidents. Rules are the shipped anomaly-rules.json with the user's "Triggers" settings applied, reloaded when
/// settings.json changes. Frames that are not recorded (the dashboard's live view) are ignored: anomalies always
/// refer to stored data. A sweep every two seconds ends anomalies whose data stopped (recording stopped, sleep).
/// </summary>
public sealed partial class AnomalyEngine : BackgroundService, ISampleConsumer
{
    /// <summary>How long ended anomalies stay in the live list.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(10);

    /// <summary>Most anomalies in the live list.</summary>
    public const int MaxLive = 20;

    /// <summary>How often a running anomaly's duration and value are written to the database.</summary>
    public static readonly TimeSpan PersistEvery = TimeSpan.FromSeconds(30);

    private readonly IAnomalyStore _store;
    private readonly AnomalyTriggerChannel _triggers;
    private readonly IOptionsMonitor<TriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AnomalyEngine> _logger;
    private readonly DateTimeOffset _startedAt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MetricExtractor _extractor = new();
    private readonly List<MetricValue> _values = [];
    private readonly List<Signal> _signals = [];
    private readonly Dictionary<string, Detector> _detectors = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Rule, string Device), Running> _running = [];
    private readonly LinkedList<Anomaly> _recent = new();
    private readonly IDisposable? _subscription;
    private volatile bool _rulesChanged = true;
    private volatile IReadOnlyList<AnomalyInfo> _live = [];

    /// <summary>Creates the engine.</summary>
    public AnomalyEngine(IAnomalyStore store, AnomalyTriggerChannel triggers, IOptionsMonitor<TriggerOptions> options, TimeProvider timeProvider, ILogger<AnomalyEngine> logger)
    {
        _store = store;
        _triggers = triggers;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _startedAt = timeProvider.GetUtcNow();
        _subscription = options.OnChange(_ => _rulesChanged = true);
    }

    /// <inheritdoc />
    public string Name => "anomalies";

    /// <inheritdoc />
    public int QueueCapacity => 300;

    /// <summary>The rules being evaluated.</summary>
    public IReadOnlyList<AnomalyRule> Rules { get; private set; } = [];

    /// <summary>Running anomalies and those that ended in the last <see cref="RecentFor"/>, newest first.</summary>
    public IReadOnlyList<AnomalyInfo> Current => _live;

    /// <inheritdoc />
    public async ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.Recorded && !frame.Watched)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _signals.Clear();
            RefreshRules(_signals);
            _values.Clear();
            _extractor.Extract(frame, _values);
            foreach (var detector in _detectors.Values)
            {
                detector.Observe(frame, _values, _signals);
            }

            await ApplyAsync(frame.Timestamp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Records the incident created or joined for an anomaly.</summary>
    public async Task LinkIncidentAsync(Anomaly anomaly, string incidentKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (key, running) in _running)
            {
                if (running.Anomaly.Id == anomaly.Id)
                {
                    _running[key] = running with { Anomaly = running.Anomaly with { IncidentKey = incidentKey } };
                }
            }

            for (var node = _recent.First; node is not null; node = node.Next)
            {
                if (node.Value.Id == anomaly.Id)
                {
                    node.Value = node.Value with { IncidentKey = incidentKey };
                }
            }

            PublishLive(_timeProvider.GetUtcNow());
        }
        finally
        {
            _gate.Release();
        }

        if (anomaly.Id > 0)
        {
            await TryStoreAsync(() => _store.LinkIncidentAsync(anomaly.Id, incidentKey, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>Ends anomalies whose data stopped and persists running ones now and then. Exposed for tests.</summary>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            _signals.Clear();
            foreach (var detector in _detectors.Values)
            {
                detector.Expire(now, _signals);
            }

            await ApplyAsync(now, cancellationToken).ConfigureAwait(false);
            foreach (var (key, running) in _running.ToList())
            {
                if (now - running.PersistedAt >= PersistEvery && running.Anomaly.Id > 0)
                {
                    _running[key] = running with { PersistedAt = now };
                    await TryStoreAsync(() => _store.UpdateAsync(running.Anomaly, cancellationToken)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var closed = await _store.CloseAbandonedAsync(_startedAt, stoppingToken).ConfigureAwait(false);
            if (closed > 0)
            {
                LogClosedAbandoned(_logger, closed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStoreFailed(_logger, ex);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Running anomalies end where their data ends.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _signals.Clear();
            foreach (var detector in _detectors.Values)
            {
                detector.EndAll(_signals);
            }

            await ApplyAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _subscription?.Dispose();
        _gate.Dispose();
        base.Dispose();
    }

    private void RefreshRules(List<Signal> signals)
    {
        if (!_rulesChanged)
        {
            return;
        }

        _rulesChanged = false;
        List<AnomalyRule> rules;
        try
        {
            rules = AnomalyRuleCatalog.Default.Apply(_options.CurrentValue).Where(r => r.Enabled).ToList();
        }
        catch (OptionsValidationException ex)
        {
            LogInvalidSettings(_logger, ex.Message);
            if (_detectors.Count > 0)
            {
                return;
            }

            rules = AnomalyRuleCatalog.Default.Rules.Where(r => r.Enabled).ToList();
        }

        foreach (var (id, detector) in _detectors.ToList())
        {
            if (rules.FirstOrDefault(r => r.Id == id) is not { } rule || rule != detector.Rule)
            {
                detector.EndAll(signals);
                _detectors.Remove(id);
            }
        }

        foreach (var rule in rules)
        {
            if (!_detectors.ContainsKey(rule.Id))
            {
                _detectors[rule.Id] = Detector.Create(rule);
            }
        }

        Rules = rules;
        _extractor.Readings = rules.Where(r => AnomalyMetrics.IsReading(r.Metric)).Select(r => r.Metric![AnomalyMetrics.ReadingPrefix.Length..]).ToHashSet(StringComparer.Ordinal);
        LogRules(_logger, rules.Count, rules.Count(r => r.CreateIncident));
    }

    private async Task ApplyAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var signal in _signals)
        {
            // A signal from a detector that was just replaced carries its own (previous) rule.
            var rule = signal.Rule;
            var key = (rule.Id, signal.DeviceKey);
            switch (signal.Kind)
            {
                case SignalKind.Started:
                case SignalKind.Instant:
                {
                    var anomaly = new Anomaly(0, rule.Id, signal.Series, signal.StartedAt, signal.Kind == SignalKind.Instant ? signal.At : null, signal.Observed, rule.Severity,
                        signal.Reasoning, signal.Device);
                    var id = await TryStoreAsync(() => _store.AddAsync(anomaly, cancellationToken)).ConfigureAwait(false);
                    anomaly = anomaly with { Id = id };
                    LogDetected(_logger, rule.Id, signal.Device ?? "system", anomaly.Reasoning.Summary());
                    if (signal.Kind == SignalKind.Started)
                    {
                        _running[key] = new Running(anomaly, rule, now);
                    }
                    else
                    {
                        Remember(anomaly);
                    }

                    if (rule.CreateIncident && id > 0)
                    {
                        _triggers.Post(new AnomalyTrigger(anomaly, rule, now));
                    }

                    break;
                }

                case SignalKind.Ongoing when _running.TryGetValue(key, out var running):
                    _running[key] = running with { Anomaly = running.Anomaly with { Observed = signal.Observed, Reasoning = signal.Reasoning } };
                    break;

                case SignalKind.Ended when _running.Remove(key, out var running):
                {
                    var ended = running.Anomaly with { EndedAt = signal.At, Observed = signal.Observed, Reasoning = signal.Reasoning };
                    if (ended.Id > 0)
                    {
                        await TryStoreAsync(() => _store.UpdateAsync(ended, cancellationToken)).ConfigureAwait(false);
                    }

                    Remember(ended);
                    LogEnded(_logger, rule.Id, signal.Device ?? "system", AnomalyReasoning.FormatDuration(signal.Reasoning.Duration));
                    break;
                }
            }
        }

        _signals.Clear();
        PublishLive(now);
    }

    private void Remember(Anomaly anomaly)
    {
        _recent.AddFirst(anomaly);
        while (_recent.Count > MaxLive)
        {
            _recent.RemoveLast();
        }
    }

    private void PublishLive(DateTimeOffset now)
    {
        while (_recent.Last is { } last && now - (last.Value.EndedAt ?? last.Value.StartedAt) > RecentFor)
        {
            _recent.RemoveLast();
        }

        var rules = Rules;
        var unitOf = new Func<string, string>(ruleId => (rules.FirstOrDefault(r => r.Id == ruleId) ?? AnomalyRuleCatalog.Default.Find(ruleId))?.MetricInfo?.Unit ?? "");
        _live = _running.Values.Select(r => r.Anomaly)
            .OrderByDescending(a => a.StartedAt)
            .Concat(_recent)
            .Take(MaxLive)
            .Select(a => a.ToInfo(unitOf(a.RuleId)))
            .ToList();
    }

    private async Task<long> TryStoreAsync(Func<Task<long>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogStoreFailed(_logger, ex);
            return 0;
        }
    }

    private async Task TryStoreAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogStoreFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Anomaly {Rule} ({Device}): {Reasoning}")]
    private static partial void LogDetected(ILogger logger, string rule, string device, string reasoning);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Anomaly {Rule} ({Device}) ended after {Duration}")]
    private static partial void LogEnded(ILogger logger, string rule, string device, string duration);

    [LoggerMessage(Level = LogLevel.Information, Message = "Anomaly rules: {Count} enabled, {Triggers} create incidents")]
    private static partial void LogRules(ILogger logger, int count, int triggers);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The trigger settings are not valid; the previous rules stay in use: {Problem}")]
    private static partial void LogInvalidSettings(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} anomalies left open by the previous run were closed")]
    private static partial void LogClosedAbandoned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An anomaly could not be stored")]
    private static partial void LogStoreFailed(ILogger logger, Exception exception);

    private sealed record Running(Anomaly Anomaly, AnomalyRule Rule, DateTimeOffset PersistedAt);
}
