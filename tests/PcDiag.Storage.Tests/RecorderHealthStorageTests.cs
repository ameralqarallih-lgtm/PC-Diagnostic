using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public sealed class RecorderHealthStorageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly DataPaths _paths;

    public RecorderHealthStorageTests()
    {
        _paths = new DataPaths(_dir.Path);
        new DatabaseInitializer(_paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), new FakeTimeProvider(Now), NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose() => _dir.Dispose();

    private TelemetryMaintenance Maintenance(int retentionHours = 24, int rollupDays = 30) => new(_paths, _factory,
        new Monitor(new StorageOptions { TelemetryRetentionHours = retentionHours, RollupRetentionDays = rollupDays }), NullLogger<TelemetryMaintenance>.Instance);

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    [Fact]
    public async Task Complete_minutes_are_rolled_up_once_with_min_avg_max()
    {
        var minute = Now.AddMinutes(-5);
        await ExecAsync(
            $"""
            INSERT INTO SystemSnapshots (ts, seq, cpu_pct) VALUES ({Ms(minute)}, 1, 10), ({Ms(minute.AddSeconds(20))}, 2, 30), ({Ms(minute.AddSeconds(40))}, 3, 20),
                                                                  ({Ms(minute.AddSeconds(50))}, 4, NULL), ({Ms(minute.AddMinutes(1))}, 5, 50);
            INSERT INTO GpuMetrics (ts, adapter_id, util_pct) VALUES ({Ms(minute)}, 7, 50), ({Ms(minute.AddSeconds(30))}, 7, 70), ({Ms(minute)}, 8, 5);
            """);
        var maintenance = Maintenance();

        var minutes = await maintenance.RollUpAsync(minute.AddMinutes(2).AddSeconds(20), CancellationToken.None);

        minutes.ShouldBe(2);
        (await RollupAsync("cpu/total_pct", minute)).ShouldBe((10.0, 20.0, 30.0));
        (await RollupAsync("cpu/total_pct", minute.AddMinutes(1))).ShouldBe((50.0, 50.0, 50.0));
        (await RollupAsync("gpu/7/util_pct", minute)).ShouldBe((50.0, 60.0, 70.0));
        (await RollupAsync("gpu/8/util_pct", minute)).ShouldBe((5.0, 5.0, 5.0));

        // Nothing new: nothing rolled again; the minute in progress is left for later.
        (await maintenance.RollUpAsync(minute.AddMinutes(2).AddSeconds(30), CancellationToken.None)).ShouldBe(0);
        (await ScalarAsync("SELECT COUNT(*) FROM Rollup1m;")).ShouldBe(4L);
    }

    /// <summary>The recorder's memory over days (a soak test, or a slow leak) outlives the 24 h of raw samples.</summary>
    [Fact]
    public async Task The_recorder_working_set_is_kept_as_one_minute_summaries()
    {
        var minute = Now.AddMinutes(-5);
        await ExecAsync($"INSERT INTO SystemSnapshots (ts, seq, agent_ws_bytes) VALUES ({Ms(minute)}, 1, 180000000), ({Ms(minute.AddSeconds(30))}, 2, 190000000);");

        await Maintenance().RollUpAsync(minute.AddMinutes(1).AddSeconds(20), CancellationToken.None);

        (await RollupAsync("agent/ws_bytes", minute)).ShouldBe((180000000.0, 185000000.0, 190000000.0));
    }

    [Fact]
    public async Task Retention_keeps_raw_data_for_hours_events_for_days_and_always_the_latest_snapshots()
    {
        await ExecAsync(
            $"""
            INSERT INTO SystemSnapshots (ts, seq) VALUES ({Ms(Now.AddHours(-25))}, 1), ({Ms(Now.AddHours(-1))}, 2);
            INSERT INTO ProcessSnapshots (ts, proc_key) VALUES ({Ms(Now.AddHours(-30))}, 1), ({Ms(Now.AddMinutes(-5))}, 1);
            INSERT INTO WindowsEvents (ts, channel, record_id, provider, event_id, level, category, severity, title)
                VALUES ({Ms(Now.AddDays(-31))}, 'System', 1, 'p', 1, 2, 0, 1, 'old'), ({Ms(Now.AddHours(-25))}, 'System', 2, 'p', 1, 2, 0, 1, 'kept');
            INSERT INTO RollupSeries (series_id, name) VALUES (1, 'cpu/total_pct');
            INSERT INTO Rollup1m (ts, series_id, min, avg, max) VALUES ({Ms(Now.AddDays(-31))}, 1, 1, 1, 1), ({Ms(Now.AddDays(-1))}, 1, 2, 2, 2);
            INSERT INTO HardwareSnapshots (ts, hash, json) VALUES ({Ms(Now.AddDays(-60))}, 'a', '[]'), ({Ms(Now.AddDays(-40))}, 'b', '[]');
            INSERT INTO DisplaySnapshots (ts, hash, json) VALUES ({Ms(Now.AddDays(-90))}, 'only', '[]');
            INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts, end_ts) VALUES
                (1, 10, 'ended.exe', {Ms(Now.AddHours(-30))}, {Ms(Now.AddHours(-26))}),
                (2, 20, 'running.exe', {Ms(Now.AddDays(-2))}, NULL),
                (3, 30, 'never-seen-ending.exe', {Ms(Now.AddDays(-40))}, NULL);
            INSERT INTO RecorderSessions (start_ts, end_ts, start_reason, end_reason, elevated) VALUES
                ({Ms(Now.AddDays(-32))}, {Ms(Now.AddDays(-31))}, 0, 0, 0), ({Ms(Now.AddDays(-1))}, {Ms(Now.AddHours(-20))}, 0, 0, 0);
            """);

        var result = await Maintenance().PruneAsync(Now, CancellationToken.None);

        (await ScalarAsync("SELECT COUNT(*) FROM SystemSnapshots;")).ShouldBe(1L);
        (await ScalarAsync("SELECT COUNT(*) FROM ProcessSnapshots;")).ShouldBe(1L);
        (await ScalarAsync("SELECT title FROM WindowsEvents;")).ShouldBe("kept");
        (await ScalarAsync("SELECT COUNT(*) FROM Rollup1m;")).ShouldBe(1L);
        (await ScalarAsync("SELECT hash FROM HardwareSnapshots;")).ShouldBe("b");
        (await ScalarAsync("SELECT COUNT(*) FROM DisplaySnapshots;")).ShouldBe(1L);
        (await ScalarAsync("SELECT group_concat(name) FROM ProcessIdentity;")).ShouldBe("running.exe");
        (await ScalarAsync("SELECT COUNT(*) FROM RecorderSessions;")).ShouldBe(1L);
        result.Total.ShouldBe(8);
    }

    [Fact]
    public async Task Large_backlogs_are_deleted_in_time_slices()
    {
        // 6 hours of 1 s rows, all past retention: deleted in 15-minute slices (24 statements).
        await using (var connection = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None))
        {
            await using var transaction = connection.BeginTransaction();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO SystemSnapshots (ts, seq, cpu_pct) VALUES ($ts, $seq, 1);";
            var ts = insert.Parameters.Add("$ts", SqliteType.Integer);
            var seq = insert.Parameters.Add("$seq", SqliteType.Integer);
            var start = Ms(Now.AddHours(-48));
            for (var i = 0; i < 6 * 3600; i++)
            {
                ts.Value = start + (i * 1000L);
                seq.Value = i;
                await insert.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        var result = await Maintenance().PruneAsync(Now, CancellationToken.None);

        result.RowsByTable["SystemSnapshots"].ShouldBe(6 * 3600);
        (await ScalarAsync("SELECT COUNT(*) FROM SystemSnapshots;")).ShouldBe(0L);
    }

    [Fact]
    public async Task Freed_space_is_returned_to_the_file_system()
    {
        await using (var connection = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 40000)
                INSERT INTO ProcessSnapshots (ts, proc_key, gpu_engine) SELECT {Ms(Now.AddDays(-3))} + i, i, printf('%.200c', 'x') FROM n;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var maintenance = Maintenance();
        await maintenance.PruneAsync(Now, CancellationToken.None);
        var pagesBefore = (long)(await ScalarAsync("PRAGMA page_count;"))!;

        var released = await maintenance.ReclaimAsync(CancellationToken.None);

        released.ShouldBeGreaterThan(TelemetryMaintenance.ReclaimThresholdPages);
        ((long)(await ScalarAsync("PRAGMA page_count;"))!).ShouldBeLessThan(pagesBefore - TelemetryMaintenance.ReclaimThresholdPages);
    }

    [Fact]
    public async Task Telemetry_is_trimmed_ahead_of_its_retention_and_compacting_gives_the_space_back()
    {
        // 6 hours of full detail with wide rows, and summaries from 20 days ago.
        await using (var connection = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 21599)
                INSERT INTO SystemSnapshots (ts, seq, cpu_pct) SELECT {Ms(Now.AddHours(-6))} + (i * 1000), i, 1 FROM n;
                WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 21599)
                INSERT INTO ProcessSnapshots (ts, proc_key, gpu_engine) SELECT {Ms(Now.AddHours(-6))} + (i * 1000), i, printf('%.300c', 'x') FROM n;
                INSERT INTO RollupSeries (series_id, name) VALUES (1, 'cpu/total_pct');
                INSERT INTO Rollup1m (ts, series_id, min, avg, max) VALUES ({Ms(Now.AddDays(-20))}, 1, 1, 2, 3), ({Ms(Now.AddDays(-1))}, 1, 1, 2, 3);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var maintenance = Maintenance();
        var before = await maintenance.CoverageAsync(CancellationToken.None);
        before.FullDetailFrom.ShouldBe(Now.AddHours(-6));
        before.SummariesFrom.ShouldBe(Now.AddDays(-20));

        var rows = await maintenance.TrimAsync(Now.AddHours(-2), Now.AddDays(-10), CancellationToken.None);

        rows.ShouldBe((4 * 3600 * 2) + 1);
        var trimmed = await maintenance.CoverageAsync(CancellationToken.None);
        trimmed.FullDetailFrom.ShouldBe(Now.AddHours(-2));
        trimmed.SummariesFrom.ShouldBe(Now.AddDays(-1));
        trimmed.FreeBytes.ShouldBeGreaterThan(before.FreeBytes);

        var given = await maintenance.CompactAsync(CancellationToken.None);

        given.ShouldBeGreaterThan(0);
        var compacted = await maintenance.CoverageAsync(CancellationToken.None);
        compacted.FreeBytes.ShouldBe(0);
        compacted.FileBytes.ShouldBeLessThan(trimmed.FileBytes);

        // The write-ahead log is emptied (reopening may add a page), so the space is really back on the drive.
        var wal = new FileInfo(_paths.TelemetryDatabase + "-wal");
        (wal.Exists ? wal.Length : 0).ShouldBeLessThan(64 * 1024);
    }

    [Fact]
    public async Task Heartbeats_and_boot_recoveries_round_trip()
    {
        var store = new AgentHeartbeatStore(_paths, _factory);
        (await store.ReadAsync(CancellationToken.None)).ShouldBeNull();

        await store.BeatAsync(Now, "2026-10-06T09:00Z", recording: true, CancellationToken.None);
        (await store.ReadAsync(CancellationToken.None)).ShouldBe(new AgentHeartbeatState(Now, "2026-10-06T09:00Z", false, true));

        await store.MarkCleanShutdownAsync(Now.AddSeconds(5), CancellationToken.None);
        (await store.ReadAsync(CancellationToken.None)).ShouldBe(new AgentHeartbeatState(Now.AddSeconds(5), "2026-10-06T09:00Z", true, false));

        var id = await store.RecordBootRecoveryAsync(new BootRecovery(Now, Now.AddHours(-1), Now.AddMinutes(-50), UncleanStopKind.BugCheck, true,
            "Windows restarted after a bug check (0x50).", new Dictionary<string, string> { ["bugcheck"] = "0x50" }), CancellationToken.None);

        id.ShouldBe(1);
        await using var c = await _factory.OpenReadOnlyAsync(_paths.IncidentIndexDatabase, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT kind, was_recording, details_json, incident_id IS NULL FROM BootRecoveries;";
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetInt32(0).ShouldBe((int)UncleanStopKind.BugCheck);
        reader.GetInt32(1).ShouldBe(1);
        reader.GetString(2).ShouldBe("""{"bugcheck":"0x50"}""");
        reader.GetInt32(3).ShouldBe(1); // waiting for the incident engine
    }

    private async Task<(double Min, double Avg, double Max)> RollupAsync(string series, DateTimeOffset minute)
    {
        await using var c = await _factory.OpenReadOnlyAsync(_paths.TelemetryDatabase, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT r.min, r.avg, r.max FROM Rollup1m r JOIN RollupSeries s USING (series_id) WHERE s.name = $name AND r.ts = $ts;";
        command.Parameters.AddWithValue("$name", series);
        command.Parameters.AddWithValue("$ts", Ms(minute));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue($"no rollup for {series} at {minute.ToString("O", CultureInfo.InvariantCulture)}");
        return (reader.GetDouble(0), reader.GetDouble(1), reader.GetDouble(2));
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var c = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class Monitor(StorageOptions value) : IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue => value;

        public StorageOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }
}
