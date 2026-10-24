using PcDiag.Core.Configuration;

namespace PcDiag.Core.Anomalies;

/// <summary>
/// The categories anomaly rules are listed under. The user can rename, reorder, add and delete them and move any
/// rule (Triggers:Categories, Triggers:RuleCategories); until then these built-in ones apply.
/// </summary>
public static class AnomalyCategories
{
    /// <summary>The built-in categories, in order.</summary>
    public static IReadOnlyList<TriggerCategory> BuiltIn { get; } =
    [
        new() { Id = "processor", Name = "Processor" },
        new() { Id = "memory", Name = "Memory" },
        new() { Id = "graphics", Name = "Graphics" },
        new() { Id = "storage", Name = "Storage" },
        new() { Id = "network", Name = "Network" },
        new() { Id = "sensors", Name = "Sensors and other readings" },
        new() { Id = "events", Name = "Events" },
    ];

    /// <summary>The categories in use: the user's, or the built-in ones.</summary>
    public static IReadOnlyList<TriggerCategory> Of(TriggerOptions options) =>
        options is { Categories.Count: > 0 } ? options.Categories : BuiltIn;

    /// <summary>The category a rule is in until the user moves it: by what it watches.</summary>
    public static string DefaultFor(AnomalyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Kind == AnomalyRuleKind.Event)
        {
            return "events";
        }

        return ForMetric(AnomalyMetrics.IsReading(rule.Metric) ? rule.Metric![AnomalyMetrics.ReadingPrefix.Length..] : rule.Metric ?? "");
    }

    /// <summary>
    /// The category a measurement belongs to: a shipped metric ("gpu.vram") or a reading ("sensor.lhm/gpu-nvidia/0/clock/0",
    /// "disk.disk-….active"). Hardware sensors go with their hardware; the rest (fans, pumps, voltages) with the sensors.
    /// </summary>
    public static string ForMetric(string metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        var m = metric.ToLowerInvariant();
        if (m.StartsWith("sensor.", StringComparison.Ordinal))
        {
            var path = m["sensor.".Length..];
            var hardware = path.StartsWith("lhm/", StringComparison.Ordinal) ? path["lhm/".Length..] : path;
            return hardware switch
            {
                _ when hardware.StartsWith("gpu", StringComparison.Ordinal) => "graphics",
                _ when hardware.StartsWith("intelcpu", StringComparison.Ordinal) || hardware.StartsWith("amdcpu", StringComparison.Ordinal) || hardware.StartsWith("cpu", StringComparison.Ordinal) => "processor",
                _ when hardware.StartsWith("memory", StringComparison.Ordinal) || hardware.StartsWith("ram", StringComparison.Ordinal) || hardware.StartsWith("vram", StringComparison.Ordinal) => "memory",
                _ when hardware.StartsWith("nvme", StringComparison.Ordinal) || hardware.StartsWith("hdd", StringComparison.Ordinal) || hardware.StartsWith("ssd", StringComparison.Ordinal) || hardware.StartsWith("disk", StringComparison.Ordinal) => "storage",
                _ when hardware.StartsWith("nic", StringComparison.Ordinal) || hardware.StartsWith("network", StringComparison.Ordinal) => "network",
                _ => "sensors",
            };
        }

        return m switch
        {
            _ when m.StartsWith("cpu", StringComparison.Ordinal) => "processor",
            _ when m.StartsWith("memory", StringComparison.Ordinal) => "memory",
            _ when m.StartsWith("gpu", StringComparison.Ordinal) => "graphics",
            _ when m.StartsWith("disk", StringComparison.Ordinal) || m.StartsWith("volume", StringComparison.Ordinal) => "storage",
            _ when m.StartsWith("net", StringComparison.Ordinal) => "network",
            _ => "sensors",
        };
    }
}
