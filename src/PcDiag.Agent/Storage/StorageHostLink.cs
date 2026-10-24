using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Platform.Windows.Ipc;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Agent.Storage;

/// <summary>
/// The recorder's side of the storage host: offers a pipe only the signed-in user can open, starts the host without
/// administrator rights (through Explorer) with a one-time token, and keeps it running. A host that stops is started
/// again (at most 5 times in 10 minutes, then after a pause); when the recorder stops, the pipe closes and the host ends.
/// </summary>
internal sealed partial class StorageHostLink : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private readonly IUnelevatedLauncher _launcher;
    private readonly string _exe;
    private readonly string _dataRoot;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Queue<DateTime> _starts = new();
    private NamedPipeServerStream? _pipe;
    private Task? _run;
    private volatile bool _connected;

    public StorageHostLink(IUnelevatedLauncher launcher, string exe, string dataRoot, ILogger logger)
    {
        _launcher = launcher;
        _exe = exe;
        _dataRoot = dataRoot.TrimEnd('\\', '/');
        _logger = logger;
    }

    /// <summary>Whether the host is connected (running).</summary>
    public bool Connected => _connected;

    /// <summary>Why the host is not running, when it is not.</summary>
    public string? Problem { get; private set; }

    /// <summary>How many times the host was started.</summary>
    public int Started { get; private set; }

    /// <summary>Starts the host (and keeps it running).</summary>
    public void Start() => _run ??= Task.Run(RunAsync);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _pipe?.Dispose();
        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            while (_starts.Count > 0 && now - _starts.Peek() > TimeSpan.FromMinutes(10))
            {
                _starts.Dequeue();
            }

            if (_starts.Count >= 5)
            {
                Problem ??= "The storage host keeps stopping; it is started again in a few minutes.";
                await Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                continue;
            }

            _starts.Enqueue(now);
            try
            {
                await SessionAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Problem = "The storage host stopped: " + ex.Message;
                LogHostStopped(_logger, ex);
            }
            finally
            {
                _connected = false;
                _pipe?.Dispose();
                _pipe = null;
            }

            await Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    /// <summary>One host's life: start it, check its token, then hold the pipe until one of them goes.</summary>
    private async Task SessionAsync()
    {
        var name = "PcDiag.Storage." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, NamedPipeServer.CreateSecurity());
        _pipe = pipe;
        if (!_launcher.TryStart(_exe, $"{StorageHostMode.Verb} --pipe {name} --token {token} --data \"{_dataRoot}\"", Path.GetDirectoryName(_exe), hidden: true))
        {
            throw new IOException("Windows' shell did not start the storage host (Explorer may not be running yet).");
        }

        Started++;
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            wait.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(wait.Token).ConfigureAwait(false);
                var hello = await ReadLineAsync(pipe, wait.Token).ConfigureAwait(false);
                if (hello is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello), Encoding.UTF8.GetBytes(token)))
                {
                    throw new InvalidDataException("Something other than the storage host connected.");
                }
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
                throw new TimeoutException("The storage host did not connect.");
            }
        }

        Problem = null;
        _connected = true;
        LogHostConnected(_logger);

        // Held until the host goes (its end closes) or the recorder stops.
        var buffer = new byte[64];
        while (await pipe.ReadAsync(buffer, _stop.Token).ConfigureAwait(false) > 0)
        {
            // The host sends nothing after its token.
        }

        throw new IOException("The storage host ended.");
    }

    /// <summary>A line of at most 256 bytes, without its end; null when the pipe closed first.</summary>
    private static async Task<string?> ReadLineAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (line.Count < 256 && await pipe.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 1)
        {
            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString([.. line]);
            }

            line.Add(one[0]);
        }

        return null;
    }

    private async Task Delay(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage host connected")]
    private static partial void LogHostConnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage host stopped; it is started again")]
    private static partial void LogHostStopped(ILogger logger, Exception exception);
}

/// <summary>
/// Starts the storage host with the recorder (<c>Agent:StorageHost</c>, on): only from the recorder's own program, never
/// from a test or a tool hosting the recorder's services.
/// </summary>
internal sealed partial class StorageHostService(DataPaths paths, IOptions<AgentOptions> options, ILogger<StorageHostService> logger) : IHostedService, IAsyncDisposable
{
    private StorageHostLink? _link;

    /// <summary>The link to the host, while it is kept running.</summary>
    internal StorageHostLink? Link => _link;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var exe = Environment.ProcessPath;
        if (!options.Value.StorageHost || exe is null || !string.Equals(Path.GetFileName(exe), "PcDiag.Agent.exe", StringComparison.OrdinalIgnoreCase))
        {
            LogNotStarted(logger);
            return Task.CompletedTask;
        }

        _link = new StorageHostLink(new DesktopShell(), exe, paths.Root, logger);
        _link.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _link, null) is { } link)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    [LoggerMessage(Level = LogLevel.Information, Message = "The storage host is not started (switched off, or not the recorder's own program)")]
    private static partial void LogNotStarted(ILogger logger);
}
