using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Collectors.Sensors;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Inventory;
using PcDiag.Platform.Windows.Storage;
using PcDiag.Sensors.LibreHardwareMonitor;

namespace PcDiag.Collectors.IntegrationTests;

public class LhmMappingTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, "Intel Core Ultra 9 285K", SensorType.Temperature, "CPU Package", SensorKeys.CpuPackageTemperature)]
    [InlineData(HardwareType.Cpu, "AMD Ryzen 9 7950X", SensorType.Temperature, "Core (Tctl/Tdie)", SensorKeys.CpuPackageTemperature)]
    [InlineData(HardwareType.Cpu, "AMD Ryzen 9 7950X", SensorType.Power, "Package", SensorKeys.CpuPackagePower)]
    [InlineData(HardwareType.Cpu, "Intel", SensorType.Temperature, "CPU Core #1", null)]
    [InlineData(HardwareType.Motherboard, "Board", SensorType.Temperature, "CPU Package", null)]
    public void Well_known_sensors_get_shared_keys(HardwareType type, string hardware, SensorType sensorType, string name, string? expected)
    {
        LhmSensorProvider.CanonicalKey(type, hardware, sensorType, name).ShouldBe(expected);
    }

    [Fact]
    public void Gpu_hotspot_is_keyed_by_adapter_name()
    {
        LhmSensorProvider.CanonicalKey(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 4090", SensorType.Temperature, "GPU Hot Spot")
            .ShouldBe(SensorKeys.GpuHotspot("NVIDIA GeForce RTX 4090"));
    }

    /// <summary>Integrated graphics get their power from the sensor provider (the driver reports none).</summary>
    [Theory]
    [InlineData(HardwareType.GpuIntel, "Intel(R) Graphics", "GPU Power")]
    [InlineData(HardwareType.GpuNvidia, "NVIDIA GeForce RTX 4090", "GPU Package")]
    public void Gpu_power_is_keyed_by_adapter_name(HardwareType type, string hardware, string name)
    {
        LhmSensorProvider.CanonicalKey(type, hardware, SensorType.Power, name).ShouldBe(SensorKeys.GpuPower(hardware));
        LhmSensorProvider.CanonicalKey(type, hardware, SensorType.Power, "GPU Memory").ShouldBeNull();
    }

    [Fact]
    public void Clocks_and_loads_are_mapped_but_data_sizes_are_not()
    {
        LhmSensorProvider.MapKind(SensorType.Clock).ShouldBe(SensorKind.Clock);
        LhmSensorProvider.MapKind(SensorType.Fan).ShouldBe(SensorKind.Fan);
        LhmSensorProvider.MapKind(SensorType.Data).ShouldBeNull();
        LhmSensorProvider.MapKind(SensorType.Throughput).ShouldBeNull();
    }

    [Fact]
    public void Cpu_sensors_need_elevation_but_gpu_sensors_do_not()
    {
        using var provider = new LhmSensorProvider(new ElevationInfo(false), TimeProvider.System, NullLogger<LhmSensorProvider>.Instance);

        provider.PrerequisiteReason(HardwareType.Cpu).ShouldBe(Availability.RequiresElevation);
        provider.PrerequisiteReason(HardwareType.Motherboard).ShouldBe(Availability.RequiresElevation);
        provider.PrerequisiteReason(HardwareType.GpuNvidia).ShouldBeNull();
        provider.PrerequisiteReason(HardwareType.Cooler).ShouldBeNull();
    }

    [Fact]
    public void Intel_control_library_is_only_wanted_for_an_intel_discrete_gpu()
    {
        const uint intel = 0x8086, nvidia = 0x10DE;
        const long mb = 1 << 20;

        // The dev PC: RTX 4090, the Core Ultra's integrated graphics (128 MB) and WSL2's twin of the 4090.
        LhmSensorProvider.HasIntelDiscreteGpu([(nvidia, 24138 * mb), (intel, 128 * mb), (nvidia, 24138 * mb)]).ShouldBeFalse();
        LhmSensorProvider.HasIntelDiscreteGpu([(intel, 128 * mb), (intel, 4096 * mb)]).ShouldBeTrue(); // Arc A310 next to integrated graphics
        LhmSensorProvider.HasIntelDiscreteGpu([]).ShouldBeFalse();
    }

    [Fact]
    public void A_drive_behind_intel_rst_gets_its_health_from_lhm_under_the_storage_keys()
    {
        using var provider = new LhmSensorProvider(new ElevationInfo(true), TimeProvider.System, NullLogger<LhmSensorProvider>.Instance);

        // An NVMe health log as CrystalDiskInfo numbers its fields (data units are 512,000 bytes).
        var log = new Dictionary<byte, double> { [0x03] = 100, [0x05] = 2, [0x07] = 1000, [0x0C] = 21000, [0x0D] = 140, [0x0E] = 0 };
        var readings = provider.DriveReadings(1, 41, 98, 21000, log).ToDictionary(r => r.Sensor.LogicalKey, r => r.Value.Value);

        readings[SensorKeys.DiskTemperature(1)].ShouldBe(41);
        readings["disk/1/wear"].ShouldBe(2); // percentage used, from the log
        readings["disk/1/power_on_hours"].ShouldBe(21000);
        readings["disk/1/data_written"].ShouldBe(512_000_000);
        readings["disk/1/media_errors"].ShouldBe(0);
        readings.ShouldNotContainKey("disk/1/data_read"); // not in this log: left to the storage provider's reason

        // A SATA drive: wear from the remaining life.
        var sata = provider.DriveReadings(2, null, 93, 800, null).ToDictionary(r => r.Sensor.LogicalKey, r => r.Value.Value);
        sata["disk/2/wear"].ShouldBe(7);
        sata["disk/2/power_on_hours"].ShouldBe(800);
        sata.ShouldNotContainKey(SensorKeys.DiskTemperature(2));
        provider.DriveReadings(-1, 40, 100, 1, null).ShouldBeEmpty();
    }

    [Fact]
    public void An_rtx_50_hotspot_is_the_hottest_valid_thermal_channel()
    {
        const long valid = 1L << 30;

        // Channels as the PawnIO module returns them: bit 30 = valid, low 16 bits = 1/256 °C.
        PcDiag.Platform.Windows.Gpu.NvidiaThermalRegisters.Hotspot([valid | (74 * 256), valid | (81 * 256 + 128), 90 * 256, valid | (66 * 256), 0, 0]).ShouldBe(81.5);
        PcDiag.Platform.Windows.Gpu.NvidiaThermalRegisters.Hotspot([90 * 256, 0, 0, 0, 0, 0]).ShouldBeNull(); // nothing marked valid
        PcDiag.Platform.Windows.Gpu.NvidiaThermalRegisters.Hotspot([valid | 0xFFFF, valid, 0, 0, 0, 0]).ShouldBeNull(); // 255.99 °C and 0 °C are not readings
    }

    [Fact]
    public void Without_pawnio_no_adapter_gets_a_hotspot_from_the_thermal_registers()
    {
        using var provider = new PcDiag.Collectors.Gpu.PawnIoNvidiaGpuDetailProvider(() => (null, "the PawnIO driver is not available (error 2)"));
        var rtx = new PcDiag.Collectors.Gpu.GpuAdapterBinding("gpu-10de-2c05-1", new PcDiag.Platform.Windows.Gpu.DxgiAdapterInfo("NVIDIA GeForce RTX 5070 Ti", 0x10DE, 0x2C05, 0, 0, 16L << 30, 0, default, false), (1, 0, 0));

        provider.Attach([rtx], null!).ShouldBe("the PawnIO driver is not available (error 2)");
        provider.Supports(rtx).ShouldBeFalse();
        provider.Read(rtx).HotspotTemperatureC.HasValue.ShouldBeFalse();
    }

    [Fact]
    public void An_nvme_drive_takes_its_temperature_from_its_own_log_not_the_counters()
    {
        var provider = new StorageReliabilitySensorProvider(new ElevationInfo(true), TimeProvider.System);
        var log = new NvmeHealthInfo(0, 49, 100, 10, 0, 1e12, 2e12, 58, 2352, 22, 0, 0, 0, 0);
        // Behind Intel RST (VMD) the counters hold placeholders: 60 °C on every drive, whatever its temperature.
        var placeholder = new StorageReliabilityInfo(0, 60, null, null, null, null, null, IsNvme: true);

        var readings = provider.Readings(0, placeholder, log).ToDictionary(r => r.Sensor.LogicalKey, r => r.Value);
        readings[SensorKeys.DiskTemperature(0)].Value.ShouldBe(49);
        readings["disk/0/power_on_hours"].Value.ShouldBe(2352);

        // When the log does not reach the drive either, the temperature stays a gap for LibreHardwareMonitor to fill.
        var unreached = provider.Readings(1, placeholder with { DiskNumber = 1 }, null).ToDictionary(r => r.Sensor.LogicalKey, r => r.Value);
        unreached[SensorKeys.DiskTemperature(1)].HasValue.ShouldBeFalse();

        // A SATA drive's counters are its source.
        var sata = provider.Readings(2, new StorageReliabilityInfo(2, 35, 51, 3, 0, 0, 900), null).ToDictionary(r => r.Sensor.LogicalKey, r => r.Value);
        sata[SensorKeys.DiskTemperature(2)].Value.ShouldBe(35);
        sata["disk/2/wear"].Value.ShouldBe(3);
    }

    [Fact]
    public async Task Real_provider_never_reports_blocked_cpu_readings_as_values()
    {
        using var provider = new LhmSensorProvider(new ElevationInfo(false), TimeProvider.System, NullLogger<LhmSensorProvider>.Instance);
        await provider.InitializeAsync(CancellationToken.None);

        var snapshot = await provider.GetSnapshotAsync(CancellationToken.None);

        // Not elevated (the test host): CPU temperature/power must carry a reason, never a (zero) value.
        var package = snapshot.Readings.Where(r => r.Sensor.LogicalKey is SensorKeys.CpuPackageTemperature or SensorKeys.CpuPackagePower).ToList();
        package.ShouldNotBeEmpty();
        package.ShouldAllBe(r => !r.Value.HasValue && r.Value.Availability == Availability.RequiresElevation);
        snapshot.Readings.Where(r => r.Sensor.HardwareName.Contains("Core", StringComparison.Ordinal) && r.Sensor.Kind == SensorKind.Power)
            .ShouldAllBe(r => !r.Value.HasValue);
    }

    [Fact]
    public async Task Readings_come_back_at_once_and_are_refreshed_in_the_background()
    {
        using var provider = new LhmSensorProvider(new ElevationInfo(false), TimeProvider.System, NullLogger<LhmSensorProvider>.Instance);
        await provider.InitializeAsync(CancellationToken.None);

        // The sampling path never waits for the hardware (elevated, a CPU update alone takes 400–500 ms).
        var started = System.Diagnostics.Stopwatch.StartNew();
        var first = await provider.GetSnapshotAsync(CancellationToken.None);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(20));
        first.Readings.ShouldNotBeEmpty();

        HardwareSnapshot later = first;
        for (var i = 0; i < 30 && later.Timestamp == first.Timestamp; i++)
        {
            await Task.Delay(100);
            later = await provider.GetSnapshotAsync(CancellationToken.None);
        }

        later.Timestamp.ShouldBeGreaterThan(first.Timestamp);

        // Frames in the ring buffer share what did not change: every sensor keeps its descriptor, and a sensor that
        // reads as before (here at least the CPU readings blocked without elevation) is the same reading.
        // (LHM can give two sensors the same identifier, for example two NVIDIA loads: those are left out here.)
        static Dictionary<string, SensorReading> ByKey(HardwareSnapshot s) => s.Readings.Where(r => r.Sensor.LogicalKey.StartsWith("lhm", StringComparison.Ordinal))
            .GroupBy(r => r.Sensor.LogicalKey).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
        var before = ByKey(first);
        var after = ByKey(later).Values.Where(r => before.ContainsKey(r.Sensor.LogicalKey)).ToList();
        after.ShouldNotBeEmpty();
        after.ShouldAllBe(r => ReferenceEquals(r.Sensor, before[r.Sensor.LogicalKey].Sensor));
        after.Where(r => r.Value == before[r.Sensor.LogicalKey].Value).ShouldAllBe(r => ReferenceEquals(r, before[r.Sensor.LogicalKey]));
        after.ShouldContain(r => ReferenceEquals(r, before[r.Sensor.LogicalKey]));
    }
}

public class SensorMergeTests
{
    private static SensorReading R(string key, double? value, Availability reason = Availability.Unsupported, string provider = "p") =>
        new(new SensorDescriptor(provider, "hw", SensorKind.Temperature, key, "°C", key),
            value is { } v ? Reading<double>.Of(v) : Reading<double>.Unavailable(reason));

    private static HardwareSnapshot S(string provider, params SensorReading[] readings) => new(DateTimeOffset.UnixEpoch, provider, readings);

    [Fact]
    public void Highest_priority_value_wins_and_values_beat_gaps()
    {
        var merged = SensorCollector.Merge([
            (60, S("storage", R("disk/0/temperature", 41), R("disk/1/temperature", null, Availability.RequiresElevation))),
            (50, S("lhm", R("disk/0/temperature", 39), R("disk/1/temperature", 45))),
        ]).ToDictionary(r => r.Sensor.LogicalKey);

        merged["disk/0/temperature"].Value.ShouldBe(Reading<double>.Of(41));
        merged["disk/1/temperature"].Value.ShouldBe(Reading<double>.Of(45)); // lower priority, but it has a value
    }

    [Fact]
    public void Without_any_value_the_most_specific_reason_is_kept()
    {
        var merged = SensorCollector.Merge([
            (60, S("a", R("cpu/package/temperature", null, Availability.Unsupported))),
            (50, S("b", R("cpu/package/temperature", null, Availability.DriverMissing))),
        ]).Single();

        merged.Value.Availability.ShouldBe(Availability.DriverMissing);
    }

    [Fact]
    public async Task Providers_are_read_on_their_own_interval()
    {
        var time = new FakeTimeProvider();
        var fast = new CountingProvider("fast", TimeSpan.FromSeconds(1));
        var slow = new CountingProvider("slow", TimeSpan.FromSeconds(60));
        var collector = new SensorCollector([fast, slow], time, NullLogger<SensorCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        for (var i = 0; i < 10; i++)
        {
            var sample = await collector.CollectAsync(new SampleContext(i + 1, time.GetUtcNow(), TimeSpan.FromSeconds(1), false), CancellationToken.None);
            sample.Readings.Count.ShouldBe(2); // the slow provider's last snapshot is reused
            time.Advance(TimeSpan.FromSeconds(1));
        }

        fast.Reads.ShouldBe(10);
        slow.Reads.ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_provider_does_not_break_the_others()
    {
        var collector = new SensorCollector([new CountingProvider("ok", TimeSpan.Zero), new CountingProvider("bad", TimeSpan.Zero) { Fail = true }], new FakeTimeProvider(), NullLogger<SensorCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        var sample = await collector.CollectAsync(new SampleContext(1, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), false), CancellationToken.None);

        sample.Readings.Single().Sensor.ProviderId.ShouldBe("ok");
    }

    private sealed class CountingProvider(string id, TimeSpan interval) : IHardwareSensorProvider
    {
        public int Reads;

        public bool Fail { get; init; }

        public string ProviderId => id;

        public int Priority => 1;

        public TimeSpan RefreshInterval => interval;

        public ValueTask<string> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult("ok");

        public ValueTask<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (Fail)
            {
                throw new InvalidOperationException("sensor chip went away");
            }

            return ValueTask.FromResult(new HardwareSnapshot(DateTimeOffset.UnixEpoch, id, [R(id + "/t", 40, provider: id)]));
        }
    }
}

public class InventoryAndReliabilityTests
{
    [Fact]
    public void Inventory_contains_os_board_and_drivers_but_no_serial_numbers()
    {
        var inventory = SystemInventory.Read(includeComputerName: false);

        inventory.System.ShouldContainKey("os.name");
        inventory.System.ShouldContainKey("os.build");
        inventory.System.ShouldContainKey("cpu.0.name");
        inventory.System.Keys.ShouldNotContain(k => k.Contains("serial", StringComparison.OrdinalIgnoreCase));
        inventory.System.Keys.ShouldNotContain("system.computer_name");
        inventory.HardwareJson.ShouldNotContain("SerialNumber");
        inventory.Drivers.ShouldContain(d => d.DeviceClass.Equals("Display", StringComparison.OrdinalIgnoreCase));
        inventory.Drivers.ShouldAllBe(d => SystemInventory.DriverClasses.Contains(d.DeviceClass));

        // The motherboard and the device register (every PC has at least a display or an input device).
        inventory.System.ShouldContainKey("firmware.type");
        inventory.System.ShouldContainKey("cpu.virtualization");
        inventory.System.ShouldContainKey("devices.connected");
        inventory.IoDevices.ShouldNotBeEmpty();
        inventory.IoDevices.ShouldContain(d => d.Present && !d.Technical);
        inventory.IoDevices.Select(d => d.Key).ShouldBeUnique();
    }

    [Fact]
    public void The_device_tree_is_read_quickly_and_device_changes_can_be_watched()
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var present = PcDiag.Platform.Windows.Devices.DeviceTree.PresentProblems();
        present.Count.ShouldBeGreaterThan(20);
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));

        var node = PcDiag.Platform.Windows.Devices.DeviceTree.Read(present.Keys.First(k => k.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)))!;
        node.Present.ShouldBeTrue();
        node.Enumerator.ShouldBe("PCI", StringCompareShould.IgnoreCase);

        using var watcher = PcDiag.Platform.Windows.Devices.DeviceNotifications.Start(_ => { }, out var error);
        watcher.ShouldNotBeNull($"CONFIGRET {error}");
        watcher.Mode.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Storage_reliability_reports_access_denied_rather_than_throwing_when_not_elevated()
    {
        var result = StorageReliability.Read();

        (result.AccessDenied || result.Disks.Count > 0).ShouldBeTrue();
    }
}
