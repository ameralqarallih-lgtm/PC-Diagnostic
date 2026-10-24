using System.Security.Principal;
using PcDiag.Core.Ipc;

namespace PcDiag.Web.Agent;

/// <summary>Talks to the recorder Agent.</summary>
public interface IAgentClient
{
    /// <summary>Why the Agent cannot be reached right now (shown to the user).</summary>
    string UnavailableReason { get; }

    /// <summary>Sends a command; null when the Agent is not reachable.</summary>
    Task<AgentResponse?> SendAsync(string command, AgentRequestArgs? args, CancellationToken cancellationToken);
}

/// <summary>
/// The Agent's named pipe for the current Windows account. Requests on a connection are serialized (the Agent
/// answers in order); process, service and tune-up commands, which can take minutes (a clean-up, stopping services),
/// have a connection of their own so the live view never waits behind them. Connection failures are remembered for a
/// few seconds so a stopped Agent does not make every request wait for the connect timeout.
/// </summary>
public sealed partial class AgentPipeClient : IAgentClient, IAsyncDisposable
{
    /// <summary>How long to wait for the pipe.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>After a failed connect, requests fail fast for this long.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Longest wait for an answer.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Longest wait for a capture (it preserves the pre-window before answering).</summary>
    public static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The Agent closes idle connections after 2 minutes; reconnect before that.</summary>
    public static readonly TimeSpan IdleReconnect = TimeSpan.FromSeconds(90);

    private const string NotRunning = "The recorder Agent is not running.";

    private readonly string? _pipeName;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentPipeClient> _logger;
    private readonly Connection _main = new();
    private readonly Connection _control = new();
    private DateTimeOffset _retryAfter;
    private string _unavailable = NotRunning;

    /// <summary>Creates the client; a null pipe name means the platform has no Agent (e.g. Linux containers).</summary>
    public AgentPipeClient(string? pipeName, TimeProvider time, ILogger<AgentPipeClient> logger)
    {
        _pipeName = pipeName;
        _time = time;
        _logger = logger;
        if (pipeName is null)
        {
            _unavailable = "The recorder Agent runs on Windows only; this host shows stored data.";
        }
    }

    /// <summary>The pipe of the Agent running under the current Windows account; null on other platforms.</summary>
    public static string? CurrentUserPipeName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return identity.User is { } sid ? AgentIpc.PipeName(sid.Value) : null;
    }

    /// <inheritdoc />
    public string UnavailableReason => _unavailable;

    /// <inheritdoc />
    public async Task<AgentResponse?> SendAsync(string command, AgentRequestArgs? args, CancellationToken cancellationToken)
    {
        if (_pipeName is null)
        {
            return null;
        }

        // Reads can be retried on a fresh connection; commands with effects are never sent twice.
        var idempotent = command is AgentCommands.Status or AgentCommands.Live or AgentCommands.Trend or AgentCommands.ControlProcesses
            or AgentCommands.ControlServices or AgentCommands.TuneUpScan or AgentCommands.TuneUpPlan or AgentCommands.CleanupFiles;
        var connection = IsControl(command) ? _control : _main;
        await connection.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var fresh = false;
                if (connection.Client is not null && _time.GetUtcNow() - connection.LastUsed >= IdleReconnect)
                {
                    await connection.DropAsync().ConfigureAwait(false);
                }

                if (connection.Client is null)
                {
                    if (!await ConnectAsync(connection, cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }

                    fresh = true;
                }

                using var timeout = new CancellationTokenSource(TimeoutFor(command), _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    var response = await connection.Client!.SendAsync(command, args, linked.Token).ConfigureAwait(false);
                    connection.LastUsed = _time.GetUtcNow();
                    return response;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException
                                           || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    // The connection state is unknown after a failure or timeout: never reuse it.
                    await connection.DropAsync().ConfigureAwait(false);
                    if (fresh || !idempotent || attempt > 0)
                    {
                        LogRequestFailed(_logger, ex, command);
                        _unavailable = ex is OperationCanceledException ? "The recorder Agent did not answer in time." : "The connection to the recorder Agent was lost.";
                        return null;
                    }
                }
            }
        }
        finally
        {
            connection.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _main.DisposeAsync().ConfigureAwait(false);
        await _control.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Process, service and tune-up commands (on their own connection).</summary>
    private static bool IsControl(string command) => command is AgentCommands.ControlProcesses or AgentCommands.ControlServices or AgentCommands.ProcessAction
        or AgentCommands.ServiceAction or AgentCommands.TuneUpScan or AgentCommands.TuneUpApply or AgentCommands.TuneUpPlan or AgentCommands.CleanupFiles;

    /// <summary>How long a command may take: a capture saves its pre-window first; a clean-up or stopping services takes minutes.</summary>
    internal static TimeSpan TimeoutFor(string command) => command switch
    {
        AgentCommands.Capture => CaptureTimeout,
        AgentCommands.TuneUpApply => TimeSpan.FromMinutes(10),
        AgentCommands.ServiceAction => TimeSpan.FromMinutes(1),
        // An import is copied, checked and unpacked, then waits for the screen host to load it (up to 10 s).
        AgentCommands.DriverImport or AgentCommands.DriverRemove or AgentCommands.OverlayScreenDriverApprove or AgentCommands.WindowsDriverImport => TimeSpan.FromSeconds(45),

        // Waits for the person at the PC to confirm (2 minutes at most), then for Windows to install or remove it.
        AgentCommands.WindowsDriverInstall or AgentCommands.WindowsDriverRemove => TimeSpan.FromMinutes(6),
        AgentCommands.WindowsDrivers => TimeSpan.FromSeconds(30),
        AgentCommands.TuneUpScan => TimeSpan.FromSeconds(90),
        AgentCommands.ControlProcesses or AgentCommands.ControlServices or AgentCommands.ProcessAction => TimeSpan.FromSeconds(30),
        _ => RequestTimeout,
    };

    /// <summary>One pipe connection and the lock that keeps its requests in order.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public AgentIpcClient? Client { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        public async Task DropAsync()
        {
            if (Client is { } client)
            {
                Client = null;
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DropAsync().ConfigureAwait(false);
            Gate.Dispose();
        }
    }

    private async Task<bool> ConnectAsync(Connection connection, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (now < _retryAfter)
        {
            return false;
        }

        try
        {
            connection.Client = await AgentIpcClient.ConnectAsync(_pipeName!, ConnectTimeout, cancellationToken).ConfigureAwait(false);
            connection.LastUsed = now;
            _unavailable = NotRunning;
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _retryAfter = now + RetryDelay;
            _unavailable = ex is UnauthorizedAccessException
                ? "The recorder pipe belongs to another Windows account, so it was not trusted."
                : NotRunning;
            if (ex is UnauthorizedAccessException)
            {
                LogUntrustedPipe(_logger);
            }

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent command {Command} failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Agent pipe is owned by another account; not connecting")]
    private static partial void LogUntrustedPipe(ILogger logger);
}
