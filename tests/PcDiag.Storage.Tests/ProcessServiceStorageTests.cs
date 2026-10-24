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

public class ProcessSelectionTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    internal static ProcessMetrics Proc(int pid, double cpu = 0, long privateBytes = 0, double io = 0, ProcessState state = ProcessState.Running) => new(
        new ProcessKey(pid, 1000 + pid), "p" + pid, Reading<double>.Of(cpu), Reading<long>.Of(0), Reading<long>.Of(privateBytes), Reading<long>.Of(privateBytes),
        Reading<long>.Of(privateBytes), Reading<double>.Of(io), Reading<double>.Of(0), Reading<int>.Of(1), Reading<int>.Of(1), Reading<int>.Of(8), state);

    [Fact]
    public void Idle_and_inactive_processes_are_not_persisted()
    {
        var sample = new ProcessSample(T, [Proc(0, cpu: 95), Proc(10, cpu: 0.05), Proc(11, cpu: 5), Proc(12, io: 100)], [], []);

        ProcessSelection.Select(sample, null, topN: 25, includeMemoryRanking: false).Select(s => s.Metrics.Key.Pid).ShouldBe([11]);
    }

    [Fact]
    public void Top_n_applies_per_ranking_and_events_and_hangs_are_always_kept()
    {
        var processes = Enumerable.Range(1, 10).Select(i => Proc(i, cpu: i)).ToList();
        processes.Add(Proc(50, state: ProcessState.NotResponding));
        processes.Add(Proc(60));
        var events = new[] { new ProcessLifecycleEvent(T, new ProcessKey(60, 1060), "p60", ProcessEventKind.Started) };

        var selected = ProcessSelection.Select(new ProcessSample(T, processes, [], events), null, topN: 3, includeMemoryRanking: false)
            .Select(s => s.Metrics.Key.Pid).ToList();

        selected.ShouldBe([8, 9, 10, 50, 60], ignoreOrder: true);
    }

    [Fact]
    public void Memory_ranking_can_be_skipped()
    {
        var sample = new ProcessSample(T, [Proc(1, privateBytes: 1L << 30)], [], []);

        ProcessSelection.Select(sample, null, 25, includeMemoryRanking: false).ShouldBeEmpty();
        ProcessSelection.Select(sample, null, 25, includeMemoryRanking: true).Count.ShouldBe(1);
    }

    [Fact]
    public void Gpu_use_is_joined_across_adapters()
    {
        var sample = new ProcessSample(T, [Proc(7, cpu: 1)], [], []);
        var gpu = new GpuSample(T, [],
        [
            new GpuProcessUsage(7, "gpu-d", 40, "3D", Reading<long>.Of(1L << 30)),
            new GpuProcessUsage(7, "gpu-i", 5, "VideoDecode", Reading<long>.Of(1L << 20)),
        ]);

        var joined = ProcessSelection.Select(sample, gpu, 25).Single();

        joined.GpuPercent.ShouldBe(40);
        joined.GpuEngine.ShouldBe("3D");
        joined.GpuMemoryBytes.ShouldBe((1L << 30) + (1L << 20));
    }
}

public sealed class ProcessServiceStorageTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Processes_identities_events_and_services_are_persisted()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        await using var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);

        var key = new ProcessKey(42, 1042);
        var ts = _time.GetUtcNow();

        // Tick 1: process appears without a path; tick 2: enrichment adds the path; tick 3: it stops.
        await consumer.ConsumeAsync(Frame(1, ts, [ProcessSelectionTests.Proc(42, cpu: 3)], [Identity(key, path: null)], []), CancellationToken.None);
        await consumer.ConsumeAsync(Frame(2, ts.AddSeconds(2), [ProcessSelectionTests.Proc(42, cpu: 4)], [Identity(key, path: @"C:\app.exe")], []), CancellationToken.None);
        await consumer.ConsumeAsync(Frame(3, ts.AddSeconds(4), [], [Identity(key, path: null)], [new ProcessLifecycleEvent(ts.AddSeconds(4), key, "app.exe", ProcessEventKind.Stopped)]), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        await using var c = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(c, $"SELECT path FROM ProcessIdentity WHERE proc_key = {key.ToStorageId()};")).ShouldBe(@"C:\app.exe"); // not overwritten by NULL
        (await ScalarAsync(c, $"SELECT end_ts FROM ProcessIdentity WHERE proc_key = {key.ToStorageId()};")).ShouldBe(UnixTime.ToUnixMs(ts.AddSeconds(4)));
        (await ScalarAsync(c, "SELECT COUNT(*) FROM ProcessSnapshots;")).ShouldBe(2L);
        (await ScalarAsync(c, $"SELECT kind FROM ProcessEvents WHERE proc_key = {key.ToStorageId()};")).ShouldBe((long)ProcessEventKind.Stopped);
        (await ScalarAsync(c, "SELECT COUNT(*) FROM ServiceSnapshots;")).ShouldBe(2L);
        (await ScalarAsync(c, "SELECT kind FROM ServiceEvents WHERE name = 'Spooler';")).ShouldBe((long)ServiceEventKind.Stopped);
    }

    [Fact]
    public async Task A_recording_that_starts_after_watching_still_has_every_process_and_the_services()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        await using var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance);
        var busy = new ProcessKey(42, 1042);
        var ts = _time.GetUtcNow();

        // Watching (not stored): the process's identity and the service list are sent now, once, with a change after.
        await consumer.ConsumeAsync(Frame(1, ts, [ProcessSelectionTests.Proc(42, cpu: 30)], [Identity(busy, path: @"C:\busy.exe")], []) with { Recorded = false }, CancellationToken.None);
        await consumer.ConsumeAsync(new SampleFrame(2, ts.AddSeconds(2), Processes: new ProcessSample(ts.AddSeconds(2), [ProcessSelectionTests.Proc(42, cpu: 35)], [], []),
            Services: new ServiceSample(ts.AddSeconds(2), null, [new ServiceChange(ts.AddSeconds(2), "W32Time", "Windows Time", ServiceEventKind.Started, "Stopped", "Running", 77)]), Recorded: false), CancellationToken.None);

        // Recording starts: neither is sent again.
        var recorded = ts.AddSeconds(10);
        await consumer.ConsumeAsync(new SampleFrame(3, recorded, Processes: new ProcessSample(recorded, [ProcessSelectionTests.Proc(42, cpu: 40)], [], [])), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);
        await using (var write = new SqliteConnection($"Data Source={paths.TelemetryDatabase};Pooling=False"))
        {
            await write.OpenAsync();
            await using var session = write.CreateCommand();
            session.CommandText = $"INSERT INTO RecorderSessions (start_ts, start_reason, elevated) VALUES ({UnixTime.ToUnixMs(recorded)}, 1, 1);";
            await session.ExecuteNonQueryAsync();
        }

        var moment = await new PcDiag.Storage.Sqlite.Reading.HistoryReader(_factory).MomentAsync(paths.TelemetryDatabase, UnixTime.ToUnixMs(recorded), (_, _) => true, CancellationToken.None);
        var process = moment.Processes.ShouldHaveSingleItem();
        (process.Name, process.CpuPercent).ShouldBe(("app.exe", 40));
        var services = moment.Services.ShouldNotBeNull();
        // The list as it was sent (Spooler running), with the change sent while watching (W32Time started) replayed.
        services.Select(s => (s.Name, s.Status)).ShouldBe([("Spooler", "Running"), ("W32Time", "Running")], ignoreOrder: true);
        services.Single(s => s.Name == "W32Time").Pid.ShouldBe(77);
    }

    private static ProcessIdentityDetails Identity(ProcessKey key, string? path) =>
        new(key, 4, "app.exe", DateTimeOffset.UnixEpoch.AddYears(56), 1, path, null, null);

    private static SampleFrame Frame(long seq, DateTimeOffset ts, List<ProcessMetrics> processes, List<ProcessIdentityDetails> identities, List<ProcessLifecycleEvent> events)
    {
        ServiceSample? services = seq == 1
            ? new ServiceSample(ts,
                [new ServiceInfo("Spooler", "Print Spooler", ServiceStatus.Running, ServiceStartType.Automatic, 900, 16, null),
                 new ServiceInfo("W32Time", "Windows Time", ServiceStatus.Stopped, ServiceStartType.Manual, null, 32, null)],
                [new ServiceChange(ts, "Spooler", "Print Spooler", ServiceEventKind.Stopped, "Running", "Stopped")])
            : null;
        return new SampleFrame(seq, ts, Processes: new ProcessSample(ts, processes, identities, events), Services: services);
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
