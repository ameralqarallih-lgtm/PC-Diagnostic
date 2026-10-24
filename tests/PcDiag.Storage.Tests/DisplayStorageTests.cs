using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public sealed class DisplayStorageTests : IDisposable
{
    private const string Key = @"DISPLAY\AUS32F4\5&12862C70&0&UID4353";

    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    private DisplaySnapshot Snapshot(double hz) => new(_time.GetUtcNow(),
    [
        new DisplayTarget(Key, "PG32UCDP", "AUS", "32F4", 3840, 2160, hz, DisplayConnection.DisplayPort,
            Reading<bool>.Of(false), Reading<bool>.Of(true), Reading<bool>.Of(true), 0, true, 0, 0, "gpu-10de", "NVIDIA GeForce RTX 4090"),
    ]);

    [Fact]
    public async Task Configurations_are_stored_when_they_change_and_every_change_is_stored()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);

        await using (var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance))
        {
            await consumer.ConsumeAsync(Frame(1, new DisplaySample(_time.GetUtcNow(), Snapshot(240), [])), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));

            // Same configuration again (e.g. after a recording restart): not stored twice.
            await consumer.ConsumeAsync(Frame(2, new DisplaySample(_time.GetUtcNow(), Snapshot(240), [])), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));

            var change = new DisplayChange(_time.GetUtcNow(), DisplayEventKind.RefreshRateChanged, "PG32UCDP", "240 Hz", "60 Hz", Key);
            await consumer.ConsumeAsync(Frame(3, new DisplaySample(_time.GetUtcNow(), Snapshot(60), [change])), CancellationToken.None);
            await consumer.FlushAsync(CancellationToken.None);
        }

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM DisplaySnapshots;")).ShouldBe(2L);
        (await ScalarAsync(c, "SELECT json FROM DisplaySnapshots ORDER BY ts DESC LIMIT 1;")).ShouldBeOfType<string>().ShouldContain("\"RefreshRateHz\":60");
        (await ScalarAsync(c, $"SELECT COUNT(*) FROM DisplayEvents WHERE kind = {(int)DisplayEventKind.RefreshRateChanged} AND old_value = '240 Hz' AND new_value = '60 Hz';")).ShouldBe(1L);
        (await ScalarAsync(c, "SELECT target_key FROM DisplayEvents;")).ShouldBe(Key);
    }

    private SampleFrame Frame(long seq, DisplaySample display) => new(seq, _time.GetUtcNow(), Display: display);

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
