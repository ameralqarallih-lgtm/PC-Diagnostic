using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Events;

/// <summary>An event that matched a rule.</summary>
/// <param name="Event">The classified event.</param>
/// <param name="Rule">The rule that labelled it.</param>
public sealed record EventClassification(SystemEvent Event, CompiledEventRule Rule);

/// <summary>
/// Applies the rules to an event record: ignore rules first (any match drops the event), then the first matching
/// rule labels it. Extracted fields fill the event's process, service and device columns and its data.
/// </summary>
public sealed partial class EventClassifier
{
    private const string Unknown = "unknown";

    private static readonly HashSet<string> ReservedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "pid", "processName", "processStart", "device", "service",
    };

    private readonly EventRuleSet _rules;
    private readonly EventRedactor _redactor;
    private readonly int _maxMessageLength;

    /// <summary>Creates a classifier.</summary>
    public EventClassifier(EventRuleSet rules, EventRedactor redactor, int maxMessageLength)
    {
        _rules = rules;
        _redactor = redactor;
        _maxMessageLength = Math.Max(200, maxMessageLength);
    }

    /// <summary>
    /// Classifies one record. <paramref name="formatMessage"/> is called only for events that are kept and whose
    /// rule stores the message (formatting loads the provider's message resources, which is the costly part).
    /// </summary>
    public EventClassification? Classify(EventRecordData record, Func<string?>? formatMessage)
    {
        ArgumentNullException.ThrowIfNull(record);

        CompiledEventRule? match = null;
        foreach (var rule in _rules.Rules)
        {
            if (rule.Rule.Ignore)
            {
                if (rule.Matches(record))
                {
                    return null;
                }
            }
            else if (match is null && rule.Matches(record))
            {
                match = rule;
            }
        }

        return match is null ? null : new EventClassification(Build(record, match, formatMessage), match);
    }

    private SystemEvent Build(EventRecordData record, CompiledEventRule rule, Func<string?>? formatMessage)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, spec) in rule.Fields)
        {
            if (spec.Evaluate(record) is { } value)
            {
                fields[name] = _redactor.Redact(Truncate(value, 500))!;
            }
        }

        var pid = EventValues.ParseProcessId(fields.GetValueOrDefault("pid"));
        var started = EventValues.ParseTimestampTicks(fields.GetValueOrDefault("processStart"));
        ProcessKey? process = pid is { } p && started is { } s ? new ProcessKey(p, s) : null;

        string? message = null;
        if (rule.Rule.StoreMessage)
        {
            message = SafeFormat(formatMessage);
            if (string.IsNullOrWhiteSpace(message))
            {
                // No message resources (e.g. nvlddmkm 153): keep the raw values instead.
                message = string.Join("; ", record.Data.Select(d => EventValues.Clean(d.Value)!).Where(v => v.Length > 0));
            }

            message = _redactor.Redact(Truncate(EventValues.Clean(message)!, _maxMessageLength));
        }

        var data = fields.Where(f => !ReservedFields.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        var level = record.Level is >= 0 and <= 5 ? (EventLevel)record.Level : EventLevel.LogAlways;

        return new SystemEvent(
            record.Timestamp,
            record.Channel,
            record.RecordId,
            record.Provider,
            record.EventId,
            level,
            rule.Rule.Category,
            rule.Rule.Severity,
            _redactor.Redact(Truncate(RenderTitle(rule.Rule.Title, record, fields), 300))!,
            message ?? string.Empty,
            pid,
            fields.GetValueOrDefault("processName"),
            fields.GetValueOrDefault("device"),
            rule.Id,
            process,
            fields.GetValueOrDefault("service"),
            data.Count == 0 ? null : data);
    }

    /// <summary>Fills <c>{field}</c> placeholders; unknown or missing fields render as "unknown".</summary>
    internal static string RenderTitle(string template, EventRecordData record, IReadOnlyDictionary<string, string> fields) =>
        Placeholder().Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return key switch
            {
                "provider" => record.Provider,
                "eventId" => record.EventId.ToString(CultureInfo.InvariantCulture),
                "channel" => record.Channel,
                _ => fields.TryGetValue(key, out var value) ? value : Unknown,
            };
        });

    private static string? SafeFormat(Func<string?>? formatMessage)
    {
        try
        {
            return formatMessage?.Invoke();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var builder = new StringBuilder(value, 0, max - 1, max);
        builder.Append('…');
        return builder.ToString();
    }

    [GeneratedRegex(@"\{([A-Za-z0-9_]{1,40})\}")]
    private static partial Regex Placeholder();
}
