using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public class SensorDeadbandTests
{
    private static SensorReading R(string key, SensorKind kind, double value, string? aliasOf = null) =>
        new(new SensorDescriptor("lhm", "hw", kind, key, "", key, aliasOf), Reading<double>.Of(value));

    [Fact]
    public void Small_changes_are_suppressed_until_the_heartbeat()
    {
        var deadband = new SensorDeadband();

        deadband.ShouldWrite(R("t", SensorKind.Temperature, 40.0), 0).ShouldBeTrue();
        deadband.ShouldWrite(R("t", SensorKind.Temperature, 40.3), 1000).ShouldBeFalse();
        deadband.ShouldWrite(R("t", SensorKind.Temperature, 40.6), 2000).ShouldBeTrue();  // ≥ 0.5 °C
        deadband.ShouldWrite(R("t", SensorKind.Temperature, 40.6), 11_000).ShouldBeFalse(); // 9 s since the last write: not yet
        deadband.ShouldWrite(R("t", SensorKind.Temperature, 40.6), 12_000).ShouldBeTrue(); // heartbeat
    }

    [Fact]
    public void Aliases_clocks_loads_and_gaps_are_not_persisted()
    {
        var deadband = new SensorDeadband();

        deadband.ShouldWrite(R("cpu/package/temperature", SensorKind.Temperature, 50, aliasOf: "lhm/intelcpu/0/temperature/0"), 0).ShouldBeFalse();
        deadband.ShouldWrite(R("c", SensorKind.Clock, 5000), 0).ShouldBeFalse();
        deadband.ShouldWrite(R("l", SensorKind.Load, 50), 0).ShouldBeFalse();
        deadband.ShouldWrite(new SensorReading(new SensorDescriptor("lhm", "hw", SensorKind.Fan, "f", "RPM", "f"), Reading<double>.Unavailable(Availability.DriverMissing)), 0).ShouldBeFalse();
    }

    [Fact]
    public void Collector_values_win_and_sensor_reasons_fill_gaps()
    {
        var sensors = new Dictionary<string, SensorReading>
        {
            [SensorKeys.CpuPackageTemperature] = new(new SensorDescriptor("lhm", "CPU", SensorKind.Temperature, "CPU Package", "°C", SensorKeys.CpuPackageTemperature), Reading<double>.Unavailable(Availability.DriverMissing)),
        };

        TelemetryStorageConsumer.FromSensors(Reading<double>.Of(70), sensors, SensorKeys.CpuPackageTemperature).ShouldBe(Reading<double>.Of(70));
        TelemetryStorageConsumer.FromSensors(Reading<double>.Unavailable(Availability.Unsupported), sensors, SensorKeys.CpuPackageTemperature)
            .Availability.ShouldBe(Availability.DriverMissing);
        TelemetryStorageConsumer.FromSensors(Reading<double>.Unavailable(Availability.Unsupported), null, SensorKeys.CpuPackageTemperature)
            .Availability.ShouldBe(Availability.Unsupported);
    }
}

public sealed class SensorInventoryStorageTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Sensors_and_changed_inventory_only_are_persisted()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        await using var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);

        var drivers = new List<DriverInfo> { new("Display", "NVIDIA GeForce RTX 4090", "PCI\\VEN_10DE", "NVIDIA", "32.0.16.1714", "2026-09-01", "oem12.inf", "Microsoft Windows Hardware Compatibility Publisher") };
        var inventory = new InventorySample(_time.GetUtcNow(), new Dictionary<string, string> { ["os.build"] = "26200" }, "{\"cpus\":[]}", drivers);

        await consumer.ConsumeAsync(Frame(1, inventory), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await consumer.ConsumeAsync(Frame(2, inventory), CancellationToken.None); // identical: nothing new stored
        _time.Advance(TimeSpan.FromSeconds(1));
        var updated = inventory with { Drivers = [drivers[0] with { Version = "32.0.16.1800" }] };
        await consumer.ConsumeAsync(Frame(3, updated), CancellationToken.None); // driver update: new snapshot
        await consumer.FlushAsync(CancellationToken.None);

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM HardwareSnapshots;")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM Drivers;")).ShouldBe(2L);
        (await ScalarAsync(c, "SELECT COUNT(DISTINCT snapshot_ts) FROM Drivers;")).ShouldBe(2L);
        (await ScalarAsync(c, "SELECT value FROM SystemInformation WHERE key = 'os.build';")).ShouldBe("26200");

        // Sensors: the raw series is stored (deadband: first value only), the alias is not.
        (await ScalarAsync(c, "SELECT COUNT(*) FROM SensorCatalog;")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM SensorReadings;")).ShouldBe(1L);

        // The CPU package column takes the sensor value when the CPU collector has none.
        (await ScalarAsync(c, "SELECT package_temp_c FROM CpuMetrics ORDER BY ts LIMIT 1;")).ShouldBe(61.5);
    }

    [Fact]
    public async Task The_PC_description_is_stored_also_when_nothing_is_recorded()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        await using var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);
        var drivers = new List<DriverInfo> { new("Display", "NVIDIA GeForce RTX 4090", "PCI\\VEN_10DE", "NVIDIA", "32.0.16.1714", "2026-09-01", "oem12.inf", "Microsoft Windows Hardware Compatibility Publisher") };
        var inventory = new InventorySample(_time.GetUtcNow(), new Dictionary<string, string> { ["os.build"] = "26200" }, "{\"cpus\":[]}", drivers);

        // Watched (not recording): a fresh installation's System page had no hardware, drivers or devices (16 Oct 2026).
        await consumer.ConsumeAsync(Frame(1, inventory) with { Recorded = false, Watched = true }, CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT value FROM SystemInformation WHERE key = 'os.build';")).ShouldBe("26200");
        (await ScalarAsync(c, "SELECT COUNT(*) FROM HardwareSnapshots;")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM Drivers;")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM CpuMetrics;")).ShouldBe(0L, "no telemetry without recording");
        (await ScalarAsync(c, "SELECT COUNT(*) FROM SensorReadings;")).ShouldBe(0L);
    }

    [Fact]
    public async Task The_device_register_keeps_every_device_and_notices_the_ones_that_are_gone()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        await using var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);

        var receiver = Device("id:046D:C54D", "LIGHTSPEED Receiver", "keyboard", present: true);
        var earbuds = Device("bt:E8C913314F45", "Galaxy Buds3 Pro", "audio", present: false);
        var inventory = new InventorySample(_time.GetUtcNow(), new Dictionary<string, string>(), "{}", [], [receiver, earbuds]);
        await consumer.ConsumeAsync(Frame(1, inventory), CancellationToken.None);
        var registered = _time.GetUtcNow().ToUnixTimeMilliseconds();

        // The receiver is unplugged; the earbuds connect; an old device is no longer listed at all.
        _time.Advance(TimeSpan.FromMinutes(5));
        await consumer.ConsumeAsync(Frame(2, inventory with { Timestamp = _time.GetUtcNow(), IoDevices = [receiver with { Present = false, Connected = 0 }, earbuds with { Present = true, Connected = 1 }] }),
            CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        var overview = await new Sqlite.Reading.SystemReader(_factory).OverviewAsync(paths.TelemetryDatabase, CancellationToken.None);
        var rows = overview.IoDevices.ShouldNotBeNull();
        rows.Count.ShouldBe(2);
        var unplugged = rows.Single(r => r.Device.Key == "id:046D:C54D");
        unplugged.Device.Present.ShouldBeFalse();
        unplugged.FirstSeen.ShouldBe(registered);
        unplugged.LastSeen.ShouldBe(_time.GetUtcNow().ToUnixTimeMilliseconds()); // noticed gone now
        unplugged.Device.Kinds.ShouldBe(["keyboard", "mouse"]);
        unplugged.Device.Parts.ShouldHaveSingleItem().Name.ShouldBe("HID Keyboard Device");
        rows.Single(r => r.Device.Key == "bt:E8C913314F45").Device.Present.ShouldBeTrue();

        var sensors = await new Sqlite.Reading.SystemReader(_factory).SensorsAsync(paths.TelemetryDatabase, "lhm/intelcpu/", CancellationToken.None);
        sensors.ShouldHaveSingleItem().Value.ShouldBe(61.5);
        (await new Sqlite.Reading.SystemReader(_factory).SensorsAsync(paths.TelemetryDatabase, "lhm/lpc/", CancellationToken.None)).ShouldBeEmpty();
    }

    private static Core.Devices.IoDevice Device(string key, string name, string kind, bool present) =>
        new(key, name, kind, kind == "keyboard" ? ["keyboard", "mouse"] : [kind], null, "USB", "USB", "Logitech", "046D", "C54D", present, present ? 1 : 0, true, false, 0, null, @"USB\VID_046D&PID_C54D\1",
            "Port_#0003.Hub_#0001", "usbccgp", "Microsoft", "10.0.26100.1", "2006-06-21", "usb.inf", @"USB\VID_046D&PID_C54D&REV_1404", DateTimeOffset.UnixEpoch.AddYears(55), null, null,
            [new Core.Devices.IoDevicePart(@"HID\VID_046D&PID_C54D&MI_01&COL01\1", "HID Keyboard Device", "keyboard", "Keyboard", present, false, 0, "kbdhid", "Microsoft", "10.0.26100.1", "2006-06-21")]);

    private SampleFrame Frame(long seq, InventorySample inventory)
    {
        var ts = _time.GetUtcNow();
        var raw = new SensorDescriptor("lhm", "Intel Core Ultra 9 285K", SensorKind.Temperature, "CPU Package", "°C", "lhm/intelcpu/0/temperature/0");
        var sensors = new SensorSample(ts,
        [
            new SensorReading(raw, Reading<double>.Of(61.5)),
            new SensorReading(raw with { LogicalKey = SensorKeys.CpuPackageTemperature, AliasOf = raw.LogicalKey }, Reading<double>.Of(61.5)),
        ]);
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        var cpu = new CpuSample(ts, Reading<double>.Of(10), none, none, none, none, none, none, Reading<long>.Unavailable(Availability.Unsupported), none, none, none, none, none, []);
        return new SampleFrame(seq, ts, Cpu: cpu, Sensors: sensors, Inventory: inventory);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class Monitor : IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue { get; } = new() { WriteBehindSeconds = 60 };

        public StorageOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }
}
