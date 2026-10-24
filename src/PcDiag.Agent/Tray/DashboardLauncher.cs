using System.Diagnostics;
using System.Text.Json;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Agent.Tray;

/// <summary>What "Open dashboard" did.</summary>
internal enum DashboardLaunchResult
{
    /// <summary>The default browser was opened with a one-time link.</summary>
    Opened,

    /// <summary>The browser could not be opened: the link is in <see cref="DashboardLaunch.Url"/> for the clipboard.</summary>
    CopyLink,

    /// <summary>The dashboard is not running and could not be started without administrator rights.</summary>
    NotRunningElevated,

    /// <summary>Starting or opening failed; see <see cref="DashboardLaunch.Problem"/>.</summary>
    Failed,
}

/// <summary>Outcome of "Open dashboard".</summary>
internal sealed record DashboardLaunch(DashboardLaunchResult Result, Uri? Url = null, string? Problem = null);

/// <summary>
/// Opens the web dashboard from the tray or the Start menu: finds the running Web host (web.json), starts it when
/// needed, writes a one-time launch ticket and opens the link. An elevated Agent never starts the Web host or a
/// browser itself, because they would inherit administrator rights: it asks the desktop shell (Explorer) to start
/// them as the signed-in user, and offers the link for the clipboard when that is not possible.
/// </summary>
internal sealed partial class DashboardLauncher(DataPaths paths, AgentOptions options, ElevationInfo elevation, TimeProvider time, ILogger logger, IUnelevatedLauncher shell)
{
    /// <summary>How long to wait for a started Web host to publish its address.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private const string WebHostExe = "PcDiag.Web.exe";

    public async Task<DashboardLaunch> OpenAsync(CancellationToken cancellationToken)
    {
        var baseUrl = RunningBaseUrl();
        if (baseUrl is null)
        {
            var exe = WebHostPath();
            if (exe is null)
            {
                return new DashboardLaunch(DashboardLaunchResult.Failed, Problem: $"{WebHostExe} was not found. Set Agent:WebHostPath in settings.json.");
            }

            if (elevation.IsElevated)
            {
                if (!shell.TryStart(exe, $"\"--Storage:DataDirectory={paths.Root}\"", Path.GetDirectoryName(exe), hidden: true))
                {
                    LogShellFailed(logger, exe);
                    return new DashboardLaunch(DashboardLaunchResult.NotRunningElevated);
                }

                LogStarted(logger, exe);
            }
            else if (!Start(exe))
            {
                return new DashboardLaunch(DashboardLaunchResult.Failed, Problem: "The dashboard could not be started. See the Agent log.");
            }

            baseUrl = await WaitForWebHostAsync(cancellationToken).ConfigureAwait(false);
            if (baseUrl is null)
            {
                return new DashboardLaunch(DashboardLaunchResult.Failed, Problem: "The dashboard did not start in time. See the web log in the logs folder.");
            }
        }

        var url = new Uri(baseUrl, "/auth/exchange?t=" + LaunchTickets.Issue(paths, time.GetUtcNow()));
        if (elevation.IsElevated)
        {
            // Only ever our own loopback URL, opened by Explorer in the user's default browser.
            return shell.TryStart(url.ToString(), null, null, hidden: false)
                ? new DashboardLaunch(DashboardLaunchResult.Opened, url)
                : new DashboardLaunch(DashboardLaunchResult.CopyLink, url);
        }

        try
        {
            // Only ever our own loopback URL, opened with the user's default browser.
            using var browser = Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
            return new DashboardLaunch(DashboardLaunchResult.Opened, url);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogBrowserFailed(logger, ex);
            return new DashboardLaunch(DashboardLaunchResult.CopyLink, url);
        }
    }

    /// <summary>
    /// PcDiag.Web.exe: the configured path, else next to the Agent (development and preview builds), else in the
    /// installed layout's web folder beside the Agent's folder.
    /// </summary>
    internal string? WebHostPath()
    {
        if (!string.IsNullOrWhiteSpace(options.WebHostPath))
        {
            return File.Exists(options.WebHostPath) ? options.WebHostPath : null;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, WebHostExe);
        if (File.Exists(beside))
        {
            return beside;
        }

        var installed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "web", WebHostExe));
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>The running Web host's address from web.json, when its process is alive.</summary>
    internal Uri? RunningBaseUrl()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(paths.WebStateFile));
            var root = document.RootElement;
            if (!root.TryGetProperty("ProcessId", out var pid) || !root.TryGetProperty("BaseUrl", out var url)
                || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out var baseUrl)
                || !baseUrl.IsLoopback || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps))
            {
                return null;
            }

            using var process = Process.GetProcessById(pid.GetInt32());
            return !process.HasExited && process.ProcessName.Equals("PcDiag.Web", StringComparison.OrdinalIgnoreCase) ? baseUrl : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private bool Start(string exe)
    {
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };

            // Same data folder as this Agent, whatever configured it.
            start.ArgumentList.Add($"--Storage:DataDirectory={paths.Root}");
            using var process = Process.Start(start);
            LogStarted(logger, exe);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            LogStartFailed(logger, ex, exe);
            return false;
        }
    }

    private async Task<Uri?> WaitForWebHostAsync(CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + StartTimeout;
        while (time.GetUtcNow() < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, cancellationToken).ConfigureAwait(false);
            if (RunningBaseUrl() is { } url)
            {
                return url;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Started the dashboard web host {Path}")]
    private static partial void LogStarted(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not start the dashboard web host {Path}")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The desktop shell could not start {Path} without administrator rights")]
    private static partial void LogShellFailed(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open the default browser; offering the link instead")]
    private static partial void LogBrowserFailed(ILogger logger, Exception exception);
}
