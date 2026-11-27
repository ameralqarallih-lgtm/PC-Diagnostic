using PcDiag.Core.Overlay;

namespace PcDiag.Core.Tests;

public sealed class DisplayedFramesTests
{
    private const long Second = 10_000_000;

    /// <summary>PresentMon 2.6's header with --qpc_time (columns found by name, so their order does not matter).</summary>
    private const string Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartQPC,MsBetweenAppStart,MsCPUBusy,MsCPUWait,"
        + "MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,DisplayLatency,DisplayedTime,MsAnimationError,AnimationTime,MsBetweenPresents";

    [Fact]
    public void PresentMon_lines_are_read_by_their_column_names_and_na_is_no_value()
    {
        var csv = new PresentMonCsv();
        csv.Parse(Header).ShouldBeNull();
        csv.Parse("").ShouldBeNull();

        var shown = csv.Parse("VALORANT-Win64-Shipping.exe,1234,0x1,DXGI,0,512,1,Hardware: Independent Flip,1000000,4.9,2.1,2.8,0.4,4.5,4.4,0.5,9.8,4.17,NA,NA,4.85")!;
        shown.StartQpc.ShouldBe(1_000_000);
        shown.FrameMs.ShouldBe(4.85);
        shown.Displayed.ShouldBeTrue();
        shown.DisplayedMs.ShouldBe(4.17);
        shown.DisplayLatencyMs.ShouldBe(9.8);
        shown.GpuBusyMs.ShouldBe(4.4);
        shown.CpuBusyMs.ShouldBe(2.1);
        shown.PresentMode.ShouldBe("Hardware: Independent Flip");

        var dropped = csv.Parse("VALORANT-Win64-Shipping.exe,1234,0x1,DXGI,0,512,1,Composed: Flip,1050000,4.9,2.1,2.8,0.4,4.5,4.4,0.5,NA,NA,NA,NA,4.85")!;
        dropped.Displayed.ShouldBeFalse();
        dropped.DisplayedMs.ShouldBeNull();
        dropped.DisplayLatencyMs.ShouldBeNull();
        dropped.PresentMode.ShouldBe("Composed: Flip");

        // Before its header PresentMon's lines cannot be read.
        new PresentMonCsv().Parse("x,1,0x1,DXGI,0,0,0,Composed: Flip,1,1,1,1,1,1,1,1,1,1,NA,NA,1").ShouldBeNull();
    }

    [Fact]
    public void A_second_counts_frames_sent_and_shown_and_the_shares_of_each_frame()
    {
        var frames = new DisplayedFrameTimes(Second);

        // 300 frames a second for 3 s (3.33 ms), on a 240 Hz screen: 60 of them a second replaced before they were shown.
        long t = 0;
        for (var i = 0; i < 900; i++)
        {
            t += Second / 300;
            var shown = i % 5 != 0;
            frames.Add(new PresentedFrame(t, 3.33, shown, shown ? 4.17 : null, shown ? 8 : null, 3.0, 1.5, "Hardware: Independent Flip"));
        }

        // One image held for 120 ms: what a hitch looks like on the screen.
        frames.Add(new PresentedFrame(t + 10, 3.33, true, 120, 9, 3.0, 1.5, "Hardware: Independent Flip"));

        var stats = frames.Stats(t + (long)(DisplayedFrameTimes.Settle.TotalSeconds * Second) + 20)!;
        stats.PresentedFps.ShouldBe(301, 2);
        stats.DisplayedFps.ShouldBe(241, 2);
        stats.Dropped.ShouldBeInRange(59, 61);
        stats.WorstDisplayedMs.ShouldBe(120);
        stats.DisplayLatencyMs!.Value.ShouldBe(8, 0.1);
        stats.GpuBusyPercent!.Value.ShouldBe(90, 0.5);
        stats.CpuBusyPercent!.Value.ShouldBe(45, 0.5);
        stats.PresentMode.ShouldBe("Hardware: Independent Flip");

        frames.Clear();
        frames.Stats(t).ShouldBeNull();
    }
}
