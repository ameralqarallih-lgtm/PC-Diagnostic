using System.Drawing;
using System.Text.Json;
using PcDiag.Agent.Overlay;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Tests;

/// <summary>
/// The recorder playing characters: the editor's test character (test.char, a coloured column moving across) for the
/// frames chosen and the facing, the shipped ones for their size and feet, a USB screen's picture, and the editor's
/// motion (motion-parity.json, written by its tests).
/// </summary>
public sealed class CharacterPlayerTests
{
    private static readonly int[] Steps = [0, 1, 7, 21];

    // The characters never fall asleep here because this PC's user is away (its last input time is not the tests').
    public CharacterPlayerTests() => CharacterReacting.UserIdle = () => null;

    private static string EditorFile(string name)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, "web", "src", "features", "overlay", "characters", name);
            if (File.Exists(file))
            {
                return file;
            }
        }

        throw new FileNotFoundException(name + " (the editor's character tests)");
    }

    private static byte[] TestCharacter() => File.ReadAllBytes(EditorFile("test.char"));

    private static RectangleF Box(JsonElement b) =>
        RectangleF.FromLTRB(b.GetProperty("left").GetSingle(), b.GetProperty("top").GetSingle(), b.GetProperty("right").GetSingle(), b.GetProperty("bottom").GetSingle());

    [Fact]
    public void Characters_move_where_the_editor_moves_them()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(EditorFile("motion-parity.json")));
        var parity = document.RootElement;
        foreach (var seed in parity.GetProperty("seeds").EnumerateArray())
        {
            var value = CharacterMotion.SeedOf(seed.GetProperty("text").GetString()!);
            value.ShouldBe(seed.GetProperty("seed").GetUInt32());
            var randoms = seed.GetProperty("randoms").EnumerateArray().Select(r => r.GetDouble()).ToArray();
            Steps.Select(k => CharacterMotion.Random(value, k)).ShouldBe(randoms);
        }

        var panel = Box(parity.GetProperty("panel"));
        var screen = Box(parity.GetProperty("screen"));
        var size = parity.GetProperty("size").GetDouble();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var expected in parity.GetProperty("frames").EnumerateArray())
        {
            var c = expected.GetProperty("character").Deserialize<OverlayCharacter>(options)!;
            var t = expected.GetProperty("t").GetDouble();
            var f = expected.GetProperty("frame");
            var frame = CharacterMotion.FrameOf(c, panel, screen, size, t);
            var what = $"{c.Place} {c.Movement} at {t}";
            frame.X.ShouldBe(f.GetProperty("x").GetDouble(), 1e-3, what);
            frame.Y.ShouldBe(f.GetProperty("y").GetDouble(), 1e-3, what);
            frame.Action.ShouldBe(f.GetProperty("action").GetString(), what);
            frame.Seconds.ShouldBe(f.GetProperty("seconds").GetDouble(), 1e-3, what);
            frame.Facing.ShouldBe(f.GetProperty("facing").GetInt32(), what);
        }
    }

    [Fact]
    public void Frames_let_go_past_the_budget_never_break_the_drawing()
    {
        // Past the budget the oldest frames are let go (a frame was measured after being let go, and the overlay crashed).
        var (bytes, _) = OverlayCharacters.Shipped["mikan"];
        using var player = CharacterPlayer.Read(bytes)!;
        player.Budget = 2 * 1024 * 1024;
        for (var i = 0; i < 300; i++)
        {
            var (bitmap, _) = player.Frame(i % 2 == 0 ? "walk" : "sit", i / 20.0, i % 3 == 0 ? 1 : -1, 60 + (i % 5 * 20))!.Value;
            bitmap.Width.ShouldBeGreaterThan(0);
        }

        // A copy for another thread has frames of its own.
        using var copy = player.Copy();
        copy.Frame("idle", 0, 1, 80)!.Value.Bitmap.ShouldNotBeSameAs(player.Frame("idle", 0, 1, 80)!.Value.Bitmap);
    }

    [Fact]
    public void A_character_stays_on_its_screen_even_on_a_panel_at_its_top_edge()
    {
        // A USB screen filled by its panel: on top of it, a character would stand above the screen, out of sight.
        var screen = new RectangleF(0, 0, 1920, 480);
        var panel = new RectangleF(340, 0, 900, 480);
        foreach (var place in new[] { "top", "around", "left", "right", "inside", "screen" })
        {
            for (var t = 0.0; t < 60; t += 0.9)
            {
                var f = CharacterMotion.FrameOf(new OverlayCharacter("c1", "mikan", place), panel, screen, 48, t);
                f.Y.ShouldBeInRange(48, 480, place);
                f.X.ShouldBeInRange(48 * 0.4, 1920 - (48 * 0.4), place);
            }
        }
    }

    /// <summary>The columns of a frame with a solid pixel on a row.</summary>
    private static int[] Columns(Bitmap b, int y) => [.. Enumerable.Range(0, b.Width).Where(x => b.GetPixel(x, y).A > 200)];

    [Fact]
    public void It_goes_to_the_tile_of_a_reading_that_turns_bad_and_back_to_its_route()
    {
        var state = new CharacterReacting();
        var c = new OverlayCharacter("c", "ren", Place: "screen");
        var box = new RectangleF(0, 0, 1000, 100);
        var route = new CharacterMotion.Frame(100, 100, "idle", 0, 1);
        var tile = new RectangleF(600, 0, 100, 80);
        var f = route;
        for (var t = 0.0; t < 1.2; t += 0.05)
        {
            f = state.GoTo(route, c, tile, true, "jump", null, box, box, 64, t);
        }

        (f.Action, f.Facing).ShouldBe(("run", 1));
        for (var t = 1.2; t < 4; t += 0.05)
        {
            f = state.GoTo(route, c, tile, true, "jump", null, box, box, 64, t);
        }

        (f.X, f.Action).ShouldBe((650, "jump"));
        // Good again: its relief first, where it is, then back along the line (walking) to its route.
        state.GoTo(route, c, null, false, "default", "dance", box, box, 64, 4).ShouldBe(route with { X = 650, Action = "dance", Seconds = 4 });
        f = state.GoTo(route, c, null, false, "default", null, box, box, 64, 4.05);
        (f.Action, f.Facing).ShouldBe(("walk", -1));
        for (var t = 4.1; t < 12; t += 0.05)
        {
            f = state.GoTo(route, c, null, false, "default", null, box, box, 64, t);
        }

        f.ShouldBe(route);
        new CharacterReacting().GoTo(route, c with { Place = "left" }, tile, true, "jump", null, box, box, 64, 0).ShouldBe(route, "beside its panel it keeps to its route");
    }

    [Fact]
    public void It_walks_over_to_the_mouse_resting_near_it_faces_it_and_shows_the_weather()
    {
        var idle = CharacterReacting.UserIdle;
        CharacterReacting.UserIdle = null;
        try
        {
            var state = new CharacterReacting();
            var c = new OverlayCharacter("c", "ren", Place: "screen", Movement: "stay", X: 0.1);
            var panel = new OverlayPanel("p", null, 0, 0, "rows", [], new OverlayStyle());
            var screen = new RectangleF(0, 0, 1000, 400);
            var feet = new PointF(float.NaN, float.NaN);
            var spot = CharacterMotion.FrameOf(c, screen, screen, 60, 0).X;
            CharacterMotion.Frame f = default!;
            CharacterThought thought = CharacterThought.None;
            for (var t = 0.0; t < 6; t += 0.05)
            {
                // The mouse resting 3 heights to the right of its spot, a little above it.
                var senses = float.IsNaN(feet.X) ? null : new CharacterSenses("p/c", feet.X, feet.Y, 60, spot + 180, 370, Line: true);
                (f, _, _, thought) = CharacterReacting.FrameOf(c, panel, new Dictionary<string, OverlaySource>(), state, screen, screen, 60, t, null, senses);
                feet = new PointF((float)f.X, (float)f.Y);
            }

            // Stopped 0.7 of its height before it, facing it.
            f.X.ShouldBe(spot + 180 - 42, 1.5);
            (f.Facing, f.Action).ShouldBe((1, "idle"));

            // The mouse gone and rain outside: it walks back to its spot, then shows the rain (and its umbrella).
            var rain = new CharacterWeather(1, "rain", 12, 2, true);
            string? effect = null;
            for (var t = 6.0; t < 12; t += 0.05)
            {
                (f, effect, _, thought) = CharacterReacting.FrameOf(c, panel, new Dictionary<string, OverlaySource>(), state, screen, screen, 60, t, null, new CharacterSenses("p/c", f.X, f.Y, 60, Weather: rain));
            }

            f.X.ShouldBe(CharacterMotion.FrameOf(c, screen, screen, 60, 0).X, 1.5, "back on its spot");
            effect.ShouldBe("rain");
            state.Prop.ShouldBe("umbrella");
            thought.Bubble.ShouldBeNull("said when it came, 4 s on screen");
        }
        finally
        {
            CharacterReacting.UserIdle = idle;
        }
    }

    [Fact]
    public void On_a_gauge_it_runs_in_the_ring_or_rides_the_needle()
    {
        var ring = new GaugeHit(new RectangleF(100, 100, 200, 200), "ring", 0.8f, 20);
        var panel = new OverlayPanel("p", null, 0, 0, "tiles", [new OverlayReading("cpu.usage")], new OverlayStyle());
        var spots = new ReadingSpots();
        spots.Gauges["cpu.usage"] = ring;
        var sources = new Dictionary<string, OverlaySource> { ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 80) };
        var state = new CharacterReacting();
        var wheel = new OverlayCharacter("c", "ren", Place: "wheel", React: "cpu.usage");
        var (f0, _, _, _) = CharacterReacting.FrameOf(wheel, panel, sources, state, RectangleF.Empty, new RectangleF(0, 0, 800, 600), 64, 0, spots);
        var (f1, _, _, _) = CharacterReacting.FrameOf(wheel, panel, sources, state, RectangleF.Empty, new RectangleF(0, 0, 800, 600), 64, 0.1, spots);
        // At the ring's bottom, inside it, running (80 %), the wheel turning; drawn to fit the ring.
        (f1.X, f1.Action).ShouldBe((200, "run"));
        f1.Y.ShouldBe(200 + (80 * 0.93), 0.01);
        (f1.Seconds > f0.Seconds).ShouldBeTrue();
        state.Play.ShouldNotBeNull();
        state.Play!.Size.ShouldBe(80 * 1.15f, 0.01f);
        state.Play.Spin.ShouldBeGreaterThan(0);
        CharacterExtras.WheelBox(state.Play).ShouldBe(new RectangleF(120, 120, 160, 160));

        // Nearly empty: asleep in its wheel.
        spots.Gauges["cpu.usage"] = ring with { Fraction = 0.01f };
        CharacterReacting.FrameOf(wheel, panel, sources, state, RectangleF.Empty, new RectangleF(0, 0, 800, 600), 64, 0.2, spots).Frame.Action.ShouldBe("sleep");

        // On the needle: at the end of the fill (a quarter round from the top: the right), facing out.
        spots.Gauges["cpu.usage"] = ring with { Fraction = 0.25f };
        var rider = new OverlayCharacter("r", "ren", Place: "needle", React: "cpu.usage");
        var needle = new CharacterReacting();
        var (f, _, _, _) = CharacterReacting.FrameOf(rider, panel, sources, needle, RectangleF.Empty, new RectangleF(0, 0, 800, 600), 64, 0, spots);
        f.X.ShouldBe(200 + 90, 0.01);
        f.Y.ShouldBe(200, 0.01);
        (f.Facing, needle.Play!.Kind).ShouldBe((1, "needle"));

        // Without its gauge on the panel: on the panel's top edge.
        var none = new CharacterReacting();
        CharacterReacting.FrameOf(rider, panel, sources, none, new RectangleF(0, 300, 400, 100), new RectangleF(0, 0, 800, 600), 64, 0, new ReadingSpots());
        none.Play.ShouldBeNull();
    }

    [Fact]
    public void On_a_bar_it_pushes_the_end_of_its_fill_and_on_a_graph_it_surfs_the_line()
    {
        var bar = new GaugeHit(new RectangleF(100, 300, 200, 10), "bar", 0.5f, 10);
        var c = new OverlayCharacter("c", "ren", Place: "bar");
        var state = new CharacterReacting();
        var (f0, _) = state.OnGauge(c, bar, false, "default", 0, 64);
        (f0.X, f0.Y, f0.Action).ShouldBe((200 - (64 * 0.12), 300, "idle"), "holding: it stands at the end of the fill, on the bar");
        state.OnGauge(c, bar with { Fraction = 0.6f }, false, "default", 0.1, 64).Frame.Action.ShouldBe("walk", "growing: it pushes");
        state.OnGauge(c, bar with { Fraction = 0.6f }, true, "default", 0.2, 64).Frame.Action.ShouldBe("panic", "critical and holding");
        CharacterReacting.Fits("bar", bar).ShouldBeTrue();
        CharacterReacting.Fits("wheel", bar).ShouldBeFalse();

        var graph = new GaugeHit(new RectangleF(100, 200, 300, 60), "graph", 0.5f, 2, [new PointF(100, 260), new PointF(250, 200), new PointF(400, 230)]);
        graph.LineAt(175).ShouldBe(230, 0.01f);
        graph.LineAt(50).ShouldBe(260);
        graph.LineAt(500).ShouldBe(230);
        var surfer = new CharacterReacting();
        var g = new OverlayCharacter("g", "ren", Place: "graph");
        surfer.OnGauge(g, graph, false, "default", 0, 64);
        var (f1, play) = surfer.OnGauge(g, graph, false, "default", 0.1, 64);
        (f1.X > 100 && f1.X < 110).ShouldBeTrue("it goes along the line");
        f1.Y.ShouldBe(graph.LineAt((float)f1.X), 0.01);
        (f1.Action, play.Kind, play.Size).ShouldBe(("run", "graph", 64f));
    }

    [Fact]
    public void Its_gauge_is_the_most_urgent_of_its_panel_in_its_order()
    {
        var panel = new OverlayPanel("p", null, 0, 0, "tiles", [new OverlayReading("cpu.usage"), new OverlayReading("gpu.usage"), new OverlayReading("cpu.temp"), new OverlayReading("ram")], new OverlayStyle());
        var spots = new ReadingSpots();
        string[] shown = ["cpu.usage", "gpu.usage", "cpu.temp"];
        foreach (var source in shown)
        {
            spots.Gauges[source] = new GaugeHit(new RectangleF(Array.IndexOf(shown, source) * 200, 0, 100, 100), "ring", 0.5f, 10);
        }

        Dictionary<string, OverlaySource> Values(double cpu, double gpu, double temp) => new()
        {
            ["cpu.usage"] = new("cpu.usage", "CPU", "", "%", cpu),
            ["gpu.usage"] = new("gpu.usage", "GPU", "", "%", gpu),
            ["cpu.temp"] = new("cpu.temp", "CPU temp", "", "°C", temp),
            ["ram"] = new("ram", "RAM", "", "%", 99),
        };
        var c = new OverlayCharacter("c", "ren", Place: "wheel", Priority: ["gpu.usage"]);
        // Critical first (the temperature, though the GPU is put first; RAM has no gauge), then high, then the first in its order.
        CharacterReacting.GaugeOf(c, panel, Values(80, 40, 95), spots).ShouldBe(spots.Gauges["cpu.temp"]);
        CharacterReacting.GaugeOf(c, panel, Values(80, 40, 50), spots).ShouldBe(spots.Gauges["cpu.usage"]);
        CharacterReacting.GaugeOf(c, panel, Values(40, 40, 50), spots).ShouldBe(spots.Gauges["gpu.usage"]);
        CharacterReacting.GaugeOf(c with { Gauge = "cpu.usage" }, panel, Values(40, 40, 95), spots).ShouldBe(spots.Gauges["cpu.usage"], "its own gauge stays");
    }

    /// <summary>A new animation's file made from a character's own (one of its clips, named for the animation, alone: no idle).</summary>
    private static byte[] ExtraOf(byte[] character, string from, string action)
    {
        var length = BitConverter.ToInt32(character, 6);
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(character, 10, length))!.AsObject();
        var clips = manifest["clips"]!.AsArray();
        var at = 10 + length;
        byte[]? frames = null;
        System.Text.Json.Nodes.JsonNode? kept = null;
        foreach (var clip in clips)
        {
            var sizes = clip!["frames"]!.AsArray().Select(f => (int)f!).ToList();
            if ((string)clip["action"]! == from)
            {
                frames = character[at..(at + sizes.Sum())];
                kept = clip.DeepClone();
                kept["action"] = action;
            }

            at += sizes.Sum();
        }

        manifest["clips"] = new System.Text.Json.Nodes.JsonArray(kept!);
        var json = System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return [.. "PCDC"u8, 1, 0, .. BitConverter.GetBytes(json.Length), .. json, .. frames!];
    }

    [Fact]
    public void Without_a_clip_it_does_the_closest_it_has_its_new_animations_counted()
    {
        var bytes = OverlayCharacters.Shipped["ren"].Bytes;
        using var player = CharacterPlayer.Read(bytes)!;
        player.Has("dance").ShouldBeFalse();
        player.Plays("cheer").ShouldBe("play", "it has no cheer or dance of its own");

        // Given a dance (a new animation's), it dances for the cheer it does not have.
        player.Attach([ExtraOf(bytes, "jump", "dance")]);
        player.Has("dance").ShouldBeTrue();
        player.Plays("cheer").ShouldBe("dance");
        player.Frame("cheer", 0.2, 1, 96).ShouldNotBeNull();
        player.Plays("sleep").ShouldBe("sleep");
        player.Plays("fly").ShouldBe("idle");
    }

    [Fact]
    public void Its_props_go_on_its_head_as_each_frame_has_it_and_on_the_ground_beside_it()
    {
        using var player = CharacterPlayer.Read(OverlayCharacters.Shipped["ren"].Bytes)!;
        using var props = CharacterPlayer.ReadProps(OverlayReactions.PropsPack.Bytes)!;
        var standing = new CharacterMotion.Frame(300, 300, "idle", 0.5, 1);
        var (plain, plainAt, _) = OverlayHost.Picture(player, null, standing, null, 96, null)!.Value;
        var (plainRight, plainTop) = (plainAt.X + plain.Width, plainAt.Y);

        // The top of its head as this frame has it (decoded): near its usual head standing, lower sitting.
        var head = player.HeadAt("idle", 0.5, 1, 96);
        Math.Abs(head.Y - player.HeadFromFeet(96, 1).Y).ShouldBeLessThan(96 * 0.15f);
        OverlayHost.Picture(player, null, standing with { Action = "sit" }, null, 96, null);
        player.HeadAt("sit", 0.5, 1, 96).Y.ShouldBeGreaterThan(head.Y);

        // An ice pack on its head: the picture grows upward, the pack's blue at its top.
        var (iced, icedAt, composed) = OverlayHost.Picture(player, null, standing, null, 96, null, null, 5, null, props, ("ice-pack", 2, null, 0, -1))!.Value;
        using (composed)
        {
            icedAt.Y.ShouldBeLessThan(plainTop);
            var blue = Enumerable.Range(0, iced.Width).SelectMany(x => Enumerable.Range(0, (plainTop - icedAt.Y) + 40).Select(y => iced.GetPixel(x, y))).Any(c => c.A > 200 && c.B > 180 && c.R < 160);
            blue.ShouldBeTrue("the ice pack's blue");
        }

        // A cone beside it, the way it faces; left behind while it walks.
        var (coned, conedAt, coneComposed) = OverlayHost.Picture(player, null, standing, null, 96, null, null, 5, null, props, ("cone", 2, null, 0, -1))!.Value;
        using (coneComposed)
        {
            (conedAt.X + coned.Width).ShouldBeGreaterThan(plainRight + 10);
        }

        var walking = standing with { Action = "walk" };
        var (walked, walkedAt, _) = OverlayHost.Picture(player, null, walking, null, 96, null)!.Value;
        var walkedRight = walkedAt.X + walked.Width;
        var (walkedCone, walkedConeAt, last) = OverlayHost.Picture(player, null, walking, null, 96, null, null, 5, null, props, ("cone", 2, null, 0, -1))!.Value;
        using (last)
        {
            (walkedConeAt.X + walkedCone.Width).ShouldBe(walkedRight);
        }
    }

    [Fact]
    public void A_character_is_drawn_with_its_shadow_its_bubble_and_its_shake()
    {
        using var player = CharacterPlayer.Read(OverlayCharacters.Shipped["ren"].Bytes)!;
        var frame = new CharacterMotion.Frame(300, 300, "idle", 0.5, 1);
        var (plain, plainAt, _) = OverlayHost.Picture(player, null, frame, null, 96, null)!.Value;
        var (plainW, plainH, plainTop) = (plain.Width, plain.Height, plainAt.Y);

        // A bubble goes over its head: the picture grows upward and holds its edge's color.
        var said = new CharacterThought(null, null, "CPU at 100 %!", "critical", 1, 5, 1);
        var (spoken, spokenAt, composed) = OverlayHost.Picture(player, null, frame, null, 96, null, said, 0.04)!.Value;
        using (composed)
        {
            spokenAt.Y.ShouldBeLessThan(plainTop);
            spoken.Width.ShouldBeGreaterThanOrEqualTo(plainW);
            spoken.Height.ShouldBeGreaterThan(plainH);
            var red = CharacterExtras.ToneColor("critical");
            var edge = Enumerable.Range(0, spoken.Width).Select(x => spoken.GetPixel(x, (plainTop - spokenAt.Y) / 2)).Any(c => c.A > 200 && Math.Abs(c.R - red.R) < 40 && c.G < 120);
            edge.ShouldBeTrue("the bubble's red edge");
        }

        // Shaking moves it side to side (critical only), and its shadow lies under its feet.
        CharacterExtras.ShakeAt(said, 96, 0.04).ShouldNotBe(0f);
        CharacterExtras.ShakeAt(said with { Shake = 0 }, 96, 0.04).ShouldBe(0f);
        var shadow = CharacterExtras.ShadowAt(new PointF(300, 300), 96);
        (shadow.Top < 300 && shadow.Bottom > 300).ShouldBeTrue();
    }

    [Fact]
    public void Each_moment_shows_its_clips_frame_facing_the_way_it_goes()
    {
        using var player = CharacterPlayer.Read(TestCharacter())!;
        player.Pack.Faces.ShouldBe(-1, "the test drawing faces left");

        // At its own size (9 tall), facing left as drawn: idle frame 0 fills column 0, walk's frames columns 2, 3 and 4.
        var (idle, feet) = player.Frame("idle", 0.1, -1, 9)!.Value;
        (idle.Width, idle.Height).ShouldBe((6, 12));
        feet.ShouldBe(new PointF(3, 11.5f));
        Columns(idle, 6).ShouldBe([0]);
        idle.GetPixel(0, 6).ToArgb().ShouldBe(Color.Red.ToArgb());
        Columns(player.Frame("walk", 0.13, -1, 9)!.Value.Bitmap, 6).ShouldBe([3], "an eighth of a second in: the second frame");
        Columns(player.Frame("walk", 0.4, -1, 9)!.Value.Bitmap, 6).ShouldBe([2], "round again after three eighths");
        Columns(player.Frame("run", 0.25, -1, 9)!.Value.Bitmap, 6).ShouldBe([4], "running plays the walk");
        Columns(player.Frame("sleep", 0.3, -1, 9)!.Value.Bitmap, 6).ShouldBe([1], "sleeping plays the idle");

        // Facing right: mirrored, its feet too.
        var (mirrored, mirroredFeet) = player.Frame("walk", 0.13, 1, 9)!.Value;
        Columns(mirrored, 6).ShouldBe([2]);
        mirroredFeet.ShouldBe(new PointF(3, 11.5f));

        // A frame is decoded once for a size, then kept.
        player.Frame("walk", 0.13, -1, 9)!.Value.Bitmap.ShouldBeSameAs(player.Frame("walk", 0.13 + 0.375, -1, 9)!.Value.Bitmap);
    }

    [Fact]
    public void A_character_is_shown_at_its_size_with_its_feet_on_its_spot()
    {
        foreach (var (id, (bytes, pack)) in OverlayCharacters.Shipped)
        {
            using var player = CharacterPlayer.Read(bytes)!;
            var (frame, feet) = player.Frame("idle", 0, 1, 56)!.Value;
            frame.Height.ShouldBe((int)Math.Round(pack.Height * 56 / pack.Tall), id);

            // Drawn above its feet, nothing below them but its shadow; about 56 pixels tall.
            var solid = Enumerable.Range(0, frame.Height).Where(y => Enumerable.Range(0, frame.Width).Any(x => frame.GetPixel(x, y).A > 200)).ToList();
            solid.Max().ShouldBeLessThanOrEqualTo((int)Math.Ceiling(feet.Y) + 1, id);
            (feet.Y - solid.Min()).ShouldBeInRange(50, 62, id);
            var paws = from y in Enumerable.Range((int)feet.Y - 5, 5) from x in Enumerable.Range(Math.Max(0, (int)feet.X - 25), 50) where x < frame.Width && frame.GetPixel(x, y).A > 200 select x;
            paws.ShouldNotBeEmpty($"{id} stands on its feet");

            // Facing left, mirrored about its middle.
            var (left, leftFeet) = player.Frame("idle", 0, -1, 56)!.Value;
            leftFeet.X.ShouldBe(frame.Width - feet.X, 0.6);
            // (Resampled each way, so alike rather than equal.)
            // A small area around a point above its feet (one pixel can fall either side of an edge).
            static double[] Around(Bitmap b, int x, int y)
            {
                var px = (from dy in Enumerable.Range(-2, 5) from dx in Enumerable.Range(-2, 5) let cx = Math.Clamp(x + dx, 0, b.Width - 1) let cy = Math.Clamp(y + dy, 0, b.Height - 1) select b.GetPixel(cx, cy)).ToList();
                return [px.Average(c => c.A), px.Average(c => c.R), px.Average(c => c.G), px.Average(c => c.B)];
            }

            var (l, r) = (Around(left, frame.Width - 1 - (int)feet.X, (int)feet.Y - 10), Around(frame, (int)feet.X, (int)feet.Y - 10));
            l.Zip(r, (a, b) => Math.Abs(a - b)).Max().ShouldBeLessThan(40, id);
        }
    }

    [Fact]
    public void A_reacting_character_holds_its_status_and_carries_on_along_its_route()
    {
        var state = new CharacterReacting();
        state.Hold(0, "normal").ShouldBe("normal");
        state.Hold(1, "high").ShouldBe("normal");
        state.Hold(2.5, "high").ShouldBe("normal");
        state.Hold(1 + CharacterReacting.StatusHold, "high").ShouldBe("high");
        var clock = new CharacterReacting();
        clock.Advance(10, 1).ShouldBe(10);
        clock.Advance(11, 0).ShouldBe(10);
        clock.Advance(12, 1.8).ShouldBe(11.8, 1e-9);
    }

    [Fact]
    public void A_character_on_a_quiet_pc_sleeps_where_it_is_with_its_zzz_above_its_head()
    {
        CharacterPlayer.Find = id => OverlayCharacters.Shipped.TryGetValue(id, out var shipped) ? CharacterPlayer.Read(shipped.Bytes) : null;
        // At noon (in the morning it would say good morning first).
        CharacterReacting.LocalTime = () => new DateTime(2026, 10, 21, 12, 0, 0);
        try
        {
            var panel = new OverlayPanel("p", null, 0, 0, "rows", [new OverlayReading("cpu.usage")], new OverlayStyle(), Characters:
            [
                new OverlayCharacter("c1", "mikan", Place: "top", Movement: "wander", Size: 80, React: "cpu.usage"),
            ]);
            var sources = new Dictionary<string, OverlaySource> { ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 3) };
            var state = new CharacterReacting();
            var draw = typeof(OverlayHost).GetMethod("DrawCharacters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            Bitmap At(double seconds)
            {
                var frame = new Bitmap(640, 400);
                using var g = Graphics.FromImage(frame);
                draw.Invoke(null, [g, panel, new RectangleF(200, 250, 240, 100), frame.Size, seconds, sources, (Func<string, CharacterReacting>)(_ => state), null, null]);
                return frame;
            }

            static Rectangle Solid(Bitmap b, Func<Color, bool> what)
            {
                var points = (from y in Enumerable.Range(0, b.Height) from x in Enumerable.Range(0, b.Width) where what(b.GetPixel(x, y)) select new Point(x, y)).ToList();
                return points.Count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) + 1, points.Max(p => p.Y) + 1);
            }

            // Asleep (curled up, low on the panel's top edge), and still there seconds later.
            using var first = At(30.0);
            using var later = At(36.0);
            static bool Fur(Color c) => c.A > 200 && c.R > 200 && c.G > 150 && c.B < 200;
            var body = Solid(first, Fur);
            body.ShouldNotBe(Rectangle.Empty);
            var then = Solid(later, Fur);
            Math.Abs((then.Left + then.Right) - (body.Left + body.Right)).ShouldBeLessThanOrEqualTo(16, "the same spot (it breathes and its tail moves)");
            Math.Abs(then.Bottom - body.Bottom).ShouldBeLessThanOrEqualTo(2);
            (body.Height < body.Width * 0.7).ShouldBeTrue("curled up asleep");

            // Its zZ: the effect's ink (a dark blue outline) above the top of its body.
            static bool Ink(Color c) => c.A > 150 && c.B > c.R + 30 && c.B > 90 && c.R < 120;
            var zzz = Solid(first, Ink);
            zzz.ShouldNotBe(Rectangle.Empty);
            zzz.Top.ShouldBeLessThan(body.Top);
        }
        finally
        {
            CharacterPlayer.Find = null;
            CharacterReacting.LocalTime = () => DateTime.Now;
        }
    }

    [Fact]
    public void A_usb_screens_characters_are_drawn_into_its_picture_around_their_panel()
    {
        CharacterPlayer.Find = id => OverlayCharacters.Shipped.TryGetValue(id, out var shipped) ? CharacterPlayer.Read(shipped.Bytes) : null;
        try
        {
            using var frame = new Bitmap(480, 300);
            using (var g = Graphics.FromImage(frame))
            {
                var panel = new OverlayPanel("p", null, 0, 0, "rows", [], new OverlayStyle(), Characters:
                [
                    new OverlayCharacter("c1", "mochi", Place: "top", Movement: "stay", X: 0.5, Size: 60),
                    new OverlayCharacter("c2", "biscuit", Place: "screen", Movement: "stay", X: 0, Size: 40, Opacity: 60),
                    new OverlayCharacter("c3", "0123456789abcdef.char", Place: "inside", Movement: "stay", Size: 40),
                ]);
                var draw = typeof(OverlayHost).GetMethod("DrawCharacters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
                draw.Invoke(null, [g, panel, new RectangleF(140, 150, 200, 80), frame.Size, 1.0, null, null, null, null]);
            }

            // One on the panel's top edge (its middle), the other on the screen's bottom at the left, faded; the one not
            // here is left out.
            Enumerable.Range(220, 40).Any(x => frame.GetPixel(x, 140).A > 200).ShouldBeTrue("above the panel");
            frame.GetPixel(240, 200).A.ShouldBe((byte)0, "not over the panel's middle");
            var bottom = Enumerable.Range(0, 60).Select(x => frame.GetPixel(x, 290).A).ToList();
            bottom.Max().ShouldBeInRange((byte)100, (byte)170, "at the bottom, at 60 %");
        }
        finally
        {
            CharacterPlayer.Find = null;
        }
    }
}
