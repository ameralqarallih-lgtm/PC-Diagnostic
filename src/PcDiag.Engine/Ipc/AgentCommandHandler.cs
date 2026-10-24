using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Control;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Engine.Anomalies;
using PcDiag.Engine.Live;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Ipc;

/// <summary>
/// Executes Agent pipe commands. Every request is validated (protocol version, command name, argument ranges)
/// before anything happens; failures return an error message, never internal details.
/// </summary>
public sealed partial class AgentCommandHandler
{
    /// <summary>Longest accepted request ID.</summary>
    public const int MaxIdLength = 64;

    /// <summary>Longest accepted capture note.</summary>
    public const int MaxNoteLength = 2000;

    /// <summary>Longest accepted incident title.</summary>
    public const int MaxTitleLength = 120;

    /// <summary>Longest item ID in a custom recording's selection.</summary>
    public const int MaxSelectionItemLength = 400;

    /// <summary>Longest wait for the first samples of devices and sensors when the catalog is asked for.</summary>
    public static readonly TimeSpan CatalogWait = TimeSpan.FromSeconds(6);

    private readonly IRecordingControl _recorder;
    private readonly IIncidentCapture _capture;
    private readonly LiveFeed? _live;
    private readonly AnomalyEngine? _anomalies;
    private readonly EnhancedModeService? _enhanced;
    private readonly IOptionsMonitor<RecorderOptions> _options;
    private readonly ElevationInfo _elevation;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentCommandHandler> _logger;
    private readonly string _version;
    private readonly ISystemControl? _control;
    private readonly Core.Overlay.IOverlayControl? _overlay;
    private readonly Core.Storage.IStorageControl? _storage;
    private readonly Core.Devices.IHardwareWatch? _hardware;
    private readonly Core.Devices.IWindowsDrivers? _windowsDrivers;

    /// <summary>Longest service name.</summary>
    private const int MaxServiceNameLength = 256;

    /// <summary>Most items in one tune-up action.</summary>
    private const int MaxItems = 1000;

    /// <summary>Longest wait for the first process sample when the process list is asked for while idle.</summary>
    private static readonly TimeSpan ProcessWait = TimeSpan.FromSeconds(5);

    private static readonly HashSet<string> ProcessActions = new(StringComparer.Ordinal) { "end", "end-tree", "priority" };
    private static readonly HashSet<string> ServiceActions = new(StringComparer.Ordinal) { "start", "stop", "restart", "start-type" };
    private static readonly HashSet<string> StartTypes = new(StringComparer.Ordinal) { "Automatic", "AutomaticDelayed", "Manual", "Disabled" };
    private static readonly HashSet<string> Priorities = new(StringComparer.Ordinal) { "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "Realtime" };

    /// <summary>Creates the handler.</summary>
    public AgentCommandHandler(IRecordingControl recorder, IIncidentCapture capture, IEnumerable<LiveFeed> live, IEnumerable<AnomalyEngine> anomalies, IOptionsMonitor<RecorderOptions> options,
        ElevationInfo elevation, TimeProvider timeProvider, ILogger<AgentCommandHandler> logger, IEnumerable<EnhancedModeService>? enhanced = null,
        ISystemControl? control = null, Core.Overlay.IOverlayControl? overlay = null,
        Core.Storage.IStorageControl? storage = null, Core.Devices.IHardwareWatch? hardware = null, Core.Devices.IWindowsDrivers? windowsDrivers = null)
    {
        _windowsDrivers = windowsDrivers;
        _storage = storage;
        _hardware = hardware;
        _control = control;
        _overlay = overlay;
        _enhanced = enhanced?.FirstOrDefault();
        _recorder = recorder;
        _capture = capture;
        _live = live.FirstOrDefault();
        _anomalies = anomalies.FirstOrDefault();
        _options = options;
        _elevation = elevation;
        _timeProvider = timeProvider;
        _logger = logger;
        _version = typeof(AgentCommandHandler).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    }

    /// <summary>Handles one request.</summary>
    public async Task<AgentResponse> HandleAsync(AgentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = request.Id is { Length: > 0 and <= MaxIdLength } ? request.Id : string.Empty;
        if (Validate(request) is { } problem)
        {
            return new AgentResponse(id, false, problem);
        }

        try
        {
            AgentCaptureResult? capture = null;
            switch (request.Command)
            {
                case AgentCommands.Start:
                    var selection = request.Args?.Selection is { } items ? RecordingSelection.Of(items) : null;
                    await _recorder.StartRecordingByUserAsync(selection, cancellationToken).ConfigureAwait(false);
                    break;
                case AgentCommands.Stop:
                    await _recorder.StopRecordingByUserAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case AgentCommands.Pause:
                    await _recorder.PauseAsync(request.Args?.PauseMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, cancellationToken).ConfigureAwait(false);
                    break;
                case AgentCommands.Resume:
                    await _recorder.ResumeAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case AgentCommands.Capture:
                    var outcome = await _capture.CaptureAsync(
                        new IncidentTrigger(_timeProvider.GetUtcNow(), IncidentSource.User, request.Args?.IncidentType, Note: request.Args?.Note), cancellationToken).ConfigureAwait(false);
                    capture = new AgentCaptureResult(outcome.Incident.Key.ToString(), outcome.Merged, outcome.SnapshotOnly, outcome.RecordingUntil);
                    break;
                case AgentCommands.UpdateIncident:
                {
                    var args = request.Args!;
                    var result = await _capture.UpdateAsync(
                        IncidentKey.Parse(args.IncidentKey!, CultureInfo.InvariantCulture),
                        new IncidentUpdate(args.Title?.Trim(), args.Note?.Trim(), args.IncidentType, args.Pinned),
                        cancellationToken).ConfigureAwait(false);
                    if (result != IncidentEditResult.Done)
                    {
                        return new AgentResponse(id, false, Describe(result), Status());
                    }

                    break;
                }

                case AgentCommands.DeleteIncident:
                {
                    var result = await _capture.DeleteAsync(IncidentKey.Parse(request.Args!.IncidentKey!, CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                    if (result != IncidentEditResult.Done)
                    {
                        return new AgentResponse(id, false, Describe(result), Status());
                    }

                    break;
                }
            }

            if (request.Command is AgentCommands.ControlProcesses or AgentCommands.ControlServices or AgentCommands.ProcessAction or AgentCommands.ServiceAction
                or AgentCommands.TuneUpScan or AgentCommands.TuneUpApply or AgentCommands.TuneUpPlan or AgentCommands.CleanupFiles)
            {
                return await ControlAsync(id, request, cancellationToken).ConfigureAwait(false);
            }

            if (request.Command is AgentCommands.OverlayState or AgentCommands.OverlayReload or AgentCommands.OverlayIdentify or AgentCommands.OverlayArrange)
            {
                return await OverlayAsync(id, request.Command, request.Args?.On, cancellationToken).ConfigureAwait(false);
            }

            if (request.Command is AgentCommands.OverlayScreenDrivers or AgentCommands.OverlayScreenDriverApprove or AgentCommands.DriverImport or AgentCommands.DriverRemove)
            {
                return ScreenDrivers(id, request.Command, request.Args);
            }

            if (request.Command is AgentCommands.StorageState or AgentCommands.StorageClean)
            {
                if (_storage is null)
                {
                    return new AgentResponse(id, false, "This recorder does not manage its storage.", Status());
                }

                if (request.Command == AgentCommands.StorageState)
                {
                    return new AgentResponse(id, true, Status: Status(), Storage: _storage.State);
                }

                var cleaned = await _storage.CleanAsync(request.Args?.Compact == true, cancellationToken).ConfigureAwait(false);
                return new AgentResponse(id, true, Status: Status(), Storage: cleaned.State, Cleaned: cleaned);
            }

            if (request.Command is AgentCommands.HardwareState or AgentCommands.HardwareCheck)
            {
                if (_hardware is null)
                {
                    return new AgentResponse(id, false, "This recorder does not check the hardware.", Status());
                }

                var status = request.Command == AgentCommands.HardwareCheck || _hardware.Status is null
                    ? await _hardware.CheckAsync(cancellationToken).ConfigureAwait(false)
                    : _hardware.Status;
                return new AgentResponse(id, true, Status: Status(), Hardware: status);
            }

            if (request.Command is AgentCommands.WindowsDrivers or AgentCommands.WindowsDriverImport or AgentCommands.WindowsDriverInstall or AgentCommands.WindowsDriverRemove)
            {
                return await WindowsDriversAsync(id, request.Command, request.Args, cancellationToken).ConfigureAwait(false);
            }

            if (request.Command == AgentCommands.RecorderCost)
            {
                return new AgentResponse(id, true, Status: Status(), Cost: Core.Diagnostics.CostMeter.Latest);
            }

            if (request.Command == AgentCommands.Catalog)
            {
                return new AgentResponse(id, true, Status: Status(), Catalog: await CatalogAsync(cancellationToken).ConfigureAwait(false));
            }

            LiveSnapshot? live = null;
            LiveTrend? trend = null;
            if (request.Command is AgentCommands.Live or AgentCommands.Trend)
            {
                await _recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
                live = request.Command == AgentCommands.Live ? _live?.Snapshot(_recorder.State == RecorderState.Recording) : null;
                if (live is not null && _anomalies is not null)
                {
                    live = live with { Anomalies = _anomalies.Current };
                }

                trend = request.Command == AgentCommands.Trend ? _live?.Trend(TimeSpan.FromMinutes(request.Args?.TrendMinutes ?? LiveFeed.TrendMinutes)) : null;
            }

            return new AgentResponse(id, true, Status: Status(), Capture: capture, Live: live, Trend: trend);
        }
        catch (Core.Storage.StorageFullException ex)
        {
            return new AgentResponse(id, false, ex.Message, Status());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCommandFailed(_logger, ex, request.Command);
            return new AgentResponse(id, false, $"The {request.Command} command failed.", Status());
        }
    }

    /// <summary>The on-screen display (the recorder draws it on the desktop it runs on).</summary>
    /// <summary>Windows drivers for devices without one: listed, imported, installed (confirmed on the PC) and removed.</summary>
    private async Task<AgentResponse> WindowsDriversAsync(string id, string command, AgentRequestArgs? args, CancellationToken cancellationToken)
    {
        if (_windowsDrivers is null)
        {
            return new AgentResponse(id, false, "This recorder does not install Windows drivers.", Status());
        }

        if (!_elevation.IsElevated && command != AgentCommands.WindowsDrivers)
        {
            return new AgentResponse(id, false, "Installing Windows drivers needs the recorder to run as administrator.", Status());
        }

        string? message = null;
        try
        {
            switch (command)
            {
                case AgentCommands.WindowsDriverImport:
                    if (args?.Value is not { Length: > 0 } upload)
                    {
                        return new AgentResponse(id, false, "The uploaded package is required.", Status());
                    }

                    if (_windowsDrivers.Import(upload) is { } importProblem)
                    {
                        return new AgentResponse(id, false, importProblem, Status());
                    }

                    break;
                case AgentCommands.WindowsDriverInstall:
                    if (args?.DriverId is not { Length: > 0 } package || args.Value is not { Length: > 0 } device)
                    {
                        return new AgentResponse(id, false, "A driver package and a device are required.", Status());
                    }

                    message = await _windowsDrivers.InstallAsync(package, device, args.On ?? false, cancellationToken).ConfigureAwait(false);
                    break;
                case AgentCommands.WindowsDriverRemove:
                    if (args?.DriverId is not { Length: > 0 } removed)
                    {
                        return new AgentResponse(id, false, "A driver package is required.", Status());
                    }

                    if (await _windowsDrivers.RemoveAsync(removed, cancellationToken).ConfigureAwait(false) is { } removeProblem)
                    {
                        return new AgentResponse(id, false, removeProblem, Status());
                    }

                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            return new AgentResponse(id, false, ex.Message, Status());
        }

        return new AgentResponse(id, true, Status: Status(), WindowsDrivers: _windowsDrivers.State() with { Message = message });
    }

    /// <summary>The USB screens' driver packages, and approving one (or withdrawing it).</summary>
    private AgentResponse ScreenDrivers(string id, string command, AgentRequestArgs? args)
    {
        if (_overlay is null)
        {
            return new AgentResponse(id, false, "This recorder cannot show readings on the screens.", Status());
        }

        if (command == AgentCommands.DriverImport)
        {
            if (args?.Value is not { Length: > 0 } upload)
            {
                return new AgentResponse(id, false, "The uploaded package is required.", Status());
            }

            if (_overlay.ImportDriver(upload) is { Problem: { } importProblem })
            {
                return new AgentResponse(id, false, importProblem, Status());
            }
        }

        if (command == AgentCommands.DriverRemove)
        {
            if (args?.DriverId is not { Length: > 0 } removed)
            {
                return new AgentResponse(id, false, "A driver package's id is required.", Status());
            }

            if (_overlay.RemoveDriver(removed) is { } removeProblem)
            {
                return new AgentResponse(id, false, removeProblem, Status());
            }
        }

        if (command == AgentCommands.OverlayScreenDriverApprove)
        {
            if (args?.DriverId is not { Length: > 0 } driver || args.Sha256 is not { Length: 64 } sha)
            {
                return new AgentResponse(id, false, "A driver package's id and its 64-character SHA-256 are required.", Status());
            }

            if (_overlay.ApproveScreenDriver(driver, sha, args.On ?? true) is { } problem)
            {
                return new AgentResponse(id, false, problem, Status());
            }
        }

        return new AgentResponse(id, true, Status: Status(), ScreenDrivers: _overlay.ScreenDrivers());
    }

    private async Task<AgentResponse> OverlayAsync(string id, string command, bool? on, CancellationToken cancellationToken)
    {
        if (_overlay is null)
        {
            return new AgentResponse(id, false, "This recorder cannot show readings on the screens.", Status());
        }

        switch (command)
        {
            case AgentCommands.OverlayReload:
                _overlay.Reload();
                return new AgentResponse(id, true, Status: Status());
            case AgentCommands.OverlayIdentify:
                _overlay.Identify();
                return new AgentResponse(id, true, Status: Status());
            case AgentCommands.OverlayArrange:
                _overlay.Arrange(on ?? true);
                return new AgentResponse(id, true, Status: Status(), Overlay: await _overlay.StateAsync(cancellationToken).ConfigureAwait(false));
            default:
                // The values come from sampling: keep it going (in memory only) while the layout is being edited.
                await _recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
                return new AgentResponse(id, true, Status: Status(), Overlay: await _overlay.StateAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The process and service controls and the tune-up (the recorder must offer them).</summary>
    private async Task<AgentResponse> ControlAsync(string id, AgentRequest request, CancellationToken cancellationToken)
    {
        if (_control is null)
        {
            return new AgentResponse(id, false, "This recorder does not offer process and service control.", Status());
        }

        var args = request.Args ?? new AgentRequestArgs();
        var confirmed = args.Confirmed == true;
        switch (request.Command)
        {
            case AgentCommands.ControlProcesses:
                // Processes are sampled while recording or for a live view: keep one going and wait for its first sample.
                await _recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
                var started = _timeProvider.GetTimestamp();
                while (_control.Processes() is { Count: 0 } && _timeProvider.GetElapsedTime(started) < ProcessWait)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                return new AgentResponse(id, true, Status: Status(), Processes: _control.Processes());
            case AgentCommands.ControlServices:
                return new AgentResponse(id, true, Status: Status(), Services: _control.Services());
            case AgentCommands.TuneUpScan:
                await _recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
                return new AgentResponse(id, true, Status: Status(), TuneUp: await _control.ScanAsync(cancellationToken).ConfigureAwait(false));
            case AgentCommands.CleanupFiles:
            {
                var items = args.Items ?? [];
                var root = items.Count > 0 && int.TryParse(items[0], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : -1;
                var listing = await Task.Run(() => _control.CleanupFiles(args.Value ?? "", root, items.Count > 1 ? items[1] ?? "" : ""), cancellationToken).ConfigureAwait(false);
                return new AgentResponse(id, true, Status: Status(), CleanupFiles: listing);
            }

            case AgentCommands.TuneUpPlan:
            {
                // The plan reads the newest samples: keep them coming and wait for the first.
                await _recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
                var waited = _timeProvider.GetTimestamp();
                while (_control.Processes() is { Count: 0 } && _timeProvider.GetElapsedTime(waited) < ProcessWait)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
                }

                return new AgentResponse(id, true, Status: Status(), Plan: await _control.PlanAsync(cancellationToken).ConfigureAwait(false));
            }
            case AgentCommands.ProcessAction:
            {
                var result = args.ControlAction == "priority"
                    ? _control.SetPriority(args.Pid!.Value, args.ProcessStart!.Value, args.Priority!)
                    : _control.EndProcess(args.Pid!.Value, args.ProcessStart!.Value, args.ControlAction == "end-tree", confirmed, args.Safe == true);
                return new AgentResponse(id, result.Ok, result.Ok ? null : result.Message, Status(), Result: result);
            }

            case AgentCommands.ServiceAction:
            {
                var result = _control.ControlService(args.ServiceName!, args.ControlAction!, args.StartType, confirmed, args.Safe == true);
                return new AgentResponse(id, result.Ok, result.Ok ? null : result.Message, Status(), Result: result);
            }

            default:
            {
                var result = await _control.ApplyAsync(args.ControlAction!, args.Items ?? [], args.Value, confirmed, cancellationToken, args.Safe == true).ConfigureAwait(false);
                return new AgentResponse(id, result.Ok, result.Ok ? null : result.Message, Status(), Result: result);
            }
        }
    }

    /// <summary>The current status.</summary>
    public AgentStatus Status() => new(
        _version,
        _elevation.IsElevated,
        _options.CurrentValue.Mode,
        _recorder.State,
        _recorder.PausedUntil,
        _capture.Active?.Key.ToString(),
        _capture.ActiveUntil,
        _recorder.LiveViewActive,
        _enhanced?.Status,
        _recorder.Selection?.Items.Count,
        _storage?.State is { Condition: not Core.Storage.StorageCondition.Normal, Message: { } message } storage ? new Core.Storage.StorageNotice(storage.Condition, message) : null,
        _hardware?.Status is { Missing.Count: > 0 } hardware ? new Core.Devices.HardwareNotice(hardware.Missing.Count, Core.Devices.HardwareCheck.Describe(hardware.Missing)) : null,
        _recorder.Watching);

    /// <summary>
    /// The catalog from the devices and sensors sampled so far. Sensors and devices are known only once sampled, so
    /// while idle this starts the live view and waits for its first full samples (a few seconds at most).
    /// </summary>
    private async Task<RecordingCatalog> CatalogAsync(CancellationToken cancellationToken)
    {
        if (_live is null)
        {
            return RecordingCatalogBuilder.Build(null, null, null, null);
        }

        await _recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
        var deadline = _timeProvider.GetTimestamp();
        while (!_live.HasSamples && _timeProvider.GetElapsedTime(deadline) < CatalogWait)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return _live.Catalog();
    }

    private static string? Validate(AgentRequest request)
    {
        if (request.Version != AgentIpc.ProtocolVersion)
        {
            return $"Unsupported protocol version {request.Version} (this Agent speaks {AgentIpc.ProtocolVersion}).";
        }

        if (request.Id is not { Length: > 0 and <= MaxIdLength })
        {
            return $"The request ID must be 1–{MaxIdLength} characters.";
        }

        if (request.Command is null || !AgentCommands.All.Contains(request.Command))
        {
            return "Unknown command.";
        }

        if (request.Command is AgentCommands.UpdateIncident or AgentCommands.DeleteIncident && request.Args?.IncidentKey is null)
        {
            return "Which incident? The incident key is missing.";
        }

        if (ValidateControl(request) is { } control)
        {
            return control;
        }

        if (request.Args is { } args)
        {
            if (args.PauseMinutes is { } minutes && minutes is < 1 or > 1440)
            {
                return "A pause must be 1–1440 minutes.";
            }

            if (args.Note is { Length: > MaxNoteLength })
            {
                return $"A note can be at most {MaxNoteLength} characters.";
            }

            if (args.IncidentType is { } type && !Enum.IsDefined(type))
            {
                return "Unknown incident type.";
            }

            if (args.Title is { Length: > MaxTitleLength })
            {
                return $"A title can be at most {MaxTitleLength} characters.";
            }

            if (args.IncidentKey is { } key && !IncidentKey.TryParse(key, CultureInfo.InvariantCulture, out _))
            {
                return "Not an incident key.";
            }

            if (args.TrendMinutes is { } trend && trend is < 1 or > LiveFeed.TrendMinutes)
            {
                return $"A trend must be 1–{LiveFeed.TrendMinutes} minutes.";
            }

            if (args.Selection is { } selection && (selection.Count is 0 or > RecordingSelection.MaxItems
                    || selection.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > MaxSelectionItemLength || item.Any(char.IsControl))))
            {
                return $"A custom recording selects 1–{RecordingSelection.MaxItems} items, each 1–{MaxSelectionItemLength} characters.";
            }
        }

        return null;
    }

    private static string? ValidateControl(AgentRequest request)
    {
        var args = request.Args;
        switch (request.Command)
        {
            case AgentCommands.ProcessAction:
                if (args?.Pid is not > 0 || args.ProcessStart is not > 0 || args.ControlAction is null || !ProcessActions.Contains(args.ControlAction))
                {
                    return "A process action needs the process (ID and start time) and one of end, end-tree or priority.";
                }

                if (args.ControlAction == "priority" && (args.Priority is null || !Priorities.Contains(args.Priority)))
                {
                    return "Unknown priority.";
                }

                break;
            case AgentCommands.ServiceAction:
                if (args?.ServiceName is not { Length: > 0 and <= MaxServiceNameLength } name || name.Any(c => char.IsControl(c) || c is '\\' or '/')
                    || args.ControlAction is null || !ServiceActions.Contains(args.ControlAction))
                {
                    return "A service action needs a service name and one of start, stop, restart or start-type.";
                }

                if (args.ControlAction == "start-type" && (args.StartType is null || !StartTypes.Contains(args.StartType)))
                {
                    return "Unknown start type.";
                }

                break;
            case AgentCommands.TuneUpApply:
                if (args?.ControlAction is null || !TuneUpActions.All.Contains(args.ControlAction))
                {
                    return "Unknown tune-up action.";
                }

                if (args.Items is { } items && (items.Count > MaxItems || items.Any(i => i is null || i.Length > 512 || i.Any(char.IsControl))))
                {
                    return $"A tune-up action takes at most {MaxItems} items of at most 512 characters.";
                }

                if (args.Value is { Length: > 100 })
                {
                    return "The value is too long.";
                }

                break;
        }

        return null;
    }

    private static string Describe(IncidentEditResult result) => result switch
    {
        IncidentEditResult.NotFound => "No such incident.",
        IncidentEditResult.StillRecording => "The incident is still recording; delete it when it is complete.",
        _ => "The incident could not be changed.",
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent command {Command} failed")]
    private static partial void LogCommandFailed(ILogger logger, Exception exception, string command);
}
