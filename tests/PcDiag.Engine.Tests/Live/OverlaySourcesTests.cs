using PcDiag.Core.Overlay;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Overlay;

namespace PcDiag.Engine.Tests.Live;

public sealed class OverlaySourcesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 1, 53, 0, TimeSpan.FromHours(4));
    private static readonly Reading<double> NoDouble = Reading<double>.Unavailable(Availability.Unsupported);
    private static readonly Reading<long> NoLong = Reading<long>.Unavailable(Availability.Unsupported);

    [Fact]
    public void Every_value_has_a_stable_id_a_label_a_group_and_a_unit()
    {
        var gpu = new GpuAdapterSample(new GpuAdapterDescriptor("gpu-10de", "NVIDIA GeForce RTX 4090", "NVIDIA", 0x10de, 0x2684, 24L << 30, 0, NoDouble, NoDouble, []),
            Reading<double>.Of(12), NoDouble, NoDouble, NoDouble, NoDouble, NoDouble, Reading<long>.Of(3L << 30), Reading<long>.Of(24L << 30), NoLong,
            Reading<double>.Of(41), NoDouble, Reading<double>.Of(285), NoDouble, NoDouble, NoDouble, NoDouble, NoLong, NoDouble);
        var hotspot = new SensorReading(new SensorDescriptor("lhm", "NVIDIA GeForce RTX 4090", SensorKind.Temperature, "NVIDIA GeForce RTX 4090 / GPU Hot Spot", "°C", "lhm/gpu-nvidia/0/temperature/2"), Reading<double>.Of(52));
        var alias = hotspot with { Sensor = hotspot.Sensor with { LogicalKey = SensorKeys.GpuHotspot("NVIDIA GeForce RTX 4090"), AliasOf = hotspot.Sensor.LogicalKey } };
        var battery = new SensorReading(new SensorDescriptor("lhm", "Nuvoton NCT6701D", SensorKind.Voltage, "Nuvoton NCT6701D / CMOS Battery", "V", "lhm/lpc/nct6701d/0/voltage/8"), Reading<double>.Of(3.088));
        var samples = new LiveSamples(T0, Cpu(52.4), Memory(), new GpuSample(T0, [gpu], []), null, null, new SensorSample(T0, [hotspot, alias, battery]));

        var sources = OverlaySources.Build(samples, T0, recording: false).ToDictionary(s => s.Id);

        sources["cpu.usage"].ShouldBe(new OverlaySource("cpu.usage", "CPU", "Processor", "%", 52.4));
        sources["cpu.clock"].Value!.Value.ShouldBe(3.6, 1e-9);
        sources["memory.usage"].Value.ShouldBe(75);
        sources["memory.used"].Unit.ShouldBe("B");
        sources["gpu.gpu-10de.vram_pct"].Value.ShouldBe(12.5);
        sources["gpu.gpu-10de.hotspot"].Value.ShouldBe(52); // from the sensor, the adapter reports none
        sources["gpu.gpu-10de.usage"].Group.ShouldBe("NVIDIA GeForce RTX 4090");
        sources["sensor.lhm/lpc/nct6701d/0/voltage/8"].ShouldBe(new OverlaySource("sensor.lhm/lpc/nct6701d/0/voltage/8", "CMOS Battery", "Nuvoton NCT6701D", "V", 3.088));
        sources.Keys.ShouldNotContain("sensor." + SensorKeys.GpuHotspot("NVIDIA GeForce RTX 4090")); // aliases repeat a sensor
        sources["time.clock"].Text.ShouldBe("01:53");
        sources["recorder.state"].Text.ShouldBe("Not recording");
    }

    [Fact]
    public void Monitors_are_keyed_by_the_display_configuration_and_numbered_left_to_right()
    {
        var areas = new List<MonitorArea>
        {
            new(@"\\.\DISPLAY2", 3840, 0, 1920, 1080, false, 1),
            new(@"\\.\DISPLAY1", 0, 0, 3840, 2160, true, 1.5),
            new(@"\\.\DISPLAY9", -1920, 0, 1920, 1080, false, 1),
        };
        var snapshot = new DisplaySnapshot(T0, [Target("AUS32F4/a", "PG32UCDP", @"\\.\DISPLAY1", 240), Target("AUS2721/b", "VG279QM", @"\\.\DISPLAY2", 120)], false);

        var displays = OverlayDisplays.Build(areas, snapshot);

        displays.Select(d => (d.Number, d.Key, d.Name)).ShouldBe([(1, @"screen:\\.\DISPLAY9", "Display 1"), (2, "AUS32F4/a", "PG32UCDP"), (3, "AUS2721/b", "VG279QM")]);
        displays[1].Scale.ShouldBe(1.5);
        displays[1].RefreshHz.ShouldBe(240);
        displays[1].Primary.ShouldBeTrue();
    }

    private static DisplayTarget Target(string key, string name, string gdi, double hz) =>
        new(key, name, "AUS", null, 1920, 1080, hz, DisplayConnection.DisplayPort, Reading<bool>.Of(false), Reading<bool>.Of(false), Reading<bool>.Of(false), 0, false, 0, 0, "gpu-10de", "RTX 4090", gdi);

    [Fact]
    public void Gaming_values_come_from_the_frame_rate_the_round_trips_and_the_app_drawing_frames()
    {
        var game = new ProcessMetrics(new ProcessKey(4242, 1), "Cyberpunk2077", Reading<double>.Of(31.5), Reading<long>.Of(0), Reading<long>.Of(9L << 30), NoLong, NoLong, NoDouble, NoDouble,
            Reading<int>.Unavailable(Availability.Unsupported), Reading<int>.Unavailable(Availability.Unsupported), Reading<int>.Unavailable(Availability.Unsupported), ProcessState.Running);
        var samples = new LiveSamples(T0, Cpu(52.4), Memory(), null, null, null, null, new ProcessSample(T0, [game], [], []));
        var state = new GameState(new FrameStats(4242, "Cyberpunk2077", 118, 8.47, 74.2, true), new LatencyStats(1, 14), 4242);

        var sources = OverlaySources.Build(samples, T0, recording: false, state).ToDictionary(s => s.Id);

        sources["game.fps"].ShouldBe(new OverlaySource("game.fps", "FPS", "Gaming", "fps", 118));
        sources["game.frametime"].Value.ShouldBe(8.47);
        sources["game.low1"].Value.ShouldBe(74.2);
        sources["game.app"].Text.ShouldBe("Cyberpunk2077");
        sources["game.cpu"].Value.ShouldBe(31.5);
        sources["game.memory"].Value.ShouldBe(9L << 30);
        sources["net.ping.router"].Value.ShouldBe(1);
        sources["net.ping.internet"].Value.ShouldBe(14);
        sources["memory.used"].Max.ShouldBe(16L << 30); // its total, for "of" and percentages
        OverlayStyles.Format(sources["game.frametime"], null).ShouldBe(("8.5", "ms"));

        // Nothing drawing frames: the values are there, empty.
        var idle = OverlaySources.Build(samples, T0, recording: false).ToDictionary(s => s.Id);
        idle["game.fps"].Value.ShouldBeNull();
        idle["game.app"].Text.ShouldBe("—");
    }

    [Fact]
    public void Texts_keep_a_fixed_space_and_an_apps_name_loses_its_build_suffix()
    {
        var sources = OverlaySources.Build(new LiveSamples(T0, Cpu(10), Memory(), null, null, null, null), T0, recording: true).ToDictionary(s => s.Id);

        sources["time.clock"].Room.ShouldBe(["00:00"]);
        sources["time.seconds"].Room.ShouldBe(["00:00:00"]);
        sources["recorder.state"].Room.ShouldBe(["Recording", "Not recording"]);
        sources["time.date"].Room!.Count.ShouldBe(7 * 12);
        sources["time.date"].Room!.ShouldContain("Wed 00 May");
        sources["game.app"].Room.ShouldBe([new string('0', 14)]);
        sources["devices.missing_names"].Room.ShouldBe([new string('0', 24)]);
        sources["cpu.usage"].Room.ShouldBeNull();

        OverlaySources.AppName("FortniteClient-Win64-Shipping").ShouldBe("FortniteClient");
        OverlaySources.AppName("VALORANT-Win64-Shipping").ShouldBe("VALORANT");
        OverlaySources.AppName("cs2").ShouldBe("cs2");
        OverlaySources.AppName("-Shipping").ShouldBe("-Shipping");
        OverlaySources.AppName(null).ShouldBe("—");
    }

    private static CpuSample Cpu(double total) => new(
        T0, Reading<double>.Of(total), NoDouble, NoDouble, Reading<double>.Of(3600), NoDouble, NoDouble, NoDouble, NoLong, NoDouble, NoDouble, NoDouble, NoDouble, NoDouble, []);

    private static MemorySample Memory() => new(
        T0, Reading<long>.Of(16L << 30), Reading<long>.Of(4L << 30), NoLong, NoLong, Reading<long>.Of(10), Reading<long>.Of(20), NoLong, NoLong, NoLong, NoDouble, NoDouble, NoDouble, NoDouble);
}
