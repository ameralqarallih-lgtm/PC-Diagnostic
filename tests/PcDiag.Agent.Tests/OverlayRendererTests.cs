using System.Drawing;
using PcDiag.Agent.Overlay;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Tests;

public sealed class OverlayRendererTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Layouts = ["rows", "tiles", "line"];

    private static readonly Dictionary<string, OverlaySource> Sources = new(StringComparer.Ordinal)
    {
        ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 52.4),
        ["cpu.temp"] = new("cpu.temp", "CPU temp", "Processor", "°C", 91),
        ["gpu.a.usage"] = new("gpu.a.usage", "GPU", "RTX 4090", "%", 97),
        ["gpu.a.temp"] = new("gpu.a.temp", "GPU temp", "RTX 4090", "°C", 64),
        ["gpu.a.vram"] = new("gpu.a.vram", "VRAM", "RTX 4090", "B", 3.4 * (1L << 30)),
        ["memory.usage"] = new("memory.usage", "RAM", "Memory", "%", 38),
        ["net.e.down"] = new("net.e.down", "Download", "Ethernet", "B/s", 19.5 * (1L << 20)),
        ["time.clock"] = new("time.clock", "Time", "This PC", string.Empty, null, "01:53"),
    };

    private static OverlayPanel Panel(string layout = "rows", OverlayStyle? style = null) => new(
        "p1",
        "System",
        1,
        0,
        layout,
        [
            new("cpu.usage", Bar: true),
            new("cpu.temp", Warning: 80, Critical: 90),
            new("gpu.a.vram"),
            new("net.e.down", Label: "Down"),
            new("time.clock"),
            new("missing.value"),
        ],
        style ?? new OverlayStyle());

    /// <summary>A panel that uses every visual: bars, rings and graphs.</summary>
    private static OverlayPanel Showcase(string layout, OverlayStyle style, string? title = "Performance") => new(
        "p2",
        title,
        1,
        0,
        layout,
        [
            new("gpu.a.usage", Visual: "ring", Warning: 95),
            new("cpu.usage", Visual: "graph"),
            new("gpu.a.temp", Visual: "bar", Max: 100, Warning: 80, Critical: 90),
            new("memory.usage", Visual: "bar"),
            new("net.e.down", Label: "Down", Visual: "graph"),
            new("time.clock", Icon: "clock"),
        ],
        style,
        Columns: 3);

    private static OverlayHistory History()
    {
        var history = new OverlayHistory();
        for (var i = 60; i >= 0; i--)
        {
            var at = Now.AddSeconds(-i);
            history.Add("cpu.usage", at, 45 + (25 * Math.Sin(i / 6.0)) + (i % 7));
            history.Add("net.e.down", at, (12 + (8 * Math.Cos(i / 4.0))) * (1L << 20));
        }

        return history;
    }

    [Fact]
    public void A_panel_is_drawn_bigger_on_a_scaled_monitor_and_wider_on_one_line()
    {
        using var normal = OverlayRenderer.Render(Panel(), Sources, 1);
        using var scaled = OverlayRenderer.Render(Panel(), Sources, 1.5);
        using var line = OverlayRenderer.Render(Panel("line"), Sources, 1);

        normal.Width.ShouldBeGreaterThan(40);
        normal.Height.ShouldBeGreaterThan(80);
        scaled.Width.ShouldBeGreaterThan((int)(normal.Width * 1.35));
        line.Width.ShouldBeGreaterThan(normal.Width);
        line.Height.ShouldBeLessThan(normal.Height);

        // The corner outside the rounded background is transparent; the middle is painted.
        normal.GetPixel(0, 0).A.ShouldBe((byte)0);
        normal.GetPixel(normal.Width / 2, normal.Height / 2).A.ShouldBeGreaterThan((byte)0);
    }

    [Fact]
    public void A_transparent_style_has_no_background_and_colors_parse()
    {
        using var bitmap = OverlayRenderer.Render(Panel(style: OverlayStyles.Presets["minimal"]), Sources, 1);
        bitmap.GetPixel(1, 1).A.ShouldBe((byte)0);
        OverlayRenderer.Parse("#38BDF8").ShouldBe(Color.FromArgb(56, 189, 248));
        OverlayRenderer.Parse("nope").ShouldBe(Color.White);
    }

    [Fact]
    public void Tiles_are_a_grid_of_columns_and_a_shadow_leaves_room_around_the_panel()
    {
        var style = OverlayStyles.Presets["aurora"];
        using var three = OverlayRenderer.Render(Showcase("tiles", style), Sources, 1, History(), Now);
        using var two = OverlayRenderer.Render(Showcase("tiles", style) with { Columns = 2 }, Sources, 1, History(), Now);

        two.Width.ShouldBeLessThan(three.Width);
        two.Height.ShouldBeGreaterThan(three.Height);

        // The shadow is soft: transparent at the very corner, faintly dark just under the panel's bottom edge.
        three.GetPixel(0, 0).A.ShouldBe((byte)0);
        var x = three.Width / 2;
        var edge = Enumerable.Range(0, three.Height).Last(y => three.GetPixel(x, y).A >= 200);
        var under = three.GetPixel(x, edge + 4);
        under.A.ShouldBeGreaterThan((byte)0);
        under.A.ShouldBeLessThan((byte)160);
    }

    [Fact]
    public void A_moving_background_drawn_as_layers_looks_the_same_as_one_drawing_and_every_motion_moves_inside_the_panel()
    {
        var still = OverlayStyles.Presets["aurora"] with { Shadow = 0 };
        var panel = Showcase("tiles", still);

        // Shimmer at its first moment is outside the panel: the layers composed match the panel drawn in one go.
        using var once = OverlayRenderer.Render(panel, Sources, 1.5, History(), Now);
        var (layered, _, layers) = OverlayRenderer.RenderLayers(panel with { Style = still with { Motion = "shimmer" } }, Sources, 1.5, History(), Now, null, null, 0, keepLayers: true);
        using (layered)
        using (layers)
        {
            layers.ShouldNotBeNull();
            layered.Size.ShouldBe(once.Size);
            var worst = 0;
            for (var x = 0; x < once.Width; x += 2)
            {
                for (var y = 0; y < once.Height; y += 2)
                {
                    var a = once.GetPixel(x, y);
                    var b = layered.GetPixel(x, y);
                    worst = Math.Max(worst, new[] { Math.Abs(a.A - b.A), Math.Abs(a.R - b.R), Math.Abs(a.G - b.G), Math.Abs(a.B - b.B) }.Max());
                }
            }

            // Within rounding: translucent text composed over a translucent background in 8-bit layers.
            worst.ShouldBeLessThanOrEqualTo(3);
        }

        // Each motion moves (two moments differ), and only inside the panel (its corner, outside the rounded box, stays clear).
        foreach (var motion in OverlayStyles.Motions.Where(m => m != "none"))
        {
            var moving = panel with { Style = still with { Motion = motion } };
            var (first, hits, kept) = OverlayRenderer.RenderLayers(moving, Sources, 1.5, History(), Now, null, null, 0, keepLayers: true);
            using (first)
            using (kept)
            using (var later = new Bitmap(first.Width, first.Height))
            {
                kept!.Compose(later, 1.3);
                var differs = 0;
                for (var x = 0; x < first.Width; x += 3)
                {
                    for (var y = 0; y < first.Height; y += 3)
                    {
                        if (first.GetPixel(x, y) != later.GetPixel(x, y))
                        {
                            differs++;
                        }
                    }
                }

                differs.ShouldBeGreaterThan(20, motion);
                later.GetPixel((int)hits.Box.X, (int)hits.Box.Y).A.ShouldBeLessThan((byte)128, motion);
            }
        }

        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [panel with { Style = still with { Motion = "spin" } }])])).ShouldNotBeNull();
        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [panel with { Style = still with { Motion = "aurora", MotionSpeed = 500 } }])])).ShouldNotBeNull();
    }

    [Fact]
    public void Handles_on_the_screen_take_the_mouse_only_where_they_are()
    {
        var style = OverlayStyles.Presets["aurora"];
        var (panel, hits) = OverlayRenderer.RenderPanel(Showcase("tiles", style), Sources, 1.5, History(), Now, null, null);
        using (panel)
        {
            var (bitmap, spots) = OverlayRenderer.Handles(panel.Size, hits, style, 1.5, move: true, resize: true, close: false, ArrangePart.None);
            using (bitmap)
            {
                bitmap.Size.ShouldBe(panel.Size);

                // The grip bar and the corner are there, every pixel of them at least slightly opaque (so all of each takes
                // the click); no close button when the panel does not allow one; elsewhere fully transparent.
                spots.Move.IsEmpty.ShouldBeFalse();
                spots.Grip.IsEmpty.ShouldBeFalse();
                spots.Close.IsEmpty.ShouldBeTrue();
                foreach (var r in new[] { spots.Move, spots.Grip })
                {
                    for (var x = (int)Math.Ceiling(r.Left); x < (int)r.Right; x += 3)
                    {
                        for (var y = (int)Math.Ceiling(r.Top); y < (int)r.Bottom; y += 3)
                        {
                            bitmap.GetPixel(x, y).A.ShouldBeGreaterThan((byte)0, $"({x},{y})");
                        }
                    }
                }

                var middle = new Point((int)hits.Box.X + ((int)hits.Box.Width / 2), (int)hits.Box.Y + ((int)hits.Box.Height / 2));
                bitmap.GetPixel(middle.X, middle.Y).A.ShouldBe((byte)0);
                bitmap.GetPixel((int)hits.Close.X + 4, (int)hits.Close.Y + 4).A.ShouldBe((byte)0);
                spots.At(middle).ShouldBe(ArrangePart.None);
                spots.At(new PointF(spots.Move.X + 2, spots.Move.Y + 2)).ShouldBe(ArrangePart.Panel);
                spots.At(new PointF(spots.Grip.Right - 2, spots.Grip.Bottom - 2)).ShouldBe(ArrangePart.Grip);
            }
        }
    }

    [Fact]
    public void A_borderless_glowing_panel_is_exactly_its_size_so_it_can_fill_a_screen()
    {
        // A whole-screen design on a 3.5" USB screen: opaque, glowing values, no border, no shadow.
        var style = OverlayStyles.Presets["neon"] with { Border = null, Shadow = 0, Glow = 60, BackgroundOpacity = 100, CornerRadius = 0 };
        var panel = Showcase("tiles", style) with { Width = 480, Height = 320 };
        using var fills = OverlayRenderer.Render(panel, Sources, 1, History(), Now);
        fills.Size.ShouldBe(new Size(480, 320));
        fills.GetPixel(0, 0).A.ShouldBe((byte)255);
        fills.GetPixel(479, 319).A.ShouldBe((byte)255);

        // A border that glows keeps room around the panel for its glow.
        using var bordered = OverlayRenderer.Render(panel with { Style = style with { Border = "#22D3EE" } }, Sources, 1, History(), Now);
        bordered.Width.ShouldBeGreaterThan(480);
        bordered.GetPixel(0, 0).A.ShouldBeLessThan((byte)255);
    }

    [Fact]
    public void Every_look_draws_every_layout_and_visual()
    {
        var history = History();
        var folder = Environment.GetEnvironmentVariable("PCDIAG_OVERLAY_PNG");
        var drawn = new List<(string Name, string Layout, Bitmap Bitmap)>();
        try
        {
            foreach (var (name, style) in OverlayStyles.Presets)
            {
                foreach (var layout in Layouts)
                {
                    var bitmap = OverlayRenderer.Render(Showcase(layout, style), Sources, 1, history, Now);
                    bitmap.Width.ShouldBeGreaterThan(50, $"{name} {layout}");
                    drawn.Add((name, layout, bitmap));
                }
            }

            if (folder is { Length: > 0 })
            {
                Directory.CreateDirectory(folder);
                SaveSheet(drawn, Path.Combine(folder, "sheet.png"));
            }
        }
        finally
        {
            foreach (var (_, _, bitmap) in drawn)
            {
                bitmap.Dispose();
            }
        }

        // A title on a band, with icons, glow and the accent edge.
        var banded = OverlayStyles.Presets["carbon"] with { TitleBar = true, Glow = 40 };
        using var band = OverlayRenderer.Render(Showcase("rows", banded), Sources, 1.5, history, Now);
        if (folder is { Length: > 0 })
        {
            band.Save(Path.Combine(folder, "carbon-titlebar.png"));
            using var identify = OverlayRenderer.Identify(new OverlayDisplay("k", "PG32UCDP", 1, 0, 0, 3840, 2160, 1.5, true, 240));
            identify.Save(Path.Combine(folder, "identify.png"));
        }
    }

    /// <summary>Every look in every layout on a dark, busy backdrop (like a game), to review them together.</summary>
    private static void SaveSheet(List<(string Name, string Layout, Bitmap Bitmap)> drawn, string path)
    {
        var rows = drawn.GroupBy(d => d.Name).ToList();
        var columnWidths = Layouts.Select(l => drawn.Where(d => d.Layout == l).Max(d => d.Bitmap.Width)).ToArray();
        var width = columnWidths.Sum() + 40 * 4;
        var height = rows.Sum(r => r.Max(d => d.Bitmap.Height)) + (40 * (rows.Count + 1));
        using var sheet = new Bitmap(width, height);
        using var g = Graphics.FromImage(sheet);
        using (var back = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, width, height), Color.FromArgb(17, 24, 39), Color.FromArgb(58, 80, 107), 35f))
        {
            g.FillRectangle(back, 0, 0, width, height);
        }

        var random = new Random(4);
        for (var i = 0; i < 40; i++)
        {
            using var blob = new SolidBrush(Color.FromArgb(70, random.Next(40, 255), random.Next(40, 200), random.Next(60, 255)));
            var r = random.Next(40, 220);
            g.FillEllipse(blob, random.Next(width), random.Next(height), r, r);
        }

        var y = 40;
        foreach (var row in rows)
        {
            var x = 40;
            var i = 0;
            foreach (var layout in Layouts)
            {
                var item = row.First(d => d.Layout == layout).Bitmap;
                g.DrawImage(item, x, y, item.Width, item.Height);
                x += columnWidths[i++] + 40;
            }

            y += row.Max(d => d.Bitmap.Height) + 40;
        }

        sheet.Save(path);
    }

    [Fact]
    public void A_panel_keeps_its_width_as_digits_change_and_an_automatic_size_never_shrinks_while_shown()
    {
        OverlayPanel Tiles(double cpu) => Showcase("tiles", OverlayStyles.Presets["aurora"]) with { Readings = [new("cpu.usage", Visual: "ring"), new("gpu.a.temp")] };
        var at22 = new Dictionary<string, OverlaySource>(Sources) { ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 22) };
        var at26 = new Dictionary<string, OverlaySource>(Sources) { ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 26) };
        var at100 = new Dictionary<string, OverlaySource>(Sources) { ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 100) };

        using var a = OverlayRenderer.Render(Tiles(22), at22, 1);
        using var b = OverlayRenderer.Render(Tiles(26), at26, 1);
        b.Size.ShouldBe(a.Size); // same number of digits, same size

        // A third digit needs more room; going back to two keeps the room while the panel is shown.
        using var wide = OverlayRenderer.Render(Tiles(100), at100, 1);
        wide.Width.ShouldBeGreaterThanOrEqualTo(a.Width);
        using var back = OverlayRenderer.Render(Tiles(22), at22, 1, atLeast: wide.Size);
        back.Size.ShouldBe(wide.Size);
    }

    [Fact]
    public void A_fixed_size_is_kept_whatever_the_readings_need()
    {
        var panel = Showcase("rows", new OverlayStyle()) with { Width = 260, Height = 90 };

        using var bitmap = OverlayRenderer.Render(panel, Sources, 1.5);

        // At 150 %: 390 × 135 for the panel (no shadow or glow, so no margin around it).
        bitmap.Size.ShouldBe(new Size(390, 135));
        bitmap.GetPixel(388, 133).A.ShouldBe((byte)0); // outside the rounded corner
        bitmap.GetPixel(195, 67).A.ShouldBeGreaterThan((byte)0);
    }

    [Fact]
    public void Rows_can_run_in_columns_and_tiles_span_cells_with_a_larger_value()
    {
        // Rows in one column by default (the tiles' column count does not split them).
        using var one = OverlayRenderer.Render(Showcase("rows", new OverlayStyle()) with { Columns = 3 }, Sources, 1, History(), Now);
        using var two = OverlayRenderer.Render(Showcase("rows", new OverlayStyle()) with { RowColumns = 2 }, Sources, 1, History(), Now);
        two.Width.ShouldBeGreaterThan(one.Width);
        two.Height.ShouldBeLessThan(one.Height);

        var plain = Showcase("tiles", OverlayStyles.Presets["aurora"]) with { Columns = 3 };
        var hero = plain with { Readings = [plain.Readings[0] with { ColSpan = 2, RowSpan = 2 }, .. plain.Readings.Skip(1)] };
        using var flat = OverlayRenderer.Render(plain, Sources, 1, History(), Now);
        using var big = OverlayRenderer.Render(hero, Sources, 1, History(), Now);
        big.Height.ShouldBeGreaterThan(flat.Height);

        // Dense placement: a 2 × 2 tile first, then the others fill the free cells beside and below it.
        OverlayRenderer.Pack(hero.Readings, 3).ShouldBe(
        [
            new OverlayRenderer.Place(0, 0, 2, 2),
            new OverlayRenderer.Place(0, 2, 1, 1),
            new OverlayRenderer.Place(1, 2, 1, 1),
            new OverlayRenderer.Place(2, 0, 1, 1),
            new OverlayRenderer.Place(2, 1, 1, 1),
            new OverlayRenderer.Place(2, 2, 1, 1),
        ]);

        if (Environment.GetEnvironmentVariable("PCDIAG_OVERLAY_PNG") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            big.Save(Path.Combine(folder, "hero-tiles.png"));
            two.Save(Path.Combine(folder, "rows-two-columns.png"));
        }
    }

    [Fact]
    public void Every_visual_the_dashboard_has_draws_in_rows_and_tiles()
    {
        string[] visuals = ["none", "bar", "ring", "arc", "speed", "thermo", "graph", "line", "columns", "stats", "status", "trend"];
        var readings = visuals.Select(v => new OverlayReading("cpu.usage", Label: v, Visual: v, Warning: 50, Critical: 90)).ToList();
        var history = History();
        foreach (var layout in Layouts)
        {
            var panel = Showcase(layout, OverlayStyles.Presets["aurora"]) with { Readings = readings, Columns = 4 };
            using var bitmap = OverlayRenderer.Render(panel, Sources, 1, history, Now);
            bitmap.Width.ShouldBeGreaterThan(100, layout);
            if (Environment.GetEnvironmentVariable("PCDIAG_OVERLAY_PNG") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                bitmap.Save(Path.Combine(folder, $"visuals-{layout}.png"));
            }
        }

        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [Showcase("tiles", new OverlayStyle()) with { Readings = readings }])])).ShouldBeNull();
    }

    [Fact]
    public void Each_reading_can_have_its_own_look_apart_from_the_panels()
    {
        string[] visuals = ["none", "bar", "ring", "arc", "speed", "thermo", "graph", "line", "columns", "status"];
        var look = new OverlayReadingLook(Font: "Consolas", ValueSize: 180, Bold: true, ValueColor: "#FF00FF", LabelSize: 140, LabelColor: "#00FF00", ShowUnit: false,
            Align: "left", VisualSize: 160, Thickness: 300, TrackColor: "#FFFFFF", TrackOpacity: 60, BarStyle: "segments", FillOpacity: 90);
        var plain = visuals.Select(v => new OverlayReading("cpu.usage", Label: v, Visual: v)).ToList();
        var own = plain.Select(r => r with { Look = look }).ToList();
        var history = History();
        foreach (var layout in Layouts)
        {
            var panel = Showcase(layout, new OverlayStyle()) with { Readings = plain, Columns = 4 };
            using var before = OverlayRenderer.Render(panel, Sources, 1, history, Now);
            using var after = OverlayRenderer.Render(panel with { Readings = own }, Sources, 1, history, Now);
            // Larger values, labels and visuals make a larger panel; the panel's own look is unchanged.
            (after.Width * after.Height).ShouldBeGreaterThan(before.Width * before.Height, layout);
            if (Environment.GetEnvironmentVariable("PCDIAG_OVERLAY_PNG") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                after.Save(Path.Combine(folder, $"look-{layout}.png"));
            }
        }

        // Only that reading changes: one with a look among plain ones leaves theirs as they were.
        using var one = OverlayRenderer.Render(Showcase("rows", new OverlayStyle()) with { Readings = [plain[0], plain[1] with { Look = new(ValueColor: "#FF00FF") }] }, Sources, 1, history, Now);
        using var none = OverlayRenderer.Render(Showcase("rows", new OverlayStyle()) with { Readings = [plain[0], plain[1]] }, Sources, 1, history, Now);
        one.Size.ShouldBe(none.Size);
        var magenta = Enumerable.Range(0, one.Width).SelectMany(x => Enumerable.Range(0, one.Height).Select(y => one.GetPixel(x, y))).Count(c => c is { R: > 200, G: < 60, B: > 200 });
        magenta.ShouldBeGreaterThan(10);

        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [Showcase("tiles", new OverlayStyle()) with { Readings = own }])])).ShouldBeNull();
        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [Showcase("tiles", new OverlayStyle()) with { Readings = [plain[0] with { Look = new(ValueSize: 20) }] }])]))!.ShouldContain("look:");
        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [Showcase("tiles", new OverlayStyle()) with { Readings = [plain[0] with { Look = new(Font: "Comic") }] }])]))!.ShouldContain("look:");
        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [Showcase("tiles", new OverlayStyle()) with { Readings = [plain[0] with { Look = new(TrackColor: "red") }] }])]))!.ShouldContain("look:");
    }

    [Fact]
    public void A_value_sits_at_the_top_middle_or_bottom_of_its_tile_as_chosen()
    {
        // A tall tile with the value alone, and one with a bar: where its value's ink is, top to bottom.
        static (int Top, int Bottom) Ink(Bitmap b, int x0, int x1)
        {
            var rows = Enumerable.Range(0, b.Height).Where(y => Enumerable.Range(x0, x1 - x0).Any(x => b.GetPixel(x, y) is { A: > 200, R: > 230, G: > 230, B: > 230 })).ToList();
            return (rows.Min(), rows.Max());
        }

        var style = new OverlayStyle { ShowLabels = false, Icons = false, TileOpacity = 0, BackgroundOpacity = 0, Shadow = 0, ValueColor = "#FFFFFF" };
        OverlayPanel Tiles(string? vertical, string visual) => new("p", null, 0, 0, "tiles", [new OverlayReading("cpu.usage", Visual: visual)], style with { ValueVertical = vertical }, Columns: 1, Width: 200, Height: 200);
        foreach (var visual in new[] { "none", "bar" })
        {
            using var top = OverlayRenderer.Render(Tiles("top", visual), Sources, 1);
            using var middle = OverlayRenderer.Render(Tiles("middle", visual), Sources, 1);
            using var bottom = OverlayRenderer.Render(Tiles("bottom", visual), Sources, 1);
            var (t, m, b) = (Ink(top, 20, 180), Ink(middle, 20, 180), Ink(bottom, 20, 180));
            t.Top.ShouldBeLessThan(m.Top - 20, visual);
            m.Top.ShouldBeLessThan(b.Top - 20, visual);
            top.Size.ShouldBe(bottom.Size);
        }

        // Automatic: the value alone in the middle, over a bar at the top; one reading of its own.
        using var auto = OverlayRenderer.Render(Tiles(null, "none"), Sources, 1);
        using var middleToo = OverlayRenderer.Render(Tiles("middle", "none"), Sources, 1);
        Ink(auto, 20, 180).ShouldBe(Ink(middleToo, 20, 180));
        using var own = OverlayRenderer.Render(Tiles(null, "none") with { Readings = [new OverlayReading("cpu.usage", Look: new(Vertical: "top"))] }, Sources, 1);
        using var topToo = OverlayRenderer.Render(Tiles("top", "none"), Sources, 1);
        Ink(own, 20, 180).ShouldBe(Ink(topToo, 20, 180));
        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [Tiles("sideways", "none")])]))!.ShouldContain("valueVertical");
    }

    [Fact]
    public void A_reading_keeps_room_for_its_full_scale_so_100_percent_does_not_resize_the_panel()
    {
        foreach (var layout in Layouts)
        {
            foreach (var visual in new[] { "ring", "speed", "bar", "none" })
            {
                Size SizeAt(double value)
                {
                    var sources = new Dictionary<string, OverlaySource>(Sources, StringComparer.Ordinal) { ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", value) };
                    var panel = new OverlayPanel("p", "Load", 1, 0, layout, [new OverlayReading("cpu.usage", Visual: visual), new OverlayReading("gpu.a.vram", Show: "of", Max: 24.0 * (1L << 30))], OverlayStyles.Presets["aurora"], Columns: 2);
                    using var bitmap = OverlayRenderer.Render(panel, sources, 1.5);
                    return bitmap.Size;
                }

                SizeAt(7).ShouldBe(SizeAt(100), $"{layout} {visual}");
                SizeAt(99).ShouldBe(SizeAt(100), $"{layout} {visual}");
            }
        }
    }

    [Fact]
    public void A_panel_keeps_its_size_and_every_reading_its_place_whatever_the_values_read()
    {
        // Each reading over its range, no reading included: frame rates, pings, temperatures, rates and sizes changing
        // digits and units, the lowest/average/highest and the trend of a ping.
        double?[] fps = [null, 9, 60, 99.6, 144, 999];
        double?[] ping = [null, 0.8, 8, 12.3, 99.96, 112, 999];
        double?[] temp = [null, 9, 45, 65, 100];
        double?[] rate = [null, 0, 512, 950 * 1024, 999.7 * 1024, 12.3 * (1L << 20), 999.9 * (1L << 20), 1.2 * (1L << 30), 45.6 * (1L << 30)];
        double?[] size = [null, 300 * (1L << 20), 999.6 * (1L << 20), 2.3 * (1L << 30), 999 * (1L << 30)];
        var readings = new OverlayReading[]
        {
            new("game.fps", Label: "FPS"),
            new("game.low1", Label: "1%"),
            new("gpu.a.temp", Label: "GPU"),
            new("net.ping.internet", Label: "Ping"),
            new("net.ping.router", Label: "Router", Visual: "stats"),
            new("net.ping.internet", Label: "Trend", Visual: "trend"),
            new("net.e.down", Label: "Down"),
            new("game.memory", Label: "Game RAM"),
            new("game.app", Label: "Game"),
            new("time.date"),
            new("time.clock"),
        };
        string[] apps = ["—", "cs2", "Cyberpunk2077", "FortniteClient-Win64-Shipping", "VeryLongProcessNameOfSomeLauncher"];
        string[] dates = ["Thu 9 Oct", "Wed 24 May", "Sun 1 Jan", "Mon 30 Sep"];
        var names = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat;
        string[] dateRoom = [.. names.AbbreviatedDayNames.SelectMany(d => names.AbbreviatedMonthNames.Where(m => m.Length > 0).Select(m => $"{d} 00 {m}"))];

        foreach (var layout in Layouts)
        {
            var sizes = new HashSet<Size>();
            var places = new HashSet<string>();
            for (var i = 0; i < 9; i++)
            {
                double? Pick(double?[] values) => values[i % values.Length];
                var sources = new Dictionary<string, OverlaySource>(Sources, StringComparer.Ordinal)
                {
                    ["game.fps"] = new("game.fps", "FPS", "Gaming", "fps", Pick(fps)),
                    ["game.low1"] = new("game.low1", "1% low", "Gaming", "fps", Pick(fps.Reverse().ToArray())),
                    ["gpu.a.temp"] = new("gpu.a.temp", "GPU temp", "RTX 4090", "°C", Pick(temp)),
                    ["net.ping.internet"] = new("net.ping.internet", "Ping internet", "Network", "ms", Pick(ping)),
                    ["net.ping.router"] = new("net.ping.router", "Ping router", "Network", "ms", Pick(ping.Reverse().ToArray())),
                    ["net.e.down"] = new("net.e.down", "Download", "Ethernet", "B/s", Pick(rate)),
                    ["game.memory"] = new("game.memory", "Game RAM", "Gaming", "B", Pick(size)),
                    ["game.app"] = new("game.app", "Game", "Gaming", string.Empty, null, apps[i % apps.Length], Room: [new string('0', 14)]),
                    ["time.date"] = new("time.date", "Date", "This PC", string.Empty, null, dates[i % dates.Length], Room: dateRoom),
                    ["time.clock"] = new("time.clock", "Time", "This PC", string.Empty, null, $"{i:00}:{(i * 7) % 60:00}", Room: ["00:00"]),
                };
                var history = new OverlayHistory();
                for (var s = 30; s >= 0; s--)
                {
                    var v = (ping[(i + s) % ping.Length] ?? 0) * (s % 3);
                    history.Add("net.ping.router", Now.AddSeconds(-s), v);
                    history.Add("net.ping.internet", Now.AddSeconds(-s), i % 2 == 0 ? v : -v + 999);
                }

                var panel = new OverlayPanel("p", "Gaming", 1, 1, layout, readings, OverlayStyles.Presets["aurora"], Columns: 3);
                var (bitmap, hits) = OverlayRenderer.RenderPanel(panel, sources, 1.5, i % 3 == 0 ? null : history, Now, null, null);
                using var drawn = bitmap;
                sizes.Add(bitmap.Size);
                places.Add(string.Join(" ", hits.Readings.Select(r => $"{r.X:F1},{r.Y:F1},{r.Width:F1},{r.Height:F1}")));
                if (Environment.GetEnvironmentVariable("PCDIAG_OVERLAY_PNG") is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder);
                    bitmap.Save(Path.Combine(folder, $"steady-{layout}-{i}.png"));
                }
            }

            sizes.Count.ShouldBe(1, $"{layout}: {string.Join(", ", sizes)}");
            places.Count.ShouldBe(1, $"{layout}: every reading keeps its place");
        }
    }

    [Fact]
    public void A_panel_drawn_again_at_the_size_it_keeps_does_not_grow()
    {
        // As the host redraws a shown panel: never smaller than its last bitmap. Float noise in "last size less the
        // margins, plus the margins" once grew a line a pixel a redraw (104 to 113 on a 4K screen at 150 %).
        foreach (var layout in Layouts)
        {
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                foreach (var shadow in new[] { 0, 35, 40, 60, 85 })
                {
                    var panel = Panel(layout, OverlayStyles.Presets["aurora"] with { Shadow = shadow });
                    Size? last = null;
                    var first = Size.Empty;
                    for (var i = 0; i < 30; i++)
                    {
                        var (bitmap, _) = OverlayRenderer.RenderPanel(panel, Sources, scale, null, Now, last, null);
                        using (bitmap)
                        {
                            if (i == 0)
                            {
                                first = bitmap.Size;
                            }

                            bitmap.Size.ShouldBe(first, $"{layout} at {scale} with shadow {shadow}, draw {i}");
                            last = bitmap.Size;
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void A_value_sits_on_the_left_in_the_middle_or_on_the_right_of_its_space()
    {
        // Ink only (no background, shadow or label): where the value's pixels start and end.
        (int First, int Last) Ink(string align, double? ping)
        {
            var style = new OverlayStyle(BackgroundOpacity: 0, Shadow: 0, ShowLabels: false, Icons: false, Sheen: false, ValueAlign: align);
            var sources = new Dictionary<string, OverlaySource>(StringComparer.Ordinal) { ["net.ping.internet"] = new("net.ping.internet", "Ping", "Network", "ms", ping) };
            using var bitmap = OverlayRenderer.Render(new OverlayPanel("p", null, 0, 0, "line", [new OverlayReading("net.ping.internet")], style), sources, 1);
            var columns = Enumerable.Range(0, bitmap.Width).Where(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).A > 40)).ToList();
            return (columns[0], columns[^1]);
        }

        var (left, _) = Ink("left", 8);
        var (centerFirst, centerLast) = Ink("center", 8);
        var (rightFirst, rightLast) = Ink("right", 8);
        left.ShouldBeLessThan(centerFirst);
        centerFirst.ShouldBeLessThan(rightFirst);

        // In the middle, a shorter value stays centred where a longer one is: neither edge moves alone.
        var (wideFirst, wideLast) = Ink("center", 88.8);
        ((wideFirst + wideLast) / 2.0).ShouldBe((centerFirst + centerLast) / 2.0, 3);

        // On the right its end stays put.
        ((double)Ink("right", 88.8).Last).ShouldBe(rightLast, 2);
        OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("k", "n", [new OverlayPanel("p", null, 0, 0, "line", [], new OverlayStyle(ValueAlign: "middle"))])])).ShouldNotBeNull();
    }

    [Fact]
    public void A_panel_being_arranged_says_where_its_readings_and_buttons_are()
    {
        foreach (var layout in Layouts)
        {
            var panel = Showcase(layout, OverlayStyles.Presets["aurora"]);
            var (plain, plainHits) = OverlayRenderer.RenderPanel(panel, Sources, 1.5, History(), Now, null, null);
            var (arranged, hits) = OverlayRenderer.RenderPanel(panel, Sources, 1.5, History(), Now, null, new ArrangeView(ArrangePart.Reading, Hover: 1));
            using (plain)
            using (arranged)
            {
                // Arranging draws on top (outline, buttons) without changing the size.
                arranged.Size.ShouldBe(plain.Size, layout);
                plainHits.Readings.Count.ShouldBe(panel.Readings.Count, layout);
                hits.Readings.ShouldAllBe(r => r.Width > 0 && r.Height > 0);
                for (var i = 0; i < hits.Readings.Count; i++)
                {
                    var r = hits.Readings[i];
                    hits.Box.Contains(r).ShouldBeTrue($"{layout} reading {i}");
                    var centre = Point.Round(new PointF(r.X + (r.Width / 2), r.Y + (r.Height / 2)));
                    if (!hits.Close.Contains(centre) && !hits.Menu.Contains(centre) && !hits.Grip.Contains(centre))
                    {
                        hits.At(centre).ShouldBe((ArrangePart.Reading, i), $"{layout} reading {i}");
                    }
                }

                hits.Box.Contains(hits.Close).ShouldBeTrue();
                hits.At(Point.Round(new PointF(hits.Close.X + (hits.Close.Width / 2), hits.Close.Y + (hits.Close.Height / 2)))).Part.ShouldBe(ArrangePart.Close);
                hits.At(Point.Round(new PointF(hits.Grip.Right - 2, hits.Grip.Bottom - 2))).Part.ShouldBe(ArrangePart.Grip);
                hits.At(new Point(0, 0)).Part.ShouldBe(ArrangePart.None);
            }
        }

        var (bar, done) = OverlayRenderer.ArrangeBar(1.5, hoverDone: true);
        using (bar)
        {
            new RectangleF(0, 0, bar.Width, bar.Height).Contains(done).ShouldBeTrue();
            done.Width.ShouldBeGreaterThan(40);
        }
    }

    [Fact]
    public void A_background_picture_and_a_pattern_are_drawn_inside_the_panel()
    {
        using var red = new Bitmap(40, 20);
        using (var g = Graphics.FromImage(red))
        {
            g.Clear(Color.FromArgb(255, 220, 30, 40));
        }

        var previous = OverlayRenderer.Images;
        OverlayRenderer.Images = id => id == "0123456789abcdef.png" ? red : null;
        try
        {
            var plain = new OverlayStyle(Background: "#000000", BackgroundOpacity: 100, CornerRadius: 0, Padding: 20);
            Color Centre(OverlayStyle style)
            {
                using var bitmap = OverlayRenderer.Render(Panel(style: style) with { Readings = [], Title = null, Width = 200, Height = 100 }, Sources, 1);
                return bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
            }

            ((int)Centre(plain).R).ShouldBeLessThan(10);
            var pictured = Centre(plain with { Image = "0123456789abcdef.png" });
            ((int)pictured.R).ShouldBeGreaterThan(200);
            ((int)pictured.G).ShouldBeLessThan(60);
            ((int)Centre(plain with { Image = "0123456789abcdef.png", ImageGray = true }).G).ShouldBeGreaterThan(60);
            ((int)Centre(plain with { Image = "0123456789abcdef.png", ImageTint = "#000000", ImageTintOpacity = 100 }).R).ShouldBeLessThan(10);
            ((int)Centre(plain with { Image = "ffffffffffffffff.png" }).R).ShouldBeLessThan(10);

            // A light pattern over a dark panel: some pixels lighter, most still dark (blended as a browser blends, so a
            // thin line at 60 % is lighter than the panel's near-black, not bright).
            foreach (var pattern in OverlayStyles.Patterns)
            {
                using var bitmap = OverlayRenderer.Render(Panel(style: plain with { Pattern = pattern, PatternOpacity = 60 }) with { Readings = [], Title = null, Width = 200, Height = 100 }, Sources, 1);
                var lit = 0;
                for (var x = 30; x < 190; x += 3)
                {
                    for (var y = 30; y < 90; y += 3)
                    {
                        if (bitmap.GetPixel(x, y).R > 20)
                        {
                            lit++;
                        }
                    }
                }

                lit.ShouldBeGreaterThan(5, pattern);
            }
        }
        finally
        {
            OverlayRenderer.Images = previous;
        }
    }

    [Fact]
    public void Every_backdrop_is_drawn_inside_its_tiles_and_never_changes_the_panels_size()
    {
        var plain = new OverlayStyle(Background: "#05070D", BackgroundOpacity: 100, Accent: "#22D3EE", TileOpacity: 8, CornerRadius: 10);
        using var none = OverlayRenderer.Render(Showcase("tiles", plain), Sources, 1.5, History(), Now);
        foreach (var backdrop in OverlayStyles.Backdrops.Where(b => b != "none"))
        {
            using var drawn = OverlayRenderer.Render(Showcase("tiles", plain with { Backdrop = backdrop }), Sources, 1.5, History(), Now);
            drawn.Size.ShouldBe(none.Size, backdrop);
            var changed = 0;
            var outside = 0;
            for (var x = 0; x < drawn.Width; x += 2)
            {
                for (var y = 0; y < drawn.Height; y += 2)
                {
                    var a = drawn.GetPixel(x, y);
                    var b = none.GetPixel(x, y);
                    if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) + Math.Abs(a.A - b.A) > 12)
                    {
                        changed++;

                        // The panel's own edge (its padding) is never drawn on.
                        if (x < 6 || y < 6 || x >= drawn.Width - 6 || y >= drawn.Height - 6)
                        {
                            outside++;
                        }
                    }
                }
            }

            changed.ShouldBeGreaterThan(40, backdrop);
            outside.ShouldBe(0, backdrop);
        }

        Validate(plain with { Backdrop = "lava" }).ShouldNotBeNull();
        Validate(plain with { Backdrop = "bezel" }).ShouldBeNull();

        // A picture drawn from an art: the art is one the editor draws, the picture an ordinary one.
        Validate(plain with { Art = "cockpit", Image = "0123456789abcdef.png", ImageFit = "stretch" }).ShouldBeNull();
        Validate(plain with { Art = "cockpit" }).ShouldBeNull();
        Validate(plain with { Art = "crayon" }).ShouldNotBeNull();
        Validate(plain with { Art = "cockpit", Image = "art:cockpit" }).ShouldNotBeNull();

        static string? Validate(OverlayStyle style) =>
            OverlayStyles.Validate(new OverlayConfig(Screens: [new OverlayScreen("AUS32F4/1", "PG32UCDP", [new OverlayPanel("p1", null, 1, 0, "tiles", [new("cpu.usage")], style)])]));
    }

    [Fact]
    public void An_animated_picture_plays_frame_by_frame_under_the_readings()
    {
        // Two frames, red then blue, two a second.
        byte[] Frame(Color color)
        {
            using var bitmap = new Bitmap(40, 20);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(color);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
            return stream.ToArray();
        }

        using var animation = AnimatedPicture.Read(OverlayAnimation.Write(2, 40, 20, [Frame(Color.FromArgb(220, 30, 40)), Frame(Color.FromArgb(30, 40, 220))])).ShouldNotBeNull();
        var previous = OverlayRenderer.Animations;
        OverlayRenderer.Animations = id => id == "0123456789abcdef.anim" ? animation : null;
        try
        {
            var style = new OverlayStyle(Background: "#000000", BackgroundOpacity: 100, CornerRadius: 0, Padding: 20, Image: "0123456789abcdef.anim", ImageFit: "stretch");
            var panel = Panel(style: style) with { Readings = [], Title = null, Width = 200, Height = 100 };
            var (first, _, layers) = OverlayRenderer.RenderLayers(panel, Sources, 1, null, Now, null, null, 0, keepLayers: true);
            using (first)
            using (layers)
            {
                layers.ShouldNotBeNull("an animated picture moves");
                var red = first.GetPixel(100, 50);
                ((int)red.R).ShouldBeGreaterThan(150);
                ((int)red.B).ShouldBeLessThan(90);
                using var next = new Bitmap(layers!.Size.Width, layers.Size.Height);
                layers.Compose(next, 0.6);
                var blue = next.GetPixel(100, 50);
                ((int)blue.B).ShouldBeGreaterThan(150);
                ((int)blue.R).ShouldBeLessThan(90);
            }
        }
        finally
        {
            OverlayRenderer.Animations = previous;
        }

        OverlayRenderer.IsAnimation("0123456789abcdef.anim").ShouldBeTrue();
        OverlayRenderer.IsAnimation("0123456789abcdef.png").ShouldBeFalse();
        animation.FrameCount.ShouldBe(2);
    }

    [Fact]
    public void Each_gauge_is_recorded_where_it_is_drawn_for_characters_to_play_on()
    {
        var panel = new OverlayPanel("p", null, 0, 0, "tiles", [new OverlayReading("cpu.usage", Visual: "ring"), new OverlayReading("cpu.temp", Visual: "speed", Max: 100), new OverlayReading("memory.usage")], new OverlayStyle(), Columns: 3, Width: 600, Height: 200);
        var (bitmap, hits) = OverlayRenderer.RenderPanel(panel, Sources, 1, null, Now, null, null);
        using (bitmap)
        {
            hits.Gauges.ShouldNotBeNull();
            var ring = hits.Gauges![0].ShouldNotBeNull();
            (ring.Visual, ring.Start, ring.Sweep).ShouldBe(("ring", -90f, 360f));
            ring.Fraction.ShouldBe(0.524f, 0.001f);
            hits.Readings[0].Contains(ring.Center).ShouldBeTrue("the ring is in its tile");
            ring.Radius.ShouldBeGreaterThan(20);
            var speed = hits.Gauges[1].ShouldNotBeNull();
            (speed.Visual, speed.Start, speed.Sweep).ShouldBe(("speed", 160f, 220f));
            speed.Fraction.ShouldBe(0.91f, 0.001f);
            hits.Gauges[2].ShouldBeNull("a plain value has no gauge");
        }

        // A bar and a line graph are recorded too: the bar's track, the graph's line as drawn.
        var history = new OverlayHistory();
        for (var i = 0; i < 10; i++)
        {
            history.Add("cpu.usage", Now.AddSeconds(i - 10), 20 + (i * 5));
        }

        var both = new OverlayPanel("p", null, 0, 0, "rows", [new OverlayReading("cpu.usage", Visual: "bar"), new OverlayReading("cpu.usage", Visual: "line")], new OverlayStyle(), Width: 400);
        var (shown, drawn) = OverlayRenderer.RenderPanel(both, Sources, 1, history, Now, null, null);
        using (shown)
        {
            var bar = drawn.Gauges![0].ShouldNotBeNull();
            (bar.Visual, bar.Fraction).ShouldBe(("bar", 0.524f));
            var line = drawn.Gauges[1].ShouldNotBeNull();
            line.Visual.ShouldBe("graph");
            line.Line.ShouldNotBeNull().Count.ShouldBeGreaterThan(5);
            line.Line!.ShouldAllBe(p => line.Box.Contains(p.X, p.Y) || p.Y == line.Box.Bottom || p.X == line.Box.Right);
        }
    }

    [Fact]
    public void An_animation_between_two_frames_shows_a_blend_of_them()
    {
        static byte[] Frame(Color color)
        {
            using var bitmap = new Bitmap(40, 20);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(color);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
            return stream.ToArray();
        }

        using var animation = AnimatedPicture.Read(OverlayAnimation.Write(2, 40, 20, [Frame(Color.FromArgb(220, 30, 40)), Frame(Color.FromArgb(30, 40, 220))])).ShouldNotBeNull();
        var red = ((Bitmap)animation.FrameAt(0)!).GetPixel(20, 10);
        ((int)red.R).ShouldBeGreaterThan(200, "on a frame: the frame itself");
        var half = ((Bitmap)animation.FrameAt(0.25)!).GetPixel(20, 10);
        ((int)half.R).ShouldBeInRange(105, 145, "half way: half of each");
        ((int)half.B).ShouldBeInRange(110, 150);
        var back = ((Bitmap)animation.FrameAt(1.75)!).GetPixel(20, 10);
        ((int)back.R).ShouldBeInRange(105, 145, "the last frame blends into the first (it loops)");
    }

    [Fact]
    public void Numbers_glide_to_a_new_value_and_the_rest_changes_at_once()
    {
        var from = new Dictionary<string, OverlaySource>
        {
            ["cpu"] = new("cpu", "CPU", "Processor", "%", 20),
            ["temp"] = new("temp", "Temp", "Processor", "°C", 50),
            ["name"] = new("name", "GPU", "Graphics", "", null, Text: "Old"),
            ["unit"] = new("unit", "Speed", "Network", "KB/s", 900),
        };
        var to = new Dictionary<string, OverlaySource>
        {
            ["cpu"] = new("cpu", "CPU", "Processor", "%", 60, Max: 100),
            ["temp"] = new("temp", "Temp", "Processor", "°C", 50),
            ["name"] = new("name", "GPU", "Graphics", "", null, Text: "New"),
            ["unit"] = new("unit", "Speed", "Network", "MB/s", 1.2),
            ["new"] = new("new", "Fan", "Cooling", "rpm", 800),
        };

        var start = OverlayHost.Between(from, to, 0);
        start["cpu"].Value.ShouldBe(20, "it starts at what is shown");
        start["cpu"].Max.ShouldBe(100, "the limit is the new one at once");
        start["name"].Text.ShouldBe("New");
        start["unit"].Value.ShouldBe(1.2, "another unit: no glide");
        start["new"].Value.ShouldBe(800, "a new reading shows at once");
        OverlayHost.Between(from, to, 0.5)["cpu"].Value.ShouldBe(40);
        OverlayHost.Between(from, to, 1)["cpu"].Value.ShouldBe(60);
        OverlayHost.Glides(from["temp"], to["temp"]).ShouldBeFalse("the same value does not glide");
        OverlayHost.Ease(0).ShouldBe(0);
        OverlayHost.Ease(1).ShouldBe(1);
        OverlayHost.Ease(0.5).ShouldBeGreaterThan(0.8, "fast first, settling into the value");
    }

    [Fact]
    public void Every_icon_is_read_into_a_path_inside_its_box()
    {
        foreach (var (id, data) in OverlayIcons.Paths)
        {
            using var path = SvgPath.Parse(data, 1, PointF.Empty);
            var bounds = path.GetBounds();
            bounds.Width.ShouldBeGreaterThan(1, id);
            bounds.Left.ShouldBeGreaterThanOrEqualTo(-0.5f, id);
            bounds.Top.ShouldBeGreaterThanOrEqualTo(-0.5f, id);
            bounds.Right.ShouldBeLessThanOrEqualTo(24.5f, id);
            bounds.Bottom.ShouldBeLessThanOrEqualTo(24.5f, id);
        }

        // Arcs: a full circle of radius 10 around (12, 12).
        using var circle = SvgPath.Parse("M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20Z", 1, PointF.Empty);
        var box = circle.GetBounds();
        box.X.ShouldBe(2, 0.05);
        box.Y.ShouldBe(2, 0.05);
        box.Width.ShouldBe(20, 0.1);
        box.Height.ShouldBe(20, 0.1);
    }
}
