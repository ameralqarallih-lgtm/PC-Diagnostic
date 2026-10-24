using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Reading;

namespace PcDiag.Storage.Tests;

public sealed class ReadersTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Ms0 = T0.ToUnixTimeMilliseconds();

    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly DataPaths _paths;
    private readonly DatabaseInitializer _initializer;
    private readonly HistoryReader _history;

    public ReadersTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _paths = new DataPaths(_dir.Path);
        _initializer = new DatabaseInitializer(_paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance);
        _initializer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _history = new HistoryReader(_factory);
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

    private async Task SeedTelemetryAsync()
    {
        var db = _paths.TelemetryDatabase;
        for (var s = 0; s < 600; s++)
        {
            await ExecAsync(db, "INSERT INTO SystemSnapshots (ts, seq, cpu_pct, mem_used_pct) VALUES ($p0, $p1, $p2, 50);", Ms0 + (s * 1000L), s, s % 100);
        }

        await ExecAsync(db, "INSERT INTO Devices (device_id, kind, stable_key, name, vendor, first_seen, last_seen) VALUES (7, 'gpu', 'luid-1', 'NVIDIA GeForce RTX 4090', 'NVIDIA', $p0, $p0);", Ms0);
        await ExecAsync(db, "INSERT INTO Devices (device_id, kind, stable_key, name, vendor, first_seen, last_seen) VALUES (8, 'disk', 'disk-1', 'Samsung SSD', 'NVMe', $p0, $p0);", Ms0);
        for (var s = 0; s < 600; s += 10)
        {
            await ExecAsync(db, "INSERT INTO GpuMetrics (ts, adapter_id, temp_c, hotspot_c) VALUES ($p0, 7, $p1, $p2);", Ms0 + (s * 1000L), 60 + (s / 100), 75);
        }

        // One-minute summaries from two hours before the raw data begins.
        await ExecAsync(db, "INSERT INTO RollupSeries (series_id, name) VALUES (1, 'cpu/total_pct');");
        for (var m = 0; m < 60; m++)
        {
            await ExecAsync(db, "INSERT INTO Rollup1m (ts, series_id, min, avg, max) VALUES ($p0, 1, 10, 20, 30);", Ms0 - (2 * 3600_000L) + (m * 60_000L));
        }

        await ExecAsync(db,
            "INSERT INTO WindowsEvents (ts, channel, record_id, provider, event_id, level, category, severity, title) VALUES ($p0, 'System', 1, 'nvlddmkm', 153, 3, 2, 3, 'Display driver reset');",
            Ms0 + 30_000);
        await ExecAsync(db, "INSERT INTO DisplayEvents (ts, kind, target, old_value, new_value) VALUES ($p0, 4, 'DELL U2723QE', '144 Hz', '60 Hz');", Ms0 + 40_000);
    }

    [Fact]
    public async Task The_catalog_lists_system_series_and_series_for_each_device()
    {
        await SeedTelemetryAsync();

        var catalog = await _history.CatalogAsync(_paths.TelemetryDatabase, CancellationToken.None);

        catalog.ShouldContain(s => s.Id == "cpu" && s.Unit == "%" && s.LongTerm);
        catalog.ShouldContain(s => s.Id == "gpu.7.hotspot" && s.Label == "NVIDIA GeForce RTX 4090 hotspot" && s.Device == "NVIDIA GeForce RTX 4090");
        catalog.ShouldContain(s => s.Id == "disk.8.readlat" && s.Unit == "ms");
        catalog.ShouldContain(s => s.Id == "memory.commit" && !s.LongTerm);
        catalog.ShouldContain(s => s.Id == "agent.ws" && s.Unit == "B" && s.Group == "Recorder" && s.LongTerm);
        catalog.ShouldContain(s => s.Id == "gpu.7.util" && s.Key == "luid-1" && s.LastSeen == Ms0);
        catalog.ShouldNotContain(s => s.Id.StartsWith("sensor.", StringComparison.Ordinal));
    }

    /// <summary>Charts and dashboard widgets can ask for every hardware sensor too, and chart their readings.</summary>
    [Fact]
    public async Task Hardware_sensors_are_in_the_catalog_on_request_and_can_be_charted()
    {
        await SeedTelemetryAsync();
        var db = _paths.TelemetryDatabase;
        await ExecAsync(db, "INSERT INTO SensorCatalog (sensor_id, provider, kind, name, unit, logical_key) VALUES (4, 'lhm', 'Fan', 'NVIDIA GeForce RTX 4090 / GPU Fan 1', 'RPM', 'lhm/gpu-nvidia/0/fan/1');");
        await ExecAsync(db, "INSERT INTO SensorCatalog (sensor_id, provider, kind, name, unit, logical_key) VALUES (5, 'lhm', 'Temperature', 'Motherboard', 'C', 'lhm/lpc/0/temperature/0');");
        for (var s = 0; s < 60; s++)
        {
            await ExecAsync(db, "INSERT INTO SensorReadings (ts, sensor_id, value) VALUES ($p0, 4, $p1);", Ms0 + (s * 1000L), 1000 + s);
        }

        var catalog = await _history.CatalogAsync(db, sensors: true, CancellationToken.None);
        var fan = catalog.Single(s => s.Id == "sensor.4");
        fan.ShouldBe(new SeriesInfo("sensor.4", "NVIDIA GeForce RTX 4090 / GPU Fan 1", "RPM", "Fan sensors", "NVIDIA GeForce RTX 4090", false, "lhm/gpu-nvidia/0/fan/1"));
        catalog.Single(s => s.Id == "sensor.5").ShouldSatisfyAllConditions(s => s.Unit.ShouldBe("°C"), s => s.Device.ShouldBeNull());

        var result = await _history.SeriesAsync(db, ["sensor.4", "sensor.5", "sensor.99"], Ms0, Ms0 + 59_000, 10, CancellationToken.None);
        var points = result.Series.Single(s => s.Id == "sensor.4").Points;
        points.Count.ShouldBe(11); // 5.9 s buckets
        points[0][0].ShouldBe(Ms0);
        points[0][1].ShouldBe(1000);
        points[0][3].ShouldBe(1005);
        result.Series.Single(s => s.Id == "sensor.5").Points.ShouldBeEmpty();
    }

    /// <summary>A moment of the rolling history, with no incident: every value, the sensors and every process running then.</summary>
    [Fact]
    public async Task A_moment_shows_every_value_and_every_process_running_then()
    {
        await SeedTelemetryAsync();
        var db = _paths.TelemetryDatabase;
        var at = Ms0 + 300_000;
        await ExecAsync(db, "INSERT INTO RecorderSessions (start_ts, end_ts, start_reason, elevated, app_version) VALUES ($p0, $p1, 0, 1, '1.0');", Ms0 - (3 * 3600_000L), Ms0 + 600_000);
        foreach (var (key, pid, name, start, end) in new (long, int, string, long, long?)[]
                 {
                     (1, 100, "busy.exe", Ms0 - 1000, null),
                     (2, 200, "idle.exe", Ms0 - 1000, Ms0 + 400_000),
                     (3, 300, "gone.exe", Ms0, Ms0 + 100_000),
                     (4, 400, "later.exe", Ms0 + 350_000, null),
                     (5, 500, "stillhere.exe", Ms0 - 5000, null),
                     (6, 600, "unseen.exe", Ms0 - 5000, null), // no end time and not running now: ended while nothing recorded
                 })
        {
            await ExecAsync(db, "INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts, end_ts) VALUES ($p0, $p1, $p2, $p3, $p4);", key, pid, name, start, end);
        }

        await ExecAsync(db, "INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, private_b, state) VALUES ($p0, 1, 42, 1000, 0), ($p1, 1, 7, 1000, 0);", at, at - 2000);

        // idle.exe's figures from the full process list 20 s before (unchanged since, so not stored again).
        await ExecAsync(db, "INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, private_b, threads, state) VALUES ($p0, 2, 0, 5000, 4, 0);", at - 20_000);

        // Services: the hourly snapshot, then a service started in idle.exe and another stopped.
        await ExecAsync(db, """
            INSERT INTO ServiceSnapshots (ts, name, display_name, status, start_type, pid, svc_type) VALUES
                ($p0, 'Dnscache', 'DNS Client', 4, 2, 100, 48), ($p0, 'Spooler', 'Print Spooler', 4, 2, 500, 16), ($p0, 'wuauserv', 'Windows Update', 1, 3, NULL, 32);
            """, Ms0);
        await ExecAsync(db, "INSERT INTO ServiceEvents (ts, name, kind, old_value, new_value, pid) VALUES ($p0, 'wuauserv', 0, 'Stopped', 'Running', 200), ($p1, 'Spooler', 1, 'Running', 'Stopped', NULL);",
            at - 10_000, at - 5_000);
        await ExecAsync(db, "INSERT INTO SensorCatalog (sensor_id, provider, kind, name, unit, logical_key) VALUES (1, 'lhm', 0, 'Intel Core Ultra 9 285K / CPU Package', '°C', 'lhm/intelcpu/0/temperature/0');");
        await ExecAsync(db, "INSERT INTO SensorReadings (ts, sensor_id, value) VALUES ($p0, 1, 61);", at + 500);

        var moment = await _history.MomentAsync(db, at, (pid, _) => pid is 100 or 500, CancellationToken.None);

        moment.Recorded.ShouldBeTrue();
        moment.Values.ShouldContain(v => v.Id == "cpu" && v.Group == "System");
        moment.Values.Single(v => v.Id == "gpu.7.temp").Value.ShouldBe(63);
        moment.Values.Single(v => v.Group == "Hardware sensors").ShouldSatisfyAllConditions(v => v.Value.ShouldBe(61), v => v.Unit.ShouldBe("°C"));
        moment.ProcessSampleTs.ShouldBe(at);
        moment.Processes.Select(p => p.Name).ShouldBe(["busy.exe", "idle.exe", "stillhere.exe"]);
        moment.Processes[0].ShouldSatisfyAllConditions(p => p.Measured.ShouldBeTrue(), p => p.CpuPercent.ShouldBe(42), p => p.StartTs.ShouldBe(Ms0 - 1000), p => p.FiguresTs.ShouldBeNull());
        moment.Processes[1].ShouldSatisfyAllConditions(p => p.Name.ShouldBe("idle.exe"), p => p.Measured.ShouldBeTrue(), p => p.PrivateBytes.ShouldBe(5000), p => p.FiguresTs.ShouldBe(at - 20_000));
        moment.Processes[2].ShouldSatisfyAllConditions(p => p.Name.ShouldBe("stillhere.exe"), p => p.Measured.ShouldBeFalse());

        // Services then, each with the process it ran in.
        var services = moment.Services.ShouldNotBeNull();
        services.Select(s => (s.Name, s.Status, s.ProcKey)).ShouldBe([("Dnscache", "Running", 1L), ("wuauserv", "Running", 2L), ("Spooler", "Stopped", (long?)null)]);
        services.Single(s => s.Name == "wuauserv").ShouldSatisfyAllConditions(s => s.Pid.ShouldBe(200), s => s.DisplayName.ShouldBe("Windows Update"));

        // Not recording then: nothing is known.
        (await _history.MomentAsync(db, Ms0 + 900_000, (_, _) => true, CancellationToken.None)).ShouldSatisfyAllConditions(
            m => m.Recorded.ShouldBeFalse(), m => m.Values.ShouldBeEmpty(), m => m.Processes.ShouldBeEmpty());

        // Beyond the raw data: one-minute summaries, and no process list (ended processes are forgotten with the raw data).
        var old = await _history.MomentAsync(db, Ms0 - (2 * 3600_000L) + 90_000, (_, _) => true, CancellationToken.None);
        old.Values.Single(v => v.Id == "cpu").Value.ShouldBe(20);
        old.Processes.ShouldBeEmpty();
        old.ProcessSampleTs.ShouldBeNull();
    }

    [Fact]
    public async Task Raw_samples_are_bucketed_into_min_average_max()
    {
        await SeedTelemetryAsync();

        var result = await _history.SeriesAsync(_paths.TelemetryDatabase, ["cpu", "gpu.7.temp"], Ms0, Ms0 + 599_000, 60, CancellationToken.None);

        result.BucketMs.ShouldBe(9984); // 599 s over 60 buckets, rounded up to whole ms
        var cpu = result.Series.Single(s => s.Id == "cpu").Points;
        cpu[0].ShouldBe([Ms0, 0, 4.5, 9]);
        cpu.Count.ShouldBeGreaterThan(55);
        result.Series.Single(s => s.Id == "gpu.7.temp").Points.Count.ShouldBe(60);
    }

    [Fact]
    public async Task Ranges_older_than_the_raw_data_use_one_minute_summaries()
    {
        await SeedTelemetryAsync();

        var result = await _history.SeriesAsync(_paths.TelemetryDatabase, ["cpu"], Ms0 - (2 * 3600_000L), Ms0 + 599_000, 200, CancellationToken.None);

        var points = result.Series.Single().Points;
        points[0][0].ShouldBe(Ms0 - (2 * 3600_000L));
        points[0][1..].ShouldBe([10, 20, 30]);
        points[^1][0].ShouldBeGreaterThanOrEqualTo(Ms0);
    }

    [Fact]
    public async Task Unknown_or_mismatched_series_are_left_out()
    {
        await SeedTelemetryAsync();

        var result = await _history.SeriesAsync(_paths.TelemetryDatabase, ["nonsense", "disk.7.active", "gpu.99.temp", "gpu.7.temp; DROP TABLE x"], Ms0, Ms0 + 60_000, 60, CancellationToken.None);

        result.Series.ShouldBeEmpty();
    }

    [Fact]
    public async Task Events_come_from_every_event_table_and_can_be_filtered_by_importance()
    {
        await SeedTelemetryAsync();

        var all = await _history.EventsAsync(_paths.TelemetryDatabase, Ms0, Ms0 + 600_000, 0, 100, CancellationToken.None);
        var serious = await _history.EventsAsync(_paths.TelemetryDatabase, Ms0, Ms0 + 600_000, 3, 100, CancellationToken.None);

        all.Select(e => e.Kind).ShouldBe(["windows-event", "display"]);
        all[1].Title.ShouldBe("DELL U2723QE: refresh rate changed (144 Hz → 60 Hz)");
        all[0].Detail!.Value.GetProperty("provider").GetString().ShouldBe("nvlddmkm");
        serious.ShouldHaveSingleItem().Title.ShouldBe("Display driver reset");
    }

    [Fact]
    public async Task The_system_overview_has_inventory_drivers_and_availability()
    {
        await SeedTelemetryAsync();
        var db = _paths.TelemetryDatabase;
        await ExecAsync(db, "INSERT INTO SystemInformation (key, value, updated_ts) VALUES ('os.name', 'Windows 11 Pro', $p0);", Ms0);
        await ExecAsync(db, "INSERT INTO Drivers (snapshot_ts, device_class, device_name, provider, version) VALUES ($p0, 'Display', 'RTX 4090', 'NVIDIA', '32.0.15.6094');", Ms0 - 1000);
        await ExecAsync(db, "INSERT INTO Drivers (snapshot_ts, device_class, device_name, provider, version) VALUES ($p0, 'Display', 'RTX 4090', 'NVIDIA', '32.0.15.7000');", Ms0);
        await ExecAsync(db, "INSERT INTO SensorAvailability (series, availability, reason, since_ts) VALUES ('cpu/package_temp', 8, 'PawnIO is not installed', $p0);", Ms0);

        var overview = await new SystemReader(_factory).OverviewAsync(db, CancellationToken.None);

        overview.Information["os.name"].ShouldBe("Windows 11 Pro");
        overview.Devices.Select(d => d.Kind).ShouldBe(["disk", "gpu"]);
        overview.Drivers.ShouldHaveSingleItem().Version.ShouldBe("32.0.15.7000"); // latest snapshot only
        overview.Availability.ShouldHaveSingleItem().Availability.ShouldBe(nameof(Availability.DriverMissing));
    }

    [Fact]
    public async Task Incidents_can_be_filtered_and_read_with_timeline_and_processes()
    {
        var store = new IncidentStore(_paths, _factory, _initializer, _time);
        var draft = new IncidentDraft(T0, T0, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), IncidentSource.Hotkey, UserIncidentType.ScreenFlicker,
            "Screen Flicker", "left monitor", Severity.Medium, false, new IncidentTrigger(T0, IncidentSource.Hotkey, UserIncidentType.ScreenFlicker), "1.0.0");
        var incident = await store.CreateAsync(draft, CancellationToken.None);
        await store.CreateAsync(draft with { UserType = UserIncidentType.GameCrash, Title = "Game Crash", Notes = null, Source = IncidentSource.User, Trigger = new IncidentTrigger(T0, IncidentSource.User) },
            CancellationToken.None);
        await store.CaptureWindowAsync(incident, T0.AddMinutes(-10), T0, [Frame(T0.AddSeconds(-4), 20), Frame(T0.AddSeconds(-2), 40)], CancellationToken.None);
        await store.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        var index = new IncidentIndexReader(_paths, _factory);
        var reader = new IncidentReader(_paths, _factory, index);
        var key = incident.Key.ToString();

        (await index.ListAsync(new IncidentQuery(Type: UserIncidentType.ScreenFlicker), CancellationToken.None)).Items.ShouldHaveSingleItem().Key.ShouldBe(key);
        (await index.ListAsync(new IncidentQuery(Source: IncidentSource.User), CancellationToken.None)).Items.ShouldHaveSingleItem().Title.ShouldBe("Game Crash");
        (await index.ListAsync(new IncidentQuery(Text: "left mon"), CancellationToken.None)).Total.ShouldBe(1);
        (await index.ListAsync(new IncidentQuery(Text: "100%_"), CancellationToken.None)).Total.ShouldBe(0);

        var detail = (await reader.DetailAsync(key, CancellationToken.None))!;
        detail.From.ShouldBe(Ms0 - 600_000);
        detail.To.ShouldBe(Ms0 + 120_000);
        detail.Triggers.ShouldHaveSingleItem().UserType.ShouldBe(UserIncidentType.ScreenFlicker);
        detail.Info["title"].ShouldBe("Screen Flicker");
        detail.HasData.ShouldBeTrue();

        var processes = await reader.ProcessesAsync(key, Ms0 - 2500, CancellationToken.None);
        processes.SampleTs.ShouldBe(Ms0 - 2000); // nearest sample
        var game = processes.Processes.ShouldHaveSingleItem();
        game.CpuPercent.ShouldBe(40);
        game.DiskBytesPerSec.ShouldBe(44_000); // Enhanced mode's true I/O, written from the in-memory frames
        game.NetBytesPerSec.ShouldBe(440);
        game.Signature.ShouldBe(nameof(SignatureStatus.NotSigned));
        game.Signer.ShouldBeNull();

        (await reader.DetailAsync("INC-20990101-00001", CancellationToken.None)).ShouldBeNull();
        reader.DatabasePath("../../Windows").ShouldBeNull();
    }

    [Fact]
    public async Task Incidents_can_be_edited_and_deleted()
    {
        var store = new IncidentStore(_paths, _factory, _initializer, _time);
        var incident = await store.CreateAsync(
            new IncidentDraft(T0, T0, TimeSpan.Zero, TimeSpan.FromMinutes(2), IncidentSource.Tray, null, "Captured from the tray", null, Severity.Medium, true,
                new IncidentTrigger(T0, IncidentSource.Tray), "1.0.0"),
            CancellationToken.None);
        var index = new IncidentIndexReader(_paths, _factory);

        (await store.UpdateAsync(incident.Key, new IncidentUpdate("Flicker", "after the driver update", UserIncidentType.ScreenFlicker, true), CancellationToken.None)).ShouldBeTrue();
        var edited = (await index.FindAsync(incident.Key.ToString(), CancellationToken.None))!.Value.Summary;
        (edited.Title, edited.Notes, edited.UserType, edited.Pinned).ShouldBe(("Flicker", "after the driver update", UserIncidentType.ScreenFlicker, true));
        (await store.UpdateAsync(incident.Key, new IncidentUpdate(Notes: ""), CancellationToken.None)).ShouldBeTrue();
        (await index.FindAsync(incident.Key.ToString(), CancellationToken.None))!.Value.Summary.Notes.ShouldBeNull();

        (await store.DeleteAsync(incident.Key, CancellationToken.None)).ShouldBeTrue();
        (await index.FindAsync(incident.Key.ToString(), CancellationToken.None)).ShouldBeNull();
        Directory.Exists(_paths.GetIncidentDirectory(incident.Key)).ShouldBeFalse();
        (await store.DeleteAsync(incident.Key, CancellationToken.None)).ShouldBeFalse();
        (await store.UpdateAsync(incident.Key, new IncidentUpdate("x"), CancellationToken.None)).ShouldBeFalse();
    }

    private static SampleFrame Frame(DateTimeOffset at, double cpu)
    {
        var none = Reading<long>.Unavailable(Availability.Unsupported);
        var metrics = new List<ProcessMetrics>
        {
            new(new ProcessKey(11, 1), "game.exe", Reading<double>.Of(cpu), none, Reading<long>.Of(1000), Reading<long>.Of(2000), none,
                Reading<double>.Unavailable(Availability.Unsupported), Reading<double>.Unavailable(Availability.Unsupported),
                Reading<int>.Unavailable(Availability.Unsupported), Reading<int>.Unavailable(Availability.Unsupported), Reading<int>.Unavailable(Availability.Unsupported),
                ProcessState.Running,
                new ProcessTrueIo(cpu * 1000, cpu * 100, cpu * 10, cpu)),
        };
        var identity = new ProcessIdentityDetails(new ProcessKey(11, 1), 4, "game.exe", at.AddMinutes(-30), 1, @"C:\Games\game.exe", null, null, null, SignatureStatus.NotSigned);
        return new SampleFrame(1, at, Processes: new ProcessSample(at, metrics, [identity], []));
    }

    [Fact]
    public async Task Recorder_overhead_summarizes_its_own_cpu_memory_and_sampling_time()
    {
        var db = _paths.TelemetryDatabase;
        for (var s = 0; s < 100; s++)
        {
            await ExecAsync(db, "INSERT INTO SystemSnapshots (ts, seq, agent_cpu_pct, agent_ws_bytes, tick_ms, collectors_missed) VALUES ($p0, $p1, $p2, $p3, $p4, $p5);",
                Ms0 + (s * 1000L), s, s == 99 ? 4.0 : 0.2, 150_000_000L + (s * 1000L), s == 50 ? 40.0 : 10.0, s == 7 ? 1 : 0);
        }

        var overhead = await _history.OverheadAsync(db, Ms0, Ms0 + 99_000, CancellationToken.None);

        overhead.Samples.ShouldBe(100);
        overhead.CpuAvgPercent!.Value.ShouldBe(0.238, 0.001);
        overhead.CpuP95Percent.ShouldBe(0.2);
        overhead.CpuMaxPercent.ShouldBe(4.0);
        overhead.WorkingSetMaxBytes.ShouldBe(150_099_000);
        overhead.TickMaxMs.ShouldBe(40);
        overhead.SamplesWithMissedCollectors.ShouldBe(1);
        (await _history.OverheadAsync(db, Ms0 + 500_000, Ms0 + 600_000, CancellationToken.None)).CpuAvgPercent.ShouldBeNull();
    }
}
