using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;

namespace PcDiag.Core.Tests;

public sealed class AnomalyRuleCatalogTests
{
    [Fact]
    public void The_shipped_rules_load_with_their_defaults()
    {
        var rules = AnomalyRuleCatalog.Default.Rules;

        rules.Count.ShouldBeGreaterThan(15);
        rules.ShouldAllBe(r => r.Enabled);
        rules.ShouldAllBe(r => r.CooldownMinutes == 30);
        rules.Select(r => r.Id).ShouldBeUnique();

        var baseline = AnomalyRuleCatalog.Default.Find("gpu-baseline")!;
        baseline.Kind.ShouldBe(AnomalyRuleKind.BaselineDeviation);
        baseline.BaselineMinutes.ShouldBe(30);
        baseline.WarmupMinutes.ShouldBe(5);
        baseline.MinStdDev.ShouldBe(5);

        var memory = AnomalyRuleCatalog.Default.Find("memory-low")!;
        memory.Direction.ShouldBe(ThresholdDirection.Below);
        memory.Severity.ShouldBe(Severity.High);
        memory.CreateIncident.ShouldBeTrue();

        var crash = AnomalyRuleCatalog.Default.Find("app-crash")!;
        crash.EventRules.ShouldContain("app.crash");
        crash.ProcessEvents.ShouldBe([ProcessEventKind.Crashed]);
        crash.DisplayChanges.ShouldBeEmpty();
    }

    [Fact]
    public void Heavy_use_is_recorded_but_does_not_create_incidents_by_default()
    {
        var catalog = AnomalyRuleCatalog.Default;

        catalog.Find("cpu-high")!.CreateIncident.ShouldBeFalse();
        catalog.Find("gpu-high")!.CreateIncident.ShouldBeFalse();
        catalog.Find("gpu-reset")!.CreateIncident.ShouldBeTrue();
        catalog.Find("hardware-error")!.Severity.ShouldBe(Severity.Critical);
    }

    [Theory]
    [InlineData("cpu-high", "CPU utilization above 95 % for at least 30 s")]
    [InlineData("memory-low", "Available memory below 1.0 GB for at least 5 s")]
    [InlineData("gpu-clock-drop", "GPU core clock drops by 60 % or more within 2 s while GPU utilization is at least 30 %")]
    [InlineData("gpu-baseline", "GPU utilization more than 3 σ above its 30 min baseline for at least 15 s")]
    [InlineData("network-errors", "Network packet errors: 50 or more within 10 s")]
    public void Rules_describe_themselves_in_plain_words(string id, string expected) =>
        AnomalyRuleCatalog.Default.Find(id)!.Describe().ShouldBe(expected);

    [Fact]
    public void User_changes_apply_to_their_rule_only()
    {
        var options = new TriggerOptions();
        options.Rules["CPU-HIGH"] = new TriggerRuleSettings { Threshold = 90, DurationSeconds = 60, CreateIncident = true, WindowSeconds = 5 };
        options.Rules["gpu-reset"] = new TriggerRuleSettings { Enabled = false, Threshold = 5 };
        options.Rules["retired-rule"] = new TriggerRuleSettings { Enabled = false };

        var rules = AnomalyRuleCatalog.Default.Apply(options);

        var cpu = rules.Single(r => r.Id == "cpu-high");
        cpu.Threshold.ShouldBe(90);
        cpu.DurationSeconds.ShouldBe(60);
        cpu.CreateIncident.ShouldBeTrue();
        cpu.WindowSeconds.ShouldBe(0, "a window does not apply to a duration rule");

        var reset = rules.Single(r => r.Id == "gpu-reset");
        reset.Enabled.ShouldBeFalse();
        reset.Threshold.ShouldBe(0, "event rules have no threshold");

        rules.Single(r => r.Id == "gpu-high").ShouldBe(AnomalyRuleCatalog.Default.Find("gpu-high"));
    }

    [Fact]
    public void Trigger_settings_are_validated_against_each_rules_range()
    {
        var options = new TriggerOptions { MaxIncidentsPerHour = 0 };
        options.Rules["cpu-high"] = new TriggerRuleSettings { Threshold = 150, DurationSeconds = 0 };
        options.Rules["gpu-baseline"] = new TriggerRuleSettings { Threshold = 3.5 };
        options.Rules["network-errors"] = new TriggerRuleSettings { WindowSeconds = 900, CooldownMinutes = -1 };
        options.Rules["retired-rule"] = new TriggerRuleSettings();

        var lenient = AnomalyRuleCatalog.Default.Validate(options, strict: false);
        lenient.ShouldContain(p => p.Contains("MaxIncidentsPerHour", StringComparison.Ordinal));
        lenient.ShouldContain(p => p.StartsWith("Triggers:Rules:cpu-high:Threshold must be between 1 % and 100 %", StringComparison.Ordinal));
        lenient.ShouldContain(p => p.StartsWith("Triggers:Rules:cpu-high:DurationSeconds", StringComparison.Ordinal));
        lenient.ShouldContain(p => p.StartsWith("Triggers:Rules:network-errors:WindowSeconds", StringComparison.Ordinal));
        lenient.ShouldContain(p => p.StartsWith("Triggers:Rules:network-errors:CooldownMinutes", StringComparison.Ordinal));
        lenient.ShouldNotContain(p => p.Contains("gpu-baseline", StringComparison.Ordinal));
        lenient.ShouldNotContain(p => p.Contains("retired-rule", StringComparison.Ordinal), "settings from an older version must not stop the Agent");

        AnomalyRuleCatalog.Default.Validate(options, strict: true).ShouldContain("Triggers:Rules:retired-rule is not a known rule.");
        new TriggerOptionsValidator().Validate(null, new TriggerOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_broken_rules_document_is_refused_with_every_problem()
    {
        const string json = """
            { "rules": [
              { "id": "Bad Id", "name": "x", "classification": "y", "kind": "Threshold", "metric": "cpu", "threshold": 500 },
              { "id": "e", "name": "x", "classification": "y", "kind": "Event" },
              { "id": "m", "name": "x", "classification": "y", "kind": "ThresholdDuration", "metric": "nope", "threshold": 1 },
              { "id": "m", "name": "x", "classification": "y", "kind": "Threshold", "metric": "cpu", "threshold": 50 }
            ] }
            """;

        var problem = Should.Throw<InvalidDataException>(() => AnomalyRuleCatalog.Load(json)).Message;

        problem.ShouldContain("'Bad Id'");
        problem.ShouldContain("outside");
        problem.ShouldContain("e: an event rule needs");
        problem.ShouldContain("unknown metric 'nope'");
        problem.ShouldContain("'m' is used twice");
        Should.Throw<InvalidDataException>(() => AnomalyRuleCatalog.Load("{ not json"));
    }

    [Fact]
    public void Reasoning_reads_like_the_specification()
    {
        var reasoning = new AnomalyReasoning("GPU utilization anomaly", "GPU utilization more than 3 σ above its 30 min baseline for at least 15 s",
            "40–70 % (mean 55, sd 8, 30 min)", 55, 8, "99 % (z = 5.5)", TimeSpan.FromSeconds(18));

        reasoning.Summary().ShouldBe(
            "Normally 40–70 % (mean 55, sd 8, 30 min); current 99 % (z = 5.5); for 18 s. Rule: GPU utilization more than 3 σ above its 30 min baseline for at least 15 s. Classification: GPU utilization anomaly.");
        AnomalyReasoning.FormatDuration(TimeSpan.FromSeconds(125)).ShouldBe("2 min 5 s");
        AnomalyReasoning.FormatDuration(TimeSpan.FromMinutes(63)).ShouldBe("1 h 3 min");
    }

    [Fact]
    public void Users_add_their_own_rules_and_delete_shipped_ones()
    {
        var options = new TriggerOptions
        {
            Removed = ["gpu-vram-full"],
            Custom =
            [
                new CustomTriggerRule { Id = "my-ram", Name = "RAM almost gone", Kind = AnomalyRuleKind.ThresholdDuration, Metric = AnomalyMetrics.MemoryAvailable, Direction = ThresholdDirection.Below, Threshold = 2, DurationSeconds = 120 },
                new CustomTriggerRule { Id = "my-crash", Name = "Any crash", Kind = AnomalyRuleKind.Event, ProcessEvents = [ProcessEventKind.Crashed], Severity = Severity.High },
                new CustomTriggerRule { Id = "my-bad", Name = "Too much", Kind = AnomalyRuleKind.Threshold, Metric = AnomalyMetrics.Cpu, Threshold = 500 },
            ],
        };

        var rules = AnomalyRuleCatalog.Default.Apply(options);

        rules.ShouldNotContain(r => r.Id == "gpu-vram-full");
        var ram = rules.Single(r => r.Id == "my-ram");
        (ram.Name, ram.Direction, ram.Threshold, ram.DurationSeconds, ram.CreateIncident).ShouldBe(("RAM almost gone", ThresholdDirection.Below, 2d, 120d, true));
        rules.Single(r => r.Id == "my-crash").ProcessEvents.ShouldBe([ProcessEventKind.Crashed]);
        rules.ShouldNotContain(r => r.Id == "my-bad"); // invalid: skipped by the recorder

        // Saving reports what is wrong, by rule.
        var problems = AnomalyRuleCatalog.Default.Validate(options, strict: true);
        problems.ShouldHaveSingleItem().ShouldContain("Triggers:Custom:2 (Too much): threshold 500 is outside 1–100.");
        AnomalyRuleCatalog.Default.Validate(new TriggerOptions { Custom = [new CustomTriggerRule { Id = "cpu-baseline", Name = "x" }] }, strict: true).ShouldHaveSingleItem().ShouldContain("must be \"my-\"");
        AnomalyRuleCatalog.Default.Validate(new TriggerOptions { Removed = ["nope"] }, strict: true).ShouldHaveSingleItem().ShouldContain("'nope' is not a shipped rule");

        // The recorder never fails on them (an invalid rule is skipped instead).
        new TriggerOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
    }
}
