using PcDiag.Engine.Ipc;
using PcDiag.Platform.Windows.Ipc;

namespace PcDiag.Agent.Ipc;

/// <summary>Hosts the Agent's command pipe (per-user name, current user and SYSTEM only).</summary>
internal sealed partial class AgentIpcService(AgentCommandHandler handler, ILogger<AgentIpcService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = new NamedPipeServer(NamedPipeServer.CurrentUserPipeName(), handler.HandleAsync, logger);
        try
        {
            await server.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // FirstPipeInstance: something else already created the pipe. Recording still works; commands do not.
            LogPipeUnavailable(logger, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogPipeUnavailable(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The Agent command pipe could not be created; the dashboard and command tools cannot control this Agent")]
    private static partial void LogPipeUnavailable(ILogger logger, Exception exception);
}
