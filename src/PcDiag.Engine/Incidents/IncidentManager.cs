using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Core.Snapshots;
using PcDiag.Engine.Diagnostics;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Incidents;

/// <summary>
/// The incident lifecycle (ARCHITECTURE §10):
/// <list type="number">
/// <item><b>Capture</b>: allocate the key, write the index row, and save the pre-window (telemetry.db plus every
/// process from the in-memory ring) and full snapshots <i>before</i> returning, so the evidence is safe even if the
/// computer stops responding next. If the recorder was not recording, a temporary recording is started and the
/// incident is "snapshot only" (no pre-window).</item>
/// <item><b>Recording the post-window</b>: further captures merge into the incident as extra triggers.</item>
/// <item><b>Finalizing</b>: <see cref="FinalizeDelay"/> after the post-window, copy it, build the timeline, and mark
/// the incident Complete (Partial when the Agent stops early; finished on the next start).</item>
/// <item><b>Analysis</b>: the diagnostic analyzers write the incident's findings (see <see cref="IncidentDiagnostics"/>).
/// Incidents analyzed by an older analyzer version are analyzed again when the Agent starts.</item>
/// </list>
/// At startup, boot recoveries (bug check, unexpected shutdown) of recorded periods become incidents centered on the
/// last heartbeat.
/// </summary>
public sealed partial class IncidentManager : BackgroundService, IIncidentCapture
{
    /// <summary>Wait after the post-window before copying it (telemetry.db is written every 5 s).</summary>
    public static readonly TimeSpan FinalizeDelay = TimeSpan.FromSeconds(10);

    /// <summary>A post-window counts as recorded when its last frame is at most this far before its end.</summary>
    public static readonly TimeSpan PostWindowTolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the post-window recorded so far is saved into the incident while it records. telemetry.db keeps only
    /// the busiest processes; every process is only in memory until saved, so a restart or crash during the
    /// post-window loses at most this much of the full process list.
    /// </summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait for a full display or service snapshot.</summary>
    public static readonly TimeSpan SnapshotTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Most incidents analyzed again at startup (newest first).</summary>
    public const int ReanalyzeAtStartup = 25;

    private readonly IIncidentStore _store;
    private readonly RingBuffer _ring;
    private readonly SampleBus _bus;
    private readonly RecordingController _recorder;
    private readonly HeartbeatService _heartbeat;
    private readonly IncidentDiagnostics _diagnostics;
    private readonly IDisplayCollector? _display;
    private readonly IServiceCollector? _services;
    private readonly IOptionsMonitor<RecorderOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IncidentManager> _logger;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Incident? _active;
    private DateTimeOffset? _activeUntil;
    private DateTimeOffset _savedUntil;
    private bool _startedRecording;
    private readonly Core.Storage.IStorageGuard? _guard;

    /// <summary>Creates the manager.</summary>
    public IncidentManager(
        IIncidentStore store,
        RingBuffer ring,
        SampleBus bus,
        RecordingController recorder,
        HeartbeatService heartbeat,
        IncidentDiagnostics diagnostics,
        IEnumerable<IDisplayCollector> display,
        IEnumerable<IServiceCollector> services,
        IOptionsMonitor<RecorderOptions> options,
        TimeProvider timeProvider,
        ILogger<IncidentManager> logger,
        Core.Storage.IStorageGuard? guard = null)
    {
        _guard = guard;
        _store = store;
        _ring = ring;
        _bus = bus;
        _recorder = recorder;
        _heartbeat = heartbeat;
        _diagnostics = diagnostics;
        _display = display.FirstOrDefault();
        _services = services.FirstOrDefault();
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _appVersion = typeof(IncidentManager).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <inheritdoc />
    public event EventHandler<IncidentCapturedEventArgs>? Captured;

    /// <inheritdoc />
    public event EventHandler<IncidentCapturedEventArgs>? Finished;

    /// <inheritdoc />
    public Incident? Active => _active;

    /// <inheritdoc />
    public DateTimeOffset? ActiveUntil => _activeUntil;

    /// <inheritdoc />
    public async Task<CaptureOutcome> CaptureAsync(IncidentTrigger trigger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        CaptureOutcome outcome;
        try
        {
            var now = _timeProvider.GetUtcNow();
            trigger = trigger with { Timestamp = now };
            if (_active is { } active && _activeUntil is { } until && now <= until)
            {
                await _store.AddTriggerAsync(active.Id, trigger, cancellationToken).ConfigureAwait(false);
                outcome = new CaptureOutcome(active, true, active.PreWindow == TimeSpan.Zero, until);
                LogMerged(_logger, active.Key.ToString(), trigger.Source);
            }
            else if (_guard is { WritesPaused: true })
            {
                // An incident is written to disk at once (its pre-window): not while the drive is almost full.
                throw new Core.Storage.StorageFullException();
            }
            else
            {
                outcome = await CreateAsync(trigger, now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        Captured?.Invoke(this, new IncidentCapturedEventArgs(outcome));
        return outcome;
    }

    /// <summary>Finalizes the active incident when its post-window (plus <see cref="FinalizeDelay"/>) is over, or now when <paramref name="force"/>. Exposed for tests.</summary>
    internal async Task FinalizeDueAsync(bool force, CancellationToken cancellationToken)
    {
        CaptureOutcome? finished = null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active is not { } incident || _activeUntil is not { } until)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            if (!force && now < until + FinalizeDelay)
            {
                return;
            }

            var end = now < until ? now : until;
            var state = end < until ? IncidentState.Partial : IncidentState.Complete;
            try
            {
                await _bus.FlushAsync(cancellationToken).ConfigureAwait(false);
                var frames = _ring.Snapshot(incident.CenterAt, end);

                // The recorder can still stop early (sleep, sign-out, shutdown): judge by what was recorded.
                if (frames.Count == 0 || frames[^1].Timestamp < end - PostWindowTolerance)
                {
                    state = IncidentState.Partial;
                }

                await _store.CaptureWindowAsync(incident, incident.CenterAt, end, frames, cancellationToken).ConfigureAwait(false);
                await _store.FinalizeAsync(incident, state, null, cancellationToken).ConfigureAwait(false);
                await TryAnalyzeAsync(incident, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                state = IncidentState.Failed;
                LogFinalizeFailed(_logger, ex, incident.Key.ToString());
                await TryMarkFailedAsync(incident, cancellationToken).ConfigureAwait(false);
            }

            // Also when the user was recording: a stop or pause asked for during the capture happens now.
            await _recorder.EndCaptureRecordingAsync(cancellationToken).ConfigureAwait(false);

            _active = null;
            _activeUntil = null;
            _startedRecording = false;
            finished = new CaptureOutcome(incident with { State = state }, false, incident.PreWindow == TimeSpan.Zero, until);
            LogFinished(_logger, incident.Key.ToString(), state);
        }
        finally
        {
            _gate.Release();
        }

        Finished?.Invoke(this, new IncidentCapturedEventArgs(finished));
    }

    /// <inheritdoc />
    public async Task<IncidentEditResult> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var found = await _store.UpdateAsync(key, update, cancellationToken).ConfigureAwait(false);
            if (found && _active is { } active && active.Key == key)
            {
                _active = active with
                {
                    Title = update.Title ?? active.Title,
                    Notes = update.Notes is null ? active.Notes : update.Notes.Length == 0 ? null : update.Notes,
                    UserType = update.UserType ?? active.UserType,
                };
            }

            return found ? IncidentEditResult.Done : IncidentEditResult.NotFound;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IncidentEditResult> DeleteAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active is { } active && active.Key == key)
            {
                return IncidentEditResult.StillRecording;
            }

            var deleted = await _store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            if (deleted)
            {
                LogDeleted(_logger, key.ToString());
            }

            return deleted ? IncidentEditResult.Done : IncidentEditResult.NotFound;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Startup work: finishes incidents left unfinished and turns boot recoveries into incidents. Exposed for tests.</summary>
    internal async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var incident in await _store.ListUnfinishedAsync(cancellationToken).ConfigureAwait(false))
        {
            var end = incident.CenterAt + incident.PostWindow;
            try
            {
                await _store.CaptureWindowAsync(incident, incident.CenterAt, end, [], cancellationToken).ConfigureAwait(false);
                await _store.FinalizeAsync(incident, IncidentState.Partial, null, cancellationToken).ConfigureAwait(false);
                await TryAnalyzeAsync(incident, cancellationToken).ConfigureAwait(false);
                LogRecovered(_logger, incident.Key.ToString());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFinalizeFailed(_logger, ex, incident.Key.ToString());
                await TryMarkFailedAsync(incident, cancellationToken).ConfigureAwait(false);
            }
        }

        await _heartbeat.RecoveryChecked.WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pending in await _store.PendingBootRecoveriesAsync(cancellationToken).ConfigureAwait(false))
        {
            var recovery = pending.Recovery;
            if (!recovery.WasRecording || recovery.Kind is not (UncleanStopKind.BugCheck or UncleanStopKind.UnexpectedShutdown))
            {
                // Not a recorded period, or nothing went wrong with Windows: no incident.
                await _store.MarkBootRecoveryAsync(pending.Id, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var pre = TimeSpan.FromMinutes(_options.CurrentValue.PreIncidentMinutes);
            var trigger = new IncidentTrigger(recovery.LastHeartbeat, IncidentSource.BootRecovery, Detail: recovery.Summary);
            var title = recovery.Kind == UncleanStopKind.BugCheck
                ? $"Blue screen (bug check {BugCheckCode(recovery)})"
                : "Unexpected shutdown or restart";
            var draft = new IncidentDraft(_timeProvider.GetUtcNow(), recovery.LastHeartbeat, pre, TimeSpan.Zero, IncidentSource.BootRecovery, null, title, null,
                recovery.Kind == UncleanStopKind.BugCheck ? Severity.Critical : Severity.High, false, trigger, _appVersion);
            var incident = await _store.CreateAsync(draft, cancellationToken).ConfigureAwait(false);

            // The recorded minutes before the last heartbeat, and anything stored since (e.g. the boot's Windows events).
            var end = recovery.DetectedAt > recovery.LastHeartbeat ? recovery.DetectedAt : recovery.LastHeartbeat;
            await _store.CaptureWindowAsync(incident, recovery.LastHeartbeat - pre, end, [], cancellationToken).ConfigureAwait(false);
            await _store.FinalizeAsync(incident, IncidentState.Complete, recovery, cancellationToken).ConfigureAwait(false);
            await TryAnalyzeAsync(incident, cancellationToken).ConfigureAwait(false);
            await _store.MarkBootRecoveryAsync(pending.Id, incident.Id, cancellationToken).ConfigureAwait(false);
            LogBootRecoveryIncident(_logger, incident.Key.ToString(), title);
            Finished?.Invoke(this, new IncidentCapturedEventArgs(new CaptureOutcome(incident with { State = IncidentState.Complete }, false, false, recovery.LastHeartbeat)));
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken).ConfigureAwait(false);
            await ReanalyzeAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SaveProgressAsync(stoppingToken).ConfigureAwait(false);
                await FinalizeDueAsync(force: false, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping; StopAsync finishes the active incident.
        }
    }

    /// <summary>
    /// Saves the post-window recorded since the previous save (every <see cref="ProgressInterval"/>), with every
    /// process from the in-memory frames. Finishing the incident saves the whole window again; this only makes sure a
    /// restart or crash before then keeps the full process list. Exposed for tests.
    /// </summary>
    internal async Task SaveProgressAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_active is not { } incident || _activeUntil is not { } until || now - _savedUntil < ProgressInterval || now > until)
            {
                return;
            }

            await _store.CaptureWindowAsync(incident, _savedUntil, now, _ring.Snapshot(_savedUntil, now), cancellationToken).ConfigureAwait(false);
            _savedUntil = now;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogProgressFailed(_logger, ex, _active?.Key.ToString() ?? "?");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Analyzes finished incidents whose findings come from an older analyzer version. Exposed for tests.</summary>
    internal async Task ReanalyzeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var count = await _diagnostics.AnalyzePendingAsync(ReanalyzeAtStartup, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                LogReanalyzed(_logger, count, IncidentDiagnostics.AnalyzerVersion);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogAnalysisFailed(_logger, ex, "(startup)");
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await FinalizeDueAsync(force: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    /// <summary>The title for a capture.</summary>
    internal static string TitleFor(IncidentTrigger trigger) =>
        trigger.UserType is { } type ? type.ToDisplayName() : trigger.Source switch
        {
            IncidentSource.Hotkey => "Captured with the hotkey",
            IncidentSource.Tray => "Captured from the tray",
            IncidentSource.Rule or IncidentSource.Event => trigger.Detail ?? "Automatic capture",
            _ => "Captured incident",
        };

    private static string BugCheckCode(BootRecovery recovery) =>
        recovery.Details.GetValueOrDefault("bugcheck")?.Split(' ', 2)[0] ?? "unknown";

    private async Task<CaptureOutcome> CreateAsync(IncidentTrigger trigger, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var recording = _recorder.State == RecorderState.Recording;
        var preWanted = TimeSpan.FromMinutes(options.PreIncidentMinutes);

        // Not recording but watched: the minutes kept in memory are stored first (before the recording starts, so they
        // stay in order), and become the incident's history like a recording's.
        var watched = recording ? [] : _ring.Snapshot(now - preWanted, now).Where(f => f.Watched).ToList();
        await _bus.PublishBackfillAsync(watched, cancellationToken).ConfigureAwait(false);

        var history = recording || watched.Count > 0;
        var started = !recording && await _recorder.StartForCaptureAsync(cancellationToken).ConfigureAwait(false);
        var pre = history ? preWanted : TimeSpan.Zero;
        var post = TimeSpan.FromSeconds(options.PostIncidentSeconds);

        // A custom recording's incident holds only what it selected (a capture that had to start recording records everything).
        var selection = recording ? _recorder.Selection : null;
        var items = selection?.ToSet();
        var incident = await _store.CreateAsync(
            new IncidentDraft(now, now, pre, post, trigger.Source, trigger.UserType, TitleFor(trigger), trigger.Note, trigger.Severity ?? Severity.Medium, !history, trigger, _appVersion,
                selection),
            cancellationToken).ConfigureAwait(false);

        // Evidence first: everything recorded up to now goes into the incident before anything else happens.
        if (history)
        {
            await _bus.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _store.CaptureWindowAsync(incident, now - pre, now, _ring.Snapshot(now - pre, now), cancellationToken).ConfigureAwait(false);
        }

        await _store.SaveSnapshotsAsync(
            incident,
            await TakeAsync<DisplaySnapshot>(_display is null || items?.Contains(RecordingItems.Display) == false ? null : _display.SnapshotAsync),
            await TakeAsync<ServiceSnapshot>(_services is null || items?.Contains(RecordingItems.Services) == false ? null : _services.SnapshotAsync),
            cancellationToken).ConfigureAwait(false);

        _active = incident;
        _activeUntil = now + post;
        _savedUntil = now;
        _recorder.HoldForCapture(now + post);
        _startedRecording = started;
        LogCaptured(_logger, incident.Key.ToString(), incident.Title,
            recording ? "with pre-window" : history ? "with the pre-window watched in memory (recording started)" : "snapshot only (recorder was not recording)");
        return new CaptureOutcome(incident, false, !history, now + post);
    }

    private async Task<T?> TakeAsync<T>(Func<CancellationToken, Task<T>>? snapshot)
        where T : class
    {
        if (snapshot is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(SnapshotTimeout, _timeProvider);
        try
        {
            return await snapshot(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogSnapshotFailed(_logger, ex, typeof(T).Name);
            return null;
        }
    }

    /// <summary>The findings are a bonus: an analysis problem never fails the incident.</summary>
    private async Task TryAnalyzeAsync(Incident incident, CancellationToken cancellationToken)
    {
        try
        {
            await _diagnostics.AnalyzeAsync(incident.Key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            LogAnalysisFailed(_logger, ex, incident.Key.ToString());
        }
    }

    private async Task TryMarkFailedAsync(Incident incident, CancellationToken cancellationToken)
    {
        try
        {
            await _store.FinalizeAsync(incident, IncidentState.Failed, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFinalizeFailed(_logger, ex, incident.Key.ToString());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key} captured: {Title}, {Evidence}")]
    private static partial void LogCaptured(ILogger logger, string key, string title, string evidence);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture ({Source}) merged into incident {Key}, which is still recording")]
    private static partial void LogMerged(ILogger logger, string key, IncidentSource source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key} is {State}")]
    private static partial void LogFinished(ILogger logger, string key, IncidentState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {Key} was left unfinished by the previous run; finished as Partial")]
    private static partial void LogRecovered(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {Key} created for the previous run: {Title}")]
    private static partial void LogBootRecoveryIncident(ILogger logger, string key, string title);

    [LoggerMessage(Level = LogLevel.Error, Message = "Incident {Key} could not be finished")]
    private static partial void LogFinalizeFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key} deleted by the user")]
    private static partial void LogDeleted(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} incidents analyzed again (analyzer version {Version})")]
    private static partial void LogReanalyzed(ILogger logger, int count, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {Key} could not be analyzed; it has no findings")]
    private static partial void LogAnalysisFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {Key}: saving the post-window so far failed; it is saved when the incident is finished")]
    private static partial void LogProgressFailed(ILogger logger, Exception exception, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The {Snapshot} for the incident could not be taken")]
    private static partial void LogSnapshotFailed(ILogger logger, Exception exception, string snapshot);
}
