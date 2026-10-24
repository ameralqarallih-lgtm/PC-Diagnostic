using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Migrations;

namespace PcDiag.Storage.Tests;

public sealed class SchemaMigrationTests : IDisposable
{
    private static readonly string[] TelemetryTables =
    [
        "SystemSnapshots", "CpuMetrics", "MemoryMetrics", "GpuMetrics", "DiskMetrics", "NetworkMetrics",
        "ProcessIdentity", "ProcessSnapshots", "ProcessEvents", "ServiceSnapshots", "ServiceEvents",
        "HardwareSnapshots", "DisplaySnapshots", "DisplayEvents", "WindowsEvents", "Anomalies", "Drivers",
        "SystemInformation", "Devices", "SensorCatalog", "SensorReadings", "SensorAvailability",
        "ExtensionSeries", "ExtensionMetrics", "RollupSeries", "Rollup1m", "AgentHeartbeat", "RecorderSessions", "SchemaVersion",
    ];

    private static readonly string[] IncidentOnlyTables = ["IncidentInfo", "IncidentEvents", "Findings"];

    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly SchemaMigrator _migrator = new(NullLogger<SchemaMigrator>.Instance);

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Telemetry_database_gets_every_documented_table()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_dir.File("telemetry.db"), CancellationToken.None);

        var version = await _migrator.MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None);

        version.ShouldBe(MigrationCatalog.LatestVersion(DatabaseKind.Telemetry));
        TelemetryTables.ShouldBeSubsetOf(await TablesAsync(connection));
    }

    [Fact]
    public async Task Incident_database_has_telemetry_schema_plus_timeline_and_findings()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_dir.File("incident.db"), CancellationToken.None);

        await _migrator.MigrateAsync(connection, DatabaseKind.Incident, CancellationToken.None);

        var tables = await TablesAsync(connection);
        TelemetryTables.ShouldBeSubsetOf(tables);
        IncidentOnlyTables.ShouldBeSubsetOf(tables);
    }

    [Fact]
    public async Task Incident_index_uses_autoincrement_so_numbers_are_never_reused()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_dir.File("incidents.db"), CancellationToken.None);
        await _migrator.MigrateAsync(connection, DatabaseKind.IncidentIndex, CancellationToken.None);

        await ExecAsync(connection, InsertIncident("INC-20260928-00001"));
        await ExecAsync(connection, InsertIncident("INC-20260928-00002"));
        await ExecAsync(connection, "DELETE FROM Incidents WHERE id = 2;");
        await ExecAsync(connection, InsertIncident("INC-20260928-00003"));

        (await ScalarAsync(connection, "SELECT MAX(id) FROM Incidents;")).ShouldBe(3L);
    }

    [Fact]
    public async Task Migrating_twice_is_a_no_op()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_dir.File("telemetry.db"), CancellationToken.None);

        await _migrator.MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None);
        await ExecAsync(connection, "INSERT INTO SystemSnapshots (ts, seq, cpu_pct) VALUES (1, 1, 12.5);");
        var version = await _migrator.MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None);

        version.ShouldBe(MigrationCatalog.LatestVersion(DatabaseKind.Telemetry));
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM SystemSnapshots;")).ShouldBe(1L);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM SchemaVersion;")).ShouldBe(1L);
    }

    [Fact]
    public async Task Uses_wal_and_incremental_vacuum()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_dir.File("telemetry.db"), CancellationToken.None);
        await _migrator.MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None);

        (await ScalarAsync(connection, "PRAGMA journal_mode;")).ShouldBe("wal");
        (await ScalarAsync(connection, "PRAGMA auto_vacuum;")).ShouldBe(2L); // 2 = INCREMENTAL
        (await ScalarAsync(connection, "PRAGMA foreign_keys;")).ShouldBe(1L);
    }

    [Fact]
    public async Task Refuses_databases_from_a_newer_version()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_dir.File("telemetry.db"), CancellationToken.None);
        await _migrator.MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None);
        await ExecAsync(connection, "UPDATE SchemaVersion SET version = 999;");

        var ex = await Should.ThrowAsync<SchemaVersionException>(() => _migrator.MigrateAsync(connection, DatabaseKind.Telemetry, CancellationToken.None));
        ex.FoundVersion.ShouldBe(999);
    }

    [Fact]
    public async Task Initializer_creates_folders_and_both_databases()
    {
        var initializer = CreateInitializer();

        await initializer.InitializeAsync(CancellationToken.None);

        Directory.Exists(Path.Combine(_dir.Path, "incidents")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_dir.Path, "logs")).ShouldBeTrue();
        File.Exists(_dir.File("telemetry.db")).ShouldBeTrue();
        File.Exists(_dir.File("incidents.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task Corrupt_telemetry_database_is_quarantined_and_rebuilt()
    {
        await File.WriteAllTextAsync(_dir.File("telemetry.db"), new string('x', 8192));

        await CreateInitializer().InitializeAsync(CancellationToken.None);

        Directory.GetFiles(_dir.Path, "telemetry.corrupt-*.db").Length.ShouldBe(1);
        await using var connection = await _factory.OpenReadOnlyAsync(_dir.File("telemetry.db"), CancellationToken.None);
        (await SchemaMigrator.GetVersionAsync(connection, CancellationToken.None)).ShouldBe(MigrationCatalog.LatestVersion(DatabaseKind.Telemetry));
    }

    [Fact]
    public async Task Corrupt_incident_index_is_never_replaced()
    {
        await File.WriteAllTextAsync(_dir.File("incidents.db"), new string('x', 8192));

        await Should.ThrowAsync<SqliteException>(() => CreateInitializer().InitializeAsync(CancellationToken.None));
        _factory.ReleasePooledConnections(_dir.File("incidents.db"));

        (await File.ReadAllTextAsync(_dir.File("incidents.db"))).ShouldStartWith("xxxx");
    }

    [Fact]
    public async Task Readers_can_query_while_a_writer_holds_a_transaction()
    {
        await CreateInitializer().InitializeAsync(CancellationToken.None);

        await using var writer = await _factory.OpenReadWriteAsync(_dir.File("telemetry.db"), CancellationToken.None);
        await using var transaction = (SqliteTransaction)await writer.BeginTransactionAsync();
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO SystemSnapshots (ts, seq) VALUES (1, 1);";
            await insert.ExecuteNonQueryAsync();
        }

        await using var reader = await _factory.OpenReadOnlyAsync(_dir.File("telemetry.db"), CancellationToken.None);
        (await ScalarAsync(reader, "SELECT COUNT(*) FROM SystemSnapshots;")).ShouldBe(0L); // uncommitted data is not visible

        await transaction.CommitAsync();
        (await ScalarAsync(reader, "SELECT COUNT(*) FROM SystemSnapshots;")).ShouldBe(1L);
    }

    private DatabaseInitializer CreateInitializer() =>
        new(new DataPaths(_dir.Path), _factory, _migrator, TimeProvider.System, NullLogger<DatabaseInitializer>.Instance);

    private static string InsertIncident(string key) =>
        $"INSERT INTO Incidents (incident_key, created_ts, center_ts, pre_ms, post_ms, state, source, title, severity, folder) " +
        $"VALUES ('{key}', 0, 0, 600000, 120000, 0, 0, 'Test', 0, '{key}');";

    private static async Task<HashSet<string>> TablesAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
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
}
