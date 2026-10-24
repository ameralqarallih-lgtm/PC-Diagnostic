using PcDiag.Collectors.Gpu;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>Pure logic of the GPU collector, with synthetic PDH instance names.</summary>
public class GpuLogicTests
{
    private static readonly AdapterLuid Nvidia = new(0, 0x55F6FCA6);
    private static readonly AdapterLuid Intel = new(0, 0x0001C5A5);

    [Fact]
    public void Engine_instance_names_are_parsed()
    {
        GpuInstanceNames.TryParseEngine("pid_1234_luid_0x00000000_0x55F6FCA6_phys_0_eng_3_engtype_VideoDecode", out var pid, out var luid, out var engine, out var type).ShouldBeTrue();

        pid.ShouldBe(1234);
        luid.ShouldBe(Nvidia);
        engine.ShouldBe(3);
        type.ToString().ShouldBe("VideoDecode");
    }

    [Theory]
    [InlineData("luid_0x00000000_0x55F6FCA6_phys_0", true)]
    [InlineData("luid_0x00000000_0x55f6fca6_phys_1", true)] // hex case and linked adapters
    [InlineData("luid_0x00000000_0x55F6FCA6", false)]
    [InlineData("pid_1_luid_0x00000000_0x55F6FCA6_phys_0", false)]
    [InlineData("_Total", false)]
    public void Adapter_memory_instance_names_are_parsed(string name, bool valid)
    {
        GpuInstanceNames.TryParseAdapter(name, out var luid).ShouldBe(valid);
        if (valid)
        {
            luid.ShouldBe(Nvidia);
        }
    }

    [Theory]
    [InlineData("pid_77_luid_0x00000000_0x0001C5A5_phys_0", true)]
    [InlineData("pid__luid_0x00000000_0x0001C5A5_phys_0", false)]
    [InlineData("pid_77_luid_0xZZ_0x0001C5A5_phys_0", false)]
    public void Process_memory_instance_names_are_parsed(string name, bool valid)
    {
        GpuInstanceNames.TryParseProcessMemory(name, out var pid, out var luid).ShouldBe(valid);
        if (valid)
        {
            (pid, luid).ShouldBe((77, Intel));
        }
    }

    [Theory]
    [InlineData("3D", GpuEngineKind.ThreeD)]
    [InlineData("Graphics_1", GpuEngineKind.ThreeD)]
    [InlineData("Compute_0", GpuEngineKind.Compute)]
    [InlineData("VideoDecode", GpuEngineKind.VideoDecode)]
    [InlineData("Video Decode", GpuEngineKind.VideoDecode)]
    [InlineData("VideoEncode", GpuEngineKind.VideoEncode)]
    [InlineData("Copy", GpuEngineKind.Copy)]
    [InlineData("VideoProcessing", GpuEngineKind.Other)]
    [InlineData("Security", GpuEngineKind.Other)]
    public void Engine_types_are_classified(string type, GpuEngineKind expected)
    {
        GpuInstanceNames.Classify(type).ShouldBe(expected);
    }

    [Fact]
    public void Utilization_is_aggregated_like_task_manager()
    {
        var usage = new GpuUsageAggregator();
        usage.Reset();

        // Two processes share the 3D engine 0 → engine 0 = 50 %. Engine 5 (VideoDecode) = 30 %.
        usage.Add("pid_10_luid_0x00000000_0x55F6FCA6_phys_0_eng_0_engtype_3D", 20);
        usage.Add("pid_20_luid_0x00000000_0x55F6FCA6_phys_0_eng_0_engtype_3D", 30);
        usage.Add("pid_20_luid_0x00000000_0x55F6FCA6_phys_0_eng_5_engtype_VideoDecode", 30);
        usage.Add("pid_30_luid_0x00000000_0x0001C5A5_phys_0_eng_0_engtype_3D", 5);

        var (overall, byKind) = usage.ForAdapter(Nvidia);
        overall.ShouldBe(50);
        byKind[GpuEngineKind.ThreeD].ShouldBe(50);
        byKind[GpuEngineKind.VideoDecode].ShouldBe(30);
        usage.ForAdapter(Intel).Overall.ShouldBe(5);

        // A process's utilization is its busiest engine.
        usage.Processes.Single(p => p.Pid == 20).ShouldBe((20, Nvidia, 30.0, "3D"));
    }

    [Fact]
    public void Engine_sums_are_capped_at_100()
    {
        var usage = new GpuUsageAggregator();
        usage.Reset();
        usage.Add("pid_1_luid_0x00000000_0x55F6FCA6_phys_0_eng_0_engtype_3D", 70);
        usage.Add("pid_2_luid_0x00000000_0x55F6FCA6_phys_0_eng_0_engtype_3D", 60);

        usage.ForAdapter(Nvidia).Overall.ShouldBe(100);
    }

    [Fact]
    public void Details_are_merged_by_priority_field_by_field()
    {
        var unsupported = Reading<double>.Unavailable(Availability.Unsupported);
        var nvml = GpuDetails.None(Availability.Unsupported) with { TemperatureC = Reading<double>.Of(45), PowerW = Reading<double>.Of(70) };
        var kmt = GpuDetails.None(Availability.Unsupported) with
        {
            TemperatureC = Reading<double>.Of(44),
            PowerPercent = Reading<double>.Of(15.9),
            FanRpm = Reading<double>.Of(0),
        };

        var merged = GpuDetails.Merge([nvml, kmt]);

        merged.TemperatureC.ShouldBe(Reading<double>.Of(45)); // higher priority wins
        merged.PowerW.ShouldBe(Reading<double>.Of(70));
        merged.PowerPercent.ShouldBe(Reading<double>.Of(15.9)); // filled from the lower-priority provider
        merged.FanRpm.ShouldBe(Reading<double>.Of(0));           // a real 0 RPM (fans stopped) is a value, not a gap
        merged.HotspotTemperatureC.ShouldBe(unsupported);
    }

    [Fact]
    public void Merge_keeps_the_most_informative_reason()
    {
        var elevation = GpuDetails.None(Availability.RequiresElevation);
        var unsupported = GpuDetails.None(Availability.Unsupported);

        GpuDetails.Merge([unsupported, elevation]).TemperatureC.Availability.ShouldBe(Availability.RequiresElevation);
        GpuDetails.Merge([]).TemperatureC.Availability.ShouldBe(Availability.Unsupported);
    }

    [Fact]
    public void Adapter_keys_are_stable_and_distinguish_identical_cards()
    {
        var card = new DxgiAdapterInfo("GPU", 0x10DE, 0x2684, 0x467519DA, 0xA1, 24L << 30, 0, Nvidia, false);

        GpuCollector.AdapterKey(card, 0).ShouldBe("gpu-10de-2684-467519da");
        GpuCollector.AdapterKey(card, 1).ShouldBe("gpu-10de-2684-467519da-1");
        GpuCollector.VendorName(0x8086).ShouldBe("Intel");
        GpuCollector.VendorName(0x1234).ShouldBe("0x1234");
    }

    /// <summary>Seen on the dev PC: WSL2's GPU adapter is listed by DXGI as a second RTX 4090 with no PCI location.</summary>
    [Fact]
    public void A_virtual_twin_of_a_real_card_is_dropped_but_real_identical_cards_are_kept()
    {
        var real = new DxgiAdapterInfo("NVIDIA GeForce RTX 4090", 0x10DE, 0x2684, 0x467519DA, 161, 24L << 30, 48L << 30, new AdapterLuid(0, 0x0001AC1C), false);
        var virtualTwin = real with { Luid = new AdapterLuid(0, 0x73571C85) };
        var secondReal = real with { Luid = new AdapterLuid(0, 0x0002BEEF) };
        var igpu = new DxgiAdapterInfo("Intel(R) Graphics", 0x8086, 0x7D67, 0x88EF1043, 6, 128L << 20, 48L << 30, new AdapterLuid(0, 0x0001A848), false);
        var basic = new DxgiAdapterInfo("Microsoft Basic Render Driver", 0x1414, 0x008C, 0, 0, 0, 0, new AdapterLuid(0, 0x0001C23C), true);
        var socGpu = new DxgiAdapterInfo("Qualcomm Adreno", 0x5143, 0x0C36, 0x00000000, 1, 0, 16L << 30, new AdapterLuid(0, 0x00010001), false);
        var pci = new Dictionary<AdapterLuid, (uint, uint, uint)?>
        {
            [real.Luid] = (1u, 0u, 0u),
            [virtualTwin.Luid] = (0u, 0xFFFFu, 0xFFFFu),
            [secondReal.Luid] = (2u, 0u, 0u),
            [igpu.Luid] = (0u, 2u, 0u),
            [basic.Luid] = (uint.MaxValue, 0xFFFFu, 0xFFFFu),
            [socGpu.Luid] = null,
        };

        GpuCollector.WithoutVirtualTwins([real, igpu, virtualTwin, basic], l => pci[l]).ShouldBe([real, igpu, basic]);
        GpuCollector.WithoutVirtualTwins([real, secondReal], l => pci[l]).ShouldBe([real, secondReal]); // two real cards
        GpuCollector.WithoutVirtualTwins([socGpu], l => pci[l]).ShouldBe([socGpu]);                       // no PCI at all, no twin
        GpuCollector.AssignKeys(GpuCollector.WithoutVirtualTwins([real, virtualTwin], l => pci[l])).Select(k => k.Key).ShouldBe(["gpu-10de-2684-467519da"]);

        // Remote Desktop: the same card listed again at the same PCI location, under another LUID - no "-1" key.
        var remoteView = real with { Luid = new AdapterLuid(0, 0x00ABCDEF) };
        pci[remoteView.Luid] = (1u, 0u, 0u);
        GpuCollector.AssignKeys(GpuCollector.WithoutVirtualTwins([real, remoteView, igpu], l => pci[l])).Select(k => k.Key).ShouldBe(["gpu-10de-2684-467519da", "gpu-8086-7d67-88ef1043"]);
    }
}
