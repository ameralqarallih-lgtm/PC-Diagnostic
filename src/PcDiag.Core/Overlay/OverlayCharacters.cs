using System.Text.RegularExpressions;

namespace PcDiag.Core.Overlay;

/// <summary>
/// An animated character of a panel (a shipped one, or one the user imported): which one, where it plays, how it
/// moves, what it does, its size, speed and facing.
/// </summary>
/// <param name="Id">Unique within its panel.</param>
/// <param name="Character">Which character: a shipped one's id (<see cref="OverlayCharacters.Shipped"/>) or an imported
/// one's (<see cref="OverlayCharacters.IsImportedId"/>, a file of the picture library).</param>
/// <param name="Place">top (on the panel's top edge), inside (along its inner bottom), left or right (beside it), screen
/// (along its screen's bottom), around (along its top, hopping down beside it and back up) or spot (where it was dropped
/// on the screen: across it at the height <paramref name="Y"/>), wheel (running inside a ring gauge like a hamster in its
/// wheel, faster as the value rises) or needle (riding the end of a gauge's fill or a speedometer's needle), bar (on a bar,
/// pushing the end of its fill) or graph (surfing a graph's line); one without its gauge on the panel plays on the
/// panel's top edge.</param>
/// <param name="Movement">stay, pace (end to end with a pause at each) or wander (to spots along its place).</param>
/// <param name="Action">What it does staying or pausing (idle, sit, sleep, play, jump, dance), or its pace (walk, run).</param>
/// <param name="X">Where it stays along its place, 0-1.</param>
/// <param name="Size">How tall it stands, in pixels at 100 % (24-240).</param>
/// <param name="Speed">How fast it moves, 25-300 %.</param>
/// <param name="Flip">Faces left when staying.</param>
/// <param name="Behind">Behind the panel (outside it only).</param>
/// <param name="Opacity">20-100 %.</param>
/// <param name="React">Reacts to a reading: "auto" (its panel's most urgent one), a reading's source, or null (it does not).</param>
/// <param name="Reactions">Its own reactions in place of its reading kind's, by status (<see cref="OverlayReactions"/>).</param>
/// <param name="Plays">
/// The animations it uses (null: all): it pauses doing only these, walks or runs only if one is there (neither: it stays),
/// and hops around a panel only if it may jump.
/// </param>
/// <param name="Tempo">How fast its animations play, 25-300 % (null: as fast as it moves).</param>
/// <param name="Pause">How long it stops between walks, 25-400 %.</param>
/// <param name="ReactionsBy">
/// Its own reactions for a reading (by source), over <paramref name="Reactions"/>: what it does when that reading is the
/// one it reacts to.
/// </param>
/// <param name="Priority">
/// The readings it reacts to first (sources, the first first; the rest after, in their panel's order): reacting to its
/// panel's (or screen's) readings, it reacts to the first of them that is not normal.
/// </param>
/// <param name="Ignore">The readings it does not react to (sources), reacting to its panel's (or screen's) readings.</param>
/// <param name="Y">Where across its screen it plays, 0 (top) to 1 (bottom), at the spot it was dropped.</param>
/// <param name="Draggable">It can be dragged on the screen itself (its window takes the mouse): dropped, it stays there, or keeps to an area around it.</param>
/// <param name="From">Where along its place the area it keeps to starts, 0-1 (pacing and wandering).</param>
/// <param name="To">Where that area ends, 0-1 (at least 5 % after <paramref name="From"/>).</param>
/// <param name="Gauge">The reading whose gauge a wheel or needle character plays on (its source; null: the first ring,
/// arc or speedometer of its panel).</param>
/// <param name="Mouse">It notices the mouse on a monitor: it watches it, comes over when it rests near it, and enjoys
/// being petted (the mouse rubbed back and forth over it).</param>
/// <param name="Clickable">It answers a click on a monitor (its window takes the mouse where it is drawn, as when it
/// can be dragged): a jump and a word, dizzy after many.</param>
public sealed record OverlayCharacter(
    string Id,
    string Character,
    string Place = "top",
    string Movement = "wander",
    string Action = "idle",
    double X = 0.5,
    double Size = 56,
    double Speed = 100,
    bool Flip = false,
    bool Behind = false,
    int Opacity = 100,
    string? React = null,
    IReadOnlyDictionary<string, OverlayReaction>? Reactions = null,
    IReadOnlyList<string>? Plays = null,
    double? Tempo = null,
    double Pause = 100,
    double From = 0,
    double To = 1,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, OverlayReaction>>? ReactionsBy = null,
    bool Draggable = false,
    IReadOnlyList<string>? Priority = null,
    IReadOnlyList<string>? Ignore = null,
    double Y = 1,
    string? Gauge = null,
    bool Mouse = true,
    bool Clickable = false)
{
    /// <summary>Equal when every setting is, its own reactions compared by content.</summary>
    public bool Equals(OverlayCharacter? other) =>
        other is not null && (Id, Character, Place, Movement, Action, X, Size, Speed, Flip, Behind, Opacity, React) == (other.Id, other.Character, other.Place, other.Movement, other.Action, other.X, other.Size, other.Speed, other.Flip, other.Behind, other.Opacity, other.React)
        && (Tempo, Pause, From, To, Draggable, Y, Gauge, Mouse, Clickable) == (other.Tempo, other.Pause, other.From, other.To, other.Draggable, other.Y, other.Gauge, other.Mouse, other.Clickable)
        && (Plays is null ? other.Plays is null : other.Plays is not null && Plays.SequenceEqual(other.Plays, StringComparer.Ordinal))
        && (Priority ?? []).SequenceEqual(other.Priority ?? [], StringComparer.Ordinal)
        && (Ignore ?? []).SequenceEqual(other.Ignore ?? [], StringComparer.Ordinal)
        && (ReactionsBy ?? EmptyBy).Count == (other.ReactionsBy ?? EmptyBy).Count
        && (ReactionsBy ?? EmptyBy).All(b => (other.ReactionsBy ?? EmptyBy).TryGetValue(b.Key, out var o) && o.Count == b.Value.Count && b.Value.All(r => o.TryGetValue(r.Key, out var x) && x == r.Value))
        && (Reactions ?? Empty).Count == (other.Reactions ?? Empty).Count
        && (Reactions ?? Empty).All(r => (other.Reactions ?? Empty).TryGetValue(r.Key, out var o) && o == r.Value);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Character, Place, Movement, Action, React);

    private static readonly Dictionary<string, OverlayReaction> Empty = [];

    /// <summary>
    /// The character dropped at a point of its line (0-1 along it): one that stays stays there; one that paces or
    /// wanders keeps to an area around it (30 % of its line).
    /// </summary>
    public OverlayCharacter DroppedAt(double along) => DroppedAt(along, null);

    /// <summary>
    /// The character dropped at a point of its screen: <paramref name="across"/> (0-1) along it and <paramref name="down"/>
    /// (0-1) from its top: it plays there (place "spot"), staying or keeping to an area around it.
    /// </summary>
    public OverlayCharacter DroppedAt(double across, double? down)
    {
        if (down is { } y)
        {
            var at = Math.Clamp(across, 0, 1);
            var spot = this with { Place = "spot", Y = Math.Clamp(y, 0, 1), X = at, Movement = Movement == "pace" || Movement == "wander" ? Movement : "stay" };
            if (spot.Movement == "stay")
            {
                return spot;
            }

            var from = Math.Clamp(at - 0.15, 0, 0.7);
            return spot with { From = from, To = Math.Min(1, from + 0.3) };
        }

        return DroppedOnLine(across);
    }

    private OverlayCharacter DroppedOnLine(double along)
    {
        var at = Math.Clamp(along, 0, 1);
        if (Movement == "stay" || Place is "left" or "right" or "around")
        {
            return this with { X = at, Movement = Place is "left" or "right" or "around" ? Movement : "stay" };
        }

        var from = Math.Clamp(at - 0.15, 0, 0.7);
        return this with { X = at, From = from, To = Math.Min(1, from + 0.3) };
    }

    private static readonly Dictionary<string, IReadOnlyDictionary<string, OverlayReaction>> EmptyBy = [];

    /// <summary>Its own reactions when <paramref name="source"/> is what it reacts to: that reading's over its general ones.</summary>
    public IReadOnlyDictionary<string, OverlayReaction>? ReactionsFor(string source)
    {
        if (ReactionsBy?.GetValueOrDefault(source) is not { Count: > 0 } by)
        {
            return Reactions;
        }

        var merged = new Dictionary<string, OverlayReaction>(Reactions ?? Empty, StringComparer.Ordinal);
        foreach (var (status, r) in by)
        {
            var under = merged.GetValueOrDefault(status);
            merged[status] = new OverlayReaction(r.Action ?? under?.Action, r.Effect ?? under?.Effect, r.Prop ?? under?.Prop);
        }

        return merged;
    }
}

/// <summary>
/// The characters' rules, and the shipped characters: original characters generated and animated with local models
/// (tools/character-gen: Animagine XL 4.0 stills, Wan 2.2 I2V clips, RIFE loops), embedded and each read when first used.
/// </summary>
public static partial class OverlayCharacters
{
    /// <summary>The most characters a panel has.</summary>
    public const int MaxPerPanel = 8;

    /// <summary>An imported character's id: a file of the picture library (named by its content).</summary>
    public const string ImportedPattern = "^[0-9a-f]{16}\\.char$";

    /// <summary>Where a character plays.</summary>
    public static readonly IReadOnlySet<string> Places = new HashSet<string>(StringComparer.Ordinal) { "top", "inside", "left", "right", "screen", "around", "spot", "wheel", "needle", "bar", "graph" };

    /// <summary>How it moves.</summary>
    public static readonly IReadOnlySet<string> Movements = new HashSet<string>(StringComparer.Ordinal) { "stay", "pace", "wander" };

    /// <summary>What it does (built in; the generator adds animations of their own: <see cref="IsAnimationId"/>).</summary>
    public static readonly IReadOnlySet<string> Actions = new HashSet<string>(StringComparer.Ordinal) { "idle", "walk", "run", "sit", "sleep", "jump", "play", "dance" };

    /// <summary>A new animation's id (made by the generator for every character): 2-24 lowercase letters, digits or dashes.</summary>
    public const string AnimationPattern = "^[a-z][a-z0-9-]{1,23}$";

    /// <summary>Whether a text is a new animation's id (not a built-in action, which it may look like).</summary>
    public static bool IsAnimationId(string? text) => text is not null && !Actions.Contains(text) && text is not ("held" or "land" or "default") && AnimationId().IsMatch(text);

    /// <summary>Whether a character can do it: a built-in action or a new animation (a character without it idles).</summary>
    public static bool IsAction(string? text) => text is not null && (Actions.Contains(text) || IsAnimationId(text));

    /// <summary>The shipped characters' ids, in the order shown.</summary>
    public static readonly IReadOnlyList<string> ShippedIds = ["mochi", "biscuit", "mikan", "kuma", "yuki", "akari", "hana", "ren", "astro"];

    private static readonly ShippedCharacters Loaded = new();

    /// <summary>The shipped characters by id: each one's file and the character read from it (read when first asked for).</summary>
    public static IReadOnlyDictionary<string, (byte[] Bytes, CharacterPack Pack)> Shipped => Loaded;

    /// <summary>Whether a text is an imported character's id.</summary>
    public static bool IsImportedId(string? text) => text is not null && ImportedId().IsMatch(text);

    /// <summary>Whether a text names a character: a shipped one, or an imported one (whether its file is here or not).</summary>
    public static bool IsCharacterId(string? text) => text is not null && (ShippedIds.Contains(text) || IsImportedId(text));

    /// <summary>What is wrong with a panel's characters; null when nothing is.</summary>
    public static string? Check(OverlayPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        var characters = panel.Characters ?? [];
        if (characters.Count > MaxPerPanel)
        {
            return $"A panel has at most {MaxPerPanel} characters.";
        }

        if (characters.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != characters.Count)
        {
            return "Each character of a panel has its own id.";
        }

        foreach (var c in characters)
        {
            if (string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 40)
            {
                return "A character's id is 1-40 characters.";
            }

            if (!IsCharacterId(c.Character))
            {
                return $"There is no character {c.Character}.";
            }

            if (!Places.Contains(c.Place) || !Movements.Contains(c.Movement) || !IsAction(c.Action))
            {
                return "A character plays top, inside, left, right, screen, around, spot, wheel, needle, bar or graph; stays, paces or wanders; and idles, walks, runs, sits, sleeps, jumps, plays or dances.";
            }

            if (c.Gauge is { } gauge && (gauge.Length is 0 or > 200 || gauge.Any(char.IsControl)))
            {
                return "A character's gauge is a reading's source.";
            }

            if (c.Y is < 0 or > 1 || !double.IsFinite(c.Y))
            {
                return "A character's height on its screen is 0-1.";
            }

            if (c.X is < 0 or > 1 || c.Size is < 24 or > 240 || c.Speed is < 25 or > 300 || c.Opacity is < 20 or > 100 || !double.IsFinite(c.X) || !double.IsFinite(c.Size) || !double.IsFinite(c.Speed))
            {
                return "A character's place along its line is 0-1, its size 24-240 px, its speed 25-300 % and its opacity 20-100 %.";
            }

            if (c.Plays is { } plays && (plays.Count > Actions.Count + 32 || plays.Any(a => !IsAction(a)) || plays.Distinct(StringComparer.Ordinal).Count() != plays.Count))
            {
                return "A character's animations are each one of idle, walk, run, sit, sleep, jump, play, dance or a new animation, once.";
            }

            if (c.Tempo is { } tempo && (!double.IsFinite(tempo) || tempo is < 25 or > 300) || !double.IsFinite(c.Pause) || c.Pause is < 25 or > 400)
            {
                return "A character's animations play at 25-300 % and its pauses last 25-400 %.";
            }

            if (!double.IsFinite(c.From) || !double.IsFinite(c.To) || c.From < 0 || c.To > 1 || c.To - c.From < 0.05)
            {
                return "A character's area along its place is from 0 to 1, at least 5 % wide.";
            }

            if (OverlayReactions.Check(c) is { } reactionProblem)
            {
                return reactionProblem;
            }
        }

        return null;
    }

    /// <summary>A shipped character's file and the character read from it.</summary>
    internal static (byte[] Bytes, CharacterPack Pack) Load(string id)
    {
        using var stream = typeof(OverlayCharacters).Assembly.GetManifestResourceStream($"PcDiag.Core.Overlay.Characters.{id}.char")
            ?? throw new InvalidOperationException($"The character {id} is missing from PcDiag.Core.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        return (bytes, CharacterPack.Read(bytes) ?? throw new InvalidOperationException($"The character {id} in PcDiag.Core is not a sound one."));
    }

    /// <summary>The shipped characters, each read from the assembly the first time it is asked for.</summary>
    private sealed class ShippedCharacters : IReadOnlyDictionary<string, (byte[] Bytes, CharacterPack Pack)>
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<(byte[], CharacterPack)>> _read = new(StringComparer.Ordinal);

        public (byte[] Bytes, CharacterPack Pack) this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => ShippedIds;

        public IEnumerable<(byte[] Bytes, CharacterPack Pack)> Values => ShippedIds.Select(id => this[id]);

        public int Count => ShippedIds.Count;

        public bool ContainsKey(string key) => ShippedIds.Contains(key);

        public bool TryGetValue(string key, out (byte[] Bytes, CharacterPack Pack) value)
        {
            if (!ShippedIds.Contains(key))
            {
                value = default;
                return false;
            }

            value = _read.GetOrAdd(key, id => new Lazy<(byte[], CharacterPack)>(() => Load(id))).Value;
            return true;
        }

        public IEnumerator<KeyValuePair<string, (byte[] Bytes, CharacterPack Pack)>> GetEnumerator() =>
            ShippedIds.Select(id => new KeyValuePair<string, (byte[] Bytes, CharacterPack Pack)>(id, this[id])).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [GeneratedRegex(ImportedPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ImportedId();

    [GeneratedRegex(AnimationPattern, RegexOptions.CultureInvariant)]
    private static partial Regex AnimationId();
}
