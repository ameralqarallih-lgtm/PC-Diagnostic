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
}
