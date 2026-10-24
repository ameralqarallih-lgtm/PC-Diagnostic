using PcDiag.Core.Diagnostics;
using System.Drawing;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Platform.Windows.Shell;
using PcDiag.Screens;
using static PcDiag.Screens.ScreenHostProtocol;

namespace PcDiag.Agent.Screens;

/// <summary>
/// The USB screens: finds them through the screen host, lists them with the monitors (their panels are kept like a
/// monitor's), and when one is used (its <see cref="OverlayDevice.Use"/>) pauses the program holding it, opens it and
/// shows the panels as pictures; when it is no longer used, closes it and starts the program again. Approvals of
/// driver packages are kept in the drivers folder, which only administrators can write.
/// </summary>
internal sealed partial class DeviceScreens : IAsyncDisposable
{
    private readonly ScreenLink _link;
    private readonly ScreenHolders _holders;
    private readonly ILogger _logger;
    private readonly string _driversFolder;
    private readonly Dictionary<string, int> _lastFrame = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _making = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _brightness = new(StringComparer.Ordinal);
    /// <summary>The packages that ship with the app (read once).</summary>
    private static readonly IReadOnlyList<ScreenDriverPackage> BuiltIns = ScreenDriverPackage.BuiltInPackages();

    private readonly SemaphoreSlim _reconciling = new(1, 1);
    private readonly string _uploads;
    private readonly Dictionary<string, (DateTime At, int Attempts)> _asked = new(StringComparer.Ordinal);
    /// <summary>How long screens to be used can be missing before their programs are started again.</summary>
    internal static readonly TimeSpan MissingGrace = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, bool> _busy = new(StringComparer.Ordinal);
    private DateTime? _missingSince;
    private OverlayConfig _config = OverlayConfig.Empty;
    private string? _problem;
    private int _again;

    public DeviceScreens(DataPaths paths, IUnelevatedLauncher launcher, IServiceSwitch services, ILogger logger, string? driversFolder = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _driversFolder = driversFolder ?? DataPaths.DriversDirectory;
        _uploads = paths.DriverUploadsDirectory;
        _holders = new ScreenHolders(paths.ScreenHoldersFile, services, logger);
        _link = new ScreenLink(launcher, () => new Setup(_driversFolder, Approvals()), logger);
        _link.Changed += () =>
        {
            Schedule();
            Changed?.Invoke();
        };
    }

    /// <summary>The screens or their states changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Starts: the drivers folder is made (administrators write, everyone reads), the screen host is started. Programs
    /// a previous run paused and did not start again stay paused while a screen is still to be used (no flicker of
    /// L-Connect at each start); otherwise they are started again (see <see cref="ReleaseHolders"/>).
    /// </summary>
    public void Start()
    {
        // The host is this program in another mode: only the recorder itself can start it (not a test host).
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "PcDiag.Agent.exe", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        EnsureDriversFolder();
        _link.Start();
    }

    /// <summary>The settings changed: screens are opened, closed and lit as they say.</summary>
    public void Apply(OverlayConfig config)
    {
        var previous = _config;
        _config = config ?? OverlayConfig.Empty;
        lock (_asked)
        {
            // A screen switched on again (or its settings changed) is tried afresh.
            foreach (var key in _asked.Keys.Where(k => previous.DeviceOf(k) != _config.DeviceOf(k)).ToList())
            {
                _asked.Remove(key);
            }
        }

        Schedule();
    }

    /// <summary>The USB screens connected now, as displays numbered after the monitors.</summary>
    public IReadOnlyList<OverlayDisplay> Displays(int firstNumber)
    {
        var displays = new List<OverlayDisplay>();
        var number = firstNumber;
        foreach (var device in _link.Devices)
        {
            var settings = _config.DeviceOf(device.Key);
            var rotation = ScreenFrames.RotationOf(device.Model, settings.Rotation);
            var size = ScreenFrames.LayoutSize(device.Model, rotation);
            var status = _link.StatusOf(device.Key);
            var holders = HoldersOf(device.DriverId);
            var holder = holders.Count > 0 ? holders[0].Name : null;
            var (state, detail) = status switch
            {
                { Open: true, Shown: false } => ("problem", status.Problem),
                { Open: true } => ("on", Progress(status)),
                { Problem: { } problem } when settings.Use => ("problem", problem),
                _ when settings.Use && _problem is { } problem => ("problem", problem),
                _ when device.Busy => ("busy", holder is null ? "Another program is using it." : $"{holder} is using it."),
                _ => ("available", null),
            };
            displays.Add(new OverlayDisplay(device.Key, device.Model.Name, number++, 0, 0, size.Width, size.Height, settings.Zoom / 100.0, false, device.Model.MaxFps, "device", state, detail, device.Model.Name));
        }

        return displays;
    }

    /// <summary>Whether a screen's panels are being shown on it.</summary>
    public bool IsOn(string key) => _link.StatusOf(key) is { Open: true };

    /// <summary>A screen's panels as one picture (its layout size): turned, encoded and sent, when it changed.</summary>
    /// <param name="moving">A motion picture (it always differs: not compared with the last one).</param>
    public void Present(string key, Bitmap layout, bool moving = false)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!IsOn(key) || _link.Devices.FirstOrDefault(d => d.Key == key) is not { } device)
        {
            return;
        }

        // Turned and encoded off the panels' thread, one picture at a time per screen: one that comes while the last is
        // still being made (moving backgrounds send them often) is passed over.
        if (!_making.TryAdd(key, 0))
        {
            return;
        }

        var rotation = ScreenFrames.RotationOf(device.Model, _config.DeviceOf(key).Rotation);
        var copy = (Bitmap)layout.Clone();
        var quality = _quality.GetOrAdd(key, _ => new ScreenFrames.JpegQuality());
        _ = Task.Run(() =>
        {
            try
            {
                using (copy)
                using (CostMeter.Measure("usb.encode"))
                {
                    // A picture as the last one sent is not encoded again (the panels refresh every second, often unchanged).
                    var pixels = moving ? 0UL : PixelHash(copy);
                    lock (_lastFrame)
                    {
                        if (!moving && _lastLayout.TryGetValue(key, out var seen) && seen == pixels)
                        {
                            return;
                        }

                        _lastLayout[key] = pixels;
                    }

                    if (ScreenFrames.MakeFrom(copy, device.Model, rotation, quality) is not { } frame)
                    {
                        return;
                    }

                    // The same picture is not sent again.
                    var hash = HashOf(frame);
                    lock (_lastFrame)
                    {
                        if (_lastFrame.TryGetValue(key, out var last) && last == hash)
                        {
                            return;
                        }

                        _lastFrame[key] = hash;
                    }

                    _ = _link.PresentAsync(key, frame);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
            {
                LogFrameFailed(_logger, key, ex);
            }
            finally
            {
                _making.TryRemove(key, out _);
            }
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScreenFrames.JpegQuality> _quality = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _lastLayout = new(StringComparer.Ordinal);

    /// <summary>A picture's pixels as one number (64-bit FNV-1a over its rows, 8 bytes at a time).</summary>
    private static ulong PixelHash(Bitmap bitmap)
    {
        // Its own 32-bit format (no conversion to read it).
        var format = bitmap.PixelFormat is System.Drawing.Imaging.PixelFormat.Format32bppPArgb or System.Drawing.Imaging.PixelFormat.Format32bppRgb ? bitmap.PixelFormat : System.Drawing.Imaging.PixelFormat.Format32bppArgb;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, format);
        try
        {
            var hash = 14695981039346656037UL;
            var rowBytes = bitmap.Width * 4;
            for (var y = 0; y < bitmap.Height; y++)
            {
                unsafe
                {
                    var row = new ReadOnlySpan<ulong>((byte*)data.Scan0 + ((long)y * data.Stride), rowBytes / 8);
                    foreach (var v in row)
                    {
                        hash = (hash ^ v) * 1099511628211UL;
                    }
                }
            }

            return hash ^ (ulong)bitmap.Width ^ ((ulong)bitmap.Height << 32);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>The driver packages found (empty until the screen host has started), with how their sensors read now.</summary>
    public IReadOnlyList<ScreenDriverInfo> Drivers()
    {
        var reading = ExternalReadings.Problems();
        return [.. _link.Packages.Select(p => new ScreenDriverInfo(
            p.Id, p.Name, p.Version, p.Publisher, p.BuiltIn, p.Sha256, p.Approved, p.Loaded,
            p.Problem ?? (reading.TryGetValue(p.Id, out var sensorProblem) ? sensorProblem : null),
            p.Models, p.Capabilities ?? ["screen"], p.Sensors ?? []))];
    }

    /// <summary>
    /// Imports a driver package uploaded in the dashboard (a zip in the uploads folder): checked from the recorder's own
    /// copy, put in the drivers folder (replacing one with its id), then waiting for approval. Its id, or the problem.
    /// </summary>
    public (string? Id, string? Problem) Import(string upload)
    {
        EnsureDriversFolder();
        var builtIn = BuiltIns.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var result = DriverImport.Import(upload, _uploads, DataPaths.DriverStagingDirectory, _driversFolder, builtIn);
        if (result.Id is { } id)
        {
            LogImported(_logger, id);
            var before = _link.Packages.FirstOrDefault(p => p.Id == id)?.Sha256;
            _link.Restart();
            WaitForHost(list => list.Any(p => p.Id == id && p.Sha256 != before));
        }

        return result;
    }

    /// <summary>Removes a driver package (not a built-in one) and its approval; the screen host starts again without it.</summary>
    public string? Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return "That is not a driver package's id.";
        }

        if (BuiltIns.Any(b => b.Id == id))
        {
            return "Drivers that come with PC Diagnostic cannot be removed.";
        }

        var folder = Path.Combine(_driversFolder, id);
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            var approvals = Approvals().Where(a => a.Id != id).ToList();
            File.WriteAllText(Path.Combine(_driversFolder, "approved.json"), JsonSerializer.Serialize(approvals, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The package could not be removed: " + ex.Message;
        }

        LogRemoved(_logger, id);
        _link.Restart();
        WaitForHost(list => list.All(p => p.Id != id));
        return null;
    }

    /// <summary>Approves a package by its hash (or withdraws it); the screen host starts again to load it.</summary>
    public string? Approve(string id, string sha256, bool approve)
    {
        var approvals = Approvals().Where(a => a.Id != id).ToList();
        if (approve)
        {
            if (_link.Packages.FirstOrDefault(p => p.Id == id && !p.BuiltIn && string.Equals(p.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) is null)
            {
                return "That package (with that content) is not in the drivers folder.";
            }

            approvals.Add(new Approval(id, sha256));
        }

        try
        {
            Directory.CreateDirectory(_driversFolder);
            File.WriteAllText(Path.Combine(_driversFolder, "approved.json"), JsonSerializer.Serialize(approvals, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The approval could not be saved: " + ex.Message;
        }

        _link.Restart();
        WaitForHost(list => list.Any(p => p.Id == id && p.Approved == approve));
        return null;
    }

    /// <summary>
    /// Waits (up to 10 s) for the screen host, started again, to report its packages as asked, so the answer lists
    /// them as they are now (an imported package, an approval).
    /// </summary>
    private void WaitForHost(Func<IReadOnlyList<PackageInfo>, bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !ready(_link.Packages))
        {
            Thread.Sleep(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _link.Devices.Where(d => IsOn(d.Key)))
        {
            await _link.CloseAsync(device.Key).ConfigureAwait(false);
        }

        await _link.DisposeAsync().ConfigureAwait(false);
        _holders.Release();
    }

    /// <summary>A screen's progress as its details read: its firmware and the pictures shown (and not taken).</summary>
    private static string? Progress(Status status)
    {
        var parts = new List<string>();
        if (status.Firmware is { Length: > 0 } firmware)
        {
            parts.Add("Firmware " + firmware);
        }

        if (status.Pictures > 0 || status.Refused > 0)
        {
            parts.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{status.Pictures} pictures shown{(status.Refused > 0 ? $", {status.Refused} not taken" : string.Empty)}"));
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Runs <see cref="Reconcile"/> on a worker, one at a time (pausing a holder waits for its services to stop: never
    /// on the caller's thread); a request while one runs runs it again after.
    /// </summary>
    private void Schedule()
    {
        Interlocked.Exchange(ref _again, 1);
        if (!_reconciling.Wait(0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _again, 0) == 1)
                {
                    Reconcile();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogReconcileFailed(_logger, ex);
            }
            finally
            {
                _reconciling.Release();
            }

            if (Volatile.Read(ref _again) == 1)
            {
                Schedule();
            }
        });
    }

    /// <summary>
    /// Whether the programs paused for the screens are started again: when no screen is to be used, or when the
    /// screens to be used have been missing for a while (unplugged). Not when they are missing for a moment: the
    /// screen host starting again, or a screen connecting again, lists none until it has looked.
    /// </summary>
    internal static bool ReleaseHolders(int wanted, int present, DateTime? missingSince, DateTime now) =>
        wanted == 0 || (present == 0 && missingSince is { } since && now - since >= MissingGrace);

    /// <summary>
    /// Opens the screens in use (pausing their holders first), closes the others, sets backlights. An open is asked
    /// once and answered by the host; a screen still busy just after its holder was paused is asked again a moment
    /// later, up to 8 times (then its problem shows until its settings change, or until it is free: its holder's
    /// program can take a while to end).
    /// </summary>
    private void Reconcile()
    {
        var wanted = (_config.Devices ?? []).Count(d => d.Use);
        var used = _link.Devices.Where(d => _config.DeviceOf(d.Key).Use).ToList();
        foreach (var device in _link.Devices.Where(d => !_config.DeviceOf(d.Key).Use && _link.StatusOf(d.Key) is { Open: true }))
        {
            _ = _link.CloseAsync(device.Key);
            lock (_lastFrame)
            {
                _lastFrame.Remove(device.Key);
            }
        }

        var now = DateTime.UtcNow;
        _missingSince = wanted > 0 && used.Count == 0 ? _missingSince ?? now : null;
        if (used.Count == 0)
        {
            _problem = null;
            if (_holders.Paused.Count > 0)
            {
                if (ReleaseHolders(wanted, used.Count, _missingSince, now))
                {
                    // Started again once nothing is in use (the host closed the screens first).
                    _ = Task.Delay(500).ContinueWith(_ => _holders.Release(), TaskScheduler.Default);
                }
                else
                {
                    // Looked at again when the grace has passed (or sooner, when the screens are back).
                    _ = Task.Delay(MissingGrace + TimeSpan.FromSeconds(1)).ContinueWith(_ => Schedule(), TaskScheduler.Default);
                }
            }

            return;
        }

        foreach (var device in used)
        {
            // A screen that was busy and is free now (its holder's program ended at last) is asked for afresh.
            if (_busy.TryGetValue(device.Key, out var wasBusy) && wasBusy && !device.Busy)
            {
                lock (_asked)
                {
                    _asked.Remove(device.Key);
                }
            }

            _busy[device.Key] = device.Busy;
            var status = _link.StatusOf(device.Key);
            if (status is { Open: true })
            {
                lock (_asked)
                {
                    _asked.Remove(device.Key);
                }

                var brightness = _config.DeviceOf(device.Key).Brightness;
                if (!_brightness.TryGetValue(device.Key, out var lit) || lit != brightness)
                {
                    _brightness[device.Key] = brightness;
                    _ = _link.SetBrightnessAsync(device.Key, brightness);
                }

                continue;
            }

            (DateTime At, int Attempts) asked;
            lock (_asked)
            {
                asked = _asked.GetValueOrDefault(device.Key);
            }

            var since = DateTime.UtcNow - asked.At;
            if (since < TimeSpan.FromSeconds(1.5))
            {
                // Asked a moment ago: its answer, or another try, after a pause.
                _ = Task.Delay(TimeSpan.FromSeconds(1.6)).ContinueWith(_ => Schedule(), TaskScheduler.Default);
                continue;
            }

            if (asked.Attempts >= 8)
            {
                continue;
            }

            _problem = _holders.Take(HoldersOf(device.DriverId));
            if (_problem is null)
            {
                _brightness.Remove(device.Key);
                lock (_asked)
                {
                    _asked[device.Key] = (DateTime.UtcNow, asked.Attempts + 1);
                }

                _ = _link.OpenAsync(device.Key);
            }
        }
    }

    /// <summary>The programs that hold a package's screens (from its driver.json; no code is loaded here).</summary>
    private IReadOnlyList<ScreenHolder> HoldersOf(string driverId)
    {
        var package = BuiltIns.FirstOrDefault(p => p.Id == driverId)
            ?? (Directory.Exists(_driversFolder)
                ? Directory.GetDirectories(_driversFolder).Select(f => ScreenDriverPackage.Read(f, out _)).FirstOrDefault(p => p?.Id == driverId)
                : null);
        return package?.Holders ?? [];
    }

    private List<Approval> Approvals()
    {
        try
        {
            var file = Path.Combine(_driversFolder, "approved.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<List<Approval>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The drivers folder, writable by administrators and SYSTEM only (anyone may read), and the staging folder (only
    /// they may even read). Set each time the recorder starts (so a folder whose rights were loosened is closed again);
    /// the screen drivers' folder of earlier versions is moved in first.
    /// </summary>
    private void EnsureDriversFolder()
    {
        try
        {
            if (!Directory.Exists(_driversFolder) && _driversFolder == DataPaths.DriversDirectory && Directory.Exists(DataPaths.ScreenDriversDirectory))
            {
                Directory.Move(DataPaths.ScreenDriversDirectory, _driversFolder);
            }

            Lock(_driversFolder, usersRead: true);
            if (_driversFolder == DataPaths.DriversDirectory)
            {
                Lock(DataPaths.DriverStagingDirectory, usersRead: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LogFolderFailed(_logger, _driversFolder, ex);
        }
    }

    /// <summary>A folder only administrators and SYSTEM may change (and, when asked, users may read), made if needed.</summary>
    private static void Lock(string folder, bool usersRead)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (usersRead)
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        var info = new DirectoryInfo(folder);
        if (info.Exists)
        {
            info.SetAccessControl(security);
        }
        else
        {
            info.Create(security);
        }
    }

    private static int HashOf(byte[] frame)
    {
        var hash = new HashCode();
        hash.AddBytes(frame);
        return hash.ToHashCode();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A picture for the USB screen {Key} could not be made")]
    private static partial void LogFrameFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The USB screens could not be brought up to date with their settings")]
    private static partial void LogReconcileFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver package {Id} imported; it waits for approval")]
    private static partial void LogImported(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver package {Id} removed")]
    private static partial void LogRemoved(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The drivers folder {Folder} could not be made")]
    private static partial void LogFolderFailed(ILogger logger, string folder, Exception exception);
}
