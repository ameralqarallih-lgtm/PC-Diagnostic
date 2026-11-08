using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Hosting;

namespace PcDiag.Agent;

/// <summary>
/// Logs the Agent's startup context and clean shutdown, with why it stopped. Recording itself is driven by the RecordingController.
/// </summary>
internal sealed partial class AgentLifetimeService(
    ElevationInfo elevation,
    DataPaths paths,
    IOptions<RecorderOptions> recorderOptions,
    StopReason reason,
    ISystemPowerEvents power,
    ILogger<AgentLifetimeService> logger) : BackgroundService
{
    private void OnSessionEnding(object? sender, EventArgs e) => reason.Set("Windows ended the session (sign-out, restart or shutdown)");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        power.SessionEnding += OnSessionEnding;
        LogStarted(
            logger,
            ProductInfo.Version,
            RuntimeInformation.OSDescription,
            elevation.IsElevated ? "elevated" : "standard user",
            paths.Root,
            recorderOptions.Value.Mode.ToString());

        if (!elevation.IsElevated)
        {
            LogStandardUserMode(logger);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        power.SessionEnding -= OnSessionEnding;
        LogStopped(logger, reason.Why);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Agent {Version} started on {Os} as {Privilege}; data directory {DataDirectory}; recording mode {RecordingMode}")]
    private static partial void LogStarted(ILogger logger, string version, string os, string privilege, string dataDirectory, string recordingMode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Running without elevation: CPU temperature/power, SMART data and Enhanced mode will be reported as RequiresElevation")]
    private static partial void LogStandardUserMode(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Agent stopped cleanly: {Reason}")]
    private static partial void LogStopped(ILogger logger, string reason);
}
