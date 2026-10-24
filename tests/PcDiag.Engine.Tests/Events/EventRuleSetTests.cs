using System.Xml.Linq;
using PcDiag.Core.Events;
using PcDiag.Engine.Events;

namespace PcDiag.Engine.Tests.Events;

public class EventRuleSetTests
{
    private static readonly string Defaults = EventRuleSet.LoadDefaultJson();

    private static EventRecordData Record(string provider, int id, int level = 2, string channel = "System", params EventDataItem[] data) =>
        new(channel, 1, provider, id, level, DateTimeOffset.UnixEpoch, 100, data, null);

    [Fact]
    public void User_rules_replace_shipped_rules_by_id_and_new_rules_take_precedence()
    {
        var user = """
            {
              "version": 1,
              "rules": [
                // Lower the severity of a shipped rule.
                { "id": "gpu.nvidia.error", "channel": "System", "providers": ["nvlddmkm"], "levels": [1, 2], "category": "Gpu", "severity": "Low", "title": "Mine" },
                // Turn one off.
                { "id": "power.sleep", "enabled": false },
                // Add one that overlaps the System catch-all.
                { "id": "my.acpi", "channel": "System", "providers": ["ACPI"], "eventIds": [13], "category": "Device", "severity": "Medium", "title": "ACPI {eventId}" },
              ]
            }
            """;
        var rules = EventRuleSet.Load(Defaults, user);
        var classifier = new EventClassifier(rules, EventRedactor.None, 2000);

        rules.Problems.ShouldBeEmpty();
        classifier.Classify(Record("nvlddmkm", 153), null)!.Event.Severity.ShouldBe(Severity.Low);
        classifier.Classify(Record("Microsoft-Windows-Kernel-Power", 42, level: 4), null).ShouldBeNull();
        classifier.Classify(Record("ACPI", 13), null)!.Rule.Id.ShouldBe("my.acpi");
        rules.Rules[0].Id.ShouldBe("my.acpi");
    }

    [Fact]
    public void Invalid_user_rules_are_skipped_and_reported_without_affecting_the_others()
    {
        var user = """
            { "version": 1, "rules": [
              { "id": "everything", "channel": "System", "category": "Other", "severity": "Low", "title": "x" },
              { "id": "inject", "channel": "System", "providers": ["a' or '1'='1"], "category": "Other", "severity": "Low", "title": "x" },
              { "id": "bad-field", "channel": "System", "providers": ["x"], "category": "Other", "severity": "Low", "title": "x", "fields": { "f": "$shell" } },
              { "id": "bad-condition", "channel": "System", "providers": ["x"], "category": "Other", "severity": "Low", "title": "x", "when": [ { "field": "A" } ] },
              { "id": "ok", "channel": "System", "providers": ["x"], "category": "Other", "severity": "Low", "title": "x" }
            ] }
            """;

        var rules = EventRuleSet.Load(Defaults, user);

        rules.Problems.Count.ShouldBe(4);
        rules.Problems[0].ShouldContain("never collected wholesale");
        rules.Problems[1].ShouldContain("provider names");
        rules.Rules.ShouldContain(r => r.Id == "ok");
        rules.Rules.ShouldContain(r => r.Id == "app.crash");
    }

    [Fact]
    public void A_broken_user_file_falls_back_to_the_shipped_rules()
    {
        var rules = EventRuleSet.Load(Defaults, "{ not json");

        rules.Problems.Single().ShouldStartWith("event-rules.json could not be read");
        rules.Rules.Count.ShouldBe(EventRuleSet.Load(Defaults, null).Rules.Count);
    }

    [Fact]
    public void Invalid_shipped_rules_fail_loudly()
    {
        Should.Throw<InvalidOperationException>(() => EventRuleSet.Load("""{ "version": 1, "rules": [ { "id": "x" } ] }""", null));
        Should.Throw<InvalidOperationException>(() => EventRuleSet.Load("""{ "version": 2, "rules": [] }""", null));
    }

    [Fact]
    public void Queries_are_valid_xml_with_small_selectors_and_suppressors_for_noise()
    {
        var rules = EventRuleSet.Load(Defaults, null);
        foreach (var channel in rules.Channels)
        {
            var query = EventQueryBuilder.Build(rules, channel).ShouldNotBeNull();
            var document = XDocument.Parse(query);
            var selects = document.Descendants("Select").ToList();

            selects.ShouldNotBeEmpty();
            document.Descendants().Where(e => e.Name.LocalName is "Select" or "Suppress").ShouldAllBe(e => e.Attribute("Path")!.Value == channel);
            foreach (var select in selects)
            {
                // Windows requires a structured query beyond 20 expressions; each selector stays far below that.
                var expressions = select.Value.Split([" or ", " and "], StringSplitOptions.None).Length;
                expressions.ShouldBeLessThanOrEqualTo(EventQueryBuilder.MaxIdsPerSelect + 4);
                select.Value.ShouldStartWith("*[System[");
            }
        }

        var system = XDocument.Parse(EventQueryBuilder.Build(rules, "System")!);
        system.Descendants("Suppress").Single().Value.ShouldBe("*[System[Provider[@Name='Microsoft-Windows-DistributedCOM'] and (EventID=10010 or EventID=10016)]]");
        system.Descendants("Select").ShouldContain(s => s.Value == "*[System[Provider[@Name='Display'] and EventID=4101]]");
        system.Descendants("Select").ShouldContain(s => s.Value == "*[System[(Level=1 or Level=2)]]");
    }

    [Fact]
    public void Backfill_queries_add_a_time_window_and_can_be_limited_to_boot_rules()
    {
        var rules = EventRuleSet.Load(Defaults, null);

        var recent = XDocument.Parse(EventQueryBuilder.Build(rules, "System", TimeSpan.FromMinutes(30))!);
        recent.Descendants("Select").ShouldAllBe(s => s.Value.Contains("TimeCreated[timediff(@SystemTime) <= 1800000]"));
        recent.Descendants("Suppress").ShouldAllBe(s => !s.Value.Contains("timediff"));

        var boot = XDocument.Parse(EventQueryBuilder.Build(rules, "System", TimeSpan.FromHours(5), sinceBootRulesOnly: true)!);
        var bootSelects = boot.Descendants("Select").Select(s => s.Value).ToList();
        bootSelects.ShouldContain(s => s.Contains("Microsoft-Windows-Kernel-Power") && s.Contains("EventID=41"));
        bootSelects.ShouldContain(s => s.Contains("'EventLog'") && s.Contains("EventID=6008"));
        bootSelects.ShouldNotContain(s => s.Contains("'Display'"));

        EventQueryBuilder.Build(rules, "Application", TimeSpan.FromHours(5), sinceBootRulesOnly: true).ShouldBeNull();
        EventQueryBuilder.Build(rules, "Security").ShouldBeNull();
    }

    [Fact]
    public void Rules_with_many_ids_are_split_across_selectors()
    {
        var rule = new EventRule { Id = "many", Channel = "System", Providers = ["p"], EventIds = Enumerable.Range(1, 30).ToList(), Title = "t" };

        var expressions = EventQueryBuilder.Expressions(rule, null).ToList();

        expressions.Count.ShouldBe(3);
        expressions.ShouldAllBe(e => e.Split(" or ", StringSplitOptions.None).Length <= EventQueryBuilder.MaxIdsPerSelect);
    }

    [Fact]
    public void Ignore_rules_win_regardless_of_order()
    {
        var user = """
            { "version": 1, "rules": [
              { "id": "want", "channel": "System", "providers": ["Noisy"], "category": "Other", "severity": "High", "title": "x" },
              { "id": "drop", "channel": "System", "providers": ["Noisy"], "eventIds": [5], "ignore": true }
            ] }
            """;
        var classifier = new EventClassifier(EventRuleSet.Load(Defaults, user), EventRedactor.None, 2000);

        classifier.Classify(Record("Noisy", 5), null).ShouldBeNull();
        classifier.Classify(Record("Noisy", 6), null)!.Rule.Id.ShouldBe("want");
    }

    [Theory]
    [InlineData("ProcessId", "0x9b4", "0x9b4")]
    [InlineData("Code:hex", "268435582", "0x1000007E")]
    [InlineData("Code:prefix0x", "c0000005", "0xC0000005")]
    [InlineData("Path:file", @"C:\Program Files\App\app.exe", "app.exe")]
    [InlineData("Path:file", @"C:\WINDOWS\x\StartMenu.exe (PC-1)", "StartMenu.exe")]
    [InlineData("Text:firstLineValue", "Application: tool.exe\nFramework Version: v4", "tool.exe")]
    public void Field_sources_and_transforms(string spec, string raw, string expected)
    {
        var name = spec.Split(':')[0];
        var record = Record("p", 1, data: new EventDataItem(name, raw));

        EventFieldSpec.TryParse(spec, out var error).ShouldNotBeNull(error).Evaluate(record).ShouldBe(expected);
    }

    [Fact]
    public void Alternatives_fall_back_to_positional_and_special_sources()
    {
        var record = new EventRecordData("System", 7, "Service Control Manager", 7034, 2, DateTimeOffset.UnixEpoch, 42,
            [new EventDataItem(null, "first"), new EventDataItem(null, "")], "49004100530074006F0072000000");

        EventFieldSpec.TryParse("param1|#0", out _)!.Evaluate(record).ShouldBe("first");
        EventFieldSpec.TryParse("param2|#1|$pid", out _)!.Evaluate(record).ShouldBe("42");
        EventFieldSpec.TryParse("$binaryText", out _)!.Evaluate(record).ShouldBe("IAStor");
        EventFieldSpec.TryParse("missing", out _)!.Evaluate(record).ShouldBeNull();
        EventFieldSpec.TryParse("$env:PATH", out var error).ShouldBeNull();
        error.ShouldNotBeNull();
    }

    [Theory]
    [InlineData("0x9b4", 2484)]
    [InlineData("2484", 2484)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    public void Process_ids_are_parsed_from_decimal_or_hex(string text, int? expected) =>
        EventValues.ParseProcessId(text).ShouldBe(expected);

    [Fact]
    public void Process_start_times_are_parsed_from_filetime_or_iso()
    {
        var fromHex = EventValues.ParseTimestampTicks("0x1dd524fd8764b1c");
        fromHex.ShouldBe(DateTime.FromFileTimeUtc(0x1dd524fd8764b1cL).Ticks);
        EventValues.ParseTimestampTicks("134354066778508060").ShouldBe(DateTime.FromFileTimeUtc(134354066778508060L).Ticks);
        EventValues.ParseTimestampTicks("2026-10-02T09:22:08.5000000Z").ShouldBe(new DateTime(2026, 10, 2, 9, 22, 8, 500, DateTimeKind.Utc).Ticks);
        EventValues.ParseTimestampTicks("4294967295").ShouldBeNull(); // not a plausible FILETIME
    }

    [Fact]
    public void Titles_render_fields_and_builtins_and_mark_missing_values()
    {
        var record = Record("Prov", 9);
        EventClassifier.RenderTitle("{provider} {eventId} {channel} {name} {missing}", record, new Dictionary<string, string> { ["name"] = "n" })
            .ShouldBe("Prov 9 System n unknown");
    }
}
