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

public sealed class TelemetryStorageConsumerTests : IAsyncLifetime, IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly SqliteConnectionFactory _factory = new();
    private DataPaths _paths = null!;

    public async Task InitializeAsync()
    {
        _paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(_paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Frames_are_buffered_until_the_write_behind_interval()
    {
        await using var consumer = CreateConsumer(writeBehindSeconds: 5);

        await consumer.ConsumeAsync(Frame(1), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await consumer.ConsumeAsync(Frame(2), CancellationToken.None);
        (await CountAsync("SystemSnapshots")).ShouldBe(0);

        _time.Advance(TimeSpan.FromSeconds(4)); // 5 s since the last flush
        await consumer.ConsumeAsync(Frame(3), CancellationToken.None);
        (await CountAsync("SystemSnapshots")).ShouldBe(3);
    }

    [Fact]
    public async Task Flush_writes_system_cpu_and_memory_rows()
    {
        await using var consumer = CreateConsumer();

        await consumer.ConsumeAsync(Frame(1), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        await using var connection = await _factory.OpenReadOnlyAsync(_paths.TelemetryDatabase, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.seq, s.cpu_pct, s.mem_used_pct, s.collectors_missed, s.agent_cpu_pct,
                   c.total_pct, c.package_temp_c, c.per_core_util, c.per_core_mhz,
                   m.total_b, m.available_b, m.compressed_b, m.pressure
            FROM SystemSnapshots s JOIN CpuMetrics c ON c.ts = s.ts JOIN MemoryMetrics m ON m.ts = s.ts;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        reader.GetInt64(0).ShouldBe(1);
        reader.GetDouble(1).ShouldBe(37.5);
        reader.GetDouble(2).ShouldBe(75.0); // 12 of 16 GB in use
        reader.GetInt64(3).ShouldBe(0);
        reader.GetDouble(4).ShouldBe(0.4);
        reader.GetDouble(5).ShouldBe(37.5);
        reader.IsDBNull(6).ShouldBeTrue(); // unavailable -> NULL, never 0
        CoreBlobs.UnpackUtilization((byte[])reader[7]).ShouldBe([10.0, null]);
        CoreBlobs.UnpackFrequency((byte[])reader[8]).ShouldBe([5300.0, null]);
        reader.GetInt64(9).ShouldBe(16L << 30);
        reader.GetInt64(10).ShouldBe(4L << 30);
        reader.IsDBNull(11).ShouldBeTrue();
        reader.GetDouble(12).ShouldBe(0.75);
    }

    [Fact]
    public async Task Availability_is_recorded_once_per_change_with_a_reason()
    {
        await using var consumer = CreateConsumer();

        await consumer.ConsumeAsync(Frame(1), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        await consumer.ConsumeAsync(Frame(2), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        (await ScalarAsync("SELECT availability FROM SensorAvailability WHERE series = 'cpu.package_temp_c';")).ShouldBe((long)Availability.RequiresElevation);
        ((string)(await ScalarAsync("SELECT reason FROM SensorAvailability WHERE series = 'cpu.package_temp_c';"))!).ShouldContain("elevated");
        (await ScalarAsync("SELECT availability FROM SensorAvailability WHERE series = 'cpu.total_pct';")).ShouldBe((long)Availability.Available);
        (await ScalarAsync("SELECT COUNT(*) FROM SensorAvailability WHERE series = 'cpu.total_pct';")).ShouldBe(1L);
    }

    [Fact]
    public async Task Frames_without_cpu_or_memory_still_record_the_tick()
    {
        await using var consumer = CreateConsumer();

        await consumer.ConsumeAsync(new SampleFrame(7, _time.GetUtcNow()), CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        (await CountAsync("SystemSnapshots")).ShouldBe(1);
        (await CountAsync("CpuMetrics")).ShouldBe(0);
        (await ScalarAsync("SELECT cpu_pct FROM SystemSnapshots;")).ShouldBe(DBNull.Value);
    }

    [Fact]
    public async Task Recorder_sessions_start_end_and_recover_after_a_crash()
    {
        var store = new RecorderSessionStore(_paths, _factory);

        var clean = await store.StartSessionAsync(_time.GetUtcNow(), RecordingStartReason.User, elevated: false, "0.1.0", null, CancellationToken.None);
        await store.EndSessionAsync(clean, _time.GetUtcNow().AddMinutes(1), RecordingEndReason.User, CancellationToken.None);

        var crashed = await store.StartSessionAsync(_time.GetUtcNow().AddMinutes(2), RecordingStartReason.Always, elevated: true, "0.1.0", null, CancellationToken.None);
        await using (var consumer = CreateConsumer())
        {
            _time.Advance(TimeSpan.FromMinutes(3));
            await consumer.ConsumeAsync(Frame(1), CancellationToken.None);
            await consumer.FlushAsync(CancellationToken.None);
        }

        (await store.CloseAbandonedSessionsAsync(CancellationToken.None)).ShouldBe(1);
        (await ScalarAsync($"SELECT end_reason FROM RecorderSessions WHERE id = {crashed};")).ShouldBe((long)RecordingEndReason.Crash);
        (await ScalarAsync($"SELECT end_ts FROM RecorderSessions WHERE id = {crashed};")).ShouldBe(UnixTime.ToUnixMs(_time.GetUtcNow()));
        (await ScalarAsync($"SELECT end_reason FROM RecorderSessions WHERE id = {clean};")).ShouldBe((long)RecordingEndReason.User);
    }

    /// <summary>
    /// Processes that exited while nothing was recorded never got an end; the first full process list after a gap ends
    /// those that are gone (so a moment before the gap still lists them, and one after it does not).
    /// </summary>
    [Fact]
    public async Task Processes_that_ended_unseen_are_ended_by_the_first_process_list_after_a_gap()
    {
        var still = new ProcessKey(10, 1);
        var gone = new ProcessKey(20, 2);
        var later = new ProcessKey(30, 3);
        await using (var connection = await _factory.OpenReadWriteAsync(_paths.TelemetryDatabase, CancellationToken.None))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts) VALUES ($a, 10, 'still.exe', 1), ($b, 20, 'gone.exe', 1);";
            command.Parameters.AddWithValue("$a", still.ToStorageId());
            command.Parameters.AddWithValue("$b", gone.ToStorageId());
            await command.ExecuteNonQueryAsync();
        }

        await using var consumer = CreateConsumer();
        SampleFrame WithProcesses(long sequence, params ProcessKey[] running)
        {
            var d = Reading<double>.Of(0);
            var l = Reading<long>.Of(0);
            var n = Reading<int>.Of(1);
            var metrics = running.Select(k => new ProcessMetrics(k, "p.exe", d, l, l, l, l, d, d, n, n, n, ProcessState.Running)).ToList();
            return Frame(sequence) with { Processes = new ProcessSample(_time.GetUtcNow(), metrics, [], []) };
        }

        async Task<object?> EndOf(ProcessKey key)
        {
            await consumer.FlushAsync(CancellationToken.None);
            return await ScalarAsync($"SELECT end_ts FROM ProcessIdentity WHERE proc_key = {key.ToStorageId()};");
        }

        var first = _time.GetUtcNow().ToUnixTimeMilliseconds();
        await consumer.ConsumeAsync(WithProcesses(1, still), CancellationToken.None);
        (await EndOf(gone)).ShouldBe(first, "not running when recording started");
        (await EndOf(still)).ShouldBe(DBNull.Value);

        // A custom recording without process figures has no list and ends nothing; nor does a list just after another.
        _time.Advance(TimeSpan.FromMinutes(1));
        await consumer.ConsumeAsync(Frame(2) with { Processes = new ProcessSample(_time.GetUtcNow(), [], [], []) }, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(2));
        await consumer.ConsumeAsync(WithProcesses(3, still), CancellationToken.None);
        (await EndOf(still)).ShouldBe(DBNull.Value);

        _time.Advance(TimeSpan.FromSeconds(2));
        await consumer.ConsumeAsync(WithProcesses(4, later), CancellationToken.None);
        (await EndOf(still)).ShouldBe(DBNull.Value, "lists 2 s apart leave no gap: an exit there is recorded as an event");

        _time.Advance(TimeSpan.FromMinutes(5));
        await consumer.ConsumeAsync(WithProcesses(5, later), CancellationToken.None);
        (await EndOf(still)).ShouldBe(_time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// Besides the busiest processes every tick, every process is stored every 30 s when its figures changed and every
    /// 10 minutes when not, so any moment has figures for every process at little cost.
    /// </summary>
    [Fact]
    public async Task Every_process_is_stored_every_30_s_when_it_changed_and_every_10_minutes_when_not()
    {
        var zero = Reading<double>.Of(0);
        var none = Reading<long>.Of(0);
        var idleKey = new ProcessKey(40, 4);
        ProcessMetrics Idle(int handles) => new(idleKey, "idle.exe", zero, none, none, none, none, zero, zero, Reading<int>.Of(3), Reading<int>.Of(handles), Reading<int>.Of(8), ProcessState.Running);
        var busy = new ProcessMetrics(new ProcessKey(50, 5), "busy.exe", Reading<double>.Of(50), Reading<long>.Of(9), Reading<long>.Of(1 << 30), Reading<long>.Of(1 << 29),
            Reading<long>.Of(1 << 29), zero, zero, Reading<int>.Of(9), Reading<int>.Of(90), Reading<int>.Of(8), ProcessState.Running);
        var idle = Idle(100);
        await using var consumer = CreateConsumer();
        var sequence = 0;
        async Task Tick(ProcessMetrics idleNow, TimeSpan advance)
        {
            _time.Advance(advance);
            await consumer.ConsumeAsync(Frame(++sequence) with { Processes = new ProcessSample(_time.GetUtcNow(), [busy, idleNow], [], []) }, CancellationToken.None);
        }

        await Tick(idle, TimeSpan.Zero);                   // first full list: the idle process is stored
        await Tick(idle, TimeSpan.FromSeconds(2));         // 2 s later: busiest only
        await Tick(idle, TimeSpan.FromSeconds(30));        // full list, unchanged: not again
        await Tick(Idle(101), TimeSpan.FromSeconds(30));   // full list, changed: stored
        await Tick(Idle(101), TimeSpan.FromMinutes(11));   // unchanged for over 10 minutes: stored again
        await consumer.FlushAsync(CancellationToken.None);

        (await ScalarAsync($"SELECT COUNT(*) FROM ProcessSnapshots WHERE proc_key = {idleKey.ToStorageId()};")).ShouldBe(3L);
        (await ScalarAsync($"SELECT COUNT(*) FROM ProcessSnapshots WHERE proc_key = {busy.Key.ToStorageId()};")).ShouldBe(5L, "the busiest are stored every tick, once");
    }

    [Fact]
    public async Task A_service_change_keeps_the_process_it_ran_in()
    {
        await using var consumer = CreateConsumer();
        var change = new ServiceChange(_time.GetUtcNow(), "Dnscache", "DNS Client", PcDiag.Core.Events.ServiceEventKind.Restarted, "Running", "Running", 1234);

        await consumer.ConsumeAsync(Frame(1) with { Services = new ServiceSample(_time.GetUtcNow(), null, [change]) }, CancellationToken.None);
        await consumer.FlushAsync(CancellationToken.None);

        (await ScalarAsync("SELECT pid FROM ServiceEvents WHERE name = 'Dnscache';")).ShouldBe(1234L);
    }

    [Fact]
    public async Task While_the_drive_is_almost_full_frames_wait_in_memory_and_are_written_once_there_is_space()
    {
        var guard = new PcDiag.Core.Storage.StorageGuard();
        guard.Set(true);
        await using var consumer = CreateConsumer(guard: guard);

        for (var i = 1; i <= 3; i++)
        {
            await consumer.ConsumeAsync(Frame(i), CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        await consumer.FlushAsync(CancellationToken.None);
        (await CountAsync("SystemSnapshots")).ShouldBe(0);

        guard.Set(false);
        await consumer.FlushAsync(CancellationToken.None);

        (await CountAsync("SystemSnapshots")).ShouldBe(3);
    }

    private TelemetryStorageConsumer CreateConsumer(int writeBehindSeconds = 5, PcDiag.Core.Storage.IStorageGuard? guard = null) =>
        new(_paths, _factory, new StaticMonitor(new StorageOptions { WriteBehindSeconds = writeBehindSeconds }), _time, NullLogger<TelemetryStorageConsumer>.Instance, guard: guard);

    private SampleFrame Frame(long sequence)
    {
        var ts = _time.GetUtcNow();
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        var cpu = new CpuSample(
            ts, Reading<double>.Of(37.5), Reading<double>.Of(20), Reading<double>.Of(17.5), Reading<double>.Of(4800), Reading<double>.Of(3700),
            Reading<double>.Of(130), Reading<double>.Of(100), Reading<long>.Of(0), Reading<double>.Of(0), Reading<double>.Of(1000), Reading<double>.Of(900),
            Reading<double>.Unavailable(Availability.RequiresElevation), Reading<double>.Unavailable(Availability.RequiresElevation),
            [new CpuCoreSample(0, Reading<double>.Of(10), Reading<double>.Of(5300)), new CpuCoreSample(1, Reading<double>.Pending, none)]);
        var memory = new MemorySample(
            ts, Reading<long>.Of(16L << 30), Reading<long>.Of(4L << 30), Reading<long>.Of(1L << 30), Reading<long>.Of(2L << 30),
            Reading<long>.Of(20L << 30), Reading<long>.Of(32L << 30), Reading<long>.Of(1L << 29), Reading<long>.Of(1L << 28),
            Reading<long>.Unavailable(Availability.Unsupported), Reading<double>.Of(100), Reading<double>.Of(2), Reading<double>.Of(5), Reading<double>.Of(0.75));
        var diagnostics = new TickDiagnostics(TimeSpan.FromMilliseconds(3), 2, 0, Reading<double>.Of(0.4), 50_000_000);
        return new SampleFrame(sequence, ts, cpu, memory, Diagnostics: diagnostics);
    }

    private Task<int> CountAsync(string table) => ScalarAsync($"SELECT COUNT(*) FROM {table};").ContinueWith(t => Convert.ToInt32(t.Result, System.Globalization.CultureInfo.InvariantCulture));

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await _factory.OpenReadOnlyAsync(_paths.TelemetryDatabase, CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private sealed class StaticMonitor(StorageOptions value) : IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue => value;

        public StorageOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }
}
