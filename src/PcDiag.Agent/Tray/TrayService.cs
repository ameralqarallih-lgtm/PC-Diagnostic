using System.Windows.Forms;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Engine.Recording;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Agent.Tray;

/// <summary>
/// Runs the tray icon and capture hotkey on a dedicated STA thread with a Windows Forms message loop.
/// Skipped when there is no interactive desktop (e.g. a service) or when both are turned off.
/// </summary>
internal sealed partial class TrayService(
    IOptions<AgentOptions> options,
    DataPaths paths,
    ElevationInfo elevation,
    TimeProvider time,
    IRecordingControl recorder,
    IIncidentCapture capture,
    IHostApplicationLifetime lifetime,
    ILogger<TrayService> logger,
    Core.Overlay.IOverlayControl? overlay = null) : IHostedService
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private TrayContext? _context;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.ShowTrayIcon && string.IsNullOrWhiteSpace(settings.CaptureHotkey))
        {
            return;
        }

        if (!Environment.UserInteractive)
        {
            LogNoDesktop(logger);
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "pcdiag-tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _context?.ExitFromAnyThread();
        _thread?.Join(TimeSpan.FromSeconds(3));
        return Task.CompletedTask;
    }

    private void Run()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var dashboard = new DashboardLauncher(paths, options.Value, elevation, time, logger, new DesktopShell());
            using var context = new TrayContext(recorder, capture, lifetime, dashboard, options.Value, logger, overlay);
            _context = context;
            _ready.TrySetResult();
            Application.Run(context);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogTrayFailed(logger, ex);
            _ready.TrySetResult();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No interactive desktop: the tray icon and capture hotkey are not available")]
    private static partial void LogNoDesktop(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The tray icon failed; recording continues without it")]
    private static partial void LogTrayFailed(ILogger logger, Exception exception);
}
