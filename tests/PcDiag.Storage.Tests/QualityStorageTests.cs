using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Migrations;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Tests;

public sealed class QualityStorageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);
    private readonly TempDataDirectory _dir = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly SqliteConnectionFactory _factory = new();

    public void Dispose() => _dir.Dispose();

    private static CpuSample Cpu(DateTimeOffset at, double busiestDpc)
    {
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        var cores = new List<CpuCoreSample>
        {
            new(0, Reading<double>.Of(97), none, Reading<double>.Of(busiestDpc), Reading<double>.Of(1)),
            new(1, Reading<double>.Of(30), none, Reading<double>.Of(0.4), Reading<double>.Of(0.2)),
        };
        return new CpuSample(at, Reading<double>.Of(40), none, none, none, none, none, none, Reading<long>.Unavailable(Availability.Unsupported), none, none, none, none, none, cores,
            Reading<double>.Of(2), Reading<double>.Of(0.6), Reading<double>.Of(5000));
    }

    /// <summary>A second of a game at 120 fps (60 from 20:01:10 to 20:01:14), pings with every 10th internet ping lost, a DNS check every 10 s.</summary>
    private static SampleFrame Frame(int second, bool recorded, DriverLatencySample? latency = null)
    {
        var at = T0.AddSeconds(second);
        var fps = second is >= 70 and < 75 ? 60 : 120;
        // Windowed from 20:02:00, through the desktop compositor; 2 frames a second never shown.
        var frames = new FrameSample(at, 42, "ArkAscended", fps, 1000.0 / fps, second == 72 ? 150 : 9, second == 72 ? 1 : 0, 100, second >= 30 ? 120 : null, second < 120,
            DisplayedFps: fps - 2, Dropped: 2, WorstDisplayedMs: 12, DisplayLatencyMs: 12, GpuBusyPercent: 80, CpuBusyPercent: 30,
            PresentMode: second < 120 ? "Hardware: Independent Flip" : "Composed: Flip", Recognized: "library");
        var connection = new ConnectionSample(at, new PingResult(true, 1.2), new PingResult(true, 9), new PingResult(true, second % 10 == 0 ? null : 20 + (second % 3)),
            second % 10 == 5 ? 30 : null, second % 10 == 5 ? second == 25 : null, "100.64.0.1");
        return new SampleFrame(second, at, Cpu: Cpu(at, second == 72 ? 25 : 1.5), Recorded: recorded, Watched: !recorded, Frames: frames, Connection: connection, DriverLatency: latency);
    }

    [Fact]
    public async Task Watched_frames_keep_the_quality_history_and_recorded_ones_their_seconds_too()
    {
        var paths = await InitializeAsync();
        await using (var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance))
        {
            // Two minutes and a half only watched…
            for (var s = 0; s < 150; s++)
            {
                await consumer.ConsumeAsync(Frame(s, recorded: false), CancellationToken.None);
            }

            // …then recorded, with a driver measurement…
            var latency = new DriverLatencySample(T0.AddSeconds(160), T0.AddSeconds(150), 10, "stutter",
                [new DriverLatency("nvlddmkm.sys", 4000, 1300, 50_000, 0, 0, 0, 9, 2), new DriverLatency("ndis.sys", 300, 90, 4000, 200, 40, 900, 0, 0)]);
            for (var s = 150; s < 200; s++)
            {
                await consumer.ConsumeAsync(Frame(s, recorded: true, s == 160 ? latency : null), CancellationToken.None);
            }

            // …and the watched seconds stored again as part of an incident are not counted twice.
            for (var s = 100; s < 110; s++)
            {
                await consumer.ConsumeAsync(Frame(s, recorded: true), CancellationToken.None);
            }

            await consumer.FlushAsync(CancellationToken.None);
        }

        await using var connection = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM QualityMinutes;")).ShouldBe(4L);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM FrameMetrics;")).ShouldBe(60L); // 50 recorded + 10 stored again
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM DriverLatency;")).ShouldBe(2L);
        (await ScalarAsync(connection, "SELECT dpc_core_pct FROM CpuMetrics WHERE ts = $p0;", UnixTime.ToUnixMs(T0.AddSeconds(160)))).ShouldBe(1.5);
        (await ScalarAsync(connection, "SELECT core_max_pct FROM CpuMetrics WHERE ts = $p0;", UnixTime.ToUnixMs(T0.AddSeconds(160)))).ShouldBe(97.0);
        (await ScalarAsync(connection, "SELECT internet_loss FROM ConnectionMetrics WHERE ts = $p0;", UnixTime.ToUnixMs(T0.AddSeconds(160)))).ShouldBe(100.0);

        var reader = new QualityReader(_factory, new HistoryReader(_factory));
        var minutes = await reader.MinutesAsync(paths.TelemetryDatabase, UnixTime.ToUnixMs(T0), UnixTime.ToUnixMs(T0.AddMinutes(5)), CancellationToken.None);
        minutes.Count.ShouldBe(4);
        var second = minutes[1]; // 20:01, with the drop
        second.Seconds.ShouldBe(60);
        second.Game.ShouldBe("ArkAscended");
        second.GameSeconds.ShouldBe(60);
        second.FpsMin.ShouldBe(60);
        second.FpsAvg.ShouldBe(115);
        second.DropSeconds.ShouldBe(5);
        second.Stutters.ShouldBe(1);
        second.WorstMs.ShouldBe(150);
        second.DpcCoreMax.ShouldBe(25);
        second.Internet.Loss.ShouldBe(10);
        second.Internet.Ms!.Value.ShouldBe(21, 0.1);
        second.Router.Loss.ShouldBe(0);
        second.DnsChecks.ShouldBe(6);
        second.DnsFailed.ShouldBe(0);
        second.DisplayedFps.ShouldBe(113);
        second.Dropped.ShouldBe(120);
        second.DisplayLatencyMs.ShouldBe(12);
        second.GpuBusyPercent.ShouldBe(80);
        second.CpuBusyPercent.ShouldBe(30);
        second.PresentMode.ShouldBe("Hardware: Independent Flip");
        minutes[2].PresentMode.ShouldBe("Composed: Flip");
        minutes[0].DnsFailed.ShouldBe(1); // the check at 20:00:25 failed
        minutes[0].DropSeconds.ShouldBe(0);

        var session = (await reader.SessionsAsync(paths.TelemetryDatabase, 0, UnixTime.ToUnixMs(T0.AddHours(1)), CancellationToken.None)).ShouldHaveSingleItem();
        session.Game.ShouldBe("ArkAscended");
        session.Seconds.ShouldBe(200);
        session.Start.ShouldBe(T0);
        session.End.ShouldBe(T0.AddSeconds(199));
        session.DropSeconds.ShouldBe(5);
        session.Stutters.ShouldBe(1);
        session.FpsP1.ShouldBe(60);
        session.DisplayedFps!.Value.ShouldBe(116.5, 0.1);
        session.Dropped.ShouldBe(400);
        session.PresentMode.ShouldBe("Hardware: Independent Flip"); // 120 s of 200
        session.WindowedPercent.ShouldBe(40);
        session.Recognized.ShouldBe("library");
        (await reader.SessionAsync(paths.TelemetryDatabase, session.Id, CancellationToken.None))!.Seconds.ShouldBe(200);

        var seconds = await reader.FrameSecondsAsync(paths.TelemetryDatabase, UnixTime.ToUnixMs(T0), UnixTime.ToUnixMs(T0.AddMinutes(5)), CancellationToken.None);
        seconds.Count.ShouldBe(60);
        seconds[0].UsualFps.ShouldBe(120);
        seconds[0].DisplayedFps.ShouldBe(118);
        seconds[0].GpuBusyPercent.ShouldBe(80);

        var measurement = (await reader.DriverLatencyAsync(paths.TelemetryDatabase, 0, UnixTime.ToUnixMs(T0.AddHours(1)), CancellationToken.None)).ShouldHaveSingleItem();
        measurement.Reason.ShouldBe("stutter");
        measurement.Drivers.Select(d => d.Driver).ShouldBe(["nvlddmkm.sys", "ndis.sys"]);
        measurement.Drivers[0].Over1Ms.ShouldBe(2);
    }

    [Fact]
    public async Task Quality_history_can_be_turned_off()
    {
        var paths = await InitializeAsync();
        await using (var consumer = new TelemetryStorageConsumer(paths, _factory, new Monitor(), _time, NullLogger<TelemetryStorageConsumer>.Instance,
                         recorder: new RecorderMonitor(new RecorderOptions { QualityHistory = false })))
        {
            for (var s = 0; s < 130; s++)
            {
                await consumer.ConsumeAsync(Frame(s, recorded: false), CancellationToken.None);
            }

            await consumer.FlushAsync(CancellationToken.None);
        }

        await using var connection = await _factory.OpenReadOnlyAsync(paths.TelemetryDatabase, CancellationToken.None);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM QualityMinutes;")).ShouldBe(0L);
        (await ScalarAsync(connection, "SELECT COUNT(*) FROM GameSessions;")).ShouldBe(0L);
    }

    [Fact]
    public async Task Programs_activity_before_and_during_a_moment_counts_unstored_ticks_as_idle()
    {
        var paths = await InitializeAsync();
        await using (var connection = await _factory.OpenReadWriteAsync(paths.TelemetryDatabase, CancellationToken.None))
        {
            await ExecAsync(connection, "INSERT INTO ProcessIdentity (proc_key, pid, name, start_ts) VALUES (1, 1, 'Game', 0), (2, 2, 'MsMpEng', 0);");
            for (var s = 0; s < 60; s += 2)
            {
                var ts = UnixTime.ToUnixMs(T0.AddSeconds(s));
                await ExecAsync(connection, $"INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, net_rx_bps, net_tx_bps, net_retrans_ps) VALUES ({ts}, 1, 30, 50000, 10000, 0.5);");
                if (s >= 40)
                {
                    await ExecAsync(connection, $"INSERT INTO ProcessSnapshots (ts, proc_key, cpu_pct, disk_read_bps) VALUES ({ts}, 2, 20, 80000000);");
                }
            }
        }

        var reader = new QualityReader(_factory, new HistoryReader(_factory));
        var activity = await reader.ProcessActivityAsync(paths.TelemetryDatabase, UnixTime.ToUnixMs(T0), UnixTime.ToUnixMs(T0.AddSeconds(40)), UnixTime.ToUnixMs(T0.AddSeconds(59)),
            CancellationToken.None);
        var scan = activity.Single(a => a.Name == "MsMpEng");
        scan.CpuBefore.ShouldBe(0);
        scan.CpuDuring.ShouldBe(20);
        scan.DiskDuring.ShouldBe(80_000_000);

        var programs = await reader.NetworkProgramsAsync(paths.TelemetryDatabase, 0, UnixTime.ToUnixMs(T0.AddHours(1)), 10, CancellationToken.None);
        var game = programs.ShouldHaveSingleItem();
        game.ReceiveBps.ShouldBe(50_000);
        game.RetransmitsPerSec.ShouldBe(0.5);
    }

    private async Task<DataPaths> InitializeAsync()
    {
        var paths = new DataPaths(_dir.Path);
        await new DatabaseInitializer(paths, _factory, new SchemaMigrator(NullLogger<SchemaMigrator>.Instance), _time, NullLogger<DatabaseInitializer>.Instance)
            .InitializeAsync(CancellationToken.None);
        return paths;
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, params object[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("$p" + i, values[i]);
        }

        return await command.ExecuteScalarAsync();
    }

    private sealed class Monitor : IOptionsMonitor<StorageOptions>
    {
        public StorageOptions CurrentValue { get; } = new();

        public StorageOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<StorageOptions, string?> listener) => null;
    }

    private sealed class RecorderMonitor(RecorderOptions value) : IOptionsMonitor<RecorderOptions>
    {
        public RecorderOptions CurrentValue => value;

        public RecorderOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<RecorderOptions, string?> listener) => null;
    }
}
