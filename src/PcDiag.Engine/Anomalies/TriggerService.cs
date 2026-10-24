using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
namespace PcDiag.Engine.Anomalies;

/// <summary>
/// Automatic incident triggers (PROJECT §11): creates an incident for each anomaly whose rule asks for one, within
/// limits: the master switch (<see cref="TriggerOptions.AutomaticIncidents"/>), the rule's cooldown, and at most
/// <see cref="TriggerOptions.MaxIncidentsPerHour"/> new incidents. A trigger that arrives while an incident is still
/// recording joins it as an extra trigger instead (this does not count against the hourly limit). The incident is
/// titled with the rule and what was observed; it never claims a cause.
/// </summary>
public sealed partial class TriggerService : BackgroundService
{
    /// <summary>
    /// A trigger that waited longer than this for its turn (a long capture before) no longer creates an incident. Counted
    /// from when the rule fired, not from when the anomaly started: a rule with a duration fires that long after.
    /// </summary>
    public static readonly TimeSpan MaxTriggerAge = TimeSpan.FromMinutes(2);

    /// <summary>Longest incident title.</summary>
    public const int MaxTitleLength = 120;

    private readonly AnomalyTriggerChannel _queue;
    private readonly AnomalyEngine _engine;
    private readonly IIncidentCapture _capture;
    private readonly IOptionsMonitor<TriggerOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TriggerService> _logger;
    private readonly Dictionary<string, DateTimeOffset> _lastByRule = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _created = new();
    private TriggerOptions _lastGood = new();

    /// <summary>Creates the service.</summary>
    public TriggerService(AnomalyTriggerChannel queue, AnomalyEngine engine, IIncidentCapture capture, IOptionsMonitor<TriggerOptions> options, TimeProvider timeProvider,
        ILogger<TriggerService> logger)
    {
        _queue = queue;
        _engine = engine;
        _capture = capture;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>What happened to a trigger.</summary>
    internal enum Outcome
    {
        /// <summary>A new incident was created.</summary>
        Created,

        /// <summary>Joined the incident still recording.</summary>
        Joined,

        /// <summary>Automatic incidents are turned off.</summary>
        Off,

        /// <summary>The rule created an incident less than its cooldown ago.</summary>
        Cooldown,

        /// <summary>The hourly limit was reached.</summary>
        Limit,

        /// <summary>The anomaly is too old.</summary>
        Stale,
    }

    /// <summary>The incident title for a trigger, e.g. "Sudden GPU clock drop: 2100 MHz → 300 MHz (−86 % in 2 s)".</summary>
    public static string TitleFor(AnomalyTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        var device = trigger.Anomaly.Device is { Length: > 0 } d && trigger.Rule.Kind != AnomalyRuleKind.Event ? $" ({d})" : "";
        var title = $"{trigger.Rule.Name}{device}: {trigger.Anomaly.Reasoning.Current}";
        return title.Length <= MaxTitleLength ? title : string.Concat(title.AsSpan(0, MaxTitleLength - 1), "…");
    }

    /// <summary>Handles one trigger. Exposed for tests.</summary>
    internal async Task<Outcome> HandleAsync(AnomalyTrigger trigger, CancellationToken cancellationToken)
    {
        var options = Options();
        var now = _timeProvider.GetUtcNow();
        var rule = trigger.Rule;
        if (!options.AutomaticIncidents)
        {
            LogSkipped(_logger, rule.Id, "automatic incidents are turned off");
            return Outcome.Off;
        }

        if (now - (trigger.FiredAt ?? trigger.Anomaly.EndedAt ?? trigger.Anomaly.StartedAt) > MaxTriggerAge)
        {
            LogSkipped(_logger, rule.Id, "the anomaly is too old");
            return Outcome.Stale;
        }

        if (_lastByRule.TryGetValue(rule.Id, out var last) && now - last < TimeSpan.FromMinutes(rule.CooldownMinutes))
        {
            LogSkipped(_logger, rule.Id, $"cooldown ({rule.CooldownMinutes} min) since the incident at {last:HH:mm:ss}");
            return Outcome.Cooldown;
        }

        while (_created.Count > 0 && now - _created.Peek() >= TimeSpan.FromHours(1))
        {
            _created.Dequeue();
        }

        var joining = _capture.Active is not null && _capture.ActiveUntil is { } until && now <= until;
        if (!joining && _created.Count >= options.MaxIncidentsPerHour)
        {
            LogSkipped(_logger, rule.Id, $"{options.MaxIncidentsPerHour} automatic incidents in the last hour");
            return Outcome.Limit;
        }

        var source = rule.Kind == AnomalyRuleKind.Event ? IncidentSource.Event : IncidentSource.Rule;
        var outcome = await _capture.CaptureAsync(new IncidentTrigger(now, source, RuleId: rule.Id, Detail: TitleFor(trigger), Severity: rule.Severity), cancellationToken)
            .ConfigureAwait(false);
        _lastByRule[rule.Id] = now;
        if (!outcome.Merged)
        {
            _created.Enqueue(now);
        }

        await _engine.LinkIncidentAsync(trigger.Anomaly, outcome.Incident.Key.ToString(), cancellationToken).ConfigureAwait(false);
        LogTriggered(_logger, rule.Id, outcome.Incident.Key.ToString(), outcome.Merged ? "joined" : "created");
        return outcome.Merged ? Outcome.Joined : Outcome.Created;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var trigger in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(trigger, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    LogFailed(_logger, ex, trigger.Rule.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private TriggerOptions Options()
    {
        try
        {
            return _lastGood = _options.CurrentValue;
        }
        catch (OptionsValidationException)
        {
            return _lastGood;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Trigger {Rule}: incident {Key} {How}")]
    private static partial void LogTriggered(ILogger logger, string rule, string key, string how);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trigger {Rule} did not create an incident: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string rule, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Trigger {Rule} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string rule);
}
