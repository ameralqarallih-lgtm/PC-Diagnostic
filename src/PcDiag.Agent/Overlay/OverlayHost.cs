using PcDiag.Core.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using PcDiag.Agent.Tray;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// The on-screen display on its UI thread: one layered window per panel on each connected monitor, redrawn every
/// refresh interval with the newest values and placed by the panel's share of the room on its monitor (so a panel at
/// the right edge stays there whatever its width). A global hotkey shows and hides the panels. Identify shows each
/// monitor's number on it for a few seconds.
/// While arranging (its own hotkey, the tray or the editor), the panels take the mouse: a panel is dragged to move it
/// (onto another screen too), its corner to resize it, a reading onto another to reorder; its buttons hide it or open
/// its options, and right-clicking a reading offers its design, how it reads, its order, its size in the grid and
/// removing it. Every change is the layout's, and <see cref="Changed"/> says so (the service saves it, so the editor
/// shows it too). Every member runs on the UI thread.
/// </summary>
internal sealed partial class OverlayHost : IDisposable
{
    /// <summary>How long Identify shows the numbers.</summary>
    public static readonly TimeSpan IdentifyTime = TimeSpan.FromSeconds(4);

    /// <summary>The designs a reading can be shown with, grouped as the editor lists them.</summary>
    private static readonly (string Group, (string Id, string Name)[] Designs)[] DesignGroups =
    [
        ("Plain", [("none", "Value only")]),
        ("Dials", [("ring", "Ring"), ("arc", "Arc dial"), ("speed", "Speedometer"), ("status", "Status light")]),
        ("Graphs", [("graph", "Area graph"), ("line", "Line graph"), ("columns", "Columns")]),
        ("Bars", [("bar", "Bar"), ("thermo", "Thermometer")]),
        ("Text", [("stats", "Low · avg · high"), ("trend", "Trend arrow")]),
    ];

    private readonly Func<List<OverlaySource>> _sources;
    private readonly ILogger _logger;
    private readonly Dictionary<(string Screen, string Panel), LayeredWindow> _windows = [];
    private readonly Dictionary<(string Screen, string Panel), PanelHits> _hits = [];
    private readonly List<(LayeredWindow Window, DateTime Until)> _identify = [];
    private readonly System.Windows.Forms.Timer _timer;
    private readonly HotkeyWindow _hotkey = new();
    private readonly HotkeyWindow _arrangeHotkey = new();
    private readonly HotkeyWindow _escape = new();
    private readonly OverlayHistory _history = new();
    private readonly TopGuard _top;

    // Each panel's size last time: an automatic size only grows while the panel, its screen and which of its readings
    // have a value source stay the same (a reading whose source is not there yet, as when the recorder starts, shows
    // its source's id as its label and would otherwise keep the panel that wide).
    private readonly Dictionary<(string Screen, string Panel), (OverlayPanel Panel, double Scale, string Present, Size Size)> _sizes = [];
    private OverlayConfig _config = OverlayConfig.Empty;
    private IReadOnlyList<OverlayDisplay> _displays = [];
    private Dictionary<string, OverlaySource> _lastSources = new(StringComparer.Ordinal);
    private string? _hotkeyText;
    private string? _arrangeHotkeyText;
    private bool _hiddenByHotkey;
    private int _ticks;
    private volatile bool _showing;
    private volatile bool _arranging;
    private volatile string? _problem;

    // Arranging: the bar with Done, what is under the mouse, and the drag in progress.
    private LayeredWindow? _bar;
    private RectangleF _barDone;
    private bool _barHover;
    private (string Screen, string Panel)? _hoverKey;
    private ArrangePart _hoverPart;
    private int? _hoverReading;
    private Drag? _drag;
    private bool _menuOpen;

    public OverlayHost(Func<List<OverlaySource>> sources, ILogger logger)
    {
        _sources = sources;
        _logger = logger;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _hotkey.Pressed += (_, _) =>
        {
            _hiddenByHotkey = !_hiddenByHotkey;

            // Shown again: the panels closed on the screens come back too.
            if (!_hiddenByHotkey)
            {
                _closed.Clear();
                _closedCount = 0;
            }

            Sync();
            Tick();
        };
        _arrangeHotkey.Pressed += (_, _) => Arrange(!_arranging);
        _escape.Pressed += (_, _) => Arrange(false);
        _top = new TopGuard(OnTop, RaiseAll, () => _menuOpen, logger);
    }

    /// <summary>The layout was changed on the screens (arranging); the new one.</summary>
    public event Action<OverlayConfig>? Changed;

    /// <summary>Panels are on the screens now (read from any thread).</summary>
    public bool Showing => _showing;

    /// <summary>Whether a USB screen is showing its panels now (its key).</summary>
    public Func<string, bool>? DeviceOn { get; set; }

    /// <summary>A USB screen's panels as one picture of its layout size, at each refresh.</summary>
    public Action<string, Bitmap, bool>? DeviceFrames { get; set; }

    /// <summary>The panels are being arranged on the screens (read from any thread).</summary>
    public bool Arranging => _arranging;

    /// <summary>Shown when enabled and not hidden with the hotkey (read from any thread).</summary>
    public bool Visible => _config.Enabled && !_hiddenByHotkey;

    /// <summary>Why it cannot do everything it was asked (a hotkey another program holds).</summary>
    public string? Problem => _problem;

    private enum DragKind
    {
        Move,
        Resize,
        Reading,
    }

    /// <summary>Uses a new layout.</summary>
    public void Apply(OverlayConfig config)
    {
        _config = config;
        _timer.Interval = config.IntervalMs;
        _problem = null;
        RegisterHotkey(_hotkey, config.Hotkey, ref _hotkeyText);
        RegisterHotkey(_arrangeHotkey, config.ArrangeHotkey, ref _arrangeHotkeyText);
        foreach (var window in _windows.Values)
        {
            window.HideFromCapture(config.HideFromCapture);
        }

        Sync();
        Tick();
    }

    /// <summary>The monitors changed (connected, removed, moved or resized).</summary>
    public void SetDisplays(IReadOnlyList<OverlayDisplay> displays)
    {
        if (displays.SequenceEqual(_displays))
        {
            return;
        }

        // A drag in progress ends: the screens it was on changed (turned, moved), and its panel may be on another list now.
        if (_drag is { } drag && _windows.TryGetValue(drag.Key, out var dragged))
        {
            dragged.Capture(false);
        }

        _drag = null;
        _displays = displays;
        Sync();
        Tick();
    }

    /// <summary>Shows each monitor's number in its middle for <see cref="IdentifyTime"/>.</summary>
    public void Identify()
    {
        ClearIdentify();
        foreach (var display in _displays.Where(d => d.Kind != "device").DistinctBy(d => (d.X, d.Y, d.Width, d.Height)))
        {
            using var bitmap = OverlayRenderer.Identify(display);
            var window = new LayeredWindow(hideFromCapture: false);
            window.Show(bitmap, new Point(display.X + (display.Width - bitmap.Width) / 2, display.Y + (display.Height - bitmap.Height) / 2), 255);
            window.KeepOnTop();
            _identify.Add((window, DateTime.UtcNow + IdentifyTime));
        }
    }

    /// <summary>Starts or ends arranging: every panel (hidden ones too, faded) takes the mouse; Esc or Done ends it.</summary>
    public void Arrange(bool on)
    {
        if (_arranging == on)
        {
            return;
        }

        _arranging = on;
        _drag = null;
        _hoverKey = null;
        _hoverPart = ArrangePart.None;
        _hoverReading = null;
        if (on)
        {
            // Esc ends arranging (only while arranging: the key is the screens' meanwhile).
            _ = _escape.Register(new HotkeyGesture(0, 0x1B, "Esc"));
        }
        else
        {
            _escape.Unregister();
        }

        Sync();
        Tick();
    }

    public void Dispose()
    {
        _top.Dispose();
        _hoverTimer?.Dispose();
        _handles?.Dispose();
        StopMotion();
        StopGlide();
        StopSprites();
        _timer.Dispose();
        _hotkey.Dispose();
        _arrangeHotkey.Dispose();
        _escape.Dispose();
        _bar?.Dispose();
        ClearIdentify();
        foreach (var window in _windows.Values)
        {
            window.Dispose();
        }

        _windows.Clear();
    }

    private void RegisterHotkey(HotkeyWindow window, string? text, ref string? registered)
    {
        if (string.Equals(text, registered, StringComparison.Ordinal))
        {
            return;
        }

        registered = text;
        window.Unregister();
        if (string.IsNullOrWhiteSpace(text) || HotkeyGesture.TryParse(text, out _) is not { } gesture)
        {
            return;
        }

        var error = window.Register(gesture);
        if (error != 0)
        {
            _problem = $"The hotkey {gesture.Text} is used by another program; choose another one.";
            LogHotkeyFailed(_logger, gesture.Text, error);
        }
    }

    /// <summary>Makes the windows match the layout, the monitors and whether it is shown (or arranged).</summary>
    private void Sync()
    {
        var wanted = new HashSet<(string, string)>();
        if (_arranging || Visible)
        {
            // A USB screen has no windows: its panels are drawn into its pictures.
            foreach (var display in _displays.Where(d => d.Kind != "device"))
            {
                // The panels for the monitor's shape now (its portrait ones while it is turned upright).
                foreach (var panel in _config.PanelsFor(display).Where(p => _arranging || (!p.Hidden && !_closed.Contains((display.Key, p.Id)))))
                {
                    wanted.Add((display.Key, panel.Id));
                }
            }
        }

        foreach (var key in _windows.Keys.Where(k => !wanted.Contains(k)).ToList())
        {
            _windows[key].Dispose();
            _windows.Remove(key);
            _windowContent.Remove(key);
            _sizes.Remove(key);
            _hits.Remove(key);
            KeepLayers(key, null);
        }

        foreach (var key in wanted.Where(k => !_windows.ContainsKey(k)))
        {
            var window = new LayeredWindow(_config.HideFromCapture);
            window.Pointer += e => OnPointer(key, e);
            window.CursorAt = point => CursorAt(key, point);
            _windows[key] = window;
        }

        foreach (var window in _windows.Values)
        {
            window.Interactive = _arranging;
        }

        if (_arranging && _bar is null)
        {
            _bar = new LayeredWindow(hideFromCapture: false) { Interactive = true };
            _bar.Pointer += OnBarPointer;
            _bar.CursorAt = point => _barDone.Contains(point) ? Cursors.Hand : Cursors.Default;
        }
        else if (!_arranging && _bar is not null)
        {
            _bar.Dispose();
            _bar = null;
            _barHover = false;
        }

        SyncCharacters();
        _showing = _windows.Count > 0 || DevicesOn().Count > 0;
        WatchHandles();
        WatchMotion();
    }

    /// <summary>The USB screens showing their panels now.</summary>
    private List<OverlayDisplay> DevicesOn() => DeviceOn is { } on ? [.. _displays.Where(d => d.Kind == "device" && on(d.Key))] : [];

    /// <summary>
    /// Each USB screen in use, as one picture: its background color, then its panels drawn as on a monitor (the same
    /// sizes kept and the same placing by their share of the room they leave), with each panel's opacity.
    /// </summary>
    /// <param name="now">The moment the values and graphs are drawn at.</param>
    /// <param name="composeOnly">A motion frame: the panels drawn at the last refresh, their moving backgrounds moved on.</param>
    /// <param name="glide">A step of values gliding: only the screens whose next picture is due (their rate).</param>
    private void DrawDevices(DateTime now, bool composeOnly = false, bool glide = false)
    {
        using var cost = CostMeter.Measure(composeOnly ? "usb.motion" : glide ? "usb.glide" : "usb.refresh");
        var drawn = new HashSet<(string, string)>();
        foreach (var display in DevicesOn())
        {
            if ((composeOnly || glide) && !MotionDue(display, now, mark: true))
            {
                drawn.UnionWith(_config.PanelsFor(display).Where(p => !p.Hidden).Select(p => (display.Key, p.Id)));
                continue;
            }

            if (composeOnly && _deviceStill.TryGetValue(display.Key, out var still) && MotionOver(display, still))
            {
                continue;
            }

            var settings = _config.DeviceOf(display.Key);
            // The panels that never move, kept as one picture with the background: a motion frame then draws only the
            // moving ones over it (made with the full picture, when nothing has characters and nothing still lies over
            // a moving panel).
            var keepStill = !composeOnly && !_config.PanelsFor(display).Any(p => !p.Hidden && HasCharacters(p)) && _config.StageFor(display) is null;
            var size = new Size(Math.Max(1, display.Width), Math.Max(1, display.Height));
            Bitmap? stillPicture = null;
            if (keepStill)
            {
                // The last still picture is drawn over again when it is the same size (no new picture each second).
                if (_deviceStill.Remove(display.Key, out var prior) && prior.Picture.Size == size)
                {
                    stillPicture = prior.Picture;
                }
                else
                {
                    prior?.Picture.Dispose();
                    stillPicture = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                }
            }

            using var stillGraphics = stillPicture is null ? null : Graphics.FromImage(stillPicture);
            stillGraphics?.Clear(OverlayRenderer.Parse(settings.Background));
            var movingPanels = new List<((string, string) Key, Rectangle At, int Opacity)>();
            // Without characters behind a panel, the picture is kept without its characters and they are drawn over it
            // 30 times a second (DeviceCharactersTick) rather than the whole picture made again.
            var over = !_config.PanelsFor(display).Any(p => !p.Hidden && (p.Characters ?? []).Any(c => c.Behind && c.Place != "inside"));
            var boxes = new List<(OverlayPanel Panel, RectangleF Area)>();
            // Where each reading's tile is on this screen (characters go to the one that turns bad).
            var screenSpots = new ReadingSpots();
            // The screen's whole picture, kept and drawn over again each time (no new picture each second).
            if (!_deviceRefreshFrames.TryGetValue(display.Key, out var frame) || frame.Size != size)
            {
                frame?.Dispose();
                frame = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _deviceRefreshFrames[display.Key] = frame;
            }

            using (var g = Graphics.FromImage(frame))
            {
                g.Clear(OverlayRenderer.Parse(settings.Background));
                foreach (var panel in _config.PanelsFor(display).Where(p => !p.Hidden))
                {
                    var key = (display.Key, panel.Id);
                    drawn.Add(key);
                    Bitmap picture;
                    RectangleF box;
                    var content = ContentKey(panel, display.Scale);
                    var unchanged = !composeOnly && content is not null && _deviceContent.TryGetValue(key, out var shown)
                        && ReferenceEquals(shown.Panel, panel) && shown.Content == content && (!Moves(panel) || _layers.ContainsKey(key));
                    if ((composeOnly || unchanged) && _devicePictures.TryGetValue(key, out var kept))
                    {
                        // A motion frame, or a refresh that shows what it showed: the picture kept (a moving one at this moment).
                        box = kept.Box;
                        picture = _layers.TryGetValue(key, out var moving) ? ComposeFrame(key, moving, MotionSeconds()) : kept.Bitmap;
                    }
                    else
                    {
                        _deviceContent[key] = (panel, content);
                        var present = string.Concat(panel.Readings.Select(r => _lastSources.ContainsKey(r.Source) ? '1' : '0'));
                        Size? atLeast = _sizes.TryGetValue(key, out var last) && SameContent(last.Panel, panel) && last.Scale == display.Scale && last.Present == present ? last.Size : null;
                        var (bitmap, hits, layers) = OverlayRenderer.RenderLayers(panel, _lastSources, display.Scale, _history, now, atLeast, null, MotionNow(), keepLayers: Moves(panel));
                        KeepLayers(key, layers);
                        _sizes[key] = (panel, display.Scale, present, bitmap.Size);
                        if (_devicePictures.Remove(key, out var old))
                        {
                            old.Bitmap.Dispose();
                        }

                        // Kept for the motion frames until the next refresh.
                        _devicePictures[key] = (bitmap, hits.Box);
                        _deviceHits[key] = hits;
                        picture = bitmap;
                        box = hits.Box;
                    }

                    var (x, y) = Place(panel, display.Width, display.Height, box);
                    var area = new RectangleF(x + box.X, y + box.Y, box.Width, box.Height);
                    if (_deviceHits.TryGetValue(key, out var drawnHits))
                    {
                        screenSpots.Add(panel, drawnHits, x, y);
                    }

                    var characters = _config.Characters && HasCharacters(panel) && !over ? panel : null;
                    var moment = _moving ? MotionSeconds() : _stillAt;
                    if (over && _config.Characters && HasCharacters(panel))
                    {
                        boxes.Add((panel, area));
                    }

                    if (characters is not null)
                    {
                        DrawCharacters(g, characters with { Characters = [.. characters.Characters!.Where(c => c.Behind && c.Place != "inside")] }, area, frame.Size, moment, _lastSources, c => DeviceReacting(display.Key, panel.Id, c), screenSpots);
                    }

                    DrawPanelPicture(g, picture, x, y, panel.Style.Opacity);
                    var at = new Rectangle(x, y, picture.Width, picture.Height);
                    if (stillGraphics is not null)
                    {
                        if (_layers.ContainsKey(key))
                        {
                            movingPanels.Add((key, at, panel.Style.Opacity));
                        }
                        else if (movingPanels.Any(m => m.At.IntersectsWith(at)))
                        {
                            // A still panel over a moving one: the frames are made whole.
                            keepStill = false;
                        }
                        else
                        {
                            DrawPanelPicture(stillGraphics, picture, x, y, panel.Style.Opacity);
                        }
                    }

                    if (characters is not null)
                    {
                        DrawCharacters(g, characters with { Characters = [.. characters.Characters!.Where(c => !c.Behind || c.Place == "inside")] }, area, frame.Size, moment, _lastSources, c => DeviceReacting(display.Key, panel.Id, c), screenSpots);
                    }
                }
            }

            if (over)
            {
                if (_deviceBase.Remove(display.Key, out var old))
                {
                    old.Picture.Dispose();
                }

                _deviceBase[display.Key] = ((Bitmap)frame.Clone(), boxes);
            }
            else if (_deviceBase.Remove(display.Key, out var gone))
            {
                gone.Picture.Dispose();
            }

            if (!composeOnly)
            {
                if (_deviceStill.Remove(display.Key, out var oldStill))
                {
                    oldStill.Picture.Dispose();
                }

                if (keepStill && stillPicture is not null && movingPanels.Count > 0)
                {
                    _deviceStill[display.Key] = new DeviceStill(stillPicture, movingPanels);
                    stillPicture = null;
                }
            }

            stillPicture?.Dispose();
            _deviceSpots[display.Key] = screenSpots;
            DrawDeviceCharacters(frame, display, over ? boxes : [], _moving ? MotionSeconds() : _stillAt);
            DeviceFrames?.Invoke(display.Key, frame, composeOnly);
        }

        if (!composeOnly)
        {
            // Panels of USB screens no longer shown: their pictures and layers go.
            ForgetMotion(k => !_windows.ContainsKey(k) && !drawn.Contains(k) && _devicePictures.ContainsKey(k));
            foreach (var key in _deviceBase.Keys.Where(k => !DevicesOn().Any(d => d.Key == k)).ToList())
            {
                _deviceBase.Remove(key, out var gone);
                gone.Picture.Dispose();
            }

            // USB screens no longer shown: their still parts and motion pictures go.
            foreach (var key in _deviceStill.Keys.Where(k => !DevicesOn().Any(d => d.Key == k)).ToList())
            {
                _deviceStill.Remove(key, out var gone);
                gone!.Picture.Dispose();
            }

            foreach (var frames in new[] { _deviceMotionFrames, _deviceRefreshFrames })
            {
                foreach (var key in frames.Keys.Where(k => !DevicesOn().Any(d => d.Key == k)).ToList())
                {
                    frames.Remove(key, out var gone);
                    gone!.Dispose();
                }
            }

            WatchDeviceCharacters();
        }
    }

    /// <summary>Each USB screen's panel's readings' places in its picture, and each USB screen's readings' places (by source).</summary>
    private readonly Dictionary<(string Screen, string Panel), PanelHits> _deviceHits = [];
    private readonly Dictionary<string, ReadingSpots> _deviceSpots = new(StringComparer.Ordinal);

    /// <summary>A USB screen's still part (background and panels that never move) and where its moving panels go.</summary>
    private sealed record DeviceStill(Bitmap Picture, List<((string Screen, string Panel) Key, Rectangle At, int Opacity)> Moving);

    private readonly Dictionary<string, DeviceStill> _deviceStill = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _deviceMotionFrames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _deviceRefreshFrames = new(StringComparer.Ordinal);

    /// <summary>
    /// A USB screen's motion frame from its still part: copied into a picture kept for it, its moving panels drawn
    /// over at this moment, and sent. False when a moving panel's layers are gone (the frame is then made whole).
    /// </summary>
    private bool MotionOver(OverlayDisplay display, DeviceStill still)
    {
        if (still.Moving.Any(m => !_layers.ContainsKey(m.Key)))
        {
            return false;
        }

        if (!_deviceMotionFrames.TryGetValue(display.Key, out var frame) || frame.Size != still.Picture.Size)
        {
            frame?.Dispose();
            frame = new Bitmap(still.Picture.Width, still.Picture.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            _deviceMotionFrames[display.Key] = frame;
        }

        var seconds = MotionSeconds();
        using (var g = Graphics.FromImage(frame))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImageUnscaled(still.Picture, 0, 0);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            foreach (var (key, at, opacity) in still.Moving)
            {
                DrawPanelPicture(g, ComposeFrame(key, _layers[key], seconds), at.X, at.Y, opacity);
            }
        }

        DeviceFrames?.Invoke(display.Key, frame, true);
        return true;
    }

    /// <summary>A panel's picture drawn at its place, faded by its opacity.</summary>
    private static void DrawPanelPicture(Graphics g, Bitmap picture, int x, int y, int opacity)
    {
        if (opacity >= 100)
        {
            g.DrawImage(picture, x, y, picture.Width, picture.Height);
            return;
        }

        using var faded = new System.Drawing.Imaging.ImageAttributes();
        faded.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity / 100f });
        g.DrawImage(picture, new Rectangle(x, y, picture.Width, picture.Height), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, faded);
    }

    /// <summary>A USB screen's characters over its picture: those of its panels (when they are drawn over it) and its own.</summary>
    private void DrawDeviceCharacters(Bitmap frame, OverlayDisplay display, List<(OverlayPanel Panel, RectangleF Area)> boxes, double moment)
    {
        if (!_config.Characters)
        {
            return;
        }

        using var g = Graphics.FromImage(frame);
        if (!_deviceCompanies.TryGetValue(display.Key, out var company))
        {
            company = new CharacterCompany();
            _deviceCompanies[display.Key] = company;
        }

        var stage = _config.StageFor(display);
        company.Keep(boxes.Select(b => b.Panel).Concat(stage is null ? [] : [stage]).SelectMany(p => (p.Characters ?? []).Select(c => p.Id + "/" + c.Id)).ToHashSet(StringComparer.Ordinal));
        foreach (var (panel, area) in boxes)
        {
            DrawCharacters(g, panel, area, frame.Size, moment, _lastSources, c => DeviceReacting(display.Key, panel.Id, c), _deviceSpots.GetValueOrDefault(display.Key), company);
        }

        if (stage is not null)
        {
            DrawCharacters(g, stage, new RectangleF(0, 0, frame.Width, frame.Height), frame.Size, moment, _lastSources, c => DeviceReacting(display.Key, OverlayScreen.StageId, c), _deviceSpots.GetValueOrDefault(display.Key), company);
        }
    }

    /// <summary>The USB screens' characters moved on: drawn over each screen's kept picture and sent, 30 times a second.</summary>
    private void DeviceCharactersTick()
    {
        using var cost = CostMeter.Measure("usb.characters");
        if (!MotionAllowed())
        {
            return;
        }

        var moment = MotionSeconds();
        foreach (var display in DevicesOn())
        {
            if (!_deviceBase.TryGetValue(display.Key, out var kept) || (kept.Boxes.Count == 0 && _config.StageFor(display) is null))
            {
                continue;
            }

            if (_deviceFrame is null || _deviceFrame.Size != kept.Picture.Size)
            {
                _deviceFrame?.Dispose();
                _deviceFrame = new Bitmap(kept.Picture.Width, kept.Picture.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }

            using (var g = Graphics.FromImage(_deviceFrame))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawImageUnscaled(kept.Picture, 0, 0);
            }

            DrawDeviceCharacters(_deviceFrame, display, kept.Boxes, moment);
            DeviceFrames?.Invoke(display.Key, _deviceFrame, true);
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _identify.Where(i => i.Until <= now).ToList())
        {
            expired.Window.Dispose();
            _identify.Remove(expired);
        }

        var devices = DevicesOn();
        if (_windows.Count == 0 && _bar is null && devices.Count == 0)
        {
            return;
        }

        try
        {
            Dictionary<string, OverlaySource> sources;
            using (CostMeter.Measure("overlay.sources"))
            {
                sources = _sources().GroupBy(s => s.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            }

            // Drawn at the values shown now, gliding to the new ones (or at the new ones at once).
            _lastSources = StartGlide(sources);
            var raise = ++_ticks % Math.Max(1, 2000 / Math.Max(1, _config.IntervalMs)) == 0;

            // The values the graphs on the screens show: one point per refresh for each.
            var graphed = _windows.Keys
                .Select(PanelOf).OfType<OverlayPanel>().SelectMany(p => p.Readings)
                .Concat(devices.SelectMany(d => _config.PanelsFor(d).SelectMany(p => p.Readings)))
                .Where(r => OverlayStyles.NeedsHistory(OverlayStyles.VisualOf(r)))
                .Select(r => r.Source)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var source in graphed)
            {
                _history.Add(source, now, sources.GetValueOrDefault(source)?.Value);
            }

            _history.KeepOnly(graphed);
            DrawDevices(now);
            foreach (var key in _windows.Keys.ToList())
            {
                Draw(key, now);
            }

            DrawBar();
            if (raise)
            {
                RaiseAll();
            }

            // Handles shown over a panel whose size changed: drawn again at its size.
            if (_handlesKey is { } shownOver && _drag is null && _windows.TryGetValue(shownOver, out var under) && _handles is { } handles && handles.Bounds != under.Bounds)
            {
                ShowHandles(shownOver);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            LogDrawFailed(_logger, ex);
        }
    }

    /// <summary>Draws one panel's window with the newest values (and, while arranging, the drag and the mouse).</summary>
    private void Draw((string Screen, string Panel) key, DateTime now)
    {
        using var cost = CostMeter.Measure("overlay.panels");
        if (!_windows.TryGetValue(key, out var window)
            || _displays.FirstOrDefault(d => d.Key == key.Screen) is not { } display
            || PanelOf(key) is not { } panel)
        {
            return;
        }

        var drag = _drag is { } d && d.Key == key ? d : null;
        var drawn = drag is { Kind: DragKind.Resize, Preview: { } preview } ? preview : panel;

        // Showing what it showed (the same panel, values and place; no graph, nothing loading, nothing moving, not being
        // arranged): its window keeps the picture it has.
        var content = _arranging || drag is not null || Moves(drawn) ? null : ContentKey(drawn, display.Scale);
        var place = (display.X, display.Y, display.Width, display.Height, display.Scale);
        if (content is not null && _windowContent.TryGetValue(key, out var shown) && ReferenceEquals(shown.Window, window)
            && ReferenceEquals(shown.Panel, panel) && shown.Content == content && shown.Place == place)
        {
            return;
        }

        _windowContent[key] = (window, panel, content, place);
        ArrangeView? view = _arranging
            ? new ArrangeView(
                _hoverKey == key ? _hoverPart : ArrangePart.None,
                _hoverKey == key ? _hoverReading : null,
                drag is { Kind: DragKind.Reading, Moved: true } ? drag.Reading : null,
                drag?.Target,
                panel.Hidden)
            : null;

        // The same panel on the same screen keeps at least its size, also when the layout is saved again (a change to
        // the panel itself, the scaling or a value source appearing or going starts afresh).
        var present = string.Concat(drawn.Readings.Select(r => _lastSources.ContainsKey(r.Source) ? '1' : '0'));
        Size? atLeast = _sizes.TryGetValue(key, out var last) && SameContent(last.Panel, drawn) && last.Scale == display.Scale && last.Present == present ? last.Size : null;
        // A moving background: drawn at the moment now, its layers kept for the frames until the next refresh.
        var (bitmap, hits, layers) = OverlayRenderer.RenderLayers(drawn, _lastSources, display.Scale, _history, now, atLeast, view, MotionNow(), keepLayers: Moves(drawn));
        KeepLayers(key, layers);
        using (bitmap)
        {
            if (atLeast != bitmap.Size)
            {
                LogPanelSize(_logger, key.Panel, key.Screen, bitmap.Width, bitmap.Height);
            }

            _sizes[key] = (drawn, display.Scale, present, bitmap.Size);
            _hits[key] = hits;
            var position = drag switch
            {
                // Dragged: where the mouse took it; resized: its top-left corner stays put.
                { Kind: DragKind.Move, Moved: true } => drag.Window,
                { Kind: DragKind.Resize } => drag.Window,
                _ => Placed(panel, display, hits.Box),
            };
            var opacity = panel.Style.Opacity * 255 / 100 * (_arranging && panel.Hidden ? 0.45 : 1);
            window.Show(bitmap, position, (byte)Math.Clamp(opacity, 0, 255));
        }
    }

    /// <summary>What each USB screen's panel showed when it was last drawn.</summary>
    private readonly Dictionary<(string Screen, string Panel), (OverlayPanel Panel, string? Content)> _deviceContent = [];

    /// <summary>What each panel window showed when it was last drawn, where.</summary>
    private readonly Dictionary<(string Screen, string Panel), (LayeredWindow Window, OverlayPanel Panel, string? Content, (int, int, int, int, double) Place)> _windowContent = [];

    /// <summary>
    /// What a panel shows now, as text to compare: each of its values as its source has it (number, text, limit, unit,
    /// label, room), at its scale. Null when it is drawn every time: a graph (it moves on with the clock) or a value
    /// still loading (its mark turns).
    /// </summary>
    private string? ContentKey(OverlayPanel panel, double scale)
    {
        var text = new System.Text.StringBuilder();
        text.Append(scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
        foreach (var reading in panel.Readings)
        {
            if (OverlayStyles.NeedsHistory(OverlayStyles.VisualOf(reading)))
            {
                return null;
            }

            text.Append(reading.Source).Append('=');
            if (_lastSources.TryGetValue(reading.Source, out var source))
            {
                if (source.Loading)
                {
                    return null;
                }

                text.Append(source.Value?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(source.Text).Append('|')
                    .Append(source.Max?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                    .Append(source.Unit).Append('|').Append(source.Label).Append('|');
                if (source.Room is { } room)
                {
                    text.AppendJoin(',', room);
                }
            }

            text.Append(';');
        }

        return text.ToString();
    }

    /// <summary>The same panel but for where it sits and whether it is hidden.</summary>
    private static bool SameContent(OverlayPanel a, OverlayPanel b) =>
        ReferenceEquals(a, b) || JsonSerializer.Serialize(Plain(a)) == JsonSerializer.Serialize(Plain(b));

    /// <summary>A panel without what does not change how it is drawn (its place, whether hidden, where to restore it, its handles).</summary>
    private static OverlayPanel Plain(OverlayPanel p) => p with { X = 0, Y = 0, Hidden = false, Restore = null, Movable = false, Resizable = false, Closable = false, Characters = null };

    /// <summary>The bar at the top of the main screen while arranging.</summary>
    private void DrawBar()
    {
        if (_bar is null || (_displays.FirstOrDefault(d => d.Primary) ?? (_displays.Count > 0 ? _displays[0] : null)) is not { } display)
        {
            return;
        }

        var (bitmap, done) = OverlayRenderer.ArrangeBar(display.Scale, _barHover);
        using (bitmap)
        {
            _barDone = done;
            _bar.Show(bitmap, new Point(display.X + ((display.Width - bitmap.Width) / 2), display.Y + (int)(10 * display.Scale)), 255);
        }
    }

    private void Redraw((string Screen, string Panel) key)
    {
        try
        {
            Draw(key, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            LogDrawFailed(_logger, ex);
        }
    }

    // ---- Arranging ---------------------------------------------------------------------------------------------

    private (ArrangePart Part, int Reading) HitAt((string Screen, string Panel) key, Point point) =>
        _hits.TryGetValue(key, out var hits) ? hits.At(point) : (ArrangePart.None, -1);

    private Cursor CursorAt((string Screen, string Panel) key, Point point)
    {
        if (_drag is { } drag)
        {
            return drag.Kind switch { DragKind.Resize => Cursors.SizeNWSE, DragKind.Move => Cursors.SizeAll, _ => Cursors.Hand };
        }

        return HitAt(key, point).Part switch
        {
            ArrangePart.Grip => Cursors.SizeNWSE,
            ArrangePart.Close or ArrangePart.Menu or ArrangePart.Reading => Cursors.Hand,
            ArrangePart.Panel => Cursors.SizeAll,
            _ => Cursors.Default,
        };
    }

    private void OnPointer((string Screen, string Panel) key, PointerEvent e)
    {
        if (!_arranging || !_windows.TryGetValue(key, out var window))
        {
            return;
        }

        switch (e.Action)
        {
            case PointerAction.Move when _drag is { } drag:
                if (drag.Key == key)
                {
                    DragTo(drag, window, e.Screen);
                }

                break;
            case PointerAction.Move:
                Hover(key, e.Client);
                break;
            case PointerAction.LeftDown:
                Press(key, window, e);
                break;
            case PointerAction.LeftUp when _drag is { } done && done.Key == key:
                window.Capture(false);
                _drag = null;
                Finish(done, window);
                break;
            case PointerAction.RightUp when _drag is null:
                var (part, reading) = HitAt(key, e.Client);
                if (part == ArrangePart.Reading)
                {
                    ReadingMenu(key, reading, e.Screen);
                }
                else if (part != ArrangePart.None)
                {
                    PanelMenu(key, e.Screen);
                }

                break;
            case PointerAction.Leave when _drag is null && _hoverKey == key:
                _hoverKey = null;
                _hoverPart = ArrangePart.None;
                _hoverReading = null;
                Redraw(key);
                break;
        }
    }

    private void Hover((string Screen, string Panel) key, Point client)
    {
        var (part, reading) = HitAt(key, client);
        int? hover = part == ArrangePart.Reading ? reading : null;
        if (_hoverKey == key && _hoverPart == part && _hoverReading == hover)
        {
            return;
        }

        var previous = _hoverKey;
        _hoverKey = key;
        _hoverPart = part;
        _hoverReading = hover;
        if (previous is { } old && old != key)
        {
            Redraw(old);
        }

        Redraw(key);
    }

    private void Press((string Screen, string Panel) key, LayeredWindow window, PointerEvent e)
    {
        if (PanelOf(key) is not { } panel || _displays.FirstOrDefault(d => d.Key == key.Screen) is not { } display)
        {
            return;
        }

        var (part, reading) = HitAt(key, e.Client);
        switch (part)
        {
            case ArrangePart.Close:
                ChangePanel(key, p => p with { Hidden = !p.Hidden });
                return;
            case ArrangePart.Menu:
                PanelMenu(key, e.Screen);
                return;
            case ArrangePart.None:
                return;
        }

        // A resize starts from the panel's size now, at 100 % scaling.
        var box = _hits.TryGetValue(key, out var hits) ? hits.Box : RectangleF.Empty;
        var size = new SizeF(panel.Width is { } w ? (float)w : box.Width / (float)display.Scale, panel.Height is { } h ? (float)h : box.Height / (float)display.Scale);
        _drag = new Drag(part switch { ArrangePart.Grip => DragKind.Resize, ArrangePart.Reading => DragKind.Reading, _ => DragKind.Move }, key, e.Screen, window.Bounds.Location, size, reading);
        window.Capture(true);
    }

    private void DragTo(Drag drag, LayeredWindow window, Point screen)
    {
        var dx = screen.X - drag.Start.X;
        var dy = screen.Y - drag.Start.Y;
        if (!drag.Moved && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;
        }

        drag.Moved = true;
        switch (drag.Kind)
        {
            case DragKind.Move:
                drag.Window = new Point(drag.Origin.X + dx, drag.Origin.Y + dy);
                window.MoveTo(drag.Window);
                break;
            case DragKind.Resize when PanelOf(drag.Key) is { } panel && _displays.FirstOrDefault(d => d.Key == drag.Key.Screen) is { } display:
                var scale = (float)display.Scale;
                drag.Window = drag.Origin;
                drag.Preview = panel with
                {
                    Width = Math.Round(Math.Clamp(drag.Size.Width + (dx / scale), 40, 4000)),
                    Height = Math.Round(Math.Clamp(drag.Size.Height + (dy / scale), 20, 3000)),
                };
                Redraw(drag.Key);
                break;
            case DragKind.Reading:
                var (part, target) = HitAt(drag.Key, new Point(screen.X - window.Bounds.X, screen.Y - window.Bounds.Y));
                int? next = part == ArrangePart.Reading ? target : null;
                if (next != drag.Target)
                {
                    drag.Target = next;
                    Redraw(drag.Key);
                }

                break;
        }
    }

    private void Finish(Drag drag, LayeredWindow window)
    {
        if (!drag.Moved)
        {
            Redraw(drag.Key);
            return;
        }

        switch (drag.Kind)
        {
            case DragKind.Move:
                MovePanel(drag.Key, window.Bounds);
                break;
            case DragKind.Resize when drag.Preview is { } preview && _displays.FirstOrDefault(d => d.Key == drag.Key.Screen) is { } display:
                // The new size, with the share of the room that keeps its top-left corner where it is.
                var bounds = window.Bounds;
                var box = _hits.TryGetValue(drag.Key, out var drawn) ? drawn.Box : new RectangleF(0, 0, bounds.Width, bounds.Height);
                ChangePanel(drag.Key, p => p with
                {
                    Width = preview.Width,
                    Height = preview.Height,
                    X = Share(bounds.X + (int)Math.Round(box.X) - display.X, display.Width - (int)Math.Round(box.Width)),
                    Y = Share(bounds.Y + (int)Math.Round(box.Y) - display.Y, display.Height - (int)Math.Round(box.Height)),
                    Restore = null,
                });
                break;
            case DragKind.Reading when drag.Target is { } target && target != drag.Reading:
                MoveReading(drag.Key, drag.Reading, target);
                break;
            default:
                Redraw(drag.Key);
                break;
        }
    }

    private static double Share(int offset, int room) => room <= 0 ? 0 : Math.Round(Math.Clamp((double)offset / room, 0, 1), 4);

    /// <summary>
    /// Where a panel's picture goes on a screen of a size (relative to its top-left): its box (not its shadow) at its
    /// share of the room the box leaves, so a panel at an edge, filling the screen or in a grid cell lines up exactly
    /// (its shadow going past the edge).
    /// </summary>
    internal static (int X, int Y) Place(OverlayPanel panel, int width, int height, RectangleF box) =>
        ((int)Math.Round(panel.X * Math.Max(0, width - Math.Round(box.Width)) - box.X), (int)Math.Round(panel.Y * Math.Max(0, height - Math.Round(box.Height)) - box.Y));

    private static Point Placed(OverlayPanel panel, OverlayDisplay display, RectangleF box)
    {
        var (x, y) = Place(panel, display.Width, display.Height, box);
        return new Point(display.X + x, display.Y + y);
    }

    /// <summary>A panel dropped where it is now: its share of the room on the screen it is mostly on (another screen moves it there).</summary>
    private void MovePanel((string Screen, string Panel) key, Rectangle bounds)
    {
        if (PanelOf(key) is not { } panel)
        {
            return;
        }

        var centre = new Point(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
        // A panel dragged on the desktop lands on a monitor (a USB screen is not on the desktop).
        var display = _displays.FirstOrDefault(d => d.Kind != "device" && centre.X >= d.X && centre.X < d.X + d.Width && centre.Y >= d.Y && centre.Y < d.Y + d.Height)
            ?? _displays.FirstOrDefault(d => d.Key == key.Screen);
        if (display is null)
        {
            return;
        }

        // Its place by its box (the shadow around it may go past the screen's edge), as it is drawn.
        var box = _hits.TryGetValue(key, out var hits) ? hits.Box : new RectangleF(0, 0, bounds.Width, bounds.Height);
        var moved = panel with
        {
            X = Share(bounds.X + (int)Math.Round(box.X) - display.X, display.Width - (int)Math.Round(box.Width)),
            Y = Share(bounds.Y + (int)Math.Round(box.Y) - display.Y, display.Height - (int)Math.Round(box.Height)),
            Restore = null,
        };
        if (display.Key == key.Screen || _config.PanelsFor(display).Count >= OverlayStyles.MaxPanels || _config.AllScreens.Any(s => s.Key == display.Key && s.EveryPanel().Any(p => p.Id == panel.Id)))
        {
            ChangePanel(key, _ => moved);
            return;
        }

        // Onto another screen: off this one's list, onto that one's.
        var without = _config.WithPanel(key.Screen, key.Panel, _ => null);
        var screens = without.AllScreens.ToList();
        var index = screens.FindIndex(s => s.Key == display.Key);
        if (index >= 0)
        {
            // Onto the list it shows now: its portrait panels while it is upright (when it has them).
            var there = screens[index];
            screens[index] = display.Height > display.Width && there.Portrait is { } tall ? there with { Portrait = [.. tall, moved] } : there with { Panels = [.. there.Panels, moved] };
        }
        else
        {
            screens.Add(new OverlayScreen(display.Key, display.Name, [moved]));
        }

        Commit(without with { Screens = screens });
    }

    private void MoveReading((string Screen, string Panel) key, int from, int to) => ChangePanel(key, p =>
    {
        if (from < 0 || from >= p.Readings.Count || to < 0 || to >= p.Readings.Count)
        {
            return p;
        }

        var readings = p.Readings.ToList();
        var reading = readings[from];
        readings.RemoveAt(from);
        readings.Insert(to, reading);
        return p with { Readings = readings };
    });

    private void ChangeReading((string Screen, string Panel) key, int index, Func<OverlayReading, OverlayReading?> change) => ChangePanel(key, p =>
        index < 0 || index >= p.Readings.Count ? p : p with { Readings = p.Readings.Select((r, i) => i == index ? change(r) : r).OfType<OverlayReading>().ToList() });

    private void ChangePanel((string Screen, string Panel) key, Func<OverlayPanel, OverlayPanel?> change) =>
        Commit(_config.WithPanel(key.Screen, key.Panel, change));

    /// <summary>Uses a layout changed on the screens and says so (it is saved, and the editor shows it).</summary>
    private void Commit(OverlayConfig next)
    {
        if (OverlayStyles.Validate(next) is { } problem)
        {
            LogArrangeRefused(_logger, problem);
            Tick();
            return;
        }

        _config = next;
        Sync();
        Tick();
        Changed?.Invoke(next);
    }

    /// <summary>A panel by its screen and id, in either orientation's list (ids are unique across both).</summary>
    private OverlayPanel? PanelOf((string Screen, string Panel) key) =>
        _config.AllScreens.FirstOrDefault(s => s.Key == key.Screen)?.EveryPanel().FirstOrDefault(p => p.Id == key.Panel);

    private void OnBarPointer(PointerEvent e)
    {
        var over = _barDone.Contains(e.Client);
        if (e.Action == PointerAction.LeftUp && over)
        {
            Arrange(false);
            return;
        }

        if (e.Action == PointerAction.Leave)
        {
            over = false;
        }

        if (over != _barHover)
        {
            _barHover = over;
            DrawBar();
        }
    }

    // ---- Menus -----------------------------------------------------------------------------------------------------

    private void ReadingMenu((string Screen, string Panel) key, int index, Point at)
    {
        if (PanelOf(key) is not { } panel || index < 0 || index >= panel.Readings.Count)
        {
            return;
        }

        var reading = panel.Readings[index];
        var source = _lastSources.GetValueOrDefault(reading.Source);
        var menu = NewMenu(string.IsNullOrWhiteSpace(reading.Label) ? source?.Label ?? reading.Source : reading.Label!);

        var designs = new ToolStripMenuItem("Shown with");
        var visual = OverlayStyles.VisualOf(reading);
        foreach (var (group, items) in DesignGroups)
        {
            if (designs.DropDownItems.Count > 0)
            {
                designs.DropDownItems.Add(new ToolStripSeparator());
            }

            designs.DropDownItems.Add(new ToolStripMenuItem(group) { Enabled = false });
            foreach (var (id, name) in items)
            {
                designs.DropDownItems.Add(Item(name, () => ChangeReading(key, index, r => r with { Visual = id, Bar = false }), visual == id));
            }
        }

        menu.Items.Add(designs);
        // Its value or a percentage: a reading with a total, or a percentage of a value with one (RAM, VRAM).
        var valueSource = source is { Unit: "%" } && OverlayStyles.ValueOf(source.Id) is { } valueId ? _lastSources.GetValueOrDefault(valueId) : null;
        if (source is { Text: null } && ((OverlayStyles.MaxOf(reading, source) is > 0 && source.Unit != "%") || valueSource is { Max: > 0 }))
        {
            var reads = new ToolStripMenuItem("Reads as");
            var show = OverlayStyles.ReadsAs(reading, source);
            foreach (var (id, name) in new[] { ("value", "Its value"), ("percent", "A percentage"), ("both", "Both"), ("of", "Of its total") })
            {
                reads.DropDownItems.Add(Item(name, () => ChangeReading(key, index, r => OverlayStyles.ReadAs(r, source, valueSource, id)), show == id));
            }

            menu.Items.Add(reads);
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Move earlier", () => MoveReading(key, index, index - 1), enabled: index > 0));
        menu.Items.Add(Item("Move later", () => MoveReading(key, index, index + 1), enabled: index < panel.Readings.Count - 1));
        if (panel.Layout == "tiles")
        {
            var columns = Math.Clamp(panel.Columns, 1, 6);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Item("Wider", () => ChangeReading(key, index, r => r with { ColSpan = r.ColSpan + 1 }), enabled: reading.ColSpan < Math.Min(4, columns)));
            menu.Items.Add(Item("Narrower", () => ChangeReading(key, index, r => r with { ColSpan = r.ColSpan - 1 }), enabled: reading.ColSpan > 1));
            menu.Items.Add(Item("Taller", () => ChangeReading(key, index, r => r with { RowSpan = r.RowSpan + 1 }), enabled: reading.RowSpan < 4));
            menu.Items.Add(Item("Shorter", () => ChangeReading(key, index, r => r with { RowSpan = r.RowSpan - 1 }), enabled: reading.RowSpan > 1));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Remove reading", () => ChangeReading(key, index, _ => null)));
        Show(menu, at);
    }

    private void PanelMenu((string Screen, string Panel) key, Point at)
    {
        if (PanelOf(key) is not { } panel)
        {
            return;
        }

        var menu = NewMenu(string.IsNullOrWhiteSpace(panel.Title) ? "Panel" : panel.Title!);
        var layout = new ToolStripMenuItem("Layout");
        foreach (var (id, name) in new[] { ("rows", "One reading per line"), ("tiles", "Tiles"), ("line", "All on one line") })
        {
            layout.DropDownItems.Add(Item(name, () => ChangePanel(key, p => p with { Layout = id }), panel.Layout == id));
        }

        menu.Items.Add(layout);
        var look = new ToolStripMenuItem("Look");
        foreach (var (id, style) in OverlayStyles.Presets)
        {
            look.DropDownItems.Add(Item(PresetName(id), () => ChangePanel(key, p => p with { Style = style }), panel.Style == style));
        }

        menu.Items.Add(look);
        var background = new ToolStripMenuItem("Background");
        foreach (var (name, opacity, tiles, outline) in new[] { ("Text only", 0, 0, true), ("See-through", 35, 6, false), ("Glass", 60, 8, false), ("Solid", 95, 10, false) })
        {
            background.DropDownItems.Add(Item(name, () => ChangePanel(key, p => p with { Style = p.Style with { BackgroundOpacity = opacity, TileOpacity = tiles, Outline = outline || p.Style.Outline } }), panel.Style.BackgroundOpacity == opacity));
        }

        menu.Items.Add(background);
        var values = new ToolStripMenuItem("Values sit");
        foreach (var (name, align) in new[] { ("On the left", "left"), ("In the middle", "center"), ("On the right", "right") })
        {
            values.DropDownItems.Add(Item(name, () => ChangePanel(key, p => p with { Style = p.Style with { ValueAlign = align } }), panel.Style.ValueAlign == align));
        }

        menu.Items.Add(values);
        var size = new ToolStripMenuItem("Size");
        size.DropDownItems.Add(Item("Automatic", () => ChangePanel(key, p => p with { Width = null, Height = null }), panel.Width is null && panel.Height is null));
        if (panel.Width is null && _hits.TryGetValue(key, out var hits) && _displays.FirstOrDefault(d => d.Key == key.Screen) is { } display)
        {
            size.DropDownItems.Add(Item("Keep this size", () => ChangePanel(key, p => p with { Width = Math.Round(hits.Box.Width / display.Scale), Height = Math.Round(hits.Box.Height / display.Scale) })));
        }

        size.DropDownItems.Add(new ToolStripMenuItem("Drag its corner to resize it") { Enabled = false });
        menu.Items.Add(size);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item(panel.Hidden ? "Show panel" : "Hide panel", () => ChangePanel(key, p => p with { Hidden = !p.Hidden })));
        menu.Items.Add(Item("Delete panel…", () =>
        {
            var answer = MessageBox.Show($"Delete the panel “{(string.IsNullOrWhiteSpace(panel.Title) ? "Panel" : panel.Title)}” and its {panel.Readings.Count} readings?", "PC Diagnostic", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer == DialogResult.Yes)
            {
                ChangePanel(key, _ => null);
            }
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Done arranging", () => Arrange(false)));
        Show(menu, at);
    }

    private ContextMenuStrip NewMenu(string title)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(new ToolStripMenuItem(title) { Enabled = false, Font = new Font(SystemFonts.MenuFont ?? Control.DefaultFont, FontStyle.Bold) });
        menu.Items.Add(new ToolStripSeparator());

        // Esc closes the menu while it is open (not arranging); disposed once it has closed (after the chosen item ran).
        menu.Opened += (_, _) =>
        {
            _escape.Unregister();
            _menuOpen = true;
        };
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (_arranging)
            {
                _ = _escape.Register(new HotkeyGesture(0, 0x1B, "Esc"));
            }

            SynchronizationContext.Current?.Post(_ => menu.Dispose(), null);
        };
        return menu;
    }

    /// <summary>
    /// Shows a menu over the panels (they are always on top, the menu and its submenus go above them) with the
    /// keyboard (the click that opened it lets this process take the foreground).
    /// </summary>
    private static void Show(ContextMenuStrip menu, Point at)
    {
        static void Raise(ToolStripDropDown dropDown, bool focus)
        {
            SetWindowPos(dropDown.Handle, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            if (focus)
            {
                SetForegroundWindow(dropDown.Handle);
            }
        }

        void Hook(ToolStripItemCollection items)
        {
            foreach (var item in items.OfType<ToolStripMenuItem>().Where(i => i.HasDropDownItems))
            {
                item.DropDown.Opened += (_, _) => Raise(item.DropDown, focus: false);
                Hook(item.DropDownItems);
            }
        }

        Hook(menu.Items);
        menu.Opened += (_, _) => Raise(menu, focus: true);
        menu.Show(at);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    private static ToolStripMenuItem Item(string text, Action action, bool check = false, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Checked = check, Enabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static string PresetName(string id) => id switch
    {
        "classic" => "Classic OSD",
        _ => char.ToUpperInvariant(id[0]) + id[1..],
    };

    /// <summary>
    /// Puts the panels back on top in one go, in the layout's order (a later panel in front, as the editor shows it),
    /// with the handles, the arranging bar and the screens' numbers above them.
    /// </summary>
    private void RaiseAll()
    {
        if (_menuOpen)
        {
            return;
        }

        LayeredWindow.Stack(FrontToBack());
    }

    /// <summary>Our windows from the front: the screens' numbers, the arranging bar, the handles, then the panels.</summary>
    internal List<LayeredWindow> FrontToBack()
    {
        var order = new List<LayeredWindow>();
        order.AddRange(_identify.Select(i => i.Window));
        if (_bar is not null)
        {
            order.Add(_bar);
        }

        if (_handlesKey is not null && _handles is not null)
        {
            order.Add(_handles);
        }

        // The panels: the last of a screen's list in front; a panel no longer in the layout behind them all.
        var rank = new Dictionary<(string, string), int>();
        foreach (var display in _displays.Where(d => d.Kind != "device"))
        {
            var panels = _config.PanelsFor(display);
            for (var i = 0; i < panels.Count; i++)
            {
                rank.TryAdd((display.Key, panels[i].Id), i);
            }
        }

        // A screen's own characters in front of every panel.
        order.AddRange(_stage?.ScreenWindows() ?? []);
        foreach (var (key, window) in _windows.OrderByDescending(w => rank.GetValueOrDefault(w.Key, -1)).ThenBy(w => w.Key.Item1, StringComparer.Ordinal))
        {
            order.AddRange(SpritesOf(key, front: true));
            order.Add(window);
            order.AddRange(SpritesOf(key, front: false));
        }

        return order;
    }

    private void ClearIdentify()
    {
        foreach (var (window, _) in _identify)
        {
            window.Dispose();
        }

        _identify.Clear();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The on-screen readings hotkey {Hotkey} could not be registered (error {Error}); another program may use it")]
    private static partial void LogHotkeyFailed(ILogger logger, string hotkey, int error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not draw the on-screen readings")]
    private static partial void LogDrawFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "On-screen panel {Panel} on {Screen} is now {Width} x {Height}")]
    private static partial void LogPanelSize(ILogger logger, string panel, string screen, int width, int height);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A change made on the screens was not kept: {Problem}")]
    private static partial void LogArrangeRefused(ILogger logger, string problem);

    /// <summary>A drag in progress: what is dragged, from where, and where it is now.</summary>
    private sealed class Drag(DragKind kind, (string Screen, string Panel) key, Point start, Point origin, SizeF size, int reading)
    {
        public DragKind Kind { get; } = kind;

        public (string Screen, string Panel) Key { get; } = key;

        /// <summary>The mouse on the desktop when it started.</summary>
        public Point Start { get; } = start;

        /// <summary>The window's place when it started.</summary>
        public Point Origin { get; } = origin;

        /// <summary>The panel's size when it started, at 100 % scaling.</summary>
        public SizeF Size { get; } = size;

        /// <summary>The reading pressed (a reading drag).</summary>
        public int Reading { get; } = reading;

        /// <summary>Moved far enough to be a drag (not a click).</summary>
        public bool Moved { get; set; }

        /// <summary>The window's place now.</summary>
        public Point Window { get; set; } = origin;

        /// <summary>Where a dragged reading would go.</summary>
        public int? Target { get; set; }

        /// <summary>The panel at its new size (a resize).</summary>
        public OverlayPanel? Preview { get; set; }
    }
}
