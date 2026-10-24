using PcDiag.Core.Diagnostics;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// The characters on the monitors, on a thread of their own: each in a small window (click-through, always on top like
/// the panels, kept out of screenshots like them), moved and drawn about 60 times a second on a steady clock. The
/// panels are drawn on the overlay's thread, which tells this one where they are (<see cref="Show"/>); drawing them
/// (every refresh, or a moving background) used to hold the characters up there, so they stopped for a moment and then
/// went on, and the 33 ms timer and a 16 ms clock made their steps uneven. Its own copies of the characters' frames are
/// kept here (a frame is never drawn on two threads at once).
/// </summary>
internal sealed partial class CharacterStage : IDisposable
{
    private const double FrameSeconds = 1 / 60.0;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Held: its "held" clip where it has one, else its jump's highest moment (40 % into it), under the mouse.</summary>
    private static CharacterMotion.Frame HeldFrame(CharacterPlayer player, CharacterMotion.Frame frame, PointF feet, double now)
    {
        var held = player.Pack.Clips.Any(c => c.Action == "held");
        var jump = player.Pack.ClipFor("jump");
        return frame with { X = feet.X, Y = feet.Y, Action = held ? "held" : "jump", Seconds = held ? now : jump.Duration * 0.4 };
    }

    /// <summary>How long it takes to land: its "land" clip, or the last 30 % of its jump.</summary>
    private static double LandSeconds(CharacterPlayer player) =>
        player.Pack.Clips.FirstOrDefault(c => c.Action == "land") is { } land ? land.Duration : player.Pack.ClipFor("jump").Duration * 0.3;

    /// <summary>Landing, <paramref name="into"/> seconds after it was let go: where it was dropped.</summary>
    private static CharacterMotion.Frame LandFrame(CharacterPlayer player, CharacterMotion.Frame frame, PointF feet, double into)
    {
        var land = player.Pack.Clips.Any(c => c.Action == "land");
        var jump = player.Pack.ClipFor("jump");
        return frame with { X = feet.X, Y = feet.Y, Action = land ? "land" : "jump", Seconds = land ? into : (jump.Duration * 0.7) + into };
    }

    /// <summary>The picture turned by <paramref name="degrees"/> about its top middle (where it is held).</summary>
    private static (Bitmap Picture, Point At) Swing(Sprite sprite, Bitmap picture, Point at, float degrees)
    {
        var pad = (int)Math.Ceiling(Math.Max(picture.Width, picture.Height) * 0.25);
        var size = new Size(picture.Width + (2 * pad), picture.Height + (2 * pad));
        if (sprite.Swung is not { } swung || swung.Size != size)
        {
            sprite.Swung?.Dispose();
            swung = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            sprite.Swung = swung;
        }

        using (var g = Graphics.FromImage(swung))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.TranslateTransform(pad + (picture.Width / 2f), pad);
            g.RotateTransform(degrees);
            g.DrawImage(picture, -picture.Width / 2f, 0, picture.Width, picture.Height);
        }

        return (swung, new Point(at.X - pad, at.Y - pad));
    }

    private readonly ILogger _logger;
    private readonly Action _added;
    private readonly Action<Key, PointF> _dropped;
    private readonly Thread _thread;
    private readonly ConcurrentDictionary<Key, (LayeredWindow Window, bool Front)> _windows = new();
    private volatile Scene _scene = new([], new Dictionary<string, OverlaySource>(StringComparer.Ordinal), false, false);
    private volatile bool _stop;

    /// <param name="logger">Where drawing problems go.</param>
    /// <param name="added">Called (on this stage's thread) when windows were added: the overlay puts them in their places among the panels.</param>
    /// <param name="dropped">Called (on this stage's thread) when a character was dragged and dropped: where its feet are, in desktop pixels.</param>
    public CharacterStage(ILogger logger, Action added, Action<Key, PointF> dropped)
    {
        _logger = logger;
        _added = added;
        _dropped = dropped;
        _thread = new Thread(Run) { IsBackground = true, Name = "PC Diagnostic characters" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>A character shown: its screen, panel (or <see cref="OverlayScreen.StageId"/>) and id.</summary>
    internal readonly record struct Key(string Screen, string Panel, string Character);

    /// <summary>
    /// A character to show: its panel (for what it reacts to), its panel's box and its screen's area in desktop pixels,
    /// and how tall it stands in pixels.
    /// </summary>
    /// <param name="Spots">Where each reading's tile is on its monitor (by source), in desktop pixels.</param>
    internal sealed record Part(Key Key, OverlayCharacter Character, OverlayPanel Panel, RectangleF Box, RectangleF Area, float Size, ReadingSpots? Spots = null);

    /// <summary>What to show now: the characters, the readings they react to, whether they move, and whether screenshots leave them out.</summary>
    internal sealed record Scene(IReadOnlyList<Part> Parts, IReadOnlyDictionary<string, OverlaySource> Sources, bool Moving, bool HideFromCapture);

    /// <summary>What to show from now on (any thread).</summary>
    public void Show(Scene scene)
    {
        _scene = scene;
        _wake.Set();
    }

    /// <summary>Wakes the thread asleep with no characters when a scene comes.</summary>
    private readonly AutoResetEvent _wake = new(false);

    /// <summary>The windows of a panel's characters in front of it (true) or behind it; a screen's own with its stage id.</summary>
    public IEnumerable<LayeredWindow> WindowsOf(string screen, string panel, bool front) =>
        _windows.Where(w => w.Key.Screen == screen && w.Key.Panel == panel && w.Value.Front == front).Select(w => w.Value.Window).ToList();

    /// <summary>Every screen's own characters' windows.</summary>
    public IEnumerable<LayeredWindow> ScreenWindows() => _windows.Where(w => w.Key.Panel == OverlayScreen.StageId).Select(w => w.Value.Window).ToList();

    private sealed class Sprite(LayeredWindow window, OverlayCharacter character) : IDisposable
    {
        public LayeredWindow Window { get; } = window;

        public OverlayCharacter Character { get; set; } = character;

        public bool Drawn { get; set; }

        public CharacterReacting Reacting { get; } = new();

        public Bitmap? Composed { get; set; }

        /// <summary>Where its feet are while it is dragged (desktop pixels), and where the mouse took it from them.</summary>
        public PointF? Dragged { get; set; }

        public SizeF Grip { get; set; }

        /// <summary>Where its feet were last drawn.</summary>
        public PointF Feet { get; set; }

        /// <summary>When it was dropped (stage seconds) and where: it lands there.</summary>
        public (double At, PointF Feet)? Landing { get; set; }

        /// <summary>Its picture turned while it swings, held (kept for the next frame).</summary>
        public Bitmap? Swung { get; set; }

        /// <summary>Where the mouse was pressed on it (a click unless it moves on to drag it).</summary>
        public PointF? Pressed { get; set; }

        /// <summary>How many times it has been clicked.</summary>
        public long Touches { get; set; }

        public void Dispose()
        {
            Window.Dispose();
            Composed?.Dispose();
            Swung?.Dispose();
        }
    }

    private void Run()
    {
        // Desktop pixels as the panels' thread has them; a problem in a window's message is logged, not a dialog.
        PcDiag.Platform.Windows.Display.MonitorLayout.UsePhysicalPixelsOnThisThread();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, threadScope: true);
        // Windows' timer at 1 ms (smooth 60 frames a second) only while characters move: it costs the whole PC otherwise.
        var fine = false;
        var sprites = new Dictionary<Key, Sprite>();
        var players = new Dictionary<string, CharacterPlayer?>(StringComparer.Ordinal);
        CharacterPlayer? effects = null;
        CharacterPlayer? props = null;
        var effectsRead = false;
        double? stillAt = null;
        var generation = CharacterPlayer.Generation;
        // Each screen's characters as last drawn (so two can meet), and when the mouse was last checked for being free.
        var companies = new Dictionary<string, CharacterCompany>(StringComparer.Ordinal);
        var mouseFree = (At: double.NegativeInfinity, Free: false);
        try
        {
            while (!_stop)
            {
                var pass = CostMeter.Measure("characters");
                var started = _clock.Elapsed.TotalSeconds;
                var scene = _scene;
                var wanted = scene.Parts.ToDictionary(p => p.Key);
                foreach (var key in sprites.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
                {
                    sprites[key].Dispose();
                    sprites.Remove(key);
                    _windows.TryRemove(key, out _);
                }

                var added = false;
                foreach (var (key, part) in wanted)
                {
                    if (sprites.TryGetValue(key, out var sprite))
                    {
                        if (sprite.Character != part.Character)
                        {
                            sprite.Character = part.Character;
                            sprite.Drawn = false;
                        }
                    }
                    else
                    {
                        sprite = new Sprite(new LayeredWindow(scene.HideFromCapture), part.Character);
                        var held = sprite;
                        var at = key;
                        held.Window.Pointer += e => Drag(held, at, e);
                        held.Window.CursorAt = _ => held.Character.Draggable ? Cursors.SizeAll : Cursors.Hand;
                        sprites[key] = sprite;
                        added = true;
                    }

                    // One that can be dragged or clicked takes the mouse where it is drawn (its see-through pixels let clicks by).
                    sprite.Window.Interactive = part.Character.Draggable || part.Character.Clickable;

                    _windows[key] = (sprite.Window, part.Character.Place == "inside" || !part.Character.Behind);
                }

                // Still (the panels hidden by motion's rules): drawn once where they are.
                var seconds = scene.Moving ? started : stillAt ??= started;
                if (scene.Moving)
                {
                    stillAt = null;
                }

                if (!effectsRead)
                {
                    effects = CharacterPlayer.ReadEffects(OverlayReactions.EffectsPack.Bytes);
                    props = CharacterPlayer.ReadProps(OverlayReactions.PropsPack.Bytes);
                    effectsRead = true;
                }

                // The characters read again (new animations made): its own copies made again from them.
                if (generation != CharacterPlayer.Generation)
                {
                    generation = CharacterPlayer.Generation;
                    foreach (var player in players.Values)
                    {
                        player?.Dispose();
                    }

                    players.Clear();
                }

                // The mouse, when it is free (shown, not held by a game, nothing full screen in front: checked 4 times a second).
                if (started - mouseFree.At >= 0.25)
                {
                    mouseFree = (started, OverlayHost.HandlesAllowed());
                }

                PointF? cursor = mouseFree.Free && GetCursorPos(out var point) ? new PointF(point.X, point.Y) : null;
                foreach (var screen in wanted.Keys.Select(k => k.Screen).Distinct(StringComparer.Ordinal))
                {
                    companies.TryAdd(screen, new CharacterCompany());
                }

                foreach (var (screen, company) in companies)
                {
                    company.Keep(wanted.Keys.Where(k => k.Screen == screen).Select(SelfOf).ToHashSet(StringComparer.Ordinal));
                }

                foreach (var (key, part) in wanted)
                {
                    var sprite = sprites[key];
                    if (!scene.Moving && sprite.Drawn && sprite.Dragged is null)
                    {
                        continue;
                    }

                    if (!players.TryGetValue(part.Character.Character, out var player))
                    {
                        // Its own copy: the overlay's thread draws the same character on USB screens.
                        player = CharacterPlayer.Find?.Invoke(part.Character.Character)?.Copy();
                        players[part.Character.Character] = player;
                    }

                    if (player is null)
                    {
                        continue;
                    }

                    try
                    {
                        var company = companies[key.Screen];
                        var senses = CharacterReacting.SensesOf(SelfOf(key), part.Character, sprite.Feet, sprite.Reacting.Play?.Size ?? part.Size, cursor, sprite.Touches, company);
                        var (frame, effect, _, thought) = CharacterReacting.FrameOf(part.Character, part.Panel, scene.Sources, sprite.Reacting, part.Box, part.Area, part.Size, seconds, part.Spots, senses);
                        var swing = 0f;
                        if (sprite.Dragged is { } held)
                        {
                            // Held: its own "held" clip, or up in the air (its jump at its highest), swinging under the mouse.
                            frame = HeldFrame(player, frame, held, started);
                            swing = (float)(Math.Sin(started * 5) * 9);
                            effect = null;
                            thought = CharacterThought.None;
                        }
                        else if (sprite.Landing is { } landing && started - landing.At < LandSeconds(player))
                        {
                            // Dropped: it lands where it was let go (its own "land" clip, or the end of its jump).
                            frame = LandFrame(player, frame, landing.Feet, started - landing.At);
                            effect = null;
                        }
                        else
                        {
                            sprite.Landing = null;
                        }

                        sprite.Feet = sprite.Dragged ?? new PointF((float)frame.X, (float)frame.Y);
                        company.Set(sprite.Reacting.SpotOf(SelfOf(key), player.Pack.Name, part.Character, sprite.Feet, sprite.Reacting.Play?.Size ?? part.Size, sprite.Dragged is not null));
                        // Its props (none while it is held: what it had goes).
                        var prop = sprite.Reacting.Props.At(started, sprite.Dragged is null ? sprite.Reacting.Prop : null);
                        if (OverlayHost.Picture(player, effects, frame, effect, part.Size, sprite.Composed, thought, started, sprite.Dragged is null ? sprite.Reacting.Play : null, props, prop) is var (picture, at, composed))
                        {
                            sprite.Composed = composed ?? sprite.Composed;
                            if (swing != 0)
                            {
                                (picture, at) = Swing(sprite, picture, at, swing);
                            }

                            sprite.Window.Show(picture, at, (byte)Math.Clamp(part.Character.Opacity * 255 / 100, 0, 255));
                            sprite.Drawn = true;
                        }
                    }
                    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ExternalException)
                    {
                        LogDrawFailed(_logger, ex);
                    }
                }

                if (added)
                {
                    _added();
                }

                // Its windows' messages, then the rest of the frame's time.
                Application.DoEvents();
                pass.Dispose();
                var fast = wanted.Count > 0 && (scene.Moving || sprites.Values.Any(x => x.Dragged is not null || x.Landing is not null));
                if (fast != fine)
                {
                    _ = fast ? TimeBeginPeriod(1) : TimeEndPeriod(1);
                    fine = fast;
                }

                if (wanted.Count == 0 && sprites.Count == 0)
                {
                    // No characters: asleep until a scene with some comes (or a second goes by).
                    _wake.WaitOne(TimeSpan.FromSeconds(1));
                }
                else
                {
                    var rest = (started + (fast ? FrameSeconds : 0.1)) - _clock.Elapsed.TotalSeconds;
                    if (rest > 0)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(rest));
                    }
                }

                // Characters of one no longer used let go.
                foreach (var id in players.Keys.Where(id => sprites.Values.All(s => s.Character.Character != id)).ToList())
                {
                    players[id]?.Dispose();
                    players.Remove(id);
                }
            }
        }
        finally
        {
            foreach (var sprite in sprites.Values)
            {
                sprite.Dispose();
            }

            foreach (var player in players.Values)
            {
                player?.Dispose();
            }

            effects?.Dispose();
            props?.Dispose();
            _windows.Clear();
            if (fine)
            {
                _ = TimeEndPeriod(1);
            }
        }
    }

    /// <summary>Who a character is among those of its screen (its mind's <see cref="CharacterSenses.Self"/>).</summary>
    private static string SelfOf(Key key) => key.Panel + "/" + key.Character;

    /// <summary>
    /// A character's window's mouse: pressed and moved a few pixels takes it (when it can be dragged), moving carries it,
    /// let go drops it; pressed and let go where it was is a click.
    /// </summary>
    private void Drag(Sprite sprite, Key key, PointerEvent e)
    {
        switch (e.Action)
        {
            case PointerAction.LeftDown:
                sprite.Pressed = e.Screen;
                sprite.Grip = new SizeF(e.Screen.X - sprite.Feet.X, e.Screen.Y - sprite.Feet.Y);
                sprite.Window.Capture(true);
                break;
            case PointerAction.Move when sprite.Dragged is null && sprite.Pressed is { } from && sprite.Character.Draggable && Math.Abs(e.Screen.X - from.X) + Math.Abs(e.Screen.Y - from.Y) >= 4:
                sprite.Dragged = new PointF(e.Screen.X - sprite.Grip.Width, e.Screen.Y - sprite.Grip.Height);
                break;
            case PointerAction.Move when sprite.Dragged is not null:
                sprite.Dragged = new PointF(e.Screen.X - sprite.Grip.Width, e.Screen.Y - sprite.Grip.Height);
                break;
            case PointerAction.LeftUp when sprite.Dragged is { } at:
                sprite.Window.Capture(false);
                sprite.Pressed = null;
                sprite.Dragged = null;
                sprite.Feet = at;
                sprite.Landing = (_clock.Elapsed.TotalSeconds, at);
                _dropped(key, at);
                break;
            case PointerAction.LeftUp when sprite.Pressed is not null:
                sprite.Window.Capture(false);
                sprite.Pressed = null;
                sprite.Touches++;
                break;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A character could not be drawn")]
    private static partial void LogDrawFailed(ILogger logger, Exception exception);
}
