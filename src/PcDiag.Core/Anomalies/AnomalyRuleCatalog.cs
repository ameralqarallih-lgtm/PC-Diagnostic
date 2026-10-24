using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;

namespace PcDiag.Core.Anomalies;

/// <summary>
/// The shipped anomaly rules (anomaly-rules.json, embedded) and how the user's "Triggers" settings change them.
/// Used by the Agent to evaluate rules and by the Web host to show and validate the Triggers settings.
/// </summary>
public sealed partial class AnomalyRuleCatalog
{
    private static readonly Lazy<AnomalyRuleCatalog> Shipped = new(() =>
    {
        using var stream = typeof(AnomalyRuleCatalog).Assembly.GetManifestResourceStream("PcDiag.Core.Anomalies.anomaly-rules.json")
            ?? throw new InvalidOperationException("The shipped anomaly rules are missing from the assembly.");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    });

    private readonly Dictionary<string, AnomalyRule> _byId;

    private AnomalyRuleCatalog(IReadOnlyList<AnomalyRule> rules)
    {
        Rules = rules;
        _byId = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The shipped rules.</summary>
    public static AnomalyRuleCatalog Default => Shipped.Value;

    /// <summary>All rules, in display order.</summary>
    public IReadOnlyList<AnomalyRule> Rules { get; }

    /// <summary>A rule by ID (case-insensitive), or null.</summary>
    public AnomalyRule? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>Parses and checks a rules document. Throws <see cref="InvalidDataException"/> describing every problem.</summary>
    public static AnomalyRuleCatalog Load(string json)
    {
        RulesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, AnomalyRuleJsonContext.Default.RulesDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The anomaly rules are not valid JSON: {ex.Message}", ex);
        }

        var rules = (document?.Rules ?? []).Select(r => r.ToRule()).Select(r => r with { Category = AnomalyCategories.DefaultFor(r) }).ToList();
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!RuleId().IsMatch(rule.Id))
            {
                problems.Add($"Rule ID '{rule.Id}' must be lowercase letters, digits and dashes.");
            }

            if (!seen.Add(rule.Id))
            {
                problems.Add($"Rule ID '{rule.Id}' is used twice.");
            }

            problems.AddRange(Check(rule).Select(p => $"{rule.Id}: {p}"));
        }

        return problems.Count > 0 ? throw new InvalidDataException(string.Join(Environment.NewLine, problems)) : new AnomalyRuleCatalog(rules);
    }

    /// <summary>The rules with the user's settings applied (unknown rule IDs are ignored).</summary>
    public IReadOnlyList<AnomalyRule> Apply(TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var removed = options.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Rules.Where(rule => !removed.Contains(rule.Id))
            .Select(rule => options.RuleCategories.TryGetValue(rule.Id, out var moved) ? rule with { Category = moved } : rule)
            .Concat(CustomRules(options))
            .Select(rule => options.Rules.TryGetValue(rule.Id, out var change) && change is not null ? Apply(rule, change) : rule).ToList();
    }

    /// <summary>The user's own rules that are valid (an invalid one is skipped; saving reports why).</summary>
    public static IReadOnlyList<AnomalyRule> CustomRules(TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. options.Custom.Where(c => c is not null && CustomId().IsMatch(c.Id) && seen.Add(c.Id)).Select(ToRule).Where(r => !Check(r).Any())];
    }

    /// <summary>A user rule as an anomaly rule.</summary>
    public static AnomalyRule ToRule(CustomTriggerRule custom)
    {
        ArgumentNullException.ThrowIfNull(custom);
        var name = custom.Name.Trim();
        var events = custom.Kind == AnomalyRuleKind.Event;
        var rule = new AnomalyRule
        {
            Id = custom.Id,
            Name = name,
            Classification = name,
            Kind = custom.Kind,
            Metric = custom.Kind == AnomalyRuleKind.Event ? null : custom.Metric,
            Direction = custom.Direction,
            Threshold = custom.Kind == AnomalyRuleKind.Event ? 0 : custom.Threshold,
            DurationSeconds = custom.DurationSeconds,
            WindowSeconds = custom.WindowSeconds,
            EventRules = events ? [.. custom.EventRules.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()), .. custom.WindowsEvents.Select((_, i) => WindowsEventRuleId(custom.Id, i))] : [],
            ProcessEvents = events ? [.. custom.ProcessEvents] : [],
            ProcessNames = events ? [.. custom.ProcessNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())] : [],
            DisplayChanges = events ? [.. custom.DisplayChanges] : [],
            DisplayTargets = events ? [.. custom.DisplayTargets.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())] : [],
            ReadingLabel = AnomalyMetrics.IsReading(custom.Metric) ? custom.ReadingLabel : null,
            ReadingUnit = AnomalyMetrics.IsReading(custom.Metric) ? custom.ReadingUnit ?? "" : null,
            Description = events ? DescribeEvents(custom) : null,
            Note = "Your rule.",
            Severity = custom.Severity,
            Enabled = custom.Enabled,
            CreateIncident = custom.CreateIncident,
            CooldownMinutes = custom.CooldownMinutes,
        };
        return rule with { Category = string.IsNullOrWhiteSpace(custom.Category) ? AnomalyCategories.DefaultFor(rule) : custom.Category };
    }

    /// <summary>The classified event rule made for a Windows event a user rule describes.</summary>
    public static string WindowsEventRuleId(string customId, int index) => $"{customId}.w{index}";

    private static string DescribeEvents(CustomTriggerRule custom)
    {
        var parts = new List<string>();
        if (custom.ProcessEvents.Count > 0)
        {
            var who = custom.ProcessNames.Count > 0 ? string.Join(", ", custom.ProcessNames) : "a program";
            parts.Add($"{who} {string.Join(" or ", custom.ProcessEvents.Select(k => k.ToString().ToLowerInvariant()))}");
        }

        if (custom.DisplayChanges.Count > 0)
        {
            var which = custom.DisplayTargets.Count > 0 ? string.Join(", ", custom.DisplayTargets) : "a display";
            parts.Add($"{which}: {string.Join(" or ", custom.DisplayChanges.Select(k => k.ToString()))}");
        }

        if (custom.EventRules.Count > 0)
        {
            parts.Add($"Windows events {string.Join(", ", custom.EventRules)}");
        }

        foreach (var e in custom.WindowsEvents)
        {
            var ids = e.EventIds.Count > 0 ? $" event {string.Join("/", e.EventIds)}" : "";
            parts.Add($"{e.Channel}: {(string.IsNullOrWhiteSpace(e.Provider) ? "any source" : e.Provider)}{ids}");
        }

        return parts.Count > 0 ? "When " + string.Join("; ", parts) : custom.Name;
    }

    /// <summary>One rule with one override applied.</summary>
    public static AnomalyRule Apply(AnomalyRule rule, TriggerRuleSettings change)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(change);
        return rule with
        {
            Enabled = change.Enabled ?? rule.Enabled,
            CreateIncident = change.CreateIncident ?? rule.CreateIncident,
            Threshold = rule.Kind == AnomalyRuleKind.Event ? rule.Threshold : change.Threshold ?? rule.Threshold,
            DurationSeconds = rule.UsesDuration ? change.DurationSeconds ?? rule.DurationSeconds : rule.DurationSeconds,
            WindowSeconds = rule.UsesWindow ? change.WindowSeconds ?? rule.WindowSeconds : rule.WindowSeconds,
            CooldownMinutes = change.CooldownMinutes ?? rule.CooldownMinutes,
        };
    }

    /// <summary>Problems with the user's trigger settings (unknown rule IDs are reported only when <paramref name="strict"/>).</summary>
    public IReadOnlyList<string> Validate(TriggerOptions options, bool strict)
    {
        ArgumentNullException.ThrowIfNull(options);
        var problems = new List<string>();
        if (options.MaxIncidentsPerHour is < 1 or > 60)
        {
            problems.Add($"Triggers:MaxIncidentsPerHour must be between 1 and 60 (was {options.MaxIncidentsPerHour}).");
        }

        var custom = new Dictionary<string, AnomalyRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (definition, index) in options.Custom.Select((c, i) => (c, i)))
        {
            var key = $"Triggers:Custom:{index}";
            if (definition is null)
            {
                continue;
            }

            if (!CustomId().IsMatch(definition.Id ?? ""))
            {
                problems.Add($"{key}: the ID must be \"my-\" followed by lowercase letters, digits and dashes (was '{definition.Id}').");
                continue;
            }

            if (Find(definition.Id!) is not null || custom.ContainsKey(definition.Id!))
            {
                problems.Add($"{key}: the ID '{definition.Id}' is already used.");
                continue;
            }

            var rule = ToRule(definition);
            foreach (var (match, at) in definition.Kind == AnomalyRuleKind.Event ? definition.WindowsEvents.Select((m, i) => (m, i)) : [])
            {
                if (match is null || string.IsNullOrWhiteSpace(match.Channel) || match.Channel.Length > 200)
                {
                    problems.Add($"{key} ({rule.Name}): Windows event {at + 1} needs its log (e.g. System).");
                }
                else if (string.IsNullOrWhiteSpace(match.Provider) && match.EventIds.Count == 0 && (match.Levels.Count == 0 || match.Levels.Any(l => l > 3)))
                {
                    problems.Add($"{key} ({rule.Name}): Windows event {at + 1} needs a source, event IDs, or only critical, error and warning levels.");
                }
                else if (match.EventIds.Any(id => id is < 0 or > 65535) || match.Levels.Any(l => l is < 1 or > 5))
                {
                    problems.Add($"{key} ({rule.Name}): Windows event {at + 1}: event IDs are 0–65535 and levels 1–5.");
                }
            }

            if (definition.Kind != AnomalyRuleKind.Event && definition.Metric is { } metric && metric.StartsWith(AnomalyMetrics.ReadingPrefix, StringComparison.Ordinal) && !AnomalyMetrics.IsReading(metric))
            {
                problems.Add($"{key} ({rule.Name}): choose a reading.");
            }

            if (string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 80)
            {
                problems.Add($"{key}: give the rule a name (at most 80 characters).");
            }

            problems.AddRange(Check(rule).Where(p => !p.StartsWith("name and", StringComparison.Ordinal)).Select(p => $"{key} ({rule.Name}): {p}"));
            if (definition.CooldownMinutes is < 0 or > 1440)
            {
                problems.Add($"{key} ({rule.Name}): the cooldown must be between 0 and 1440 minutes.");
            }

            if (rule.UsesDuration && rule.DurationSeconds > 3600 || rule.UsesWindow && rule.WindowSeconds > 600)
            {
                problems.Add($"{key} ({rule.Name}): the duration is at most 3600 s and the window at most 600 s.");
            }

            custom[definition.Id!] = rule;
        }

        if (strict)
        {
            problems.AddRange(options.Removed.Where(id => Find(id) is null).Select(id => $"Triggers:Removed: '{id}' is not a shipped rule."));
        }

        var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in options.Categories)
        {
            if (category is null || !CategoryId().IsMatch(category.Id ?? "") || string.IsNullOrWhiteSpace(category.Name) || category.Name.Trim().Length > 40)
            {
                problems.Add($"Triggers:Categories: each category needs a name (at most 40 characters) and an ID of lowercase letters, digits and dashes (was '{category?.Id}').");
            }
            else if (!categories.Add(category.Id!))
            {
                problems.Add($"Triggers:Categories: the ID '{category.Id}' is used twice.");
            }
        }

        foreach (var (id, change) in options.Rules)
        {
            if (custom.ContainsKey(id))
            {
                continue; // a user rule is changed in its definition
            }

            if (Find(id) is not { } rule)
            {
                if (strict)
                {
                    problems.Add($"Triggers:Rules:{id} is not a known rule.");
                }

                continue;
            }

            if (change is null)
            {
                continue;
            }

            var key = $"Triggers:Rules:{rule.Id}";
            var (min, max) = rule.ThresholdRange;
            if (change.Threshold is { } threshold && rule.Kind != AnomalyRuleKind.Event && (double.IsNaN(threshold) || threshold < min || threshold > max))
            {
                problems.Add($"{key}:Threshold must be between {AnomalyMetrics.Format(min, rule.ThresholdUnit)} and {AnomalyMetrics.Format(max, rule.ThresholdUnit)} (was {threshold}).");
            }

            if (change.DurationSeconds is { } duration && rule.UsesDuration && (double.IsNaN(duration) || duration < 1 || duration > 3600))
            {
                problems.Add($"{key}:DurationSeconds must be between 1 and 3600 (was {duration}).");
            }

            if (change.WindowSeconds is { } window && rule.UsesWindow && (double.IsNaN(window) || window < 1 || window > 600))
            {
                problems.Add($"{key}:WindowSeconds must be between 1 and 600 (was {window}).");
            }

            if (change.CooldownMinutes is < 0 or > 1440)
            {
                problems.Add($"{key}:CooldownMinutes must be between 0 and 1440 (was {change.CooldownMinutes}).");
            }
        }

        return problems;
    }

    private static IEnumerable<string> Check(AnomalyRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name) || string.IsNullOrWhiteSpace(rule.Classification))
        {
            yield return "name and classification are required.";
        }

        if (rule.Kind == AnomalyRuleKind.Event)
        {
            if (rule.EventRules.Count + rule.ProcessEvents.Count + rule.DisplayChanges.Count == 0)
            {
                yield return "an event rule needs eventRules, processEvents or displayChanges.";
            }

            yield break;
        }

        if (rule.MetricInfo is null)
        {
            yield return $"unknown metric '{rule.Metric}'.";
            yield break;
        }

        var (min, max) = rule.ThresholdRange;
        if (rule.Threshold < min || rule.Threshold > max)
        {
            yield return $"threshold {rule.Threshold} is outside {min}–{max}.";
        }

        if (rule.UsesDuration && rule.DurationSeconds < 1)
        {
            yield return "durationSeconds must be at least 1.";
        }

        if (rule.UsesWindow && rule.WindowSeconds < 1)
        {
            yield return "windowSeconds must be at least 1.";
        }

        if (rule.Kind == AnomalyRuleKind.RateOfChange && rule.GuardMetric is not null && AnomalyMetrics.Find(rule.GuardMetric) is null)
        {
            yield return $"unknown guard metric '{rule.GuardMetric}'.";
        }

        if (rule.Kind == AnomalyRuleKind.BaselineDeviation && (rule.BaselineMinutes < 1 || rule.WarmupMinutes < 0 || rule.MinStdDev <= 0))
        {
            yield return "baselineMinutes must be at least 1, warmupMinutes at least 0 and minStdDev above 0.";
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex RuleId();

    [GeneratedRegex("^my-[a-z0-9][a-z0-9-]{0,36}$")]
    private static partial Regex CustomId();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex CategoryId();

    /// <summary>The rules file.</summary>
    internal sealed record RulesDocument(IReadOnlyList<RuleDocument> Rules);

    /// <summary>
    /// One rule as written in the file. Separate from <see cref="AnomalyRule"/> because generated deserialization
    /// sets init-only members that are missing from the JSON to their type's default, not to their initializer, so
    /// every default is applied here explicitly.
    /// </summary>
    internal sealed record RuleDocument(
        string? Id,
        string? Name,
        string? Classification,
        AnomalyRuleKind Kind,
        string? Metric,
        ThresholdDirection? Direction,
        double? Threshold,
        double? DurationSeconds,
        double? WindowSeconds,
        string? GuardMetric,
        double? GuardMinimum,
        double? BaselineMinutes,
        double? WarmupMinutes,
        double? MinStdDev,
        IReadOnlyList<string>? EventRules,
        IReadOnlyList<ProcessEventKind>? ProcessEvents,
        IReadOnlyList<DisplayEventKind>? DisplayChanges,
        string? Description,
        string? Note,
        Severity? Severity,
        bool? Enabled,
        bool? CreateIncident,
        int? CooldownMinutes)
    {
        public AnomalyRule ToRule()
        {
            var defaults = new AnomalyRule { Id = "", Name = "", Classification = "", Kind = Kind };
            return defaults with
            {
                Id = Id ?? "",
                Name = Name ?? "",
                Classification = Classification ?? "",
                Metric = Metric,
                Direction = Direction ?? defaults.Direction,
                Threshold = Threshold ?? defaults.Threshold,
                DurationSeconds = DurationSeconds ?? defaults.DurationSeconds,
                WindowSeconds = WindowSeconds ?? defaults.WindowSeconds,
                GuardMetric = GuardMetric,
                GuardMinimum = GuardMinimum ?? defaults.GuardMinimum,
                BaselineMinutes = BaselineMinutes ?? defaults.BaselineMinutes,
                WarmupMinutes = WarmupMinutes ?? defaults.WarmupMinutes,
                MinStdDev = MinStdDev ?? defaults.MinStdDev,
                EventRules = EventRules ?? defaults.EventRules,
                ProcessEvents = ProcessEvents ?? defaults.ProcessEvents,
                DisplayChanges = DisplayChanges ?? defaults.DisplayChanges,
                Description = Description,
                Note = Note,
                Severity = Severity ?? defaults.Severity,
                Enabled = Enabled ?? defaults.Enabled,
                CreateIncident = CreateIncident ?? defaults.CreateIncident,
                CooldownMinutes = CooldownMinutes ?? defaults.CooldownMinutes,
            };
        }
    }
}

/// <summary>Source-generated JSON for the rules file.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AnomalyRuleCatalog.RulesDocument))]
internal sealed partial class AnomalyRuleJsonContext : JsonSerializerContext;
