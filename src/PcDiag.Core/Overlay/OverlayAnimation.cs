using System.Buffers.Binary;

namespace PcDiag.Core.Overlay;

/// <summary>
/// An animated picture for a panel's background (the ".anim" pictures): JPEG frames played in a loop at a fixed rate.
/// The editor makes them (a generated scene baked, or an uploaded GIF, WebP or APNG converted); the recorder plays
/// them, decoding a frame only when it is shown. Layout, little-endian:
/// <code>
///  0  "PCDA"
///  4  version (1)        5  reserved (0)
///  6  frames (1-300)     8  frames a second (1-60)
/// 10  width             12  height            14  reserved (0)
/// 16  each frame's length (4 bytes each), then the frames (JPEG) one after another
/// </code>
/// </summary>
public sealed class OverlayAnimation
{
    /// <summary>The most frames an animation may have.</summary>
    public const int MaxFrames = 300;

    /// <summary>The largest an animation's side may be, in pixels.</summary>
    public const int MaxSide = 4096;

    private OverlayAnimation(int fps, int width, int height, IReadOnlyList<(int Offset, int Length)> frames)
    {
        Fps = fps;
        Width = width;
        Height = height;
        Frames = frames;
    }

    /// <summary>Frames a second.</summary>
    public int Fps { get; }

    /// <summary>Its width in pixels (every frame's).</summary>
    public int Width { get; }

    /// <summary>Its height in pixels (every frame's).</summary>
    public int Height { get; }

    /// <summary>Where each frame's JPEG is in the file.</summary>
    public IReadOnlyList<(int Offset, int Length)> Frames { get; }

    /// <summary>How long one loop lasts.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames.Count / Fps);

    /// <summary>The frame shown at a moment (in seconds on any steady clock): the loop repeats.</summary>
    public int FrameAt(double seconds) => Frames.Count == 0 ? 0 : (int)(((long)Math.Floor(Math.Max(0, seconds) * Fps)) % Frames.Count);

    /// <summary>Where the loop is at a moment, in frames (0 to the frame count; the whole part is <see cref="FrameAt"/>).</summary>
    public double PositionAt(double seconds)
    {
        if (Frames.Count == 0)
        {
            return 0;
        }

        var frames = Math.Max(0, seconds) * Fps;
        var position = frames - (Math.Floor(frames / Frames.Count) * Frames.Count);
        return position >= Frames.Count ? 0 : position;
    }

    /// <summary>Reads an animation's header and frame table; null when the bytes are not one (or not a sound one).</summary>
    public static OverlayAnimation? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes[..4].SequenceEqual("PCDA"u8) || bytes[4] != 1)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..8]);
        int fps = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..10]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..12]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..14]);
        if (count is < 1 or > MaxFrames || fps is < 1 or > 60 || width is < 1 or > MaxSide || height is < 1 or > MaxSide)
        {
            return null;
        }

        var table = 16 + (4 * count);
        if (bytes.Length < table)
        {
            return null;
        }

        var frames = new List<(int, int)>(count);
        long at = table;
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(16 + (4 * i))..]);
            if (length < 4 || at + length > bytes.Length)
            {
                return null;
            }

            // Each frame is a JPEG (starts with its marker).
            if (bytes[(int)at] != 0xFF || bytes[(int)at + 1] != 0xD8)
            {
                return null;
            }

            frames.Add(((int)at, (int)length));
            at += length;
        }

        return at == bytes.Length ? new OverlayAnimation(fps, width, height, frames) : null;
    }

    /// <summary>Writes an animation from its JPEG frames (the editor's format; used by tests and tools).</summary>
    public static byte[] Write(int fps, int width, int height, IReadOnlyList<byte[]> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var table = 16 + (4 * frames.Count);
        var bytes = new byte[table + frames.Sum(f => f.Length)];
        "PCDA"u8.CopyTo(bytes);
        bytes[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)frames.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)fps);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)height);
        var at = table;
        for (var i = 0; i < frames.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + (4 * i)), (uint)frames[i].Length);
            frames[i].CopyTo(bytes, at);
            at += frames[i].Length;
        }

        return bytes;
    }
}
