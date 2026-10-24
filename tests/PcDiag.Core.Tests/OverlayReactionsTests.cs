using System.Text.Json;
using PcDiag.Core.Overlay;

namespace PcDiag.Core.Tests;

public sealed class OverlayReactionsTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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

    [Fact]
    public void Readings_are_judged_and_reacted_to_as_the_editor_judges_them()
    {
        // The editor's cases (reaction-parity.json, written by its tests): kind, status and reaction alike.
        using var document = JsonDocument.Parse(File.ReadAllText(EditorFile("reaction-parity.json")));
        var options = WebJson;
        var count = 0;
        foreach (var c in document.RootElement.EnumerateArray())
        {
            var source = c.GetProperty("source").GetString()!;
            var reading = c.TryGetProperty("reading", out var r) ? (r.Deserialize<OverlayReading>(options)! with { Source = source }) : null;
            double? max = c.TryGetProperty("max", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : null;
            var kind = OverlayReactions.KindOf(source, c.GetProperty("unit").GetString()!);
            var what = $"{source} at {c.GetProperty("value").GetDouble()}";
            kind.ShouldBe(c.GetProperty("kind").GetString(), what);
            var status = OverlayReactions.StatusOf(kind, reading, c.GetProperty("value").GetDouble(), max);
            status.ShouldBe(c.GetProperty("status").GetString(), what);
            var (action, effect) = OverlayReactions.ReactionFor(kind, status, null);
            action.ShouldBe(c.GetProperty("reaction").GetProperty("action").GetString(), what);
            effect.ShouldBe(c.GetProperty("reaction").GetProperty("effect").GetString(), what);
            OverlayReactions.PropFor(kind, status, null).ShouldBe(c.GetProperty("prop").ValueKind == JsonValueKind.Null ? null : c.GetProperty("prop").GetString(), what);
            count++;
        }

        count.ShouldBeGreaterThan(25);
    }

    [Fact]
    public void Props_are_in_their_file_shown_by_status_changed_by_a_character_and_checked()
    {
        // Every prop has its frame (and nothing else is there); every kind shows props there are.
        OverlayReactions.PropsPack.Pack.Actions.ShouldBe(OverlayReactions.Props.Keys, ignoreOrder: true);
        foreach (var kind in OverlayReactions.Kinds.Values)
        {
            foreach (var prop in kind.Props?.Values ?? [])
            {
                OverlayReactions.Props.ShouldContainKey(prop);
            }
        }

        OverlayReactions.Props["ice-pack"].ShouldBe(new PropInfo("ice-pack", "Ice pack", "head", 0.26, 0.12, "none", "drip", false, false, 0.253));
        OverlayReactions.Props["desk-fan"].Mirror.ShouldBeTrue();
        OverlayReactions.Props["umbrella"].Behind.ShouldBeTrue();

        // Hot: an ice pack on its head; its own choice wins, "none" shows none.
        OverlayReactions.PropFor("temperature", "high", null).ShouldBe("ice-pack");
        OverlayReactions.PropFor("temperature", "normal", null).ShouldBeNull();
        var own = new Dictionary<string, OverlayReaction> { ["high"] = new(Prop: "umbrella"), ["critical"] = new(Prop: "none") };
        OverlayReactions.PropFor("temperature", "high", own).ShouldBe("umbrella");
        OverlayReactions.PropFor("temperature", "critical", own).ShouldBeNull();

        // A reading's own over its general ones, the prop too.
        var c = new OverlayCharacter("c1", "mochi", React: "auto", Reactions: new Dictionary<string, OverlayReaction> { ["high"] = new("sit", Prop: "cone") },
            ReactionsBy: new Dictionary<string, IReadOnlyDictionary<string, OverlayReaction>> { ["cpu.temp"] = new Dictionary<string, OverlayReaction> { ["high"] = new(Effect: "steam") } });
        c.ReactionsFor("cpu.temp")!["high"].ShouldBe(new OverlayReaction("sit", "steam", "cone"));

        OverlayReactions.Check(c with { Reactions = new Dictionary<string, OverlayReaction> { ["high"] = new(Prop: "piano") } }).ShouldBe("There is no prop piano.");
        OverlayReactions.Check(c with { Reactions = new Dictionary<string, OverlayReaction> { ["high"] = new(Prop: "none") } }).ShouldBeNull();
    }

    [Fact]
    public void A_characters_own_reactions_and_its_reading_are_checked_and_kept_with_the_layout()
    {
        var own = new Dictionary<string, OverlayReaction> { ["high"] = new("dance", "none"), ["low"] = new(null, "hearts") };
        OverlayReactions.ReactionFor("load", "high", own).ShouldBe(("dance", (string?)null));
        OverlayReactions.ReactionFor("load", "low", own).ShouldBe(("sleep", "hearts"));

        OverlayCharacter Kitten(params (string Status, OverlayReaction Reaction)[] reactions) =>
            new("c1", "mochi", React: "auto", Reactions: reactions.ToDictionary(r => r.Status, r => r.Reaction));
        OverlayPanel With(OverlayCharacter c) => new("p1", null, 0, 0, "rows", [new OverlayReading("cpu.usage")], new OverlayStyle(), Characters: [c]);
        OverlayCharacters.Check(With(Kitten(("high", new("run", "sweat"))))).ShouldBeNull();
        OverlayCharacters.Check(With(Kitten(("hot", new("run", null))))).ShouldNotBeNull("no such status");
        OverlayCharacters.Check(With(Kitten(("high", new("Fly!", null))))).ShouldNotBeNull("no such action");
        OverlayCharacters.Check(With(Kitten(("high", new(null, "fireworks"))))).ShouldNotBeNull("no such effect");
        OverlayCharacters.Check(With(Kitten() with { React = string.Empty })).ShouldNotBeNull();

        // Kept with the layout as the editor writes it, and equal by content.
        var json = """{"id":"c1","character":"mochi","react":"cpu.usage","reactions":{"high":{"action":"dance"},"critical":{"effect":"none"}}}""";
        var read = JsonSerializer.Deserialize<OverlayCharacter>(json, WebJson)!;
        read.React.ShouldBe("cpu.usage");
        read.Reactions!["high"].ShouldBe(new OverlayReaction("dance"));
        read.Reactions["critical"].Effect.ShouldBe("none");
        read.ShouldBe(JsonSerializer.Deserialize<OverlayCharacter>(json, WebJson));
        read.ShouldNotBe(read with { Reactions = null });
    }

    [Fact]
    public void It_reacts_to_the_most_urgent_reading_its_order_breaking_ties()
    {
        var panel = new OverlayPanel("p", null, 0, 0, "rows", [new OverlayReading("cpu.usage"), new OverlayReading("cpu.temp")], new OverlayStyle());
        var sources = new Dictionary<string, OverlaySource>(StringComparer.Ordinal)
        {
            ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 80),
            ["cpu.temp"] = new("cpu.temp", "CPU temp", "Processor", "°C", 90),
        };
        var c = new OverlayCharacter("c1", "mochi", React: "auto");
        // High and critical: the critical one, whatever the order.
        OverlayReactions.ReadingFor(c, panel, sources)!.Source.Id.ShouldBe("cpu.temp");
        OverlayReactions.ReadingFor(c with { Priority = ["cpu.usage"] }, panel, sources)!.Source.Id.ShouldBe("cpu.temp");
        // An idle GPU put first does not drown out a CPU at full load (16 Oct 2026).
        var busy = new OverlayPanel("b", null, 0, 0, "rows", [new OverlayReading("gpu.usage"), new OverlayReading("cpu.usage")], new OverlayStyle());
        var loads = new Dictionary<string, OverlaySource>(StringComparer.Ordinal)
        {
            ["gpu.usage"] = new("gpu.usage", "GPU", "Graphics", "%", 5),
            ["cpu.usage"] = new("cpu.usage", "CPU", "Processor", "%", 100),
        };
        OverlayReactions.ReadingFor(c with { Priority = ["gpu.usage"] }, busy, loads)!.Status.ShouldBe("critical");
        // No critical or high one: the first reading, whatever it shows (a low one later does not come first).
        loads["cpu.usage"] = loads["cpu.usage"] with { Value = 50 };
        OverlayReactions.ReadingFor(c with { Priority = ["cpu.usage"] }, busy, loads)!.Source.Id.ShouldBe("cpu.usage");
        OverlayReactions.ReadingFor(c with { Priority = ["gpu.usage"] }, busy, loads)!.Status.ShouldBe("low");
        // Both high: the first in its order.
        sources["cpu.temp"] = sources["cpu.temp"] with { Value = 80 };
        OverlayReactions.ReadingFor(c, panel, sources)!.Source.Id.ShouldBe("cpu.usage");
        OverlayReactions.ReadingFor(c with { Priority = ["cpu.temp", "cpu.usage"] }, panel, sources)!.Source.Id.ShouldBe("cpu.temp");
        OverlayReactions.Ordered(panel.Readings, ["cpu.temp"]).Select(r => r.Source).ShouldBe(["cpu.temp", "cpu.usage"]);
        OverlayReactions.ReadingFor(c with { Ignore = ["cpu.usage"] }, panel, sources)!.Source.Id.ShouldBe("cpu.temp");
        OverlayReactions.ReadingFor(c with { Ignore = ["cpu.usage", "cpu.temp"] }, panel, sources).ShouldBeNull();
        (c with { Priority = ["a"] }).ShouldNotBe(c with { Priority = ["b"] });
    }

    [Fact]
    public void A_readings_own_reactions_come_over_the_characters_general_ones()
    {
        var c = new OverlayCharacter("c1", "mochi", Reactions: new Dictionary<string, OverlayReaction> { ["critical"] = new("sit") },
            ReactionsBy: new Dictionary<string, IReadOnlyDictionary<string, OverlayReaction>> { ["cpu.temp"] = new Dictionary<string, OverlayReaction> { ["critical"] = new(Effect: "steam") } });
        c.ReactionsFor("cpu.temp")!["critical"].ShouldBe(new OverlayReaction("sit", "steam"));
        c.ReactionsFor("cpu.usage")!["critical"].ShouldBe(new OverlayReaction("sit"));
        OverlayReactions.ReactionFor("temperature", "critical", c.ReactionsFor("cpu.temp")).ShouldBe(("sit", "steam"));
        OverlayReactions.Check(c).ShouldBeNull();
        OverlayReactions.Check(c with { ReactionsBy = new Dictionary<string, IReadOnlyDictionary<string, OverlayReaction>> { ["cpu.temp"] = new Dictionary<string, OverlayReaction> { ["boiling"] = new("sit") } } })!.ShouldContain("statuses");
        (c with { }).ShouldBe(c);
        (c with { ReactionsBy = null }).ShouldNotBe(c);
    }

    [Fact]
    public void Auto_reacts_to_the_panels_most_urgent_reading()
    {
        var panel = new OverlayPanel("p1", null, 0, 0, "rows", [new OverlayReading("cpu.usage"), new OverlayReading("cpu.temp"), new OverlayReading("net.n1.down")], new OverlayStyle());
        var sources = new Dictionary<string, OverlaySource>
        {
            ["cpu.usage"] = new("cpu.usage", "CPU", "g", "%", 40),
            ["cpu.temp"] = new("cpu.temp", "CPU temp", "g", "°C", 90),
            ["net.n1.down"] = new("net.n1.down", "Down", "g", "B/s", 1000),
        };
        OverlayReactions.ReadingFor(new OverlayCharacter("c", "mochi", React: "auto"), panel, sources)!.ShouldSatisfyAllConditions(
            j => j.Kind.ShouldBe("temperature"),
            j => j.Status.ShouldBe("critical"));
        OverlayReactions.ReadingFor(new OverlayCharacter("c", "mochi", React: "net.n1.down"), panel, sources)!.Status.ShouldBe("low");
        OverlayReactions.ReadingFor(new OverlayCharacter("c", "mochi"), panel, sources).ShouldBeNull();
    }

    [Fact]
    public void The_effects_are_embedded_whole_and_the_shipped_characters_have_heads()
    {
        var (_, pack) = OverlayReactions.EffectsPack;
        pack.Actions.ShouldBe(OverlayReactions.Effects.Keys, ignoreOrder: true);
        pack.Clips.Single(c => c.Action == "zzz").At.ShouldBe("head");
        pack.Clips.Single(c => c.Action == "zzz").Mirror.ShouldBeFalse();
        pack.Clips.Single(c => c.Action == "data").ShouldSatisfyAllConditions(c => c.At.ShouldBe("feet"), c => c.Mirror.ShouldBeTrue());
        pack.Tall.ShouldBe(160);
        foreach (var (id, (_, shipped)) in OverlayCharacters.Shipped)
        {
            // The top of the head: over the drawing and well above the feet (ears may reach above its height).
            shipped.Head.X.ShouldBeInRange(0, shipped.Width, id);
            (shipped.AnchorY - shipped.Head.Y).ShouldBeInRange(shipped.Tall * 0.6, shipped.Tall * 1.2, id);
        }
    }
}
