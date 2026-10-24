using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// What is drawn with a character besides its frame (as the editor's CharacterSprite draws it): a soft shadow under its
/// feet, its shake while what it reacts to is critical, and its speech bubble over its head, edged in the bubble's tone
/// and fading in and out.
/// </summary>
internal static class CharacterExtras
{
    /// <summary>How far it shakes, in pixels, at a moment (seconds): side to side, a fortieth of its height at most.</summary>
    public static float ShakeAt(CharacterThought? thought, float size, double now) =>
        thought is { Shake: > 0 } t ? (float)(Math.Sin(now * 38) * size * 0.025 * t.Shake) : 0;

    /// <summary>Where its shadow goes: an ellipse under its feet.</summary>
    public static RectangleF ShadowAt(PointF feet, float size) =>
        new(feet.X - (size * 0.21f), feet.Y - (size * 0.045f), size * 0.42f, size * 0.08f);

    /// <summary>A soft shadow under its feet (darkest in the middle).</summary>
    public static void DrawShadow(Graphics g, PointF feet, float size, float opacity)
    {
        var at = ShadowAt(feet, size);
        if (at.Width < 2 || at.Height < 1)
        {
            return;
        }

        using var path = new GraphicsPath();
        path.AddEllipse(at);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb((int)(90 * opacity), 0, 0, 0),
            SurroundColors = [Color.FromArgb(0, 0, 0, 0)],
        };
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        g.SmoothingMode = smoothing;
    }

    /// <summary>The bubble's text size for a character of <paramref name="size"/> pixels.</summary>
    private static float TextPx(float size) => Math.Clamp(size * 0.17f, 11, 22);

    /// <summary>Where its bubble goes (null: none), its bottom just over its head.</summary>
    public static RectangleF? BubbleAt(CharacterThought? thought, PointF head, float size)
    {
        if (thought?.Bubble is not { } text)
        {
            return null;
        }

        var px = TextPx(size);
        using var font = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
        var measured = TextRenderer.MeasureText(text, font);
        var pad = px * 0.6f;
        var w = measured.Width + (2 * pad);
        var h = (px * 1.35f) + (2 * pad * 0.6f);
        var tail = px * 0.5f;
        // A little rise as it appears.
        var rise = (float)Math.Max(0, 1 - (thought.BubbleAge / 0.25)) * px * 0.4f;
        return new RectangleF(head.X - (w / 2), head.Y - (px * 0.3f) - tail - h + rise, w, h + tail);
    }

    /// <summary>Its bubble (the rectangle from <see cref="BubbleAt"/>, its tail under it pointing at its head).</summary>
    public static void DrawBubble(Graphics g, CharacterThought thought, RectangleF at, float size, float opacity)
    {
        if (thought.Bubble is not { } text)
        {
            return;
        }

        var px = TextPx(size);
        var tail = px * 0.5f;
        var fade = (float)Math.Clamp(Math.Min(thought.BubbleAge / 0.25, (thought.BubbleLife - thought.BubbleAge) / 0.4), 0, 1) * opacity;
        if (fade <= 0.01f)
        {
            return;
        }

        var box = new RectangleF(at.X, at.Y, at.Width, at.Height - tail);
        var r = Math.Min(box.Height / 2, px * 0.7f);
        using var path = new GraphicsPath();
        path.AddArc(box.X, box.Y, 2 * r, 2 * r, 180, 90);
        path.AddArc(box.Right - (2 * r), box.Y, 2 * r, 2 * r, 270, 90);
        path.AddArc(box.Right - (2 * r), box.Bottom - (2 * r), 2 * r, 2 * r, 0, 90);
        var middle = box.X + (box.Width / 2);
        path.AddLine(middle + (tail * 0.7f), box.Bottom, middle, box.Bottom + tail);
        path.AddLine(middle, box.Bottom + tail, middle - (tail * 0.7f), box.Bottom);
        path.AddArc(box.X, box.Bottom - (2 * r), 2 * r, 2 * r, 90, 90);
        path.CloseFigure();

        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (var fill = new SolidBrush(Color.FromArgb((int)(235 * fade), 15, 23, 42)))
        {
            g.FillPath(fill, path);
        }

        using (var edge = new Pen(Color.FromArgb((int)(255 * fade), ToneColor(thought.Tone)), Math.Max(1.5f, px * 0.13f)))
        {
            g.DrawPath(edge, path);
        }

        using var font = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.FromArgb((int)(255 * fade), 248, 250, 252));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, ink, box, format);
        g.Restore(state);
    }

    /// <summary>Its particles at a moment (<see cref="CharacterParticles"/>): its burst (confetti) and, running, its dust.</summary>
    public static IReadOnlyList<CharacterParticle> ParticlesOf(CharacterThought? thought, CharacterMotion.Frame frame, float size, double now)
    {
        var confetti = thought is { Burst: "confetti" } t ? CharacterParticles.ConfettiAt(t.BurstAge, size) : [];
        var dust = frame.Action == "run" ? CharacterParticles.DustAt(now, frame.Facing, size) : [];
        return dust.Count == 0 ? confetti : confetti.Count == 0 ? dust : [.. confetti, .. dust];
    }

    /// <summary>The box its particles take around its feet (empty without any).</summary>
    public static RectangleF ParticlesBox(IReadOnlyList<CharacterParticle> particles, PointF feet)
    {
        if (particles.Count == 0)
        {
            return RectangleF.Empty;
        }

        var box = RectangleF.FromLTRB(particles.Min(p => p.X - p.R - 1), particles.Min(p => p.Y - p.R - 1), particles.Max(p => p.X + p.R + 1), particles.Max(p => p.Y + p.R + 1));
        box.Offset(feet);
        return box;
    }

    /// <summary>Its particles, from its feet: dust as soft discs, confetti as turning bits.</summary>
    public static void DrawParticles(Graphics g, IReadOnlyList<CharacterParticle> particles, PointF feet, float opacity)
    {
        if (particles.Count == 0)
        {
            return;
        }

        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var p in particles)
        {
            var alpha = (int)Math.Clamp(p.Alpha * opacity * ((p.Color >> 24) & 0xFF), 0, 255);
            if (alpha <= 2)
            {
                continue;
            }

            using var brush = new SolidBrush(Color.FromArgb(alpha, (int)((p.Color >> 16) & 0xFF), (int)((p.Color >> 8) & 0xFF), (int)(p.Color & 0xFF)));
            if (p.Angle == 0)
            {
                g.FillEllipse(brush, feet.X + p.X - p.R, feet.Y + p.Y - p.R, 2 * p.R, 2 * p.R);
                continue;
            }

            var turned = g.Save();
            g.TranslateTransform(feet.X + p.X, feet.Y + p.Y);
            g.RotateTransform(p.Angle * 180 / MathF.PI);
            g.FillRectangle(brush, -p.R, -p.R * 0.5f, 2 * p.R, p.R);
            g.Restore(turned);
        }

        g.Restore(state);
    }

    /// <summary>The box a wheel's spokes take (the ring's inside); empty for a needle.</summary>
    public static RectangleF WheelBox(CharacterReacting.GaugePlay? play)
    {
        if (play is not { Kind: "wheel" } wheel)
        {
            return RectangleF.Empty;
        }

        var r = Math.Max(4, wheel.Gauge.Radius - (wheel.Gauge.Thickness / 2));
        return new RectangleF(wheel.Gauge.Center.X - r, wheel.Gauge.Center.Y - r, 2 * r, 2 * r);
    }

    /// <summary>
    /// A hamster wheel inside a ring gauge: eight spokes and a hub, faint, turning back under the runner's feet as it
    /// runs (by <see cref="CharacterReacting.GaugePlay.Spin"/>).
    /// </summary>
    public static void DrawWheel(Graphics g, CharacterReacting.GaugePlay? play, float opacity)
    {
        var box = WheelBox(play);
        if (box.IsEmpty)
        {
            return;
        }

        var r = box.Width / 2;
        var center = new PointF(box.X + r, box.Y + r);
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var spoke = new Pen(Color.FromArgb((int)(70 * opacity), 255, 255, 255), Math.Max(1.2f, r * 0.03f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var turn = -(play!.Spin % 360) * Math.PI / 180;
        for (var k = 0; k < 8; k++)
        {
            var a = turn + (k * Math.PI / 4);
            var (cos, sin) = ((float)Math.Cos(a), (float)Math.Sin(a));
            g.DrawLine(spoke, center.X + (cos * r * 0.18f), center.Y + (sin * r * 0.18f), center.X + (cos * r * 0.94f), center.Y + (sin * r * 0.94f));
        }

        using var hub = new Pen(Color.FromArgb((int)(90 * opacity), 255, 255, 255), Math.Max(1.5f, r * 0.04f));
        g.DrawEllipse(hub, center.X - (r * 0.16f), center.Y - (r * 0.16f), r * 0.32f, r * 0.32f);
        g.Restore(state);
    }

    /// <summary>A prop as drawn at a moment: what it is, its frame and where its point is in it, its point (on the canvas) and pose, and what it gives off.</summary>
    internal readonly record struct PropDrawn(PropInfo Info, Bitmap Bitmap, PointF Anchor, PointF Origin, PropPose Pose, IReadOnlyList<CharacterParticle> Particles);

    /// <summary>
    /// A character's props at <paramref name="now"/> (<see cref="CharacterProps"/>): the one shown and the one going,
    /// each where it goes for this frame (on the top of its head as this frame has it, beside its feet), with what it
    /// gives off; none drawn without the props' file.
    /// </summary>
    public static IReadOnlyList<PropDrawn> PropsOf(CharacterPlayer player, CharacterPlayer? props, CharacterMotion.Frame frame, PointF feet, float size, double now,
        (string? Id, double Age, string? Going, double GoingAge, double Gone) track, bool moving)
    {
        if (props is null || (track.Id is null && track.Going is null))
        {
            return [];
        }

        var head = player.HeadAt(frame.Action, frame.Seconds, frame.Facing, size);
        var reach = player.ReachAt(frame.Facing, size);
        var list = new List<PropDrawn>(2);
        foreach (var (id, age, gone) in new[] { (track.Going, track.GoingAge, track.Gone), (track.Id, track.Age, -1.0) })
        {
            if (id is null || !OverlayReactions.Props.TryGetValue(id, out var info) || props.Effect(id, 0, frame.Facing, size) is not var (bitmap, anchor, _))
            {
                continue;
            }

            var pose = CharacterProps.PoseAt(info, age, gone, now, frame.Facing, size, head.X, head.Y, reach, moving);
            if (pose.Alpha <= 0.01f)
            {
                continue;
            }

            list.Add(new PropDrawn(info, bitmap, anchor, new PointF(feet.X + pose.X, feet.Y + pose.Y), pose, CharacterProps.EmitAt(info, now, frame.Facing, size)));
        }

        return list;
    }

    /// <summary>The box a prop takes on the canvas (turned and grown as posed), with what it gives off.</summary>
    public static RectangleF PropBox(PropDrawn p)
    {
        var s = p.Pose.Scale;
        var (cos, sin) = (MathF.Cos(p.Pose.Angle), MathF.Sin(p.Pose.Angle));
        PointF Corner(float x, float y) => new(p.Origin.X + (s * ((x * cos) - (y * sin))), p.Origin.Y + (s * ((x * sin) + (y * cos))));
        PointF[] corners = [Corner(-p.Anchor.X, -p.Anchor.Y), Corner(p.Bitmap.Width - p.Anchor.X, -p.Anchor.Y), Corner(-p.Anchor.X, p.Bitmap.Height - p.Anchor.Y), Corner(p.Bitmap.Width - p.Anchor.X, p.Bitmap.Height - p.Anchor.Y)];
        var box = RectangleF.FromLTRB(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
        var sparks = ParticlesBox(p.Particles, p.Origin);
        return sparks.IsEmpty ? box : RectangleF.Union(box, sparks);
    }

    /// <summary>
    /// A prop's part drawn behind the character (<paramref name="behind"/>: an umbrella, a siren's glow) or in front of
    /// it (the rest), at an opacity (0-1).
    /// </summary>
    public static void DrawProp(Graphics g, PropDrawn p, float opacity, bool behind)
    {
        var alpha = p.Pose.Alpha * opacity;
        if (CharacterProps.EmitsBehind(p.Info) == behind)
        {
            DrawParticles(g, p.Particles, p.Origin, alpha);
        }

        if (p.Info.Behind != behind)
        {
            return;
        }

        var state = g.Save();
        g.TranslateTransform(p.Origin.X, p.Origin.Y);
        g.RotateTransform(p.Pose.Angle * 180 / MathF.PI);
        g.ScaleTransform(p.Pose.Scale, p.Pose.Scale);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        var at = new Rectangle((int)Math.Round(-p.Anchor.X), (int)Math.Round(-p.Anchor.Y), p.Bitmap.Width, p.Bitmap.Height);
        if (alpha >= 0.999f)
        {
            g.DrawImage(p.Bitmap, at);
        }
        else
        {
            using var faded = new System.Drawing.Imaging.ImageAttributes();
            faded.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
            g.DrawImage(p.Bitmap, at, 0, 0, p.Bitmap.Width, p.Bitmap.Height, GraphicsUnit.Pixel, faded);
        }

        g.Restore(state);
    }

    /// <summary>A bubble tone's edge color: critical red, high amber, relief green, info blue.</summary>
    public static Color ToneColor(string tone) => tone switch
    {
        "critical" => Color.FromArgb(239, 68, 68),
        "high" => Color.FromArgb(245, 158, 11),
        "relief" => Color.FromArgb(52, 211, 153),
        _ => Color.FromArgb(96, 165, 250),
    };
}
