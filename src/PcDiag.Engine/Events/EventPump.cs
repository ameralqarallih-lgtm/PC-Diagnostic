using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;

namespace PcDiag.Engine.Events;

/// <summary>
/// Runs the push-based <see cref="IEventCollector"/>s while recording and puts their events into the
/// <see cref="SystemEventBuffer"/>, from which each scheduler tick takes them into the frame. A collector that
/// fails is restarted after a delay (it backfills on start, and storage de-duplicates, so nothing is lost or doubled).
/// </summary>
public sealed partial class EventPump
{
    /// <summary>Delay before a failed collector is restarted.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IEventCollector> _collectors;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EventPump> _logger;
    private readonly HashSet<IEventCollector> _disabled = [];
    private bool _initialized;

    /// <summary>Creates the pump.</summary>
    public EventPump(IEnumerable<IEventCollector> collectors, SystemEventBuffer buffer, TimeProvider timeProvider, ILogger<EventPump> logger)
    {
        _collectors = collectors.ToList();
        Buffer = buffer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Where events wait for the next tick.</summary>
    public SystemEventBuffer Buffer { get; }

    /// <summary>Initializes every collector once. A collector that fails to initialize is disabled for this Agent run.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        foreach (var collector in _collectors)
        {
            try
            {
                var status = await collector.InitializeAsync(cancellationToken).ConfigureAwait(false);
                LogInitialized(_logger, collector.Id, status.Availability, status.Message ?? string.Empty);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _disabled.Add(collector);
                LogInitFailed(_logger, ex, collector.Id);
            }
        }

        _initialized = true;
    }

    /// <summary>Runs all enabled collectors until <paramref name="cancellationToken"/> is cancelled.</summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_collectors.Where(c => !_disabled.Contains(c)).Select(c => Task.Run(() => RunCollectorAsync(c, cancellationToken), CancellationToken.None)));

    private async Task RunCollectorAsync(IEventCollector collector, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var systemEvent in collector.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
                {
                    Buffer.Publish(systemEvent);
                }

                return; // the collector has nothing (more) to read, e.g. disabled by privacy settings
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogCollectorFailed(_logger, ex, collector.Id, RestartDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(RestartDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event collector {Collector} initialized: {Availability} {Message}")]
    private static partial void LogInitialized(ILogger logger, string collector, Core.Telemetry.Availability availability, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event collector {Collector} failed to initialize and is disabled for this session")]
    private static partial void LogInitFailed(ILogger logger, Exception exception, string collector);

    [LoggerMessage(Level = LogLevel.Error, Message = "Event collector {Collector} failed; restarting in {Seconds} s")]
    private static partial void LogCollectorFailed(ILogger logger, Exception exception, string collector, double seconds);
}
