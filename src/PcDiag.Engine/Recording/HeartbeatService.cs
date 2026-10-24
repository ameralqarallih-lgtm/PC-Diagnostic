using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;

namespace PcDiag.Engine.Recording;

/// <summary>
/// Writes a heartbeat every <see cref="Interval"/> (with whether the recorder is recording) and marks a clean stop
/// when the Agent stops normally or Windows ends the session. At startup, when the previous run did not stop
/// cleanly, it classifies what happened (<see cref="BootRecoveryClassifier"/>) from the Windows events logged
/// since the last heartbeat and stores the finding for the incident engine.
/// </summary>
public sealed partial class HeartbeatService : BackgroundService
{
    /// <summary>Heartbeat interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <summary>How long the end-of-session handler may take (Windows ends processes soon after).</summary>
    public static readonly TimeSpan SessionEndTimeout = TimeSpan.FromSeconds(4);

    private readonly IAgentHeartbeatStore _store;
    private readonly IBootEvidenceSource _evidence;
    private readonly RecordingController _recorder;
    private readonly ISystemPowerEvents _power;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly TaskCompletionSource _recoveryChecked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopped;

    /// <summary>Creates the service.</summary>
    public HeartbeatService(
        IAgentHeartbeatStore store,
        IBootEvidenceSource evidence,
        RecordingController recorder,
        ISystemPowerEvents power,
        TimeProvider timeProvider,
        ILogger<HeartbeatService> logger)
    {
        _store = store;
        _evidence = evidence;
        _recorder = recorder;
        _power = power;
        _timeProvider = timeProvider;
        _logger = logger;
        _power.SessionEnding += OnSessionEnding;
    }

    /// <summary>Completes once the startup check has run.</summary>
    public Task RecoveryChecked => _recoveryChecked.Task;

    /// <summary>What the startup check found.</summary>
    public BootRecovery? LastRecovery { get; private set; }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await MarkCleanAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _power.SessionEnding -= OnSessionEnding;
        base.Dispose();
    }

    /// <summary>Runs the startup check. Exposed for tests.</summary>
    internal async Task CheckPreviousRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var previous = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (previous is null || previous.CleanShutdown)
            {
                return;
            }

            IReadOnlyList<SystemEvent> events;
            try
            {
                events = await _evidence.ReadSinceAsync(previous.LastBeat - BootRecoveryClassifier.Slack, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogEvidenceFailed(_logger, ex);
                events = [];
            }

            var recovery = BootRecoveryClassifier.Classify(previous, _evidence.BootTime, events, _timeProvider.GetUtcNow());
            if (recovery is null)
            {
                return;
            }

            LastRecovery = recovery;
            var id = await _store.RecordBootRecoveryAsync(recovery, cancellationToken).ConfigureAwait(false);
            if (recovery.Kind == UncleanStopKind.WindowsRestarted)
            {
                LogRecoveryInfo(_logger, id, recovery.Kind, recovery.Summary);
            }
            else
            {
                LogRecovery(_logger, id, recovery.Kind, recovery.WasRecording, recovery.Summary);
            }
        }
        finally
        {
            _recoveryChecked.TrySetResult();
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckPreviousRunAsync(stoppingToken).ConfigureAwait(false);

        var bootId = BootRecoveryClassifier.BootId(_evidence.BootTime);
        using var timer = new PeriodicTimer(Interval, _timeProvider);
        try
        {
            do
            {
                if (_stopped)
                {
                    break;
                }

                try
                {
                    await _store.BeatAsync(_timeProvider.GetUtcNow(), bootId, _recorder.State == RecorderState.Recording, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogBeatFailed(_logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Handles Windows ending the session. Exposed for tests.</summary>
    internal async Task HandleSessionEndingAsync(CancellationToken cancellationToken)
    {
        LogSessionEnding(_logger);
        await _recorder.StopForSessionEndAsync(cancellationToken).ConfigureAwait(false);
        await MarkCleanAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkCleanAsync(CancellationToken cancellationToken)
    {
        _stopped = true;
        try
        {
            await _store.MarkCleanShutdownAsync(_timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogBeatFailed(_logger, ex);
        }
    }

    // Runs on the notification thread; Windows ends the process soon after, so this is bounded.
    private void OnSessionEnding(object? sender, EventArgs e)
    {
        using var timeout = new CancellationTokenSource(SessionEndTimeout);
        try
        {
            Task.Run(() => HandleSessionEndingAsync(timeout.Token), timeout.Token).Wait(SessionEndTimeout);
        }
        catch (AggregateException ex)
        {
            LogBeatFailed(_logger, ex.GetBaseException());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Previous run did not stop cleanly: {Kind} (recording: {WasRecording}); boot recovery #{Id}. {Summary}")]
    private static partial void LogRecovery(ILogger logger, long id, UncleanStopKind kind, bool wasRecording, string summary);

    [LoggerMessage(Level = LogLevel.Information, Message = "Previous run did not stop cleanly: {Kind}; boot recovery #{Id}. {Summary}")]
    private static partial void LogRecoveryInfo(ILogger logger, long id, UncleanStopKind kind, string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read Windows events about the previous shutdown; classifying without them")]
    private static partial void LogEvidenceFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat could not be written")]
    private static partial void LogBeatFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows is ending the session; stopping the recording and marking a clean stop")]
    private static partial void LogSessionEnding(ILogger logger);
}
