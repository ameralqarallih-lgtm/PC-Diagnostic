using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Telemetry;
using static PcDiag.Core.Recording.RecordingItems;

namespace PcDiag.Engine.Tests.Recording;

/// <summary>A custom recording stores only what the user selected; the live view still gets everything.</summary>
public class CustomRecordingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly Reading<double> D = Reading<double>.Of(1);
    private static readonly Reading<long> L = Reading<long>.Of(1);

    private static GpuAdapterSample Adapter(string key, string name) => new(
        new GpuAdapterDescriptor(key, name, "NVIDIA", 0x10DE, 0x2684, 24L << 30, 0, Reading<double>.Of(90), Reading<double>.Of(83), ["nvml"]),
        D, D, D, D, D, D, L, L, L, Reading<double>.Of(55), Reading<double>.Of(62), Reading<double>.Of(2580), Reading<double>.Of(10501), Reading<double>.Of(300), D, D, L, D);

    private static SensorReading SensorValue(string hardware, string name, string key, string? aliasOf = null) =>
        new(new SensorDescriptor("lhm", hardware, SensorKind.Temperature, name, "°C", key, aliasOf), Reading<double>.Of(60));

    private static SampleFrame Frame()
    {
        var cpu = new CpuSample(T0, Reading<double>.Of(37), D, D, Reading<double>.Of(4800), D, D, D, L, D, D, D, Reading<double>.Of(70), Reading<double>.Of(90), [new CpuCoreSample(0, D, D)]);
        var memory = new MemorySample(T0, L, Reading<long>.Of(30L << 30), L, L, L, L, L, L, L, D, Reading<double>.Of(5), D, D);
        var gpu = new GpuSample(T0, [Adapter("gpu-a", "RTX 4090"), Adapter("gpu-b", "Intel Graphics")],
            [new GpuProcessUsage(100, "gpu-a", 80, "3D", L), new GpuProcessUsage(200, "gpu-b", 5, "3D", L)]);
        var disk = new DiskSample(T0,
            [new PhysicalDiskSample(new DiskDescriptor("disk-0", 0, "Samsung 9100 PRO", "NVMe", StorageMediaKind.Ssd, 4L << 40, false, "Healthy"), ["C:"], D, D, D, D, Reading<double>.Of(3), D, D, D, Reading<double>.Of(45))],
            [new VolumeSample("C:", "disk-0", "Windows", "NTFS", "Fixed", L, L)]);
        var network = new NetworkSample(T0,
            [new NetworkInterfaceSample(new NetworkInterfaceDescriptor("net-1", "Ethernet 3", "Intel I226-V", NetworkInterfaceKind.Ethernet, null), D, D, D, D, L, L, L, L, L, D, true, D, D)]);
        var process = new ProcessMetrics(new ProcessKey(100, 1), "game.exe", D, L, L, L, L, D, D, Reading<int>.Of(1), Reading<int>.Of(1), Reading<int>.Of(8), ProcessState.Running);
        var processes = new ProcessSample(T0, [process], [], [new ProcessLifecycleEvent(T0, new ProcessKey(300, 2), "tool.exe", ProcessEventKind.Started)]);
        var sensors = new SensorSample(T0,
        [
            SensorValue("Intel Core Ultra 9 285K", "CPU Package", "lhm/intelcpu/0/temperature/0"),
            SensorValue("Intel Core Ultra 9 285K", "CPU Package", SensorKeys.CpuPackageTemperature, aliasOf: "lhm/intelcpu/0/temperature/0"),
            SensorValue("NZXT Kraken X3", "Liquid", "lhm/usbhid/1/temperature/0"),
        ]);
        SystemEvent Event(EventCategory category, string title) => new(T0, "System", 1, "p", 1, EventLevel.Error, category, Severity.Medium, title, "");
        return new SampleFrame(1, T0, cpu, memory, gpu, disk, network, processes, sensors,
            new TickDiagnostics(TimeSpan.FromMilliseconds(8), 10, 0, Reading<double>.Of(0.2), 200L << 20),
            Events: [Event(EventCategory.Display, "TDR"), Event(EventCategory.Network, "Link down")]);
    }

    [Fact]
    public void Only_the_selected_items_are_kept_and_the_rest_says_it_was_not_selected()
    {
        var frame = Frame();
        var items = RecordingSelection.Of([CpuUsage, Gpu("gpu-a", "thermal"), Gpu("gpu-a", "processes"), Sensor("lhm/intelcpu/0/temperature/0"), ProcessLifecycle, RecordingItems.Events(EventCategory.Display)]).ToSet();

        var kept = RecordingFilter.Apply(frame, items);

        kept.Cpu!.TotalPercent.Value.ShouldBe(37);
        kept.Cpu.FrequencyMhz.ShouldBe(Reading<double>.Unavailable(Availability.NotSelected));
        kept.Cpu.PackageTemperatureC.Availability.ShouldBe(Availability.NotSelected);
        kept.Cpu.Cores.ShouldBeEmpty();
        kept.Memory.ShouldBeNull();

        // The other GPU is left out; the kept one has its temperatures but not its clocks, and only its own processes.
        var gpu = kept.Gpu!.Adapters.ShouldHaveSingleItem();
        gpu.Adapter.AdapterKey.ShouldBe("gpu-a");
        gpu.HotspotTemperatureC.Value.ShouldBe(62);
        gpu.CoreClockMhz.Availability.ShouldBe(Availability.NotSelected);
        kept.Gpu.Processes.ShouldHaveSingleItem().ProcessId.ShouldBe(100);

        kept.Disk.ShouldBeNull();
        kept.Network.ShouldBeNull();
        kept.Processes!.Processes.ShouldBeEmpty();
        kept.Processes.Events.ShouldHaveSingleItem().Name.ShouldBe("tool.exe");

        // A selected sensor keeps its shared key (the CPU package temperature the CPU table uses) too.
        kept.Sensors!.Readings.Select(r => r.Sensor.LogicalKey).ShouldBe(["lhm/intelcpu/0/temperature/0", SensorKeys.CpuPackageTemperature]);
        kept.Events.ShouldHaveSingleItem().Title.ShouldBe("TDR");
        kept.Diagnostics.ShouldBe(frame.Diagnostics, "the recorder's own figures are always recorded");

        // Unchanged objects keep their identity, so the ring buffer still shares them between frames.
        ReferenceEquals(kept.Sensors.Readings[0], frame.Sensors!.Readings[0]).ShouldBeTrue();
    }

    [Fact]
    public void Nothing_selected_of_a_source_leaves_it_out_and_whole_sources_pass_through()
    {
        var frame = Frame();

        var kept = RecordingFilter.Apply(frame, RecordingSelection.Of([MemoryPaging, VolumeSpace, Network("net-1", "errors")]).ToSet());

        kept.Cpu.ShouldBeNull();
        kept.Gpu.ShouldBeNull();
        kept.Sensors!.Readings.ShouldBeEmpty();
        kept.Memory!.HardFaultsPerSec.Value.ShouldBe(5);
        kept.Memory.AvailableBytes.Availability.ShouldBe(Availability.NotSelected);
        kept.Disk!.Disks.ShouldBeEmpty();
        kept.Disk.Volumes.ShouldHaveSingleItem();
        kept.Network!.Interfaces.ShouldHaveSingleItem().ReceiveBytesPerSec.Availability.ShouldBe(Availability.NotSelected);
        kept.Processes.ShouldBeNull();
        kept.Events.ShouldBeNull();
    }

    [Fact]
    public void The_catalog_lists_each_device_and_sensor_once()
    {
        var frame = Frame();

        var catalog = RecordingCatalogBuilder.Build(frame.Gpu, frame.Disk, frame.Network, frame.Sensors);

        catalog.Sources.Select(s => s.Id).ShouldBe(
        [
            "cpu", "memory", "gpu:gpu-a", "gpu:gpu-b", "disk:disk-0", "volumes", "net:net-1", "sensors:Intel Core Ultra 9 285K", "sensors:NZXT Kraken X3",
            "processes", "events", "display", "services", "inventory",
        ]);
        catalog.Sources.Single(s => s.Id == "gpu:gpu-a").ShouldSatisfyAllConditions(
            s => s.Label.ShouldBe("RTX 4090"), s => s.Items.Select(i => i.Id).ShouldContain(Gpu("gpu-a", "thermal")));
        catalog.Sources.Single(s => s.Id == "sensors:Intel Core Ultra 9 285K").Items.ShouldHaveSingleItem().Id.ShouldBe(Sensor("lhm/intelcpu/0/temperature/0"));
        catalog.Sources.Single(s => s.Id == "events").Items.Count.ShouldBe(Enum.GetValues<EventCategory>().Length);
        catalog.Sources.SelectMany(s => s.Items).Select(i => i.Id).ShouldBeUnique();
    }

    [Fact]
    public async Task During_a_custom_recording_the_live_view_gets_everything_and_storage_only_the_selection()
    {
        var time = new FakeTimeProvider(T0);
        var stored = new RecordingConsumer("storage");
        var live = new RecordingConsumer("live") { SeesUnrecordedData = true };
        var bus = EngineFactory.Bus(stored, live);
        await bus.StartAsync(CancellationToken.None);
        var scheduler = EngineFactory.Scheduler(time, bus, new FakeCollector("cpu", SamplingTier.System));
        var plan = new TierPlan(new SamplingOptions());

        await scheduler.RunTickAsync(plan, [], CancellationToken.None, recorded: true, selection: RecordingSelection.Of([MemoryUsage]).ToSet());
        await scheduler.RunTickAsync(plan, [], CancellationToken.None, recorded: true);
        await bus.FlushAsync(CancellationToken.None);

        live.Frames.ShouldAllBe(f => f.Cpu != null);
        stored.Frames[0].Cpu.ShouldBeNull();
        stored.Frames[1].Cpu.ShouldNotBeNull("no selection records everything");
        await bus.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Selections_combine_and_compare_by_their_items()
    {
        var a = RecordingSelection.Of([CpuUsage, MemoryUsage]);
        var b = RecordingSelection.Of([MemoryUsage, CpuUsage]);

        RecordingSelection.SameAs(a, b).ShouldBeTrue();
        RecordingSelection.SameAs(a, null).ShouldBeFalse();
        RecordingSelection.SameAs(null, null).ShouldBeTrue();
        RecordingSelection.Union([a, RecordingSelection.Of([RecordingItems.ProcessMetrics])])!.Items.Order(StringComparer.Ordinal).ShouldBe([CpuUsage, MemoryUsage, RecordingItems.ProcessMetrics]);
        RecordingSelection.Union([a, null]).ShouldBeNull("a window that records everything wins");
    }
}
