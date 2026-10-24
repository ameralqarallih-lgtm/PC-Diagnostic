using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public sealed class AnomalyStorageTests : IDisposable
{
    private static readonly DateTimeOffset Center = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly FakeTimeProvider _time = new(Center);
    private readonly DataPaths _paths;
    private readonly DatabaseInitializer _initializer;
    private readonly IncidentStore _incidents;
    private readonly AnomalyStore _anomalies;
    private readonly AnomalyReader _reader;

    public AnomalyStorageTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _paths = new DataPaths(_dir.Path);
        _initializer = new DatabaseInitializer(_paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance);
        _initializer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        _incidents = new IncidentStore(_paths, _factory, _initializer, _time);
        _anomalies = new AnomalyStore(_paths, _factory);
        _reader = new AnomalyReader(_factory);
    }

    public void Dispose() => _dir.Dispose();

    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private static Anomaly GpuAnomaly(DateTimeOffset start, DateTimeOffset? end = null) => new(
        0, "gpu-baseline", "gpu.util", start, end, 99, Severity.Low,
        new AnomalyReasoning("GPU utilization anomaly", "GPU utilization more than 3 σ above its 30 min baseline for at least 15 s", "40–70 % (mean 55, sd 8, 30 min)", 55, 8,
            "99 % (z = 5.5)", TimeSpan.FromSeconds(18)),
        "NVIDIA GeForce RTX 4090");

    private static IncidentDraft Draft() => new(
        Center, Center, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), IncidentSource.Rule, null, "Unusual GPU load", null, Severity.Low, false,
        new IncidentTrigger(Center, IncidentSource.Rule, RuleId: "gpu-baseline", Detail: "Unusual GPU load: 99 %"), "1.0.0");

    [Fact]
    public async Task Anomalies_are_stored_updated_linked_and_read_back_with_their_reasoning()
    {
        var id = await _anomalies.AddAsync(GpuAnomaly(Center.AddSeconds(-18)), CancellationToken.None);
        await _anomalies.UpdateAsync(GpuAnomaly(Center.AddSeconds(-18), Center.AddSeconds(5)) with { Id = id, Observed = 100 }, CancellationToken.None);
        await _anomalies.LinkIncidentAsync(id, "INC-20261006-00001", CancellationToken.None);

        var info = (await _reader.ListAsync(_paths.TelemetryDatabase, Ms(Center.AddHours(-1)), Ms(Center.AddHours(1)), 10, CancellationToken.None)).ShouldHaveSingleItem();

        info.Id.ShouldBe(id);
        info.Classification.ShouldBe("GPU utilization anomaly");
        info.Device.ShouldBe("NVIDIA GeForce RTX 4090");
        info.StartedAt.ShouldBe(Ms(Center.AddSeconds(-18)));
        info.EndedAt.ShouldBe(Ms(Center.AddSeconds(5)));
        info.Observed.ShouldBe(100);
        info.Unit.ShouldBe("%");
        info.Baseline.ShouldBe("40–70 % (mean 55, sd 8, 30 min)");
        info.DurationSeconds.ShouldBe(18);
        info.IncidentKey.ShouldBe("INC-20261006-00001");
        info.Summary.ShouldStartWith("Normally 40–70 %");
        (await _reader.ListAsync(_paths.TelemetryDatabase, Ms(Center.AddHours(1)), Ms(Center.AddHours(2)), 10, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Anomalies_left_open_by_a_crash_end_at_their_stored_duration()
    {
        await _anomalies.AddAsync(GpuAnomaly(Center.AddMinutes(-5)), CancellationToken.None);
        var current = await _anomalies.AddAsync(GpuAnomaly(Center.AddSeconds(10)), CancellationToken.None);

        (await _anomalies.CloseAbandonedAsync(Center, CancellationToken.None)).ShouldBe(1);

        var rows = await _reader.ListAsync(_paths.TelemetryDatabase, 0, long.MaxValue, 10, CancellationToken.None);
        rows.Single(r => r.Id != current).EndedAt.ShouldBe(Ms(Center.AddMinutes(-5).AddSeconds(18)));
        rows.Single(r => r.Id == current).EndedAt.ShouldBeNull("started by this run");
    }

    [Fact]
    public async Task An_incident_keeps_the_latest_state_of_its_anomalies_and_shows_them_in_its_timeline()
    {
        var id = await _anomalies.AddAsync(GpuAnomaly(Center.AddSeconds(-18)), CancellationToken.None);
        var incident = await _incidents.CreateAsync(Draft(), CancellationToken.None);
        await _incidents.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center, [], CancellationToken.None);

        // The anomaly ends and gets its incident after the pre-window was saved.
        await _anomalies.UpdateAsync(GpuAnomaly(Center.AddSeconds(-18), Center.AddSeconds(5)) with { Id = id }, CancellationToken.None);
        await _anomalies.LinkIncidentAsync(id, incident.Key.ToString(), CancellationToken.None);
        await _incidents.CaptureWindowAsync(incident, Center, Center.AddMinutes(2), [], CancellationToken.None);
        await _incidents.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);

        var database = Path.Combine(_paths.GetIncidentDirectory(incident.Key), "incident.db");
        var copied = (await _reader.ListAsync(database, 0, long.MaxValue, 10, CancellationToken.None)).ShouldHaveSingleItem();
        copied.EndedAt.ShouldBe(Ms(Center.AddSeconds(5)));
        copied.IncidentKey.ShouldBe(incident.Key.ToString());

        var reader = new IncidentReader(_paths, _factory, new IncidentIndexReader(_paths, _factory));
        var entry = (await reader.TimelineAsync(incident.Key.ToString(), CancellationToken.None)).Single(e => e.Kind == "anomaly");
        entry.Title.ShouldBe("GPU utilization anomaly on NVIDIA GeForce RTX 4090: 99 % (z = 5.5)");
        entry.Importance.ShouldBe(1);
        entry.Detail!.Value.GetProperty("baseline").GetString().ShouldBe("40–70 % (mean 55, sd 8, 30 min)");
    }

    [Fact]
    public async Task Findings_replace_the_previous_analysis_and_record_the_analyzer_version()
    {
        var incident = await _incidents.CreateAsync(Draft(), CancellationToken.None);
        await _incidents.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        (await _incidents.ListUnanalyzedAsync(1, 10, CancellationToken.None)).ShouldBe([incident.Key]);

        await _incidents.SaveFindingsAsync(incident.Key, [new Finding(EvidenceLevel.Observation, "old", [])], 1, CancellationToken.None);
        var findings = new[]
        {
            new Finding(EvidenceLevel.Observation, "GPU utilization reached 99 %.", [new EvidenceRef("metric", Center, "gpu.3.util")]),
            new Finding(EvidenceLevel.NotProven, "The collected telemetry does not establish what caused the capture.", []),
        };
        (await _incidents.SaveFindingsAsync(incident.Key, findings, 2, CancellationToken.None)).ShouldBeTrue();

        var reader = new IncidentReader(_paths, _factory, new IncidentIndexReader(_paths, _factory));
        var stored = await reader.FindingsAsync(incident.Key.ToString(), CancellationToken.None);
        stored.Select(f => (f.Ordinal, f.Level, f.Text)).ShouldBe([(1, "Observation", "GPU utilization reached 99 %."), (2, "NotProven", findings[1].Text)]);
        stored[0].Evidence!.Value[0].GetProperty("sourceId").GetString().ShouldBe("gpu.3.util");
        stored[0].Evidence!.Value[0].GetProperty("ts").GetInt64().ShouldBe(Ms(Center));
        (await reader.DetailAsync(incident.Key.ToString(), CancellationToken.None))!.Info["analysis_version"].ShouldBe("2");
        (await _incidents.ListUnanalyzedAsync(2, 10, CancellationToken.None)).ShouldBeEmpty();
        (await _incidents.ListUnanalyzedAsync(3, 10, CancellationToken.None)).ShouldBe([incident.Key]);
        (await _incidents.SaveFindingsAsync(IncidentKey.Create(new DateOnly(2026, 10, 6), 99), findings, 2, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task The_evidence_reader_gives_the_analyzers_tracks_events_anomalies_and_triggers()
    {
        await SeedAsync();
        await _anomalies.AddAsync(GpuAnomaly(Center.AddSeconds(-18), Center.AddSeconds(5)), CancellationToken.None);
        var incident = await _incidents.CreateAsync(Draft(), CancellationToken.None);
        await _incidents.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center.AddMinutes(2), [], CancellationToken.None);
        await _incidents.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        var reader = new IncidentReader(_paths, _factory, new IncidentIndexReader(_paths, _factory));
        var evidence = new IncidentEvidenceReader(reader, new HistoryReader(_factory), _factory, _initializer);

        var loaded = (await evidence.LoadAsync(incident.Key, CancellationToken.None)).ShouldNotBeNull();

        loaded.CenterAt.ShouldBe(Center);
        loaded.From.ShouldBe(Center.AddMinutes(-10));
        loaded.Source.ShouldBe(IncidentSource.Rule);
        var util = loaded.Tracks("gpu.util").ShouldHaveSingleItem();
        util.SeriesId.ShouldBe("gpu.1.util");
        util.Device.ShouldBe("NVIDIA GeForce RTX 4090");
        util.Points.Count.ShouldBe(120);
        util.Points[^1].Value.ShouldBe(99);
        loaded.Track("cpu").ShouldNotBeNull().Points.Count.ShouldBe(120);
        loaded.Metrics.ShouldNotContain(m => m.SeriesId == "gpu.1.fan", "only what the analyzers use is read");

        var tdr = loaded.Events.Single(e => e.Kind == "windows-event");
        tdr.Category.ShouldBe(EventCategory.Display);
        tdr.EventId.ShouldBe(4101);
        tdr.Provider.ShouldBe("Display");
        tdr.ToRef().SourceId.ShouldBe("WindowsEvents:7");
        loaded.Anomalies.ShouldHaveSingleItem().Reasoning.Classification.ShouldBe("GPU utilization anomaly");
        loaded.Triggers.ShouldHaveSingleItem().RuleId.ShouldBe("gpu-baseline");
        (await evidence.LoadAsync(IncidentKey.Create(new DateOnly(2026, 10, 6), 99), CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>
    /// Incidents recorded before the GPU collector skipped WSL2's virtual twin of the card hold it as a second GPU: its
    /// zero-load tracks must not reach the analyzers. A second real identical card and an unrelated GPU are kept.
    /// </summary>
    [Fact]
    public async Task The_evidence_reader_leaves_out_a_recorded_virtual_twin_of_a_gpu()
    {
        await SeedAsync();
        await using (var connection = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            const string nvml = """{"vendorId":4318,"deviceId":9860,"detailSources":["nvml","d3dkmt"]}""";
            const string d3dkmt = """{"vendorId":4318,"deviceId":9860,"detailSources":["d3dkmt"]}""";
            const string intel = """{"vendorId":32902,"detailSources":["d3dkmt"]}""";
            var rows = string.Join(",\n", Enumerable.Range(1, 120).SelectMany(s => Enumerable.Range(2, 3).Select(a => $"({Ms(Center.AddSeconds(-120 + s))}, {a}, 0, 2100)")));
            command.CommandText =
                $"""
                UPDATE Devices SET stable_key = 'gpu-10de-2684-467519da', details_json = '{nvml}' WHERE device_id = 1;
                INSERT INTO Devices (device_id, kind, stable_key, name, details_json, first_seen, last_seen) VALUES
                    (2, 'gpu', 'gpu-10de-2684-467519da-1', 'NVIDIA GeForce RTX 4090', '{d3dkmt}', 0, 0),
                    (3, 'gpu', 'gpu-8086-7d67-0', 'Intel(R) Graphics', '{intel}', 0, 0),
                    (4, 'gpu', 'gpu-10de-2684-467519da-2', 'NVIDIA GeForce RTX 4090', '{nvml}', 0, 0);
                INSERT INTO GpuMetrics (ts, adapter_id, util_pct, core_mhz) VALUES {rows};
                """;
            await command.ExecuteNonQueryAsync();
        }

        var incident = await _incidents.CreateAsync(Draft(), CancellationToken.None);
        await _incidents.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center.AddMinutes(2), [], CancellationToken.None);
        await _incidents.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        var evidence = new IncidentEvidenceReader(new IncidentReader(_paths, _factory, new IncidentIndexReader(_paths, _factory)), new HistoryReader(_factory), _factory, _initializer);

        var loaded = (await evidence.LoadAsync(incident.Key, CancellationToken.None)).ShouldNotBeNull();

        loaded.Tracks("gpu.util").Select(t => t.SeriesId).Order(StringComparer.Ordinal).ShouldBe(["gpu.1.util", "gpu.3.util", "gpu.4.util"]);
        loaded.Metrics.ShouldNotContain(m => m.SeriesId.StartsWith("gpu.2.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_incident_of_a_custom_recording_gives_the_analyzers_its_selection()
    {
        await SeedAsync();
        var custom = RecordingSelection.Of([RecordingItems.CpuUsage, RecordingItems.Gpu("gpu-10de", "usage")]);
        var incident = await _incidents.CreateAsync(Draft() with { Selection = custom }, CancellationToken.None);
        await _incidents.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center.AddMinutes(2), [], CancellationToken.None);
        await _incidents.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        var reader = new IncidentReader(_paths, _factory, new IncidentIndexReader(_paths, _factory));
        var evidence = new IncidentEvidenceReader(reader, new HistoryReader(_factory), _factory, _initializer);

        var loaded = (await evidence.LoadAsync(incident.Key, CancellationToken.None)).ShouldNotBeNull();

        loaded.RecordedItems.ShouldNotBeNull().SetEquals(custom.Items).ShouldBeTrue();
        loaded.Recorded(RecordingItems.CpuUsage).ShouldBeTrue();
        loaded.Recorded(RecordingItems.CpuUsage, RecordingItems.MemoryUsage).ShouldBeFalse();
        (await reader.DetailAsync(incident.Key.ToString(), CancellationToken.None))!.Info["recording_selection"].ShouldBe("[\"cpu.usage\",\"gpu:gpu-10de.usage\"]");
        IncidentEvidenceReader.RecordedItems("not json").ShouldBeNull();
        IncidentEvidenceReader.RecordedItems(null).ShouldBeNull();
    }

    /// <summary>Regression: analysis reads the incident with pooled read-only connections, then writes its findings.</summary>
    [Fact]
    public async Task Findings_can_be_saved_right_after_the_evidence_was_read_and_while_someone_else_reads_it()
    {
        await SeedAsync();
        var incident = await _incidents.CreateAsync(Draft(), CancellationToken.None);
        await _incidents.CaptureWindowAsync(incident, Center.AddMinutes(-10), Center.AddMinutes(2), [], CancellationToken.None);
        await _incidents.FinalizeAsync(incident, IncidentState.Complete, null, CancellationToken.None);
        var reader = new IncidentReader(_paths, _factory, new IncidentIndexReader(_paths, _factory));
        var evidence = new IncidentEvidenceReader(reader, new HistoryReader(_factory), _factory, _initializer);
        var database = Path.Combine(_paths.GetIncidentDirectory(incident.Key), "incident.db");
        Finding[] findings = [new(EvidenceLevel.NotProven, "The collected telemetry does not establish what caused the capture.", [])];

        // Same process: the readers' idle pooled connections must not block the switch back to a single file.
        await evidence.LoadAsync(incident.Key, CancellationToken.None);
        (await _incidents.SaveFindingsAsync(incident.Key, findings, 1, CancellationToken.None)).ShouldBeTrue();
        (await JournalModeAsync(database)).ShouldBe("delete");

        // Another process (the Web host) keeps a connection open and reads now and then: the findings are still saved
        // (the file may stay in WAL mode until the next write).
        await using (var other = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False"))
        {
            await other.OpenAsync();
            await using var read = other.CreateCommand();
            read.CommandText = "SELECT COUNT(*) FROM Findings;";
            await read.ExecuteScalarAsync();
            (await _incidents.SaveFindingsAsync(incident.Key, findings, 2, CancellationToken.None)).ShouldBeTrue();
            (await read.ExecuteScalarAsync()).ShouldBe(1L);
            (await _incidents.SaveFindingsAsync(incident.Key, findings, 2, CancellationToken.None)).ShouldBeTrue();
        }

        (await _incidents.ListUnanalyzedAsync(2, 10, CancellationToken.None)).ShouldBeEmpty();
        (await reader.FindingsAsync(incident.Key.ToString(), CancellationToken.None)).ShouldHaveSingleItem();
    }

    private static async Task<string?> JournalModeAsync(string database)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task SeedAsync()
    {
        await using var connection = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None);
        await using var command = connection.CreateCommand();
        var rows = string.Join(",\n", Enumerable.Range(1, 120).Select(s => $"({Ms(Center.AddSeconds(-120 + s))}, 1, {(s == 120 ? 99 : 40)}, 2100)"));
        var system = string.Join(",\n", Enumerable.Range(1, 120).Select(s => $"({Ms(Center.AddSeconds(-120 + s))}, {s}, 30)"));
        command.CommandText =
            $"""
            INSERT INTO Devices (device_id, kind, stable_key, name, first_seen, last_seen) VALUES (1, 'gpu', 'gpu-10de', 'NVIDIA GeForce RTX 4090', 0, 0);
            INSERT INTO GpuMetrics (ts, adapter_id, util_pct, core_mhz) VALUES {rows};
            INSERT INTO SystemSnapshots (ts, seq, cpu_pct) VALUES {system};
            INSERT INTO WindowsEvents (id, ts, channel, record_id, provider, event_id, level, category, severity, title, rule_id)
                VALUES (7, {Ms(Center.AddSeconds(-2))}, 'System', 1, 'Display', 4101, 3, {(int)EventCategory.Display}, 3, 'Display driver nvlddmkm stopped responding and has successfully recovered', 'gpu.tdr');
            """;
        await command.ExecuteNonQueryAsync();
    }
}
