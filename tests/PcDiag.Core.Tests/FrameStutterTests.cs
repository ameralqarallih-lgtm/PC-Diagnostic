using PcDiag.Core.Overlay;
using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Tests;

public sealed class FrameStutterTests
{
    private const long Second = 10_000_000;

    [Fact]
    public void Stutters_are_frames_far_longer_than_the_usual_one_counted_once_in_the_second_they_end()
    {
        var frames = new FrameTimes(Second);

        // A game at 100 fps for 20 s, with one frame of 120 ms that starts 0.95 s into second 10 and ends in second 11.
        long t = 0;
        while (t < 10 * Second - (Second / 20))
        {
            t += Second / 100;
            frames.Add(1, t);
        }

        var before = t;
        t += Second * 12 / 100;
        frames.Add(1, t);
        var hitchEnd = t;
        while (t < 20 * Second)
        {
            t += Second / 100;
            frames.Add(1, t);
        }

        // Read each second as the collector does (0.2 s settle): the hitch is in the second its frame came.
        var atHitch = frames.Stats(hitchEnd + (Second / 2) + (Second / 5), foreground: 1)!.Value;
        atHitch.Stutters.ShouldBe(1);
        atHitch.WorstFrameTimeMs.ShouldBe(120, 0.5);

        var earlier = frames.Stats(before + (Second / 5), foreground: 1)!.Value;
        earlier.Stutters.ShouldBe(0);
        earlier.WorstFrameTimeMs.ShouldBeLessThan(11);

        var later = frames.Stats(t + (Second / 5), foreground: 1)!.Value;
        later.Stutters.ShouldBe(0);
    }

    [Fact]
    public void A_frozen_game_reads_slow_while_it_waits_not_its_old_rate()
    {
        var frames = new FrameTimes(Second);
        long t = 0;
        for (var i = 0; i < 600; i++)
        {
            t += Second / 60;
            frames.Add(1, t);
        }

        // 1.5 s without a frame.
        var frozen = frames.Stats(t + (Second * 3 / 2) + (Second / 5), foreground: 1)!.Value;
        frozen.Fps.ShouldBeLessThan(1);
        frozen.WorstFrameTimeMs.ShouldBeGreaterThan(1400);
    }

    [Fact]
    public void A_slow_stretch_is_a_drop_not_stutters_and_hitches_in_it_still_count()
    {
        var frames = new FrameTimes(Second);

        // The live test window: 230 fps for 35 s, then each frame held 25 ms for 10 s (about 34 fps), then 230 fps with
        // 6 hitches of 150 ms 1.3 s apart.
        long t = 0;
        var stutters = 0;
        var secondEnd = Second;
        void Frame(long length)
        {
            t += length;
            frames.Add(1, t);
            while (t >= secondEnd + (Second / 5))
            {
                stutters += frames.Stats(secondEnd + (Second / 5), foreground: 1)!.Value.Stutters;
                secondEnd += Second;
            }
        }

        while (t < 35 * Second)
        {
            Frame(Second / 230);
        }

        while (t < 45 * Second)
        {
            Frame(Second * 29 / 1000);
        }

        var slowStretch = stutters;
        var next = t + (Second * 13 / 10);
        for (var hitch = 0; hitch < 6;)
        {
            if (t >= next)
            {
                Frame(Second * 150 / 1000);
                next = t + (Second * 13 / 10);
                hitch++;
            }
            else
            {
                Frame(Second / 230);
            }
        }

        for (var i = 0; i < 460; i++)
        {
            Frame(Second / 230);
        }

        slowStretch.ShouldBe(0);
        (stutters - slowStretch).ShouldBe(6);
    }

    [Fact]
    public void At_a_high_frame_rate_a_few_ms_more_is_no_stutter()
    {
        var frames = new FrameTimes(Second);

        // 300 fps (3.3 ms) with frames of 9 ms now and then: 2.7 times as long, but only 6 ms more.
        long t = 0;
        for (var i = 0; i < 3000; i++)
        {
            t += i % 50 == 0 ? Second * 9 / 1000 : Second / 300;
            frames.Add(1, t);
        }

        frames.Stats(t + (Second / 5), foreground: 1)!.Value.Stutters.ShouldBe(0);
    }

    [Fact]
    public void The_usual_frame_rate_comes_after_30_s_and_starts_over_after_a_pause()
    {
        var usual = new UsualFrameRate();
        var t0 = new DateTimeOffset(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
        double? last = null;
        for (var s = 0; s < 31; s++)
        {
            last = usual.Add(7, t0.AddSeconds(s), s < 5 ? 20 : 144); // loading at first
        }

        last.ShouldBe(144);
        usual.Add(7, t0.AddSeconds(31), 40).ShouldBe(144); // one slow second does not move it

        // A pause over a minute, or another game: warming up again.
        usual.Add(7, t0.AddSeconds(120), 144).ShouldBeNull();
        usual.Add(8, t0.AddSeconds(121), 144).ShouldBeNull();
    }

    [Fact]
    public void A_frame_sample_knows_how_far_below_its_usual_rate_it_is()
    {
        var at = DateTimeOffset.UnixEpoch;
        new FrameSample(at, 1, "g", 60, 16.7, 20, 0, null, 120, true).DropPercent.ShouldBe(50);
        new FrameSample(at, 1, "g", 130, 7.7, 9, 0, null, 120, true).DropPercent.ShouldBe(0);
        new FrameSample(at, 1, "g", 60, 16.7, 20, 0, null, null, true).DropPercent.ShouldBeNull();
    }
}
