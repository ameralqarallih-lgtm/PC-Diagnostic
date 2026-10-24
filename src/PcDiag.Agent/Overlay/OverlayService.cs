using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Forms;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Platform.Windows.Shell;
using PcDiag.Core.Recording;
using PcDiag.Engine.Live;
using PcDiag.Engine.Overlay;
using PcDiag.Engine.Recording;
using PcDiag.Platform.Windows.Display;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Runs the on-screen display: its own UI thread (per-monitor DPI aware, so panels are placed in physical pixels),
/// the layout from overlay.json (read again when the Web host saves it, and saved here when the panels are arranged on
/// the screens), the monitors (checked every few seconds), and sampling while panels are shown (in memory only, like
/// the dashboard's live view: nothing is recorded).
/// </summary>
internal sealed partial class OverlayService(
    DataPaths paths,
    IEnumerable<LiveFeed> live,
    IRecordingControl recorder,
    IDisplayCollector displays,
    TimeProvider time,
    ILogger<OverlayService> logger,
    IFrameMonitor? frames = null,
    ILatencyProbe? latency = null,
    Core.Devices.IHardwareWatch? hardware = null,
    Engine.Incidents.IncidentManager? incidents = null) : IHostedService, IOverlayControl, IDisposable
{
    /// <summary>An incident saved: the characters react (jump, "Incident saved: …").</summary>
    private void OnIncident(object? sender, Core.Incidents.IncidentCapturedEventArgs e) => CharacterEvents.Raise("incident", e.Outcome.Incident.Title);

    // What the characters were last told: recording or not, and the game in front.
    private bool? _wasRecording;
    private string? _lastGame;

    /// <summary>Recording turned on or off, a game come to the front: the characters react once.</summary>
    private void Happened(bool recording, GameState game)
    {
        if (_wasRecording is { } was && was != recording)
        {
            CharacterEvents.Raise(recording ? "recording-on" : "recording-off");
        }

        _wasRecording = recording;
        // A game: an app in front presenting frames steadily (a desktop app redrawing now and then is not one).
        var playing = game.Frames is { Foreground: true, Fps: >= 20, ProcessName: { Length: > 0 } name } ? name : null;
        if (playing is not null && !string.Equals(playing, _lastGame, StringComparison.OrdinalIgnoreCase))
        {
            CharacterEvents.Raise("game", Path.GetFileNameWithoutExtension(playing));
        }

        if (playing is not null || game.Frames is null)
        {
            _lastGame = playing;
        }
    }

    /// <summary>How often the monitors are checked and sampling kept going.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2.5);

    private readonly LiveFeed? _live = live.FirstOrDefault();

    // The values' names as last seen: a panel shown before its values arrive reads their names while they load.
    private readonly OverlayKnownSources _known = new(Path.Combine(paths.Root, "overlay-sources.json"), time);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Completed when Windows says the displays changed (turned to portrait, resized): looked at again at once.</summary>
    private volatile TaskCompletionSource _displaysChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Thread? _thread;
    private OverlayHost? _host;
    private SynchronizationContext? _ui;
    private ApplicationContext? _app;
    private FileSystemWatcher? _watcher;
    private Task? _loop;
    private System.Threading.Timer? _debounce;
    private readonly Lock _writeLock = new();

    // Background pictures by id, read once from the data folder (dropped when no panel uses them any more).
    private readonly ConcurrentDictionary<string, Image> _images = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AnimatedPicture> _animations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CharacterPlayer> _characters = new(StringComparer.Ordinal);
    private volatile OverlayConfig _config = OverlayConfig.Empty;
    private volatile string? _problem;

    // The layout as this service last saved it (arranged on the screens): the file change that follows is its own.
    private volatile string? _written;

    // The USB screens (screens that are not Windows monitors), and the monitors last read.
    private Screens.DeviceScreens? _screens;
    private IReadOnlyList<OverlayDisplay> _monitors = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (incidents is not null)
        {
            incidents.Captured += OnIncident;
        }

        // At start the layout is read once more if it is being replaced (else the panels start empty).
        if (!Load())
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            Load();
        }

        if (!Environment.UserInteractive)
        {
            _problem = "The recorder runs without a desktop, so it cannot draw on the screens.";
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "pcdiag-overlay" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        Watch();
        StartScreens();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (incidents is not null)
        {
            incidents.Captured -= OnIncident;
        }

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_screens is not null)
        {
            // Screens closed and their holders (L-Connect…) started again before the recorder goes.
            await _screens.DisposeAsync().ConfigureAwait(false);
        }

        _watcher?.Dispose();
        _ui?.Post(_ => _app?.ExitThread(), null);
        _thread?.Join(TimeSpan.FromSeconds(3));
        if (_loop is not null)
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _stop.Dispose();
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    /// <inheritdoc />
    public async Task<OverlayState> StateAsync(CancellationToken cancellationToken)
    {
        var monitors = await DisplaysAsync(cancellationToken).ConfigureAwait(false);
        return new OverlayState(_thread?.IsAlive == true, _host?.Visible ?? false, _problem ?? _host?.Problem, AllDisplays(monitors), Sources(editing: true), _host?.Arranging ?? false);
    }

    /// <inheritdoc />
    public IReadOnlyList<ScreenDriverInfo> ScreenDrivers() => _screens?.Drivers() ?? [];

    /// <inheritdoc />
    public (string? Id, string? Problem) ImportDriver(string upload) =>
        _screens is { } screens ? screens.Import(upload) : (null, "USB screens and driver packages are not available in this recorder.");

    /// <inheritdoc />
    public string? RemoveDriver(string id) =>
        _screens is { } screens ? screens.Remove(id) : "USB screens and driver packages are not available in this recorder.";

    /// <inheritdoc />
    public string? ApproveScreenDriver(string id, string sha256, bool approve) =>
        _screens is { } screens ? screens.Approve(id, sha256, approve) : "USB screens are not available in this recorder.";

    /// <summary>The monitors, then the USB screens (numbered after them).</summary>
    private List<OverlayDisplay> AllDisplays(IReadOnlyList<OverlayDisplay> monitors) => [.. monitors, .. _screens?.Displays(monitors.Count + 1) ?? []];

    /// <summary>
    /// Starts the USB screens: found through the screen host, drawn by the panel host at each refresh while one is
    /// used, and listed with the monitors (at once when they change).
    /// </summary>
    private void StartScreens()
    {
        var screens = new Screens.DeviceScreens(paths, new DesktopShell(), new Screens.WindowsServiceSwitch(), logger);
        _screens = screens;
        screens.Changed += () =>
        {
            var all = AllDisplays(_monitors);
            _ui?.Post(_ => _host?.SetDisplays(all), null);
        };
        screens.Apply(_config);
        _ui?.Post(
            _ =>
            {
                if (_host is { } host)
                {
                    host.DeviceOn = screens.IsOn;
                    host.DeviceFrames = screens.Present;
                }
            },
            null);
        screens.Start();
    }

    /// <inheritdoc />
    public void Reload()
    {
        // The file this service just saved (the panels were arranged on the screens) is already shown.
        if (_written is { } written && FileText(paths.OverlayFile) == written)
        {
            return;
        }

        if (!Load())
        {
            // Being replaced just now: keep what is shown and read it again shortly.
            _debounce?.Change(500, Timeout.Infinite);
            return;
        }

        var config = _config;
        _screens?.Apply(config);
        _ui?.Post(_ => _host?.Apply(config), null);
    }

    /// <inheritdoc />
    public void Identify() => _ui?.Post(_ => _host?.Identify(), null);

    /// <inheritdoc />
    public void Arrange(bool arranging)
    {
        // Done on the screens' thread before answering, so the state that follows says so.
        if (_ui is { } ui && _thread?.IsAlive == true && Thread.CurrentThread != _thread)
        {
            ui.Send(_ => _host?.Arrange(arranging), null);
        }
        else
        {
            _host?.Arrange(arranging);
        }
    }

    /// <inheritdoc />
    public bool Arranging => _host?.Arranging ?? false;

    /// <inheritdoc />
    public int ClosedPanels => _host?.ClosedCount ?? 0;

    /// <inheritdoc />
    public void ShowClosed() => _ui?.Post(_ => _host?.ShowClosed(), null);

    /// <summary>Saves a layout changed on the screens (off the UI thread).</summary>
    private void Save(OverlayConfig config)
    {
        _config = config;
        _screens?.Apply(config);
        _ = Task.Run(() =>
        {
            try
            {
                lock (_writeLock)
                {
                    _written = OverlayStyles.Serialize(config);
                    OverlayStyles.Write(paths.OverlayFile, config);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(logger, ex);
            }
        });
    }

    private static string? FileText(string path)
    {
        try
        {
            return OverlayStyles.ReadShared(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Run()
    {
        try
        {
            MonitorLayout.UsePhysicalPixelsOnThisThread();
            // A problem in a window's message (drawing a panel or a character) is logged and the overlay goes on: never
            // Windows Forms' "Unhandled exception" dialog on the user's screen.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
            Application.ThreadException += (_, e) => LogWindowFailed(logger, e.Exception);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _ui = SynchronizationContext.Current;
            OverlayRenderer.Images = Picture;
            OverlayRenderer.Animations = Animation;
            CharacterPlayer.Find = Character;
            using var host = new OverlayHost(Sources, logger);
            host.Changed += Save;
            _host = host;
            host.Apply(_config);
            _app = new ApplicationContext();
            _ready.TrySetResult();
            Application.Run(_app);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _problem = "The on-screen readings could not start: " + ex.Message;
            LogFailed(logger, ex);
            _ready.TrySetResult();
        }
    }

    /// <summary>Reads the layout; false (the layout kept) when the file could not be read just now.</summary>
    /// <summary>A character's new animations' files (animations.json; those made and here, in the animations folder).</summary>
    private IEnumerable<byte[]> ExtrasOf(string id)
    {
        foreach (var (_, file) in CharacterAnimations.FilesFor(CharacterAnimations.Read(paths.Root), id))
        {
            if (Path.Combine(paths.Root, CharacterAnimations.Folder, file) is var path && File.Exists(path))
            {
                byte[]? bytes = null;
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (IOException)
                {
                }

                if (bytes is not null)
                {
                    yield return bytes;
                }
            }
        }
    }

    // When weather.json was last written, and when it was last looked at for being too old.
    private DateTime _weatherWritten;
    private DateTimeOffset _weatherLooked;

    /// <summary>The weather for the characters, from weather.json (written by the Web host) when it changes, and let go when it is old.</summary>
    private void WatchWeather()
    {
        var file = Path.Combine(paths.Root, WeatherReport.FileName);
        var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        var now = time.GetUtcNow();
        if (written == _weatherWritten && now - _weatherLooked < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _weatherWritten = written;
        _weatherLooked = now;
        CharacterReacting.Weather = written == DateTime.MinValue ? null : WeatherReport.Read(paths.Root)?.ForCharacters(now);
    }

    // When animations.json was last written: a change (a new animation made, one removed) reads the characters again.
    private DateTime _animationsWritten;

    /// <summary>The characters read again when the new animations changed (their players let go; the next draw reads them with the new clips).</summary>
    private void WatchAnimations()
    {
        var file = Path.Combine(paths.Root, CharacterAnimations.FileName);
        var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (written == _animationsWritten)
        {
            return;
        }

        var first = _animationsWritten == default;
        _animationsWritten = written;
        if (first)
        {
            return;
        }

        var gone = new List<CharacterPlayer>();
        foreach (var id in _characters.Keys.ToList())
        {
            if (_characters.TryRemove(id, out var player))
            {
                gone.Add(player);
            }
        }

        CharacterPlayer.Generation++;
        if (_ui is { } ui)
        {
            ui.Post(_ => gone.ForEach(p => p.Dispose()), null);
        }
        else
        {
            gone.ForEach(p => p.Dispose());
        }
    }

    private bool Load()
    {
        if (!OverlayStyles.TryRead(paths.OverlayFile, out var config, out var problem))
        {
            LogBadLayout(logger, paths.OverlayFile, problem ?? "unreadable");
            return false;
        }

        _config = config;
        if (problem is not null)
        {
            LogBadLayout(logger, paths.OverlayFile, problem);
        }

        var used = _config.EveryPanel().Select(p => p.Style.Image).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var id in _images.Keys.Where(id => !used.Contains(id)).ToList())
        {
            if (_images.TryRemove(id, out var unused))
            {
                unused.Dispose();
            }
        }

        foreach (var id in _animations.Keys.Where(id => !used.Contains(id)).ToList())
        {
            if (_animations.TryRemove(id, out var unused))
            {
                unused.Dispose();
            }
        }

        // Imported characters no panel has any more: let go on the overlay's thread (it may be drawing one now).
        var playing = _config.EveryPanel().SelectMany(p => p.Characters ?? []).Select(c => c.Character).ToHashSet(StringComparer.Ordinal);
        var gone = new List<CharacterPlayer>();
        foreach (var id in _characters.Keys.Where(id => !playing.Contains(id) && !OverlayCharacters.ShippedIds.Contains(id)).ToList())
        {
            if (_characters.TryRemove(id, out var unused))
            {
                gone.Add(unused);
            }
        }

        if (gone.Count > 0)
        {
            if (_ui is { } ui)
            {
                ui.Post(_ => gone.ForEach(p => p.Dispose()), null);
            }
            else
            {
                gone.ForEach(p => p.Dispose());
            }
        }

        return true;
    }

    /// <summary>
    /// A character's frames by id: a shipped one, or an imported one's file in the picture library (null when it is
    /// not there or is not a sound one).
    /// </summary>
    private CharacterPlayer? Character(string id)
    {
        if (_characters.TryGetValue(id, out var cached))
        {
            return cached;
        }

        try
        {
            byte[] bytes;
            if (OverlayCharacters.Shipped.TryGetValue(id, out var shipped))
            {
                bytes = shipped.Bytes;
            }
            else if (OverlayCharacters.IsImportedId(id) && Path.Combine(paths.OverlayImagesDirectory, id) is var path && File.Exists(path))
            {
                bytes = File.ReadAllBytes(path);
            }
            else
            {
                return null;
            }

            if (CharacterPlayer.Read(bytes) is not { } player)
            {
                LogPictureFailed(logger, id, new InvalidDataException("Not a character this version plays."));
                return null;
            }

            // Its new animations (the generator's extra files in the picture library) played with it.
            player.Attach(ExtrasOf(id));
            if (_characters.TryAdd(id, player))
            {
                return player;
            }

            player.Dispose();
            return _characters[id];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException or InvalidOperationException)
        {
            LogPictureFailed(logger, id, ex);
            return null;
        }
    }

    /// <summary>An animated background picture by id (null when it is not there or is not a sound animation).</summary>
    private AnimatedPicture? Animation(string id)
    {
        if (_animations.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!OverlayStyles.IsImageId(id) || !OverlayRenderer.IsAnimation(id))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(paths.OverlayImagesDirectory, id);
            if (!File.Exists(path))
            {
                return null;
            }

            // Kept as its JPEG frames; each decoded when shown.
            if (AnimatedPicture.Read(File.ReadAllBytes(path)) is not { } animation)
            {
                LogPictureFailed(logger, id, new InvalidDataException("Not an animation this version plays."));
                return null;
            }

            if (_animations.TryAdd(id, animation))
            {
                return animation;
            }

            animation.Dispose();
            return _animations[id];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
        {
            LogPictureFailed(logger, id, ex);
            return null;
        }
    }

    /// <summary>A background picture by id (null when it is not there or cannot be read).</summary>
    private Image? Picture(string id)
    {
        if (_images.TryGetValue(id, out var cached))
        {
            return cached;
        }

        if (!OverlayStyles.IsImageId(id) || OverlayRenderer.IsAnimation(id))
        {
            return null;
        }

        try
        {
            var path = Path.Combine(paths.OverlayImagesDirectory, id);
            if (!File.Exists(path))
            {
                return null;
            }

            // Read into memory first: the file stays free (the Web host may replace or delete it).
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var loaded = Image.FromStream(stream);
            var picture = new Bitmap(loaded);
            return _images.GetOrAdd(id, picture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException)
        {
            LogPictureFailed(logger, id, ex);
            return null;
        }
    }

    /// <summary>Reads the layout again shortly after overlay.json changes (the Web host writes it, then renames it into place).</summary>
    private void Watch()
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(paths.Root, Path.GetFileName(paths.OverlayFile)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime };
            FileSystemEventHandler changed = (_, _) => _debounce.Change(300, Timeout.Infinite);
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            LogWatchFailed(logger, ex);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var monitors = await DisplaysAsync(cancellationToken).ConfigureAwait(false);
                _monitors = monitors;
                var all = AllDisplays(monitors);
                _ui?.Post(_ => _host?.SetDisplays(all), null);

                WatchAnimations();
                WatchWeather();
                // Values come from sampling: keep it going (in memory only) while panels are on the screens.
                if (_host?.Showing == true)
                {
                    await recorder.KeepLiveViewAsync(cancellationToken).ConfigureAwait(false);
                }

                // The next look in a moment, or at once when a display is turned or changed.
                var changed = _displaysChanged;
                await Task.WhenAny(Task.Delay(CheckInterval, time, cancellationToken), changed.Task).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (changed.Task.IsCompleted)
                {
                    _displaysChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogLoopFailed(logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(10), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _displaysChanged.TrySetResult();

    private async Task<List<OverlayDisplay>> DisplaysAsync(CancellationToken cancellationToken)
    {
        var areas = MonitorLayout.Read();
        Core.Snapshots.DisplaySnapshot? snapshot = null;
        try
        {
            snapshot = await displays.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDisplaysFailed(logger, ex);
        }

        return OverlayDisplays.Build(areas, snapshot);
    }

    private List<OverlaySource> Sources() => Sources(editing: false);

    /// <summary>
    /// The values now. Frame rates are measured while a shown panel has them or the editor is open; the round trips
    /// are measured all the time.
    /// </summary>
    private List<OverlaySource> Sources(bool editing)
    {
        if (_live is null)
        {
            return [];
        }

        var config = _config;
        var shown = config.Enabled ? config.EveryPanel().Where(p => !p.Hidden).SelectMany(p => p.Readings).Select(r => r.Source).ToHashSet(StringComparer.Ordinal) : [];
        if (editing || shown.Any(id => id.StartsWith("game.", StringComparison.Ordinal)))
        {
            frames?.Want();
        }

        var foreground = Platform.Windows.Shell.ForegroundWindow.ProcessId();
        var game = new GameState(frames?.Current(foreground), latency?.Current(), foreground);
        var check = hardware?.Status;
        var recording = recorder.State == RecorderState.Recording;
        Happened(recording, game);
        var sources = OverlaySources.Build(_live.Latest(), time.GetLocalNow(), recording, game, check?.Expected is null ? null : check.Missing);
        return _known.Complete(sources, shown);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The on-screen readings went on after a problem in a window")]
    private static partial void LogWindowFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The on-screen readings could not start")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The on-screen readings layout {Path} was not used: {Problem}")]
    private static partial void LogBadLayout(ILogger logger, string path, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Changes to the on-screen readings layout are not watched")]
    private static partial void LogWatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The on-screen readings could not check the monitors")]
    private static partial void LogLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The display configuration could not be read for the on-screen readings")]
    private static partial void LogDisplaysFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The on-screen background picture {Id} could not be read")]
    private static partial void LogPictureFailed(ILogger logger, string id, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The on-screen readings arranged on the screens could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
