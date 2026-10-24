using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Agent.Tray;
using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;
using PcDiag.Hosting;
using PcDiag.Platform.Windows.Ipc;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Agent;

/// <summary>
/// <c>PcDiag.Agent.exe open</c>, the Start menu's "PC Diagnostic": makes sure the recorder runs (through the
/// installer's scheduled task when there is one, so it gets administrator rights without a prompt; otherwise as this
/// user) and opens the dashboard in the default browser.
/// </summary>
internal static partial class OpenCommand
{
    /// <summary>The command name.</summary>
    public const string Name = "open";

    /// <summary>The scheduled task the installer registers (Task Scheduler path).</summary>
    public const string RecorderTask = @"\PC Diagnostic\Recorder";

    /// <summary>How long to wait for a started recorder to answer.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    public static bool IsCommand(string[] args) => args.Length > 0 && string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var hostArgs = args[1..];
        var builder = Host.CreateApplicationBuilder(hostArgs);
        builder.AddPcDiagHost("launcher");
        builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration.GetSection(AgentOptions.SectionName));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PcDiag.Agent.Open");
        var paths = host.Services.GetRequiredService<DataPaths>();
        paths.EnsureCreated();

        if (!await AgentRunningAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
        {
            StartRecorder(hostArgs, logger);
            var deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline && !await AgentRunningAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
            {
                await Task.Delay(500).ConfigureAwait(false);
            }
        }

        var launcher = new DashboardLauncher(paths, host.Services.GetRequiredService<IOptions<AgentOptions>>().Value, ElevationDetector.Detect(),
            TimeProvider.System, logger, new DesktopShell());
        var launch = await launcher.OpenAsync(CancellationToken.None).ConfigureAwait(false);
        switch (launch.Result)
        {
            case DashboardLaunchResult.Opened:
                return 0;
            case DashboardLaunchResult.CopyLink:
                OnUiThread(() =>
                {
                    Clipboard.SetText(launch.Url!.ToString());
                    MessageBox.Show("The browser could not be opened. The dashboard link is on the clipboard: paste it into your browser within 2 minutes.",
                        "PC Diagnostic", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
                return 0;
            default:
                OnUiThread(() => MessageBox.Show(launch.Problem ?? "The dashboard could not be started.", "PC Diagnostic", MessageBoxButtons.OK, MessageBoxIcon.Warning));
                return 1;
        }
    }

    /// <summary>The clipboard and message boxes need a single-threaded apartment; Main is not one.</summary>
    private static void OnUiThread(Action action)
    {
        var thread = new Thread(() => action()) { IsBackground = false };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static async Task<bool> AgentRunningAsync(TimeSpan timeout)
    {
        try
        {
            var client = await AgentIpcClient.ConnectAsync(NamedPipeServer.CurrentUserPipeName(), timeout, CancellationToken.None).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts the recorder: the installer's task (elevated, no prompt) when it exists, else this program as the user.</summary>
    private static void StartRecorder(string[] hostArgs, ILogger logger)
    {
        // schtasks.exe from System32 with fixed arguments (ARCHITECTURE §11).
        var schtasks = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        schtasks.ArgumentList.Add("/Run");
        schtasks.ArgumentList.Add("/TN");
        schtasks.ArgumentList.Add(RecorderTask);
        try
        {
            using var run = Process.Start(schtasks)!;
            run.StandardOutput.ReadToEnd();
            run.StandardError.ReadToEnd();
            if (run.WaitForExit(10_000) && run.ExitCode == 0)
            {
                LogStartedTask(logger, RecorderTask);
                return;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogTaskFailed(logger, ex);
        }

        var self = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in hostArgs)
        {
            self.ArgumentList.Add(argument);
        }

        using var agent = Process.Start(self);
        LogStartedSelf(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the recorder through the scheduled task {Task}")]
    private static partial void LogStartedTask(ILogger logger, string task);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not run the recorder's scheduled task")]
    private static partial void LogTaskFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the recorder without administrator rights (no scheduled task)")]
    private static partial void LogStartedSelf(ILogger logger);
}
