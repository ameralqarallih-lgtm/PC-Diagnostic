using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PcDiag.Screens;

namespace PcDiag.Agent.Screens;

/// <summary>Pictures for USB screens: turned to the panel's orientation and encoded as the panel takes them.</summary>
internal static class ScreenFrames
{
    private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// The size a USB screen's layout has: the panel's own (its native orientation) turned by the rotation (a portrait
    /// 480×1920 panel mounted as a bar, turned 90°, is laid out 1920×480).
    /// </summary>
    public static Size LayoutSize(ScreenModel model, int rotation) => rotation is 90 or 270 ? new Size(model.Height, model.Width) : new Size(model.Width, model.Height);

    /// <summary>The rotation a screen's picture is turned by: its own setting, or its model's usual one.</summary>
    public static int RotationOf(ScreenModel model, int setting) => setting is 0 or 90 or 180 or 270 ? setting : model.Rotation;

    /// <summary>
    /// A layout-sized picture turned onto the panel (clockwise by the rotation: what reads upright on a panel turned
    /// the other way in its frame) and encoded; null when it cannot be made small enough for the panel.
    /// </summary>
    public static byte[]? Make(Bitmap layout, ScreenModel model, int rotation)
    {
        ArgumentNullException.ThrowIfNull(layout);
        using var panel = (Bitmap)layout.Clone();
        return MakeFrom(panel, model, rotation);
    }

    /// <summary>As <see cref="Make"/>, turning <paramref name="panel"/> itself (a copy the caller owns).</summary>
    public static byte[]? MakeFrom(Bitmap panel, ScreenModel model, int rotation, JpegQuality? quality = null)
    {
        ArgumentNullException.ThrowIfNull(panel);
        ArgumentNullException.ThrowIfNull(model);
        if (rotation is 90 or 180 or 270)
        {
            panel.RotateFlip(rotation switch
            {
                90 => RotateFlipType.Rotate90FlipNone,
                180 => RotateFlipType.Rotate180FlipNone,
                _ => RotateFlipType.Rotate270FlipNone,
            });
        }

        if (panel.Width != model.Width || panel.Height != model.Height)
        {
            return null;
        }

        var frame = Encode(panel, model.Format, model.MaxFrameBytes, quality);
        return model.MaxFrameBytes > 0 && frame.Length > model.MaxFrameBytes ? null : frame;
    }

    /// <summary>
    /// A 32-bit picture turned clockwise by 90 or 270 degrees: the same pixels as GDI+'s RotateFlip, moved 8 x 8 at a
    /// time, into <paramref name="into"/> when it has the turned size and the format (a picture given back after
    /// encoding: making a 1920 x 480 one took 0.9 ms), otherwise into a new one. Null when it cannot be done so
    /// (another rotation or format, a side that is not a multiple of 8, no AVX2): the caller turns it with RotateFlip.
    /// </summary>
    public static unsafe Bitmap? Turned(Bitmap source, int rotation, Bitmap? into = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var format = source.PixelFormat;
        int w = source.Width, h = source.Height;
        if (rotation is not (90 or 270) || !Avx2.IsSupported || w % 8 != 0 || h % 8 != 0
            || format is not (PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format32bppRgb))
        {
            return null;
        }

        var turned = into is not null && into.Width == h && into.Height == w && into.PixelFormat == format ? into : new Bitmap(h, w, format);
        turned.SetResolution(source.HorizontalResolution, source.VerticalResolution);
        // Both in their own format: the pixels are moved as they are, not converted.
        var from = source.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, format);
        var to = turned.LockBits(new Rectangle(0, 0, h, w), ImageLockMode.WriteOnly, format);
        try
        {
            var src = (byte*)from.Scan0;
            var dst = (byte*)to.Scan0;
            long sourceStride = from.Stride, turnedStride = to.Stride;
            // Clockwise 90: a block's rows read from the bottom, so each column comes out top to bottom, and pixel
            // (x, y) goes to row x, column h-1-y; 270: rows from the top, pixel (x, y) to row w-1-x, column y.
            var rowStep = rotation == 90 ? -sourceStride : sourceStride;
            var columnStep = rotation == 90 ? turnedStride : -turnedStride;
            // Down each strip of 8 columns: the 8 rows each strip turns into are then written one after another.
            for (var x0 = 0; x0 < w; x0 += 8)
            {
                for (var y0 = 0; y0 < h; y0 += 8)
                {
                    var p = src + ((rotation == 90 ? y0 + 7 : y0) * sourceStride) + (x0 * 4L);
                    var r0 = Vector256.Load((uint*)p);
                    var r1 = Vector256.Load((uint*)(p + rowStep));
                    var r2 = Vector256.Load((uint*)(p + (2 * rowStep)));
                    var r3 = Vector256.Load((uint*)(p + (3 * rowStep)));
                    var r4 = Vector256.Load((uint*)(p + (4 * rowStep)));
                    var r5 = Vector256.Load((uint*)(p + (5 * rowStep)));
                    var r6 = Vector256.Load((uint*)(p + (6 * rowStep)));
                    var r7 = Vector256.Load((uint*)(p + (7 * rowStep)));

                    // The 8 x 8 transpose: pairs of rows interleaved by 32 bits, then by 64, then the halves swapped.
                    var t0 = Avx2.UnpackLow(r0, r1).AsUInt64();
                    var t1 = Avx2.UnpackHigh(r0, r1).AsUInt64();
                    var t2 = Avx2.UnpackLow(r2, r3).AsUInt64();
                    var t3 = Avx2.UnpackHigh(r2, r3).AsUInt64();
                    var t4 = Avx2.UnpackLow(r4, r5).AsUInt64();
                    var t5 = Avx2.UnpackHigh(r4, r5).AsUInt64();
                    var t6 = Avx2.UnpackLow(r6, r7).AsUInt64();
                    var t7 = Avx2.UnpackHigh(r6, r7).AsUInt64();
                    var u0 = Avx2.UnpackLow(t0, t2).AsUInt32();
                    var u1 = Avx2.UnpackHigh(t0, t2).AsUInt32();
                    var u2 = Avx2.UnpackLow(t1, t3).AsUInt32();
                    var u3 = Avx2.UnpackHigh(t1, t3).AsUInt32();
                    var u4 = Avx2.UnpackLow(t4, t6).AsUInt32();
                    var u5 = Avx2.UnpackHigh(t4, t6).AsUInt32();
                    var u6 = Avx2.UnpackLow(t5, t7).AsUInt32();
                    var u7 = Avx2.UnpackHigh(t5, t7).AsUInt32();

                    var q = rotation == 90 ? dst + (x0 * turnedStride) + ((h - 8 - y0) * 4L) : dst + ((w - 1 - x0) * turnedStride) + (y0 * 4L);
                    Avx2.Permute2x128(u0, u4, 0x20).Store((uint*)q);
                    Avx2.Permute2x128(u1, u5, 0x20).Store((uint*)(q + columnStep));
                    Avx2.Permute2x128(u2, u6, 0x20).Store((uint*)(q + (2 * columnStep)));
                    Avx2.Permute2x128(u3, u7, 0x20).Store((uint*)(q + (3 * columnStep)));
                    Avx2.Permute2x128(u0, u4, 0x31).Store((uint*)(q + (4 * columnStep)));
                    Avx2.Permute2x128(u1, u5, 0x31).Store((uint*)(q + (5 * columnStep)));
                    Avx2.Permute2x128(u2, u6, 0x31).Store((uint*)(q + (6 * columnStep)));
                    Avx2.Permute2x128(u3, u7, 0x31).Store((uint*)(q + (7 * columnStep)));
                }
            }
        }
        finally
        {
            source.UnlockBits(from);
            turned.UnlockBits(to);
        }

        return turned;
    }

    /// <summary>
    /// The JPEG quality a screen's pictures fit at, remembered between pictures: the next starts there (one encoding
    /// instead of a step down from 90 each time), and tries one step better when the last was well under the limit.
    /// </summary>
    public sealed class JpegQuality
    {
        public long Value { get; set; } = 90;

        /// <summary>
        /// The screen's encoding buffer, kept at the size its pictures need (a new one grew by doubling, copying what
        /// was written, for each picture). A screen encodes one picture at a time.
        /// </summary>
        internal MemoryStream Buffer { get; } = new();
    }

    /// <summary>A picture as a screen takes it. A JPEG starts at quality 90 and steps down until it fits the limit.</summary>
    public static byte[] Encode(Bitmap bitmap, FrameFormat format, int maxBytes, JpegQuality? remembered = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        switch (format)
        {
            case FrameFormat.Png:
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }

            case FrameFormat.Rgb565:
                return Rgb565(bitmap);

            default:
            {
                byte[] jpeg = [];
                for (var quality = remembered?.Value ?? 90L; quality >= 30; quality -= 10)
                {
                    using var own = remembered is null ? new MemoryStream() : null;
                    var stream = own ?? remembered!.Buffer;
                    stream.SetLength(0);
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                    bitmap.Save(stream, JpegCodec, parameters);
                    jpeg = stream.ToArray();
                    if (maxBytes <= 0 || jpeg.Length <= maxBytes)
                    {
                        if (remembered is not null)
                        {
                            // Well under the limit: the next one tries a step better (up to 90).
                            remembered.Value = maxBytes > 0 && jpeg.Length < maxBytes * 0.6 ? Math.Min(90, quality + 10) : quality;
                        }

                        break;
                    }
                }

                return jpeg;
            }
        }
    }

    private static byte[] Rgb565(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            var output = new byte[bitmap.Width * bitmap.Height * 2];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var (b, g, r) = (row[x * 4], row[(x * 4) + 1], row[(x * 4) + 2]);
                    var pixel = (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
                    var at = ((y * bitmap.Width) + x) * 2;
                    output[at] = (byte)pixel;
                    output[at + 1] = (byte)(pixel >> 8);
                }
            }

            return output;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
