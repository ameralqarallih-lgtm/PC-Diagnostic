using System.Drawing;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Where a character is and what it does at a moment, from its settings, its panel's box and its screen's (the
/// editor's characters/behaviour.ts, the same rules and the same integer hash): staying, pacing or wandering along its
/// place, or running all around its panel. Nothing is remembered between moments.
/// </summary>
internal static class CharacterMotion
{
    /// <summary>The character's anchor (its feet), what it plays, how far into it, and which way it faces.</summary>
    internal readonly record struct Frame(double X, double Y, string Action, double Seconds, int Facing);

    private readonly record struct Leg(double Duration, Func<double, Frame> At);

    /// <summary>FNV-1a of a text (UTF-16 code units).</summary>
    internal static uint SeedOf(string text)
    {
        var h = 2166136261u;
        foreach (var c in text)
        {
            h ^= c;
            h = unchecked(h * 16777619u);
        }

        return h;
    }

    /// <summary>A number in [0, 1) for a seed and a step (xorshift).</summary>
    internal static double Random(uint seed, int k)
    {
        var x = seed ^ unchecked((uint)(k + 1) * 0x9E3779B1u);
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x / 4294967296.0;
    }

    private static double Smooth(double u) => u * u * (3 - (2 * u));

    /// <summary>The line of a character dropped on its screen: across it, at its height there.</summary>
    internal static (double X0, double X1, double Y) SpotLine(OverlayCharacter c, RectangleF screen, double size) =>
        (screen.Left + (size * 0.5), screen.Right - (size * 0.5), screen.Top + (Math.Clamp(c.Y, 0, 1) * screen.Height));

    /// <summary>The line a place stands on: from X0 to X1 at Y (pixels).</summary>
    internal static (double X0, double X1, double Y) LineOf(string place, RectangleF panel, RectangleF screen, double size) => place switch
    {
        "inside" => (panel.Left + (size * 0.4), panel.Right - (size * 0.4), panel.Bottom - (size * 0.06)),
        "left" => (panel.Left - (size * 0.5), panel.Left - (size * 0.5), panel.Bottom),
        "right" => (panel.Right + (size * 0.5), panel.Right + (size * 0.5), panel.Bottom),
        "screen" => (screen.Left + (size * 0.5), screen.Right - (size * 0.5), screen.Bottom),
        _ => (panel.Left + (size * 0.35), panel.Right - (size * 0.35), panel.Top),
    };

    /// <summary>
    /// Where a character is and what it does `t` seconds on (any steady clock), its size in pixels: kept on its screen
    /// (on a panel at the screen's top edge it stands just inside the screen rather than above it, out of sight).
    /// </summary>
    public static Frame FrameOf(OverlayCharacter c, RectangleF panel, RectangleF screen, double size, double t)
    {
        var f = Route(c, panel, screen, size, t);
        return f with
        {
            X = Math.Max(screen.Left + (size * 0.4), Math.Min(screen.Right - (size * 0.4), f.X)),
            Y = Math.Max(screen.Top + size, Math.Min(screen.Bottom, f.Y)),
        };
    }

    private static Frame Route(OverlayCharacter c, RectangleF panel, RectangleF screen, double size, double t)
    {
        ArgumentNullException.ThrowIfNull(c);
        var s = Math.Clamp(c.Speed / 100, 0.25, 3);
        // How fast its animations play (as fast as it moves unless set), and how long it stops.
        var a = c.Tempo is { } tempo ? Math.Clamp(tempo / 100, 0.25, 3) : s;
        var hold = Math.Clamp(c.Pause / 100, 0.25, 4);
        var seed = SeedOf(c.Id);
        bool May(string action) => c.Plays is null || c.Plays.Contains(action, StringComparer.Ordinal);
        var rest = c.Action is "walk" or "run" ? "idle" : c.Action;
        if (!May(rest))
        {
            rest = Resting.FirstOrDefault(May) ?? "idle";
        }

        var gait = c.Action == "run" ? "run" : "walk";
        if (!May(gait))
        {
            gait = gait == "run" ? "walk" : "run";
        }

        var moves = May(gait);
        var hop = May("jump") ? "jump" : gait;
        var v = size * (gait == "run" ? 1.3 : 0.55) * s;
        var pause = (rest == "sleep" ? 6 : 2.6) / Math.Sqrt(s) * hold;

        if (c.Place == "around" && moves)
        {
            double l = panel.Left + (size * 0.35), r = Math.Max(l, panel.Right - (size * 0.35));
            double xr = panel.Right + (size * 0.55), xl = panel.Left - (size * 0.55), top = panel.Top, bottom = panel.Bottom;
            var walk = (r - l) / v;
            var j = 1 / s;
            var p = 1.8 / Math.Sqrt(s) * hold;
            Leg[] around =
            [
                new(walk, u => new Frame(l + (v * u), top, gait, u * a, 1)),
                new(j, u => new Frame(r + ((xr - r) * Smooth(u / j)), top + ((bottom - top) * Smooth(u / j)), hop, u * a, 1)),
                new(p, u => new Frame(xr, bottom, rest, u * a, -1)),
                new(j, u => new Frame(xr + ((r - xr) * Smooth(u / j)), bottom + ((top - bottom) * Smooth(u / j)), hop, u * a, -1)),
                new(walk, u => new Frame(r - (v * u), top, gait, u * a, -1)),
                new(j, u => new Frame(l + ((xl - l) * Smooth(u / j)), top + ((bottom - top) * Smooth(u / j)), hop, u * a, -1)),
                new(p, u => new Frame(xl, bottom, rest, u * a, 1)),
                new(j, u => new Frame(xl + ((l - xl) * Smooth(u / j)), bottom + ((top - bottom) * Smooth(u / j)), hop, u * a, 1)),
            ];
            return Along(around, t + (Random(seed, 0) * 30));
        }

        var (x0, x1, y) = c.Place == "spot" ? SpotLine(c, screen, size) : LineOf(c.Place == "around" ? "top" : c.Place, panel, screen, size);
        if (c.Movement != "stay" && (c.From > 0 || c.To < 1))
        {
            // Kept to its area along the line.
            var whole = Math.Max(0, x1 - x0);
            (x0, x1) = (x0 + (whole * Math.Clamp(c.From, 0, 1)), x0 + (whole * Math.Clamp(c.To, 0, 1)));
        }

        var span = Math.Max(0, x1 - x0);
        if (c.Movement == "stay" || span < size * 0.2 || !moves)
        {
            return new Frame(x0 + (span * Math.Clamp(c.X, 0, 1)), y, moves || c.Action is not ("walk" or "run") ? c.Action : rest, t * a, c.Flip ? -1 : 1);
        }

        if (c.Movement == "pace")
        {
            var walk = span / v;
            Leg[] pace =
            [
                new(walk, u => new Frame(x0 + (v * u), y, gait, u * a, 1)),
                new(pause, u => new Frame(x1, y, rest, u * a, 1)),
                new(walk, u => new Frame(x1 - (v * u), y, gait, u * a, -1)),
                new(pause, u => new Frame(x0, y, rest, u * a, -1)),
            ];
            return Along(pace, t + (Random(seed, 0) * ((2 * walk) + (2 * pause))));
        }

        // Wandering: to six spots in turn (the last back to the first), a pause doing something at each.
        string[] pool = c.Plays is null ? [rest, "idle", "sit", "play", "jump"] : [.. new[] { rest }.Concat(c.Plays.Where(p => Resting.Contains(p))).Distinct(StringComparer.Ordinal)];
        var spots = Enumerable.Range(0, 6).Select(k => x0 + (span * Random(seed, k + 1))).ToArray();
        var legs = new List<Leg>();
        for (var k = 0; k < 6; k++)
        {
            var from = spots[k];
            var to = spots[(k + 1) % 6];
            var dir = to >= from ? 1 : -1;
            var doing = pool[(int)Math.Floor(Random(seed, k + 21) * pool.Length)];
            var wait = (doing == "sleep" ? 6 : 1.5 + (3.5 * Random(seed, k + 11))) / Math.Sqrt(s) * hold;
            legs.Add(new Leg(Math.Abs(to - from) / v, u => new Frame(from + (dir * v * u), y, gait, u * a, dir)));
            legs.Add(new Leg(wait, u => new Frame(to, y, doing, u * a, dir)));
        }

        return Along(legs, t);
    }

    /// <summary>What it may do between walks, in the order one is chosen when its own is not among its animations.</summary>
    private static readonly string[] Resting = ["idle", "sit", "play", "dance", "sleep", "jump"];

    /// <summary>The frame `t` seconds into a route of legs, looping.</summary>
    private static Frame Along(IReadOnlyList<Leg> legs, double t)
    {
        var total = legs.Sum(l => l.Duration);
        var u = total > 0 ? ((t % total) + total) % total : 0;
        for (var i = 0; i < legs.Count; i++)
        {
            if (u < legs[i].Duration || i == legs.Count - 1)
            {
                return legs[i].At(Math.Min(u, legs[i].Duration));
            }

            u -= legs[i].Duration;
        }

        return legs[0].At(0);
    }
}
