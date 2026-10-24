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

public sealed class GpuStorageTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_1_databases_are_upgraded_and_keep_their_data()
    {
        var path = _dir.File("telemetry.db");
        await using (var v1 = await _factory.OpenReadWriteAsync(path, CancellationToken.None))
        {
            await ExecAsync(v1, MigrationCatalog.LoadScript("Telemetry.0001_initial.sql"));
            await ExecAsync(v1, "CREATE TABLE SchemaVersion (version INTEGER NOT NULL); INSERT INTO SchemaVersion VALUES (1);");
            await ExecAsync(v1, "INSERT INTO GpuMetrics (ts, adapter_id, util_pct, vram_budget_b) VALUES (1, 1, 50, 24000);");
        }

        await using var connection = await _factory.OpenReadWriteAsync(path, CancellationToken.None);
        var version = await new SchemaMigrator(NullLogger<SchemaMigrator>.Instance).MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None);

        version.ShouldBe(MigrationCatalog.LatestVersion(DatabaseKind.Telemetry));
        (await ScalarAsync(connection, "SELECT vram_total_b FROM GpuMetrics WHERE ts = 1;")).ShouldBe(24000L);
        (await ScalarAsync(connection, "SELECT power_pct FROM GpuMetrics WHERE ts = 1;")).ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task Gpu_adapters_are_written_with_a_device_row_each()
    {
        var paths = await InitializeAsync();
        await using var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);

        await consumer.ConsumeAsync(Frame(1), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await consumer.ConsumeAsync(Frame(2), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        await using var connection = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM Devices WHERE kind = 'gpu';")).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM GpuMetrics;")).ShouldBe(4L);
        ((string)(await ScalarAsync(connection, "SELECT details_json FROM Devices WHERE stable_key = 'gpu-dgpu';"))!).ShouldContain("\"temperatureWarningC\":84");
        (await ScalarAsync(connection, "SELECT last_seen FROM Devices WHERE stable_key = 'gpu-dgpu';")).ShouldBe(UnixTime.ToUnixMs(_time.GetUtcNow()));

        // Unavailable values are NULL with a reason, never 0.
        (await ScalarAsync(connection, "SELECT temp_c FROM GpuMetrics g JOIN Devices d ON d.device_id = g.adapter_id WHERE d.stable_key = 'gpu-igpu' LIMIT 1;")).ShouldBe(DBNull.Value);
        (await ScalarAsync(connection, "SELECT availability FROM SensorAvailability WHERE series = 'gpu.gpu-igpu.temp_c';")).ShouldBe((long)Availability.Unsupported);

        // Headline values: busiest adapter for GPU %, largest-VRAM adapter for VRAM %.
        (await ScalarAsync(connection, "SELECT gpu_pct FROM SystemSnapshots ORDER BY ts LIMIT 1;")).ShouldBe(60.0);
        (await ScalarAsync(connection, "SELECT vram_pct FROM SystemSnapshots ORDER BY ts LIMIT 1;")).ShouldBe(25.0);
    }

    private async Task<DataPaths> InitializeAsync()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        return paths;
    }

    private SampleFrame Frame(long sequence)
    {
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        GpuAdapterSample Adapter(string key, long totalBytes, long usedBytes, double util, Reading<double> temp, Reading<double> warning) => new(
            new GpuAdapterDescriptor(key, key, "Vendor", 1, 2, totalBytes, 0, Reading<double>.Of(90), warning, []),
            Reading<double>.Of(util), Reading<double>.Of(util), none, none, none, none,
            Reading<long>.Of(usedBytes), Reading<long>.Of(totalBytes), Reading<long>.Of(0),
            temp, none, none, none, none, none, none, Reading<long>.Unavailable(Availability.Unsupported), none);

        var gpu = new GpuSample(_time.GetUtcNow(),
        [
            Adapter("gpu-dgpu", 24L << 30, 6L << 30, 40, Reading<double>.Of(45), Reading<double>.Of(84)),
            Adapter("gpu-igpu", 128L << 20, 0, 60, none, none),
        ], []);
        return new SampleFrame(sequence, _time.GetUtcNow(), Gpu: gpu);
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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
