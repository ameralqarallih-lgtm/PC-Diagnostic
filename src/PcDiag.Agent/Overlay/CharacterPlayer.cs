using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// A character as the recorder plays it (<see cref="CharacterPack"/>): its frames kept as PNG, all decoded in the
/// background at the size it is shown (and mirrored when it faces the other way) as soon as that size is first asked
/// for (<see cref="Prepare"/>), so even its first turn plays smoothly; a frame not ready yet is decoded when shown. What
/// is kept stays under a budget: past it, the frames used longest ago are let go.
/// </summary>
internal sealed class CharacterPlayer : IDisposable
{
    /// <summary>Decoded frames kept, at most, in bytes (smaller in tests).</summary>
    internal long Budget { get; set; } = 256L * 1024 * 1024;

    private readonly byte[] _bytes;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Action, int Frame, int Height, bool Mirrored), (Bitmap Bitmap, long Used)> _kept = [];

    /// <summary>
    /// Its body in each frame decoded, in that frame's pixels (kept when the frame is let go): the top of its head and
    /// its middle there (Top -1: none found), and its left and right edges.
    /// </summary>
    private readonly Dictionary<(string Action, int Frame, int Height, bool Mirrored), (int Top, float Middle, int Left, int Right)> _bodies = [];
    private readonly HashSet<int> _prepared = [];
    private long _keptBytes;
    private long _uses;

    private static readonly bool[] BothWays = [false, true];
    private static readonly bool[] OneWay = [false];

    private CharacterPlayer(byte[] bytes, CharacterPack pack)
    {
        _bytes = bytes;
        Pack = pack;
    }

    /// <summary>The characters by id (the service finds a shipped one or reads an imported one's file); none without it.</summary>
    public static Func<string, CharacterPlayer?>? Find { get; set; }

    /// <summary>Counts each time the characters are read again (new animations made): copies kept elsewhere are made again.</summary>
    public static volatile int Generation;

    /// <summary>The character played.</summary>
    public CharacterPack Pack { get; }

    /// <summary>A player of the same character with frames of its own (for another thread: a frame is never drawn on two at once).</summary>
    public CharacterPlayer Copy() => new(_bytes, Pack) { Extras = Extras.ToDictionary(e => e.Key, e => e.Value.Copy(), StringComparer.Ordinal) };

    /// <summary>
    /// Its new animations' clips (made by the generator: an extra file each, at its own height), by action: played for
    /// an action its own file does not have.
    /// </summary>
    public IReadOnlyDictionary<string, CharacterPlayer> Extras { get; private set; } = new Dictionary<string, CharacterPlayer>(StringComparer.Ordinal);

    /// <summary>Plays these new animations' clips with it too (their files: each a clip alone, read with <see cref="CharacterPack.ReadExtra"/>).</summary>
    public void Attach(IEnumerable<byte[]> extras)
    {
        var attached = new Dictionary<string, CharacterPlayer>(Extras, StringComparer.Ordinal);
        foreach (var bytes in extras)
        {
            if (CharacterPack.ReadExtra(bytes) is { } pack)
            {
                foreach (var clip in pack.Clips.Where(c => !Pack.Clips.Any(own => own.Action == c.Action)))
                {
                    attached[clip.Action] = new CharacterPlayer(bytes, pack);
                }
            }
        }

        Extras = attached;
    }

    /// <summary>Reads a character's file; null when it is not one.</summary>
    public static CharacterPlayer? Read(byte[] bytes) =>
        CharacterPack.Read(bytes) is { } pack ? new CharacterPlayer(bytes, pack) : null;

    /// <summary>Reads the effects' file (a clip per effect); null when it is not one.</summary>
    public static CharacterPlayer? ReadEffects(byte[] bytes) =>
        CharacterPack.ReadEffects(bytes) is { } pack ? new CharacterPlayer(bytes, pack) : null;

    /// <summary>Reads the props' file (a clip per prop); null when it is not one.</summary>
    public static CharacterPlayer? ReadProps(byte[] bytes) =>
        CharacterPack.ReadProps(bytes) is { } pack ? new CharacterPlayer(bytes, pack) : null;

    /// <summary>
    /// Where the top of its head is from its feet in the frame shown for an action `seconds` into it (as decoded: it
    /// sits, jumps and bends, and what is on its head goes with it), <paramref name="size"/> pixels tall facing a way;
    /// its usual head (<see cref="HeadFromFeet"/>) until that frame is decoded.
    /// </summary>
    public PointF HeadAt(string action, double seconds, int facing, float size)
    {
        action = Plays(action);
        if (!Pack.Clips.Any(c => c.Action == action) && Extras.TryGetValue(action, out var extra))
        {
            return extra.HeadAt(action, seconds, facing, size);
        }

        var usual = HeadFromFeet(size, facing);
        var clip = Pack.ClipFor(action);
        var (height, mirrored) = (HeightAt(size), facing != Pack.Faces);
        (int Top, float Middle, int Left, int Right) body;
        lock (_lock)
        {
            if (!_bodies.TryGetValue((clip.Action, clip.FrameAt(seconds), height, mirrored), out body) || body.Top < 0)
            {
                return usual;
            }
        }

        var (anchorX, anchorY) = AnchorAt(height, mirrored);
        return new PointF(body.Middle - anchorX, body.Top - anchorY);
    }

    /// <summary>
    /// How far its body reaches from its feet the way it faces, <paramref name="size"/> pixels tall (as it stands: its
    /// first idle frame, once decoded; a third of its size until then).
    /// </summary>
    public float ReachAt(int facing, float size)
    {
        var (height, mirrored) = (HeightAt(size), facing != Pack.Faces);
        (int Top, float Middle, int Left, int Right) body;
        lock (_lock)
        {
            if (!_bodies.TryGetValue((Pack.ClipFor("idle").Action, 0, height, mirrored), out body) || body.Top < 0)
            {
                return size * 0.3f;
            }
        }

        var (anchorX, _) = AnchorAt(height, mirrored);
        return facing > 0 ? body.Right - anchorX : anchorX - body.Left;
    }

    private int HeightAt(float size) => Math.Max(1, (int)Math.Round(Pack.Height * size / Pack.Tall));

    /// <summary>Where its feet are in its frames <paramref name="height"/> pixels high (mirrored or not).</summary>
    private (float X, float Y) AnchorAt(int height, bool mirrored)
    {
        var width = Math.Max(1, (int)Math.Round(Pack.Width * height / (double)Pack.Height));
        var x = mirrored ? Pack.Width - Pack.AnchorX : Pack.AnchorX;
        return ((float)(x * width / Pack.Width), (float)(Pack.AnchorY * height / Pack.Height));
    }

    /// <summary>
    /// Its body in a frame decoded (as the editor's tops.ts): the top of its head, the first row with a solid stretch as
    /// wide as a head (ears, an antenna, hair spikes passed over), and that stretch's middle; and its left and right edges.
    /// </summary>
    private (int Top, float Middle, int Left, int Right) Measure(Bitmap bitmap)
    {
        // A head is at least a quarter of its height across (ears, an antenna, hair spikes are narrower).
        var wide = Math.Max(2, (int)(0.24 * Pack.Tall * bitmap.Height / Pack.Height));
        var (top, middle, left, right) = (-1, 0f, bitmap.Width, -1);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                var run = 0;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (row[(x * 4) + 3] <= 96)
                    {
                        run = 0;
                        continue;
                    }

                    (left, right) = (Math.Min(left, x), Math.Max(right, x));
                    if (++run == wide && top < 0)
                    {
                        // The stretch's middle: on to its end.
                        var end = x;
                        while (end + 1 < bitmap.Width && row[((end + 1) * 4) + 3] > 96)
                        {
                            end++;
                        }

                        (top, middle) = (y, (x - wide + 1 + end) / 2f);
                    }
                }
            }

            return (top, middle, left, right);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Where the top of its head is from its feet, <paramref name="size"/> pixels tall, facing a way.</summary>
    public PointF HeadFromFeet(float size, int facing)
    {
        var k = size / Pack.Tall;
        var dx = (Pack.Head.X - Pack.AnchorX) * k;
        return new PointF((float)(facing != Pack.Faces ? -dx : dx), (float)((Pack.Head.Y - Pack.AnchorY) * k));
    }

    /// <summary>
    /// An effect's frame `seconds` into it for a character <paramref name="size"/> pixels tall facing a way, where the
    /// point it is attached to is in it, and what that is (head or feet); null when there is no such effect.
    /// </summary>
    public (Bitmap Bitmap, PointF Anchor, string At)? Effect(string effect, double seconds, int facing, float size)
    {
        if (Pack.Clips.FirstOrDefault(c => c.Action == effect) is not { } clip)
        {
            return null;
        }

        return FrameOf(clip, seconds, clip.Mirror && facing != Pack.Faces, size) is var (bitmap, anchor) ? (bitmap, anchor, clip.At) : null;
    }

    /// <summary>
    /// The frame shown for an action `seconds` into it, the character <paramref name="size"/> pixels tall, facing right
    /// (1) or left (-1), and where its feet are in that frame; null when the frame cannot be decoded. The bitmap is
    /// the player's: drawn from, never kept or changed.
    /// </summary>
    public (Bitmap Bitmap, PointF Anchor)? Frame(string action, double seconds, int facing, float size)
    {
        action = Plays(action);
        return !Pack.Clips.Any(c => c.Action == action) && Extras.TryGetValue(action, out var extra)
            ? extra.FrameOf(extra.Pack.Clips.First(c => c.Action == action), seconds, facing != extra.Pack.Faces, size)
            : FrameOf(Pack.ClipFor(action), seconds, facing != Pack.Faces, size);
    }

    /// <summary>Whether it has a clip of its own for an action (in its file, or a new animation's).</summary>
    public bool Has(string action) => Pack.Clips.Any(c => c.Action == action) || Extras.ContainsKey(action);

    /// <summary>What it plays for an action: that one, or the closest it has, its new animations' clips counted (idle when nothing is closer).</summary>
    public string Plays(string action) => CharacterPack.CloseTo(action).FirstOrDefault(Has) ?? "idle";

    private (Bitmap Bitmap, PointF Anchor)? FrameOf(CharacterClip clip, double seconds, bool mirrored, float size)
    {
        var index = clip.FrameAt(seconds);
        var k = size / Pack.Tall;
        var width = Math.Max(1, (int)Math.Round(Pack.Width * k));
        var height = Math.Max(1, (int)Math.Round(Pack.Height * k));
        var x = mirrored ? Pack.Width - Pack.AnchorX : Pack.AnchorX;
        var anchor = new PointF((float)(x * width / Pack.Width), (float)(Pack.AnchorY * height / Pack.Height));
        var key = (clip.Action, index, height, mirrored);
        Prepare(size);
        lock (_lock)
        {
            if (_kept.TryGetValue(key, out var kept))
            {
                _kept[key] = (kept.Bitmap, ++_uses);
                return (kept.Bitmap, anchor);
            }
        }

        var bitmap = Decode(clip.Frames[index], width, height, mirrored);
        if (bitmap is null)
        {
            return null;
        }

        var body = Measure(bitmap);
        lock (_lock)
        {
            _bodies[key] = body;
            if (_kept.TryGetValue(key, out var raced))
            {
                bitmap.Dispose();
                return (raced.Bitmap, anchor);
            }

            // Shown from this thread only: the frames used longest ago are let go here (never while being drawn).
            var bytes = (long)width * height * 4;
            foreach (var (old, value) in _kept.OrderBy(k => k.Value.Used).ToList())
            {
                if (_keptBytes + bytes <= Budget)
                {
                    break;
                }

                _keptBytes -= (long)value.Bitmap.Width * value.Bitmap.Height * 4;
                _kept.Remove(old);
                value.Bitmap.Dispose();
            }

            _kept[key] = (bitmap, ++_uses);
            _keptBytes += bytes;
        }

        return (bitmap, anchor);
    }

    /// <summary>
    /// Decodes every frame of every clip, both ways it faces, for the character <paramref name="size"/> pixels tall, in
    /// the background (once per size, while the budget allows).
    /// </summary>
    public void Prepare(float size)
    {
        var k = size / Pack.Tall;
        var width = Math.Max(1, (int)Math.Round(Pack.Width * k));
        var height = Math.Max(1, (int)Math.Round(Pack.Height * k));
        lock (_lock)
        {
            if (!_prepared.Add(height))
            {
                return;
            }
        }

        var work = Pack.Clips.SelectMany(c => Enumerable.Range(0, c.Frames.Count).SelectMany(i => (c.Mirror ? BothWays : OneWay).Select(m => (Clip: c, Index: i, Mirrored: m)))).ToList();
        _ = Task.Run(() =>
        {
            foreach (var (clip, index, mirrored) in work)
            {
                var key = (clip.Action, index, height, mirrored);
                lock (_lock)
                {
                    if (_disposed || _kept.ContainsKey(key))
                    {
                        continue;
                    }

                    if (_keptBytes + ((long)width * height * 4) > Budget)
                    {
                        return;
                    }
                }

                if (Decode(clip.Frames[index], width, height, mirrored) is not { } bitmap)
                {
                    continue;
                }

                var body = Measure(bitmap);
                lock (_lock)
                {
                    _bodies[key] = body;
                    if (_disposed || !_kept.TryAdd(key, (bitmap, 0)))
                    {
                        bitmap.Dispose();
                        continue;
                    }

                    _keptBytes += (long)width * height * 4;
                }
            }
        });
    }

    /// <summary>
    /// Draws the character with its feet at <paramref name="feet"/>, <paramref name="size"/> pixels tall, at an opacity
    /// (0-1): a USB screen's picture.
    /// </summary>
    /// <param name="tall">How tall it is drawn against its size (breathing), its feet where they are.</param>
    public void Draw(Graphics g, string action, double seconds, int facing, float size, PointF feet, float opacity = 1, float tall = 1)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (Frame(action, seconds, facing, size) is not var (bitmap, anchor))
        {
            return;
        }

        var target = new Rectangle((int)Math.Round(feet.X - anchor.X), (int)Math.Round(feet.Y - (anchor.Y * tall)), bitmap.Width, (int)Math.Round(bitmap.Height * tall));
        if (opacity >= 1)
        {
            g.DrawImage(bitmap, target);
            return;
        }

        using var faded = new ImageAttributes();
        faded.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        g.DrawImage(bitmap, target, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel, faded);
    }

    private Bitmap? Decode((int Offset, int Length) frame, int width, int height, bool mirrored)
    {
        try
        {
            using var stream = new MemoryStream(_bytes, frame.Offset, frame.Length, writable: false);
            using var image = Image.FromStream(stream);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bitmap);
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            if (mirrored)
            {
                g.TranslateTransform(width, 0);
                g.ScaleTransform(-1, 1);
            }

            // Edges taken as they are (not blended with the far side's pixels).
            using var edges = new ImageAttributes();
            edges.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(image, new Rectangle(0, 0, width, height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, edges);
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (var (bitmap, _) in _kept.Values)
            {
                bitmap.Dispose();
            }

            _kept.Clear();
            _keptBytes = 0;
        }

        foreach (var extra in Extras.Values)
        {
            extra.Dispose();
        }
    }
}
