using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
    /// The JPEG quality a screen's pictures fit at, remembered between pictures: the next starts there (one encoding
    /// instead of a step down from 90 each time), and tries one step better when the last was well under the limit.
    /// </summary>
    public sealed class JpegQuality
    {
        public long Value { get; set; } = 90;
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
                    using var stream = new MemoryStream();
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
