using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;

namespace PcDiag.Engine.Recording;

/// <summary>
/// Enhanced mode (ARCHITECTURE R9): runs the ETW flight recorder and the per-process I/O monitor while recording,
/// when turned on in the settings (both need administrator rights), and saves the flight recorder's trace as
/// traces\incident.etl in each new incident's folder. Saving happens in the background so a capture is never
/// slowed down; merged captures do not save again.
/// </summary>
public sealed partial class EnhancedModeService : BackgroundService
{
    /// <summary>How often the wanted state is checked.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly IOptionsMonitor<EnhancedOptions> _options;
    private readonly IRecordingControl _recorder;
    private readonly IIncidentCapture _capture;
    private readonly DataPaths _paths;
    private readonly IFlightRecorder? _flight;
    private readonly IProcessIoMonitor? _io;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EnhancedModeService> _logger;
    private readonly Channel<IncidentKey> _saves = Channel.CreateBounded<IncidentKey>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    private bool _flightWanted;
    private bool _ioWanted;

    /// <summary>Creates the service.</summary>
    public EnhancedModeService(
        IOptionsMonitor<EnhancedOptions> options,
        IRecordingControl recorder,
        IIncidentCapture capture,
        DataPaths paths,
        IEnumerable<IFlightRecorder> flight,
        IEnumerable<IProcessIoMonitor> io,
        TimeProvider timeProvider,
        ILogger<EnhancedModeService> logger)
    {
        _options = options;
        _recorder = recorder;
        _capture = capture;
        _paths = paths;
        _flight = flight.FirstOrDefault();
        _io = io.FirstOrDefault();
        _timeProvider = timeProvider;
        _logger = logger;
        _capture.Captured += OnCaptured;
    }

    /// <summary>What Enhanced mode is doing, for the dashboard (null when it is off).</summary>
    public string? Status
    {
        get
        {
            var options = _options.CurrentValue;
            if (!options.FlightRecorder && !options.ProcessIo)
            {
                return null;
            }

            var parts = new List<string>();
            if (options.FlightRecorder)
            {
                parts.Add(_flight is null ? "flight recorder: not available" : _flight.Running ? "flight recorder on" : $"flight recorder: {_flight.Problem ?? "starts when recording"}");
            }

            if (options.ProcessIo)
            {
                parts.Add(_io is null ? "per-process I/O: not available" : _io.Running ? "per-process I/O on" : $"per-process I/O: {_io.Problem ?? "starts when recording"}");
            }

            return string.Join("; ", parts);
        }
    }

    /// <summary>Starts or stops each part to match the settings and the recorder. Exposed for tests.</summary>
    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        var recording = _recorder.State == RecorderState.Recording;
        if (_flight is not null)
        {
            var want = options.FlightRecorder && recording;

            // After a failed start, try again only when it is wanted afresh (turned on again, or recording again).
            var retry = _flight.Problem is null || !_flightWanted;
            _flightWanted = want;
            if (want && !_flight.Running && retry)
            {
                if (await _flight.StartAsync(cancellationToken).ConfigureAwait(false))
                {
                    LogStarted(_logger, "flight recorder");
                }
                else
                {
                    LogUnavailable(_logger, "flight recorder", _flight.Problem ?? "unknown");
                }
            }
            else if (!want && _flight.Running)
            {
                await _flight.StopAsync(cancellationToken).ConfigureAwait(false);
                LogStopped(_logger, "flight recorder");
            }
        }

        if (_io is not null)
        {
            // Per-process network and disk rates fill every processes list, so they run whenever the recorder samples.
            var sampling = _recorder.State == RecorderState.Recording || _recorder.Watching || _recorder.LiveViewActive;
            var want = options.ProcessIo && sampling;
            var retry = _io.Problem is null || !_ioWanted;
            _ioWanted = want;
            if (want && !_io.Running && retry)
            {
                if (_io.StartCollecting())
                {
                    LogStarted(_logger, "per-process I/O");
                }
                else
                {
                    LogUnavailable(_logger, "per-process I/O", _io.Problem ?? "unknown");
                }
            }
            else if (!want && _io.Running)
            {
                _io.StopCollecting();
                LogStopped(_logger, "per-process I/O");
            }
        }
    }

    /// <summary>Saves the flight recorder's trace into an incident's folder. Exposed for tests.</summary>
    internal async Task<bool> SaveTraceAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        if (_flight is not { Running: true })
        {
            return false;
        }

        var path = Path.Combine(_paths.GetIncidentDirectory(key), "traces", "incident.etl");
        var started = _timeProvider.GetTimestamp();
        LogSaving(_logger, key.ToString());
        var saved = await _flight.SaveAsync(path, $"PC Diagnostic incident {key}", cancellationToken).ConfigureAwait(false);
        if (saved)
        {
            LogSaved(_logger, key.ToString(), new FileInfo(path).Length / (1024 * 1024), _timeProvider.GetElapsedTime(started).TotalSeconds);
        }
        else
        {
            LogSaveFailed(_logger, key.ToString(), _flight.Problem ?? "unknown");
        }

        return saved;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var saving = Task.Run(async () =>
        {
            await foreach (var key in _saves.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SaveTraceAsync(key, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && !stoppingToken.IsCancellationRequested)
                {
                    // Whatever went wrong with one save, the next incident still gets its trace.
                    LogSaveFailed(_logger, key.ToString(), ex.Message);
                }
            }
        }, stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval, _timeProvider);
        try
        {
            do
            {
                try
                {
                    await ApplyAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    LogUnavailable(_logger, "enhanced mode", ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }

        _saves.Writer.TryComplete();
        await saving.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (_flight is { Running: true })
        {
            await _flight.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _io?.StopCollecting();
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _capture.Captured -= OnCaptured;
        base.Dispose();
    }

    private void OnCaptured(object? sender, IncidentCapturedEventArgs e)
    {
        if (!e.Outcome.Merged && _flight is { Running: true })
        {
            _saves.Writer.TryWrite(e.Outcome.Incident.Key);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enhanced mode: {Part} started")]
    private static partial void LogStarted(ILogger logger, string part);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enhanced mode: {Part} stopped")]
    private static partial void LogStopped(ILogger logger, string part);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enhanced mode: {Part} is not available: {Problem}")]
    private static partial void LogUnavailable(ILogger logger, string part, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key}: saving the flight recorder trace")]
    private static partial void LogSaving(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key}: flight recorder trace saved ({Megabytes} MB in {Seconds:F1} s)")]
    private static partial void LogSaved(ILogger logger, string key, long megabytes, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Incident {Key}: the flight recorder trace could not be saved: {Problem}")]
    private static partial void LogSaveFailed(ILogger logger, string key, string problem);
}
