using System.Text.Json.Serialization;
using PcDiag.Core.Events;

namespace PcDiag.Engine.Events;

/// <summary>
/// One classification rule from event-rules.json. A rule is data only: it selects events by channel, provider,
/// event ID, level and optional field conditions, and describes how to label them. Nothing in a rule is executed.
/// </summary>
public sealed class EventRule
{
    /// <summary>Unique, stable identifier (letters, digits, '.', '_', '-'), e.g. "gpu.tdr". User rules with the same id replace shipped rules.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>False disables the rule (a user file can disable a shipped rule with just its id).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Event log channel, e.g. "System".</summary>
    public string Channel { get; init; } = string.Empty;

    /// <summary>Provider names; empty matches any provider (then <see cref="Levels"/> must restrict to critical/error/warning).</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    /// <summary>Event IDs; empty matches any ID of the providers.</summary>
    public IReadOnlyList<int> EventIds { get; init; } = [];

    /// <summary>Levels (1 critical, 2 error, 3 warning, 4 information); empty matches any level.</summary>
    public IReadOnlyList<int> Levels { get; init; } = [];

    /// <summary>Extra conditions on event fields; all must hold. Evaluated after the event log query.</summary>
    public IReadOnlyList<EventRuleCondition> When { get; init; } = [];

    /// <summary>Matching events are dropped (known noise). Ignore rules win over any other rule.</summary>
    public bool Ignore { get; init; }

    /// <summary>Category assigned to matching events.</summary>
    public EventCategory Category { get; init; }

    /// <summary>Severity assigned to matching events.</summary>
    public Severity Severity { get; init; }

    /// <summary>Title template; <c>{field}</c> inserts an extracted field, plus <c>{provider}</c>, <c>{eventId}</c> and <c>{channel}</c>.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Fields to extract: name → source. Sources: a Data/UserData name (e.g. "ProcessId"), "#0" for the first
    /// unnamed value, "$pid" (logging process), "$binaryText" (binary data as text). Alternatives are separated by
    /// "|", and a transform can follow ":" (hex, prefix0x, file, firstLineValue). The names pid, processName,
    /// processStart, device and service fill the matching event columns; others are stored as event data.
    /// </summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();

    /// <summary>Store the formatted event message. False for events whose message holds personal data (e.g. Wi-Fi network names).</summary>
    public bool StoreMessage { get; init; } = true;

    /// <summary>Also read back to the last boot when recording starts (for events logged during boot after a crash).</summary>
    public bool SinceBoot { get; init; }

    /// <summary>Free-text explanation for people reading the rules file; ignored by the classifier.</summary>
    public string? Note { get; init; }
}

/// <summary>A condition on one event field. Exactly one of <see cref="EqualTo"/>, <see cref="NotEqualTo"/> or <see cref="MinAbs"/> is set.</summary>
public sealed class EventRuleCondition
{
    /// <summary>Field source, with the same syntax as <see cref="EventRule.Fields"/> values.</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>The field equals this text (case-insensitive).</summary>
    [JsonPropertyName("equals")]
    public string? EqualTo { get; init; }

    /// <summary>The field is present and does not equal this text (case-insensitive).</summary>
    [JsonPropertyName("notEquals")]
    public string? NotEqualTo { get; init; }

    /// <summary>The field is a number whose absolute value is at least this.</summary>
    public double? MinAbs { get; init; }
}

/// <summary>The rules file: <c>{ "version": 1, "rules": [ ... ] }</c>.</summary>
public sealed class EventRulesDocument
{
    /// <summary>File format version (1).</summary>
    public int Version { get; init; } = 1;

    /// <summary>Rules in priority order: the first matching rule labels the event.</summary>
    public IReadOnlyList<EventRule> Rules { get; init; } = [];
}
