using System.Globalization;
using System.Windows.Forms;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Engine.Recording;

namespace PcDiag.Agent.Tray;

/// <summary>
/// The notification-area icon and menu (dashboard, capture, quick types, recording, pause, exit) and the capture hotkey.
/// Runs on its own STA thread; work happens on the thread pool and results come back through the UI context.
/// </summary>
internal sealed partial class TrayContext : ApplicationContext
{
    private const string Product = "PC Diagnostic";

    private readonly IRecordingControl _recorder;
    private readonly IIncidentCapture _capture;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly DashboardLauncher _dashboard;
    private readonly AgentOptions _options;
    private readonly ILogger _logger;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon? _icon;
    private readonly HotkeyWindow? _hotkey;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private readonly Dictionary<TrayState, Icon> _icons = [];
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _startStop = new();
    private readonly ToolStripMenuItem _pause = new("Pause recording");
    private readonly ToolStripMenuItem _resume = new("Resume recording");
    private TrayState? _shownState;
    private readonly Core.Overlay.IOverlayControl? _overlay;
    private readonly ToolStripMenuItem _arrange = new("Arrange on-screen readings");
    private readonly ToolStripMenuItem _showClosed = new("Show closed panels") { Visible = false };

    public TrayContext(IRecordingControl recorder, IIncidentCapture capture, IHostApplicationLifetime lifetime, DashboardLauncher dashboard, AgentOptions options, ILogger logger,
        Core.Overlay.IOverlayControl? overlay = null)
    {
        _overlay = overlay;
        _recorder = recorder;
        _capture = capture;
        _lifetime = lifetime;
        _dashboard = dashboard;
        _options = options;
        _logger = logger;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        HotkeyText = null;
        if (!string.IsNullOrWhiteSpace(options.CaptureHotkey) && HotkeyGesture.TryParse(options.CaptureHotkey, out _) is { } gesture)
        {
            _hotkey = new HotkeyWindow();
            _hotkey.Pressed += (_, _) => Capture(IncidentSource.Hotkey, null);
            var error = _hotkey.Register(gesture);
            if (error == 0)
            {
                HotkeyText = gesture.Text;
                LogHotkey(_logger, gesture.Text);
            }
            else
            {
                LogHotkeyFailed(_logger, gesture.Text, error);
            }
        }

        if (options.ShowTrayIcon)
        {
            _icon = new NotifyIcon { ContextMenuStrip = BuildMenu(), Visible = true };
            _icon.DoubleClick += (_, _) => Capture(IncidentSource.Tray, null);
            Refresh();
            _refresh.Tick += (_, _) => Refresh();
            _refresh.Start();
        }

        _capture.Finished += OnFinished;
    }

    /// <summary>The registered hotkey, e.g. "Ctrl+Alt+Shift+I"; null when none is active.</summary>
    public string? HotkeyText { get; }

    /// <summary>Ends the tray thread from any thread.</summary>
    public void ExitFromAnyThread() => _ui.Post(_ => ExitThread(), null);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _capture.Finished -= OnFinished;

            _refresh.Dispose();
            _hotkey?.Dispose();
            if (_icon is not null)
            {
                _icon.Visible = false;
                _icon.ContextMenuStrip?.Dispose();
                _icon.Dispose();
            }

            foreach (var icon in _icons.Values)
            {
                icon.Dispose();
            }

            _status.Dispose();
            _startStop.Dispose();
            _pause.Dispose();
            _resume.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        var capture = new ToolStripMenuItem("Capture incident", null, (_, _) => Capture(IncidentSource.Tray, null))
        {
            Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold),
            ShortcutKeyDisplayString = HotkeyText,
        };
        var captureAs = new ToolStripMenuItem("Capture as");
        foreach (var type in Enum.GetValues<UserIncidentType>())
        {
            captureAs.DropDownItems.Add(type.ToDisplayName(), null, (_, _) => Capture(IncidentSource.Tray, type));
        }

        _startStop.Click += (_, _) => Run(_recorder.State is RecorderState.Recording
            ? ct => _recorder.StopRecordingByUserAsync(ct)
            : ct => _recorder.StartRecordingByUserAsync(ct));
        _pause.DropDownItems.Add("For 15 minutes", null, (_, _) => Run(ct => _recorder.PauseAsync(TimeSpan.FromMinutes(15), ct)));
        _pause.DropDownItems.Add("For 1 hour", null, (_, _) => Run(ct => _recorder.PauseAsync(TimeSpan.FromHours(1), ct)));
        _pause.DropDownItems.Add("Until I resume", null, (_, _) => Run(ct => _recorder.PauseAsync(null, ct)));
        _resume.Click += (_, _) => Run(ct => _recorder.ResumeAsync(ct));

        var dashboard = new ToolStripMenuItem("Open dashboard", null, (_, _) => OpenDashboard());
        menu.Items.AddRange([_status, dashboard, new ToolStripSeparator(), capture, captureAs, new ToolStripSeparator(), _startStop, _pause, _resume, new ToolStripSeparator()]);
        if (_overlay is not null)
        {
            // The on-screen panels: move, resize and edit them on the screens themselves.
            _arrange.Click += (_, _) => _overlay.Arrange(!_overlay.Arranging);
            _showClosed.Click += (_, _) => _overlay.ShowClosed();
            menu.Items.AddRange([_arrange, _showClosed, new ToolStripSeparator()]);
        }

        menu.Items.Add("Exit PC Diagnostic", null, (_, _) => _lifetime.StopApplication());
        menu.Opening += (_, _) => Refresh();
        return menu;
    }

    private void Refresh()
    {
        if (_icon is null)
        {
            return;
        }

        var state = _recorder.State;
        var active = _capture.Active;
        var trayState = active is not null ? TrayState.CapturingIncident
            : state is RecorderState.Recording or RecorderState.Starting ? TrayState.Recording
            : state == RecorderState.Paused ? TrayState.Paused
            : TrayState.Idle;

        var text = trayState switch
        {
            TrayState.CapturingIncident => $"Recording {active!.Key}, {Remaining(_capture.ActiveUntil)} left",
            TrayState.Recording => "Recording",
            TrayState.Paused => _recorder.PausedUntil == DateTimeOffset.MaxValue ? "Paused until you resume" : $"Paused until {Local(_recorder.PausedUntil)}",
            _ => "Not recording",
        };
        _status.Text = text;
        _arrange.Text = _overlay?.Arranging == true ? "Done arranging on-screen readings" : "Arrange on-screen readings";
        var closed = _overlay?.ClosedPanels ?? 0;
        _showClosed.Visible = closed > 0;
        _showClosed.Text = closed == 1 ? "Show the closed panel" : $"Show the {closed} closed panels";
        _startStop.Text = state == RecorderState.Recording ? "Stop recording" : "Start recording";
        _pause.Visible = state != RecorderState.Paused;
        _resume.Visible = state == RecorderState.Paused;
        _icon.Text = Truncate($"{Product}: {text}", 127);

        if (_shownState != trayState)
        {
            if (!_icons.TryGetValue(trayState, out var icon))
            {
                _icons[trayState] = icon = TrayIcons.Create(trayState, SystemInformation.SmallIconSize);
            }

            _icon.Icon = icon;
            _shownState = trayState;
        }
    }

    private void Capture(IncidentSource source, UserIncidentType? type)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await _capture.CaptureAsync(new IncidentTrigger(DateTimeOffset.UtcNow, source, type), CancellationToken.None).ConfigureAwait(false);
                var message = outcome.Merged
                    ? $"Added to {outcome.Incident.Key}, which is still recording."
                    : outcome.SnapshotOnly
                        ? $"{outcome.Incident.Key}: the recorder was not running, so there is no data from before. Recording until {Local(outcome.RecordingUntil)}."
                        : $"{outcome.Incident.Key} saved. Recording until {Local(outcome.RecordingUntil)}.";
                Notify("Incident captured", message, ToolTipIcon.Info);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogActionFailed(_logger, ex, "capture");
                Notify("Capture failed", "The incident could not be captured. See the Agent log for details.", ToolTipIcon.Error);
            }
        });
    }

    private void Run(Func<CancellationToken, Task> action)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogActionFailed(_logger, ex, "recording control");
            }

            _ui.Post(_ => Refresh(), null);
        });
    }

    private void OpenDashboard()
    {
        _ = Task.Run(async () =>
        {
            DashboardLaunch launch;
            try
            {
                launch = await _dashboard.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogActionFailed(_logger, ex, "open dashboard");
                launch = new DashboardLaunch(DashboardLaunchResult.Failed, Problem: "The dashboard could not be opened. See the Agent log.");
            }

            _ui.Post(_ => ShowLaunch(launch), null);
        });
    }

    private void ShowLaunch(DashboardLaunch launch)
    {
        switch (launch.Result)
        {
            case DashboardLaunchResult.CopyLink:
                try
                {
                    Clipboard.SetText(launch.Url!.ToString());
                    Balloon("Dashboard link copied", "Paste it into your browser within 2 minutes. It works once.", ToolTipIcon.Info);
                }
                catch (System.Runtime.InteropServices.ExternalException ex)
                {
                    LogActionFailed(_logger, ex, "copy dashboard link");
                    Balloon("Dashboard", "The link could not be copied to the clipboard. Try again.", ToolTipIcon.Warning);
                }

                break;
            case DashboardLaunchResult.NotRunningElevated:
                Balloon("Dashboard not running", "The dashboard could not be started without administrator rights. Open PC Diagnostic from the Start menu instead.", ToolTipIcon.Warning);
                break;
            case DashboardLaunchResult.Failed:
                Balloon("Dashboard", launch.Problem ?? "The dashboard could not be opened.", ToolTipIcon.Warning);
                break;
        }
    }

    private void Balloon(string title, string text, ToolTipIcon icon) => _icon?.ShowBalloonTip(5000, title, text, icon);


    private void OnFinished(object? sender, IncidentCapturedEventArgs e)
    {
        var incident = e.Outcome.Incident;
        Notify(incident.State == IncidentState.Complete ? "Incident complete" : $"Incident {incident.State.ToString().ToLowerInvariant()}",
            $"{incident.Key}: {incident.Title}", incident.State == IncidentState.Failed ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (_icon is null || !_options.NotifyOnCapture)
        {
            return;
        }

        _ui.Post(_ =>
        {
            Refresh();
            _icon.ShowBalloonTip(5000, title, text, icon);
        }, null);
    }

    private static string Remaining(DateTimeOffset? until)
    {
        var left = (until ?? DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow;
        return left <= TimeSpan.Zero ? "0:00" : left.ToString(@"m\:ss", CultureInfo.CurrentCulture);
    }

    private static string Local(DateTimeOffset? time) =>
        time is { } t ? t.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "?";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture hotkey {Hotkey} registered")]
    private static partial void LogHotkey(ILogger logger, string hotkey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Capture hotkey {Hotkey} could not be registered (error {Error}); another application may be using it. The tray menu still works")]
    private static partial void LogHotkeyFailed(ILogger logger, string hotkey, int error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tray {Action} failed")]
    private static partial void LogActionFailed(ILogger logger, Exception exception, string action);
}
