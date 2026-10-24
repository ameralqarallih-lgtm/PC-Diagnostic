using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>A part of a panel being arranged on the screen.</summary>
internal enum ArrangePart
{
    None,
    Panel,
    Reading,
    Close,
    Menu,
    Grip,
}

/// <summary>
/// A gauge as drawn: its box (the ring's or dial's square), its kind (ring, arc or speed), how far it is filled (0-1)
/// and its stroke's thickness; where its fill starts and how far it can go, in degrees clockwise from the right.
/// </summary>
/// <param name="Line">A graph's line as drawn, its points left to right (null for other gauges).</param>
internal sealed record GaugeHit(RectangleF Box, string Visual, float Fraction, float Thickness, IReadOnlyList<PointF>? Line = null)
{
    /// <summary>Where the fill starts: the top for a ring, the lower left for an arc or a speedometer.</summary>
    public float Start => Visual switch { "arc" => 145, "speed" => 160, _ => -90 };

    /// <summary>How far the fill can go: a full turn for a ring, 250 degrees for an arc, 220 for a speedometer.</summary>
    public float Sweep => Visual switch { "arc" => 250, "speed" => 220, _ => 360 };

    /// <summary>Its middle.</summary>
    public PointF Center => new(Box.X + (Box.Width / 2), Box.Y + (Box.Height / 2));

    /// <summary>The radius of its stroke's middle.</summary>
    public float Radius => (Math.Min(Box.Width, Box.Height) - Thickness) / 2;

    /// <summary>The gauge moved to (<paramref name="x"/>, <paramref name="y"/>) further.</summary>
    public GaugeHit Offset(float x, float y) => this with
    {
        Box = new RectangleF(Box.X + x, Box.Y + y, Box.Width, Box.Height),
        Line = Line is null ? null : [.. Line.Select(p => new PointF(p.X + x, p.Y + y))],
    };

    /// <summary>The height of a graph's line at <paramref name="x"/> (between its points; its ends beyond them).</summary>
    public float LineAt(float x)
    {
        if (Line is not { Count: > 0 } line)
        {
            return Box.Bottom;
        }

        if (x <= line[0].X)
        {
            return line[0].Y;
        }

        for (var i = 1; i < line.Count; i++)
        {
            if (x <= line[i].X)
            {
                var (a, b) = (line[i - 1], line[i]);
                return b.X - a.X < 0.001f ? b.Y : a.Y + ((b.Y - a.Y) * (x - a.X) / (b.X - a.X));
            }
        }

        return line[^1].Y;
    }
}

/// <summary>Where a drawn panel's parts are, in its bitmap's pixels (for arranging it on the screen).</summary>
/// <param name="Box">The panel itself (inside its shadow).</param>
/// <param name="Readings">Each reading's area, in reading order.</param>
/// <param name="Close">The hide (or show) button.</param>
/// <param name="Menu">The options button.</param>
/// <param name="Grip">The resize corner.</param>
/// <param name="Gauges">Each reading's gauge as drawn (a ring, arc or speedometer; null for other visuals), in the same order.</param>
internal sealed record PanelHits(RectangleF Box, IReadOnlyList<RectangleF> Readings, RectangleF Close, RectangleF Menu, RectangleF Grip, IReadOnlyList<GaugeHit?>? Gauges = null)
{
    /// <summary>The part at a point, and the reading's index when it is one.</summary>
    public (ArrangePart Part, int Reading) At(Point point)
    {
        if (Close.Contains(point))
        {
            return (ArrangePart.Close, -1);
        }

        if (Menu.Contains(point))
        {
            return (ArrangePart.Menu, -1);
        }

        if (Grip.Contains(point))
        {
            return (ArrangePart.Grip, -1);
        }

        for (var i = 0; i < Readings.Count; i++)
        {
            if (Readings[i].Contains(point))
            {
                return (ArrangePart.Reading, i);
            }
        }

        return Box.Contains(point) ? (ArrangePart.Panel, -1) : (ArrangePart.None, -1);
    }
}

/// <summary>How a panel being arranged is drawn: its outline and buttons, the reading under the mouse, a reading dragged.</summary>
/// <param name="Part">The part under the mouse.</param>
/// <param name="Hover">The reading under the mouse.</param>
/// <param name="Dragged">The reading being dragged.</param>
/// <param name="Target">Where the dragged reading would go.</param>
/// <param name="Hidden">The panel is hidden (its button shows it again).</param>
internal sealed record ArrangeView(ArrangePart Part = ArrangePart.None, int? Hover = null, int? Dragged = null, int? Target = null, bool Hidden = false);

/// <summary>
/// Draws a panel of readings into a 32-bit bitmap with per-pixel transparency (what a layered window shows). Three
/// layouts: rows (icon, label, graph, value, ring per line, bars under), line (all on one line) and tiles (a grid of
/// tiles, each with its label, a large value and its bar, ring or graph). The look comes entirely from the panel's
/// style: a solid or gradient background with a sheen, a shadow, a border, an accent edge or title bar, values that
/// glow or carry a dark outline, icons, and bars, rings and graphs in the accent (or the warning and critical colors).
/// Text is drawn as outlines; sizes grow with the monitor's display scaling. The editor's preview follows the same
/// rules (web/src/features/overlay/PanelPreview.tsx).
/// </summary>
internal static partial class OverlayRenderer
{
    private static readonly ConcurrentDictionary<string, FontFamily> Families = new(StringComparer.Ordinal);

    /// <summary>Draws a panel; the caller disposes the bitmap.</summary>
    /// <param name="panel">The panel.</param>
    /// <param name="sources">The values now, by source id.</param>
    /// <param name="scale">The monitor's display scaling.</param>
    /// <param name="history">Recent values for graphs (none drawn without it).</param>
    /// <param name="now">The time the graphs end at.</param>
    /// <param name="atLeast">
    /// The bitmap's size last time (an automatic size never shrinks while it is shown, so the panel does not jump as
    /// values change width); ignored for a fixed width or height.
    /// </param>
    public static Bitmap Render(OverlayPanel panel, IReadOnlyDictionary<string, OverlaySource> sources, double scale, OverlayHistory? history = null, DateTime? now = null, Size? atLeast = null) =>
        RenderPanel(panel, sources, scale, history, now, atLeast, null).Bitmap;

    /// <summary>The background pictures by id (the service loads them from the data folder); none without it.</summary>
    public static Func<string, Image?>? Images { get; set; }

    /// <summary>The animated background pictures (".anim"), by id; null when missing.</summary>
    public static Func<string, AnimatedPicture?>? Animations { get; set; }

    /// <summary>Whether a picture is an animation (played frame by frame on the moving layers).</summary>
    internal static bool IsAnimation(string? image) => image is not null && image.EndsWith(".anim", StringComparison.Ordinal);

    /// <summary>Draws a panel and says where its parts are; with <paramref name="arrange"/>, as it is being arranged.</summary>
    public static (Bitmap Bitmap, PanelHits Hits) RenderPanel(OverlayPanel panel, IReadOnlyDictionary<string, OverlaySource> sources, double scale, OverlayHistory? history, DateTime? now, Size? atLeast, ArrangeView? arrange)
    {
        var (bitmap, hits, layers) = RenderLayers(panel, sources, scale, history, now, atLeast, arrange, motionSeconds: null, keepLayers: false);
        layers?.Dispose();
        return (bitmap, hits);
    }

    /// <summary>
    /// Draws a panel; one with a moving background is drawn as two layers, its background and its readings, composed
    /// with the motion at <paramref name="motionSeconds"/> (still, at its start, without it), and the layers are given
    /// back when asked (to compose the next moments without drawing the readings again).
    /// </summary>
    internal static (Bitmap Bitmap, PanelHits Hits, MotionLayers? Layers) RenderLayers(OverlayPanel panel, IReadOnlyDictionary<string, OverlaySource> sources, double scale, OverlayHistory? history, DateTime? now, Size? atLeast, ArrangeView? arrange, double? motionSeconds, bool keepLayers)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(sources);
        var style = panel.Style;
        var m = new Metrics(style, Family(style.Font), (float)Math.Clamp(scale, 0.5, 4));
        var at = now ?? DateTime.UtcNow;
        var columns = Math.Clamp(panel.Columns, 1, 6);

        // Tiles are placed first: a tile spanning more cells shows its value larger.
        var places = panel.Layout == "tiles" ? Pack(panel.Readings, columns) : null;
        var items = panel.Readings.Select((r, i) => MakeItem(r, sources.GetValueOrDefault(r.Source), style, m, history, at, places is null ? 1 : HeroScale(places[i]))).ToList();
        using var title = string.IsNullOrWhiteSpace(panel.Title) ? null : Text(Caps(panel.Title!, style.Caps), m.Family, FontStyle.Bold, m.TitlePx, tracking: Tracking(style.Caps));
        try
        {
            var body = panel.Layout switch
            {
                "line" => MeasureLine(items, m),
                "tiles" => MeasureTiles(items, m, places!, columns, 0, 0).Size,
                _ => MeasureRows(items, m, Math.Clamp(panel.RowColumns, 1, 4), 0).Size,
            };

            // The title on its own line, or on a band across the top (which replaces the top padding).
            var titleBand = title is null ? 0 : style.TitleBar ? m.TitleLine + (m.Pad * 0.9f) : m.TitleLine + m.RowGap;
            var bandTrim = style.TitleBar && title is not null ? m.Pad * 0.2f : 0;
            var naturalWidth = Math.Max(body.Width, (title?.Width ?? 0) + (style.TitleBar ? m.Pad * 0.3f : 0));
            var naturalHeight = body.Height;
            if (items.Count == 0 && title is null)
            {
                naturalWidth = m.Px * 6;
                naturalHeight = m.Line;
            }

            // The size: fixed, or what the readings need and never less than last time.
            var panelWidth = panel.Width is { } fixedWidth
                ? (float)fixedWidth * m.S
                : Math.Max(naturalWidth + (2 * m.Pad), (atLeast?.Width ?? 0) - (2 * m.Margin));
            var panelHeight = panel.Height is { } fixedHeight
                ? (float)fixedHeight * m.S
                : Math.Max(naturalHeight + (2 * m.Pad) + titleBand - bandTrim, (atLeast?.Height ?? 0) - (2 * m.Margin) - m.ShadowOffset);
            var contentWidth = Math.Max(0, panelWidth - (2 * m.Pad));
            var contentHeight = Math.Max(0, panelHeight - (2 * m.Pad) - titleBand + bandTrim);

            // Whole pixels, rounded up but not for float noise: the size kept from the last drawing comes back as that
            // size less the margins, and adding them again can land a hair above it (which grew the panel a pixel a draw).
            var bitmap = new Bitmap(
                Math.Max(1, WholePixels(panelWidth + (2 * m.Margin))),
                Math.Max(1, WholePixels(panelHeight + (2 * m.Margin) + m.ShadowOffset)),
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Colors blend as a browser blends them (not in linear light, which "high quality" does): a translucent tile,
            // sheen, shadow or tint then looks as the editor shows it rather than washed out.
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            g.Clear(Color.Transparent);

            // Half a pixel in for a crisp border; without one the background covers every pixel (a panel the size of its
            // screen has no soft edge).
            var box = style.Border is null
                ? new RectangleF(m.Margin, m.Margin, panelWidth, panelHeight)
                : new RectangleF(m.Margin + 0.5f, m.Margin + 0.5f, panelWidth - 1, panelHeight - 1);
            using var shape = RoundedRect(box, style.CornerRadius * m.S);

            // An animated picture is drawn frame by frame between the layers (with what lies over it).
            var animation = IsAnimation(style.Image) ? Animations?.Invoke(style.Image!) : null;
            KeptBackground(g, bitmap.Size, shape, box, style, m, overlays: animation is null);

            // A moving background: what is above it (title, readings, border) goes on a layer of its own.
            var moving = (style.Motion is { } motion && motion != "none") || animation is not null;
            // Premultiplied: the layers are blended every motion frame, several times faster than plain alpha.
            Bitmap? back = moving ? bitmap.Clone(new Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb) : null;
            Bitmap? frontBitmap = null;
            Graphics? front = null;
            if (moving)
            {
                frontBitmap = new Bitmap(bitmap.Width, bitmap.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                front = Graphics.FromImage(frontBitmap);
                front.SmoothingMode = SmoothingMode.AntiAlias;
                front.TextRenderingHint = TextRenderingHint.AntiAlias;
                front.PixelOffsetMode = PixelOffsetMode.HighQuality;
                front.CompositingQuality = CompositingQuality.AssumeLinear;
                front.Clear(Color.Transparent);
            }

            var gc = front ?? g;

            // A panel made smaller than its readings shows what fits (its readings are cut at its edge).
            var outside = gc.Clip;
            if (panel.Width is not null || panel.Height is not null)
            {
                gc.SetClip(shape);
            }

            var top = box.Y + m.Pad;
            if (title is not null)
            {
                if (style.TitleBar)
                {
                    var band = new RectangleF(box.X, box.Y, box.Width, titleBand);
                    var clip = gc.Clip;
                    gc.SetClip(shape, CombineMode.Intersect);
                    using (var fill = new SolidBrush(Color.FromArgb(235, Parse(style.Accent))))
                    {
                        gc.FillRectangle(fill, band);
                    }

                    gc.Clip = clip;
                    Draw(gc, title, box.X + m.Pad, box.Y + ((titleBand - m.TitleLine) / 2), OnBand(Parse(style.TitleColor), Parse(style.Accent)), false, 0, m);
                    top = box.Y + titleBand + (m.Pad * 0.8f);
                }
                else
                {
                    Draw(gc, title, box.X + m.Pad, top, Parse(style.TitleColor), style.Outline, style.Glow, m);
                    top += titleBand;
                }
            }

            var origin = new PointF(box.X + m.Pad, top);
            switch (panel.Layout)
            {
                case "line":
                    // A line taller than its readings (a size kept or chosen) has them in the middle.
                    DrawLine(gc, items, new PointF(origin.X, origin.Y + Math.Max(0, (contentHeight - naturalHeight) / 2)), m, style);
                    break;
                case "tiles":
                    DrawTiles(gc, items, origin, m, style, places!, columns, contentWidth, contentHeight);
                    break;
                default:
                    DrawRows(gc, items, origin, m, style, Math.Clamp(panel.RowColumns, 1, 4), contentWidth);
                    break;
            }

            gc.Clip = outside;

            // The border last, so it sits over the accent edge and the title band.
            if (style.Border is { } border)
            {
                var color = Parse(border);
                if (style.Glow > 0)
                {
                    Glow(gc, shape, color, style.Glow, m.GlowPx * 0.8f);
                }

                using var pen = new Pen(color, (float)style.BorderWidth * m.S);
                gc.DrawPath(pen, shape);
            }

            var chip = 22 * m.S;
            var inset = 5 * m.S;
            var close = new RectangleF(box.Right - inset - chip, box.Y + inset, chip, chip);
            var hits = new PanelHits(
                box,
                items.Select(i => i.Bounds).ToList(),
                close,
                new RectangleF(close.X - chip - (4 * m.S), close.Y, chip, chip),
                new RectangleF(box.Right - chip, box.Bottom - chip, chip, chip),
                items.Select(i => i.Gauge).ToList());
            if (arrange is not null)
            {
                DrawArrange(gc, box, items, arrange, hits, style, m);
            }

            if (front is null)
            {
                return (bitmap, hits, null);
            }

            // The background, the motion at this moment, then the rest.
            front.Dispose();
            var layers = new MotionLayers(back!, frontBitmap!, box, style.CornerRadius * m.S, style, m.S, animation, m);
            g.ResetClip();
            layers.Compose(g, motionSeconds ?? 0);
            if (keepLayers)
            {
                return (bitmap, hits, layers);
            }

            layers.Dispose();
            return (bitmap, hits, null);
        }
        finally
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    /// <summary>
    /// A panel with a moving background as layers: its background (shadow, color, picture, pattern) and what is above it
    /// (title, readings, border), each drawn once per refresh; <c>Compose</c> draws a moment of the motion between
    /// them. The moment is the time in seconds (the same clock everywhere, so panels and the editor move together).
    /// </summary>
    internal sealed class MotionLayers(Bitmap back, Bitmap front, RectangleF box, float radius, OverlayStyle style, float s, AnimatedPicture? animation = null, Metrics? metrics = null) : IDisposable
    {
        public Size Size => back.Size;

        /// <summary>Draws the panel at a moment into a picture of its size (cleared first).</summary>
        public void Compose(Graphics g, double seconds)
        {
            ArgumentNullException.ThrowIfNull(g);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImageUnscaled(back, 0, 0);
            g.CompositingMode = CompositingMode.SourceOver;
            using (var shape = RoundedRect(box, radius))
            {
                // The animated picture's frame at this moment, with what lies over it (pattern, sheen, accent edge).
                if (animation is not null && metrics is not null)
                {
                    DrawOverlays(g, shape, box, style, metrics, animation.FrameAt(seconds), fast: true);
                }

                DrawMotion(g, shape, box, style, s, seconds);
            }

            g.DrawImageUnscaled(front, 0, 0);
        }

        /// <summary>Draws the panel at a moment into a picture of its size.</summary>
        public void Compose(Bitmap target, double seconds)
        {
            ArgumentNullException.ThrowIfNull(target);
            using var g = Graphics.FromImage(target);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.AssumeLinear;
            Compose(g, seconds);
        }

        public void Dispose()
        {
            back.Dispose();
            front.Dispose();
        }
    }

    /// <summary>
    /// A moment of a moving background, inside the panel and under its readings: aurora (soft lights drifting), grid
    /// (lines scrolling down), stars (drifting, twinkling), pulse (a breathing glow), scan (a band sweeping down) or
    /// shimmer (a sheen sweeping across); its speed is the style's, its colors the accent and the gradient's second
    /// color, its strength following the background's opacity.
    /// </summary>
    internal static void DrawMotion(Graphics g, GraphicsPath shape, RectangleF box, OverlayStyle style, float s, double seconds)
    {
        if (style.Motion is not { } motion || motion == "none" || box.Width < 2 || box.Height < 2)
        {
            return;
        }

        var t = seconds * Math.Clamp(style.MotionSpeed, 25, 300) / 100.0;
        var accent = Parse(style.Accent);
        var second = style.Background2 is { } b2 ? Parse(b2) : accent;
        var strength = Math.Clamp(Math.Max(style.BackgroundOpacity, 35), 0, 100) / 100f;
        int A(double alpha) => Math.Clamp((int)(alpha * strength), 0, 255);
        var clip = g.Clip;
        g.SetClip(shape, CombineMode.Intersect);
        try
        {
            switch (motion)
            {
                case "aurora":
                    for (var i = 0; i < 3; i++)
                    {
                        var cx = box.X + (box.Width * (float)(0.5 + (0.42 * Math.Sin((t * 0.23) + (i * 2.1)))));
                        var cy = box.Y + (box.Height * (float)(0.5 + (0.38 * Math.Cos((t * 0.19) + (i * 1.7)))));
                        var r = Math.Max(box.Width, box.Height) * (float)(0.5 + (0.12 * Math.Sin((t * 0.31) + i)));
                        using var path = new GraphicsPath();
                        path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                        using var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb(A(110), i % 2 == 0 ? accent : second), SurroundColors = [Color.FromArgb(0, i % 2 == 0 ? accent : second)] };
                        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
                    }

                    break;
                case "grid":
                {
                    var step = 22 * s;
                    var offset = (float)((t * 14 * s) % step);
                    using var pen = new Pen(Color.FromArgb(A(70), accent), Math.Max(1, 0.8f * s));
                    for (var x = box.X + (step / 2); x < box.Right; x += step)
                    {
                        g.DrawLine(pen, x, box.Y, x, box.Bottom);
                    }

                    for (var y = box.Y - step + offset; y < box.Bottom; y += step)
                    {
                        g.DrawLine(pen, box.X, y, box.Right, y);
                    }

                    break;
                }

                case "stars":
                {
                    var random = new Random(((int)box.Width * 31) + (int)box.Height);
                    var count = Math.Clamp((int)(box.Width * box.Height / (700 * s * s)), 12, 160);
                    using var brush = new SolidBrush(Color.White);
                    for (var i = 0; i < count; i++)
                    {
                        var x0 = random.NextDouble() * box.Width;
                        var y = box.Y + (float)(random.NextDouble() * box.Height);
                        var speed = (0.4 + random.NextDouble()) * 9 * s;
                        var size = (float)((0.6 + (random.NextDouble() * 1.4)) * s);
                        var x = box.X + (float)(((x0 + (t * speed)) % box.Width + box.Width) % box.Width);
                        var twinkle = 0.55 + (0.45 * Math.Sin((t * 2.2) + (i * 1.3)));
                        brush.Color = Color.FromArgb(A(220 * twinkle), i % 5 == 0 ? accent : Color.White);
                        g.FillEllipse(brush, x - size, y - size, size * 2, size * 2);
                    }

                    break;
                }

                case "pulse":
                {
                    var beat = 0.5 + (0.5 * Math.Sin(t * Math.PI * 2 / 2.4));
                    var cx = box.X + (box.Width / 2);
                    var cy = box.Y + (box.Height / 2);
                    var r = Math.Max(box.Width, box.Height) * (float)(0.55 + (0.15 * beat));
                    using var path = new GraphicsPath();
                    path.AddEllipse(cx - r, cy - r, r * 2, r * 2);
                    using var brush = new PathGradientBrush(path) { CenterColor = Color.FromArgb(A(40 + (70 * beat)), accent), SurroundColors = [Color.FromArgb(0, accent)] };
                    g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
                    break;
                }

                case "scan":
                {
                    var band = Math.Max(box.Height * 0.22f, 14 * s);
                    var y = box.Y - band + (float)(((t / 3.2) % 1) * (box.Height + (band * 2)));
                    var area = new RectangleF(box.X, y, box.Width, band);
                    using var brush = new LinearGradientBrush(new RectangleF(area.X, area.Y - 1, area.Width, area.Height + 2), Color.Transparent, Color.Transparent, LinearGradientMode.Vertical)
                    {
                        InterpolationColors = new ColorBlend { Colors = [Color.FromArgb(0, accent), Color.FromArgb(A(95), accent), Color.FromArgb(0, accent)], Positions = [0f, 0.5f, 1f] },
                    };
                    g.FillRectangle(brush, area);
                    break;
                }

                case "shimmer":
                {
                    var width = Math.Max(box.Width * 0.35f, 30 * s);
                    var x = box.X - width + (float)(((t / 4.0) % 1) * (box.Width + (width * 2)));
                    var area = new RectangleF(x, box.Y, width, box.Height);
                    using var brush = new LinearGradientBrush(new RectangleF(area.X - 1, area.Y, area.Width + 2, area.Height), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
                    {
                        InterpolationColors = new ColorBlend { Colors = [Color.FromArgb(0, Color.White), Color.FromArgb(A(70), Color.White), Color.FromArgb(0, Color.White)], Positions = [0f, 0.5f, 1f] },
                    };
                    g.FillRectangle(brush, area);
                    break;
                }
            }
        }
        finally
        {
            g.Clip = clip;
        }
    }

    /// <summary>A panel being arranged: a dashed outline, the reading under the mouse, the dragged one and where it goes, and buttons.</summary>
    private static void DrawArrange(Graphics g, RectangleF box, List<Item> items, ArrangeView view, PanelHits hits, OverlayStyle style, Metrics m)
    {
        var accent = Parse(style.Accent);
        using (var outline = new Pen(Color.FromArgb(235, accent), Math.Max(1.5f, 2 * m.S)) { DashStyle = DashStyle.Dash })
        {
            var around = RectangleF.Inflate(box, 1.5f * m.S, 1.5f * m.S);
            using var path = RoundedRect(around, (style.CornerRadius * m.S) + (1.5f * m.S));
            g.DrawPath(outline, path);
        }

        void Mark(int index, Color fill, Color line, float width, bool dashed)
        {
            if (index < 0 || index >= items.Count || items[index].Bounds.IsEmpty)
            {
                return;
            }

            var r = RectangleF.Inflate(items[index].Bounds, 2 * m.S, 2 * m.S);
            using var path = RoundedRect(r, 5 * m.S);
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            using var pen = new Pen(line, width) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
            g.DrawPath(pen, path);
        }

        if (view.Dragged is { } dragged)
        {
            Mark(dragged, Color.FromArgb(45, Color.White), Color.FromArgb(160, Color.White), Math.Max(1, 1.5f * m.S), dashed: true);
            if (view.Target is { } target && target != dragged)
            {
                Mark(target, Color.FromArgb(40, accent), accent, Math.Max(2, 3 * m.S), dashed: false);
            }
        }
        else if (view.Hover is { } hover)
        {
            Mark(hover, Color.FromArgb(38, accent), Color.FromArgb(210, accent), Math.Max(1, 1.5f * m.S), dashed: false);
        }

        Chip(g, hits.Menu, view.Part == ArrangePart.Menu, accent, m, (graphics, r, ink) =>
        {
            var d = Math.Max(1.5f, r.Width * 0.09f);
            for (var i = -1; i <= 1; i++)
            {
                graphics.FillEllipse(ink, r.X + (r.Width / 2) + (i * r.Width * 0.22f) - d, r.Y + (r.Height / 2) - d, d * 2, d * 2);
            }
        });
        Chip(g, hits.Close, view.Part == ArrangePart.Close, accent, m, (graphics, r, ink) =>
        {
            using var pen = new Pen(ink, Math.Max(1.5f, r.Width * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var c = new PointF(r.X + (r.Width / 2), r.Y + (r.Height / 2));
            var k = r.Width * 0.2f;
            if (view.Hidden)
            {
                // An eye: show the panel again.
                graphics.DrawEllipse(pen, c.X - (r.Width * 0.3f), c.Y - (r.Height * 0.17f), r.Width * 0.6f, r.Height * 0.34f);
                graphics.FillEllipse(ink, c.X - (r.Width * 0.09f), c.Y - (r.Width * 0.09f), r.Width * 0.18f, r.Width * 0.18f);
            }
            else
            {
                graphics.DrawLine(pen, c.X - k, c.Y - k, c.X + k, c.Y + k);
                graphics.DrawLine(pen, c.X - k, c.Y + k, c.X + k, c.Y - k);
            }
        });

        // The resize corner: three short diagonal strokes.
        var grip = hits.Grip;
        using (var back = new SolidBrush(Color.FromArgb(view.Part == ArrangePart.Grip ? 230 : 150, view.Part == ArrangePart.Grip ? accent : Color.FromArgb(20, 20, 24))))
        {
            using var path = RoundedRect(new RectangleF(grip.X + (grip.Width * 0.25f), grip.Y + (grip.Height * 0.25f), grip.Width * 0.7f, grip.Height * 0.7f), 4 * m.S);
            g.FillPath(back, path);
        }

        using (var strokes = new Pen(Color.FromArgb(235, Color.White), Math.Max(1.2f, 1.4f * m.S)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            for (var i = 1; i <= 3; i++)
            {
                var o = grip.Width * (0.22f * i);
                g.DrawLine(strokes, grip.Right - (grip.Width * 0.12f) - o, grip.Bottom - (grip.Height * 0.12f), grip.Right - (grip.Width * 0.12f), grip.Bottom - (grip.Height * 0.12f) - o);
            }
        }
    }

    /// <summary>Where a panel's handles are, in its window's pixels (empty for those it does not allow).</summary>
    internal readonly record struct HandleSpots(RectangleF Move, RectangleF Close, RectangleF Grip)
    {
        /// <summary>The handle at a point.</summary>
        public ArrangePart At(PointF point) =>
            Close.Contains(point) ? ArrangePart.Close : Grip.Contains(point) ? ArrangePart.Grip : Move.Contains(point) ? ArrangePart.Panel : ArrangePart.None;
    }

    /// <summary>
    /// A panel's handles for moving, closing and resizing it on the screen itself (those it allows): a grip bar at the
    /// top, a close button at the top right and the resize corner, in a picture the size of its window that is fully
    /// transparent elsewhere (a click beside them goes through to what is under the panel). Each handle's whole area is
    /// at least slightly opaque, so all of it takes the click.
    /// </summary>
    internal static (Bitmap Bitmap, HandleSpots Spots) Handles(Size window, PanelHits hits, OverlayStyle style, double scale, bool move, bool resize, bool close, ArrangePart hover)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentNullException.ThrowIfNull(style);
        var s = (float)Math.Clamp(scale, 0.5, 4);
        var m = new Metrics(new OverlayStyle(), Family("Segoe UI"), s);
        var accent = Parse(style.Accent);
        var box = hits.Box;
        var bitmap = new Bitmap(Math.Max(1, window.Width), Math.Max(1, window.Height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.CompositingQuality = CompositingQuality.AssumeLinear;
        g.Clear(Color.Transparent);

        var bar = move ? new RectangleF(box.X + (box.Width / 2) - (28 * s), box.Y + (2 * s), 56 * s, 20 * s) : RectangleF.Empty;
        if (move)
        {
            // The whole bar takes the click; the pill and its dots show it.
            using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            {
                g.FillRectangle(hit, bar);
            }

            var pill = new RectangleF(bar.X + (6 * s), bar.Y + (4 * s), bar.Width - (12 * s), bar.Height - (8 * s));
            using (var path = RoundedRect(pill, pill.Height / 2))
            using (var fill = new SolidBrush(hover == ArrangePart.Panel ? accent : Color.FromArgb(205, 18, 20, 26)))
            using (var edge = new Pen(Color.FromArgb(110, Color.White), Math.Max(1, s)))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }

            using var dots = new SolidBrush(Color.FromArgb(235, Color.White));
            var d = Math.Max(1.2f, 1.3f * s);
            for (var row = 0; row < 2; row++)
            {
                for (var col = -2; col <= 2; col++)
                {
                    g.FillEllipse(dots, pill.X + (pill.Width / 2) + (col * 5 * s) - d, pill.Y + (pill.Height / 2) + ((row - 0.5f) * 4 * s) - d, d * 2, d * 2);
                }
            }
        }

        if (close)
        {
            Chip(g, hits.Close, hover == ArrangePart.Close, accent, m, (graphics, r, ink) =>
            {
                using var pen = new Pen(ink, Math.Max(1.5f, r.Width * 0.09f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                var c = new PointF(r.X + (r.Width / 2), r.Y + (r.Height / 2));
                var k = r.Width * 0.2f;
                graphics.DrawLine(pen, c.X - k, c.Y - k, c.X + k, c.Y + k);
                graphics.DrawLine(pen, c.X - k, c.Y + k, c.X + k, c.Y - k);
            });
        }

        if (resize)
        {
            var grip = hits.Grip;
            using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
            {
                g.FillRectangle(hit, grip);
            }

            using (var back = new SolidBrush(Color.FromArgb(hover == ArrangePart.Grip ? 230 : 170, hover == ArrangePart.Grip ? accent : Color.FromArgb(20, 20, 24))))
            using (var path = RoundedRect(new RectangleF(grip.X + (grip.Width * 0.25f), grip.Y + (grip.Height * 0.25f), grip.Width * 0.7f, grip.Height * 0.7f), 4 * s))
            {
                g.FillPath(back, path);
            }

            using var strokes = new Pen(Color.FromArgb(235, Color.White), Math.Max(1.2f, 1.4f * s)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (var i = 1; i <= 3; i++)
            {
                var o = grip.Width * (0.22f * i);
                g.DrawLine(strokes, grip.Right - (grip.Width * 0.12f) - o, grip.Bottom - (grip.Height * 0.12f), grip.Right - (grip.Width * 0.12f), grip.Bottom - (grip.Height * 0.12f) - o);
            }
        }

        return (bitmap, new HandleSpots(bar, close ? hits.Close : RectangleF.Empty, resize ? hits.Grip : RectangleF.Empty));
    }

    private static void Chip(Graphics g, RectangleF r, bool hover, Color accent, Metrics m, Action<Graphics, RectangleF, Brush> glyph)
    {
        using (var fill = new SolidBrush(hover ? accent : Color.FromArgb(205, 18, 20, 26)))
        {
            g.FillEllipse(fill, r);
        }

        using (var edge = new Pen(Color.FromArgb(110, Color.White), Math.Max(1, m.S)))
        {
            g.DrawEllipse(edge, r);
        }

        using var ink = new SolidBrush(hover ? (Luminance(accent) > 0.55 ? Color.FromArgb(10, 12, 16) : Color.White) : Color.White);
        glyph(g, r, ink);
    }

    /// <summary>
    /// The bar shown at the top of the main screen while the panels are arranged on the screens: what can be done, and
    /// a Done button (with where it is).
    /// </summary>
    public static (Bitmap Bitmap, RectangleF Done) ArrangeBar(double scale, bool hoverDone)
    {
        var s = (float)Math.Clamp(scale, 0.5, 4);
        var family = Family("Segoe UI");
        var m = new Metrics(new OverlayStyle(), family, s);
        using var title = Text("Arranging on-screen readings", family, FontStyle.Bold, 14 * s);
        using var hint = Text("Drag a panel to move it, its corner to resize it, a reading onto another to reorder · right-click for options · Esc to finish", family, FontStyle.Regular, 12.5f * s);
        using var done = Text("Done", family, FontStyle.Bold, 13.5f * s);
        var pad = 14 * s;
        var height = 46 * s;
        var doneWidth = done.Width + (28 * s);
        var width = pad + title.Width + (16 * s) + hint.Width + (18 * s) + doneWidth + (8 * s);
        var margin = 16 * s;
        var bitmap = new Bitmap((int)Math.Ceiling(width + (2 * margin)), (int)Math.Ceiling(height + (2 * margin)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        var box = new RectangleF(margin, margin, width, height);
        using var shape = RoundedRect(box, height / 2);
        for (var i = 6; i >= 1; i--)
        {
            using var shadow = new SolidBrush(Color.FromArgb(14, 0, 0, 0));
            using var soft = RoundedRect(RectangleF.Inflate(box, i * 1.6f * s, i * 1.6f * s), (height / 2) + (i * 1.6f * s));
            g.FillPath(shadow, soft);
        }

        using (var fill = new LinearGradientBrush(box, Color.FromArgb(245, 22, 28, 38), Color.FromArgb(245, 12, 16, 24), LinearGradientMode.Vertical))
        {
            g.FillPath(fill, shape);
        }

        var accent = Color.FromArgb(56, 189, 248);
        using (var edge = new Pen(Color.FromArgb(150, accent), Math.Max(1, 1.2f * s)))
        {
            g.DrawPath(edge, shape);
        }

        var x = box.X + pad;
        Draw(g, title, x, box.Y + ((height - title.Size * 1.33f) / 2), Color.White, false, 0, m);
        x += title.Width + (16 * s);
        Draw(g, hint, x, box.Y + ((height - hint.Size * 1.33f) / 2), Color.FromArgb(170, 185, 200), false, 0, m);
        var doneBox = new RectangleF(box.Right - (8 * s) - doneWidth, box.Y + (7 * s), doneWidth, height - (14 * s));
        using (var button = RoundedRect(doneBox, doneBox.Height / 2))
        using (var brush = new SolidBrush(hoverDone ? Color.FromArgb(125, 211, 252) : accent))
        {
            g.FillPath(brush, button);
        }

        Draw(g, done, doneBox.X + ((doneBox.Width - done.Width) / 2), doneBox.Y + ((doneBox.Height - (done.Size * 1.33f)) / 2), Color.FromArgb(4, 19, 29), false, 0, m);
        return (bitmap, doneBox);
    }

    /// <summary>A large number with the monitor's name, to tell the monitors apart.</summary>
    public static Bitmap Identify(OverlayDisplay display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var s = (float)Math.Clamp(display.Scale, 0.5, 4);
        var family = Family("Segoe UI");
        using var number = Text(display.Number.ToString(CultureInfo.InvariantCulture), family, FontStyle.Bold, 150 * s);
        using var name = Text(display.Name, family, FontStyle.Bold, 28 * s);
        using var detail = Text($"{display.Width} × {display.Height}{(display.RefreshHz is { } hz ? $" · {Math.Round(hz)} Hz" : string.Empty)}{(display.Primary ? " · main display" : string.Empty)}", family, FontStyle.Regular, 20 * s);
        var pad = 36 * s;
        var width = Math.Max(number.Width, Math.Max(name.Width, detail.Width)) + (2 * pad);
        var height = LineHeight(family, 150 * s) + LineHeight(family, 28 * s) + LineHeight(family, 20 * s) + (2 * pad);
        var bitmap = new Bitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var box = new RectangleF(0.5f, 0.5f, bitmap.Width - 1, bitmap.Height - 1);
        using (var shape = RoundedRect(box, 24 * s))
        {
            using (var fill = Gradient(box, Color.FromArgb(235, 30, 27, 75), Color.FromArgb(235, 14, 116, 144), 135))
            {
                g.FillPath(fill, shape);
            }

            Sheen(g, shape, box);
            using var pen = new Pen(Color.FromArgb(34, 211, 238), 3 * s);
            g.DrawPath(pen, shape);
        }

        var y = pad;
        Draw(g, number, (bitmap.Width - number.Width) / 2, y, Color.White, false, 0, null);
        y += LineHeight(family, 150 * s);
        Draw(g, name, (bitmap.Width - name.Width) / 2, y, Color.FromArgb(34, 211, 238), false, 0, null);
        y += LineHeight(family, 28 * s);
        Draw(g, detail, (bitmap.Width - detail.Width) / 2, y, Color.FromArgb(199, 210, 254), false, 0, null);
        return bitmap;
    }

    /// <summary>A color from #RRGGBB (white when it is not one).</summary>
    public static Color Parse(string? hex) =>
        OverlayStyles.IsColor(hex) ? Color.FromArgb(int.Parse(hex!.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : Color.White;

    // ---- Background --------------------------------------------------------------------------------------------

    /// <summary>A panel's background as last drawn for the same look, size and scale (a refresh or a value gliding draws the same one).</summary>
    private readonly record struct BackgroundKey(OverlayStyle Style, float Scale, Size Size, RectangleF Box, bool Overlays);

    private static readonly Lock BackgroundsLock = new();
    private static readonly LinkedList<(BackgroundKey Key, Bitmap Picture)> Backgrounds = new();
    private const long BackgroundsBudget = 48L * 1024 * 1024;

    /// <summary>
    /// Draws the background, from the copy kept for the same look, size and scale when there is one (its shadow alone is
    /// several blurred passes); the 16 last used are kept, within 48 MB. A picture not loaded yet is not kept.
    /// </summary>
    private static void KeptBackground(Graphics g, Size size, GraphicsPath shape, RectangleF box, OverlayStyle style, Metrics m, bool overlays)
    {
        var key = new BackgroundKey(style, m.S, size, box, overlays);
        lock (BackgroundsLock)
        {
            for (var node = Backgrounds.First; node is not null; node = node.Next)
            {
                if (node.Value.Key.Equals(key))
                {
                    Backgrounds.Remove(node);
                    Backgrounds.AddFirst(node);
                    var mode = g.CompositingMode;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(node.Value.Picture, 0, 0);
                    g.CompositingMode = mode;
                    return;
                }
            }
        }

        var waiting = overlays && style.Image is { } image && !IsAnimation(image) && Images?.Invoke(image) is null;
        if (waiting)
        {
            DrawBackground(g, shape, box, style, m, overlays);
            return;
        }

        var picture = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var copy = Graphics.FromImage(picture))
        {
            copy.SmoothingMode = SmoothingMode.AntiAlias;
            copy.PixelOffsetMode = PixelOffsetMode.HighQuality;
            copy.CompositingQuality = CompositingQuality.AssumeLinear;
            copy.Clear(Color.Transparent);
            DrawBackground(copy, shape, box, style, m, overlays);
        }

        var drawing = g.CompositingMode;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(picture, 0, 0);
        g.CompositingMode = drawing;
        lock (BackgroundsLock)
        {
            Backgrounds.AddFirst((key, picture));
            long bytes = 0;
            var count = 0;
            for (var node = Backgrounds.First; node is not null;)
            {
                var next = node.Next;
                bytes += (long)node.Value.Picture.Width * node.Value.Picture.Height * 4;
                if (++count > 16 || bytes > BackgroundsBudget)
                {
                    node.Value.Picture.Dispose();
                    Backgrounds.Remove(node);
                }

                node = next;
            }
        }
    }

    private static void DrawBackground(Graphics g, GraphicsPath shape, RectangleF box, OverlayStyle style, Metrics m, bool overlays = true)
    {
        if (style.Shadow > 0 && (style.BackgroundOpacity > 0 || style.Border is not null))
        {
            Shadow(g, box, style, m);
        }

        if (style.BackgroundOpacity > 0)
        {
            var alpha = style.BackgroundOpacity * 255 / 100;
            using Brush fill = style.Background2 is { } second
                ? Gradient(box, Color.FromArgb(alpha, Parse(style.Background)), Color.FromArgb(alpha, Parse(second)), style.GradientAngle)
                : new SolidBrush(Color.FromArgb(alpha, Parse(style.Background)));
            g.FillPath(fill, shape);
        }

        if (overlays)
        {
            DrawOverlays(g, shape, box, style, m, style.Image is { } image && !IsAnimation(image) ? Images?.Invoke(image) : null);
        }
    }

    /// <summary>What lies over the background's color: the picture (a still, or an animation's frame), the pattern, the sheen and the accent edge.</summary>
    /// <param name="fast">A moving picture: scaled with bilinear filtering (a still one with bicubic).</param>
    private static void DrawOverlays(Graphics g, GraphicsPath shape, RectangleF box, OverlayStyle style, Metrics m, Image? picture, bool fast = false)
    {
        if (picture is not null)
        {
            DrawImage(g, shape, box, picture, style, m, fast);
        }

        if (style.Pattern is { } pattern && style.PatternOpacity > 0)
        {
            DrawPattern(g, shape, box, pattern, style, m);
        }

        if (style.Sheen && style.BackgroundOpacity > 0)
        {
            Sheen(g, shape, box);
        }

        if (style.AccentEdge is "left" or "top" or "bottom")
        {
            var thickness = Math.Max(3f, 3.5f * m.S);
            var stripe = style.AccentEdge switch
            {
                "left" => new RectangleF(box.X, box.Y, thickness, box.Height),
                "top" => new RectangleF(box.X, box.Y, box.Width, thickness),
                _ => new RectangleF(box.X, box.Bottom - thickness, box.Width, thickness),
            };
            var clip = g.Clip;
            g.SetClip(shape);
            using (var accent = new SolidBrush(Parse(style.Accent)))
            {
                g.FillRectangle(accent, stripe);
            }

            g.Clip = clip;
        }
    }

    /// <summary>
    /// The background picture, clipped to the panel: placed (cover, contain, stretch, tile or centre), blurred (drawn
    /// small, then large), grey, at its opacity, under its tint.
    /// </summary>
    private static void DrawImage(Graphics g, GraphicsPath shape, RectangleF box, Image picture, OverlayStyle style, Metrics m, bool fast = false)
    {
        var clip = g.Clip;
        g.SetClip(shape, CombineMode.Intersect);
        var interpolation = g.InterpolationMode;
        g.InterpolationMode = fast ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
        Bitmap? blurred = null;
        try
        {
            Image source = picture;
            if (style.ImageBlur > 0)
            {
                var factor = 1 + (style.ImageBlur * m.S / 2.5f);
                blurred = new Bitmap(Math.Max(2, (int)(picture.Width / factor)), Math.Max(2, (int)(picture.Height / factor)));
                using (var small = Graphics.FromImage(blurred))
                {
                    small.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    small.DrawImage(picture, 0, 0, blurred.Width, blurred.Height);
                }

                source = blurred;
            }

            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            var matrix = style.ImageGray
                ? new System.Drawing.Imaging.ColorMatrix([[0.299f, 0.299f, 0.299f, 0, 0], [0.587f, 0.587f, 0.587f, 0, 0], [0.114f, 0.114f, 0.114f, 0, 0], [0, 0, 0, 1, 0], [0, 0, 0, 0, 1]])
                : new System.Drawing.Imaging.ColorMatrix();
            matrix.Matrix33 = style.ImageOpacity / 100f;
            attributes.SetColorMatrix(matrix);
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            if (style.ImageFit == "tile")
            {
                var w = picture.Width * m.S;
                var h = picture.Height * m.S;
                for (var y = box.Y; y < box.Bottom; y += h)
                {
                    for (var x = box.X; x < box.Right; x += w)
                    {
                        g.DrawImage(source, Rectangle.Round(new RectangleF(x, y, w, h)), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                    }
                }
            }
            else
            {
                g.DrawImage(source, Rectangle.Round(ImageBox(box, picture.Width, picture.Height, style, m)), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
        }
        finally
        {
            blurred?.Dispose();
            g.InterpolationMode = interpolation;
        }

        if (style.ImageTint is { } tint && style.ImageTintOpacity > 0)
        {
            using var over = new SolidBrush(Color.FromArgb(style.ImageTintOpacity * 255 / 100, Parse(tint)));
            g.FillPath(over, shape);
        }

        g.Clip = clip;
    }

    /// <summary>Where a background picture goes in its panel (as CSS places a background image).</summary>
    private static RectangleF ImageBox(RectangleF box, int width, int height, OverlayStyle style, Metrics m)
    {
        if (style.ImageFit == "stretch" || width <= 0 || height <= 0)
        {
            return box;
        }

        var scale = style.ImageFit switch
        {
            "cover" => Math.Max(box.Width / width, box.Height / height),
            "contain" => Math.Min(box.Width / width, box.Height / height),
            _ => m.S,
        };
        var w = width * scale;
        var h = height * scale;
        var x = style.ImagePosition switch { "left" => box.X, "right" => box.Right - w, _ => box.X + ((box.Width - w) / 2) };
        var y = style.ImagePosition switch { "top" => box.Y, "bottom" => box.Bottom - h, _ => box.Y + ((box.Height - h) / 2) };
        return new RectangleF(x, y, w, h);
    }

    /// <summary>A pattern over the background, repeated from one tile from the panel's corner (the editor draws the same tile).</summary>
    private static void DrawPattern(Graphics g, GraphicsPath shape, RectangleF box, string pattern, OverlayStyle style, Metrics m)
    {
        var u = 12 * m.S * style.PatternScale / 100f;
        var color = Color.FromArgb(style.PatternOpacity * 255 / 100, style.PatternColor is { } own ? Parse(own) : Luminance(Parse(style.Background)) < 0.5 ? Color.White : Color.Black);
        var (tileWidth, tileHeight) = PatternTile(pattern, u);
        using var tile = new Bitmap(Math.Max(1, (int)Math.Round(tileWidth)), Math.Max(1, (int)Math.Round(tileHeight)));
        using (var t = Graphics.FromImage(tile))
        {
            t.SmoothingMode = SmoothingMode.AntiAlias;
            t.ScaleTransform(tile.Width / tileWidth, tile.Height / tileHeight);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, Math.Max(1, 0.07f * u));
            switch (pattern)
            {
                case "dots":
                    t.FillEllipse(brush, (u / 2) - (0.12f * u), (u / 2) - (0.12f * u), 0.24f * u, 0.24f * u);
                    break;
                case "grid":
                    t.FillRectangle(brush, 0, 0, u, Math.Max(1, 0.06f * u));
                    t.FillRectangle(brush, 0, 0, Math.Max(1, 0.06f * u), u);
                    break;
                case "diagonal":
                {
                    var d = tileWidth;
                    t.DrawLine(pen, 0, d, d, 0);
                    t.DrawLine(pen, -d / 4, d / 4, d / 4, -d / 4);
                    t.DrawLine(pen, 3 * d / 4, 5 * d / 4, 5 * d / 4, 3 * d / 4);
                    break;
                }

                case "carbon":
                {
                    var c = tileWidth;
                    t.FillRectangle(brush, 0, 0, c / 2, c / 2);
                    t.FillRectangle(brush, c / 2, c / 2, c / 2, c / 2);
                    break;
                }

                case "hexagons":
                {
                    // One hexagon (pointy top) and the edge down to the next row; the tile repeats into a honeycomb.
                    var w = tileWidth;
                    var r = tileHeight / 3;
                    using var hex = new GraphicsPath();
                    hex.AddPolygon(new[] { new PointF(w / 2, 0), new PointF(w, r / 2), new PointF(w, 1.5f * r), new PointF(w / 2, 2 * r), new PointF(0, 1.5f * r), new PointF(0, r / 2) });
                    t.DrawPath(pen, hex);
                    t.DrawLine(pen, w / 2, 2 * r, w / 2, 3 * r);
                    break;
                }

                default:
                    // Scanlines: a line, then a gap.
                    t.FillRectangle(brush, 0, 0, tileWidth, tileHeight / 2);
                    break;
            }
        }

        var clip = g.Clip;
        g.SetClip(shape, CombineMode.Intersect);
        using (var texture = new TextureBrush(tile, WrapMode.Tile))
        {
            texture.ScaleTransform(tileWidth / tile.Width, tileHeight / tile.Height, MatrixOrder.Append);
            texture.TranslateTransform(box.X, box.Y, MatrixOrder.Append);
            g.FillPath(texture, shape);
        }

        g.Clip = clip;
    }

    /// <summary>A pattern's tile size for its unit (12 px at 100 % size and scaling).</summary>
    internal static (float Width, float Height) PatternTile(string pattern, float u) => pattern switch
    {
        "diagonal" => (0.7f * u, 0.7f * u),
        "carbon" => (0.5f * u, 0.5f * u),
        "hexagons" => (u * 0.866f, u * 1.5f),
        "scanlines" => (1, Math.Max(2, 0.25f * u)),
        _ => (u, u),
    };

    /// <summary>A soft shadow under the panel: rounded rectangles growing outwards with falling opacity.</summary>
    private static void Shadow(Graphics g, RectangleF box, OverlayStyle style, Metrics m)
    {
        const int Layers = 10;
        var strength = style.Shadow / 100f;
        for (var i = Layers; i >= 1; i--)
        {
            var grow = m.ShadowBlur * i / Layers;
            var falloff = 1 - ((i - 1) / (float)Layers);
            var alpha = (int)Math.Clamp(255 * 0.5f * strength * falloff * falloff / Layers * 2.2f, 0, 255);
            var r = new RectangleF(box.X - grow + (m.ShadowBlur * 0.15f), box.Y - grow + m.ShadowOffset + (m.ShadowBlur * 0.15f), box.Width + (2 * grow) - (m.ShadowBlur * 0.3f), box.Height + (2 * grow) - (m.ShadowBlur * 0.3f));
            using var layer = RoundedRect(r, (style.CornerRadius * m.S) + grow);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillPath(brush, layer);
        }
    }

    /// <summary>A light sheen over the top half (glass) and a lighter top edge.</summary>
    private static void Sheen(Graphics g, GraphicsPath shape, RectangleF box)
    {
        // The brush is a little larger than what it fills: GDI+ repeats a gradient's first color on its last row.
        var half = new RectangleF(box.X, box.Y, box.Width, box.Height * 0.5f);
        var brushBox = RectangleF.Inflate(half, 0, 2);
        using (var sheen = new LinearGradientBrush(brushBox, Color.FromArgb(30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical) { WrapMode = WrapMode.TileFlipXY })
        {
            var clip = g.Clip;
            g.SetClip(shape);
            g.FillRectangle(sheen, half);
            g.Clip = clip;
        }

        using var edgeBrush = new LinearGradientBrush(brushBox, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical) { WrapMode = WrapMode.TileFlipXY };
        using var edge = new Pen(edgeBrush, 1);
        g.DrawPath(edge, shape);
    }

    /// <summary>A size in whole pixels, rounded up (a thousandth over a whole pixel is float noise, not more room).</summary>
    private static int WholePixels(float size) => (int)Math.Ceiling(size - 0.001f);

    /// <summary>Relative luminance, 0 (black) to 1 (white).</summary>
    internal static double Luminance(Color c)
    {
        static double Channel(int v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    /// <summary>The title's color on the accent band: its own when it stands out, else white or near black.</summary>
    internal static Color OnBand(Color title, Color band)
    {
        var (a, b) = (Luminance(title), Luminance(band));
        var contrast = (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        return contrast >= 3 ? title : Luminance(band) > 0.45 ? Color.FromArgb(15, 23, 42) : Color.White;
    }

    /// <summary>A linear gradient at a CSS angle (0 to the top, 90 to the right) that covers the box like CSS's.</summary>
    private static LinearGradientBrush Gradient(RectangleF box, Color from, Color to, int angle)
    {
        var radians = angle * Math.PI / 180;
        var dx = (float)Math.Sin(radians);
        var dy = (float)-Math.Cos(radians);
        var length = (Math.Abs(box.Width * dx) + Math.Abs(box.Height * dy)) / 2 + 1;
        var center = new PointF(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        var brush = new LinearGradientBrush(new PointF(center.X - (dx * length), center.Y - (dy * length)), new PointF(center.X + (dx * length), center.Y + (dy * length)), from, to)
        {
            WrapMode = WrapMode.TileFlipXY,
        };
        return brush;
    }

    // ---- Rows --------------------------------------------------------------------------------------------------

    /// <summary>One column of the rows layout: its parts' widths (icon, label, graph, value, ring) and its whole width.</summary>
    private sealed record RowColumn(float Icon, float Label, float Graph, float Value, float Ring)
    {
        public float Width => Icon + Label + Graph + Value + Ring;
    }

    /// <summary>The rows layout, readings left to right in <paramref name="columns"/> columns; extra width goes to the labels.</summary>
    private static (SizeF Size, List<RowColumn> Columns) MeasureRows(List<Item> items, Metrics m, int columns, float width)
    {
        var cols = Math.Max(1, Math.Min(columns, items.Count));
        var list = new List<RowColumn>();
        for (var j = 0; j < cols; j++)
        {
            var column = items.Where((_, i) => i % cols == j).ToList();
            list.Add(RowColumns(column, m));
        }

        var gap = m.ColGap * 1.6f;
        var natural = list.Sum(c => c.Width) + ((cols - 1) * gap);
        if (width > natural && list.Count > 0)
        {
            var extra = (width - natural) / list.Count;
            list = list.Select(c => c with { Label = c.Label + extra }).ToList();
        }

        float height = 0;
        for (var r = 0; r * cols < items.Count; r++)
        {
            var row = items.Skip(r * cols).Take(cols).ToList();
            height += row.Max(i => RowHeight(i, m)) + row.Max(i => UnderHeight(i, m)) + (r > 0 ? m.RowGap : 0);
        }

        return (new SizeF(Math.Max(natural, width), height), list);
    }

    private static RowColumn RowColumns(List<Item> items, Metrics m)
    {
        var icon = items.Any(i => i.Icon is not null) ? items.Where(i => i.Icon is not null).Max(i => i.M.IconSize) + (m.ColGap * 0.55f) : 0;
        var labelWidth = items.Count == 0 ? 0 : items.Max(i => i.Label?.Width ?? 0);
        var label = labelWidth > 0 ? labelWidth + m.ColGap : 0;
        var graph = items.Any(i => IsChart(i)) ? items.Where(IsChart).Max(i => i.M.InlineGraphWidth) + (m.ColGap * 0.6f) : 0;
        var value = items.Count == 0 ? 0 : items.Max(i => i.ValueWidth(m));
        var ring = items.Any(i => IsDial(i)) ? items.Where(IsDial).Max(i => i.M.InlineRing) + (m.ColGap * 0.5f) : 0;
        return new RowColumn(icon, label, graph, value, ring);
    }

    private static float RowHeight(Item item, Metrics m) =>
        Math.Max(Math.Max(item.ValueLine, item.Label is null ? 0 : item.M.LabelLine), Math.Max(item.Icon is null ? 0 : item.M.IconSize, IsDial(item) ? item.M.InlineRing : 0));

    private static bool IsDial(Item item) => OverlayStyles.Dials.Contains(item.Visual);

    private static bool IsChart(Item item) => OverlayStyles.Charts.Contains(item.Visual);

    private static bool IsUnder(Item item) => OverlayStyles.Unders.Contains(item.Visual);

    /// <summary>The height a bar or thermometer takes under its value (with the gap above it).</summary>
    private static float UnderHeight(Item item, Metrics m) => item.Visual == "thermo" ? m.BarGap + ThermoHeight(item.M) : item.Visual == "bar" ? m.BarGap + item.M.BarH : 0;

    private static float ThermoHeight(Metrics m) => Math.Max(m.BarH * 1.9f, 6 * m.S);

    private static void DrawRows(Graphics g, List<Item> items, PointF origin, Metrics m, OverlayStyle style, int columns, float contentWidth)
    {
        if (items.Count == 0)
        {
            return;
        }

        var (_, cols) = MeasureRows(items, m, columns, contentWidth);
        var gap = m.ColGap * 1.6f;
        var y = origin.Y;
        for (var r = 0; r * cols.Count < items.Count; r++)
        {
            var row = items.Skip(r * cols.Count).Take(cols.Count).ToList();
            var h = row.Max(i => RowHeight(i, m));
            var left = origin.X;
            for (var j = 0; j < row.Count; j++)
            {
                var item = row[j];
                var c = cols[j];
                var x = left;
                if (item.Icon is not null)
                {
                    DrawIcon(g, item, x, y + ((h - item.M.IconSize) / 2), item.M.IconSize, style, m);
                }

                x += c.Icon;
                var valueTop = y + ((h - item.ValueLine) / 2);
                if (item.Label is not null)
                {
                    Draw(g, item.Label, x, valueTop + item.Value.Ascent - item.Label.Ascent, item.LabelColor, style.Outline, 0, m);
                }

                x += c.Label;
                if (IsChart(item))
                {
                    var gh = h * 0.78f;
                    Chart(g, new RectangleF(x, y + ((h - gh) / 2), item.M.InlineGraphWidth, gh), item, style, m);
                }

                x += c.Graph;

                // The value column is each row's value space (as wide as the widest of them can read).
                DrawValue(g, item, x, c.Value, valueTop, style, m);
                x += c.Value;
                if (IsDial(item))
                {
                    Dial(g, new RectangleF(x + (m.ColGap * 0.5f), y + ((h - item.M.InlineRing) / 2), item.M.InlineRing, item.M.InlineRing), item, style, m, item.M.InlineRing * 0.16f, inline: true);
                }

                if (IsUnder(item))
                {
                    Under(g, new RectangleF(left + c.Icon, y + h + m.BarGap, c.Width - c.Icon, UnderHeight(item, m) - m.BarGap), item, style, m);
                }

                item.Bounds = new RectangleF(left, y, c.Width, h + row.Max(i => UnderHeight(i, m)));
                left += c.Width + gap;
            }

            y += h + row.Max(i => UnderHeight(i, m)) + m.RowGap;
        }
    }

    // ---- Line --------------------------------------------------------------------------------------------------

    private static float LineItemWidth(Item item, Metrics m) =>
        (item.Icon is null ? 0 : item.M.IconSize + (m.ColGap * 0.35f))
        + (item.Label is null ? 0 : item.Label.Width + (m.Px * 0.35f))
        + item.ValueWidth(m)
        + (IsChart(item) ? (m.ColGap * 0.5f) + item.M.LineGraphWidth : 0)
        + (IsDial(item) ? (m.ColGap * 0.4f) + item.M.InlineRing : 0);

    private static SizeF MeasureLine(List<Item> items, Metrics m)
    {
        var width = items.Sum(i => LineItemWidth(i, m)) + (Math.Max(0, items.Count - 1) * m.ColGap * 1.4f);
        var h = items.Count == 0 ? m.Line : items.Max(i => RowHeight(i, m));
        var height = h + (items.Count == 0 ? 0 : items.Max(i => UnderHeight(i, m)));
        return new SizeF(width, height);
    }

    private static void DrawLine(Graphics g, List<Item> items, PointF origin, Metrics m, OverlayStyle style)
    {
        var h = items.Count == 0 ? m.Line : items.Max(i => RowHeight(i, m));
        var x = origin.X;
        foreach (var item in items)
        {
            var valueTop = origin.Y + ((h - item.ValueLine) / 2);
            var left = x;
            var width = LineItemWidth(item, m);
            if (item.Icon is not null)
            {
                DrawIcon(g, item, x, origin.Y + ((h - item.M.IconSize) / 2), item.M.IconSize, style, m);
                x += item.M.IconSize + (m.ColGap * 0.35f);
            }

            if (item.Label is not null)
            {
                Draw(g, item.Label, x, valueTop + item.Value.Ascent - item.Label.Ascent, item.LabelColor, style.Outline, 0, m);
                x += item.Label.Width + (m.Px * 0.35f);
            }

            DrawValue(g, item, x, item.ValueWidth(m), valueTop, style, m);
            x += item.ValueWidth(m);
            if (IsChart(item))
            {
                var gh = h * 0.72f;
                Chart(g, new RectangleF(x + (m.ColGap * 0.5f), origin.Y + ((h - gh) / 2), item.M.LineGraphWidth, gh), item, style, m);
                x += (m.ColGap * 0.5f) + item.M.LineGraphWidth;
            }

            if (IsDial(item))
            {
                Dial(g, new RectangleF(x + (m.ColGap * 0.4f), origin.Y + ((h - item.M.InlineRing) / 2), item.M.InlineRing, item.M.InlineRing), item, style, m, item.M.InlineRing * 0.16f, inline: true);
                x += (m.ColGap * 0.4f) + item.M.InlineRing;
            }

            if (IsUnder(item))
            {
                Under(g, new RectangleF(left, origin.Y + h + m.BarGap, width, UnderHeight(item, m) - m.BarGap), item, style, m);
            }

            item.Bounds = new RectangleF(left, origin.Y, width, h + UnderHeight(item, m));
            x = left + width + (m.ColGap * 1.4f);
        }
    }

    // ---- Tiles -------------------------------------------------------------------------------------------------

    /// <summary>Where a tile goes in the grid: its first row and column and how many it spans.</summary>
    internal readonly record struct Place(int Row, int Column, int Columns, int Rows);

    /// <summary>
    /// Places the tiles in reading order, each in the first free cells where it fits (CSS grid's "row dense"
    /// placement, which the editor's preview uses too).
    /// </summary>
    internal static List<Place> Pack(IReadOnlyList<OverlayReading> readings, int columns)
    {
        var taken = new List<bool[]>();
        bool Free(int row, int column, int width, int height)
        {
            for (var r = row; r < row + height; r++)
            {
                for (var c = column; c < column + width; c++)
                {
                    if (r < taken.Count && taken[r][c])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        var places = new List<Place>();
        foreach (var reading in readings)
        {
            var width = Math.Clamp(reading.ColSpan, 1, columns);
            var height = Math.Clamp(reading.RowSpan, 1, 4);
            for (var row = 0; ; row++)
            {
                var column = Enumerable.Range(0, columns - width + 1).Cast<int?>().FirstOrDefault(c => Free(row, c!.Value, width, height));
                if (column is not { } at)
                {
                    continue;
                }

                while (taken.Count < row + height)
                {
                    taken.Add(new bool[columns]);
                }

                for (var r = row; r < row + height; r++)
                {
                    for (var c = at; c < at + width; c++)
                    {
                        taken[r][c] = true;
                    }
                }

                places.Add(new Place(row, at, width, height));
                break;
            }
        }

        return places;
    }

    /// <summary>How far down its room a tile's value sits: 0 at the top, 0.5 in the middle, 1 at the bottom.</summary>
    private static float Vertical(Item item, string automatic) => (item.Style.ValueVertical ?? automatic) switch
    {
        "top" => 0,
        "bottom" => 1,
        _ => 0.5f,
    };

    /// <summary>A tile spanning more cells shows its value larger (1, 1.6, 2.2…).</summary>
    private static float HeroScale(Place place) => 1 + (0.6f * (Math.Min(place.Columns, place.Rows) - 1));

    /// <summary>A tile's own content size (header and body), without its padding.</summary>
    private static SizeF TileContent(Item item, Metrics m)
    {
        var header = (item.Icon is null ? 0 : item.M.TileIcon + (m.Px * 0.3f)) + (item.Label?.Width ?? 0);
        var headerHeight = item.Label is null && item.Icon is null ? 0 : Math.Max(item.M.LabelLine, item.Icon is null ? 0 : item.M.TileIcon) + (m.Px * 0.35f);
        var (bodyWidth, bodyHeight) = item.Visual switch
        {
            "ring" or "arc" or "speed" => (RingDiameter(item, m) * Math.Max(1, item.M.VisualScale), RingDiameter(item, m) * Math.Max(1, item.M.VisualScale) * (item.Visual == "ring" ? 1 : 0.92f)),
            _ when IsChart(item) => (item.ValueWidth(m), item.ValueLine + (m.Px * 0.3f) + item.M.TileGraphHeight),
            _ when IsUnder(item) => (item.ValueWidth(m), item.ValueLine + UnderHeight(item, m)),
            _ => (item.ValueWidth(m), item.ValueLine),
        };
        return new SizeF(Math.Max(m.Px * 5.5f, Math.Max(header, bodyWidth)), headerHeight + bodyHeight);
    }

    private static float RingDiameter(Item item, Metrics m) => Math.Max(item.ValueLine * 2.3f, item.ValueWidth(m) + (m.Px * 1.6f));

    /// <summary>The grid: one cell size for all (a spanning tile covers its cells and the gaps between), stretched to fill.</summary>
    private static (SizeF Size, float CellWidth, float CellHeight, int Columns, int Rows) MeasureTiles(List<Item> items, Metrics m, List<Place> places, int columns, float width, float height)
    {
        if (items.Count == 0)
        {
            return (new SizeF(0, 0), 0, 0, 0, 0);
        }

        var cols = Math.Min(columns, places.Max(p => p.Column + p.Columns));
        var rows = places.Max(p => p.Row + p.Rows);
        float cellWidth = 0, cellHeight = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var content = TileContent(items[i], m);
            cellWidth = Math.Max(cellWidth, (content.Width + (2 * m.TilePad) - ((places[i].Columns - 1) * m.TileGap)) / places[i].Columns);
            cellHeight = Math.Max(cellHeight, (content.Height + (2 * m.TilePad) - ((places[i].Rows - 1) * m.TileGap)) / places[i].Rows);
        }

        cellWidth = Math.Max(cellWidth, (width - ((cols - 1) * m.TileGap)) / cols);
        cellHeight = Math.Max(cellHeight, (height - ((rows - 1) * m.TileGap)) / rows);
        return (new SizeF((cols * cellWidth) + ((cols - 1) * m.TileGap), (rows * cellHeight) + ((rows - 1) * m.TileGap)), cellWidth, cellHeight, cols, rows);
    }

    private static void DrawTiles(Graphics g, List<Item> items, PointF origin, Metrics m, OverlayStyle style, List<Place> places, int columns, float width, float height)
    {
        if (items.Count == 0)
        {
            return;
        }

        var (_, cellWidth, cellHeight, _, _) = MeasureTiles(items, m, places, columns, width, height);
        var dark = Luminance(Parse(style.Background)) < 0.5;
        var tint = Color.FromArgb(Math.Clamp(style.TileOpacity * 255 / 100, 0, 255), style.TileColor is { } own ? Parse(own) : dark ? Color.White : Color.Black);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var place = places[index];
            var tile = new RectangleF(
                origin.X + (place.Column * (cellWidth + m.TileGap)),
                origin.Y + (place.Row * (cellHeight + m.TileGap)),
                (place.Columns * cellWidth) + ((place.Columns - 1) * m.TileGap),
                (place.Rows * cellHeight) + ((place.Rows - 1) * m.TileGap));
            item.Bounds = tile;
            var radius = Math.Max(2 * m.S, style.CornerRadius * m.S * 0.65f);
            using (var shape = TileShape(tile, radius, style.Backdrop, m))
            {
                // A status tile is tinted in its status color, like the dashboard's status tiles.
                var fillColor = item.Visual == "status" ? Color.FromArgb(Math.Clamp(Math.Max(46, tint.A * 3), 0, 255), item.StatusColor) : tint;
                if (fillColor.A > 0)
                {
                    using var fill = new SolidBrush(fillColor);
                    g.FillPath(fill, shape);
                }

                // What is drawn behind the tile, fitted to it (under the readings).
                if (style.Backdrop is { } backdrop && backdrop != "none")
                {
                    var header = item.Label is not null || item.Icon is not null ? Math.Max(item.M.LabelLine, item.Icon is null ? 0 : item.M.TileIcon) : 0;
                    DrawBackdrop(g, tile, shape, radius, header > 0 ? tile.Y + m.TilePad + header + (m.Px * 0.175f) : null,
                        item.Visual is "ring" or "arc" or "speed", item.Level > 0 ? item.VisualColor : Color.Empty, style, m);
                }

                if (item.Level > 0)
                {
                    using var pen = new Pen(Color.FromArgb(200, item.VisualColor), Math.Max(1f, 1.5f * m.S));
                    g.DrawPath(pen, shape);
                }
            }

            var x = tile.X + m.TilePad;
            var y = tile.Y + m.TilePad;
            var inner = tile.Width - (2 * m.TilePad);
            if (item.Label is not null || item.Icon is not null)
            {
                var headerHeight = Math.Max(item.M.LabelLine, item.Icon is null ? 0 : item.M.TileIcon);
                if (item.Icon is not null)
                {
                    DrawIcon(g, item, x, y + ((headerHeight - item.M.TileIcon) / 2), item.M.TileIcon, style, m);
                    x += item.M.TileIcon + (m.Px * 0.3f);
                }

                if (item.Label is not null)
                {
                    Draw(g, item.Label, x, y + ((headerHeight - item.M.LabelLine) / 2), item.LabelColor, style.Outline, 0, m);
                }

                y += headerHeight + (m.Px * 0.35f);
            }

            x = tile.X + m.TilePad;
            var remaining = tile.Bottom - m.TilePad - y;
            switch (item.Visual)
            {
                case "ring" or "arc" or "speed":
                {
                    // The dial fills its tile (a large tile gets a large dial).
                    var fill = Math.Min(inner, remaining / (item.Visual == "ring" ? 1 : 0.92f));
                    var d = Math.Max(Math.Min(RingDiameter(item, m), fill), fill * Math.Min(1, item.M.VisualScale));
                    var dial = new RectangleF(x + ((inner - d) / 2), y + ((remaining - (d * (item.Visual == "ring" ? 1 : 0.92f))) / 2), d, d);
                    // Thicker with size, but a large tile's ring stays a ring, not a band.
                    var thickness = Math.Max(2 * m.S, Math.Min(d * 0.085f, m.Px * 0.9f));
                    if (style.Backdrop == "bezel")
                    {
                        DrawBezel(g, dial, thickness, style, m, tint);
                    }

                    Dial(g, dial, item, style, m, thickness, inline: false);
                    var valueY = item.Visual switch
                    {
                        "ring" => dial.Y + ((d - item.ValueLine) / 2),
                        "arc" => dial.Y + (d * 0.52f) - (item.ValueLine / 2),
                        _ => dial.Y + (d * 0.66f),
                    };
                    // In a dial, always in the middle.
                    DrawValue(g, item, dial.X, d, valueY, style, m, "center");
                    break;
                }

                case "graph" or "line" or "columns":
                {
                    DrawValue(g, item, x, inner, y, style, m);
                    var graphHeight = Math.Max(item.M.TileGraphHeight, (remaining - item.ValueLine - (m.Px * 0.3f)) * Math.Min(1, item.M.VisualScale));
                    Chart(g, new RectangleF(x, tile.Bottom - m.TilePad - graphHeight, inner, graphHeight), item, style, m);
                    break;
                }

                case "bar" or "thermo":
                {
                    var uh = UnderHeight(item, m) - m.BarGap;
                    // At the top unless chosen otherwise: in the room above the bar.
                    var free = Math.Max(0, tile.Bottom - m.TilePad - uh - m.BarGap - y - item.ValueLine);
                    DrawValue(g, item, x, inner, y + (free * Vertical(item, "top")), style, m);
                    Under(g, new RectangleF(x, tile.Bottom - m.TilePad - uh, inner, uh), item, style, m);
                    break;
                }

                default:
                {
                    var valueY = y + ((remaining - item.ValueLine) * Vertical(item, "middle"));
                    var dot = item.ValueLine * 0.4f * item.M.VisualScale;
                    DrawValue(g, item, x, item.Visual == "status" ? inner - (dot * 1.3f) - (m.Px * 0.5f) : inner, valueY, style, m);
                    if (item.Visual == "status")
                    {
                        // The light sits at the end of the value's line (in the header it would crowd the label).
                        Status(g, new RectangleF(tile.Right - m.TilePad - (dot * 1.3f), valueY + ((item.ValueLine - dot) / 2), dot, dot), item, style, m);
                    }

                    break;
                }
            }
        }
    }

    // ---- Visuals -----------------------------------------------------------------------------------------------

    private static void Bar(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        var fraction = item.Fraction ?? 0;
        item.Gauge = new GaugeHit(r, "bar", fraction, r.Height);
        using var track = new SolidBrush(item.Track);
        using var fill = new SolidBrush(item.VisualColor);
        switch (style.BarStyle)
        {
            case "segments":
            {
                var count = Math.Clamp((int)Math.Round(r.Width / (r.Height * 2.4f)), 5, 24);
                var gap = Math.Max(1f, r.Height * 0.4f);
                var segment = (r.Width - ((count - 1) * gap)) / count;
                var lit = (int)Math.Round(fraction * count);
                for (var i = 0; i < count; i++)
                {
                    var cell = new RectangleF(r.X + (i * (segment + gap)), r.Y, segment, r.Height);
                    g.FillRectangle(i < lit ? fill : track, cell);
                }

                break;
            }

            case "solid":
                g.FillRectangle(track, r);
                g.FillRectangle(fill, r.X, r.Y, r.Width * fraction, r.Height);
                break;
            default:
            {
                using (var back = RoundedRect(r, r.Height / 2))
                {
                    g.FillPath(track, back);
                }

                if (fraction > 0)
                {
                    var width = Math.Max(r.Height, r.Width * fraction);
                    using var front = RoundedRect(new RectangleF(r.X, r.Y, width, r.Height), r.Height / 2);
                    if (style.Glow > 0)
                    {
                        Glow(g, front, item.VisualColor, style.Glow, m.GlowPx * 0.6f);
                    }

                    g.FillPath(fill, front);
                }

                break;
            }
        }
    }

    /// <summary>A dial beside the value or filling a tile: ring, arc, speedometer or status light.</summary>
    private static void Dial(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m, float thickness, bool inline)
    {
        (style, m) = (item.Style, item.M);
        if (item.Visual is "ring" or "arc" or "speed")
        {
            item.Gauge = new GaugeHit(r, item.Visual, item.Fraction ?? 0, thickness * style.BarHeight / 100f);
        }

        switch (item.Visual)
        {
            case "status":
                var size = inline ? r.Width * 0.62f : Math.Min(r.Width, r.Height) * 0.3f;
                Status(g, new RectangleF(r.X + ((r.Width - size) / 2), r.Y + ((r.Height - size) / 2), size, size), item, style, m);
                break;
            case "arc":
                // The dashboard's load arc: 250 degrees open at the bottom.
                Arc(g, r, item, style, m, thickness, 145, 250);
                break;
            case "speed":
                Speedometer(g, r, item, style, m, thickness);
                break;
            default:
                Ring(g, r, item, style, m, thickness);
                break;
        }
    }

    /// <summary>A bar or a thermometer under the value.</summary>
    private static void Under(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        if (item.Visual == "thermo")
        {
            Thermometer(g, r, item, style, m);
        }
        else
        {
            Bar(g, r, item, style, m);
        }
    }

    /// <summary>A chart of the recent history: an area, a line or columns.</summary>
    private static void Chart(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        if (item.Visual == "columns")
        {
            Columns(g, r, item, style, m);
        }
        else
        {
            Graph(g, r, item, style, m, area: item.Visual == "graph");
        }
    }

    /// <summary>An arc of <paramref name="sweep"/> degrees from <paramref name="start"/> (clockwise from the right), filled to the value.</summary>
    private static void Arc(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m, float thickness, float start, float sweep)
    {
        (style, m) = (item.Style, item.M);
        thickness *= style.BarHeight / 100f;
        var arc = RectangleF.Inflate(r, -thickness / 2, -thickness / 2);
        if (arc.Width <= 1)
        {
            return;
        }

        using (var track = new Pen(item.Track, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(track, arc, start, sweep);
        }

        var filled = sweep * (item.Fraction ?? 0);
        if (filled <= 0.5f)
        {
            return;
        }

        if (style.Glow > 0)
        {
            using var glow = new Pen(Color.FromArgb((int)(255 * style.Glow / 100f * 0.18f), item.VisualColor), thickness + (m.GlowPx * 0.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(glow, arc, start, filled);
        }

        using var pen = new Pen(item.VisualColor, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, arc, start, filled);
    }

    /// <summary>A speedometer: a 220-degree scale with ticks and a needle at the value.</summary>
    private static void Speedometer(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m, float thickness)
    {
        (style, m) = (item.Style, item.M);
        const float Start = 160, Sweep = 220;
        thickness *= 0.6f;
        Arc(g, r, item, style, m, thickness, Start, Sweep);
        var center = new PointF(r.X + (r.Width / 2), r.Y + (r.Height / 2));
        var radius = (r.Width / 2) - thickness;
        using (var tick = new Pen(Color.FromArgb(150, item.LabelColor), Math.Max(1f, m.S)))
        {
            for (var i = 0; i <= 5; i++)
            {
                var a = (Start + (Sweep * i / 5)) * Math.PI / 180;
                var (cos, sin) = ((float)Math.Cos(a), (float)Math.Sin(a));
                g.DrawLine(tick, center.X + (cos * radius * 0.78f), center.Y + (sin * radius * 0.78f), center.X + (cos * radius * 0.9f), center.Y + (sin * radius * 0.9f));
            }
        }

        var angle = (Start + (Sweep * (item.Fraction ?? 0))) * Math.PI / 180;
        var tip = new PointF(center.X + ((float)Math.Cos(angle) * radius * 0.72f), center.Y + ((float)Math.Sin(angle) * radius * 0.72f));
        using (var needle = new Pen(item.VisualColor, Math.Max(1.5f, r.Width * 0.035f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(needle, center, tip);
        }

        var hub = Math.Max(2f, r.Width * 0.07f);
        using var brush = new SolidBrush(item.VisualColor);
        g.FillEllipse(brush, center.X - hub, center.Y - hub, 2 * hub, 2 * hub);
    }

    /// <summary>A status light: OK, warning or critical (with a soft halo).</summary>
    private static void Status(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        using (var halo = new SolidBrush(Color.FromArgb(60, item.StatusColor)))
        {
            g.FillEllipse(halo, RectangleF.Inflate(r, r.Width * 0.3f, r.Height * 0.3f));
        }

        if (style.Glow > 0)
        {
            using var glow = new SolidBrush(Color.FromArgb((int)(255 * style.Glow / 100f * 0.3f), item.StatusColor));
            g.FillEllipse(glow, RectangleF.Inflate(r, m.GlowPx * 0.4f, m.GlowPx * 0.4f));
        }

        using var brush = new SolidBrush(item.StatusColor);
        g.FillEllipse(brush, r);
    }

    /// <summary>A thermometer lying down: a bulb on the left and a tube filling to the value.</summary>
    private static void Thermometer(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        var bulb = r.Height;
        var tube = Math.Max(2f, r.Height * 0.46f);
        var tubeRect = new RectangleF(r.X + (bulb * 0.5f), r.Y + ((r.Height - tube) / 2), r.Width - (bulb * 0.5f), tube);
        using (var track = new SolidBrush(item.Track))
        using (var back = RoundedRect(tubeRect, tube / 2))
        {
            g.FillPath(track, back);
        }

        using var fill = new SolidBrush(item.VisualColor);
        var fraction = item.Fraction ?? 0;
        if (fraction > 0)
        {
            using var front = RoundedRect(new RectangleF(tubeRect.X, tubeRect.Y, Math.Max(tube, tubeRect.Width * fraction), tube), tube / 2);
            g.FillPath(fill, front);
        }

        g.FillEllipse(fill, r.X, r.Y, bulb, bulb);
    }

    /// <summary>The recent history as columns (the highest of each slice of the window).</summary>
    private static void Columns(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        using (var baseline = new Pen(item.Track, Math.Max(1f, m.S)))
        {
            g.DrawLine(baseline, r.Left, r.Bottom, r.Right, r.Bottom);
        }

        if (Window(item, style) is not { Count: >= 1 } window)
        {
            return;
        }

        var (lo, hi, start) = Range(item, window, style);
        var count = Math.Clamp((int)(r.Width / (4 * m.S)), 6, 40);
        var slice = style.GraphSeconds / (double)count;
        var width = (r.Width / count) - Math.Max(1f, m.S * 0.8f);
        using var brush = new SolidBrush(item.VisualColor);
        for (var i = 0; i < count; i++)
        {
            var from = start.AddSeconds(i * slice);
            var to = from.AddSeconds(slice);
            var values = window.Where(p => p.At >= from && p.At < to).Select(p => p.Value).ToList();
            if (values.Count == 0)
            {
                continue;
            }

            var h = (float)(Math.Clamp((values.Max() - lo) / (hi - lo), 0, 1) * r.Height);
            g.FillRectangle(brush, r.X + (i * (r.Width / count)), r.Bottom - Math.Max(1f, h), width, Math.Max(1f, h));
        }
    }

    /// <summary>The readings in the graph's window (the last <see cref="OverlayStyle.GraphSeconds"/>), or null without enough.</summary>
    private static List<(DateTime At, double Value)>? Window(Item item, OverlayStyle style)
    {
        var points = item.Points;
        if (points is null || points.Count < 1)
        {
            return null;
        }

        var start = points[^1].At.AddSeconds(-style.GraphSeconds);
        var window = points.Where(p => p.At >= start && !double.IsNaN(p.Value)).ToList();
        return window.Count > 0 ? window : null;
    }

    /// <summary>A chart's scale: from 0 to the full scale, or around the values; and where the window starts.</summary>
    private static (double Lo, double Hi, DateTime Start) Range(Item item, List<(DateTime At, double Value)> window, OverlayStyle style)
    {
        var start = window[^1].At.AddSeconds(-style.GraphSeconds);
        if (item.Max is { } max)
        {
            return (0, max, start);
        }

        var (lo, hi) = (window.Min(p => p.Value), window.Max(p => p.Value));
        var span = Math.Max(hi - lo, Math.Max(Math.Abs(hi) * 0.05, 1));
        var middle = (hi + lo) / 2;
        return (middle - (span * 0.6), middle + (span * 0.6), start);
    }

    private static void Ring(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m, float thickness)
    {
        (style, m) = (item.Style, item.M);
        thickness *= style.BarHeight / 100f;
        var arc = RectangleF.Inflate(r, -thickness / 2, -thickness / 2);
        if (arc.Width <= 1)
        {
            return;
        }

        using (var track = new Pen(item.Track, thickness))
        {
            g.DrawEllipse(track, arc);
        }

        var sweep = 360f * (item.Fraction ?? 0);
        if (sweep <= 0.5f)
        {
            return;
        }

        if (style.Glow > 0)
        {
            for (var k = 3; k >= 1; k--)
            {
                using var glow = new Pen(Color.FromArgb((int)(255 * style.Glow / 100f * 0.12f), item.VisualColor), thickness + (m.GlowPx * k / 3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(glow, arc, -90, sweep);
            }
        }

        using var pen = new Pen(item.VisualColor, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, arc, -90, sweep);
    }

    private static void Graph(Graphics g, RectangleF r, Item item, OverlayStyle style, Metrics m, bool area = true)
    {
        (style, m) = (item.Style, item.M);
        var line = m.GraphLine;
        using (var baseline = new Pen(item.Track, Math.Max(1f, m.S)))
        {
            g.DrawLine(baseline, r.Left, r.Bottom, r.Right, r.Bottom);
        }

        var points = item.Points;
        if (points is null || points.Count < 2)
        {
            return;
        }

        var end = points[^1].At;
        var start = end.AddSeconds(-style.GraphSeconds);
        var window = points.Where(p => p.At >= start && !double.IsNaN(p.Value)).ToList();
        if (window.Count < 2)
        {
            return;
        }

        double lo, hi;
        if (item.Max is { } max)
        {
            (lo, hi) = (0, max);
        }
        else
        {
            (lo, hi) = (window.Min(p => p.Value), window.Max(p => p.Value));
            var span = Math.Max(hi - lo, Math.Max(Math.Abs(hi) * 0.05, 1));
            var middle = (hi + lo) / 2;
            (lo, hi) = (middle - (span * 0.6), middle + (span * 0.6));
        }

        var inset = line;
        PointF At((DateTime At, double Value) p) => new(
            r.Left + (float)((p.At - start).TotalSeconds / style.GraphSeconds * r.Width),
            r.Bottom - inset - (float)(Math.Clamp((p.Value - lo) / (hi - lo), 0, 1) * (r.Height - (2 * inset))));
        var mapped = window.Select(At).ToArray();
        item.Gauge = new GaugeHit(r, "graph", item.Fraction ?? 0, line, mapped);

        if (area)
        {
            using var shape = new GraphicsPath();
            shape.AddLine(mapped[0].X, r.Bottom, mapped[0].X, mapped[0].Y);
            shape.AddLines(mapped);
            shape.AddLine(mapped[^1].X, mapped[^1].Y, mapped[^1].X, r.Bottom);
            shape.CloseFigure();
            using var fill = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(item.FillAlpha, item.VisualColor), Color.FromArgb(Math.Min(4, item.FillAlpha), item.VisualColor), LinearGradientMode.Vertical);
            g.FillPath(fill, shape);
        }

        if (style.Glow > 0)
        {
            using var glow = new Pen(Color.FromArgb((int)(255 * style.Glow / 100f * 0.25f), item.VisualColor), line + (m.GlowPx * 0.6f)) { LineJoin = LineJoin.Round };
            g.DrawLines(glow, mapped);
        }

        using (var pen = new Pen(item.VisualColor, line) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLines(pen, mapped);
        }

        var dot = line * 1.5f;
        using var brush = new SolidBrush(item.VisualColor);
        g.FillEllipse(brush, mapped[^1].X - dot, mapped[^1].Y - dot, 2 * dot, 2 * dot);
    }

    private static void DrawIcon(Graphics g, Item item, float x, float y, float size, OverlayStyle style, Metrics m)
    {
        (style, m) = (item.Style, item.M);
        if (item.Icon is null || !OverlayIcons.Paths.TryGetValue(item.Icon, out var data))
        {
            return;
        }

        using var path = SvgPath.Parse(data, size / 24f, new PointF(x, y));
        var stroke = Math.Max(1.2f, size / 12f);
        if (style.Outline)
        {
            using var outline = new Pen(Color.FromArgb(200, 0, 0, 0), stroke + Math.Max(2f, m.Px * 0.14f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(outline, path);
        }

        if (style.Glow > 0)
        {
            using var glow = new Pen(Color.FromArgb((int)(255 * style.Glow / 100f * 0.3f), item.IconColor), stroke + (m.GlowPx * 0.5f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(glow, path);
        }

        using var pen = new Pen(item.IconColor, stroke) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawPath(pen, path);
    }

    // ---- Text --------------------------------------------------------------------------------------------------

    private static Item MakeItem(OverlayReading reading, OverlaySource? source, OverlayStyle style, Metrics m, OverlayHistory? history, DateTime now, float valueScale)
    {
        // The reading's own look over the panel's: its style and sizes (the panel's places and gaps stay).
        var look = reading.Look;
        if (look is not null)
        {
            style = style with
            {
                Font = look.Font ?? style.Font,
                ValueScale = style.ValueScale * (look.ValueSize ?? 100) / 100,
                Bold = look.Bold ?? style.Bold,
                ValueColor = look.ValueColor ?? style.ValueColor,
                LabelColor = look.LabelColor ?? style.LabelColor,
                UnitColor = look.UnitColor ?? style.UnitColor,
                ShowLabels = look.ShowLabel ?? style.ShowLabels,
                ShowUnits = look.ShowUnit ?? style.ShowUnits,
                ValueAlign = look.Align ?? style.ValueAlign,
                ValueVertical = look.Vertical ?? style.ValueVertical,
                BarStyle = look.BarStyle ?? style.BarStyle,
                BarHeight = style.BarHeight * (look.Thickness ?? 100) / 100,
            };
            m = new Metrics(style, Family(style.Font), m.S, (look.LabelSize ?? 100) / 100f, (look.VisualSize ?? 100) / 100f, (look.Thickness ?? 100) / 100f);
        }

        var (value, unit) = source is null ? ("—", string.Empty) : OverlayStyles.Shown(source, reading);
        if (source is { Loading: true })
        {
            // Not read yet: a loading mark where its value goes.
            value = OverlayStyles.LoadingMark(now);
        }

        var label = style.ShowLabels ? (string.IsNullOrWhiteSpace(reading.Label) ? source?.Label ?? reading.Source : reading.Label!) : null;
        var level = OverlayStyles.Level(reading, source?.Value, reading.Max ?? source?.Max);
        var levelColor = level == 2 ? Parse(style.CriticalColor) : level == 1 ? Parse(style.WarningColor) : (Color?)null;
        var max = OverlayStyles.MaxOf(reading, source);
        var visual = OverlayStyles.VisualOf(reading);

        // A dial, bar or thermometer needs a full scale (a status light only limits): without one of its own it is drawn
        // against an automatic one (see AutoMax), and only a text reading falls back to its value. History needs a number.
        var scaled = OverlayStyles.Dials.Contains(visual) && visual != "status" || OverlayStyles.Unders.Contains(visual);
        if (scaled && max is not > 0)
        {
            max = OverlayStyles.AutoMax(source);
        }

        if ((scaled && source?.Text is not null) || (OverlayStyles.NeedsHistory(visual) && source?.Text is not null) || (visual == "status" && source?.Text is not null))
        {
            visual = "none";
        }

        var points = OverlayStyles.NeedsHistory(visual) && history is not null ? history.Of(reading.Source) : null;
        var extra = visual is "stats" or "trend" && source is not null ? Extra(visual, source, reading, points, style) : null;

        // The value, its unit and the text after it keep room for the widest they can read, so the panel keeps its size
        // whatever the values (a unit is kept even while there is no reading: "—" for a size has none).
        var (wideValues, wideUnits) = source is null ? ([], []) : OverlayStyles.Widest(source, reading);
        var wideExtra = visual is "stats" or "trend" && source is { Text: null } ? ExtraRoom(visual, source, reading) : null;

        float? fraction = max is > 0 && source?.Value is { } v ? (float)Math.Clamp(v / max.Value, 0, 1) : null;
        var valuePx = m.ValuePx * valueScale;

        // A percentage's sign belongs to its number ("50%"): in the value's color and weight, close to it. Other units,
        // labels too, are kept readable on the panel (a dim color on a dark panel over a light desktop disappeared).
        var percent = unit == "%";
        var valueColor = levelColor ?? Parse((look?.ValueColor is null ? reading.Color : null) ?? style.ValueColor);
        var labelColor = Parse(OverlayStyles.Readable(style.LabelColor, style));
        var track = Color.FromArgb((int)Math.Round(255 * (look?.TrackOpacity ?? 22) / 100.0), look?.TrackColor is { } trackColor ? Parse(trackColor) : labelColor);
        return new Item(
            label is null ? null : Text(Caps(label, style.Caps), m.Family, style.Caps ? FontStyle.Bold : FontStyle.Regular, m.LabelPx, tracking: Tracking(style.Caps)),
            Text(value, m.Family, style.Bold ? FontStyle.Bold : FontStyle.Regular, valuePx, even: true, reserve: wideValues, fit: source is { Text: not null, Room.Count: > 0 }),
            style.ShowUnits && (unit.Length > 0 || wideUnits.Count > 0)
                ? percent
                    ? Text(unit, m.Family, style.Bold ? FontStyle.Bold : FontStyle.Regular, valuePx * PercentSize, reserve: wideUnits)
                    : Text(unit, m.Family, FontStyle.Regular, m.UnitPx * valueScale, reserve: wideUnits)
                : null,
            valueColor,
            levelColor ?? Parse(reading.Color ?? style.Accent),
            levelColor ?? Parse(style.IconColor ?? style.Accent),
            labelColor,
            percent ? valueColor : Parse(OverlayStyles.Readable(style.UnitColor, style)),
            level,
            visual,
            fraction,
            max,
            style.Icons ? OverlayStyles.IconOf(reading, source) : null,
            points,
            LineHeight(m.Family, valuePx),
            percent ? valuePx * PercentGap : m.UnitGap * valueScale,
            // A status light without a reading is neither OK nor a problem.
            source?.Value is null ? Parse(style.UnitColor) : level == 2 ? Parse(style.CriticalColor) : level == 1 ? Parse(style.WarningColor) : Parse(style.OkColor),
            extra is null && wideExtra is null ? null : Text(extra ?? string.Empty, m.Family, FontStyle.Regular, m.UnitPx * valueScale, even: true, reserve: wideExtra),
            style,
            m,
            track,
            (int)Math.Round(255 * (look?.FillOpacity ?? 43) / 100.0));
    }

    /// <summary>A percentage sign's size against its number's, and the space before it.</summary>
    private const float PercentSize = 0.7f;

    private const float PercentGap = 0.03f;

    /// <summary>The widest a reading's lowest, average and highest (or its trend) can read.</summary>
    private static List<string> ExtraRoom(string visual, OverlaySource source, OverlayReading reading)
    {
        var (values, _) = OverlayStyles.Widest(source, reading, raw: true);
        return visual == "stats"
            ? [.. values.Select(v => $"lo {v} · avg {v} · hi {v}")]
            : [.. values.SelectMany(v => new[] { "▲ " + v, "▼ " + v }), "● 0"];
    }

    /// <summary>
    /// The text after the value: its lowest, average and highest over the graph's window ("lo 41 · avg 52 · hi 78"), or its
    /// trend over the window, an arrow and the change ("▲ 12"). Null without readings to tell from.
    /// </summary>
    private static string? Extra(string visual, OverlaySource source, OverlayReading reading, IReadOnlyList<(DateTime At, double Value)>? points, OverlayStyle style)
    {
        if (points is null || points.Count == 0)
        {
            return null;
        }

        var start = points[^1].At.AddSeconds(-style.GraphSeconds);
        var values = points.Where(p => p.At >= start && !double.IsNaN(p.Value)).Select(p => p.Value).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        string F(double v) => OverlayStyles.Format(source with { Value = v, Max = null }, reading.Decimals).Value;
        if (visual == "stats")
        {
            // Words, not arrows: small arrows read as ones ("↓84" as "184").
            return $"lo {F(values.Min())} · avg {F(values.Average())} · hi {F(values.Max())}";
        }

        var change = values[^1] - values[0];
        var still = Math.Abs(change) < Math.Max(Math.Abs(values.Max() - values.Min()) * 0.05, 1e-9);
        return still ? "● 0" : $"{(change > 0 ? "▲" : "▼")} {F(Math.Abs(change))}";
    }

    /// <summary>
    /// A value, its unit and the text after it, as one, in its space (<paramref name="space"/> wide from
    /// <paramref name="x"/>: never narrower than the room it keeps), on the left, in the middle or on the right of it.
    /// </summary>
    private static void DrawValue(Graphics g, Item item, float x, float space, float y, OverlayStyle style, Metrics m, string? align = null)
    {
        (style, m) = (item.Style, item.M);
        var unit = item.Unit is { Width: > 0 } ? item.UnitGap + item.Unit.Width : 0;
        var extra = item.Extra is { Width: > 0 } ? (item.UnitGap * 2.5f) + item.Extra.Width : 0;
        var free = Math.Max(0, space - (item.Value.Width + unit + extra));
        x += (align ?? style.ValueAlign) switch
        {
            "left" => 0,
            "right" => free,
            _ => free / 2,
        };
        Draw(g, item.Value, x, y, item.ValueColor, style.Outline, style.Glow, m);
        var after = x + item.Value.Width;
        if (unit > 0)
        {
            // Units sit on the value's baseline.
            Draw(g, item.Unit!, after + item.UnitGap, y + (item.Value.Ascent - item.Unit!.Ascent), item.UnitColor, style.Outline, 0, m);
            after += unit;
        }

        if (extra > 0)
        {
            Draw(g, item.Extra!, after + (item.UnitGap * 2.5f), y + (item.Value.Ascent - item.Extra!.Ascent), item.LabelColor, style.Outline, 0, m);
        }
    }

    private static void Draw(Graphics g, Shape shape, float x, float y, Color color, bool outline, int glow, Metrics? m)
    {
        using var path = (GraphicsPath)shape.Path.Clone();
        using var move = new Matrix();
        move.Translate(x, y);
        path.Transform(move);
        var px = m?.Px ?? shape.Size;
        if (glow > 0 && m is not null)
        {
            Glow(g, path, color, glow, m.GlowPx);
        }

        if (outline)
        {
            using var pen = new Pen(Color.FromArgb(215, 0, 0, 0), Math.Max(2f, px * 0.17f)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }

        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>A soft glow: the path stroked wide and faint, then narrower and stronger.</summary>
    private static void Glow(Graphics g, GraphicsPath path, Color color, int strength, float width)
    {
        const int Passes = 4;
        for (var k = Passes; k >= 1; k--)
        {
            var alpha = (int)Math.Clamp(255 * strength / 100f * 0.14f * (Passes + 1 - k) / Passes, 0, 255);
            using var pen = new Pen(Color.FromArgb(alpha, color), Math.Max(1f, width * k / Passes)) { LineJoin = LineJoin.Round };
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Small capitals (spaced by <see cref="Tracking"/>).</summary>
    private static string Caps(string text, bool caps) => caps ? text.ToUpperInvariant() : text;

    /// <summary>
    /// Space after each letter of spaced capitals, against the text size: 0.08, as the editor's letter-spacing (spaces
    /// between the letters would be a whole letter wide in a monospaced font).
    /// </summary>
    private static float Tracking(bool caps) => caps ? 0.08f : 0;

    private static readonly Lock MeasureLock = new();
    private static readonly Bitmap MeasureBitmap = new(1, 1);
    private static readonly Graphics Measurer = Graphics.FromImage(MeasureBitmap);

    /// <param name="even">
    /// A number: its width is that of the same text with every digit a 0, so a value keeps its width as its digits
    /// change (the panel does not jump from 22 to 26).
    /// </param>
    /// <param name="reserve">
    /// Texts whose room is kept as well (the widest a reading's value can read, so 99 % to 100 % or 9.9 to 12.4 ms does
    /// not widen the panel); the shape is drawn as <paramref name="text"/>, its <see cref="Shape.Room"/> is the widest.
    /// </param>
    /// <param name="fit">A text longer than its room is shortened with "…" to fit it (an app's name in its space).</param>
    /// <param name="tracking">Space after each letter, against <paramref name="px"/> (as CSS letter-spacing, the last letter's too).</param>
    private static Shape Text(string text, FontFamily family, FontStyle style, float px, bool even = false, IReadOnlyList<string>? reserve = null, bool fit = false, float tracking = 0)
    {
        var fontStyle = family.IsStyleAvailable(style) ? style : FontStyle.Regular;
        var path = new GraphicsPath();
        using var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        var ascent = px * family.GetCellAscent(fontStyle) / family.GetEmHeight(fontStyle);
        if (text.Length == 0 && reserve is not { Count: > 0 })
        {
            return new Shape(path, 0, ascent, px);
        }

        float width, room;
        lock (MeasureLock)
        {
            using var font = new Font(family, px, fontStyle, GraphicsUnit.Pixel);
            float Measure(string t)
            {
                var key = (family.Name, fontStyle, px, t);
                if (!Widths.TryGetValue(key, out var w))
                {
                    if (Widths.Count > 4096)
                    {
                        Widths.Clear();
                    }

                    Widths[key] = w = t.Length == 0 ? 0 : Measurer.MeasureString(t, font, PointF.Empty, format).Width;
                }

                return w;
            }

            if (tracking > 0)
            {
                // Letter by letter, each followed by the spacing.
                var at = 0f;
                foreach (var letter in text.EnumerateRunes())
                {
                    var one = letter.ToString();
                    if (one != " ")
                    {
                        path.AddString(one, family, (int)fontStyle, px, new PointF(at, 0), format);
                    }

                    at += Measure(one) + (tracking * px);
                }

                return new Shape(path, at, ascent, px);
            }

            width = Measure(text);
            if (even)
            {
                width = Math.Max(width, Measure(OverlayStyles.Zeros(text)));
            }

            var kept = 0f;
            foreach (var wide in reserve ?? [])
            {
                kept = Math.Max(kept, Measure(OverlayStyles.Zeros(wide)));
            }

            if (fit && kept > 0 && width > kept)
            {
                // The longest start of it that fits with "…".
                var length = text.Length - 1;
                while (length > 0 && Measure(text[..length].TrimEnd() + "…") > kept)
                {
                    length--;
                }

                text = text[..length].TrimEnd() + "…";
                width = Measure(text);
            }

            room = Math.Max(width, kept);
        }

        if (text.Length > 0)
        {
            path.AddString(text, family, (int)fontStyle, px, PointF.Empty, format);
        }

        return new Shape(path, text.Length == 0 ? 0 : width + (px * 0.02f), ascent, px) { Room = room + (px * 0.02f) };
    }

    /// <summary>Measured widths by font and text (the same texts are measured every time a panel is drawn).</summary>
    private static readonly Dictionary<(string Family, FontStyle Style, float Px, string Text), float> Widths = [];

    private static float LineHeight(FontFamily family, float px) => px * family.GetLineSpacing(FontStyle.Regular) / family.GetEmHeight(FontStyle.Regular);

    private static FontFamily Family(string name) => Families.GetOrAdd(name, n =>
    {
        try
        {
            return new FontFamily(n);
        }
        catch (ArgumentException)
        {
            return new FontFamily("Segoe UI");
        }
    });

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Every size for a panel, from its style and the monitor's scaling (the preview computes the same).</summary>
    internal sealed class Metrics
    {
        public Metrics(OverlayStyle style, FontFamily family, float s, float labelScale = 1, float visualScale = 1, float lineScale = 1)
        {
            Family = family;
            S = s;
            Px = (float)style.Size * s;
            var spacing = style.Spacing / 100f;
            ValuePx = Px * style.ValueScale / 100f;
            LabelPx = Px * (style.Caps ? 0.74f : 0.85f) * labelScale;
            TitlePx = Px * (style.Caps ? 0.74f : 0.8f);
            UnitPx = ValuePx * 0.62f;
            UnitGap = ValuePx * 0.12f;
            Pad = style.Padding * s;
            RowGap = Px * 0.3f * spacing;
            ColGap = Px * 0.6f * spacing;
            Line = LineHeight(family, Px);
            ValueLine = LineHeight(family, ValuePx);
            LabelLine = LineHeight(family, LabelPx);
            TitleLine = LineHeight(family, TitlePx);
            IconSize = Math.Max(LabelLine, Line * 0.9f);
            TileIcon = LabelLine * 1.05f;
            BarH = Math.Max(2f, Px * 0.22f * style.BarHeight / 100f);
            BarGap = Px * 0.18f;
            VisualScale = visualScale;
            GraphLine = Math.Max(1.5f * s, Px * 0.1f) * lineScale;
            InlineRing = Math.Max(ValueLine * 0.85f, Line) * visualScale;
            InlineGraphWidth = Px * 4.5f * visualScale;
            LineGraphWidth = Px * 3.5f * visualScale;
            TileGraphHeight = Px * 1.9f * visualScale;
            TilePad = Px * 0.6f * Math.Min(1.5f, spacing);
            TileGap = Px * 0.5f * spacing;
            GlowPx = style.Glow > 0 ? Px * 0.55f * (0.5f + (style.Glow / 200f)) : 0;
            ShadowBlur = style.Shadow > 0 ? (6 + (14 * style.Shadow / 100f)) * s : 0;
            ShadowOffset = ShadowBlur * 0.35f;
            // Room around the panel for its shadow, and for its glow when a border glows (the glow of text, dials and
            // icons stays within the padding): a borderless panel's box is its picture, so a panel the size of its
            // screen fills it.
            Margin = (float)Math.Ceiling(Math.Max(ShadowBlur + ShadowOffset * 0.5f, style.Border is null ? 0 : GlowPx));
        }

        public FontFamily Family { get; }

        public float S { get; }

        public float Px { get; }

        public float ValuePx { get; }

        public float LabelPx { get; }

        public float TitlePx { get; }

        public float UnitPx { get; }

        public float UnitGap { get; }

        public float Pad { get; }

        public float RowGap { get; }

        public float ColGap { get; }

        public float Line { get; }

        public float ValueLine { get; }

        public float LabelLine { get; }

        public float TitleLine { get; }

        public float IconSize { get; }

        public float TileIcon { get; }

        public float BarH { get; }

        public float BarGap { get; }

        public float InlineRing { get; }

        /// <summary>A reading's dial and chart size against the panel's (its <see cref="OverlayReadingLook.VisualSize"/>).</summary>
        public float VisualScale { get; }

        /// <summary>A graph's line width.</summary>
        public float GraphLine { get; }

        public float InlineGraphWidth { get; }

        public float LineGraphWidth { get; }

        public float TileGraphHeight { get; }

        public float TilePad { get; }

        public float TileGap { get; }

        public float GlowPx { get; }

        public float ShadowBlur { get; }

        public float ShadowOffset { get; }

        public float Margin { get; }
    }

    /// <summary>Text as an outline at the origin, with its width, ascent and size.</summary>
    private sealed record Shape(GraphicsPath Path, float Width, float Ascent, float Size) : IDisposable
    {
        /// <summary>The room it takes in a layout: its width, or more when it keeps room for a wider reading.</summary>
        public float Room { get; init; } = Width;

        public void Dispose() => Path.Dispose();
    }

    private sealed record Item(
        Shape? Label,
        Shape Value,
        Shape? Unit,
        Color ValueColor,
        Color VisualColor,
        Color IconColor,
        Color LabelColor,
        Color UnitColor,
        int Level,
        string Visual,
        float? Fraction,
        double? Max,
        string? Icon,
        IReadOnlyList<(DateTime At, double Value)>? Points,
        float ValueLine,
        float UnitGap,
        Color StatusColor,
        Shape? Extra,
        OverlayStyle Style,
        Metrics M,
        Color Track,
        int FillAlpha) : IDisposable
    {
        /// <summary>Where it was drawn, in the bitmap's pixels.</summary>
        public RectangleF Bounds { get; set; }

        /// <summary>Its gauge as drawn (a ring, arc or speedometer), for a character to play on; null for other visuals.</summary>
        public GaugeHit? Gauge { get; set; }

        /// <summary>
        /// The room the value, unit and extra text take (each keeps room for the widest it can read; the number is drawn
        /// at the end of its room, so its unit stays beside it).
        /// </summary>
        public float ValueWidth(Metrics m) => Value.Room + (Unit is null ? 0 : UnitGap + Unit.Room) + (Extra is null ? 0 : (UnitGap * 2.5f) + Extra.Room);

        public void Dispose()
        {
            Label?.Dispose();
            Value.Dispose();
            Unit?.Dispose();
            Extra?.Dispose();
        }
    }
}
