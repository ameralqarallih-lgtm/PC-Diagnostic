using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Recording;

/// <summary>Starts, stops and pauses recording on request (UI, tray, hotkey; wired through IPC in Phase 9).</summary>
public interface IRecordingControl
{
    /// <summary>Current recorder state.</summary>
    RecorderState State { get; }

    /// <summary>When a pause ends; <see cref="DateTimeOffset.MaxValue"/> for "until resumed"; null when not paused.</summary>
    DateTimeOffset? PausedUntil { get; }

    /// <summary>What the current recording stores; null when it records everything or is not recording.</summary>
    RecordingSelection? Selection { get; }

    /// <summary>Starts recording everything at the user's request (this also ends a pause).</summary>
    Task StartRecordingByUserAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Starts recording at the user's request, only <paramref name="selection"/> (null records everything); a
    /// recording of something else starts again with it. The choice holds until the user stops recording.
    /// </summary>
    Task StartRecordingByUserAsync(RecordingSelection? selection, CancellationToken cancellationToken);

    /// <summary>Stops recording at the user's request.</summary>
    Task StopRecordingByUserAsync(CancellationToken cancellationToken);

    /// <summary>Pauses recording for <paramref name="duration"/>, or until resumed when null. Works in every mode.</summary>
    Task PauseAsync(TimeSpan? duration, CancellationToken cancellationToken);

    /// <summary>Ends a pause; recording resumes if the mode (or the user's earlier start) says it should.</summary>
    Task ResumeAsync(CancellationToken cancellationToken);

    /// <summary>Whether the dashboard's live view is sampling (in memory only) because the recorder is idle.</summary>
    bool LiveViewActive { get; }

    /// <summary>Whether the recorder watches for problems in memory while not recording.</summary>
    bool Watching => false;

    /// <summary>
    /// Called while a dashboard is watching: when the recorder is idle, samples for the dashboard only (nothing is
    /// stored) until no dashboard has asked for <see cref="RecordingController.LiveViewLinger"/>.
    /// A pause is respected: no live view while paused.
    /// </summary>
    Task KeepLiveViewAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Decides when the recorder records. The Agent process can run while idle (no collectors, no disk writes);
/// recording follows <see cref="RecorderOptions.Mode"/>:
/// <list type="bullet">
/// <item><b>Manual</b>: only between a user start and stop.</item>
/// <item><b>Always</b>: whenever the Agent runs, unless the user stopped it (until the mode changes or the Agent restarts).</item>
/// <item><b>Scheduled</b>: inside the configured weekly windows (local time).</item>
/// </list>
/// A pause (for a time or until resumed) overrides every mode. Sleep ends the session (reason Suspend) and waking
/// starts a new one (reason Resume), so recorded periods never span time the computer was asleep. When Windows
/// ends the session the recording is stopped and not restarted. Every start and stop is a RecorderSessions row.
/// </summary>
public sealed partial class RecordingController : BackgroundService, IRecordingControl
{
    /// <summary>How often the mode, schedule and pause are re-evaluated.</summary>
    public static readonly TimeSpan EvaluationInterval = TimeSpan.FromSeconds(15);

    /// <summary>The live view stops this long after the last dashboard request.</summary>
    public static readonly TimeSpan LiveViewLinger = TimeSpan.FromSeconds(15);

    /// <summary>How long the sleep notification may block while the session is closed (Windows allows about 2 s).</summary>
    public static readonly TimeSpan SuspendTimeout = TimeSpan.FromSeconds(1.5);

    private readonly TelemetryScheduler _scheduler;
    private readonly SampleBus _bus;
    private readonly IRecorderSessionStore _sessions;
    private readonly IOptionsMonitor<RecorderOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ElevationInfo _elevation;
    private readonly ISystemPowerEvents _power;
    private readonly string _appVersion;
    private readonly ILogger<RecordingController> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _evaluateGate = new(1, 1);

    private CancellationTokenSource? _runCts;
    private Task _runTask = Task.CompletedTask;
    private long _sessionId;
    private bool _userWantsRecording;
    private RecordingSelection? _userSelection;
    private RecordingSelection? _selection;
    private bool _userStopped;
    private RecordingMode _lastMode;
    private DateTimeOffset? _pausedUntil;
    private volatile bool _suspended;
    private volatile bool _sessionEnded;
    private bool _resumePending;
    private RecordingStartReason _sessionReason;
    private DateTimeOffset? _captureHoldUntil;
    private RecordingEndReason? _deferredEnd;
    private CancellationTokenSource? _liveCts;
    private Task _liveTask = Task.CompletedTask;
    private volatile bool _liveWatches;

    /// <summary>Creates the controller.</summary>
    public RecordingController(
        TelemetryScheduler scheduler,
        SampleBus bus,
        IRecorderSessionStore sessions,
        IOptionsMonitor<RecorderOptions> options,
        TimeProvider timeProvider,
        ElevationInfo elevation,
        ISystemPowerEvents power,
        ILogger<RecordingController> logger)
    {
        _scheduler = scheduler;
        _bus = bus;
        _sessions = sessions;
        _options = options;
        _timeProvider = timeProvider;
        _elevation = elevation;
        _power = power;
        _logger = logger;
        _appVersion = typeof(RecordingController).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _lastMode = options.CurrentValue.Mode;
        _power.Suspending += OnSuspending;
        _power.Resumed += OnResumed;
    }

    /// <inheritdoc />
    public RecorderState State { get; private set; } = RecorderState.Idle;

    /// <inheritdoc />
    public DateTimeOffset? PausedUntil => _pausedUntil;

    /// <inheritdoc />
    public bool LiveViewActive => Volatile.Read(ref _liveCts) is not null;

    /// <inheritdoc />
    public bool Watching => _liveWatches && Volatile.Read(ref _liveCts) is not null;

    /// <inheritdoc />
    public async Task KeepLiveViewAsync(CancellationToken cancellationToken)
    {
        if (State != RecorderState.Idle || _sessionEnded || _suspended)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecorderState.Idle || _sessionEnded || _suspended)
            {
                return;
            }

            if (_liveCts is { } live)
            {
                if (_liveWatches)
                {
                    return; // watching already feeds the dashboard
                }

                try
                {
                    live.CancelAfter(LiveViewLinger);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // It just ended; start a new one.
                }
            }

            var cts = new CancellationTokenSource(LiveViewLinger, _timeProvider);
            _liveCts = cts;
            _liveWatches = false;
            _liveTask = RunLiveViewAsync(cts, watch: false);
            LogLiveView(_logger, "started");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public RecordingSelection? Selection => State == RecorderState.Recording ? Volatile.Read(ref _selection) : null;

    /// <inheritdoc />
    public Task StartRecordingByUserAsync(CancellationToken cancellationToken) => StartRecordingByUserAsync(null, cancellationToken);

    /// <inheritdoc />
    public async Task StartRecordingByUserAsync(RecordingSelection? selection, CancellationToken cancellationToken)
    {
        _userWantsRecording = true;
        _userSelection = selection;
        _userStopped = false;
        ClearPause();
        if (State == RecorderState.Recording && !RecordingSelection.SameAs(selection, Volatile.Read(ref _selection)))
        {
            await StopRecordingAsync(RecordingEndReason.User, cancellationToken).ConfigureAwait(false);
        }

        await StartRecordingAsync(RecordingStartReason.User, selection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StopRecordingByUserAsync(CancellationToken cancellationToken)
    {
        _userWantsRecording = false;
        _userSelection = null;
        _userStopped = true;
        if (DeferForCapture("stop", RecordingEndReason.User))
        {
            return;
        }

        await StopRecordingAsync(RecordingEndReason.User, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task PauseAsync(TimeSpan? duration, CancellationToken cancellationToken)
    {
        if (duration is { } d && d <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A pause must be longer than zero.");
        }

        _pausedUntil = duration is { } length ? _timeProvider.GetUtcNow() + length : DateTimeOffset.MaxValue;
        await StopLiveViewAsync().ConfigureAwait(false);
        if (DeferForCapture("pause", RecordingEndReason.Pause))
        {
            LogPaused(_logger, _pausedUntil == DateTimeOffset.MaxValue ? "until resumed" : $"until {_pausedUntil:O}");
            return;
        }

        await StopRecordingAsync(RecordingEndReason.Pause, cancellationToken).ConfigureAwait(false);
        State = RecorderState.Paused;
        LogPaused(_logger, _pausedUntil == DateTimeOffset.MaxValue ? "until resumed" : $"until {_pausedUntil:O}");
    }

    /// <inheritdoc />
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (_pausedUntil is null)
        {
            return;
        }

        ClearPause();
        _resumePending = true;
        LogResumed(_logger, "by request");
        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a temporary recording for a capture when the recorder is not recording (also during a pause: a capture
    /// is an explicit request). Returns true when it started one.
    /// </summary>
    public async Task<bool> StartForCaptureAsync(CancellationToken cancellationToken)
    {
        if (State == RecorderState.Recording || _sessionEnded || _suspended)
        {
            return false;
        }

        if (State == RecorderState.Paused)
        {
            State = RecorderState.Idle; // the pause continues after the capture
        }

        await StartRecordingAsync(RecordingStartReason.Capture, null, cancellationToken).ConfigureAwait(false);
        return State == RecorderState.Recording;
    }

    /// <summary>
    /// Keeps recording until a capture's post-window is recorded: a stop or pause asked for before then takes effect
    /// when <see cref="EndCaptureRecordingAsync"/> runs, so the incident's evidence is never cut short by the user.
    /// </summary>
    public void HoldForCapture(DateTimeOffset until) =>
        _captureHoldUntil = _captureHoldUntil is { } current && current > until ? current : until;

    /// <summary>
    /// Ends a temporary capture recording once the incident's post-window is recorded, unless recording should
    /// continue anyway (the mode, the schedule or the user asked for it in the meantime). Also carries out a stop or
    /// pause that was deferred while the capture needed the recorder.
    /// </summary>
    public async Task EndCaptureRecordingAsync(CancellationToken cancellationToken)
    {
        _captureHoldUntil = null;
        var deferredEnd = _deferredEnd;
        _deferredEnd = null;
        if (State != RecorderState.Recording || _sessionReason != RecordingStartReason.Capture)
        {
            return;
        }

        if (_pausedUntil is null && Decide(_options.CurrentValue) is { Record: true } decision)
        {
            _sessionReason = decision.Reason;
            return;
        }

        await StopRecordingAsync(deferredEnd ?? RecordingEndReason.CaptureComplete, cancellationToken).ConfigureAwait(false);
        if (_pausedUntil is not null && State == RecorderState.Idle)
        {
            State = RecorderState.Paused;
        }
    }

    /// <summary>Stops recording because Windows is ending the session; recording is not restarted afterwards.</summary>
    public async Task StopForSessionEndAsync(CancellationToken cancellationToken)
    {
        _sessionEnded = true;
        await StopRecordingAsync(RecordingEndReason.SessionEnd, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Evaluates the mode, schedule, pause and sleep state once. Exposed for deterministic tests.</summary>
    internal async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _evaluateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessionEnded || _suspended)
            {
                return;
            }

            if (_pausedUntil is { } until)
            {
                if (_timeProvider.GetUtcNow() < until)
                {
                    return;
                }

                ClearPause();
                _resumePending = true;
                LogResumed(_logger, "pause ended");
            }

            var options = _options.CurrentValue;
            if (options.Mode != _lastMode)
            {
                _lastMode = options.Mode;
                _userStopped = false;
                _userWantsRecording = false;
                _userSelection = null;
                if (State == RecorderState.Recording)
                {
                    await StopRecordingAsync(RecordingEndReason.ModeChanged, cancellationToken).ConfigureAwait(false);
                }
            }

            var (shouldRecord, reason, selection) = Decide(options);
            if (shouldRecord && State == RecorderState.Idle)
            {
                await StartRecordingAsync(_resumePending ? RecordingStartReason.Resume : reason, selection, cancellationToken).ConfigureAwait(false);
            }
            else if (shouldRecord && State == RecorderState.Recording && _sessionReason != RecordingStartReason.Capture
                     && !RecordingSelection.SameAs(selection, Volatile.Read(ref _selection)))
            {
                // A scheduled window that records something else began, or its selection was edited: a session per selection.
                await StopRecordingAsync(options.Mode == RecordingMode.Scheduled ? RecordingEndReason.Schedule : RecordingEndReason.User, cancellationToken).ConfigureAwait(false);
                await StartRecordingAsync(reason, selection, cancellationToken).ConfigureAwait(false);
            }
            else if (!shouldRecord && State == RecorderState.Recording && options.Mode == RecordingMode.Scheduled && _sessionReason != RecordingStartReason.Capture)
            {
                await StopRecordingAsync(RecordingEndReason.Schedule, cancellationToken).ConfigureAwait(false);
            }

            _resumePending = false;
            await WatchAsync(options).ConfigureAwait(false);
        }
        finally
        {
            _evaluateGate.Release();
        }
    }

    /// <summary>
    /// While not recording, not paused and licensed to record: watches for problems in memory (when the option is on).
    /// Replaces a dashboard's live view, which it also feeds.
    /// </summary>
    private async Task WatchAsync(RecorderOptions options)
    {
        var want = options.WatchWhenNotRecording && State == RecorderState.Idle && _pausedUntil is null && !_sessionEnded && !_suspended;
        if (want == Watching)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLiveViewAsync().ConfigureAwait(false);
            if (want && State == RecorderState.Idle)
            {
                var cts = new CancellationTokenSource();
                _liveCts = cts;
                _liveWatches = true;
                _liveTask = RunLiveViewAsync(cts, watch: true);
                LogWatching(_logger, "started");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Handles the computer going to sleep. Exposed for tests.</summary>
    internal async Task SuspendAsync(CancellationToken cancellationToken)
    {
        _suspended = true;
        await StopLiveViewAsync().ConfigureAwait(false);
        if (State == RecorderState.Recording)
        {
            await StopRecordingAsync(RecordingEndReason.Suspend, cancellationToken).ConfigureAwait(false);
            _resumePending = true;
            LogSuspended(_logger);
        }
    }

    /// <summary>Handles the computer waking up. Exposed for tests.</summary>
    internal async Task WakeAsync(CancellationToken cancellationToken)
    {
        if (!_suspended)
        {
            return;
        }

        _suspended = false;
        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var closed = await _sessions.CloseAbandonedSessionsAsync(stoppingToken).ConfigureAwait(false);
        if (closed > 0)
        {
            LogAbandonedSessions(_logger, closed);
        }

        using var timer = new PeriodicTimer(EvaluationInterval, _timeProvider);
        try
        {
            do
            {
                await EvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down; StopAsync(CancellationToken) on the host path finishes the session.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await StopLiveViewAsync().ConfigureAwait(false);
        await StopRecordingAsync(RecordingEndReason.Shutdown, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _power.Suspending -= OnSuspending;
        _power.Resumed -= OnResumed;
        try
        {
            _liveCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already ended.
        }

        _runCts?.Dispose();
        _gate.Dispose();
        _evaluateGate.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Whether to record now, why, and what: a user's start carries its selection in every mode until the user stops;
    /// mode Always otherwise records everything, and mode Scheduled what its active windows select (everything when
    /// any of them records everything).
    /// </summary>
    private (bool Record, RecordingStartReason Reason, RecordingSelection? Selection) Decide(RecorderOptions options)
    {
        var user = _userWantsRecording ? _userSelection : null;
        switch (options.Mode)
        {
            case RecordingMode.Always:
                return (!_userStopped, RecordingStartReason.Always, user);
            case RecordingMode.Scheduled:
                var now = _timeProvider.GetLocalNow().DateTime;
                var active = options.Schedule.Where(w => w.Contains(now)).ToList();
                return (!_userStopped && active.Count > 0, RecordingStartReason.Schedule,
                    _userWantsRecording ? user : RecordingSelection.Union(active.Select(w => w.Selection)));
            default:
                return (_userWantsRecording, RecordingStartReason.User, user);
        }
    }

    private async Task RunLiveViewAsync(CancellationTokenSource cts, bool watch)
    {
        try
        {
            await Task.Run(() => _scheduler.RunAsync(cts.Token, recorded: false, watched: watch), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLiveViewFailed(_logger, ex);
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _liveCts, null, cts) == cts)
            {
                if (watch)
                {
                    LogWatching(_logger, "stopped");
                }
                else
                {
                    LogLiveView(_logger, "stopped");
                }
            }

            cts.Dispose();
        }
    }

    private async Task StopLiveViewAsync()
    {
        if (Volatile.Read(ref _liveCts) is { } cts)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already ended.
            }
        }

        await _liveTask.ConfigureAwait(false);
    }

    /// <summary>
    /// When a capture still needs the recorder, turns the session into a capture session (stopped by
    /// <see cref="EndCaptureRecordingAsync"/>) instead of stopping now.
    /// </summary>
    private bool DeferForCapture(string request, RecordingEndReason endReason)
    {
        if (State != RecorderState.Recording || _captureHoldUntil is not { } until || until <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        _sessionReason = RecordingStartReason.Capture;
        _deferredEnd = endReason;
        LogDeferred(_logger, request, until);
        return true;
    }

    private void ClearPause()
    {
        _pausedUntil = null;
        if (State == RecorderState.Paused)
        {
            State = RecorderState.Idle;
        }
    }

    // Sleep: close the session before the computer suspends (Windows gives applications about two seconds).
    private void OnSuspending(object? sender, EventArgs e)
    {
        try
        {
            if (!Task.Run(() => SuspendAsync(CancellationToken.None)).Wait(SuspendTimeout))
            {
                LogSuspendSlow(_logger);
            }
        }
        catch (AggregateException ex)
        {
            LogPowerHandlerFailed(_logger, ex.GetBaseException());
        }
    }

    private void OnResumed(object? sender, EventArgs e) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await WakeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogPowerHandlerFailed(_logger, ex);
            }
        });

    private async Task StartRecordingAsync(RecordingStartReason reason, RecordingSelection? selection, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecorderState.Idle || _sessionEnded)
            {
                return;
            }

            // The scheduler runs once: a recording replaces the live view.
            await StopLiveViewAsync().ConfigureAwait(false);
            State = RecorderState.Starting;
            _sessionId = await _sessions.StartSessionAsync(_timeProvider.GetUtcNow(), reason, _elevation.IsElevated, _appVersion, selection, cancellationToken).ConfigureAwait(false);
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;
            var items = selection?.ToSet();
            Volatile.Write(ref _selection, selection);
            _runTask = Task.Run(() => _scheduler.RunAsync(token, recorded: true, items), CancellationToken.None);
            _sessionReason = reason;
            State = RecorderState.Recording;
            LogStarted(_logger, reason, selection is null ? "everything" : $"a custom selection of {selection.Items.Count} items");
        }
        catch
        {
            State = RecorderState.Idle;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopRecordingAsync(RecordingEndReason reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != RecorderState.Recording)
            {
                return;
            }

            State = RecorderState.Stopping;
            await _runCts!.CancelAsync().ConfigureAwait(false);
            await _runTask.ConfigureAwait(false);
            _runCts.Dispose();
            _runCts = null;

            await _bus.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _sessions.EndSessionAsync(_sessionId, _timeProvider.GetUtcNow(), reason, cancellationToken).ConfigureAwait(false);
            State = RecorderState.Idle;
            LogStopped(_logger, reason);
        }
        finally
        {
            _gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording started ({Reason}; {What})")]
    private static partial void LogStarted(ILogger logger, RecordingStartReason reason, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording stopped ({Reason})")]
    private static partial void LogStopped(ILogger logger, RecordingEndReason reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording paused {Until}")]
    private static partial void LogPaused(ILogger logger, string until);

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Request} takes effect when the incident being recorded is complete ({Until:O})")]
    private static partial void LogDeferred(ILogger logger, string request, DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dashboard live view {What} (in memory only; nothing is recorded)")]
    private static partial void LogLiveView(ILogger logger, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching for problems while not recording {What} (in memory; a problem starts a recording)")]
    private static partial void LogWatching(ILogger logger, string what);

    [LoggerMessage(Level = LogLevel.Error, Message = "The dashboard live view failed")]
    private static partial void LogLiveViewFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording pause ended ({How})")]
    private static partial void LogResumed(ILogger logger, string how);

    [LoggerMessage(Level = LogLevel.Information, Message = "The computer is going to sleep; the recording session was closed and resumes on wake")]
    private static partial void LogSuspended(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closing the recording session before sleep took longer than expected")]
    private static partial void LogSuspendSlow(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling a sleep/wake notification failed")]
    private static partial void LogPowerHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed {Count} recording session(s) left open by an unclean shutdown")]
    private static partial void LogAbandonedSessions(ILogger logger, int count);

}
