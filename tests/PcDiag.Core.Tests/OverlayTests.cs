using PcDiag.Core.Overlay;

namespace PcDiag.Core.Tests;

public sealed class OverlayTests
{
    private static OverlayPanel Panel(string id = "panel-1", double x = 1, OverlayStyle? style = null, params OverlayReading[] readings) =>
        new(id, "System", x, 0, "rows", readings.Length > 0 ? readings : [new("cpu.usage")], style ?? new OverlayStyle());

    private static OverlayConfig Config(params OverlayPanel[] panels) => new(Enabled: true, Screens: [new OverlayScreen("AUS32F4/1", "PG32UCDP", panels)]);

    private static readonly System.Text.Json.JsonSerializerOptions WebJson = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public void Panels_have_characters_shipped_or_imported_checked_and_kept_with_the_layout()
    {
        // A panel's characters are shipped ones or imported ones (a library file), with a place, a movement and an
        // action each, sizes and speeds in range, 8 at most.
        OverlayCharacter Kitten(string id = "c1") => new(id, "mochi", "around", "stay", "sleep", 0.3, 64, 120, true, true, 80);
        OverlayPanel With(params OverlayCharacter[] characters) => new("p1", null, 0, 0, "rows", [new OverlayReading("cpu.usage")], new OverlayStyle(), Characters: characters);
        OverlayStyles.Validate(new OverlayConfig(Screens: [new("S", "Screen", [With(Kitten(), Kitten("c2") with { Character = "0123456789abcdef.char", Place = "screen", Movement = "wander", Action = "dance" })])])).ShouldBeNull();
        OverlayCharacters.Check(With(Kitten() with { Character = "dragon" }))!.ShouldContain("no character dragon");
        OverlayCharacters.Check(With(Kitten() with { Character = "0123456789abcdef.anim" })).ShouldNotBeNull("a picture is not a character");
        OverlayCharacters.Check(With(Kitten() with { Place = "ceiling" })).ShouldNotBeNull();
        OverlayCharacters.Check(With(Kitten() with { Action = "Fly!" })).ShouldNotBeNull();
        OverlayCharacters.Check(With(Kitten() with { Action = "wave-hello", Plays = ["idle", "worried", "wave-hello"] })).ShouldBeNull("new animations and the emotions are actions");
        OverlayCharacters.Check(With(Kitten() with { Size = 500 })).ShouldNotBeNull();
        OverlayCharacters.Check(With(Kitten() with { Speed = 10 })).ShouldNotBeNull();
        OverlayCharacters.Check(With(Kitten(), Kitten())).ShouldNotBeNull("the same id twice");
        OverlayCharacters.Check(With([.. Enumerable.Range(0, 9).Select(i => Kitten($"c{i}"))])).ShouldNotBeNull("more than 8");
        OverlayStyles.Validate(new OverlayConfig(Screens: [new("S", "Screen", [With(Kitten() with { Character = "dragon" })])]))!.ShouldContain("characters:");
        OverlayStyles.IsImageId("0123456789abcdef.char").ShouldBeFalse("a character is not a panel's picture");

        // Kept with the layout as the editor writes it.
        var json = System.Text.Json.JsonSerializer.Serialize(With(Kitten()), WebJson);
        json.ShouldContain("\"characters\":[{\"id\":\"c1\",\"character\":\"mochi\",\"place\":\"around\"");
        System.Text.Json.JsonSerializer.Deserialize<OverlayPanel>(json, WebJson)!.Characters!.Single().ShouldBe(Kitten());
        new OverlayConfig().Characters.ShouldBeTrue();
    }

    [Fact]
    public void A_reading_shown_as_a_percentage_is_judged_by_that_percentage()
    {
        // VRAM in bytes, shown as a percentage of the card's memory: its limits are percentages.
        var vram = new OverlayReading("gpu.g1.vram", Show: "percent", Warning: 90, Critical: 97);
        OverlayStyles.Level(vram, 8e9, 25e9).ShouldBe(0);
        OverlayStyles.Level(vram, 23e9, 25e9).ShouldBe(1);
        OverlayStyles.Level(vram, 24.5e9, 25e9).ShouldBe(2);

        // Shown as a value (or no total known), its limits are values.
        OverlayStyles.Level(new OverlayReading("cpu.temp", Warning: 85), 90, 100).ShouldBe(1);
        OverlayStyles.Level(vram, 8e9).ShouldBe(2);
    }

    [Fact]
    public void An_animation_is_read_back_as_written_and_anything_else_is_refused()
    {
        byte[] Jpeg(byte fill) => [0xFF, 0xD8, 0xFF, 0xE0, fill, fill, 0xFF, 0xD9];
        var bytes = OverlayAnimation.Write(12, 480, 1920, [Jpeg(1), Jpeg(2), Jpeg(3)]);
        var animation = OverlayAnimation.Read(bytes).ShouldNotBeNull();
        (animation.Fps, animation.Width, animation.Height, animation.Frames.Count).ShouldBe((12, 480, 1920, 3));
        animation.Duration.ShouldBe(TimeSpan.FromSeconds(0.25));
        bytes.AsSpan(animation.Frames[1].Offset, animation.Frames[1].Length).ToArray().ShouldBe(Jpeg(2));

        // The loop: frame by the clock, round and round.
        animation.FrameAt(0).ShouldBe(0);
        animation.FrameAt(1.0 / 12 + 0.001).ShouldBe(1);
        animation.FrameAt(0.25 + 0.001).ShouldBe(0);

        // Not an animation: another file, a cut one, a frame that is not a JPEG, too many frames.
        OverlayAnimation.Read("PNG-------------"u8).ShouldBeNull();
        OverlayAnimation.Read(bytes.AsSpan(0, bytes.Length - 1)).ShouldBeNull();
        var bad = (byte[])bytes.Clone();
        bad[animation.Frames[0].Offset] = 0;
        OverlayAnimation.Read(bad).ShouldBeNull();
        OverlayAnimation.Read(OverlayAnimation.Write(12, 10, 10, Enumerable.Repeat(Jpeg(1), OverlayAnimation.MaxFrames + 1).ToList())).ShouldBeNull();
        OverlayStyles.IsImageId("0123456789abcdef.anim").ShouldBeTrue();
        OverlayStyles.IsImageId("0123456789abcdef.gif").ShouldBeFalse();
    }

    [Fact]
    public void A_character_dropped_on_the_screen_stays_there_or_keeps_to_the_area_around_it()
    {
        var c = new OverlayCharacter("c1", "mikan", "top", Movement: "stay", Draggable: true);
        c.DroppedAt(0.8).ShouldSatisfyAllConditions(d => d.X.ShouldBe(0.8), d => d.Movement.ShouldBe("stay"));
        var wanderer = (c with { Movement = "wander" }).DroppedAt(0.9);
        (wanderer.Movement, wanderer.From, wanderer.To).ShouldBe(("wander", 0.7, 1.0));
        var left = (c with { Movement = "pace" }).DroppedAt(-1);
        (left.From, left.To).ShouldBe((0, 0.3));
        OverlayCharacters.Check(new OverlayPanel("p", null, 0, 0, "rows", [], new OverlayStyle(), Characters: [wanderer])).ShouldBeNull();
        (c with { }).ShouldBe(c);
        (c with { Draggable = false }).ShouldNotBe(c);

        // Dropped at a point of its screen: it plays there.
        var spot = c.DroppedAt(0.25, 0.4);
        (spot.Place, spot.X, spot.Y, spot.Movement).ShouldBe(("spot", 0.25, 0.4, "stay"));
        var walker = (c with { Movement = "wander" }).DroppedAt(0.5, 0.9);
        (walker.Place, walker.Y).ShouldBe(("spot", 0.9));
        walker.From.ShouldBe(0.35, 1e-9);
        walker.To.ShouldBe(0.65, 1e-9);
        OverlayCharacters.Check(new OverlayPanel("p", null, 0, 0, "rows", [], new OverlayStyle(), Characters: [walker])).ShouldBeNull();
        OverlayCharacters.Check(new OverlayPanel("p", null, 0, 0, "rows", [], new OverlayStyle(), Characters: [walker with { Y = 2 }])).ShouldNotBeNull();
    }

    [Fact]
    public void A_screen_has_characters_of_its_own_that_react_to_all_its_panels()
    {
        var kitty = new OverlayCharacter("c1", "mikan", "screen");
        OverlayPanel Panel(string id, params string[] sources) => new(id, null, 0, 0, "rows", [.. sources.Select(s => new OverlayReading(s))], new OverlayStyle());
        var screen = new OverlayScreen("S", "Screen", [Panel("a", "cpu.usage"), Panel("b", "cpu.usage", "gpu.g1.temp"), Panel("c", "net.up") with { Hidden = true }], Characters: [kitty, kitty with { Id = "c2", Place = "top" }]);
        var stage = screen.Stage(portrait: false)!;
        stage.Id.ShouldBe(OverlayScreen.StageId);
        stage.Readings.Select(r => r.Source).ShouldBe(["cpu.usage", "gpu.g1.temp"]);
        stage.Characters!.Count.ShouldBe(2);
        (screen with { Characters = null }).Stage(false).ShouldBeNull();
        new OverlayConfig(Screens: [screen]).StageFor(new OverlayDisplay("S", "Screen", 1, 0, 0, 1920, 1080, 1, true, 60)).ShouldNotBeNull();

        OverlayStyles.Validate(new OverlayConfig(Screens: [screen])).ShouldBeNull();
        OverlayStyles.Validate(new OverlayConfig(Screens: [screen with { Characters = [kitty with { Place = "around" }] }]))!.ShouldContain("screen characters");
        OverlayStyles.Validate(new OverlayConfig(Screens: [screen with { Characters = [kitty, kitty] }]))!.ShouldContain("screen characters");
    }

    [Fact]
    public void A_sound_layout_passes_and_each_mistake_is_named()
    {
        OverlayStyles.Validate(Config(Panel())).ShouldBeNull();
        OverlayStyles.Validate(OverlayConfig.Empty).ShouldBeNull();
        foreach (var preset in OverlayStyles.Presets.Values)
        {
            OverlayStyles.Validate(Config(Panel(style: preset))).ShouldBeNull();
        }

        OverlayStyles.Validate(Config(Panel()) with { IntervalMs = 7 })!.ShouldContain("intervalMs");
        OverlayStyles.Validate(Config(Panel()) with { Hotkey = "O" })!.ShouldContain("hotkey");
        OverlayStyles.Validate(Config(Panel(x: 1.5)))!.ShouldContain("x and y");
        OverlayStyles.Validate(Config(Panel(), Panel()))!.ShouldContain("unique");
        OverlayStyles.Validate(Config(Panel(id: "../x")))!.ShouldContain("id is");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(Font: "Comic Sans MS"))))!.ShouldContain("font");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(ValueColor: "red"))))!.ShouldContain("#RRGGBB");
        OverlayStyles.Validate(Config(Panel(readings: new OverlayReading("cpu.usage", Decimals: 9))))!.ShouldContain("decimals");
        OverlayStyles.Validate(Config(Panel(readings: new OverlayReading("cpu\u0001"))))!.ShouldContain("source");
        OverlayStyles.Validate(Config(Enumerable.Range(1, OverlayStyles.MaxPanels + 1).Select(i => Panel($"p{i}")).ToArray()))!.ShouldContain("panels per screen");
    }

    [Fact]
    public async Task A_layout_is_saved_while_another_program_reads_it_and_an_unreadable_file_is_not_an_empty_layout()
    {
        // The recorder and the Web host both read and save overlay.json; a save replacing it while the other reads it
        // was refused ("Access to the path is denied") and a read meanwhile gave the empty layout (the panels vanished).
        var folder = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "overlay.json");
        try
        {
            var one = Config(Panel("one"));
            var two = Config(Panel("two"));
            OverlayStyles.Write(path, one);

            // Held open by a reader for a moment (as a reader does, sharing or not): the save waits for it and goes ahead.
            foreach (var (share, next) in new[] { (FileShare.ReadWrite | FileShare.Delete, two), (FileShare.Read, one) })
            {
                var held = new FileStream(path, FileMode.Open, FileAccess.Read, share);
                var release = Task.Run(async () =>
                {
                    await Task.Delay(120);
                    await held.DisposeAsync();
                });
                OverlayStyles.Write(path, next);
                await release;
                OverlayStyles.Read(path, out _).PanelsOf("AUS32F4/1").ShouldHaveSingleItem().Id.ShouldBe(next.PanelsOf("AUS32F4/1")[0].Id);
            }

            // Saving and reading at once, many times: every read is a whole layout, never an error or the empty one.
            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    OverlayStyles.Write(path, i % 2 == 0 ? one : two);
                }
            });
            var reads = 0;
            while (!writer.IsCompleted || reads < 200)
            {
                OverlayStyles.TryRead(path, out var read, out var problem).ShouldBeTrue(problem);
                problem.ShouldBeNull();
                read.PanelsOf("AUS32F4/1").ShouldHaveSingleItem();
                OverlayStyles.Revision(path).ShouldNotBe("unreadable");
                reads++;
            }

            await writer;
            Directory.GetFiles(folder, "*.tmp").ShouldBeEmpty();

            // Locked for longer than a moment: unreadable, not "no layout" (the caller keeps the one it has).
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                OverlayStyles.TryRead(path, out var locked, out var problem).ShouldBeFalse();
                problem.ShouldNotBeNull();
                locked.ShouldBe(OverlayConfig.Empty);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_layout_survives_the_file_and_a_broken_file_gives_the_empty_layout()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "overlay.json");
        try
        {
            var config = Config(Panel(readings: new OverlayReading("gpu.g.temp", "GPU", 1, 80, 90, Bar: true, Max: 110, Color: "#22D3EE")));
            OverlayStyles.Write(path, config);
            var read = OverlayStyles.Read(path, out var problem);
            problem.ShouldBeNull();
            read.PanelsOf("AUS32F4/1").ShouldHaveSingleItem().Readings.ShouldHaveSingleItem().ShouldBe(config.PanelsOf("AUS32F4/1")[0].Readings[0]);
            read.PanelsOf("other").ShouldBeEmpty();

            File.WriteAllText(path, "{ not json");
            OverlayStyles.Read(path, out problem).ShouldBe(OverlayConfig.Empty);
            problem.ShouldNotBeNull();
            OverlayStyles.Read(Path.Combine(folder, "missing.json"), out problem).ShouldBe(OverlayConfig.Empty);
            problem.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("%", 52.4, null, "52", "%")]
    [InlineData("%", 52.4, 1, "52.4", "%")]
    [InlineData("B", 3.4 * 1073741824, null, "3.4", "GB")]
    [InlineData("B", 512.0 * 1048576, null, "512", "MB")]
    [InlineData("B/s", 19.5 * 1048576, null, "19.5", "MB/s")]
    [InlineData("B/s", 300.0 * 1024, null, "300", "KB/s")]
    [InlineData("GHz", 4.783, null, "4.78", "GHz")]
    [InlineData("V", 3.088, null, "3.088", "V")]
    [InlineData("°C", 61.6, null, "62", "°C")]
    [InlineData("", 7.25, null, "7.3", "")]
    [InlineData("B/s", 999.7 * 1024, null, "1.0", "MB/s")]
    [InlineData("B/s", 1010.0 * 1048576, null, "0.99", "GB/s")]
    [InlineData("B", 1000.0 * 1048576, null, "1.0", "GB")]
    [InlineData("ms", 99.94, null, "99.9", "ms")]
    [InlineData("ms", 99.96, null, "100", "ms")]
    public void Values_are_formatted_with_their_unit_scaled(string unit, double value, int? decimals, string text, string shownUnit) =>
        OverlayStyles.Format(new OverlaySource("x", "X", "G", unit, value), decimals).ShouldBe((text, shownUnit));

    [Theory]
    [InlineData("%", null, "000", "%")]
    [InlineData("fps", null, "000", "fps")]
    [InlineData("ms", null, "00.0 000", "ms")]
    [InlineData("°C", null, "000", "°C")]
    [InlineData("B/s", null, "000 0.0 00.0 000.0 0.00 00.00", "KB/s MB/s GB/s")]
    [InlineData("B", 32.0 * 1073741824, "000 0.0 00.0", "KB MB GB")]
    public void A_value_keeps_room_for_the_widest_it_can_read(string unit, double? max, string values, string units)
    {
        var (wide, wideUnits) = OverlayStyles.Widest(new OverlaySource("x", "X", "G", unit, 5, Max: max), new OverlayReading("x"));
        foreach (var value in values.Split(' '))
        {
            wide.ShouldContain(value);
        }

        // Never four digits before the point (the next unit reads it).
        wide.ShouldAllBe(v => !v.Contains("0000", StringComparison.Ordinal));
        wideUnits.ShouldBe(units.Split(' '), ignoreOrder: true);
        OverlayStyles.Widest(new OverlaySource("t", "T", "G", string.Empty, null, "12:00"), new OverlayReading("t")).Values.ShouldBeEmpty();
    }

    [Fact]
    public void A_percentage_of_a_value_with_its_total_reads_as_either_its_limits_following()
    {
        OverlayStyles.ValueOf("gpu.g1.vram_pct").ShouldBe("gpu.g1.vram");
        OverlayStyles.ValueOf("memory.usage").ShouldBe("memory.used");
        OverlayStyles.ValueOf("cpu.usage").ShouldBeNull();

        var pct = new OverlaySource("gpu.g1.vram_pct", "VRAM", "RTX 4090", "%", 14);
        var vram = new OverlaySource("gpu.g1.vram", "VRAM", "RTX 4090", "B", 3.4 * (1L << 30), Max: 24.0 * (1L << 30));
        var reading = new OverlayReading("gpu.g1.vram_pct", Warning: 80, Critical: 95);
        OverlayStyles.ReadsAs(reading, pct).ShouldBe("percent");

        // As its value: VRAM in use, 80 % and 95 % of 24 GB its limits; as both, the same source.
        var value = OverlayStyles.ReadAs(reading, pct, vram, "value");
        value.Source.ShouldBe("gpu.g1.vram");
        value.Show.ShouldBeNull();
        value.Warning.ShouldBe(Math.Round(0.8 * 24 * (1L << 30)));
        value.Critical.ShouldBe(Math.Round(0.95 * 24 * (1L << 30)));
        OverlayStyles.ReadsAs(value, vram).ShouldBe("value");
        OverlayStyles.ReadAs(reading, pct, vram, "both").Show.ShouldBe("both");

        // A percentage stays one; from the value's source it reads as a percentage of the total; without a total nothing
        // switches.
        OverlayStyles.ReadAs(reading, pct, vram, "percent").ShouldBe(reading with { Show = null });
        OverlayStyles.ReadAs(value, vram, null, "percent").ShouldBe(value with { Show = "percent" });
        OverlayStyles.Shown(vram, value with { Show = "percent" }).ShouldBe(("14", "%"));
        OverlayStyles.ReadAs(reading, pct, vram with { Max = null }, "value").Source.ShouldBe("gpu.g1.vram_pct");
    }

    [Theory]
    // The same answers as the editor's readableOn (so both draw the same colors).
    // The line panel's units: slate on a dark panel, moved toward the value color until readable.
    [InlineData("#64748B", "#0F172A", "#F1F5F9", "#728196")]
    // Already readable: unchanged.
    [InlineData("#CBD5E1", "#0F172A", "#FFFFFF", "#CBD5E1")]
    [InlineData("#94A3B8", "#1E1B4B", "#FFFFFF", "#94A3B8")]
    // A light panel: a pale label darkened toward the dark value color.
    [InlineData("#CBD5E1", "#FFFFFF", "#0F172A", "#6E7787")]
    public void Labels_and_units_keep_a_readable_contrast_on_the_panel(string color, string background, string value, string expected)
    {
        var readable = OverlayStyles.Readable(color, background, value);
        readable.ShouldBe(expected);
        var style = new OverlayStyle(Background: background, ValueColor: value, BackgroundOpacity: 72);
        OverlayStyles.Readable(color, style).ShouldBe(expected);

        // A panel with little background shows what is behind it: its colors are kept.
        OverlayStyles.Readable(color, style with { BackgroundOpacity = 20 }).ShouldBe(color);
    }

    [Fact]
    public void A_text_keeps_its_declared_space_and_values_sit_left_in_the_middle_or_right()
    {
        OverlayStyles.Widest(new OverlaySource("t", "T", "G", string.Empty, null, "12:00", Room: ["00:00"]), new OverlayReading("t")).Values.ShouldBe(["00:00"]);
        new OverlayStyle().ValueAlign.ShouldBe("center");
        OverlayStyles.Zeros("Wed 24 May 12.5").ShouldBe("Wed 00 May 00.0");
        foreach (var align in new[] { "left", "center", "right" })
        {
            OverlayStyles.Validate(Config(new OverlayStyle(ValueAlign: align))).ShouldBeNull(align);
        }

        OverlayStyles.Validate(Config(new OverlayStyle(ValueAlign: "middle"))).ShouldNotBeNull();

        static OverlayConfig Config(OverlayStyle style) => new(Screens: [new OverlayScreen("k", "n", [new OverlayPanel("p", null, 0, 0, "rows", [], style)])]);
    }

    [Fact]
    public void Text_and_missing_values_and_limits()
    {
        OverlayStyles.Format(new OverlaySource("time.clock", "Time", "PC", string.Empty, null, "01:53"), null).ShouldBe(("01:53", string.Empty));
        OverlayStyles.Format(new OverlaySource("cpu.temp", "CPU temp", "CPU", "°C", null), null).ShouldBe(("—", "°C"));
        OverlayStyles.Level(new OverlayReading("x", Warning: 80, Critical: 90), 85).ShouldBe(1);
        OverlayStyles.Level(new OverlayReading("x", Warning: 80, Critical: 90), 95).ShouldBe(2);
        OverlayStyles.Level(new OverlayReading("x", Warning: 1000, Critical: 500, Below: true), 400).ShouldBe(2);
        OverlayStyles.Level(new OverlayReading("x", Warning: 80), null).ShouldBe(0);
    }

    [Fact]
    public void The_redesigned_looks_layouts_visuals_and_icons_are_checked()
    {
        var tiles = Panel(readings: [new("gpu.a.usage", Visual: "ring"), new("cpu.usage", Visual: "graph", Icon: "cpu"), new("memory.usage", Icon: "none")]) with { Layout = "tiles", Columns = 3 };
        OverlayStyles.Validate(Config(tiles)).ShouldBeNull();

        OverlayStyles.Validate(Config(tiles with { Layout = "grid" }))!.ShouldContain("rows, line or tiles");
        OverlayStyles.Validate(Config(tiles with { Columns = 7 }))!.ShouldContain("columns");
        OverlayStyles.Validate(Config(Panel(readings: new OverlayReading("cpu.usage", Visual: "pie"))))!.ShouldContain("visual");
        OverlayStyles.Validate(Config(Panel(readings: new OverlayReading("cpu.usage", Icon: "rocket"))))!.ShouldContain("icon");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(Accent: "cyan"))))!.ShouldContain("#RRGGBB");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(Background2: "#12345"))))!.ShouldContain("#RRGGBB");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(Glow: 120))))!.ShouldContain("glow");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(ValueScale: 400))))!.ShouldContain("value size");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(AccentEdge: "right"))))!.ShouldContain("accent edge");
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(BarStyle: "dots"))))!.ShouldContain("bar style");
    }

    [Fact]
    public void An_older_layout_keeps_its_look_and_its_bars()
    {
        // Written before the redesign: no visual, icon, columns or new style fields.
        var json = """{"version":1,"enabled":true,"screens":[{"key":"k","name":"n","panels":[{"id":"p","title":null,"x":1,"y":0,"layout":"rows","readings":[{"source":"cpu.usage","bar":true}],"style":{"font":"Segoe UI","size":14,"bold":true,"labelColor":"#9CA3AF","valueColor":"#FFFFFF","unitColor":"#9CA3AF","titleColor":"#38BDF8","warningColor":"#F59E0B","criticalColor":"#EF4444","background":"#0B1220","backgroundOpacity":70,"border":null,"cornerRadius":8,"padding":10,"opacity":100,"outline":false,"showLabels":true,"showUnits":true}}]}]}""";
        var file = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
        try
        {
            var config = OverlayStyles.Read(file, out var problem);
            problem.ShouldBeNull();
            var panel = config.PanelsOf("k").ShouldHaveSingleItem();
            panel.Columns.ShouldBe(2);
            OverlayStyles.VisualOf(panel.Readings[0]).ShouldBe("bar");
            panel.Style.ShouldBe(new OverlayStyle());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Icons_are_chosen_by_what_a_reading_measures_and_the_editor_has_the_same_ones()
    {
        OverlayIcons.Auto("gpu.a.temp", "°C").ShouldBe("temp");
        OverlayIcons.Auto("gpu.a.vram", "B").ShouldBe("memory");
        OverlayIcons.Auto("gpu.a.usage", "%").ShouldBe("gpu");
        OverlayIcons.Auto("cpu.clock", "GHz").ShouldBe("cpu");
        OverlayIcons.Auto("net.e.up", "B/s").ShouldBe("up");
        OverlayIcons.Auto("time.date", string.Empty).ShouldBe("calendar");
        OverlayIcons.Auto("sensor.lhm/fan/1", "RPM").ShouldBe("fan");
        OverlayIcons.Auto("sensor.x", "%").ShouldBe("gauge");
        OverlayStyles.IconOf(new OverlayReading("cpu.usage", Icon: "none"), null).ShouldBeNull();
        OverlayStyles.IconOf(new OverlayReading("cpu.usage", Icon: "fan"), null).ShouldBe("fan");

        // The editor draws the icons from its own copy: it must be this list.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PcDiagnostic.slnx")))
        {
            root = root.Parent;
        }

        var script = File.ReadAllText(Path.Combine(root.ShouldNotBeNull().FullName, "web", "src", "features", "overlay", "icons.ts"));
        var start = script.IndexOf("export const ICON_PATHS = {", StringComparison.Ordinal);
        var block = script[start..script.IndexOf("} as const;", start, StringComparison.Ordinal)];
        var editor = System.Text.RegularExpressions.Regex.Matches(block, @"^\s+(\w+): '([^']+)',?$", System.Text.RegularExpressions.RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        editor.ShouldBe(OverlayIcons.Paths.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void A_value_reads_as_a_percentage_both_or_of_its_total()
    {
        var vram = new OverlaySource("gpu.a.vram", "VRAM", "RTX 4090", "B", 22.1 * (1L << 30), Max: 24.0 * (1L << 30));

        OverlayStyles.Shown(vram, new OverlayReading("gpu.a.vram")).ShouldBe(("22.1", "GB"));
        OverlayStyles.Shown(vram, new OverlayReading("gpu.a.vram", Show: "percent")).ShouldBe(("92", "%"));
        OverlayStyles.Shown(vram, new OverlayReading("gpu.a.vram", Show: "both")).ShouldBe(("22.1", "GB · 92%"));
        OverlayStyles.Shown(vram, new OverlayReading("gpu.a.vram", Show: "of")).ShouldBe(("22.1/24.0", "GB"));
        OverlayStyles.MaxOf(new OverlayReading("gpu.a.vram"), vram).ShouldBe(24.0 * (1L << 30));

        // Without a total (or already a percentage) it reads as its value.
        var temp = new OverlaySource("cpu.temp", "CPU temp", "Processor", "°C", 61);
        OverlayStyles.Shown(temp, new OverlayReading("cpu.temp", Show: "percent")).ShouldBe(("61", "°C"));
        OverlayStyles.Shown(temp, new OverlayReading("cpu.temp", Show: "of", Max: 100)).ShouldBe(("61/100", "°C"));
        var usage = new OverlaySource("cpu.usage", "CPU", "Processor", "%", 37.4);
        OverlayStyles.Shown(usage, new OverlayReading("cpu.usage", Show: "both")).ShouldBe(("37", "%"));

        OverlayStyles.Validate(Config(Panel(readings: new OverlayReading("cpu.usage", Show: "ratio"))))!.ShouldContain("show");
        OverlayStyles.Validate(Config(Panel(readings: new OverlayReading("cpu.usage", ColSpan: 7))))!.ShouldContain("spans");
        OverlayStyles.Validate(Config(Panel() with { Width = 10 }))!.ShouldContain("width");
        OverlayStyles.Validate(Config(Panel() with { Width = 300, Height = 120 })).ShouldBeNull();
        OverlayStyles.Validate(Config(Panel(style: new OverlayStyle(TileOpacity: 120))))!.ShouldContain("tile opacity");
    }

    [Fact]
    public void Frame_rates_count_the_last_second_and_the_1_percent_low_the_slowest_frames()
    {
        const long Second = 10_000_000;
        var frames = new FrameTimes(Second);

        // A game at 120 fps for 10 s with a 50 ms stutter every second; a browser at 60 fps.
        long t = 0;
        for (var i = 0; i < 1200; i++)
        {
            t += i % 120 == 119 ? Second / 20 : Second / 120;
            frames.Add(4242, t);
        }

        for (long b = 0; b < 10 * Second; b += Second / 60)
        {
            frames.Add(77, b);
        }

        // Read as the session does: 0.2 s after the last frame came in.
        var game = frames.Stats(t + (Second / 5), foreground: 4242)!.Value;
        game.ProcessId.ShouldBe(4242);
        game.Foreground.ShouldBeTrue();
        game.Fps.ShouldBeInRange(110, 121);
        game.FrameTimeMs.ShouldBeInRange(8, 9.5);
        game.Low1Fps!.Value.ShouldBeInRange(18, 22); // the 50 ms stutters: 20 fps

        // Nothing in front draws frames: the busiest app that does.
        frames.Stats(t, foreground: 999)!.Value.ProcessId.ShouldBe(4242);

        // An app that stopped presenting is forgotten.
        frames.Stats(t + (40 * Second), foreground: 4242).ShouldBeNull();
    }

    [Fact]
    public void Apps_without_runtime_presents_are_counted_from_the_graphics_kernel_once_per_frame()
    {
        const long Second = 10_000_000;
        var frames = new FrameTimes(Second);
        long t = 0;
        for (var i = 0; i < 300; i++)
        {
            t += Second / 100;

            // A Vulkan game at 100 fps: two kernel events per present (its flip and its history), counted once.
            frames.AddKernel(10, 168, t);
            frames.AddKernel(10, 171, t + 5);

            // A DirectX game at 100 fps: its runtime's presents count, not the kernel's as well.
            frames.Add(20, t);
            frames.AddKernel(20, 215, t + 7);
        }

        var vulkan = frames.Stats(t + (Second / 5), foreground: 10)!.Value;
        vulkan.ProcessId.ShouldBe(10);
        vulkan.Fps.ShouldBeInRange(98, 101);
        vulkan.FrameTimeMs.ShouldBeInRange(9.5, 10.5);
        frames.Stats(t + (Second / 5), foreground: 20)!.Value.Fps.ShouldBeInRange(98, 101);
    }

    [Fact]
    public void Frames_not_all_in_yet_or_on_another_clock_do_not_change_the_rate()
    {
        const long Second = 10_000_000;
        var frames = new FrameTimes(Second);
        long t = 0;
        for (var i = 0; i < 600; i++)
        {
            frames.Add(1, t += Second / 60);
        }

        // Read 0.2 s after the last frame (as the session hands them over), and right at it: 60 fps both times.
        frames.Stats(t + (Second / 5), foreground: 1)!.Value.Fps.ShouldBeInRange(59, 61);
        frames.Stats(t, foreground: 1)!.Value.Fps.ShouldBeInRange(59, 61);

        // A clock far behind the frames' (system time against QPC): no reading, not every frame kept counted at once.
        frames.Stats(t / 1000, foreground: 1).ShouldBeNull();
    }

    [Fact]
    public void Pictures_patterns_and_the_arrange_hotkey_are_checked_and_one_panel_can_be_changed()
    {
        static OverlayConfig With(OverlayStyle style, string arrange = OverlayConfig.DefaultArrangeHotkey) =>
            new(ArrangeHotkey: arrange, Screens: [new OverlayScreen("k", "n", [new OverlayPanel("p", "P", 0, 0, "rows", [new OverlayReading("cpu.usage")], style), new OverlayPanel("q", "Q", 1, 1, "rows", [], style)])]);

        OverlayStyles.Validate(With(new OverlayStyle(Image: "0123456789abcdef.jpg", ImageFit: "tile", ImageBlur: 30, ImageTint: "#000000", Pattern: "hexagons", PatternScale: 400))).ShouldBeNull();
        OverlayStyles.Validate(With(new OverlayStyle(Image: "../overlay.json")))!.ShouldContain("image");
        OverlayStyles.Validate(With(new OverlayStyle(ImageFit: "zoom")))!.ShouldContain("image fit");
        OverlayStyles.Validate(With(new OverlayStyle(ImageBlur: 31)))!.ShouldContain("blur");
        OverlayStyles.Validate(With(new OverlayStyle(Pattern: "zigzag")))!.ShouldContain("pattern");
        OverlayStyles.Validate(With(new OverlayStyle(), arrange: "Ctrl+Alt+Shift+O"))!.ShouldContain("differ");
        OverlayStyles.Validate(With(new OverlayStyle(), arrange: string.Empty)).ShouldBeNull();

        var config = With(new OverlayStyle());
        var changed = config.WithPanel("k", "p", p => p with { Hidden = true });
        changed.PanelsOf("k").Select(p => p.Hidden).ShouldBe([true, false]);
        config.WithPanel("k", "q", _ => null).PanelsOf("k").ShouldHaveSingleItem().Id.ShouldBe("p");
        config.WithPanel("other", "p", _ => null).PanelsOf("k").Count.ShouldBe(2);

        var file = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            OverlayStyles.Revision(file).ShouldBe("none");
            OverlayStyles.Write(file, config);
            var first = OverlayStyles.Revision(file);
            OverlayStyles.Write(file, changed);
            OverlayStyles.Revision(file).ShouldNotBe(first);
            first.Length.ShouldBe(16);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_screen_turned_upright_shows_its_portrait_panels_and_ids_are_unique_across_both()
    {
        var wide = Panel("panel-1");
        var tall = Panel("panel-2") with { Width = 400, Height = 900 };
        var config = new OverlayConfig(Enabled: true, Screens: [new OverlayScreen("AUS32F4/1", "PG32UCDP", [wide], [tall])]);
        OverlayStyles.Validate(config).ShouldBeNull();

        // By its shape now: upright (taller than wide) shows the portrait panels, wide the others.
        var landscape = new OverlayDisplay("AUS32F4/1", "PG32UCDP", 1, 0, 0, 3840, 2160, 1.5, true, 240);
        var portrait = landscape with { Width = 2160, Height = 3840, Rotation = 90 };
        config.PanelsFor(landscape).ShouldBe([wide]);
        config.PanelsFor(portrait).ShouldBe([tall]);
        portrait.Orientation.ShouldBe("portrait");
        landscape.Orientation.ShouldBe("landscape");
        config.EveryPanel().Select(p => p.Id).ShouldBe(["panel-1", "panel-2"]);

        // Without portrait panels, the same panels both ways; a change finds the panel in either list.
        (config with { Screens = [new OverlayScreen("AUS32F4/1", "PG32UCDP", [wide])] }).PanelsFor(portrait).ShouldBe([wide]);
        config.WithPanel("AUS32F4/1", "panel-2", p => p with { X = 0.5 }).AllScreens[0].Portrait![0].X.ShouldBe(0.5);

        // One id in both lists, or more than 12 in either, is refused.
        OverlayStyles.Validate(config with { Screens = [new OverlayScreen("AUS32F4/1", "PG32UCDP", [wide], [wide])] }).ShouldNotBeNull();
        var many = Enumerable.Range(1, 13).Select(i => Panel($"p-{i}")).ToList();
        OverlayStyles.Validate(config with { Screens = [new OverlayScreen("AUS32F4/1", "PG32UCDP", [wide], many)] }).ShouldNotBeNull();
    }

    [Fact]
    public void A_place_to_restore_and_handles_on_the_screen_are_checked()
    {
        var panel = Panel() with { Restore = new OverlayPanelPlace(0.25, 1, 320, null, 14), Movable = true, Closable = true };
        OverlayStyles.Validate(Config(panel)).ShouldBeNull();
        panel.Handles.ShouldBeTrue();
        Panel().Handles.ShouldBeFalse();
        OverlayStyles.Validate(Config(panel with { Restore = new OverlayPanelPlace(1.5, 0, null, null, 14) })).ShouldNotBeNull();
        OverlayStyles.Validate(Config(panel with { Restore = new OverlayPanelPlace(0, 0, 10, null, 14) })).ShouldNotBeNull();
        OverlayStyles.Validate(Config(panel with { Restore = new OverlayPanelPlace(0, 0, null, null, 100) })).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("B", 2.0 * 1024 * 1024 * 1024, 2.5 * 1024 * 1024 * 1024)]
    [InlineData("B", 1700, 2.5 * 1024)]
    [InlineData("W", 80, 100)]
    [InlineData("MHz", 2100, 5000)]
    public void A_reading_without_a_full_scale_gets_an_automatic_one(string unit, double value, double expected) =>
        OverlayStyles.AutoMax(new OverlaySource("x", "X", "G", unit, value))!.Value.ShouldBe(expected, 1e-6);

    [Fact]
    public void A_text_or_empty_reading_has_no_automatic_scale()
    {
        OverlayStyles.AutoMax(new OverlaySource("x", "X", "G", "", null, "On")).ShouldBeNull();
        OverlayStyles.AutoMax(new OverlaySource("x", "X", "G", "W", 0)).ShouldBeNull();
    }
}
