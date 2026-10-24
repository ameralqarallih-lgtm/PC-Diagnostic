using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Events;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Telemetry;

/// <summary>
/// Runs collectors on their tiers, assembles one <see cref="SampleFrame"/> per base tick and publishes it.
/// A collector that throws, times out or is still busy from an earlier tick is skipped and counted as missed;
/// it never delays the next tick or stops the others. Event collectors run alongside while recording, and each
/// frame carries the events that arrived since the previous tick.
/// </summary>
public sealed partial class TelemetryScheduler
{
    private readonly IReadOnlyList<CollectorSlot> _slots;
    private readonly SampleBus _bus;
    private readonly EventPump _events;
    private readonly SelfMonitor _selfMonitor;
    private readonly IOptionsMonitor<SamplingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ElevationInfo _elevation;
    private readonly ILogger<TelemetryScheduler> _logger;
    private bool _initialized;
    private long _sequence;

    /// <summary>Creates the scheduler.</summary>
    public TelemetryScheduler(
        IEnumerable<IScheduledCollector> collectors,
        SampleBus bus,
        EventPump events,
        SelfMonitor selfMonitor,
        IOptionsMonitor<SamplingOptions> options,
        TimeProvider timeProvider,
        ElevationInfo elevation,
        ILogger<TelemetryScheduler> logger)
    {
        _slots = collectors.Select(c => new CollectorSlot(c, logger)).ToList();
        _bus = bus;
        _events = events;
        _selfMonitor = selfMonitor;
        _options = options;
        _timeProvider = timeProvider;
        _elevation = elevation;
        _logger = logger;
    }

    /// <summary>Samples until <paramref name="cancellationToken"/> is cancelled.</summary>
    /// <param name="cancellationToken">Stops sampling.</param>
    /// <param name="recorded">False for the dashboard's live view: frames are marked so nothing stores them.</param>
    /// <param name="selection">
    /// A custom recording's items (see <see cref="RecordingItems"/>): only those reach storage, the ring buffer and the
    /// anomaly rules, while the live view still gets everything. Null records everything.
    /// </param>
    public async Task RunAsync(CancellationToken cancellationToken, bool recorded = true, IReadOnlySet<string>? selection = null, bool watched = false)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Options are read once per recording session; changing intervals takes effect on the next start.
        var plan = new TierPlan(_options.CurrentValue);
        using var timer = new PeriodicTimer(plan.BaseInterval, _timeProvider);
        var lastTick = new Dictionary<SamplingTier, long>();
        using var eventsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var events = _events.RunAsync(eventsCts.Token);

        try
        {
            // First tick immediately, then on the timer. Missed timer ticks are coalesced, never queued.
            do
            {
                await RunTickAsync(plan, lastTick, cancellationToken, recorded, selection, watched).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Recording stopped.
        }
        finally
        {
            await eventsCts.CancelAsync().ConfigureAwait(false);
            await events.ConfigureAwait(false);
        }
    }

    /// <summary>Initializes every collector once. Failures are logged; the collector still runs and reports unavailable data.</summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        foreach (var slot in _slots)
        {
            try
            {
                var status = await slot.Collector.Collector.InitializeAsync(cancellationToken).ConfigureAwait(false);
                LogCollectorInitialized(_logger, slot.Collector.Collector.Id, slot.Collector.Tier, status.Availability, status.Message ?? string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                slot.Disabled = true;
                LogCollectorInitFailed(_logger, ex, slot.Collector.Collector.Id);
            }
        }

        await _events.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    /// <summary>Runs one tick and publishes its frame. Exposed for deterministic tests.</summary>
    internal async Task<SampleFrame> RunTickAsync(
        TierPlan plan, Dictionary<SamplingTier, long> lastTick, CancellationToken cancellationToken, bool recorded = true, IReadOnlySet<string>? selection = null, bool watched = false)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var timestamp = _timeProvider.GetUtcNow();
        var started = Stopwatch.GetTimestamp();
        var nowTicks = _timeProvider.GetTimestamp();

        var due = _slots.Where(s => !s.Disabled && plan.IsDue(s.Collector.Tier, sequence)).ToList();
        var missed = 0;

        // One token per tick: cancelled at the end of the tick, which stops the timeout timer and asks any
        // collector that is still running to give up.
        using var tickCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<(CollectorSlot Slot, Task<Action<SampleFrameBuilder>> Task)>();

        foreach (var slot in due)
        {
            if (slot.InFlight is { IsCompleted: false })
            {
                missed++; // still busy from an earlier tick
                slot.RecordFailure(null, "still running from a previous tick");
                continue;
            }

            var elapsed = lastTick.TryGetValue(slot.Collector.Tier, out var last)
                ? _timeProvider.GetElapsedTime(last, nowTicks)
                : plan.Interval(slot.Collector.Tier);
            var context = new SampleContext(sequence, timestamp, elapsed, _elevation.IsElevated);

            var tickToken = tickCts.Token;
            var task = Task.Run(() => slot.Collector.CollectAsync(context, tickToken).AsTask(), tickToken);
            slot.InFlight = task;
            running.Add((slot, task));
        }

        foreach (var tier in due.Select(s => s.Collector.Tier).Distinct())
        {
            lastTick[tier] = nowTicks;
        }

        var builder = new SampleFrameBuilder { Events = _events.Buffer.Drain() };
        var timeout = Task.Delay(plan.CollectorTimeout, _timeProvider, tickCts.Token);
        foreach (var (slot, task) in running)
        {
            await Task.WhenAny(task, timeout).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!task.IsCompleted)
            {
                missed++;
                slot.RecordFailure(null, $"did not finish within {plan.CollectorTimeout.TotalMilliseconds:F0} ms");
            }
            else if (task.IsCompletedSuccessfully)
            {
                task.Result(builder);
                slot.RecordSuccess();
            }
            else
            {
                missed++;
                slot.RecordFailure(task.Exception?.GetBaseException(), "failed");
            }
        }

        await tickCts.CancelAsync().ConfigureAwait(false);

        var (agentCpu, agentWorkingSet) = _selfMonitor.Measure();
        var diagnostics = new TickDiagnostics(Stopwatch.GetElapsedTime(started), running.Count, missed, agentCpu, agentWorkingSet);
        var frame = builder.Build(sequence, timestamp, diagnostics, recorded, !recorded && watched);
        _bus.Publish(frame, recorded && selection is not null ? RecordingFilter.Apply(frame, selection) : frame);
        return frame;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Collector {Collector} ({Tier}) initialized: {Availability} {Message}")]
    private static partial void LogCollectorInitialized(ILogger logger, string collector, SamplingTier tier, Availability availability, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collector {Collector} failed to initialize and is disabled for this session")]
    private static partial void LogCollectorInitFailed(ILogger logger, Exception exception, string collector);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collector {Collector} {Problem} ({Count} consecutive)")]
    private static partial void LogCollectorProblem(ILogger logger, Exception? exception, string collector, string problem, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Collector {Collector} recovered after {Count} missed ticks")]
    private static partial void LogCollectorRecovered(ILogger logger, string collector, int count);

    private sealed class CollectorSlot(IScheduledCollector collector, ILogger logger)
    {
        private int _consecutiveFailures;

        public IScheduledCollector Collector { get; } = collector;

        public Task? InFlight { get; set; }

        public bool Disabled { get; set; }

        public void RecordSuccess()
        {
            if (_consecutiveFailures > 0)
            {
                LogCollectorRecovered(logger, Collector.Collector.Id, _consecutiveFailures);
                _consecutiveFailures = 0;
            }
        }

        // Rate-limited: first failure, then every 60th, so a broken collector cannot flood the log.
        public void RecordFailure(Exception? exception, string problem)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures == 1 || _consecutiveFailures % 60 == 0)
            {
                LogCollectorProblem(logger, exception, Collector.Collector.Id, problem, _consecutiveFailures);
            }
        }
    }
}
