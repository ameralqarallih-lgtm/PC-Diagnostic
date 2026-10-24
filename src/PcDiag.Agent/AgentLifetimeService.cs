using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Hosting;

namespace PcDiag.Agent;

/// <summary>
/// Logs the Agent's startup context and clean shutdown. Recording itself is driven by the RecordingController.
/// </summary>
internal sealed partial class AgentLifetimeService(
    ElevationInfo elevation,
    DataPaths paths,
    IOptions<RecorderOptions> recorderOptions,
    ILogger<AgentLifetimeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
        LogStopped(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Agent {Version} started on {Os} as {Privilege}; data directory {DataDirectory}; recording mode {RecordingMode}")]
    private static partial void LogStarted(ILogger logger, string version, string os, string privilege, string dataDirectory, string recordingMode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Running without elevation: CPU temperature/power, SMART data and Enhanced mode will be reported as RequiresElevation")]
    private static partial void LogStandardUserMode(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Agent stopped cleanly")]
    private static partial void LogStopped(ILogger logger);
}
