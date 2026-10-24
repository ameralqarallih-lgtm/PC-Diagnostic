using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public sealed class IncidentStoreTests : IDisposable
{
    private static readonly DateTimeOffset Center = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Center);
    private readonly DataPaths _paths;
    private readonly IncidentStore _store;

    public IncidentStoreTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _paths = new DataPaths(_dir.Path);
        var initializer = new DatabaseInitializer(_paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance);
        initializer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _store = new IncidentStore(_paths, _factory, initializer, _time);
    }

    public void Dispose() => _dir.Dispose();

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private static IncidentDraft Draft(UserIncidentType? type = UserIncidentType.ScreenFlicker) => new(
        Center, Center, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), IncidentSource.Hotkey, type, "Screen Flicker", "flicker in game", Severity.Medium, false,
        new IncidentTrigger(Center, IncidentSource.Hotkey, type), "1.0.0");

    [Fact]
    public async Task Incidents_get_sequential_keys_a_folder_and_a_self_describing_database()
    {
        var first = await _store.CreateAsync(Draft(), CancellationToken.None);
        var second = await _store.CreateAsync(Draft(null), CancellationToken.None);

        first.Key.ToString().ShouldBe("INC-20261006-00001");
        second.Key.ToString().ShouldBe("INC-20261006-00002");
        var database = Path.Combine(_paths.GetIncidentDirectory(first.Key), "incident.db");
        File.Exists(database).ShouldBeTrue();
        (await ScalarAsync(database, "SELECT value FROM IncidentInfo WHERE key = 'title';")).ShouldBe("Screen Flicker");
        (await ScalarAsync(database, "SELECT value FROM IncidentInfo WHERE key = 'pre_seconds';")).ShouldBe("600");
        (await ScalarAsync(_paths.IncidentIndexDatabase, "SELECT state FROM Incidents WHERE id = 1;")).ShouldBe((long)IncidentState.Recording);
        (await ScalarAsync(_paths.IncidentIndexDatabase, "SELECT user_type FROM IncidentTriggers WHERE incident_id = 1;")).ShouldBe((long)UserIncidentType.ScreenFlicker);
    }

    [Fact]
    public async Task The_window_is_copied_with_state_before_it_late_events_and_every_process()
    {
        await SeedTelemetryAsync();
        var incident = await _store.CreateAsync(Draft(), CancellationToken.None);
        var frames = new[] { Frame(Center.AddSeconds(-2), (11, "chrome.exe", 2.5), (12, "idle-app.exe", 0)) };

        await _store.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center, frames, CancellationToken.None);
        await _store.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center, frames, CancellationToken.None); // idempotent

        var db = IncidentDb(incident);
        (await ScalarAsync(db, "SELECT COUNT(*) FROM SystemSnapshots;")).ShouldBe(2L);           // in window only
        (await ScalarAsync(db, "SELECT group_concat(title, '|') FROM WindowsEvents ORDER BY ts;")).ShouldBe("in window|late WER report"); // post-window event included
        (await ScalarAsync(db, "SELECT event_ref FROM ServiceEvents;")).ShouldBe(42L);             // IDs kept: reference still valid
        (await ScalarAsync(db, "SELECT hash FROM HardwareSnapshots;")).ShouldBe("hw-before");      // latest before the window
        (await ScalarAsync(db, "SELECT COUNT(*) FROM Drivers;")).ShouldBe(1L);
        (await ScalarAsync(db, "SELECT name FROM Devices;")).ShouldBe("NVIDIA GeForce RTX 4090");
        (await ScalarAsync(db, "SELECT COUNT(*) FROM ProcessSnapshots;")).ShouldBe(3L);            // 1 top process from telemetry + 2 from the ring
        (await ScalarAsync(db, "SELECT group_concat(name, '|') FROM (SELECT name FROM ProcessIdentity ORDER BY name);")).ShouldBe("chrome.exe|game.exe|idle-app.exe");
    }

    [Fact]
    public async Task Post_window_saved_while_recording_keeps_every_process_when_finished_after_a_restart()
    {
        await SeedTelemetryAsync();
        var incident = await _store.CreateAsync(Draft(), CancellationToken.None);
        var during = new[] { Frame(Center.AddSeconds(4), (11, "chrome.exe", 2.5), (12, "idle-app.exe", 0), (13, "explorer.exe", 0.1)) };

        // Saved during the post-window (IncidentManager.SaveProgressAsync), then the Agent restarts: at startup the
        // unfinished incident is finished from telemetry.db alone (no frames), which keeps only the busiest processes.
        await _store.CaptureWindowAsync(incident, Center, Center.AddSeconds(10), during, CancellationToken.None);
        await _store.CaptureWindowAsync(incident, Center, Center.AddMinutes(2), [], CancellationToken.None);
        await _store.FinalizeAsync(incident, IncidentState.Partial, null, CancellationToken.None);

        (await ScalarAsync(IncidentDb(incident), $"SELECT COUNT(*) FROM ProcessSnapshots WHERE ts = {Ms(Center.AddSeconds(4))};")).ShouldBe(3L);
    }

    [Fact]
    public async Task Finalizing_builds_the_timeline_and_headline_and_updates_the_index()
    {
        await SeedTelemetryAsync();
        var incident = await _store.CreateAsync(Draft(), CancellationToken.None);
        await _store.AddTriggerAsync(incident.Id, new IncidentTrigger(Center.AddSeconds(30), IncidentSource.Tray, UserIncidentType.SystemFreeze), CancellationToken.None);
        await _store.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center.AddMinutes(2), [], CancellationToken.None);
        await _store.SaveSnapshotsAsync(incident,
            new DisplaySnapshot(Center, [], IsRemoteSession: true),
            new ServiceSnapshot(Center, [new ServiceInfo("Spooler", "Print Spooler", ServiceStatus.Running, ServiceStartType.Automatic, 1234, 16, null)]), CancellationToken.None);

        await _store.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);

        // A finished incident is one self-contained file (checked before anything reopens it read-write).
        var db = IncidentDb(incident);
        File.Exists(db + "-wal").ShouldBeFalse();
        await using (var check = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
        {
            await check.OpenAsync();
            await using var mode = check.CreateCommand();
            mode.CommandText = "PRAGMA journal_mode;";
            (await mode.ExecuteScalarAsync()).ShouldBe("delete");
        }

        var timeline = await RowsAsync(db, "SELECT kind || ': ' || title FROM IncidentEvents ORDER BY ts, id;");
        timeline.ShouldBe(
        [
            "process: game.exe crashed",
            "windows-event: in window",
            "service: Spooler failed",
            "display: PG32UCDP: refresh rate changed (240 Hz → 60 Hz)",
            "trigger: Captured: Screen Flicker (hotkey)",
            "trigger: Captured: System Freeze (tray)",
            "windows-event: late WER report",
        ]);
        (await ScalarAsync(db, "SELECT importance FROM IncidentEvents WHERE kind = 'process';")).ShouldBe(3L);
        (await ScalarAsync(db, "SELECT COUNT(*) FROM ServiceSnapshots WHERE name = 'Spooler';")).ShouldBe(1L);
        (await ScalarAsync(db, "SELECT json_extract(json, '$.IsRemoteSession') FROM DisplaySnapshots ORDER BY id DESC LIMIT 1;")).ShouldBe(1L);
        (await ScalarAsync(_paths.IncidentIndexDatabase, "SELECT state FROM Incidents WHERE id = 1;")).ShouldBe((long)IncidentState.Complete);
        (await ScalarAsync(_paths.IncidentIndexDatabase, "SELECT json_extract(headline_json, '$.cpuPct') FROM Incidents WHERE id = 1;")).ShouldBe(55.0);
        (await _store.ListUnfinishedAsync(CancellationToken.None)).ShouldBeEmpty();

    }

    [Fact]
    public async Task Unfinished_incidents_and_boot_recoveries_are_listed_until_handled()
    {
        var incident = await _store.CreateAsync(Draft(), CancellationToken.None);
        var heartbeat = new AgentHeartbeatStore(_paths, _factory);
        var recovery = new BootRecovery(Center, Center.AddMinutes(-5), Center.AddMinutes(-3), UncleanStopKind.UnexpectedShutdown, true, "Windows restarted without a clean shutdown.",
            new Dictionary<string, string> { ["events"] = "Microsoft-Windows-Kernel-Power 41" });
        var id = await heartbeat.RecordBootRecoveryAsync(recovery, CancellationToken.None);

        (await _store.ListUnfinishedAsync(CancellationToken.None)).ShouldHaveSingleItem().Key.ShouldBe(incident.Key);
        var pending = (await _store.PendingBootRecoveriesAsync(CancellationToken.None)).ShouldHaveSingleItem();
        pending.Id.ShouldBe(id);
        pending.Recovery.ShouldBeEquivalentTo(recovery);

        await _store.MarkBootRecoveryAsync(id, incident.Id, CancellationToken.None);

        (await _store.PendingBootRecoveriesAsync(CancellationToken.None)).ShouldBeEmpty();
        (await ScalarAsync(_paths.IncidentIndexDatabase, "SELECT incident_id FROM BootRecoveries;")).ShouldBe(incident.Id);
    }

    private string IncidentDb(Incident incident) => Path.Combine(_paths.GetIncidentDirectory(incident.Key), "incident.db");

    private static SampleFrame Frame(DateTimeOffset at, params (int Pid, string Name, double Cpu)[] processes)
    {
        var d = Reading<double>.Unavailable(Availability.Unsupported);
        var l = Reading<long>.Unavailable(Availability.Unsupported);
        var n = Reading<int>.Unavailable(Availability.Unsupported);
        var metrics = processes.Select(p => new ProcessMetrics(new ProcessKey(p.Pid, 1), p.Name, Reading<double>.Of(p.Cpu), l, l, l, l, d, d, n, n, n, ProcessState.Running)).ToList();
        return new SampleFrame(1, at, Processes: new ProcessSample(at, metrics, [], []));
    }

    private async Task SeedTelemetryAsync()
    {
        var game = new ProcessKey(10, 1).ToStorageId();
        await ExecAsync(_paths.TelemetryDatabase,
            $"""
            INSERT INTO SystemSnapshots (ts, seq, cpu_pct) VALUES ({Ms(Center.AddMinutes(-11))}, 1, 10), ({Ms(Center.AddMinutes(-5))}, 2, 40), ({Ms(Center.AddSeconds(-1))}, 3, 55), ({Ms(Center.AddMinutes(3))}, 4, 99);
            INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts) VALUES ({game}, 10, 'game.exe', {Ms(Center.AddHours(-1))}), (999, 99, 'unrelated.exe', {Ms(Center.AddHours(-1))});
            INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct) VALUES ({Ms(Center.AddSeconds(-1))}, {game}, 90);
            INSERT INTO ProcessEvents (ts, proc_key, kind, detail_json) VALUES ({Ms(Center.AddMinutes(-3))}, {game}, {(int)ProcessEventKind.Crashed}, json_object('name', 'game.exe'));
            INSERT INTO WindowsEvents (id, ts, channel, record_id, provider, event_id, level, category, severity, title)
                VALUES (42, {Ms(Center.AddMinutes(-2))}, 'System', 1, 'p', 1, 2, 0, 2, 'in window'),
                       (43, {Ms(Center.AddMinutes(1))}, 'Application', 2, 'Windows Error Reporting', 1001, 4, 5, 1, 'late WER report'),
                       (44, {Ms(Center.AddMinutes(-30))}, 'System', 3, 'p', 1, 2, 0, 1, 'too old');
            INSERT INTO ServiceEvents (ts, name, kind, new_value, event_ref) VALUES ({Ms(Center.AddMinutes(-2))}, 'Spooler', {(int)ServiceEventKind.Failed}, 'stopped', 42);
            INSERT INTO DisplayEvents (ts, kind, target, old_value, new_value) VALUES ({Ms(Center.AddMinutes(-1))}, {(int)DisplayEventKind.RefreshRateChanged}, 'PG32UCDP', '240 Hz', '60 Hz');
            INSERT INTO HardwareSnapshots (ts, hash, json) VALUES ({Ms(Center.AddDays(-2))}, 'hw-old', '[]'), ({Ms(Center.AddHours(-1))}, 'hw-before', '[]');
            INSERT INTO Drivers (snapshot_ts, device_class, device_name, version) VALUES ({Ms(Center.AddDays(-2))}, 'Display', 'old', '1'), ({Ms(Center.AddHours(-1))}, 'Display', 'RTX', '2');
            INSERT INTO Devices (device_id, kind, stable_key, name, first_seen, last_seen) VALUES (1, 'gpu', 'gpu-10de', 'NVIDIA GeForce RTX 4090', 0, 0);
            """);
    }

    private async Task ExecAsync(string database, string sql)
    {
        await using var c = await _factory.OpenReadWriteAsync(database, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task The_reader_lists_incidents_newest_first_with_trigger_counts()
    {
        var first = await _store.CreateAsync(Draft(), CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(30));
        var later = Draft(null) with { CenterAt = Center.AddMinutes(30), Title = "Captured incident" };
        var second = await _store.CreateAsync(later, CancellationToken.None);
        await _store.AddTriggerAsync(first.Id, new IncidentTrigger(Center.AddSeconds(5), IncidentSource.User, Note: "again"), CancellationToken.None);
        var reader = new IncidentIndexReader(_paths, _factory);

        var page = await reader.ListAsync(10, 0, CancellationToken.None);

        page.Total.ShouldBe(2);
        page.Items.Select(i => i.Key).ShouldBe([second.Key.ToString(), first.Key.ToString()]);
        var older = page.Items[1];
        older.TriggerCount.ShouldBe(2);
        older.UserType.ShouldBe(UserIncidentType.ScreenFlicker);
        older.PreSeconds.ShouldBe(600);
        older.PostSeconds.ShouldBe(120);
        older.State.ShouldBe(IncidentState.Recording);
        older.Notes.ShouldBe("flicker in game");
        (await reader.ListAsync(1, 1, CancellationToken.None)).Items.ShouldHaveSingleItem().Key.ShouldBe(first.Key.ToString());
    }

    [Fact]
    public async Task The_storage_manager_lists_incidents_oldest_first_with_pinned_and_finished()
    {
        var first = await _store.CreateAsync(Draft(), CancellationToken.None);
        await _store.FinalizeAsync(first, IncidentState.Complete, null, CancellationToken.None);
        var second = await _store.CreateAsync(Draft() with { CenterAt = Center.AddMinutes(-30) }, CancellationToken.None);
        await _store.UpdateAsync(second.Key, new IncidentUpdate(null, null, null, Pinned: true), CancellationToken.None);
        PcDiag.Core.Storage.IIncidentInventory inventory = _store;

        var list = await inventory.ListAsync(CancellationToken.None);

        list.Select(i => (i.Key, i.Pinned, i.Finished)).ShouldBe([(second.Key.ToString(), true, false), (first.Key.ToString(), false, true)]);
    }

    [Fact]
    public async Task Deleting_an_incident_whose_file_is_open_elsewhere_removes_it_from_the_index_and_leaves_the_folder()
    {
        var incident = await _store.CreateAsync(Draft(), CancellationToken.None);
        var folder = _paths.GetIncidentDirectory(incident.Key);
        var marker = Path.Combine(folder, "open.bin");
        await File.WriteAllBytesAsync(marker, [1, 2, 3]);

        bool deleted;
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            deleted = await _store.DeleteAsync(incident.Key, CancellationToken.None);
        }

        deleted.ShouldBeTrue();
        (await new IncidentIndexReader(_paths, _factory).ListAsync(10, 0, CancellationToken.None)).Total.ShouldBe(0);
        Directory.Exists(folder).ShouldBeTrue(); // the storage manager removes it once nothing holds it
    }

    [Fact]
    public async Task The_reader_returns_nothing_when_there_is_no_index_yet()
    {
        using var empty = new TempDataDirectory();
        var page = await new IncidentIndexReader(new DataPaths(empty.Path), _factory).ListAsync(10, 0, CancellationToken.None);

        page.Total.ShouldBe(0);
        page.Items.ShouldBeEmpty();
    }

    private async Task<object?> ScalarAsync(string database, string sql)
    {
        await using var c = await _factory.OpenReadWriteAsync(database, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task<List<string>> RowsAsync(string database, string sql)
    {
        await using var c = await _factory.OpenReadWriteAsync(database, CancellationToken.None);
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
