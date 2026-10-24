using System.Drawing;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// A character reacting to its reading, moment to moment (as the editor's characters/reactions.ts): its status held
/// until a new one lasts <see cref="StatusHold"/> seconds (a value hovering at a limit does not make it flicker), and
/// its own clock along its route, which runs faster while it runs and stops while it stays to do something, so it
/// carries on from where it was rather than jumping.
/// </summary>
internal sealed partial class CharacterReacting
{
    /// <summary>How long a new status must last before the character reacts to it, in seconds.</summary>
    public const double StatusHold = 2;

    private string? _status;
    private (string Status, double Since)? _pending;
    private double? _clock;
    private double _last;

    /// <summary>The status held now (null before the first).</summary>
    public string? Status => _status;

    /// <summary>Its mind: bubbles, relief, fast-rise warnings, away and back (<see cref="CharacterMind"/>).</summary>
    public CharacterMind Mind { get; } = new();

    /// <summary>The prop it shows at the moment decided last (null: none): its mind's, else its reaction's.</summary>
    public string? Prop { get; private set; }

    /// <summary>Its props as they change: the one shown and the one going.</summary>
    public PropTrack Props { get; } = new();

    /// <summary>Where its feet were last drawn (on a USB screen; the monitors' stage keeps its own).</summary>
    public PointF Feet { get; set; }

    /// <summary>How long the user has not touched the keyboard or mouse, in seconds (null: not known).</summary>
    public static Func<double?>? UserIdle { get; set; } = InputIdleSeconds;

    /// <summary>The time since the last keyboard or mouse input in this session (Windows' GetLastInputInfo).</summary>
    private static double? InputIdleSeconds()
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.Time) / 1000.0 : null;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>The status held at <paramref name="now"/> (seconds), given the status now.</summary>
    public string Hold(double now, string status)
    {
        _status ??= status;
        if (status == _status)
        {
            _pending = null;
        }
        else if (_pending is not { } pending || pending.Status != status)
        {
            _pending = (status, now);
        }
        else if (now - pending.Since >= StatusHold)
        {
            _status = status;
            _pending = null;
        }

        return _status;
    }

    /// <summary>The route's clock at <paramref name="now"/>, having gone at <paramref name="pace"/> since the last moment.</summary>
    public double Advance(double now, double pace)
    {
        _clock = _clock is { } clock ? clock + (Math.Clamp(now - _last, 0, 1) * pace) : now;
        _last = now;
        return _clock.Value;
    }

    /// <summary>
    /// Where a reacting character is and what it does at <paramref name="now"/>: its motion frame along its route at
    /// its own clock, the reaction's action in place of its own (a way of moving while it moves; what it does in place
    /// when it stops), and the effect to show. Without reactions, its plain motion frame.
    /// </summary>
    /// <param name="spots">Where each reading's tile is (by source), in the same pixels as <paramref name="box"/>: a
    /// character goes to the tile of a reading that turns bad (null: it stays on its route).</param>
    /// <param name="senses">What it senses beyond its reading (the mouse, clicks, another character, the weather, the time; null: nothing).</param>
    public static (CharacterMotion.Frame Frame, string? Effect, string? Status, CharacterThought Thought) FrameOf(
        OverlayCharacter c, OverlayPanel panel, IReadOnlyDictionary<string, OverlaySource> sources, CharacterReacting state, RectangleF box, RectangleF screen, double size, double now,
        ReadingSpots? spots = null, CharacterSenses? senses = null)
    {
        var judged = OverlayReactions.ReadingFor(c, panel, sources);
        var status = judged is null ? null : state.Hold(now, judged.Status);
        var thought = state.Mind.Think(now, judged, status, UserIdle?.Invoke(), c.Plays, CharacterEvents.Latest, senses);
        state.Play = null;
        state.Prop = thought.Prop ?? (judged is null ? null : OverlayReactions.PropFor(judged.Kind, status!, c.ReactionsFor(judged.Source.Id))) ?? thought.WeatherProp;
        if (c.Place is "wheel" or "needle" or "bar" or "graph")
        {
            // On its gauge (running in the ring, riding the needle); without one on the panel, on its top edge.
            if (GaugeOf(c, panel, sources, spots) is { } gauge)
            {
                var reaction = judged is null ? ("default", (string?)null) : OverlayReactions.ReactionFor(judged.Kind, status!, c.ReactionsFor(judged.Source.Id));
                var (played, play) = state.OnGauge(c, gauge, status == "critical", thought.Action ?? reaction.Item1, now, (float)size);
                state.Play = play;
                return (played, thought.Effect ?? reaction.Item2 ?? thought.Weather, status, thought);
            }

            c = c with { Place = "top" };
        }
        // Where its mind takes it (the mouse resting near it, a friend), as a tile's middle.
        var toward = thought.Toward is { } x ? new RectangleF((float)x, 0, 0, 0) : (RectangleF?)null;
        if (judged is null && thought.Action is null)
        {
            var own = state.GoTo(CharacterMotion.FrameOf(c, box, screen, size, state.Advance(now, 1)), c, toward, false, "default", null, box, screen, size, now);
            return (Faced(own, thought), thought.Effect ?? thought.Weather, null, thought);
        }

        var (action, effect) = judged is null ? ("default", null) : OverlayReactions.ReactionFor(judged.Kind, status!, c.ReactionsFor(judged.Source.Id));
        // What its mind adds (a relief dance, a jump at a fast rise, asleep while the user is away) comes over the reaction.
        action = thought.Action ?? action;
        effect = thought.Effect ?? effect;
        var pace = OverlayReactions.PaceOf(action);
        // Something done in place (dancing, sitting) is done when it pauses: it keeps its route between (wandering or
        // pacing as chosen); a way of moving (running) changes its pace.
        // Asleep it stays where it is.
        var asleep = action == "sleep";
        var frame = CharacterMotion.FrameOf(c, box, screen, size, state.Advance(now, pace == 0 && !asleep ? 1 : pace));
        if (action != "default")
        {
            if (asleep)
            {
                frame = frame with { Action = action, Seconds = now };
            }
            else if (pace == 0)
            {
                if (frame.Action is not ("walk" or "run"))
                {
                    frame = frame with { Action = action };
                }
            }
            else if (frame.Action is "walk" or "run" || c.Movement == "stay" || c.Place is "left" or "right")
            {
                frame = frame with { Action = action };
            }
        }

        // A reading turned bad: it goes to its tile and reacts there (back to its route when it is good again).
        var bad = judged is not null && status is not null && CharacterMind.IsBad(judged.Kind, status);
        var tile = bad && spots is not null && spots.Tiles.TryGetValue(judged!.Source.Id, out var spot) ? spot : (RectangleF?)null;
        frame = state.GoTo(frame, c, tile ?? toward, status == "critical" && tile is not null, action, thought.Action, box, screen, size, now);
        return (Faced(frame, thought), effect ?? thought.Weather, status, thought);
    }

    /// <summary>The frame facing the way its mind says (the mouse, a friend), unless it is walking or running.</summary>
    private static CharacterMotion.Frame Faced(CharacterMotion.Frame frame, CharacterThought thought) =>
        thought.Facing != 0 && frame.Action is not ("walk" or "run") ? frame with { Facing = thought.Facing } : frame;

    /// <summary>The local time, for the characters' minds (tests set it).</summary>
    public static Func<DateTime> LocalTime { get; set; } = () => DateTime.Now;

    /// <summary>The local time of day (0-24) and day for the characters' minds (good mornings, late nights).</summary>
    public static (double Hour, int Day) Clock()
    {
        var now = LocalTime();
        return (now.TimeOfDay.TotalHours, DateOnly.FromDateTime(now).DayNumber);
    }

    /// <summary>The weather where the user is (null: not known, or not wanted): read from weather.json by the overlay.</summary>
    public static CharacterWeather? Weather { get; set; }

    /// <summary>
    /// What a character of a screen senses now: where it was last drawn, the mouse (null on a USB screen, or when it
    /// does not notice it), its clicks, the nearest other of <paramref name="company"/>, the weather and the time.
    /// </summary>
    internal static CharacterSenses SensesOf(string self, OverlayCharacter c, PointF feet, double size, PointF? cursor, long touches, CharacterCompany? company)
    {
        var (hour, day) = Clock();
        var line = CharacterCompany.IsLine(c.Place);
        return new CharacterSenses(self, feet.X, feet.Y, size, c.Mouse ? cursor?.X : null, c.Mouse ? cursor?.Y : null, touches, company?.FriendOf(self), Weather, hour, day, line);
    }

    /// <summary>Where it was drawn, for the others of its screen.</summary>
    internal CharacterSpot SpotOf(string self, string name, OverlayCharacter c, PointF feet, double size, bool held) =>
        new(self, name, feet.X, feet.Y, size, Mind.Free && !held, Mind.MeetingWith, CharacterCompany.IsLine(c.Place) && Play is null && !held);

    /// <summary>
    /// A character playing on a gauge, as decided for this moment: on which, its kind (wheel or needle), how tall it is
    /// drawn (it fits the gauge) and how far the wheel has turned (degrees).
    /// </summary>
    internal sealed record GaugePlay(string Kind, GaugeHit Gauge, float Size, double Spin);

    /// <summary>What it plays on a gauge at this moment (null: it is not on one).</summary>
    public GaugePlay? Play { get; private set; }

    /// <summary>
    /// Its gauge: the one of its reading; otherwise, of its panel's readings shown with a gauge, in its order, the first
    /// critical one, else the first high one, else the first (as it chooses what to react to); null: none drawn.
    /// </summary>
    internal static GaugeHit? GaugeOf(OverlayCharacter c, OverlayPanel panel, IReadOnlyDictionary<string, OverlaySource> sources, ReadingSpots? spots)
    {
        if (spots is null)
        {
            return null;
        }

        if (c.Gauge is { } source)
        {
            return spots.Gauges.GetValueOrDefault(source) is { } own && Fits(c.Place, own) ? own : null;
        }

        var shown = panel.Readings.Where(r => spots.Gauges.TryGetValue(r.Source, out var g) && Fits(c.Place, g)).ToList();
        if (shown.Count == 0)
        {
            return null;
        }

        var chosen = OverlayReactions.ReadingFor(c with { React = "auto" }, panel with { Readings = shown }, sources)?.Reading.Source ?? Ordered(shown, c).First().Source;
        return spots.Gauges.GetValueOrDefault(chosen);
    }

    /// <summary>Whether a gauge suits a place: a ring, arc or speedometer for a wheel or a needle, a bar for a bar, a line graph for a graph.</summary>
    internal static bool Fits(string place, GaugeHit gauge) => place switch
    {
        "bar" => gauge.Visual == "bar",
        "graph" => gauge.Visual == "graph",
        _ => gauge.Visual is "ring" or "arc" or "speed",
    };

    /// <summary>Its readings in its order, without those it leaves out (all of them when it leaves out every one).</summary>
    private static IEnumerable<OverlayReading> Ordered(IReadOnlyList<OverlayReading> readings, OverlayCharacter c)
    {
        var kept = OverlayReactions.Ordered(readings, c.Priority).Where(r => c.Ignore?.Contains(r.Source, StringComparer.Ordinal) != true).ToList();
        return kept.Count > 0 ? kept : readings;
    }

    private double _wheel;
    private double _wheelAt = double.NaN;
    private double _lastFill = double.NaN;

    /// <summary>
    /// On a gauge at <paramref name="now"/>: in a ring (wheel) it runs in place at its bottom, faster as the gauge fills
    /// (asleep near empty, walking when low), the wheel turning with it; on a needle it sits on the end of the fill,
    /// facing out, doing <paramref name="action"/> when it is not its own way.
    /// </summary>
    /// <param name="size">How tall it stands (on a bar or a graph it keeps its own height).</param>
    internal (CharacterMotion.Frame Frame, GaugePlay Play) OnGauge(OverlayCharacter c, GaugeHit gauge, bool critical, string action, double now, float size = 48)
    {
        var dt = double.IsNaN(_wheelAt) ? 0 : Math.Clamp(now - _wheelAt, 0, 0.1);
        _wheelAt = now;
        var center = gauge.Center;
        var fill = Math.Clamp(gauge.Fraction, 0, 1);
        var own = action is "default" or "walk" or "run" or "idle" ? null : action;
        if (c.Place == "wheel")
        {
            // In its wheel the running is the reading: worried or tired, it keeps running (panic, its moments and the
            // user's own choices still stop it).
            if (own is "worried" or "tired")
            {
                own = null;
            }

            var inner = Math.Max(4, gauge.Radius - (gauge.Thickness / 2));
            var pace = (0.3 + (2.2 * fill)) * c.Speed / 100;
            _wheel += dt * pace;
            var stride = fill < 0.04 ? Pick(c.Plays, "sleep", "sit", "idle") : fill < 0.3 ? Pick(c.Plays, "walk", "run") : Pick(c.Plays, "run", "walk");
            var frame = new CharacterMotion.Frame(center.X, center.Y + (inner * 0.93), own ?? stride ?? "idle", _wheel, 1);
            return (frame, new GaugePlay("wheel", gauge, inner * 1.15f, _wheel * 150));
        }

        if (c.Place == "bar")
        {
            // On the bar, pushing the end of its fill: walking while it grows, as it reacts while it holds or shrinks.
            var growing = !double.IsNaN(_lastFill) && fill - _lastFill > 0.0005;
            _lastFill = fill;
            var pushing = own ?? (growing ? Pick(c.Plays, "walk", "run") : critical ? Pick(c.Plays, "panic", "jump") : null) ?? "idle";
            var at = new CharacterMotion.Frame(gauge.Box.Left + (gauge.Box.Width * fill) - (size * 0.12), gauge.Box.Top, pushing, now, 1);
            return (at, new GaugePlay("bar", gauge, size, 0));
        }

        if (c.Place == "graph")
        {
            // Surfing the line left to right (6 s across at its speed, round again), up and down with it.
            _wheel += dt * c.Speed / 100 / 6;
            var x = gauge.Box.Left + (float)((_wheel % 1) * gauge.Box.Width);
            var surf = new CharacterMotion.Frame(x, gauge.LineAt(x), own ?? Pick(c.Plays, "run", "walk") ?? "idle", now, 1);
            return (surf, new GaugePlay("graph", gauge, size, 0));
        }

        var angle = (gauge.Start + (gauge.Sweep * fill)) * Math.PI / 180;
        var tip = new PointF(center.X + (float)(Math.Cos(angle) * gauge.Radius), center.Y + (float)(Math.Sin(angle) * gauge.Radius));
        var rider = new CharacterMotion.Frame(tip.X, tip.Y, own ?? (critical ? Pick(c.Plays, "jump", "sit") : Pick(c.Plays, "sit", "idle")) ?? "idle", now, Math.Cos(angle) >= 0 ? 1 : -1);
        return (rider, new GaugePlay("needle", gauge, Math.Max(12, gauge.Radius * 0.55f), 0));
    }

    /// <summary>Where it is when it went to a tile (null: on its route), which way it faced, and when it last moved.</summary>
    private double? _atX;
    private int _facing = 1;
    private double _goalAt = double.NaN;

    /// <summary>
    /// The frame on its way to (or at) <paramref name="tile"/>'s middle along its line, running when it is critical, doing
    /// <paramref name="action"/> there; without a tile, back to its route (a moment of its mind, a relief dance, done
    /// where it is first). Only along a line (on top of or inside its panel, its screen's bottom, a spot): a character
    /// beside its panel or hopping around it stays on its route.
    /// </summary>
    internal CharacterMotion.Frame GoTo(CharacterMotion.Frame frame, OverlayCharacter c, RectangleF? tile, bool critical, string action, string? moment, RectangleF box, RectangleF screen, double size, double now)
    {
        var dt = double.IsNaN(_goalAt) ? 0 : Math.Clamp(now - _goalAt, 0, 0.1);
        _goalAt = now;
        if (c.Place is not ("top" or "inside" or "screen" or "spot"))
        {
            _atX = null;
            return frame;
        }

        var line = c.Place is "top" or "inside" ? box : screen;
        double? goal = tile is { } t ? Math.Clamp(t.X + (t.Width / 2), line.Left + (size * 0.25), Math.Max(line.Left + (size * 0.25), line.Right - (size * 0.25))) : null;
        if (_atX is not { } at)
        {
            if (goal is null)
            {
                return frame;
            }

            at = frame.X;
        }

        if (goal is null && moment is not null)
        {
            // Its moment first, where it is.
            _atX = at;
            return frame with { X = at, Action = moment, Facing = _facing, Seconds = now };
        }

        var target = goal ?? frame.X;
        var dx = target - at;
        var speed = size * (critical ? 3 : 1.6) * c.Speed / 100;
        at += Math.Sign(dx) * Math.Min(Math.Abs(dx), speed * dt);
        if (goal is null && Math.Abs(target - at) < 1.5)
        {
            // Back on its route.
            _atX = null;
            return frame;
        }

        _atX = at;
        if (Math.Abs(target - at) >= 1.5)
        {
            _facing = dx < 0 ? -1 : 1;
            var way = critical ? Pick(c.Plays, "run", "walk") : Pick(c.Plays, "walk", "run");
            return frame with { X = at, Action = way ?? frame.Action, Facing = _facing, Seconds = now };
        }

        return frame with { X = at, Action = moment ?? (action is "default" or "walk" or "run" ? "idle" : action), Facing = _facing, Seconds = now };
    }

    private static string? Pick(IReadOnlyList<string>? plays, params string[] wanted) =>
        wanted.FirstOrDefault(a => plays is null || plays.Contains(a, StringComparer.Ordinal));
}
