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

public sealed class DiskNetworkStorageTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Disks_networks_and_volumes_are_written_with_devices()
    {
        var paths = await InitializeAsync();
        await using var consumer = CreateConsumer(paths);

        await consumer.ConsumeAsync(Frame(1, freeBytes: 500L << 30), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM Devices WHERE kind = 'disk';")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM Devices WHERE kind = 'network';")).ShouldBe(2L);
        (await ScalarAsync(c, "SELECT write_lat_ms FROM DiskMetrics;")).ShouldBe(0.5);
        (await ScalarAsync(c, "SELECT temp_c FROM DiskMetrics;")).ShouldBe(DBNull.Value);
        (await ScalarAsync(c, "SELECT free_b FROM VolumeMetrics WHERE mount = 'C:\\';")).ShouldBe(500L << 30);
        (await ScalarAsync(c, "SELECT disk_id IS NOT NULL FROM VolumeMetrics;")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT disk_active_pct FROM SystemSnapshots;")).ShouldBe(40.0);

        // Headline network counts the physical NIC only (the virtual adapter's traffic is also on the NIC).
        (await ScalarAsync(c, "SELECT net_bps FROM SystemSnapshots;")).ShouldBe((1000.0 + 500.0) * 8);
    }

    [Fact]
    public async Task Volume_rows_are_written_only_on_change_or_once_a_minute()
    {
        var paths = await InitializeAsync();
        await using var consumer = CreateConsumer(paths);

        for (var i = 1; i <= 5; i++)
        {
            await consumer.ConsumeAsync(Frame(i, freeBytes: 500L << 30), CancellationToken.None); // unchanged
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await consumer.ConsumeAsync(Frame(6, freeBytes: (500L << 30) - (10L << 20)), CancellationToken.None); // 10 MB used
        _time.Advance(TimeSpan.FromSeconds(61));
        await consumer.ConsumeAsync(Frame(7, freeBytes: (500L << 30) - (10L << 20)), CancellationToken.None); // a minute later
        await consumer.FlushAsync(CancellationToken.None);

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM VolumeMetrics;")).ShouldBe(3L);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM DiskMetrics;")).ShouldBe(7L);
    }

    private TelemetryStorageConsumer CreateConsumer(DataPaths paths) =>
        new(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);

    private async Task<DataPaths> InitializeAsync()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        return paths;
    }

    private SampleFrame Frame(long sequence, long freeBytes)
    {
        var ts = _time.GetUtcNow();
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        var descriptor = new DiskDescriptor("disk-abc", 1, "Samsung SSD 970 EVO 1TB", "NVMe", StorageMediaKind.Ssd, 1L << 40, false, "Healthy");
        var disk = new PhysicalDiskSample(descriptor, ["C:"], Reading<double>.Of(1e6), Reading<double>.Of(2e6), Reading<double>.Of(10), Reading<double>.Of(20),
            Reading<double>.Of(0.1), Reading<double>.Of(0.5), Reading<double>.Of(0.2), Reading<double>.Of(40), none);
        var volume = new VolumeSample(@"C:\", "disk-abc", null, "NTFS", "Fixed", Reading<long>.Of(930L << 30), Reading<long>.Of(freeBytes));

        NetworkInterfaceSample Nic(string key, NetworkInterfaceKind kind, double rx, double tx) => new(
            new NetworkInterfaceDescriptor(key, key, key, kind, null),
            Reading<double>.Of(rx), Reading<double>.Of(tx), Reading<double>.Of(1), Reading<double>.Of(1),
            Reading<long>.Of(0), Reading<long>.Of(0), Reading<long>.Of(0), Reading<long>.Of(0),
            Reading<long>.Of(1_000_000_000), Reading<double>.Of(0.01), true, none, none);

        return new SampleFrame(
            sequence,
            ts,
            Disk: new DiskSample(ts, [disk], [volume]),
            Network: new NetworkSample(ts, [Nic("{nic}", NetworkInterfaceKind.Ethernet, 1000, 500), Nic("{veth}", NetworkInterfaceKind.Virtual, 900, 400)]));
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class Monitor : IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue { get; } = new();

        public StorageOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }
}
