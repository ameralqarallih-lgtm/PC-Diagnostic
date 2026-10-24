using System.Buffers.Binary;
using System.Text.Json;

namespace PcDiag.Core.Overlay;

/// <summary>
/// An animated character (".char"): a clip of transparent PNG frames for each thing it does (idle, walk, jump, …), all
/// frames the same size, with the point its feet stand on and how tall it stands. The shipped characters are baked
/// from their drawings (web/characters); the editor makes the others from animations the user imports (GIF, WebP,
/// APNG, video, PNG frames, sprite sheets). The editor and the recorder play the same frames. Layout, little-endian:
/// <code>
///  0  "PCDC"
///  4  version (1)        5  reserved (0)
///  6  the manifest's length (4 bytes), then the manifest (UTF-8 JSON, below)
///     then every clip's frames (PNG), one after another, in the manifest's order
/// </code>
/// The manifest: <c>{"name", "width", "height", "anchor": [x, y], "head": [x, y], "tall", "faces": 1 | -1, "clips":
/// [{"action", "fps", "frames": [each frame's length]}]}</c>; the anchor (its feet), its head (the top of it, where
/// effects go; optional) and how tall it stands are in the frames' pixels, and <c>faces</c> is the way the drawing looks
/// (1: right). The effects shown around reacting characters are one such file too (<see cref="ReadEffects"/>): a clip
/// per effect, each saying what it is attached to (<c>"at"</c>: head or feet) and whether it turns with the character
/// (<c>"mirror"</c>).
/// </summary>
public sealed class CharacterPack
{
    /// <summary>The most bytes a character may have.</summary>
    public const int MaxBytes = 48 * 1024 * 1024;

    /// <summary>The largest a frame's side may be, in pixels.</summary>
    public const int MaxSide = 1024;

    /// <summary>The most frames a clip may have.</summary>
    public const int MaxFrames = 300;

    /// <summary>The most frames a character may have in all.</summary>
    public const int MaxTotalFrames = 1200;

    private const int ManifestLimit = 256 * 1024;

    private CharacterPack(string name, int width, int height, double anchorX, double anchorY, double tall, int faces, IReadOnlyList<CharacterClip> clips, (double X, double Y)? head)
    {
        Head = head ?? (anchorX, Math.Max(0, anchorY - tall));
        Name = name;
        Width = width;
        Height = height;
        AnchorX = anchorX;
        AnchorY = anchorY;
        Tall = tall;
        Faces = faces;
        Clips = clips;
    }

    /// <summary>Its name, as shown.</summary>
    public string Name { get; }

    /// <summary>Every frame's width in pixels.</summary>
    public int Width { get; }

    /// <summary>Every frame's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Where its feet stand in a frame (pixels from the left).</summary>
    public double AnchorX { get; }

    /// <summary>Where its feet stand in a frame (pixels from the top).</summary>
    public double AnchorY { get; }

    /// <summary>How tall it stands in the frames' pixels (a character's size on the screen is this height).</summary>
    public double Tall { get; }

    /// <summary>The top of its head in a frame (where effects go): its own, or the top of its height above its feet.</summary>
    public (double X, double Y) Head { get; }

    /// <summary>The way its drawing faces: 1 right, -1 left.</summary>
    public int Faces { get; }

    /// <summary>Its clips, one for each thing it does.</summary>
    public IReadOnlyList<CharacterClip> Clips { get; }

    /// <summary>The things it does.</summary>
    public IEnumerable<string> Actions => Clips.Select(c => c.Action);

    /// <summary>
    /// The clip it plays for an action: that one, or the closest it has (dancing plays its play, running its walk,
    /// sitting or sleeping its idle, …), idle when nothing is closer.
    /// </summary>
    public CharacterClip ClipFor(string action)
    {
        foreach (var name in CloseTo(action))
        {
            if (Clips.FirstOrDefault(c => c.Action == name) is { } clip)
            {
                return clip;
            }
        }

        return Clips.First(c => c.Action == "idle");
    }

    /// <summary>An action and what comes closest to it, in order (idle last): what a character without it does instead.</summary>
    public static IReadOnlyList<string> CloseTo(string action) => action switch
        {
            "dance" => ["dance", "play", "jump", "idle"],
            "play" => ["play", "dance", "idle"],
            "run" => ["run", "walk", "idle"],
            "walk" => ["walk", "run", "idle"],
            "sleep" => ["sleep", "sit", "idle"],
            "sit" => ["sit", "sleep", "idle"],
            "jump" => ["jump", "play", "idle"],
            // The emotions (made by the generator: CharacterAnimations.Emotions) until a character has them: the closest it has.
            "worried" => ["worried", "sit", "idle"],
            "panic" => ["panic", "jump", "idle"],
            "tired" => ["tired", "sit", "sleep", "idle"],
            "cheer" => ["cheer", "dance", "play", "jump", "idle"],
            _ => [action, "idle"],
        };

    /// <summary>Reads a character's file; null when the bytes are not one (or not a sound one).</summary>
    public static CharacterPack? Read(ReadOnlySpan<byte> bytes) => Read(bytes, a => OverlayCharacters.IsAction(a) || a is "held" or "land", needsIdle: true);

    /// <summary>Reads a new animation's file for a character (its clips alone, no idle: <c>animate.py</c>); null when it is not a sound one.</summary>
    public static CharacterPack? ReadExtra(ReadOnlySpan<byte> bytes) => Read(bytes, OverlayCharacters.IsAction, needsIdle: false);

    /// <summary>Reads the effects' file: a clip per effect (<see cref="OverlayReactions.Effects"/>); null when it is not a sound one.</summary>
    public static CharacterPack? ReadEffects(ReadOnlySpan<byte> bytes) => Read(bytes, OverlayReactions.Effects.ContainsKey, needsIdle: false);

    /// <summary>Reads the props' file (a clip per prop, as the effects' file); null when it is not a sound one.</summary>
    public static CharacterPack? ReadProps(ReadOnlySpan<byte> bytes) => Read(bytes, OverlayReactions.Props.ContainsKey, needsIdle: false);

    private static CharacterPack? Read(ReadOnlySpan<byte> bytes, Func<string, bool> isAction, bool needsIdle)
    {
        if (bytes.Length < 10 || bytes.Length > MaxBytes || !bytes[..4].SequenceEqual("PCDC"u8) || bytes[4] != 1)
        {
            return null;
        }

        var manifestLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..10]);
        if (manifestLength is < 2 or > ManifestLimit || 10 + manifestLength > bytes.Length)
        {
            return null;
        }

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(bytes.Slice(10, (int)manifestLength), Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is not { Name: { } name, Anchor: [var anchorX, var anchorY], Clips: { Count: > 0 } manifestClips }
            || name.Trim().Length is < 1 or > 60
            || manifest.Width is < 1 or > MaxSide || manifest.Height is < 1 or > MaxSide
            || !double.IsFinite(anchorX) || !double.IsFinite(anchorY) || anchorX < 0 || anchorX > manifest.Width || anchorY < 0 || anchorY > manifest.Height
            || !double.IsFinite(manifest.Tall) || manifest.Tall < 8 || manifest.Tall > manifest.Height
            || manifest.Faces is not (1 or -1)
            || manifestClips.Count > 32
            || manifestClips.Any(c => c.Action is null || !isAction(c.Action) || c.Fps is < 1 or > 60 || c.Frames is not { Count: > 0 and <= MaxFrames } || c.At is not (null or "head" or "feet"))
            || manifestClips.Select(c => c.Action).Distinct(StringComparer.Ordinal).Count() != manifestClips.Count
            || (needsIdle && manifestClips.All(c => c.Action != "idle"))
            || manifest.Head is { } h && (h.Length != 2 || !double.IsFinite(h[0]) || !double.IsFinite(h[1]) || h[0] < 0 || h[0] > manifest.Width || h[1] < 0 || h[1] > manifest.Height)
            || manifestClips.Sum(c => c.Frames!.Count) > MaxTotalFrames)
        {
            return null;
        }

        // Every frame a PNG of the character's size, and nothing after the last one.
        long at = 10 + manifestLength;
        var clips = new List<CharacterClip>(manifestClips.Count);
        foreach (var clip in manifestClips)
        {
            var frames = new List<(int Offset, int Length)>(clip.Frames!.Count);
            foreach (var length in clip.Frames!)
            {
                if (length < 33 || at + length > bytes.Length || !IsPng(bytes.Slice((int)at, length), manifest.Width, manifest.Height))
                {
                    return null;
                }

                frames.Add(((int)at, length));
                at += length;
            }

            if (clip.Loop is < 0 || clip.Loop >= frames.Count || clip.End < (clip.Loop ?? 0) || clip.End >= frames.Count)
            {
                return null;
            }

            clips.Add(new CharacterClip(clip.Action!, clip.Fps, frames) { At = clip.At ?? "feet", Mirror = clip.Mirror ?? true, Loop = clip.Loop, End = clip.End, Bounce = clip.Bounce == true });
        }

        return at == bytes.Length
            ? new CharacterPack(name.Trim(), manifest.Width, manifest.Height, anchorX, anchorY, manifest.Tall, manifest.Faces, clips, manifest.Head is [var hx, var hy] ? (hx, hy) : null)
            : null;
    }

    /// <summary>Writes a character from its clips' PNG frames (tests and tools; the editor writes its own).</summary>
    public static byte[] Write(string name, int width, int height, double anchorX, double anchorY, double tall, int faces, IReadOnlyList<(string Action, int Fps, IReadOnlyList<byte[]> Frames)> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(
            new Manifest(name, width, height, [anchorX, anchorY], null, tall, faces, [.. clips.Select(c => new ManifestClip(c.Action, c.Fps, [.. c.Frames.Select(f => f.Length)], null, null))]),
            Json);
        using var output = new MemoryStream();
        output.Write("PCDC"u8);
        output.WriteByte(1);
        output.WriteByte(0);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)manifest.Length);
        output.Write(length);
        output.Write(manifest);
        foreach (var frame in clips.SelectMany(c => c.Frames))
        {
            output.Write(frame);
        }

        return output.ToArray();
    }

    /// <summary>Whether the bytes are a PNG of a size (its header).</summary>
    private static bool IsPng(ReadOnlySpan<byte> bytes, int width, int height)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        return bytes.Length > 24
            && bytes[..8].SequenceEqual(signature)
            && bytes[12..16].SequenceEqual("IHDR"u8)
            && BinaryPrimitives.ReadInt32BigEndian(bytes[16..20]) == width
            && BinaryPrimitives.ReadInt32BigEndian(bytes[20..24]) == height;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Manifest(string? Name, int Width, int Height, double[]? Anchor, double[]? Head, double Tall, int Faces, List<ManifestClip>? Clips);

    private sealed record ManifestClip(string? Action, int Fps, List<int>? Frames, string? At, bool? Mirror, int? Loop = null, int? End = null, bool? Bounce = null);
}

/// <summary>One thing a character does: its frames (where each PNG is in the file), played in a loop at a rate.</summary>
public sealed record CharacterClip(string Action, int Fps, IReadOnlyList<(int Offset, int Length)> Frames)
{
    /// <summary>An effect's: what it is attached to, "head" (the top of the character's head) or "feet".</summary>
    public string At { get; init; } = "feet";

    /// <summary>An effect's: whether it turns with the character (false: drawn as it is, whichever way it faces).</summary>
    public bool Mirror { get; init; } = true;

    /// <summary>
    /// The first frame repeated (null: all of them): the frames before it play once (sitting down), then the rest
    /// repeats (sitting).
    /// </summary>
    public int? Loop { get; init; }

    /// <summary>The last frame repeated (null: the clip's last).</summary>
    public int? End { get; init; }

    /// <summary>The repeated frames play forwards then back (a clip whose end does not lead back to its start).</summary>
    public bool Bounce { get; init; }

    /// <summary>How long one loop lasts, in seconds.</summary>
    public double Duration => (double)Frames.Count / Fps;

    /// <summary>The frame shown `seconds` into the clip (it loops).</summary>
    public int FrameAt(double seconds)
    {
        var n = Frames.Count;
        if (n == 0)
        {
            return 0;
        }

        var i = (long)Math.Floor(seconds * Fps);
        if (Loop is null && !Bounce)
        {
            return (int)(((i % n) + n) % n);
        }

        // Once through the opening, then the repeated part (forwards, or forwards and back).
        var start = Math.Clamp(Loop ?? 0, 0, n - 1);
        var end = Math.Clamp(End ?? (n - 1), start, n - 1);
        if (i >= 0 && i < start)
        {
            return (int)i;
        }

        var length = end - start + 1;
        var j = i - start;
        if (!Bounce || length < 2)
        {
            return start + (int)(((j % length) + length) % length);
        }

        var period = (2 * length) - 2;
        var p = (int)(((j % period) + period) % period);
        return start + (p < length ? p : period - p);
    }
}
