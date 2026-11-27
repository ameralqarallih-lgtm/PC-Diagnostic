using System.Drawing;
using PcDiag.Agent.Screens;
using PcDiag.Screens;

namespace PcDiag.Agent.Tests;

public sealed class ScreenFrameQualityTests
{
    [Fact]
    public void A_screen_remembers_the_jpeg_quality_that_fits_and_starts_there_next_time()
    {
        // Noise: a picture that is big as a JPEG, so the quality must come down to fit.
        using var bitmap = new Bitmap(480, 240);
        var random = new Random(7);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
            }
        }

        var free = ScreenFrames.Encode(bitmap, FrameFormat.Jpeg, 0);
        var limit = free.Length / 2;
        var quality = new ScreenFrames.JpegQuality();
        var first = ScreenFrames.Encode(bitmap, FrameFormat.Jpeg, limit, quality);
        first.Length.ShouldBeLessThanOrEqualTo(limit);
        quality.Value.ShouldBeLessThan(90);

        // The next starts at the quality that fit, and fits.
        var kept = quality.Value;
        ScreenFrames.Encode(bitmap, FrameFormat.Jpeg, limit, quality).Length.ShouldBeLessThanOrEqualTo(limit);
        quality.Value.ShouldBe(kept);
    }

    [Fact]
    public void A_screens_kept_encoding_buffer_gives_the_same_pictures_as_a_new_one_each_time()
    {
        static Bitmap Picture(int width, int height, int seed)
        {
            var bitmap = new Bitmap(width, height);
            var random = new Random(seed);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                }
            }

            return bitmap;
        }

        // A big picture, then a small one (the buffer is longer than it needs), then the big one again.
        using var big = Picture(480, 240, 1);
        using var small = Picture(64, 32, 2);
        var quality = new ScreenFrames.JpegQuality();
        foreach (var picture in new[] { big, small, big })
        {
            var alone = ScreenFrames.Encode(picture, FrameFormat.Jpeg, 0, new ScreenFrames.JpegQuality());
            ScreenFrames.Encode(picture, FrameFormat.Jpeg, 0, quality).ShouldBe(alone);
        }
    }
}
