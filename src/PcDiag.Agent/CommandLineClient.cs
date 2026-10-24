using System.Globalization;
using System.Text.Json;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Platform.Windows.Ipc;

namespace PcDiag.Agent;

/// <summary>
/// Sends one command to the running Agent and prints the answer as JSON:
/// <c>PcDiag.Agent.exe status | start | stop | pause [minutes] | resume | capture [type] [note]</c>.
/// Exit codes: 0 = done, 1 = the Agent refused the command, 4 = no Agent is running, 5 = the Agent turned the
/// connection away (too many open), 64 = usage error.
/// </summary>
internal static class CommandLineClient
{
    public const int Done = 0;
    public const int Refused = 1;
    public const int NotRunning = 4;
    public const int Busy = 5;
    public const int Usage = 64;

    private static readonly JsonSerializerOptions Pretty = new(AgentIpcJsonContext.Default.Options) { WriteIndented = true };

    /// <summary>True when the arguments are a client command rather than host options.</summary>
    public static bool IsCommand(string[] args) => args.Length > 0 && AgentCommands.All.Contains(args[0].ToLowerInvariant());

    public static async Task<int> RunAsync(string[] args)
    {
        var command = args[0].ToLowerInvariant();
        AgentRequestArgs? arguments = null;
        if (command == AgentCommands.Pause && args.Length > 1)
        {
            if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                await Console.Error.WriteLineAsync("Usage: pause [minutes]").ConfigureAwait(false);
                return Usage;
            }

            arguments = new AgentRequestArgs(PauseMinutes: minutes);
        }
        else if (command == AgentCommands.Capture && args.Length > 1)
        {
            if (!Enum.TryParse<UserIncidentType>(args[1], ignoreCase: true, out var type) || !Enum.IsDefined(type))
            {
                await Console.Error.WriteLineAsync("Usage: capture [" + string.Join("|", Enum.GetNames<UserIncidentType>()) + "] [note]").ConfigureAwait(false);
                return Usage;
            }

            arguments = new AgentRequestArgs(IncidentType: type, Note: args.Length > 2 ? string.Join(' ', args[2..]) : null);
        }

        AgentIpcClient client;
        try
        {
            client = await AgentIpcClient.ConnectAsync(NamedPipeServer.CurrentUserPipeName(), TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync("PC Diagnostic Agent is not running in this session.").ConfigureAwait(false);
            return NotRunning;
        }

        await using (client.ConfigureAwait(false))
        {
            AgentResponse response;
            try
            {
                response = await client.SendAsync(command, arguments, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The Agent closes a connection it cannot take (its limit of open connections is reached: Web hosts
                // and other clients are connected).
                await Console.Error.WriteLineAsync("PC Diagnostic Agent is busy: it has as many connections open as it takes. Try again shortly.").ConfigureAwait(false);
                return Busy;
            }

            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, Pretty)).ConfigureAwait(false);
            return response.Ok ? Done : Refused;
        }
    }
}
