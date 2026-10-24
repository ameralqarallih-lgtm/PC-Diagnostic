using System.Text.Json;

namespace PcDiag.Core.Overlay;

/// <summary>A character's own reaction to a status: what it does ("default": its own behaviour), its effect and its prop ("none": none).</summary>
public sealed record OverlayReaction(string? Action = null, string? Effect = null, string? Prop = null);

/// <summary>
/// A prop (a thing shown with a character as it reacts: an ice pack on its head, a cone beside it): its frame is in
/// props.char, the rest here, from reactions.json.
/// </summary>
/// <param name="At">Where it goes: head (on top of its head), above (over its head), hand (held before it, halfway up),
/// beside (on the ground, the way it faces), front (on the ground just before it).</param>
/// <param name="Size">How tall it is against the character.</param>
/// <param name="Grip">How far up it is held, a share of its own height (0: it stands on its point).</param>
/// <param name="Motion">How it moves: none, bob, sway or wobble.</param>
/// <param name="Emit">What it gives off (<see cref="CharacterProps.EmitAt"/>): drip, steam, wind, sparkle, glow, foam, water, or null.</param>
/// <param name="Behind">Drawn behind the character (an umbrella's handle hidden by it).</param>
/// <param name="Mirror">Turned with the character (a fan blows at it either way).</param>
/// <param name="Width">How wide it is against the character (it stands that far clear of it).</param>
/// <param name="NozzleX">Where what it sprays comes out, from its point, against the character: along its drawing (turned with it).</param>
/// <param name="NozzleY">How far up its point that is, against the character.</param>
public sealed record PropInfo(string Id, string Name, string At, double Size, double Grip, string Motion, string? Emit, bool Behind, bool Mirror, double Width = 0, double NozzleX = 0, double NozzleY = 0);

/// <summary>A reading's kind's rules: its limits, the bad way, its full-scale shares, and a reaction per status.</summary>
public sealed record ReadingKindRules(
    string Name,
    bool BadDown,
    double? Low,
    double? High,
    double? Critical,
    (double Low, double High, double Critical)? Share,
    IReadOnlyDictionary<string, (string Action, string? Effect)> Reactions,
    IReadOnlyDictionary<string, string>? Props = null);

/// <summary>What a character reacts to now: the reading, its value's source, its kind and its status.</summary>
public sealed record ReadingJudged(OverlayReading Reading, OverlaySource Source, string Kind, string Status);

/// <summary>
/// Characters reacting to readings, as the editor has it (characters/reactions.ts, from the same reactions.json):
/// each reading is of a kind (performance, temperature, bandwidth, frame rate, frame time, latency, fan speed, power,
/// other) by its source and unit; its value has a status (low, normal, high, critical: critical beyond the critical
/// limit the bad way, low the quiet end); and each kind's status brings a reaction, something to do and an effect
/// around the character, and maybe a prop (an ice pack, a cone), which a character's own settings can change. The
/// effects' frames are effects.char, the props' props.char.
/// </summary>
public static class OverlayReactions
{
    /// <summary>The statuses, in order.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["low", "normal", "high", "critical"];

    private static readonly Lazy<(IReadOnlyDictionary<string, string> Effects, IReadOnlyDictionary<string, PropInfo> Props, IReadOnlyDictionary<string, ReadingKindRules> Kinds)> Table = new(LoadTable);
    private static readonly Lazy<(byte[] Bytes, CharacterPack Pack)> EffectsFile = new(() => LoadPack("effects", b => CharacterPack.ReadEffects(b)));
    private static readonly Lazy<(byte[] Bytes, CharacterPack Pack)> PropsFile = new(() => LoadPack("props", b => CharacterPack.ReadProps(b)));

    /// <summary>The effects by id, with their names.</summary>
    public static IReadOnlyDictionary<string, string> Effects => Table.Value.Effects;

    /// <summary>The reading kinds' rules by kind.</summary>
    public static IReadOnlyDictionary<string, ReadingKindRules> Kinds => Table.Value.Kinds;

    /// <summary>The effects' file (embedded): its bytes and its clips.</summary>
    public static (byte[] Bytes, CharacterPack Pack) EffectsPack => EffectsFile.Value;

    /// <summary>The props by id, in the table's order.</summary>
    public static IReadOnlyDictionary<string, PropInfo> Props => Table.Value.Props;

    /// <summary>The props' file (embedded): its bytes and a clip per prop.</summary>
    public static (byte[] Bytes, CharacterPack Pack) PropsPack => PropsFile.Value;

    /// <summary>The kind of a reading, by its source and unit.</summary>
    public static string KindOf(string source, string unit)
    {
        ArgumentNullException.ThrowIfNull(source);
        var u = (unit ?? string.Empty).Trim();
        return u switch
        {
            _ when u.EndsWith("°C", StringComparison.Ordinal) || u == "C" => "temperature",
            "fps" => "fps",
            "ms" => source.Contains("frametime", StringComparison.Ordinal) ? "frametime" : "latency",
            "B/s" => "bandwidth",
            _ when string.Equals(u, "RPM", StringComparison.OrdinalIgnoreCase) => "fan",
            "W" => "power",
            "%" => "load",
            _ => "other",
        };
    }

    /// <summary>
    /// A reading's status: its own warning and critical limits when it has them (crossed going down when it says so),
    /// otherwise its kind's; bandwidth and power judged against the reading's full scale when it has one.
    /// </summary>
    public static string StatusOf(string kind, OverlayReading? reading, double? value, double? total)
    {
        if (value is not { } raw || !double.IsFinite(raw) || !Kinds.TryGetValue(kind, out var k))
        {
            return "normal";
        }

        var v = reading?.Show == "percent" && total is > 0 ? raw / total.Value * 100 : raw;
        var own = reading?.Warning is not null || reading?.Critical is not null;
        var down = own ? reading!.Below : k.BadDown;
        var scale = reading?.Max ?? (reading?.Show == "percent" ? null : total);
        double? Limit(string which)
        {
            if (k.Share is { } share && scale is > 0)
            {
                return which switch { "low" => share.Low, "high" => share.High, _ => share.Critical } * scale.Value;
            }

            return which switch { "low" => k.Low, "high" => k.High, _ => k.Critical };
        }

        // The limit that warns is the bad way's; a reading's own limits stand alone; the quiet end is its kind's when
        // the reading goes the kind's way.
        var warning = own ? reading!.Warning : down ? Limit("low") : Limit("high");
        var critical = own ? reading!.Critical : Limit("critical");
        var quiet = down == k.BadDown ? (down ? Limit("high") : Limit("low")) : null;
        if (down)
        {
            return critical is { } c && v <= c ? "critical" : warning is { } w && v <= w ? "low" : quiet is { } q && v >= q ? "high" : "normal";
        }

        return critical is { } c2 && v >= c2 ? "critical" : warning is { } w2 && v >= w2 ? "high" : quiet is { } q2 && v <= q2 ? "low" : "normal";
    }

    /// <summary>The reaction to a status: the character's own choice when it made one, otherwise its kind's.</summary>
    public static (string Action, string? Effect) ReactionFor(string kind, string status, IReadOnlyDictionary<string, OverlayReaction>? own)
    {
        var (action, effect) = Kinds.TryGetValue(kind, out var k) && k.Reactions.TryGetValue(status, out var r) ? r : ("default", null);
        var mine = own?.GetValueOrDefault(status);
        return (mine?.Action ?? action, mine?.Effect switch { null => effect, "none" => null, var e => e });
    }

    /// <summary>The prop shown with a status (null: none): the character's own choice when it made one, otherwise its kind's.</summary>
    public static string? PropFor(string kind, string status, IReadOnlyDictionary<string, OverlayReaction>? own)
    {
        var prop = Kinds.TryGetValue(kind, out var k) ? k.Props?.GetValueOrDefault(status) : null;
        return own?.GetValueOrDefault(status)?.Prop switch { null => prop, "none" => null, var p => p };
    }

    /// <summary>What a character reacts to now: its reading (or the panel's most urgent one, for "auto"); null when none.</summary>
    public static ReadingJudged? ReadingFor(OverlayCharacter character, OverlayPanel panel, IReadOnlyDictionary<string, OverlaySource> sources)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(sources);
        ReadingJudged? Judge(OverlayReading reading)
        {
            if (!sources.TryGetValue(reading.Source, out var source) || source.Value is null)
            {
                return null;
            }

            var kind = KindOf(source.Id, source.Unit);
            return new ReadingJudged(reading, source, kind, StatusOf(kind, reading, source.Value, source.Max));
        }

        if (character.React is not { } react)
        {
            return null;
        }

        if (react != "auto")
        {
            return Judge(panel.Readings.FirstOrDefault(r => r.Source == react) ?? new OverlayReading(react));
        }

        // In its order (the readings it puts first, then the rest as on the panel): the first critical one; else the first
        // high one; else the first reading, whatever it shows (low or normal).
        ReadingJudged? best = null;
        foreach (var reading in Ordered(panel.Readings, character.Priority))
        {
            if (character.Ignore?.Contains(reading.Source, StringComparer.Ordinal) == true || Judge(reading) is not { } j)
            {
                continue;
            }

            if (best is null || Urgency(j.Status) > Urgency(best.Status))
            {
                best = j;
            }
        }

        return best;
    }

    /// <summary>How urgent a status is, for choosing what an "auto" character reacts to.</summary>
    private static int Urgency(string status) => status switch { "critical" => 2, "high" => 1, _ => 0 };

    /// <summary>A panel's readings in a character's order: those it puts first, first, then the rest as they are.</summary>
    public static IEnumerable<OverlayReading> Ordered(IReadOnlyList<OverlayReading> readings, IReadOnlyList<string>? priority)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (priority is not { Count: > 0 })
        {
            return readings;
        }

        var rank = priority.Select((source, i) => (source, i)).DistinctBy(p => p.source, StringComparer.Ordinal).ToDictionary(p => p.source, p => p.i, StringComparer.Ordinal);
        return readings.Select((r, i) => (r, i)).OrderBy(p => rank.TryGetValue(p.r.Source, out var at) ? at : priority.Count + p.i).Select(p => p.r);
    }

    /// <summary>How fast a character goes along its route for each way of reacting (0: it stops where it is).</summary>
    public static double PaceOf(string action) => action switch
    {
        "default" or "walk" => 1,
        "run" => 1.8,
        _ => 0,
    };

    /// <summary>What is wrong with a character's reactions; null when nothing is.</summary>
    public static string? Check(OverlayCharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (character.React is { } react && (react.Length is < 1 or > 200))
        {
            return "A character reacts to \"auto\" or to one of its panel's readings.";
        }

        if ((character.Priority is { } order && (order.Count > 64 || order.Any(k => k is not { Length: >= 1 and <= 200 })))
            || (character.Ignore is { } ignored && (ignored.Count > 64 || ignored.Any(k => k is not { Length: >= 1 and <= 200 }))))
        {
            return "A character puts at most 64 readings first, and ignores at most 64.";
        }

        if (character.ReactionsBy is { } by && (by.Count > 64 || by.Keys.Any(k => k.Length is < 1 or > 200)))
        {
            return "A character has its own reactions for at most 64 readings.";
        }

        foreach (var (status, reaction) in (character.Reactions ?? new Dictionary<string, OverlayReaction>()).Concat((character.ReactionsBy ?? new Dictionary<string, IReadOnlyDictionary<string, OverlayReaction>>()).Values.SelectMany(t => t)))
        {
            if (!Statuses.Contains(status))
            {
                return "A character's reactions are for the statuses low, normal, high and critical.";
            }

            if (reaction.Action is { } action && action != "default" && !OverlayCharacters.IsAction(action))
            {
                return $"A character cannot react by {action}.";
            }

            if (reaction.Effect is { } effect && effect != "none" && !Effects.ContainsKey(effect))
            {
                return $"There is no effect {effect}.";
            }

            if (reaction.Prop is { } prop && prop != "none" && !Props.ContainsKey(prop))
            {
                return $"There is no prop {prop}.";
            }
        }

        return null;
    }

    private static (IReadOnlyDictionary<string, string>, IReadOnlyDictionary<string, PropInfo>, IReadOnlyDictionary<string, ReadingKindRules>) LoadTable()
    {
        using var stream = typeof(OverlayReactions).Assembly.GetManifestResourceStream("PcDiag.Core.Overlay.reactions.json")
            ?? throw new InvalidOperationException("The reactions table is missing from PcDiag.Core.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var effects = root.GetProperty("effects").EnumerateArray().ToDictionary(e => e[0].GetString()!, e => e[1].GetString()!, StringComparer.Ordinal);
        var props = new Dictionary<string, PropInfo>(StringComparer.Ordinal);
        foreach (var p in root.GetProperty("props").EnumerateArray())
        {
            bool Flag(string name) => p.TryGetProperty(name, out var f) && f.ValueKind == JsonValueKind.True;
            props[p.GetProperty("id").GetString()!] = new PropInfo(
                p.GetProperty("id").GetString()!, p.GetProperty("name").GetString()!, p.GetProperty("at").GetString()!, p.GetProperty("size").GetDouble(),
                p.GetProperty("grip").GetDouble(), p.GetProperty("motion").GetString()!, p.GetProperty("emit").ValueKind == JsonValueKind.String ? p.GetProperty("emit").GetString() : null,
                Flag("behind"), Flag("mirror"), p.TryGetProperty("width", out var w) ? w.GetDouble() : 0,
                p.TryGetProperty("nozzle", out var n) ? n[0].GetDouble() : 0, p.TryGetProperty("nozzle", out var n2) ? n2[1].GetDouble() : 0);
        }

        var kinds = new Dictionary<string, ReadingKindRules>(StringComparer.Ordinal);
        foreach (var kind in root.GetProperty("kinds").EnumerateObject())
        {
            var k = kind.Value;
            double? Number(string name) => k.GetProperty(name).ValueKind == JsonValueKind.Number ? k.GetProperty(name).GetDouble() : null;
            (double, double, double)? share = k.TryGetProperty("share", out var s) ? (s.GetProperty("low").GetDouble(), s.GetProperty("high").GetDouble(), s.GetProperty("critical").GetDouble()) : null;
            var reactions = k.GetProperty("reactions").EnumerateObject().ToDictionary(
                r => r.Name,
                r => (r.Value[0].GetString()!, r.Value[1].ValueKind == JsonValueKind.Null ? null : r.Value[1].GetString()),
                StringComparer.Ordinal);
            var kindProps = k.TryGetProperty("props", out var shown)
                ? shown.EnumerateObject().Where(s => s.Value.ValueKind == JsonValueKind.String).ToDictionary(s => s.Name, s => s.Value.GetString()!, StringComparer.Ordinal)
                : null;
            kinds[kind.Name] = new ReadingKindRules(k.GetProperty("name").GetString()!, k.GetProperty("bad").GetString() == "down", Number("low"), Number("high"), Number("critical"), share, reactions, kindProps);
        }

        return (effects, props, kinds);
    }

    /// <summary>An embedded file of clips (the effects', the props'): its bytes and its clips.</summary>
    private static (byte[], CharacterPack) LoadPack(string name, Func<byte[], CharacterPack?> read)
    {
        using var stream = typeof(OverlayReactions).Assembly.GetManifestResourceStream($"PcDiag.Core.Overlay.Characters.{name}.char")
            ?? throw new InvalidOperationException($"The {name} are missing from PcDiag.Core.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        return (bytes, read(bytes) ?? throw new InvalidOperationException($"The {name} in PcDiag.Core are not sound."));
    }
}
