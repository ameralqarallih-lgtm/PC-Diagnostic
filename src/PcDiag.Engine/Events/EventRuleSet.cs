using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PcDiag.Core.Events;

namespace PcDiag.Engine.Events;

/// <summary>A validated rule with its field sources parsed.</summary>
public sealed class CompiledEventRule
{
    private readonly HashSet<string> _providers;
    private readonly HashSet<int> _eventIds;
    private readonly HashSet<int> _levels;
    private readonly (EventFieldSpec Spec, EventRuleCondition Condition)[] _conditions;

    internal CompiledEventRule(EventRule rule, IReadOnlyList<(string Name, EventFieldSpec Spec)> fields, (EventFieldSpec, EventRuleCondition)[] conditions)
    {
        Rule = rule;
        Fields = fields;
        _conditions = conditions;
        _providers = new HashSet<string>(rule.Providers, StringComparer.OrdinalIgnoreCase);
        _eventIds = [.. rule.EventIds];
        _levels = [.. rule.Levels];
    }

    /// <summary>The rule as written.</summary>
    public EventRule Rule { get; }

    /// <summary>Rule id.</summary>
    public string Id => Rule.Id;

    internal IReadOnlyList<(string Name, EventFieldSpec Spec)> Fields { get; }

    /// <summary>True when the event matches the rule's channel, provider, ID, level and conditions.</summary>
    public bool Matches(EventRecordData record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!record.Channel.Equals(Rule.Channel, StringComparison.OrdinalIgnoreCase)
            || (_providers.Count > 0 && !_providers.Contains(record.Provider))
            || (_eventIds.Count > 0 && !_eventIds.Contains(record.EventId))
            || (_levels.Count > 0 && !_levels.Contains(record.Level)))
        {
            return false;
        }

        foreach (var (spec, condition) in _conditions)
        {
            if (!Holds(spec.Evaluate(record), condition))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Holds(string? value, EventRuleCondition condition)
    {
        if (condition.EqualTo is { } expected)
        {
            return string.Equals(value ?? string.Empty, expected, StringComparison.OrdinalIgnoreCase);
        }

        if (condition.NotEqualTo is { } unwanted)
        {
            return value is not null && !value.Equals(unwanted, StringComparison.OrdinalIgnoreCase);
        }

        if (condition.MinAbs is { } min)
        {
            return EventValues.TryParseInteger(value, out var integer) ? Math.Abs((double)integer) >= min
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && Math.Abs(number) >= min;
        }

        return false;
    }
}

/// <summary>
/// The active classification rules: the shipped event-rules.json merged with an optional user file.
/// User rules with a shipped rule's id replace it in place; new user rules take precedence over shipped ones.
/// Invalid user rules are skipped (and reported in <see cref="Problems"/>); the shipped rules must be valid.
/// </summary>
public sealed partial class EventRuleSet
{
    /// <summary>Upper bound on rules, so a broken user file cannot make queries unbounded.</summary>
    public const int MaxRules = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private EventRuleSet(IReadOnlyList<CompiledEventRule> rules, IReadOnlyList<string> problems)
    {
        Rules = rules;
        Problems = problems;
        Channels = rules.Where(r => !r.Rule.Ignore).Select(r => r.Rule.Channel).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Enabled rules in priority order.</summary>
    public IReadOnlyList<CompiledEventRule> Rules { get; }

    /// <summary>Channels that at least one (non-ignore) rule selects from.</summary>
    public IReadOnlyList<string> Channels { get; }

    /// <summary>Problems found in the user rules file (those rules were skipped).</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The shipped rules (embedded event-rules.json).</summary>
    public static string LoadDefaultJson()
    {
        using var stream = typeof(EventRuleSet).Assembly.GetManifestResourceStream("PcDiag.Engine.Events.event-rules.json")
            ?? throw new InvalidOperationException("The embedded event-rules.json is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Builds the rule set from the shipped rules and an optional user file.</summary>
    /// <exception cref="InvalidOperationException">The shipped rules are invalid.</exception>
    public static EventRuleSet Load(string defaultJson, string? userJson)
    {
        var defaults = Parse(defaultJson, out var defaultError);
        if (defaultError is not null)
        {
            throw new InvalidOperationException("Shipped event rules are invalid: " + defaultError);
        }

        var problems = new List<string>();
        var merged = new List<EventRule>();
        var defaultErrors = new List<string>();
        foreach (var rule in defaults!.Rules)
        {
            if (Validate(rule) is { } error)
            {
                defaultErrors.Add($"rule '{rule.Id}': {error}");
            }

            merged.Add(rule);
        }

        if (defaultErrors.Count > 0)
        {
            throw new InvalidOperationException("Shipped event rules are invalid: " + string.Join("; ", defaultErrors));
        }

        if (!string.IsNullOrWhiteSpace(userJson))
        {
            var user = Parse(userJson, out var userError);
            if (userError is not null)
            {
                problems.Add("event-rules.json could not be read: " + userError);
            }
            else
            {
                var added = new List<EventRule>();
                foreach (var rule in user!.Rules)
                {
                    // A disabled user rule only needs an id (it switches a shipped rule off).
                    var error = rule.Enabled ? Validate(rule) : (IdPattern().IsMatch(rule.Id ?? string.Empty) ? null : "invalid id");
                    if (error is not null)
                    {
                        problems.Add($"rule '{rule.Id}': {error}; skipped");
                        continue;
                    }

                    var existing = merged.FindIndex(r => r.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing >= 0)
                    {
                        merged[existing] = rule;
                    }
                    else if (added.Exists(r => r.Id.Equals(rule.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"rule '{rule.Id}': duplicate id; skipped");
                    }
                    else
                    {
                        added.Add(rule);
                    }
                }

                merged.InsertRange(0, added);
            }
        }

        var duplicate = merged.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Shipped event rules contain the duplicate id '{duplicate.Key}'.");
        }

        if (merged.Count > MaxRules)
        {
            problems.Add($"more than {MaxRules} rules; only the first {MaxRules} are used");
            merged = merged.Take(MaxRules).ToList();
        }

        var compiled = merged.Where(r => r.Enabled).Select(Compile).ToList();
        return new EventRuleSet(compiled, problems);
    }

    /// <summary>Checks one rule. Returns null when valid, otherwise the problem.</summary>
    public static string? Validate(EventRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrEmpty(rule.Id) || !IdPattern().IsMatch(rule.Id))
        {
            return "id must be 1–100 letters, digits, '.', '_' or '-'";
        }

        if (string.IsNullOrEmpty(rule.Channel) || !ChannelPattern().IsMatch(rule.Channel))
        {
            return "channel must be 1–200 letters, digits, spaces, '.', '_', '-' or '/'";
        }

        // Names go into an XPath query; the character set rules out quotes and XML markup entirely.
        if (rule.Providers.Any(p => string.IsNullOrEmpty(p) || !ProviderPattern().IsMatch(p)))
        {
            return "provider names must be 1–200 letters, digits, spaces, '.', '_', '-', '(' or ')'";
        }

        if (rule.EventIds.Any(id => id is < 0 or > 65535))
        {
            return "event IDs must be between 0 and 65535";
        }

        if (rule.Levels.Any(l => l is < 0 or > 5))
        {
            return "levels must be between 0 and 5";
        }

        if (rule.Providers.Count == 0 && (rule.Levels.Count == 0 || rule.Levels.Any(l => l is < 1 or > 3)))
        {
            return "a rule without providers must be limited to levels 1–3 (critical, error, warning); the event log is never collected wholesale";
        }

        if (!rule.Ignore)
        {
            if (string.IsNullOrWhiteSpace(rule.Title) || rule.Title.Length > 300)
            {
                return "title must be 1–300 characters";
            }

            if (!Enum.IsDefined(rule.Category) || !Enum.IsDefined(rule.Severity))
            {
                return "unknown category or severity";
            }
        }

        foreach (var (name, source) in rule.Fields)
        {
            if (!FieldNamePattern().IsMatch(name))
            {
                return $"field name '{name}' must be 1–40 letters, digits or '_'";
            }

            if (EventFieldSpec.TryParse(source, out var error) is null)
            {
                return $"field '{name}': {error}";
            }
        }

        foreach (var condition in rule.When)
        {
            if (EventFieldSpec.TryParse(condition.Field, out var error) is null)
            {
                return $"condition: {error}";
            }

            var set = (condition.EqualTo is null ? 0 : 1) + (condition.NotEqualTo is null ? 0 : 1) + (condition.MinAbs is null ? 0 : 1);
            if (set != 1)
            {
                return "each condition needs exactly one of equals, notEquals or minAbs";
            }
        }

        return null;
    }

    private static EventRulesDocument? Parse(string json, out string? error)
    {
        error = null;
        try
        {
            var document = JsonSerializer.Deserialize<EventRulesDocument>(json, JsonOptions);
            if (document is null)
            {
                error = "empty document";
                return null;
            }

            if (document.Version != 1)
            {
                error = $"unsupported version {document.Version.ToString(CultureInfo.InvariantCulture)} (expected 1)";
                return null;
            }

            return document;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private static CompiledEventRule Compile(EventRule rule)
    {
        var fields = rule.Fields.Select(f => (f.Key, EventFieldSpec.TryParse(f.Value, out _)!)).ToList();
        var conditions = rule.When.Select(c => (EventFieldSpec.TryParse(c.Field, out _)!, c)).ToArray();
        return new CompiledEventRule(rule, fields, conditions);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9 ._/-]{1,200}$")]
    private static partial Regex ChannelPattern();

    [GeneratedRegex("^[A-Za-z0-9 ._()-]{1,200}$")]
    private static partial Regex ProviderPattern();

    [GeneratedRegex("^[A-Za-z0-9_]{1,40}$")]
    private static partial Regex FieldNamePattern();
}
