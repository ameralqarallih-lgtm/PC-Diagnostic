using System.Drawing;
using System.Windows.Forms;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// The panels' animated characters on the monitors: each in a small window of its own (click-through, always on top
/// like the panels, kept out of screenshots like them), moved and drawn again about 30 times a second where its motion
/// rules put it around its panel (<see cref="CharacterMotion"/>), in front of its panel or behind it. They keep still
/// when motion does (the panels hidden, a full-screen game in front unless asked otherwise, the PC locked, …) and are
/// not shown while arranging. On a USB screen they are drawn into its pictures instead (<see cref="DrawCharacters"/>).
/// Each plays its character's frames (<see cref="CharacterPlayer"/>): a shipped one, or one the user imported. A
/// character that reacts to a reading does what its status says, with the status's effect drawn with it
/// (<see cref="CharacterReacting"/>).
/// </summary>
internal sealed partial class OverlayHost
{
    private static readonly Lazy<CharacterPlayer?> EffectFrames = new(() => CharacterPlayer.ReadEffects(OverlayReactions.EffectsPack.Bytes));
    private static readonly Lazy<CharacterPlayer?> PropFrames = new(() => CharacterPlayer.ReadProps(OverlayReactions.PropsPack.Bytes));

    private readonly Dictionary<(string Screen, string Panel, string Character), CharacterReacting> _deviceReacting = [];

    // Each USB screen's characters as last drawn, so two can meet.
    private readonly Dictionary<string, CharacterCompany> _deviceCompanies = new(StringComparer.Ordinal);
    private CharacterStage? _stage;
    private System.Windows.Forms.Timer? _spriteTimer;
    private double _stillAt = 1.2;

    /// <summary>A character's frames by its id; null when it is not here (an imported one removed, say).</summary>
    private static CharacterPlayer? PlayerOf(string id) => CharacterPlayer.Find?.Invoke(id);

    private static bool HasCharacters(OverlayPanel panel) => panel.Characters is { Count: > 0 };

    /// <summary>
    /// Tells the characters' thread what to show: each shown panel's characters around its box, and each monitor's own
    /// on its working area (called as the panels change, and ten times a second for where they are and the readings).
    /// </summary>
    private void SyncCharacters()
    {
        var parts = new List<CharacterStage.Part>();
        if (!_arranging && Visible && _config.Characters)
        {
            foreach (var display in _displays.Where(d => d.Kind != "device"))
            {
                var area = (RectangleF)Screen.FromRectangle(new Rectangle(display.X, display.Y, display.Width, display.Height)).WorkingArea;
                // Where each reading's tile is on this monitor, in desktop pixels (characters go to the one that turns bad).
                var spots = new ReadingSpots();
                foreach (var (key, window) in _windows.Where(w => w.Key.Screen == display.Key))
                {
                    if (PanelOf(key) is { } shown && _hits.TryGetValue(key, out var at))
                    {
                        spots.Add(shown, at, window.Bounds.X, window.Bounds.Y);
                    }
                }

                if (_config.StageFor(display) is { } stage)
                {
                    parts.AddRange(stage.Characters!.Where(c => PlayerOf(c.Character) is not null)
                        .Select(c => new CharacterStage.Part(new(display.Key, OverlayScreen.StageId, c.Id), c, stage, area, area, (float)(c.Size * display.Scale), spots)));
                }

                foreach (var (key, window) in _windows.Where(w => w.Key.Screen == display.Key))
                {
                    if (PanelOf(key) is not { } panel || !HasCharacters(panel) || !_hits.TryGetValue(key, out var hits))
                    {
                        continue;
                    }

                    var box = new RectangleF(window.Bounds.X + hits.Box.X, window.Bounds.Y + hits.Box.Y, hits.Box.Width, hits.Box.Height);
                    parts.AddRange(panel.Characters!.Where(c => PlayerOf(c.Character) is not null)
                        .Select(c => new CharacterStage.Part(new(key.Screen, key.Panel, c.Id), c, panel, box, area, (float)(c.Size * display.Scale), spots)));
                }
            }
        }

        if (parts.Count > 0 && _stage is null)
        {
            var ui = SynchronizationContext.Current;
            // New characters' windows are put in their places among the panels', on this thread.
            _stage = new CharacterStage(_logger, () => ui?.Post(_ => RaiseAll(), null), (key, feet) => ui?.Post(_ => Dropped(key, feet), null));
        }

        _stage?.Show(new CharacterStage.Scene(parts, _lastSources, MotionAllowed(), _config.HideFromCapture));
        if (parts.Count > 0 && _spriteTimer is null)
        {
            _spriteTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _spriteTimer.Tick += (_, _) => SyncCharacters();
            _spriteTimer.Start();
        }
        else if (parts.Count == 0 && _spriteTimer is not null)
        {
            _spriteTimer.Dispose();
            _spriteTimer = null;
        }
    }

    /// <summary>
    /// A character dragged on the screen and dropped: put where along its line it was dropped (a screen's own one at
    /// the top or the bottom, by which half), and the layout saved as when a panel is moved on the screen.
    /// </summary>
    private void Dropped(CharacterStage.Key key, PointF feet)
    {
        if (_displays.FirstOrDefault(d => d.Key == key.Screen) is not { } display)
        {
            return;
        }

        // Where it was let go on its screen: across it and down it (it plays there from now on: place "spot").
        var area = (RectangleF)Screen.FromRectangle(new Rectangle(display.X, display.Y, display.Width, display.Height)).WorkingArea;
        OverlayCharacter Moved(OverlayCharacter c)
        {
            var size = c.Size * display.Scale;
            var (x0, x1, _) = CharacterMotion.SpotLine(c, area, size);
            return c.DroppedAt(x1 > x0 ? (feet.X - x0) / (x1 - x0) : 0.5, area.Height > 0 ? (feet.Y - area.Top) / area.Height : 1);
        }

        if (key.Panel == OverlayScreen.StageId)
        {
            Commit(_config with { Screens = [.. _config.AllScreens.Select(s => s.Key == key.Screen ? s with { Characters = [.. (s.Characters ?? []).Select(c => c.Id == key.Character ? Moved(c) : c)] } : s)] });
            return;
        }

        ChangePanel((key.Screen, key.Panel), p => p with { Characters = [.. (p.Characters ?? []).Select(c => c.Id == key.Character ? Moved(c) : c)] });
    }

    private void StopSprites()
    {
        _spriteTimer?.Dispose();
        _spriteTimer = null;
        _stage?.Dispose();
        _stage = null;
        _deviceCharacterTimer?.Dispose();
        _deviceCharacterTimer = null;
        foreach (var (picture, _) in _deviceBase.Values)
        {
            picture.Dispose();
        }

        _deviceBase.Clear();
        _deviceFrame?.Dispose();
        _deviceFrame = null;
    }

    /// <summary>
    /// A character's picture for a moment and where its top left goes: its frame alone, or its frame and its effect
    /// drawn together (into <paramref name="reuse"/> when it is the size needed; the new one is returned to keep), with
    /// its props (<paramref name="prop"/>: the one shown and the one going) from <paramref name="props"/>.
    /// </summary>
    internal static (Bitmap Picture, Point At, Bitmap? Composed)? Picture(CharacterPlayer player, CharacterPlayer? effects, CharacterMotion.Frame frame, string? effect, float size, Bitmap? reuse, CharacterThought? thought = null, double now = 0, CharacterReacting.GaugePlay? play = null,
        CharacterPlayer? props = null, (string? Id, double Age, string? Going, double GoingAge, double Gone) prop = default)
    {
        // On a gauge it is as tall as the gauge lets it be.
        size = play?.Size ?? size;
        // Shaking (what it reacts to is critical): side to side, its effect with it.
        frame = frame with { X = frame.X + CharacterExtras.ShakeAt(thought, size, now) };
        if (player.Frame(frame.Action, frame.Seconds, frame.Facing, size) is not var (bitmap, anchor))
        {
            return null;
        }

        var feet = new PointF((float)frame.X, (float)frame.Y);
        // Breathing while it stands, sits or sleeps: a little taller and shorter, its feet where they are.
        var breath = (float)CharacterParticles.BreathAt(frame.Action, now);
        var tall = (int)Math.Round(bitmap.Height * breath);
        var body = new Rectangle((int)Math.Round(feet.X - anchor.X), (int)Math.Round(feet.Y - (anchor.Y * breath)), bitmap.Width, tall);
        var particles = CharacterExtras.ParticlesOf(thought, frame, size, now);
        var fxAt = effect is null ? null : EffectAt(player, effects, frame, effect, size);
        var shadow = Rectangle.Ceiling(CharacterExtras.ShadowAt(feet, size));
        var head = player.HeadFromFeet(size, frame.Facing);
        var bubble = CharacterExtras.BubbleAt(thought, new PointF(feet.X + head.X, feet.Y + head.Y), size);
        var union = Rectangle.Union(body, shadow);
        // Its props: on the ground they are left while it walks or runs (or plays on a gauge).
        var shown = CharacterExtras.PropsOf(player, props, frame, feet, size, now, prop, frame.Action is "walk" or "run" || play is not null);
        foreach (var p in shown)
        {
            union = Rectangle.Union(union, Rectangle.Ceiling(RectangleF.Inflate(CharacterExtras.PropBox(p), 1, 1)));
        }

        var sparks = CharacterExtras.ParticlesBox(particles, feet);
        if (!sparks.IsEmpty)
        {
            union = Rectangle.Union(union, Rectangle.Ceiling(sparks));
        }

        var wheel = CharacterExtras.WheelBox(play);
        if (!wheel.IsEmpty)
        {
            union = Rectangle.Union(union, Rectangle.Ceiling(RectangleF.Inflate(wheel, 2, 2)));
        }
        if (fxAt is var (_, fxBox))
        {
            union = Rectangle.Union(union, fxBox);
        }

        if (bubble is { } bubbleBox)
        {
            union = Rectangle.Union(union, Rectangle.Ceiling(RectangleF.Inflate(bubbleBox, 2, 2)));
        }

        var composed = reuse is { } r && r.Width == union.Width && r.Height == union.Height ? r : new Bitmap(union.Width, union.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(composed))
        {
            g.Clear(Color.Transparent);
            g.TranslateTransform(-union.X, -union.Y);
            CharacterExtras.DrawWheel(g, play, 1);
            if (play?.Kind != "needle")
            {
                CharacterExtras.DrawShadow(g, feet, size, 1);
            }

            foreach (var p in shown)
            {
                CharacterExtras.DrawProp(g, p, 1, behind: true);
            }

            g.DrawImage(bitmap, body.X, body.Y, body.Width, body.Height);
            foreach (var p in shown)
            {
                CharacterExtras.DrawProp(g, p, 1, behind: false);
            }

            CharacterExtras.DrawParticles(g, particles, feet, 1);
            if (fxAt is var (fx, at))
            {
                g.DrawImage(fx, at.X, at.Y, fx.Width, fx.Height);
            }

            if (bubble is { } box && thought is not null)
            {
                CharacterExtras.DrawBubble(g, thought, box, size, 1);
            }
        }

        if (!ReferenceEquals(composed, reuse))
        {
            reuse?.Dispose();
        }

        return (composed, union.Location, composed);
    }

    /// <summary>An effect's frame and where it goes for a character's frame: at the top of its head or at its feet.</summary>
    private static (Bitmap Bitmap, Rectangle At)? EffectAt(CharacterPlayer player, CharacterPlayer? effects, CharacterMotion.Frame frame, string effect, float size)
    {
        if (effects?.Effect(effect, frame.Seconds, frame.Facing, size) is not var (fx, fxAnchor, attached))
        {
            return null;
        }

        var point = new PointF((float)frame.X, (float)frame.Y);
        if (attached == "head")
        {
            var head = player.HeadFromFeet(size, frame.Facing);
            point = new PointF(point.X + head.X, point.Y + head.Y);
        }

        return (fx, new Rectangle((int)Math.Round(point.X - fxAnchor.X), (int)Math.Round(point.Y - fxAnchor.Y), fx.Width, fx.Height));
    }

    /// <summary>The characters' windows of a panel in front of it (true) or behind it.</summary>
    private IEnumerable<LayeredWindow> SpritesOf((string Screen, string Panel) key, bool front) => _stage?.WindowsOf(key.Screen, key.Panel, front) ?? [];

    /// <summary>
    /// A USB screen's panel's characters, drawn into its picture around the panel's box: reacting to the readings in
    /// <paramref name="sources"/> with the state <paramref name="reacting"/> keeps for each (none: as they move alone).
    /// </summary>
    /// <param name="company">Its screen's characters as last drawn (null: none meet).</param>
    private static void DrawCharacters(Graphics g, OverlayPanel panel, RectangleF box, Size screen, double seconds, IReadOnlyDictionary<string, OverlaySource>? sources, Func<string, CharacterReacting>? reacting, ReadingSpots? spots = null, CharacterCompany? company = null)
    {
        foreach (var c in panel.Characters ?? [])
        {
            if (PlayerOf(c.Character) is not { } player)
            {
                continue;
            }

            var size = (float)c.Size;
            var area = new RectangleF(0, 0, screen.Width, screen.Height);
            var state = sources is not null && reacting is not null ? reacting(c.Id) : null;
            // What it senses (no mouse on a USB screen): another character, the weather, the time.
            var self = panel.Id + "/" + c.Id;
            var senses = state is null ? null : CharacterReacting.SensesOf(self, c, state.Feet, state.Play?.Size ?? size, null, 0, company);
            var (frame, effect, thought) = state is not null
                ? CharacterReacting.FrameOf(c, panel, sources!, state, box, area, size, seconds, spots, senses) is var (f, e, _, t) ? (f, e, t) : default
                : (CharacterMotion.FrameOf(c, box, area, size, seconds), null, null);
            if (state is not null)
            {
                state.Feet = new PointF((float)frame.X, (float)frame.Y);
                company?.Set(state.SpotOf(self, player.Pack.Name, c, state.Feet, state.Play?.Size ?? size, held: false));
            }

            var play = state?.Play;
            size = play?.Size ?? size;
            var opacity = Math.Clamp(c.Opacity, 0, 100) / 100f;
            frame = frame with { X = frame.X + CharacterExtras.ShakeAt(thought, size, seconds) };
            var feet = new PointF((float)frame.X, (float)frame.Y);
            CharacterExtras.DrawWheel(g, play, opacity);
            if (play?.Kind != "needle")
            {
                CharacterExtras.DrawShadow(g, feet, size, opacity);
            }

            var shown = state is null ? [] : CharacterExtras.PropsOf(player, PropFrames.Value, frame, feet, size, seconds, state.Props.At(seconds, state.Prop), frame.Action is "walk" or "run" || play is not null);
            foreach (var p in shown)
            {
                CharacterExtras.DrawProp(g, p, opacity, behind: true);
            }

            player.Draw(g, frame.Action, frame.Seconds, frame.Facing, size, feet, opacity, (float)CharacterParticles.BreathAt(frame.Action, seconds));
            foreach (var p in shown)
            {
                CharacterExtras.DrawProp(g, p, opacity, behind: false);
            }

            CharacterExtras.DrawParticles(g, CharacterExtras.ParticlesOf(thought, frame, size, seconds), feet, opacity);
            if (effect is not null && EffectAt(player, EffectFrames.Value, frame, effect, size) is var (fx, at))
            {
                using var faded = new System.Drawing.Imaging.ImageAttributes();
                faded.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity });
                g.DrawImage(fx, at, 0, 0, fx.Width, fx.Height, GraphicsUnit.Pixel, faded);
            }

            // Its bubble over its head, kept on the screen.
            var head = player.HeadFromFeet(size, frame.Facing);
            if (thought is not null && CharacterExtras.BubbleAt(thought, new PointF(feet.X + head.X, feet.Y + head.Y), size) is { } bubble)
            {
                bubble.X = Math.Clamp(bubble.X, 2, Math.Max(2, screen.Width - bubble.Width - 2));
                bubble.Y = Math.Max(2, bubble.Y);
                CharacterExtras.DrawBubble(g, thought, bubble, size, opacity);
            }
        }
    }

    /// <summary>A USB screen's character's reacting state (kept while it is shown).</summary>
    private CharacterReacting DeviceReacting(string screen, string panel, string character)
    {
        if (!_deviceReacting.TryGetValue((screen, panel, character), out var state))
        {
            state = new CharacterReacting();
            _deviceReacting[(screen, panel, character)] = state;
        }

        return state;
    }
}
