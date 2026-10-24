using System.Drawing;
using System.Drawing.Drawing2D;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// What is drawn behind each tile (<see cref="OverlayStyle.Backdrop"/>), fitted to the tile and to its dial wherever the
/// tile is and whatever its size: drawn over the tile's color and under its readings. Sizes are in units of the panel's
/// scale (<c>m.S</c>); the editor's <c>TileBackdrop</c> draws the same shapes at the same sizes.
/// </summary>
internal static partial class OverlayRenderer
{
    /// <summary>The tile's own shape: cut corners for chevron, rounded otherwise.</summary>
    private static GraphicsPath TileShape(RectangleF tile, float radius, string? backdrop, Metrics m) =>
        backdrop == "chevron" ? Chevron(tile, ChevronCut(tile, m)) : RoundedRect(tile, radius);

    private static float ChevronCut(RectangleF tile, Metrics m) => Math.Min(10 * m.S, Math.Min(tile.Width, tile.Height) / 3);

    private static GraphicsPath Chevron(RectangleF r, float cut)
    {
        var path = new GraphicsPath();
        path.AddPolygon([
            new PointF(r.X, r.Y), new PointF(r.Right - cut, r.Y), new PointF(r.Right, r.Y + cut),
            new PointF(r.Right, r.Bottom), new PointF(r.X + cut, r.Bottom), new PointF(r.X, r.Bottom - cut),
        ]);
        return path;
    }

    private static RectangleF Inset(RectangleF r, float by) => new(r.X + by, r.Y + by, Math.Max(0, r.Width - (2 * by)), Math.Max(0, r.Height - (2 * by)));

    private static Color Alpha(Color color, int percent) => Color.FromArgb(Math.Clamp(percent * 255 / 100, 0, 255), color.R, color.G, color.B);

    /// <summary>
    /// The backdrop of one tile, over its color and under its readings. <paramref name="underline"/> is where the line
    /// under the label goes (none without a label).
    /// </summary>
    private static void DrawBackdrop(Graphics g, RectangleF tile, GraphicsPath shape, float radius, float? underline, bool dial, Color level, OverlayStyle style, Metrics m)
    {
        var s = m.S;
        var accent = Parse(style.Accent);
        switch (style.Backdrop)
        {
            case "plate":
            {
                // A metal plate: a light top edge, a dark bottom edge and four screws.
                using (var light = new Pen(Alpha(Color.White, 22), 1 * s))
                {
                    g.DrawLine(light, tile.X + radius, tile.Y + (0.5f * s), tile.Right - radius, tile.Y + (0.5f * s));
                }

                using (var dark = new Pen(Alpha(Color.Black, 35), 1.5f * s))
                {
                    g.DrawLine(dark, tile.X + radius, tile.Bottom - (0.75f * s), tile.Right - radius, tile.Bottom - (0.75f * s));
                }

                if (tile.Width > 30 * s && tile.Height > 24 * s)
                {
                    using var head = new SolidBrush(Alpha(Color.White, 18));
                    using var rim = new Pen(Alpha(Color.Black, 40), 0.8f * s);
                    using var slot = new Pen(Alpha(Color.Black, 45), 0.7f * s);
                    var r = 1.9f * s;
                    foreach (var (cx, cy) in new[] { (tile.X + (6 * s), tile.Y + (6 * s)), (tile.Right - (6 * s), tile.Y + (6 * s)), (tile.Right - (6 * s), tile.Bottom - (6 * s)), (tile.X + (6 * s), tile.Bottom - (6 * s)) })
                    {
                        g.FillEllipse(head, cx - r, cy - r, 2 * r, 2 * r);
                        g.DrawEllipse(rim, cx - r, cy - r, 2 * r, 2 * r);
                        g.DrawLine(slot, cx - (0.85f * s), cy + (0.85f * s), cx + (0.85f * s), cy - (0.85f * s));
                    }
                }

                break;
            }

            case "hud":
            {
                // Corner brackets in the accent, and a short line under the label.
                var arm = Math.Min(9 * s, Math.Min(tile.Width, tile.Height) / 3);
                var t = 1.6f * s;
                var h = t / 2;
                using var pen = new Pen(Alpha(accent, 85), t) { LineJoin = LineJoin.Miter };
                g.DrawLines(pen, [new PointF(tile.X + h, tile.Y + arm), new PointF(tile.X + h, tile.Y + h), new PointF(tile.X + arm, tile.Y + h)]);
                g.DrawLines(pen, [new PointF(tile.Right - arm, tile.Y + h), new PointF(tile.Right - h, tile.Y + h), new PointF(tile.Right - h, tile.Y + arm)]);
                g.DrawLines(pen, [new PointF(tile.Right - h, tile.Bottom - arm), new PointF(tile.Right - h, tile.Bottom - h), new PointF(tile.Right - arm, tile.Bottom - h)]);
                g.DrawLines(pen, [new PointF(tile.X + arm, tile.Bottom - h), new PointF(tile.X + h, tile.Bottom - h), new PointF(tile.X + h, tile.Bottom - arm)]);
                if (underline is { } y)
                {
                    using var line = new Pen(Alpha(accent, 35), 1 * s);
                    g.DrawLine(line, tile.X + m.TilePad, y, tile.X + m.TilePad + ((tile.Width - (2 * m.TilePad)) * 0.6f), y);
                }

                break;
            }

            case "bezel" when !dial:
            {
                // A double frame (a dial gets its own ring instead: DrawBezel).
                using (var outer = new Pen(Alpha(Color.White, 14), 1 * s))
                using (var path = RoundedRect(Inset(tile, 0.5f * s), Math.Max(0, radius - (0.5f * s))))
                {
                    g.DrawPath(outer, path);
                }

                using (var inner = new Pen(Alpha(Color.Black, 35), 1 * s))
                using (var path = RoundedRect(Inset(tile, 3.5f * s), Math.Max(0, radius - (3.5f * s))))
                {
                    g.DrawPath(inner, path);
                }

                break;
            }

            case "socket":
            {
                // A recess: darker, with a dark top edge turning light at the bottom.
                using (var shade = new SolidBrush(Alpha(Color.Black, 16)))
                {
                    g.FillPath(shade, shape);
                }

                var inset = Inset(tile, 0.75f * s);
                using var edge = new LinearGradientBrush(new RectangleF(inset.X, inset.Y - 1, inset.Width, inset.Height + 2), Alpha(Color.Black, 55), Alpha(Color.White, 14), LinearGradientMode.Vertical);
                using var pen = new Pen(edge, 1.5f * s);
                using var path = RoundedRect(inset, Math.Max(0, radius - (0.75f * s)));
                g.DrawPath(pen, path);
                break;
            }

            case "chevron":
            {
                // Cut corners outlined in the accent, a tab in the top one.
                var cut = ChevronCut(tile, m);
                using (var pen = new Pen(Alpha(accent, 55), 1.2f * s))
                using (var path = Chevron(Inset(tile, 0.6f * s), Math.Max(0, cut - (0.25f * s))))
                {
                    g.DrawPath(pen, path);
                }

                if (cut > 3 * s)
                {
                    using var tab = new SolidBrush(Alpha(accent, 90));
                    g.FillPolygon(tab, [new PointF(tile.Right - cut + (2.5f * s), tile.Y), new PointF(tile.Right, tile.Y), new PointF(tile.Right, tile.Y + cut - (2.5f * s))]);
                }

                break;
            }

            case "glass":
            {
                // Frosted: light at the top fading down, a fine light edge and a highlight along the top.
                using (var frost = new LinearGradientBrush(new RectangleF(tile.X, tile.Y - 1, tile.Width, tile.Height + 2), Alpha(Color.White, 14), Alpha(Color.White, 2), LinearGradientMode.Vertical))
                {
                    g.FillPath(frost, shape);
                }

                using (var edge = new Pen(Alpha(Color.White, 22), 1 * s))
                using (var path = RoundedRect(Inset(tile, 0.5f * s), Math.Max(0, radius - (0.5f * s))))
                {
                    g.DrawPath(edge, path);
                }

                using (var highlight = new Pen(Alpha(Color.White, 45), 1 * s))
                {
                    g.DrawLine(highlight, tile.X + radius, tile.Y + (1.5f * s), tile.Right - radius, tile.Y + (1.5f * s));
                }

                break;
            }

            case "neon":
            {
                // A glowing outline in the accent (in the warning or critical color past a limit).
                var color = level.A > 0 ? level : accent;
                using var path = RoundedRect(Inset(tile, 3 * s), Math.Max(0, radius - (3 * s)));
                foreach (var (width, percent) in new[] { (6f, 10), (3.5f, 22), (1.4f, 95) })
                {
                    using var pen = new Pen(Alpha(color, percent), width * s);
                    g.DrawPath(pen, path);
                }

                break;
            }

            case "blueprint":
            {
                // A fine grid in the accent, in a frame.
                var grid = 10 * s;
                var width = Math.Max(1, 0.8f * s);
                var state = g.Save();
                g.SetClip(shape, CombineMode.Intersect);
                using (var pen = new Pen(Alpha(accent, 12), width))
                {
                    for (var x = tile.X + (width / 2); x < tile.Right; x += grid)
                    {
                        g.DrawLine(pen, x, tile.Y, x, tile.Bottom);
                    }

                    for (var y = tile.Y + (width / 2); y < tile.Bottom; y += grid)
                    {
                        g.DrawLine(pen, tile.X, y, tile.Right, y);
                    }
                }

                g.Restore(state);
                using var frame = new Pen(Alpha(accent, 45), 1 * s);
                using var path = RoundedRect(Inset(tile, 0.5f * s), Math.Max(0, radius - (0.5f * s)));
                g.DrawPath(frame, path);
                break;
            }
        }
    }

    /// <summary>
    /// A dial's bezel (backdrop "bezel"): a band behind its track in the tiles' color, and a fine ring in the accent
    /// around it, kept inside the tile's padding.
    /// </summary>
    private static void DrawBezel(Graphics g, RectangleF dial, float thickness, OverlayStyle style, Metrics m, Color tint)
    {
        var d = dial.Width;
        var cx = dial.X + (d / 2);
        var cy = dial.Y + (d / 2);
        var outer = (d / 2) + Math.Min((0.35f * thickness) + m.S, Math.Max(0, m.TilePad - (2.5f * m.S)));
        var inner = Math.Max(0, (d / 2) - (1.35f * thickness));
        var band = (outer + inner) / 2;
        using (var pen = new Pen(Alpha(tint, Math.Clamp(style.TileOpacity * 5 / 2, 10, 40)), outer - inner))
        {
            g.DrawEllipse(pen, cx - band, cy - band, 2 * band, 2 * band);
        }

        var hair = outer + (1 * m.S);
        using var line = new Pen(Alpha(Parse(style.Accent), 45), Math.Max(1, 0.9f * m.S));
        g.DrawEllipse(line, cx - hair, cy - hair, 2 * hair, 2 * hair);
    }
}
