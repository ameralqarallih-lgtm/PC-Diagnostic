using System.Text;
using Microsoft.Extensions.Configuration;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;

namespace PcDiag.Agent.Tests;

/// <summary>The recorder reads the user's own and deleted rules from settings.json as the Web host writes them.</summary>
public sealed class TriggerSettingsBindingTests
{
    [Fact]
    public void Own_and_deleted_rules_bind_from_the_settings_file()
    {
        const string json = """
            { "Triggers": {
                "removed": [ "gpu-vram-full" ],
                "custom": [
                  { "id": "my-ram", "name": "RAM almost gone", "kind": "ThresholdDuration", "metric": "memory.available", "direction": "Below", "threshold": 2, "durationSeconds": 120, "severity": "High", "createIncident": true, "cooldownMinutes": 10, "enabled": true, "eventRules": [], "processEvents": [], "displayChanges": [] },
                  { "id": "my-crash", "name": "Any crash", "kind": "Event", "processEvents": [ "Crashed" ], "displayChanges": [ "Disconnected" ], "eventRules": [ "gpu.tdr" ] }
                ] } }
            """;
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();

        var options = configuration.GetSection(TriggerOptions.SectionName).Get<TriggerOptions>()!;

        options.Removed.ShouldBe(["gpu-vram-full"]);
        var rules = AnomalyRuleCatalog.Default.Apply(options);
        var ram = rules.Single(r => r.Id == "my-ram");
        (ram.Direction, ram.Threshold, ram.DurationSeconds, ram.Severity, ram.CooldownMinutes).ShouldBe((ThresholdDirection.Below, 2d, 120d, Severity.High, 10));
        var crash = rules.Single(r => r.Id == "my-crash");
        crash.ProcessEvents.ShouldBe([ProcessEventKind.Crashed]);
        crash.DisplayChanges.ShouldBe([DisplayEventKind.Disconnected]);
        crash.EventRules.ShouldBe(["gpu.tdr"]);
        rules.ShouldNotContain(r => r.Id == "gpu-vram-full");
    }
}
