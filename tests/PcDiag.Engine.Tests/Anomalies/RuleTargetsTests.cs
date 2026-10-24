using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Engine.Anomalies;
using PcDiag.Engine.Events;

namespace PcDiag.Engine.Tests.Anomalies;

/// <summary>What the user's own rules can watch: any reading, chosen programs and displays, and Windows events they describe.</summary>
public sealed class RuleTargetsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "targets-" + Guid.NewGuid().ToString("N"));

    public RuleTargetsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("game.exe", "game.exe", true)]
    [InlineData("game", "Game.exe", true)]
    [InlineData("chrome*", "chrome.exe", true)]
    [InlineData("chrome*", "msedge.exe", false)]
    [InlineData("PG32UCDP", "PG32UCDP", true)]
    public void Programs_and_displays_match_by_name_with_wildcards(string pattern, string name, bool matches)
    {
        EventDetector.NameMatches([pattern], name).ShouldBe(matches);
        EventDetector.NameMatches([], name).ShouldBeTrue("no names: any");
    }

    [Theory]
    [InlineData("gpu.vram", "graphics")]
    [InlineData("sensor.lhm/gpu-nvidia/0/clock/0", "graphics")]
    [InlineData("sensor.lhm/intelcpu/0/clock/0", "processor")]
    [InlineData("sensor.lhm/memory/dimm/1/temperature/0", "memory")]
    [InlineData("sensor.lhm/nvme/0/load/30", "storage")]
    [InlineData("sensor.disk/0/available_spare", "storage")]
    [InlineData("disk.disk-bfac5402cfa6.active", "storage")]
    [InlineData("net.ping.internet", "network")]
    [InlineData("sensor.lhm/lpc/nct6701d/0/fan/1", "sensors")]
    [InlineData("sensor.ext/test-probe/desk/humidity", "sensors")]
    public void A_measurement_goes_with_its_hardware(string metric, string category) =>
        AnomalyCategories.ForMetric(metric).ShouldBe(category);

    [Fact]
    public void Windows_events_a_rule_describes_are_collected_and_classified()
    {
        var triggers = new TriggerOptions
        {
            Custom =
            [
                new CustomTriggerRule
                {
                    Id = "my-power", Name = "Power lost", Kind = AnomalyRuleKind.Event, Severity = Severity.High,
                    WindowsEvents = [new CustomEventMatch { Channel = "System", Provider = "Microsoft-Windows-Kernel-Power", EventIds = [41] }],
                },
            ],
        };
        var provider = new EventRuleProvider(new DataPaths(_directory), NullLogger<EventRuleProvider>.Instance, new StaticOptionsMonitor<TriggerOptions>(triggers));

        var rules = provider.Load();

        rules.Problems.ShouldBeEmpty();
        var made = rules.Rules.Single(r => r.Rule.Id == "my-power.w0").Rule;
        (made.Channel, made.Providers.Single(), made.EventIds.Single(), made.Severity).ShouldBe(("System", "Microsoft-Windows-Kernel-Power", 41, Severity.High));
        AnomalyRuleCatalog.Default.Apply(triggers).Single(r => r.Id == "my-power").EventRules.ShouldBe(["my-power.w0"]);
    }

    [Fact]
    public void Rules_are_listed_by_category_and_can_watch_any_reading()
    {
        var options = new TriggerOptions
        {
            RuleCategories = { ["cpu-high"] = "gaming" },
            Custom =
            [
                new CustomTriggerRule
                {
                    Id = "my-fan", Name = "Fan stopped", Kind = AnomalyRuleKind.ThresholdDuration, Metric = "reading:sensor/lpc/fan/1",
                    ReadingLabel = "Nuvoton › CPU fan", ReadingUnit = "RPM", Direction = ThresholdDirection.Below, Threshold = 200, DurationSeconds = 10,
                },
            ],
        };

        var rules = AnomalyRuleCatalog.Default.Apply(options);

        rules.Single(r => r.Id == "cpu-high").Category.ShouldBe("gaming");
        rules.Single(r => r.Id == "gpu-vram-full").Category.ShouldBe("graphics");
        rules.Single(r => r.Id == "app-crash").Category.ShouldBe("events");
        var fan = rules.Single(r => r.Id == "my-fan");
        (fan.Category, fan.Describe()).ShouldBe(("sensors", "Nuvoton › CPU fan below 200 RPM for at least 10 s"));
        AnomalyRuleCatalog.Default.Validate(options, strict: true).ShouldBeEmpty();

        // A category needs a name; a Windows event needs its log and something to narrow it.
        var bad = new TriggerOptions
        {
            Categories = [new TriggerCategory { Id = "x", Name = " " }],
            Custom = [new CustomTriggerRule { Id = "my-any", Name = "Anything", Kind = AnomalyRuleKind.Event, WindowsEvents = [new CustomEventMatch { Channel = "System" }] }],
        };
        var problems = AnomalyRuleCatalog.Default.Validate(bad, strict: true);
        problems.ShouldContain(p => p.Contains("each category needs a name", StringComparison.Ordinal));
        problems.ShouldContain(p => p.Contains("needs a source, event IDs", StringComparison.Ordinal));
    }
}
