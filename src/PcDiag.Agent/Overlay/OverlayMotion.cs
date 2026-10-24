using PcDiag.Core.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Moving backgrounds: a panel whose look has one is drawn once per refresh as two layers (its background, and its
/// readings with everything above them), and a separate timer composes them with the motion about 15 times a second
/// without drawing the readings again (30 times a second; a USB screen as often as its setting and its model allow, at
/// most 30). Motion keeps still, showing its first moment, while
/// the panels are hidden, being arranged or dragged, the PC is locked, it is used over Remote Desktop, the battery
/// saver is on or Windows' animations are off, and when moving backgrounds are switched off. A full-screen game keeps
/// nothing still.
/// </summary>
internal sealed partial class OverlayHost
{
    private const int SpiGetClientAreaAnimation = 0x1042;
    /// <summary>When each USB screen's last motion picture was made.</summary>
    private readonly Dictionary<string, DateTime> _deviceMotionAt = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a USB screen's next picture of motion (or of a gliding value) is due: its own rate, 1–30 a second and at
    /// most its model's; marks it made when asked.
    /// </summary>
    private bool MotionDue(OverlayDisplay display, DateTime now, bool mark)
    {
        var most = display.RefreshHz is { } hz && hz >= 1 ? (int)Math.Min(30, hz) : 30;
        var every = TimeSpan.FromSeconds(1.0 / Math.Clamp(_config.DeviceOf(display.Key).MotionFps, 1, most));
        // A few ms early counts (the timer ticks every 33 ms, a little unevenly).
        var due = !_deviceMotionAt.TryGetValue(display.Key, out var last) || now - last >= every - TimeSpan.FromMilliseconds(8);
        if (due && mark)
        {
            _deviceMotionAt[display.Key] = now;
        }

        return due;
    }
    private readonly Dictionary<(string Screen, string Panel), OverlayRenderer.MotionLayers> _layers = [];
    private readonly Dictionary<(string Screen, string Panel), Bitmap> _frames = [];
    private readonly Dictionary<(string Screen, string Panel), (Bitmap Bitmap, RectangleF Box)> _devicePictures = [];
    private System.Windows.Forms.Timer? _motionTimer;
    private bool _moving;
    private bool _locked;
    private DateTime _allowedAt;
    private bool _allowed;

    /// <summary>The moment of every motion (seconds; one clock for all panels).</summary>
    /// <summary>The motion's clock, in seconds (a steady one: the 16 ms system tick made characters step unevenly).</summary>
    private static double MotionSeconds() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>USB screens' pictures kept without their characters, and where their panels' characters play.</summary>
    private readonly Dictionary<string, (Bitmap Picture, List<(OverlayPanel Panel, RectangleF Area)> Boxes)> _deviceBase = new(StringComparer.Ordinal);
    private Bitmap? _deviceFrame;
    private System.Windows.Forms.Timer? _deviceCharacterTimer;

    /// <summary>Runs the USB screens' characters' timer only while one shows characters over its picture.</summary>
    private void WatchDeviceCharacters()
    {
        var wanted = _config.Characters && Visible && DevicesOn().Any(d => _deviceBase.TryGetValue(d.Key, out var k) && (k.Boxes.Count > 0 || _config.StageFor(d) is not null));
        if (wanted && _deviceCharacterTimer is null)
        {
            _deviceCharacterTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _deviceCharacterTimer.Tick += (_, _) => DeviceCharactersTick();
            _deviceCharacterTimer.Start();
        }
        else if (!wanted && _deviceCharacterTimer is not null)
        {
            _deviceCharacterTimer.Dispose();
            _deviceCharacterTimer = null;
        }
    }

    private static bool Moves(OverlayPanel panel) => (panel.Style.Motion is { } motion && motion != "none") || OverlayRenderer.IsAnimation(panel.Style.Image);

    /// <summary>
    /// A USB screen whose whole picture is made again with the motion: a panel moves, or characters play that are not
    /// drawn over its kept picture (one behind a panel).
    /// </summary>
    private bool DeviceMoves(OverlayDisplay d) =>
        _config.PanelsFor(d).Any(p => !p.Hidden && Moves(p))
        || (_config.Characters && !_deviceBase.ContainsKey(d.Key) && (_config.PanelsFor(d).Any(p => !p.Hidden && HasCharacters(p)) || _config.StageFor(d) is not null));

    /// <summary>The moment panels are drawn at now: moving, or still at the start.</summary>
    private double? MotionNow() => _moving ? MotionSeconds() : null;

    /// <summary>Keeps a panel's layers for the frames between refreshes (or forgets them).</summary>
    private void KeepLayers((string Screen, string Panel) key, OverlayRenderer.MotionLayers? layers)
    {
        if (_layers.Remove(key, out var old))
        {
            old.Dispose();
        }

        if (layers is not null)
        {
            _layers[key] = layers;
        }
        else if (_frames.Remove(key, out var frame))
        {
            frame.Dispose();
        }
    }

    /// <summary>Forgets what is kept for panels no longer shown.</summary>
    private void ForgetMotion(Func<(string Screen, string Panel), bool> gone)
    {
        foreach (var key in _layers.Keys.Where(gone).ToList())
        {
            KeepLayers(key, null);
        }

        foreach (var key in _devicePictures.Keys.Where(gone).ToList())
        {
            _devicePictures[key].Bitmap.Dispose();
            _devicePictures.Remove(key);
        }
    }

    /// <summary>Runs the motion timer only while a shown panel (or a USB screen's) moves and moving backgrounds are on.</summary>
    private void WatchMotion()
    {
        var wanted = _config.Motion && Visible
            && (_windows.Keys.Any(k => PanelOf(k) is { } p && Moves(p)) || DevicesOn().Any(DeviceMoves));
        if (wanted && _motionTimer is null)
        {
            _motionTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _motionTimer.Tick += (_, _) => MotionTick();
            _motionTimer.Start();
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        else if (!wanted && _motionTimer is not null)
        {
            _motionTimer.Dispose();
            _motionTimer = null;
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            _moving = false;
        }
    }

    private void StopMotion()
    {
        _motionTimer?.Dispose();
        _motionTimer = null;
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        foreach (var key in _layers.Keys.ToList())
        {
            KeepLayers(key, null);
        }

        foreach (var picture in _devicePictures.Values)
        {
            picture.Bitmap.Dispose();
        }

        _devicePictures.Clear();
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e) =>
        _locked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect
            || (_locked && e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect));

    private void MotionTick()
    {
        if (!MotionAllowed())
        {
            if (_moving)
            {
                // Still again: every panel shows its first moment.
                _moving = false;
                Tick();
            }

            return;
        }

        _moving = true;
        var seconds = MotionSeconds();
        using var cost = CostMeter.Measure("overlay.motion");
        foreach (var (key, layers) in _layers)
        {
            if (!_windows.TryGetValue(key, out var window) || PanelOf(key) is not { } panel)
            {
                continue;
            }

            window.Show(ComposeFrame(key, layers, seconds), window.Bounds.Location, (byte)Math.Clamp(panel.Style.Opacity * 255 / 100, 0, 255));
        }

        var now = DateTime.UtcNow;
        if (DevicesOn().Any(d => DeviceMoves(d) && MotionDue(d, now, mark: false)))
        {
            DrawDevices(now, composeOnly: true);
        }
    }

    /// <summary>A panel at a moment of its motion, in a picture kept for it (made again when its size changes).</summary>
    private Bitmap ComposeFrame((string Screen, string Panel) key, OverlayRenderer.MotionLayers layers, double seconds)
    {
        if (!_frames.TryGetValue(key, out var frame) || frame.Size != layers.Size)
        {
            frame?.Dispose();
            frame = new Bitmap(layers.Size.Width, layers.Size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            _frames[key] = frame;
        }

        layers.Compose(frame, seconds);
        return frame;
    }

    /// <summary>Motion may move now (looked at once a second: the checks ask Windows).</summary>
    private bool MotionAllowed() => _config.Motion && Lively();

    /// <summary>
    /// Panels may move now (moving backgrounds, gliding values): shown, not being arranged or dragged, the PC not
    /// locked, not over Remote Desktop, no battery saver, Windows' animations on, and no full-screen game in front
    /// (unless asked otherwise). Looked at once a second: the checks ask Windows.
    /// </summary>
    private bool Lively()
    {
        if (!Visible || _arranging || _drag is not null || _locked)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - _allowedAt < TimeSpan.FromSeconds(1))
        {
            return _allowed;
        }

        _allowedAt = now;
        // A full-screen game keeps nothing still (the panels are there to be seen with it, on the monitors and USB screens).
        _allowed = !SystemInformation.TerminalServerSession && !BatterySaver() && AnimationsOn();
        return _allowed;
    }

    private static bool BatterySaver() => GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;

    private static bool AnimationsOn() => !SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var on, 0) || on;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(int action, int param, [MarshalAs(UnmanagedType.Bool)] out bool value, int winIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }
}
