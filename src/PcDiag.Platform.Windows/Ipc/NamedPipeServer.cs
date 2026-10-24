using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Ipc;

namespace PcDiag.Platform.Windows.Ipc;

/// <summary>
/// The Agent's command pipe. Access: the interactive user (also the owner, so clients using
/// <see cref="PipeOptions.CurrentUserOnly"/> can verify it) and SYSTEM; network logons are denied. The first instance
/// is created with <see cref="PipeOptions.FirstPipeInstance"/>, so the Agent fails to start rather than share a name
/// someone else created first. At most <see cref="MaxConnections"/> clients at a time; idle connections are closed.
/// </summary>
public sealed partial class NamedPipeServer
{
    /// <summary>Concurrent client connections.</summary>
    public const int MaxConnections = 4;

    /// <summary>A connection that sends nothing for this long is closed.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    private readonly string _pipeName;
    private readonly Func<AgentRequest, CancellationToken, Task<AgentResponse>> _handler;
    private readonly ILogger _logger;

    /// <summary>Creates the server.</summary>
    public NamedPipeServer(string pipeName, Func<AgentRequest, CancellationToken, Task<AgentResponse>> handler, ILogger logger)
    {
        _pipeName = pipeName;
        _handler = handler;
        _logger = logger;
    }

    /// <summary>Raised once the first pipe instance exists and clients can connect.</summary>
    public event EventHandler? Listening;

    /// <summary>The pipe's security: owner and read/write for the current user, full control for SYSTEM, none for network logons.</summary>
    public static PipeSecurity CreateSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>The pipe name for the current user.</summary>
    public static string CurrentUserPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return AgentIpc.PipeName(identity.User!.Value);
    }

    /// <summary>Serves clients until cancelled.</summary>
    /// <exception cref="IOException">Another process already owns the pipe name.</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                MaxConnections + 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
                4096,
                4096,
                CreateSecurity());
            if (first)
            {
                first = false;
                LogListening(_logger, _pipeName);
                Listening?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            catch (IOException ex)
            {
                // A client that connected and left before the connection was taken ("the pipe is being closed", e.g. a
                // probe that only checks the recorder is up): that one instance is gone, the pipe keeps listening.
                LogConnectionLost(_logger, ex.Message);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            connections.RemoveAll(t => t.IsCompleted);
            if (connections.Count >= MaxConnections)
            {
                LogTooMany(_logger, MaxConnections);
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            connections.Add(Task.Run(() => ServeAsync(pipe, cancellationToken), CancellationToken.None));
        }

        await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(IdleTimeout);
                AgentRequest? request;
                try
                {
                    request = await AgentIpc.ReadAsync(pipe, AgentIpcJsonContext.Default.AgentRequest, idle.Token).ConfigureAwait(false);
                }
                catch (InvalidDataException ex)
                {
                    // Not our protocol: answer once and drop the connection.
                    LogBadMessage(_logger, ex.Message);
                    await TryWriteAsync(pipe, new AgentResponse(string.Empty, false, "Malformed request."), cancellationToken).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return; // idle timeout or stopping
                }
                catch (IOException)
                {
                    return; // client went away
                }

                if (request is null)
                {
                    return;
                }

                var response = await _handler(request, cancellationToken).ConfigureAwait(false);
                if (!await TryWriteAsync(pipe, response, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
    }

    private static async Task<bool> TryWriteAsync(NamedPipeServerStream pipe, AgentResponse response, CancellationToken cancellationToken)
    {
        try
        {
            await AgentIpc.WriteAsync(pipe, response, AgentIpcJsonContext.Default.AgentResponse, cancellationToken, AgentIpc.MaxResponseBytes).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent command pipe {Pipe} is listening (current user and SYSTEM only)")]
    private static partial void LogListening(ILogger logger, string pipe);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a pipe client: {Max} connections are already open")]
    private static partial void LogTooMany(ILogger logger, int max);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A pipe client left before its connection was taken ({Problem}); still listening")]
    private static partial void LogConnectionLost(ILogger logger, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected a malformed pipe message: {Problem}")]
    private static partial void LogBadMessage(ILogger logger, string problem);
}
