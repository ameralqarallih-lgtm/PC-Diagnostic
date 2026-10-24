using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Reading;

namespace PcDiag.Storage.Tests;

/// <summary>Everything recorded in a period, gathered for the period diagnostic.</summary>
public sealed class PeriodEvidenceReaderTests : IDisposable
{
    private const long Mb = 1024L * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Ms0 = T0.ToUnixTimeMilliseconds();

    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly DataPaths _paths;
    private readonly PeriodEvidenceReader _reader;

    public PeriodEvidenceReaderTests()
    {
        var time = new FakeTimeProvider(T0);
        _paths = new DataPaths(_dir.Path);
        new DatabaseInitializer(_paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _reader = new PeriodEvidenceReader(_paths, _factory, new HistoryReader(_factory), new AnomalyReader(_factory), new IncidentIndexReader(_paths, _factory));
    }

    public void Dispose() => _dir.Dispose();

    private async Task ExecAsync(string database, string sql, params object?[] parameters)
    {
        await using var connection = await _factory.OpenReadWriteAsync(database, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < parameters.Length; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", parameters[i] ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Ten minutes of raw samples from T0, with a CPU load from 4 to 6 minutes and memory lowest at 5 minutes.</summary>
    private async Task SeedAsync()
    {
        var db = _paths.TelemetryDatabase;
        await ExecAsync(db, "INSERT INTO RecorderSessions (start_ts, end_ts, start_reason, elevated, app_version) VALUES ($p0, $p1, 0, 1, '1.0');", Ms0, Ms0 + 600_000);
        await ExecAsync(db, "INSERT INTO SystemInformation (key, value, updated_ts) VALUES ('cpu.0.logical_processors', '16', $p0);", Ms0);
        for (var s = 0; s < 600; s++)
        {
            var load = s is >= 240 and < 360;
            await ExecAsync(db, "INSERT INTO SystemSnapshots (ts, seq, cpu_pct, mem_used_pct, gpu_pct, disk_active_pct) VALUES ($p0, $p1, $p2, 50, 5, 2);",
                Ms0 + (s * 1000L), s, load ? 90 + (s % 10) : 10);
            await ExecAsync(db, "INSERT INTO MemoryMetrics (ts, available_b) VALUES ($p0, $p1);", Ms0 + (s * 1000L), (8000 - (s == 300 ? 7500 : 0)) * Mb);
        }

        // Processes: busy.exe (signature not trusted) used 60 s of CPU time; grow.exe's private memory grew.
        await ExecAsync(db, "INSERT INTO ProcessIdentity (proc_key, pid, name, path, start_ts, sig_status, signer) VALUES (1, 100, 'busy.exe', 'C:\\busy.exe', $p0, 4, 'Contoso');", Ms0 - 1000);
        await ExecAsync(db, "INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts) VALUES (2, 200, 'grow.exe', $p0);", Ms0 - 1000);
        await ExecAsync(db, "INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, cpu_time_ms, private_b, handles, state) VALUES ($p0, 1, 5, 1000, $p1, 100, 0), ($p2, 1, 80, 61000, $p1, 150, 0);",
            Ms0, 50 * Mb, Ms0 + 300_000);
        await ExecAsync(db, "INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, private_b, state) VALUES ($p0, 2, 1, $p1, 0), ($p2, 2, 2, $p3, 0);",
            Ms0, 100 * Mb, Ms0 + 290_000, 900 * Mb);
        for (var i = 0; i < 3; i++)
        {
            await ExecAsync(db, "INSERT INTO ProcessEvents (ts, proc_key, kind) VALUES ($p0, 1, 0);", Ms0 + (i * 1000L));
        }

        await ExecAsync(db, "INSERT INTO ProcessEvents (ts, proc_key, kind) VALUES ($p0, 2, 3), ($p1, 2, 4);", Ms0 + 100_000, Ms0 + 110_000);

        // Services: a snapshot with display names, then two failures.
        await ExecAsync(db, "INSERT INTO ServiceSnapshots (ts, name, display_name, status) VALUES ($p0, 'wuauserv', 'Windows Update', 4);", Ms0 - 60_000);
        await ExecAsync(db, "INSERT INTO ServiceEvents (ts, name, kind) VALUES ($p0, 'wuauserv', 4), ($p1, 'wuauserv', 4), ($p1, 'wuauserv', 0);", Ms0 + 50_000, Ms0 + 70_000);

        // A temperature sensor, a volume running low on space, an adapter with packet errors.
        await ExecAsync(db, "INSERT INTO SensorCatalog (sensor_id, provider, kind, name, unit, logical_key) VALUES (1, 'lhm', 'Temperature', 'CPU Package', '°C', 'cpu/package');");
        await ExecAsync(db, "INSERT INTO SensorReadings (ts, sensor_id, value) VALUES ($p0, 1, 50), ($p1, 1, 96), ($p2, 1, 60);", Ms0, Ms0 + 200_000, Ms0 + 400_000);
        await ExecAsync(db, "INSERT INTO VolumeMetrics (ts, mount, total_b, free_b, label) VALUES ($p0, 'C:\\', $p1, $p2, 'System'), ($p3, 'C:\\', $p1, $p4, 'System');",
            Ms0, 100_000 * Mb, 9000 * Mb, Ms0 + 100_000, 5000 * Mb);
        await ExecAsync(db, "INSERT INTO Devices (device_id, kind, stable_key, name, first_seen, last_seen) VALUES (5, 'network', 'net-1', 'Ethernet', $p0, $p0);", Ms0);
        await ExecAsync(db, "INSERT INTO NetworkMetrics (ts, if_id, rx_bps, rx_errors, tx_errors) VALUES ($p0, 5, 1000, 3, 0), ($p1, 5, 1000, 1, 1), ($p2, 5, 1000, 0, 0);",
            Ms0 + 10_000, Ms0 + 20_000, Ms0 + 30_000);

        await ExecAsync(_paths.IncidentIndexDatabase,
            "INSERT INTO BootRecoveries (detected_ts, last_heartbeat_ts, boot_ts, kind, was_recording, summary) VALUES ($p0, $p1, $p0, 0, 1, 'The recorder stopped unexpectedly.');",
            Ms0 + 500_000, Ms0 + 450_000);
    }

    [Fact]
    public async Task A_period_gathers_every_aspect_recorded_in_it()
    {
        await SeedAsync();

        var e = await _reader.LoadAsync(T0.AddMinutes(-30), T0.AddMinutes(10), CancellationToken.None);

        e.RawFrom.ShouldBe(T0);
        e.LogicalProcessors.ShouldBe(16);
        e.Sessions.ShouldHaveSingleItem().Start.ShouldBe(T0);
        e.Series.ShouldContain(s => s.Id == "cpu" && s.Points.Count > 0);
        e.UncleanStops.ShouldHaveSingleItem().Kind.ShouldBe("RecorderStopped");

        var busy = e.Processes.Single(p => p.Name == "busy.exe");
        busy.CpuTimeMs.ShouldBe(60_000);
        busy.Signature.ShouldBe("NotTrusted");
        busy.Signer.ShouldBe("Contoso");
        busy.FirstHandles.ShouldBe(100);
        busy.LastHandles.ShouldBe(150);
        var grow = e.Processes.Single(p => p.Name == "grow.exe");
        (grow.FirstPrivateBytes, grow.LastPrivateBytes, grow.MaxPrivateBytes).ShouldBe((100 * Mb, 900 * Mb, 900 * Mb));

        e.ProcessNames.Single(n => n.Name == "busy.exe").Started.ShouldBe(3);
        var hung = e.ProcessNames.Single(n => n.Name == "grow.exe");
        (hung.Hung, hung.Recovered).ShouldBe((1, 1));

        var update = e.Services.ShouldHaveSingleItem();
        (update.DisplayName, update.Failed, update.Started).ShouldBe(("Windows Update", 2, 1));
        update.LastFailure.ShouldBe(T0.AddSeconds(70));

        var sensor = e.Sensors.ShouldHaveSingleItem();
        (sensor.Kind, sensor.Max, sensor.MaxAt).ShouldBe(("Temperature", 96d, T0.AddSeconds(200)));
        var volume = e.Volumes.ShouldHaveSingleItem();
        (volume.MinFreeBytes, volume.MinFreeAt, volume.Label).ShouldBe((5000 * Mb, T0.AddSeconds(100), "System"));

        var errors = e.NetworkErrors.ShouldHaveSingleItem();
        (errors.SeriesId, errors.Total, errors.Most, errors.MostAt).ShouldBe(("net.5.errors", 5L, 3L, T0.AddSeconds(10)));
    }

    [Fact]
    public async Task Peaks_name_the_processes_using_the_most_at_the_sustained_extreme()
    {
        await SeedAsync();

        var e = await _reader.LoadAsync(T0, T0.AddMinutes(10), CancellationToken.None);

        // The CPU peak falls in the load (4–6 min), where busy.exe was the busiest process sampled.
        var cpu = e.Peaks.Single(p => p.SeriesId == "cpu");
        cpu.At.ShouldBeInRange(T0.AddMinutes(4), T0.AddMinutes(6));
        cpu.Value.ShouldBeGreaterThanOrEqualTo(90);
        // Memory was lowest at 5 minutes; grow.exe then held the most private memory (its latest figures).
        var memory = e.Peaks.Single(p => p.SeriesId == "memory.available");
        memory.At.ShouldBe(T0.AddMinutes(5));
        memory.Processes[0].Name.ShouldBe("grow.exe");
        memory.Processes[0].Value.ShouldBe(900 * Mb);
    }

    [Fact]
    public async Task Process_and_sensor_details_exist_only_where_raw_samples_do()
    {
        await SeedAsync();

        // A period entirely before the raw samples: series from summaries only (none here), no process details.
        var e = await _reader.LoadAsync(T0.AddHours(-3), T0.AddHours(-2), CancellationToken.None);

        e.RawFrom.ShouldBeNull();
        e.Processes.ShouldBeEmpty();
        e.Sensors.ShouldBeEmpty();
        e.Peaks.ShouldBeEmpty();
        e.Sessions.ShouldBeEmpty();
    }
}
