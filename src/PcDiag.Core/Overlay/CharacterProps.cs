namespace PcDiag.Core.Overlay;

/// <summary>
/// Where a prop is drawn at a moment: its point (where its bottom's middle goes, from the character's feet, in pixels),
/// how big against its size, its turn about that point (radians) and its opacity.
/// </summary>
public readonly record struct PropPose(float X, float Y, float Scale, float Angle, float Alpha);

/// <summary>
/// A character's props drawn in code (as the editor's characters/props.ts, the same numbers, checked by
/// prop-parity.json): where each goes (on its head, over it, beside it on the ground the way it faces, just before it),
/// how it arrives (popping up on the character, dropping in and bouncing on the ground), how it moves (bobbing,
/// swaying, wobbling) and what it gives off (drips, steam, wind, sparkles, a flashing glow). Each is a function of time
/// alone, so the recorder and the editor draw the same.
/// </summary>
public static class CharacterProps
{
    /// <summary>How long a prop takes to arrive, in seconds.</summary>
    public const double ArriveSeconds = 0.45;

    /// <summary>How long a prop takes to go, in seconds.</summary>
    public const double LeaveSeconds = 0.3;

    /// <summary>Whether a prop stands on the ground (beside it or before it): left behind while it walks or runs.</summary>
    public static bool OnGround(PropInfo prop) => prop.At is "beside" or "front";

    /// <summary>
    /// A prop's pose <paramref name="age"/> seconds after it came (<paramref name="gone"/>: seconds since it went, or
    /// negative while it stays), at <paramref name="now"/>, with a character <paramref name="size"/> pixels tall facing a
    /// way, the top of its head at (<paramref name="headX"/>, <paramref name="headY"/>) from its feet and its body
    /// reaching <paramref name="reach"/> pixels from its feet the way it faces; on the ground it is not shown while the
    /// character is <paramref name="moving"/>.
    /// </summary>
    public static PropPose PoseAt(PropInfo prop, double age, double gone, double now, int facing, float size, float headX, float headY, float reach, bool moving)
    {
        ArgumentNullException.ThrowIfNull(prop);
        var h = prop.Size * size;
        var (x, y) = prop.At switch
        {
            "head" => (headX, headY + (0.05 * size)),
            "above" => (headX + (facing * 0.06 * size), headY - (0.04 * size)),
            // Held before it, at its front edge halfway up.
            "hand" => (facing * reach * 0.75, -0.3 * size),
            // Clear of its body, a little way off; just before it, its front edge.
            "beside" => (facing * (reach + (0.04 * size) + (prop.Width * size / 2)), 0.0),
            _ => (facing * reach * 0.85, 0.0),
        };
        // Held up by its grip: that far up its height is at the point.
        y += prop.Grip * h;

        var t = Math.Clamp(age / ArriveSeconds, 0, 1);
        double scale = 1, alpha;
        if (OnGround(prop))
        {
            // Dropped in from above, bouncing as it lands.
            y -= (1 - Bounce(t)) * size * 0.6;
            alpha = Math.Clamp(age / 0.12, 0, 1);
        }
        else
        {
            // Popping up a little past its size, then settling.
            scale = Back(t);
            alpha = Math.Clamp(age / 0.15, 0, 1);
        }

        double angle = 0;
        switch (prop.Motion)
        {
            case "bob":
                y += Math.Sin(2 * Math.PI * now / 1.6) * 0.015 * size;
                break;
            case "sway":
                angle = Math.Sin(2 * Math.PI * now / 2.2) * 0.08;
                break;
            case "wobble":
                angle = Math.Sin(2 * Math.PI * now / 0.3) * 0.03;
                break;
        }

        if (gone >= 0)
        {
            var out_ = Math.Clamp(gone / LeaveSeconds, 0, 1);
            alpha *= 1 - out_;
            scale *= 1 - (0.2 * out_);
        }

        if (moving && OnGround(prop))
        {
            alpha = 0;
        }

        return new PropPose((float)x, (float)y, (float)scale, (float)(angle * (prop.Mirror ? facing : 1)), (float)alpha);
    }

    /// <summary>
    /// What a prop gives off at <paramref name="now"/>, from its point (its bottom's middle, in pixels), with a character
    /// <paramref name="size"/> pixels tall facing a way: drips falling from an ice pack, steam rising from a cup, wind
    /// blowing from a fan back at the character, sparkles twinkling about a trophy, a siren's glow flashing red and blue.
    /// </summary>
    public static IReadOnlyList<CharacterParticle> EmitAt(PropInfo prop, double now, int facing, float size)
    {
        ArgumentNullException.ThrowIfNull(prop);
        var h = prop.Size * size;
        var list = new List<CharacterParticle>(6);
        switch (prop.Emit)
        {
            case "drip":
                for (var k = 0; k < 3; k++)
                {
                    var a = Fraction((now / 1.0) + (k / 3.0));
                    list.Add(new CharacterParticle((float)((k - 1) * 0.08 * size), (float)((-0.02 * size) + (0.5 * size * 0.9 * a * a)), size * 0.022f, 0xFF7DD3FC, (float)(0.9 * (1 - a)), 0));
                }

                break;
            case "steam":
                for (var k = 0; k < 3; k++)
                {
                    var a = Fraction((now / 1.8) + (k / 3.0));
                    list.Add(new CharacterParticle((float)(Math.Sin((a * 5) + (k * 2)) * 0.04 * size), (float)(-h - (a * 0.35 * size)), (float)(size * (0.03 + (0.05 * a))), 0xFFF1F5F9,
                        (float)(0.5 * (1 - a) * Math.Min(1, a * 5)), 0));
                }

                break;
            case "wind":
                for (var k = 0; k < 6; k++)
                {
                    var a = Fraction((now / 0.9) + (k / 6.0));
                    list.Add(new CharacterParticle((float)(-facing * ((0.05 * size) + (a * 0.45 * size))), (float)((-h * 0.6) + (((k % 3) - 1) * 0.06 * size)), size * 0.018f, 0xFFE0F2FE, (float)(0.6 * (1 - a)), 0));
                }

                break;
            case "sparkle":
                for (var k = 0; k < 4; k++)
                {
                    var twinkle = Math.Max(0, Math.Sin(2 * Math.PI * ((now / 1.2) + CharacterParticles.Random(k, 9))));
                    if (twinkle <= 0)
                    {
                        continue;
                    }

                    list.Add(new CharacterParticle((float)((CharacterParticles.Random(k, 7) - 0.5) * 0.5 * size), (float)(-h * (0.3 + (0.8 * CharacterParticles.Random(k, 8)))),
                        (float)(0.03 * size * twinkle), 0xFFFDE68A, (float)twinkle, (float)(Math.PI / 4)));
                }

                break;
            case "foam":
                // Thick white foam bursting from its nozzle, the way it faces, spreading and falling.
                var (fx, fy) = Nozzle(prop, facing, size);
                for (var k = 0; k < 14; k++)
                {
                    var a = Fraction((now / 0.7) + (k / 14.0));
                    list.Add(new CharacterParticle((float)(fx + (facing * a * 0.6 * size)), (float)(fy + ((CharacterParticles.Random(k, 11) - 0.5) * a * 0.34 * size) + (a * a * 0.16 * size)),
                        (float)(size * (0.02 + (0.07 * a * (0.6 + (0.8 * CharacterParticles.Random(k, 12)))))), 0xFFFFFFFF, (float)(0.9 * (1 - (a * a))), 0));
                }

                break;
            case "water":
                // A stream of drops arcing out of its nozzle, the way it faces.
                var (wx, wy) = Nozzle(prop, facing, size);
                for (var k = 0; k < 10; k++)
                {
                    var a = Fraction((now / 0.6) + (k / 10.0));
                    list.Add(new CharacterParticle((float)(wx + (facing * a * 0.6 * size)), (float)(wy - (a * 0.12 * size) + (a * a * 0.35 * size)), size * 0.026f, 0xFF38BDF8, (float)(1 - (0.7 * a)), 0));
                }

                break;
            case "glow":
                // Red, then blue, in turn: three soft rings each, faint outside, bright inside.
                var p = Math.Pow((Math.Sin(2 * Math.PI * now / 0.8) + 1) / 2, 2);
                foreach (var (color, on) in new[] { (0xFFEF4444u, p), (0xFF60A5FAu, 1 - p) })
                {
                    list.Add(new CharacterParticle(0, (float)(-h * 0.72), size * 0.3f, color, (float)(0.1 * on), 0));
                    list.Add(new CharacterParticle(0, (float)(-h * 0.72), size * 0.2f, color, (float)(0.16 * on), 0));
                    list.Add(new CharacterParticle(0, (float)(-h * 0.72), size * 0.11f, color, (float)(0.3 * on), 0));
                }

                break;
        }

        return list;
    }

    /// <summary>Whether what it gives off is drawn behind it (a glow) rather than in front.</summary>
    public static bool EmitsBehind(PropInfo prop) => prop.Emit == "glow";

    private static double Fraction(double x) => x - Math.Floor(x);

    /// <summary>Where what a prop sprays comes out, from its point (turned with it when it turns with the character).</summary>
    private static (double X, double Y) Nozzle(PropInfo prop, int facing, float size) => ((prop.Mirror ? facing : 1) * prop.NozzleX * size, -prop.NozzleY * size);

    /// <summary>Easing out past the end and back (a pop).</summary>
    private static double Back(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + (c3 * Math.Pow(t - 1, 3)) + (c1 * Math.Pow(t - 1, 2));
    }

    /// <summary>Easing out with bounces (landing).</summary>
    private static double Bounce(double t)
    {
        const double n = 7.5625, d = 2.75;
        if (t < 1 / d)
        {
            return n * t * t;
        }

        if (t < 2 / d)
        {
            t -= 1.5 / d;
            return (n * t * t) + 0.75;
        }

        if (t < 2.5 / d)
        {
            t -= 2.25 / d;
            return (n * t * t) + 0.9375;
        }

        t -= 2.625 / d;
        return (n * t * t) + 0.984375;
    }
}

/// <summary>
/// A character's prop as it changes (as the editor's PropTrack): the one shown and since when, and the one before it,
/// going, until it has gone.
/// </summary>
public sealed class PropTrack
{
    private string? _id;
    private double _since;
    private string? _old;
    private double _oldSince;
    private double _goneAt;

    /// <summary>The props to draw at <paramref name="now"/> with <paramref name="prop"/> wanted: the one shown (null: none) and how long it has been, and the one going (null: none) with its age and how long since it went.</summary>
    public (string? Id, double Age, string? Going, double GoingAge, double Gone) At(double now, string? prop)
    {
        if (prop != _id)
        {
            if (_id is not null)
            {
                (_old, _oldSince, _goneAt) = (_id, _since, now);
            }

            (_id, _since) = (prop, now);
        }

        if (_old is not null && (now - _goneAt >= CharacterProps.LeaveSeconds || _old == _id))
        {
            _old = null;
        }

        return (_id, now - _since, _old, _old is null ? 0 : now - _oldSince, _old is null ? -1 : now - _goneAt);
    }
}
