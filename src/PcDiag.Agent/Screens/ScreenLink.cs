using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using PcDiag.Platform.Windows.Ipc;
using PcDiag.Platform.Windows.Shell;
using PcDiag.Screens;
using static PcDiag.Screens.ScreenHostProtocol;

namespace PcDiag.Agent.Screens;

/// <summary>
/// The recorder's side of the screen host: offers a pipe only the signed-in user can open, starts the host without
/// administrator rights (through Explorer) with a one-time token, and keeps what it reports (the packages, the screens
/// connected and each session's state). A host that goes away is started again (at most 5 times in 10 minutes), and
/// the screens that were in use are opened again.
/// </summary>
internal sealed partial class ScreenLink : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);
    private readonly IUnelevatedLauncher _launcher;
    private readonly Func<Setup> _setup;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, Status> _statuses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _wanted = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _starts = new();
    private NamedPipeServerStream? _pipe;
    private Task? _run;

    public ScreenLink(IUnelevatedLauncher launcher, Func<Setup> setup, ILogger logger)
    {
        _launcher = launcher;
        _setup = setup;
        _logger = logger;
    }

    /// <summary>Something the host reported changed (packages, screens, a session).</summary>
    public event Action? Changed;

    /// <summary>The packages the host found (empty until it is connected).</summary>
    public IReadOnlyList<PackageInfo> Packages { get; private set; } = [];

    /// <summary>The screens connected now.</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; private set; } = [];

    /// <summary>Whether the host is connected.</summary>
    public bool Connected => _pipe?.IsConnected == true;

    /// <summary>Why the host is not running, when it is not.</summary>
    public string? Problem { get; private set; }

    /// <summary>A screen's session state, when it has one.</summary>
    public Status? StatusOf(string key) => _statuses.TryGetValue(key, out var status) ? status : null;

    /// <summary>Starts the host (and keeps it running).</summary>
    public void Start() => _run ??= Task.Run(RunAsync);

    /// <summary>Starts again (after an approval: the host loads packages when it starts).</summary>
    public void Restart()
    {
        _pipe?.Dispose();
    }

    /// <summary>Opens a screen (and opens it again if the host restarts).</summary>
    public Task OpenAsync(string key)
    {
        lock (_wanted)
        {
            _wanted.Add(key);
        }

        return SendAsync(Kind.Open, new Target(key));
    }

    /// <summary>Ends a screen's session.</summary>
    public Task CloseAsync(string key)
    {
        lock (_wanted)
        {
            _wanted.Remove(key);
        }

        _statuses.TryRemove(key, out _);
        return SendAsync(Kind.Close, new Target(key));
    }

    /// <summary>Sets a screen's backlight.</summary>
    public Task SetBrightnessAsync(string key, int percent) => SendAsync(Kind.Brightness, new ScreenHostProtocol.Brightness(key, percent));

    /// <summary>Sends a picture to an open screen (dropped when the host is not connected).</summary>
    public async Task PresentAsync(string key, byte[] picture)
    {
        if (_pipe is not { IsConnected: true } pipe || StatusOf(key) is not { Open: true })
        {
            return;
        }

        await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await WriteAsync(pipe, Kind.Frame, FrameBody(key, picture), _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The host went away; it is started again.
        }
        finally
        {
            _write.Release();
        }
    }

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
            }
        }

        _stop.Dispose();
        _write.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            // At most 5 starts in 10 minutes: a host that keeps failing is left stopped, with its problem shown.
            var now = DateTime.UtcNow;
            while (_starts.Count > 0 && now - _starts.Peek() > TimeSpan.FromMinutes(10))
            {
                _starts.Dequeue();
            }

            if (_starts.Count >= 5)
            {
                Problem ??= "The screen host keeps stopping; it is started again in a few minutes.";
                await Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                continue;
            }

            _starts.Enqueue(now);
            try
            {
                await SessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or UnauthorizedAccessException)
            {
                Problem = "The screen host stopped: " + ex.Message;
                LogHostStopped(_logger, ex);
            }
            finally
            {
                _pipe?.Dispose();
                _pipe = null;
                Devices = [];
                _statuses.Clear();
                Changed?.Invoke();
            }

            await Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    /// <summary>One host's life: start it, check its token, give it the setup, read what it reports.</summary>
    private async Task SessionAsync()
    {
        var name = "PcDiag.Screens." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, NamedPipeServer.CreateSecurity());
        _pipe = pipe;
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The recorder's program path is unknown.");
        if (!_launcher.TryStart(exe, $"{ScreenHostMode.Verb} --pipe {name} --token {token}", Path.GetDirectoryName(exe), hidden: true))
        {
            throw new IOException("Windows' shell did not start the screen host (Explorer may not be running yet).");
        }

        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            wait.CancelAfter(ConnectTimeout);
            try
            {
                await pipe.WaitForConnectionAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
            {
                throw new TimeoutException("The screen host did not connect.");
            }
        }

        if (await ReadAsync(pipe, _stop.Token).ConfigureAwait(false) is not ({ } kind, { } body) || kind != Kind.Hello
            || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(Parse<Hello>(body).Token), System.Text.Encoding.UTF8.GetBytes(token)))
        {
            throw new InvalidDataException("Something other than the screen host connected.");
        }

        Problem = null;
        await SendAsync(Kind.Setup, _setup()).ConfigureAwait(false);
        string[] reopen;
        lock (_wanted)
        {
            reopen = [.. _wanted];
        }

        foreach (var key in reopen)
        {
            await SendAsync(Kind.Open, new Target(key)).ConfigureAwait(false);
        }

        while (await ReadAsync(pipe, _stop.Token).ConfigureAwait(false) is ({ } next, { } message))
        {
            if (next == Kind.Readings)
            {
                // Sensor values (every second or so): kept for the sensors, nothing else changes.
                var loaded = Packages.Where(p => p.Loaded && p.Capabilities?.Contains("sensors") == true).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                ExternalReadings.Put(Parse<Readings>(message).List, loaded, DateTimeOffset.UtcNow);
                continue;
            }

            switch (next)
            {
                case Kind.Packages:
                    Packages = Parse<Packages>(message).List;
                    break;
                case Kind.Devices:
                    Devices = Parse<ScreenHostProtocol.Devices>(message).List;
                    break;
                case Kind.Status:
                    var status = Parse<Status>(message);
                    _statuses[status.Key] = status;
                    if (!status.Open && status.Problem is { } problem)
                    {
                        LogScreenProblem(_logger, status.Key, problem);
                    }

                    break;
            }

            Changed?.Invoke();
        }
    }

    private async Task SendAsync<T>(Kind kind, T message)
    {
        if (_pipe is not { IsConnected: true } pipe)
        {
            return;
        }

        await _write.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            await WriteJsonAsync(pipe, kind, message, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The host went away; it is started again.
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task Delay(TimeSpan wait)
    {
        try
        {
            await Task.Delay(wait, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The screen host stopped")]
    private static partial void LogHostStopped(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "USB screen {Key}: {Problem}")]
    private static partial void LogScreenProblem(ILogger logger, string key, string problem);
}
