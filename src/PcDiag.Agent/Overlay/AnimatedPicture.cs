using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Intrinsics;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// An animated background picture (<see cref="OverlayAnimation"/>) as the recorder plays it: its frames kept as JPEG
/// and decoded only when shown. When all of them decoded fit in a small budget they are kept (a short, small loop costs
/// no decoding after its first turn); otherwise only the two on show are. Between two frames it shows a blend of them,
/// so a loop made at 12 frames a second moves smoothly at the 30 a second a USB screen or a monitor is sent.
/// </summary>
internal sealed class AnimatedPicture : IDisposable
{
    /// <summary>Decoded frames kept, at most, in bytes.</summary>
    private const long Budget = 48L * 1024 * 1024;

    private readonly byte[] _bytes;
    private readonly OverlayAnimation _animation;
    private readonly Bitmap?[] _decoded;
    private readonly bool _keepAll;
    private readonly Lock _lock = new();
    // Without the budget: the frames last decoded (the one on show and the next), by index.
    private readonly List<(int Index, Bitmap? Frame)> _recent = [];
    private Bitmap? _blend;
    // What the blend shows (frame and weight): asked again for the same, it is given as it is. A refresh and a motion
    // frame of a USB screen, or two panels with the same picture, often ask for the same moment.
    private (int Index, int Weight)? _blendOf;

    private AnimatedPicture(byte[] bytes, OverlayAnimation animation)
    {
        _bytes = bytes;
        _animation = animation;
        _decoded = new Bitmap?[animation.Frames.Count];
        _keepAll = (long)animation.Width * animation.Height * 4 * animation.Frames.Count <= Budget;
    }

    public int Width => _animation.Width;

    public int Height => _animation.Height;

    public int FrameCount => _animation.Frames.Count;

    /// <summary>Reads an animation's file; null when it is not one.</summary>
    public static AnimatedPicture? Read(byte[] bytes) =>
        OverlayAnimation.Read(bytes) is { } animation ? new AnimatedPicture(bytes, animation) : null;

    /// <summary>
    /// The picture shown at a moment (seconds on the motion clock): the frame then, blended toward the next by how far
    /// the moment is between them; null when it cannot be decoded. The picture is reused by the next call.
    /// </summary>
    public Image? FrameAt(double seconds)
    {
        var position = _animation.PositionAt(seconds);
        var index = (int)position;
        // A twentieth of the way or less: the frame itself (no blend for so little).
        var weight = (int)Math.Round((position - index) * 256);
        lock (_lock)
        {
            var frame = Frame(index);
            if (frame is null || FrameCount < 2 || weight < 13)
            {
                return frame;
            }

            var next = Frame((index + 1) % FrameCount);
            if (next is null || next.Size != frame.Size)
            {
                return frame;
            }

            if (_blend is null || _blend.Size != frame.Size)
            {
                _blend?.Dispose();
                _blend = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppPArgb);
                _blendOf = null;
            }

            if (_blendOf != (index, weight))
            {
                Blend(frame, next, weight, _blend);
                _blendOf = (index, weight);
            }

            return _blend;
        }
    }

    /// <summary>A frame decoded: kept when all fit the budget, otherwise among the two last decoded.</summary>
    private Bitmap? Frame(int index)
    {
        if (_keepAll)
        {
            return _decoded[index] ??= Decode(index);
        }

        foreach (var (at, kept) in _recent)
        {
            if (at == index)
            {
                return kept;
            }
        }

        var frame = Decode(index);
        _recent.Add((index, frame));
        if (_recent.Count > 2)
        {
            _recent[0].Frame?.Dispose();
            _recent.RemoveAt(0);
        }

        return frame;
    }

    /// <summary>
    /// <paramref name="to"/> = <paramref name="a"/> moved <paramref name="weight"/>/256 of the way to <paramref name="b"/>,
    /// pixel by pixel (two channels at a time in one 32-bit sum, 8 pixels at a time where the processor can: the same
    /// sums either way).
    /// </summary>
    internal static void Blend(Bitmap a, Bitmap b, int weight, Bitmap to)
    {
        var rect = new Rectangle(0, 0, a.Width, a.Height);
        var da = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var db = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var dt = to.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var keep = (uint)(256 - weight);
            var take = (uint)weight;
            unsafe
            {
                for (var y = 0; y < rect.Height; y++)
                {
                    var pa = (uint*)((byte*)da.Scan0 + ((long)y * da.Stride));
                    var pb = (uint*)((byte*)db.Scan0 + ((long)y * db.Stride));
                    var pt = (uint*)((byte*)dt.Scan0 + ((long)y * dt.Stride));
                    var x = BlendVectors(pa, pb, pt, rect.Width, keep, take);
                    for (; x < rect.Width; x++)
                    {
                        uint ca = pa[x], cb = pb[x];
                        var rb = ((((ca & 0x00FF00FFu) * keep) + ((cb & 0x00FF00FFu) * take)) >> 8) & 0x00FF00FFu;
                        var ag = ((((ca >> 8) & 0x00FF00FFu) * keep) + (((cb >> 8) & 0x00FF00FFu) * take)) & 0xFF00FF00u;
                        pt[x] = rb | ag;
                    }
                }
            }
        }
        finally
        {
            a.UnlockBits(da);
            b.UnlockBits(db);
            to.UnlockBits(dt);
        }
    }

    /// <summary>The blend of a row 8 (or 4) pixels at a time; how many pixels it did (the rest are done one by one).</summary>
    private static unsafe int BlendVectors(uint* pa, uint* pb, uint* pt, int width, uint keep, uint take)
    {
        var x = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var low = Vector256.Create(0x00FF00FFu);
            var high = Vector256.Create(0xFF00FF00u);
            var k = Vector256.Create(keep);
            var t = Vector256.Create(take);
            for (; x <= width - 8; x += 8)
            {
                var ca = Vector256.Load(pa + x);
                var cb = Vector256.Load(pb + x);
                var rb = Vector256.ShiftRightLogical(((ca & low) * k) + ((cb & low) * t), 8) & low;
                var ag = ((Vector256.ShiftRightLogical(ca, 8) & low) * k) + ((Vector256.ShiftRightLogical(cb, 8) & low) * t);
                (rb | (ag & high)).Store(pt + x);
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            var lo = Vector128.Create(0x00FF00FFu);
            var hi = Vector128.Create(0xFF00FF00u);
            var k = Vector128.Create(keep);
            var t = Vector128.Create(take);
            for (; x <= width - 4; x += 4)
            {
                var ca = Vector128.Load(pa + x);
                var cb = Vector128.Load(pb + x);
                var rb = Vector128.ShiftRightLogical(((ca & lo) * k) + ((cb & lo) * t), 8) & lo;
                var ag = ((Vector128.ShiftRightLogical(ca, 8) & lo) * k) + ((Vector128.ShiftRightLogical(cb, 8) & lo) * t);
                (rb | (ag & hi)).Store(pt + x);
            }
        }

        return x;
    }

    private Bitmap? Decode(int index)
    {
        var (offset, length) = _animation.Frames[index];
        try
        {
            using var stream = new MemoryStream(_bytes, offset, length, writable: false);
            using var image = Image.FromStream(stream);
            // Premultiplied, as the layers are: drawn and blended without converting each time.
            var frame = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(frame))
            {
                g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height));
            }

            return frame;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var frame in _decoded)
            {
                frame?.Dispose();
            }

            foreach (var (_, frame) in _recent)
            {
                frame?.Dispose();
            }

            _recent.Clear();
            _blend?.Dispose();
            _blend = null;
            _blendOf = null;
        }
    }
}
