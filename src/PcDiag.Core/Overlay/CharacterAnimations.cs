using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcDiag.Core.Overlay;

/// <summary>
/// A new animation made by the character generator for every character (Settings › Characters › Animations): its id
/// (what a character does, as an action), its name as shown, the prompt it was made from, and each character's clip
/// (by character id).
/// </summary>
/// <param name="Id">2-24 lowercase letters, digits or dashes (<see cref="OverlayCharacters.IsAnimationId"/>).</param>
/// <param name="Clips">By character id: how its clip is coming along, and its file once made.</param>
/// <param name="Kind">animation (something it does) or emotion (a face and a mood: the reactions use the built-in ones);
/// null: an emotion when its id is a built-in emotion's.</param>
/// <param name="Active">Played (false: the characters do the closest they have, its clips kept).</param>
public sealed record CharacterAnimation(string Id, string Name, string Prompt, DateTimeOffset Created, IReadOnlyDictionary<string, AnimationClip> Clips, string? Kind = null, bool Active = true)
{
    /// <summary>animation or emotion.</summary>
    public string KindOf => Kind is "animation" or "emotion" ? Kind : CharacterAnimations.Emotions.Any(e => e.Id == Id) ? "emotion" : "animation";
}

/// <summary>A character's clip of a new animation.</summary>
/// <param name="State">queued, running, done or failed.</param>
/// <param name="File">Its file in the picture library once made (an extra character file: the clip alone, at the character's height).</param>
/// <param name="Step">What the generator is doing now.</param>
/// <param name="Progress">How far it is, 0-1.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Job">The generator's job for it (while it is made).</param>
/// <param name="Active">Played (false: this character does the closest it has, its clip kept).</param>
/// <remarks>Made again, its last file is kept and played until the new one is made.</remarks>
public sealed record AnimationClip(string State, string? File = null, string? Step = null, double Progress = 0, string? Error = null, string? Job = null, bool Active = true);

/// <summary>
/// The new animations, in <c>animations.json</c> in the data folder: written by the Web host (which has the generator
/// make them), read by the recorder (which plays each character's clips with it).
/// </summary>
public static class CharacterAnimations
{
    /// <summary>The file's name in the data folder.</summary>
    public const string FileName = "animations.json";

    /// <summary>The folder (in the data folder) of the clips' files, each named by its content (16 hex digits, ".char").</summary>
    public const string Folder = "animations";

    /// <summary>The most new animations kept.</summary>
    public const int Max = 32;

    /// <summary>
    /// The emotions: new animations the reactions use (worried and tired when a reading is bad, panic when it is
    /// critical, cheer when it is good again), made for every character with one click; until a character has one, it
    /// plays the closest clip it has (worried and tired sit, panic jumps, cheer dances).
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Name, string Prompt)> Emotions =
    [
        ("worried", "Worried", "The character looks worried and anxious: a worried frown, its eyebrows raised in the middle, biting its lip, glancing around nervously and fidgeting in place, no smile."),
        ("panic", "Panicked", "The character panics in alarm: wide frightened eyes, its mouth open in a shout, waving its arms frantically and trembling, staying in place, no smile."),
        ("tired", "Tired", "The character is exhausted and overheated: droopy half-closed eyes, a weary sigh, its shoulders slumping, swaying tiredly in place, no smile."),
        ("cheer", "Cheering", "The character cheers with joy: a big happy smile, its eyes closed in delight, both arms raised in celebration, a little hop in place."),
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// The files a character plays (its clips that are made, the animation and the clip both on): what the recorder
    /// and the editor attach to it. A clip being made again plays its last file meanwhile.
    /// </summary>
    public static IEnumerable<(string Animation, string File)> FilesFor(IEnumerable<CharacterAnimation> animations, string character) =>
        animations.Where(a => a.Active).Select(a => (a.Id, Clip: a.Clips.GetValueOrDefault(character)))
            .Where(x => x.Clip is { Active: true, File: { } file } && OverlayCharacters.IsImportedId(file))
            .Select(x => (x.Id, x.Clip!.File!));

    /// <summary>The new animations in a data folder (none when the file is missing or unreadable).</summary>
    public static IReadOnlyList<CharacterAnimation> Read(string root)
    {
        try
        {
            var file = Path.Combine(root, FileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<List<CharacterAnimation>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes them (whole, through a temporary file).</summary>
    public static void Write(string root, IReadOnlyList<CharacterAnimation> animations)
    {
        var file = Path.Combine(root, FileName);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(animations, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>A name as an id: lowercase letters, digits and dashes ("Wave hello!" is "wave-hello"); null when nothing is left.</summary>
    public static string? IdOf(string name)
    {
        var id = new string([.. (name ?? string.Empty).Trim().ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-')]);
        while (id.Contains("--", StringComparison.Ordinal))
        {
            id = id.Replace("--", "-", StringComparison.Ordinal);
        }

        id = id.Trim('-');
        if (id.Length > 24)
        {
            id = id[..24].TrimEnd('-');
        }

        if (id.Length > 0 && id[0] is >= '0' and <= '9')
        {
            id = "a-" + id;
        }

        return OverlayCharacters.IsAnimationId(id) ? id : null;
    }
}
