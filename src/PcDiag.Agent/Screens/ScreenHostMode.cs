using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using PcDiag.Platform.Windows.Usb;
using PcDiag.Screens;
using static PcDiag.Screens.ScreenHostProtocol;

namespace PcDiag.Agent.Screens;

/// <summary>
/// "PcDiag.Agent.exe screen-host --pipe NAME --token TOKEN": the screen host, started by the recorder without
/// administrator rights. It is the only process that loads screen drivers (the built-in ones and the approved
/// packages): it finds the USB screens they drive, opens the ones the recorder asks for and shows the pictures it is
/// sent (the newest only, if pictures come faster than a screen takes them). It ends when the recorder goes.
/// </summary>
internal static class ScreenHostMode
{
    /// <summary>The command-line verb.</summary>
    public const string Verb = "screen-host";

    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(2);

    /// <summary>Whether the arguments ask for the screen host.</summary>
    public static bool IsCommand(string[] args) => args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the host until the recorder disconnects (exit code 0), or 64 for bad arguments, 1 when it cannot connect.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        if (Arg("--pipe") is not { } pipeName || Arg("--token") is not { } token)
        {
            return 64;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return 1;
        }

        // Anything a worker leaves unobserved is written down (a host that stops says why).
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Host.Log("unobserved: " + e.Exception);
            e.SetObserved();
        };

        using var host = new Host(pipe);
        try
        {
            await host.RunAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Host.Log("stopped: " + ex);
            return 2;
        }

        return 0;
    }

    private sealed class Host(NamedPipeClientStream pipe) : IDisposable
    {
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly List<(ScreenDriverPackage Package, IScreenDriver? Driver)> _drivers = [];
        private readonly ConcurrentDictionary<string, Screen> _open = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _opening = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new();
        private readonly Lock _usb = new();
        private readonly List<SensorDriverState> _sensors = [];
        private readonly PcDiag.Drivers.Io.SensorIo _io = new();
        private List<DeviceInfo> _devices = [];

        public async Task RunAsync(string token)
        {
            Log($"started (pid {Environment.ProcessId})");
            await SendAsync(Kind.Hello, new Hello(token, Environment.ProcessId, typeof(Host).Assembly.GetName().Version?.ToString() ?? "0")).ConfigureAwait(false);
            if (await ReadAsync(pipe, _stop.Token).ConfigureAwait(false) is not ({ } kind, { } body) || kind != Kind.Setup)
            {
                return;
            }

            LoadDrivers(Parse<Setup>(body));
            Log($"drivers: {string.Join(", ", _drivers.Select(d => d.Package.Id + (d.Driver is null ? " (not loaded)" : string.Empty)))}");
            _ = ScanLoopAsync();
            if (_sensors.Count > 0)
            {
                _ = SensorLoopAsync();
            }

            try
            {
                while (await ReadAsync(pipe, _stop.Token).ConfigureAwait(false) is ({ } next, { } message))
                {
                    try
                    {
                        Handle(next, message);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // One message the host could not act on does not end it (and the screens it shows).
                        Log($"a {next} message was not handled: {ex}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
            {
                // The recorder went away.
            }
            finally
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            foreach (var screen in _open.Values)
            {
                try
                {
                    screen.Dispose();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log($"{screen.Key}: did not close: {ex.Message}");
                }
            }

            foreach (var sensor in _sensors)
            {
                sensor.Dispose();
            }

            _io.Dispose();
            _stop.Dispose();
            _write.Dispose();
        }

        /// <summary>The built-in packages, and each package folder: used when approved by its hash.</summary>
        private void LoadDrivers(Setup setup)
        {
            var infos = new List<PackageInfo>();
            foreach (var package in ScreenDriverPackage.BuiltInPackages())
            {
                _drivers.Add((package, ScreenDrivers.Create(package, ClearPng, BlackJpeg)));
                infos.Add(Info(package, approved: true, loaded: true, null));
            }

            var folders = Directory.Exists(setup.DriversFolder) ? Directory.GetDirectories(setup.DriversFolder) : [];
            foreach (var folder in folders.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (ScreenDriverPackage.Read(folder, out var problem) is not { } package)
                {
                    infos.Add(new PackageInfo(Path.GetFileName(folder), Path.GetFileName(folder), "?", "?", false, null, false, false, problem, []));
                    continue;
                }

                var approved = setup.Approved.Any(a => a.Id == package.Id && string.Equals(a.Sha256, package.Sha256, StringComparison.OrdinalIgnoreCase));
                if (!approved || _drivers.Any(d => d.Package.Id == package.Id) || _sensors.Any(d => d.Package.Id == package.Id))
                {
                    infos.Add(Info(package, approved, loaded: false, approved ? "Another package has this id." : "Waiting for your approval."));
                    continue;
                }

                string? loadProblem = null;
                if (package.Capabilities.Contains("screen"))
                {
                    try
                    {
                        _drivers.Add((package, ScreenDrivers.Create(package, ClearPng, BlackJpeg)));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        loadProblem = "Its screen driver could not be loaded: " + ex.Message;
                    }
                }

                if (package.Sensors is not null)
                {
                    try
                    {
                        _sensors.Add(new SensorDriverState(package, SensorDrivers.Create(package, _io), Log));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        loadProblem = "Its sensor driver could not be loaded: " + ex.Message;
                    }
                }

                infos.Add(Info(package, approved, loaded: loadProblem is null, loadProblem));
            }

            _ = SendAsync(Kind.Packages, new Packages(infos));
        }

        private static PackageInfo Info(ScreenDriverPackage p, bool approved, bool loaded, string? problem) =>
            new(p.Id, p.Name, p.Version, p.Publisher, p.BuiltIn, p.BuiltIn ? null : p.Sha256, approved, loaded, problem, [.. p.Models.Select(m => m.Name)], p.Capabilities,
                p.Sensors?.Sensors?.Select(s => s.Name).ToList() ?? (p.Sensors is null ? null : ["(its own)"]));

        /// <summary>
        /// Reads each sensor driver at its own rate on a worker, with 5 s for a read (one still reading is not started
        /// again), and sends the values at most once a second. A driver that fails 5 times in a row is set aside until
        /// the host starts again (its problem shows with its package).
        /// </summary>
        private async Task SensorLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var changed = false;
                foreach (var sensor in _sensors)
                {
                    changed |= sensor.Poll(now, _stop.Token);
                }

                if (changed)
                {
                    await SendAsync(Kind.Readings, new Readings([.. _sensors.Select(s => s.Latest)])).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>Finds the screens every few seconds and tells the recorder when they change.</summary>
        private async Task ScanLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var found = new List<DeviceInfo>();
                foreach (var (package, driver) in _drivers)
                {
                    foreach (var model in driver?.Models ?? [])
                    {
                        foreach (var device in WinUsbDevice.Find(model.VendorId, model.ProductId))
                        {
                            var key = ScreenDevice.KeyOf(model.VendorId, model.ProductId, device.Serial);
                            found.Add(new DeviceInfo(key, package.Id, model, device.Serial, Busy: !_open.ContainsKey(key) && IsBusy(device.Path)));
                        }
                    }
                }

                if (!found.SequenceEqual(_devices))
                {
                    _devices = found;
                    Log("screens: " + (found.Count == 0 ? "none" : string.Join(", ", found.Select(d => $"{d.Model.Name} {d.Key}{(d.Busy ? " (busy)" : string.Empty)}"))));
                    await SendAsync(Kind.Devices, new ScreenHostProtocol.Devices(found)).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(ScanEvery, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Whether another program holds a screen (it cannot be opened: WinUSB is one program at a time). Never while
        /// the host itself opens one (the probe would hold it for that moment).
        /// </summary>
        private bool IsBusy(string path)
        {
            lock (_usb)
            {
                using var device = WinUsbDevice.Open(path, 0x01, 0x81, out var error);
                return device is null && error == 5;
            }
        }

        private void Handle(Kind kind, byte[] body)
        {
            switch (kind)
            {
                case Kind.Open:
                    Open(Parse<Target>(body).Key);
                    break;
                case Kind.Close:
                    if (_open.TryRemove(Parse<Target>(body).Key, out var closed))
                    {
                        Log($"{closed.Key}: closed after {closed.Pictures} pictures ({closed.Refused} not taken)");
                        closed.Dispose();
                        _ = SendAsync(Kind.Status, new Status(closed.Key, false, null, null));
                    }

                    break;
                case Kind.Frame:
                    var (key, picture) = ReadFrame(body);
                    if (_open.TryGetValue(key, out var screen))
                    {
                        screen.Show(picture.ToArray());
                    }

                    break;
                case Kind.Brightness:
                    var brightness = Parse<ScreenHostProtocol.Brightness>(body);
                    if (_open.TryGetValue(brightness.Key, out var lit))
                    {
                        lit.SetBrightness(brightness.Percent);
                    }

                    break;
            }
        }

        /// <summary>
        /// Starts a session with a screen on a worker (waking a panel takes a moment). A screen already open is said to
        /// be; a request while it is being opened is passed over (its answer comes when it is open).
        /// </summary>
        private void Open(string key)
        {
            if (_open.TryGetValue(key, out var already))
            {
                _ = SendAsync(Kind.Status, already.Status());
                return;
            }

            if (_devices.FirstOrDefault(d => d.Key == key) is not { } info || _drivers.FirstOrDefault(d => d.Package.Id == info.DriverId).Driver is not { } driver)
            {
                _ = SendAsync(Kind.Status, new Status(key, false, null, "The screen is not connected."));
                return;
            }

            if (!_opening.TryAdd(key, 0))
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await OpenAsync(key, info, driver).ConfigureAwait(false);
                }
                finally
                {
                    _opening.TryRemove(key, out _);
                }
            });
        }

        private async Task OpenAsync(string key, DeviceInfo info, IScreenDriver driver)
        {
            {
                var path = WinUsbDevice.Find(info.Model.VendorId, info.Model.ProductId).FirstOrDefault(d => d.Serial == info.Serial)?.Path;
                var error = 0;
                WinUsbDevice? device = null;
                if (path is not null)
                {
                    lock (_usb)
                    {
                        device = WinUsbDevice.Open(path, 0x01, 0x81, out error);
                    }
                }
                if (device is null)
                {
                    Log($"{key}: not opened: {(path is null ? "not connected" : WinUsbDevice.Describe(error))}");
                    await SendAsync(Kind.Status, new Status(key, false, null, path is null ? "The screen is not connected." : WinUsbDevice.Describe(error))).ConfigureAwait(false);
                    return;
                }

                try
                {
                    var session = driver.Open(info.Model, new Transport(device));
                    var screen = new Screen(key, session, s => SendAsync(Kind.Status, s), Stopped);
                    _open[key] = screen;
                    Log($"{key}: open, firmware {session.Firmware ?? "(none said)"}");
                    await SendAsync(Kind.Status, screen.Status()).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    device.Dispose();
                    Log($"{key}: did not start: {ex}");
                    await SendAsync(Kind.Status, new Status(key, false, null, "The screen did not start: " + ex.Message)).ConfigureAwait(false);
                }
            }
        }

        /// <summary>A screen whose pictures stopped going (its session failed): closed, and the recorder told why.</summary>
        private void Stopped(Screen screen, Exception exception)
        {
            Log($"{screen.Key}: stopped after {screen.Pictures} pictures: {exception}");
            if (_open.TryRemove(new KeyValuePair<string, Screen>(screen.Key, screen)))
            {
                _ = SendAsync(Kind.Status, new Status(screen.Key, false, null, "The screen stopped: " + exception.Message));
            }
        }

        private async Task SendAsync<T>(Kind kind, T message)
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                await WriteJsonAsync(pipe, kind, message, _stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                _write.Release();
            }
        }

        /// <summary>A line in the host's own log (screen-host.log, kept small), for finding why a screen shows nothing.</summary>
        internal static void Log(string line)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcDiagnostic", "logs");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "screen-host.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                {
                    File.Move(file, file + ".old", overwrite: true);
                }

                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log line is not worth failing for.
            }
        }

        private static byte[] ClearPng(int width, int height)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        private static byte[] BlackJpeg(int width, int height)
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Black);
            }

            return ScreenFrames.Encode(bitmap, FrameFormat.Jpeg, 0);
        }
    }

    /// <summary>
    /// A screen in use: pictures go to it one at a time, the newest first (older waiting ones are dropped). If its
    /// session fails, the screen is said to have stopped (and closes); closing it more than once, or after, is harmless.
    /// </summary>
    internal sealed class Screen : IDisposable
    {
        private const int NoBrightness = -1;
        private readonly IScreenSession _session;
        private readonly Func<Status, Task> _report;
        private readonly Action<Screen, Exception>? _stopped;
        private readonly SemaphoreSlim _wake = new(0, 1);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;
        private byte[]? _next;
        private int _brightness = NoBrightness;
        private int _closed;
        private bool _lastShown = true;
        private int _pictures;
        private int _refused;
        private DateTime _reported = DateTime.MinValue;
        private (int Pictures, int Refused) _reportedCounts;

        public Screen(string key, IScreenSession session, Func<Status, Task> report, Action<Screen, Exception>? stopped = null)
        {
            Key = key;
            _session = session;
            _report = report;
            _stopped = stopped;
            _pump = Task.Run(PumpAsync);
        }

        public string Key { get; }

        public int Pictures => _pictures;

        public int Refused => _refused;

        /// <summary>The session's state now.</summary>
        public Status Status() => new(Key, true, _session.Firmware, _lastShown ? null : "The screen did not take the last picture.", _lastShown, _pictures, _refused);

        public void Show(byte[] picture)
        {
            Interlocked.Exchange(ref _next, picture);
            Wake();
        }

        /// <summary>For tests: completes when the pump has ended.</summary>
        internal Task Ended => _pump;

        public void SetBrightness(int percent)
        {
            Volatile.Write(ref _brightness, Math.Clamp(percent, 0, 100));
            Wake();
        }

        /// <summary>Its pictures stop going and its session ends (once, from whichever side first).</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 1)
            {
                return;
            }

            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Its pump had already ended.
            }
        }

        private void Wake()
        {
            if (Volatile.Read(ref _closed) == 1 || _wake.CurrentCount > 0)
            {
                return;
            }

            try
            {
                _wake.Release();
            }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
            {
                // Already woken, or closed meanwhile.
            }
        }

        private DateTime _rateFrom = DateTime.UtcNow;
        private int _sent;
        private TimeSpan _sendTime;

        private async Task PumpAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _wake.WaitAsync(_stop.Token).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _brightness, NoBrightness) is var brightness and not NoBrightness)
                    {
                        _session.SetBrightness(brightness);
                    }

                    if (Interlocked.Exchange(ref _next, null) is { } picture)
                    {
                        var sending = System.Diagnostics.Stopwatch.GetTimestamp();
                        var shown = _session.Present(picture);
                        _sendTime += System.Diagnostics.Stopwatch.GetElapsedTime(sending);
                        _sent++;
                        if (DateTime.UtcNow - _rateFrom >= TimeSpan.FromSeconds(10))
                        {
                            // How fast pictures go, every 10 s (a screen's own limit shows here).
                            Host.Log($"{Key}: {_sent / (DateTime.UtcNow - _rateFrom).TotalSeconds:F1} pictures a second, {_sendTime.TotalMilliseconds / Math.Max(1, _sent):F0} ms each to send");
                            (_rateFrom, _sent, _sendTime) = (DateTime.UtcNow, 0, TimeSpan.Zero);
                        }

                        if (shown)
                        {
                            _pictures++;
                        }
                        else
                        {
                            _refused++;
                        }

                        if (_pictures + _refused <= 3 || !shown)
                        {
                            Host.Log($"{Key}: picture {_pictures + _refused} ({picture.Length} bytes) {(shown ? "shown" : "NOT taken")}");
                        }

                        // A change of how pictures go is said at once; the counts every few seconds while they change.
                        if (shown != _lastShown || (DateTime.UtcNow - _reported > TimeSpan.FromSeconds(5) && (_pictures, _refused) != _reportedCounts))
                        {
                            _lastShown = shown;
                            _reported = DateTime.UtcNow;
                            _reportedCounts = (_pictures, _refused);
                            await _report(Status()).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Interlocked.Exchange(ref _closed, 1);
                _stopped?.Invoke(this, ex);
            }
            finally
            {
                Interlocked.Exchange(ref _closed, 1);
                _session.Dispose();
                _wake.Dispose();
                _stop.Dispose();
            }
        }
    }

    /// <summary>WinUSB as a screen's transport.</summary>
    private sealed class Transport(WinUsbDevice device) : IScreenTransport
    {
        public bool Write(ReadOnlySpan<byte> data, TimeSpan timeout) => device.Write(data, timeout);

        public byte[]? Read(int max, TimeSpan timeout) => device.Read(max, timeout);

        public void Dispose() => device.Dispose();
    }
}

/// <summary>A sensor driver in the host: when it is next read, its read under way, its last values and failures.</summary>
internal sealed class SensorDriverState(ScreenDriverPackage package, ISensorDriver driver, Action<string> log) : IDisposable
{
    private static readonly TimeSpan ReadLimit = TimeSpan.FromSeconds(5);
    private const int FailuresToSetAside = 5;
    private Task<IReadOnlyList<SensorValue>>? _reading;
    private DateTime _readSince;
    private DateTime _next;
    private int _failures;

    public ScreenDriverPackage Package => package;

    public ScreenHostProtocol.DriverReadings Latest { get; private set; } = new(package.Id, [], "Not read yet.");

    /// <summary>Whether it was set aside (failed too often).</summary>
    public bool SetAside => _failures >= FailuresToSetAside;

    /// <summary>Takes a finished read's values, starts the next when due; true when its values or problem changed.</summary>
    public bool Poll(DateTime now, CancellationToken cancellationToken)
    {
        if (SetAside)
        {
            return false;
        }

        var changed = false;
        if (_reading is { } reading)
        {
            if (reading.IsCompleted)
            {
                _reading = null;
                if (reading.IsCompletedSuccessfully)
                {
                    _failures = 0;
                    Latest = new(package.Id, [.. reading.Result.Take(SensorDrivers.MaxValues)], null);
                }
                else
                {
                    Failed(reading.Exception?.GetBaseException().Message ?? "it stopped.");
                }

                changed = true;
            }
            else if (now - _readSince > ReadLimit)
            {
                // Still reading: left to finish on its own; counted as a failure, and not started again until it does.
                _readSince = now;
                Failed("it did not answer within 5 s.");
                changed = true;
            }
        }

        if (_reading is null && !SetAside && now >= _next)
        {
            _next = now + TimeSpan.FromSeconds(Math.Clamp(package.Sensors?.Every ?? 2, 1, 60));
            _readSince = now;
            _reading = Task.Run(() => driver.Read(cancellationToken), cancellationToken);
        }

        return changed;
    }

    public void Dispose() => driver.Dispose();

    private void Failed(string why)
    {
        _failures++;
        var problem = SetAside ? $"Set aside after {FailuresToSetAside} failures in a row (last: {why}) It is tried again when the recorder starts again." : "Not read: " + why;
        // Its values are not read now: shown as unavailable rather than as old values.
        Latest = new(package.Id, [.. Latest.Values.Select(v => v with { Value = null })], problem);
        log($"{package.Id}: {problem}");
    }
}
