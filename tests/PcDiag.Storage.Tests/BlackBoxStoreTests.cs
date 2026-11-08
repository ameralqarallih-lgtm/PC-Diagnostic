using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.BlackBox;

namespace PcDiag.Storage.Tests;

/// <summary>The crash black box keeps the latest watched minutes on disk and gives the previous run's back after a restart.</summary>
public sealed class BlackBoxStoreTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();
    private readonly SqliteConnectionFactory _factory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 17, 0, 0, TimeSpan.Zero));
    private readonly Monitor<RecorderOptions> _recorder = new(new RecorderOptions());
    private readonly DataPaths _paths;
    private long _sequence;

    public BlackBoxStoreTests() => _paths = new DataPaths(_dir.Path);

    public void Dispose() => _dir.Dispose();

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; set; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private BlackBoxStore Store() => new(_paths, _factory, _recorder, new Monitor<SamplingOptions>(new SamplingOptions { TopProcessCount = 5 }), _time, NullLogger<BlackBoxStore>.Instance);

    private static ProcessMetrics Process(int pid, double cpu) => new(
        new ProcessKey(pid, 1000 + pid), "p" + pid, Reading<double>.Of(cpu), Reading<long>.Of(pid * 10), Reading<long>.Of(pid << 20), Reading<long>.Of(pid << 20),
        Reading<long>.Of(pid << 20), Reading<double>.Of(0), Reading<double>.Of(0), Reading<int>.Of(4), Reading<int>.Of(90), Reading<int>.Of(8), ProcessState.Running);

    /// <summary>A frame with most of what a real one holds: CPU, memory, GPU with its processes, sensors, 60 processes, events, services.</summary>
    private SampleFrame Frame(bool watched = true, bool names = false)
    {
        var ts = _time.GetUtcNow();
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        var cpu = new CpuSample(ts, Reading<double>.Of(37.5), Reading<double>.Of(20), Reading<double>.Of(17.5), Reading<double>.Of(4800), Reading<double>.Of(3700),
            Reading<double>.Of(130), Reading<double>.Of(100), Reading<long>.Of(0), Reading<double>.Of(0), Reading<double>.Of(1000), Reading<double>.Of(900),
            Reading<double>.Unavailable(Availability.RequiresElevation), Reading<double>.Unavailable(Availability.RequiresElevation),
            [new CpuCoreSample(0, Reading<double>.Of(10), Reading<double>.Of(5300)), new CpuCoreSample(1, Reading<double>.Pending, none)]);
        var memory = new MemorySample(ts, Reading<long>.Of(16L << 30), Reading<long>.Of(4L << 30), Reading<long>.Of(1L << 30), Reading<long>.Of(2L << 30),
            Reading<long>.Of(20L << 30), Reading<long>.Of(32L << 30), Reading<long>.Of(1L << 29), Reading<long>.Of(1L << 28),
            Reading<long>.Unavailable(Availability.Unsupported), Reading<double>.Of(100), Reading<double>.Of(2), Reading<double>.Of(5), Reading<double>.Of(0.75));
        var gpu = new GpuSample(ts, [], [new GpuProcessUsage(7, "gpu-dgpu", 40, "3D", Reading<long>.Of(21L << 30))]);
        var sensor = new SensorDescriptor("lhm", "Intel Core Ultra 9 285K", SensorKind.Temperature, "CPU Package", "°C", "lhm/intelcpu/0/temperature/0");
        var sensors = new SensorSample(ts, [new SensorReading(sensor, Reading<double>.Of(61.5)), new SensorReading(sensor with { LogicalKey = SensorKeys.CpuPackageTemperature, AliasOf = sensor.LogicalKey }, Reading<double>.Of(61.5))]);
        var all = Enumerable.Range(1, 60).Select(pid => Process(pid, pid)).ToList();
        var identities = names ? all.Select(p => new ProcessIdentityDetails(p.Key, 4, p.Name + ".exe", ts.AddHours(-1), 1, $@"C:\Apps\{p.Name}.exe", null, null)).ToList() : [];
        var processes = new ProcessSample(ts, all, identities, [new ProcessLifecycleEvent(ts, all[0].Key, all[0].Name, ProcessEventKind.Started)]);
        var events = new List<SystemEvent>
        {
            new(ts, "System", 41, "Microsoft-Windows-Kernel-Power", 41, EventLevel.Critical, EventCategory.UnexpectedShutdown, Severity.High, "Kernel-Power 41", "The system has rebooted.",
                RuleId: "shutdown.unexpected", Data: new Dictionary<string, string> { ["bugcheck"] = "0x0" }),
        };
        var services = new ServiceSample(ts, null, [new ServiceChange(ts, "BITS", "Background Intelligent Transfer Service", ServiceEventKind.Stopped, "Running", "Stopped", 1200)]);
        var diagnostics = new TickDiagnostics(TimeSpan.FromMilliseconds(3), 2, 0, Reading<double>.Of(0.4), 50_000_000);
        return new SampleFrame(++_sequence, ts, cpu, memory, gpu, Processes: processes, Sensors: sensors, Diagnostics: diagnostics, Services: services, Events: events,
            Recorded: !watched, Watched: watched);
    }

    private static string Json(SampleFrame frame) => JsonSerializer.Serialize(frame);

    [Fact]
    public async Task A_frame_comes_back_as_it_was_written()
    {
        await using var store = Store();
        var frame = store.Compact(Frame(names: true));

        var back = BlackBoxStore.Unpack(BlackBoxStore.Pack(frame)).ShouldNotBeNull();

        Json(back).ShouldBe(Json(frame));
        BlackBoxStore.Pack(frame).Length.ShouldBeLessThan(Json(frame).Length / 2, "it is compressed");
    }

    [Fact]
    public async Task The_next_run_reads_the_watched_minutes_with_the_busiest_processes_and_their_names()
    {
        var start = _time.GetUtcNow();
        await using (var first = Store())
        {
            // Names come only when a process appears: in a frame the recorder did not watch, before watching began.
            await first.ConsumeAsync(Frame(watched: false, names: true) with { Recorded = false }, CancellationToken.None);
            for (var i = 0; i < 30; i++)
            {
                _time.Advance(TimeSpan.FromSeconds(1));
                await first.ConsumeAsync(Frame(), CancellationToken.None);
            }
        }

        await using var next = Store();
        var frames = await next.ReadPreviousAsync(start, start.AddSeconds(20), CancellationToken.None);

        frames.Count.ShouldBe(20);
        frames.Select(f => f.Timestamp).ShouldBeInOrder();
        var processes = frames[0].Processes.ShouldNotBeNull();
        // The busiest, as a recording stores them (top 5 by CPU and by memory here, and what just started), not all 60.
        processes.Processes.Count.ShouldBeLessThan(20);
        processes.Processes.Select(p => p.Key.Pid).ShouldContain(60);
        processes.Identities.Select(i => i.Name).ShouldContain("p60.exe");
        frames[0].Cpu!.TotalPercent.Value.ShouldBe(37.5);
        frames[0].Events.ShouldNotBeNull().ShouldHaveSingleItem().Data!["bugcheck"].ShouldBe("0x0");
    }

    [Fact]
    public async Task Recorded_frames_and_a_switched_off_black_box_write_nothing()
    {
        var start = _time.GetUtcNow();
        await using (var first = Store())
        {
            await first.ConsumeAsync(Frame(watched: false), CancellationToken.None);
            _recorder.CurrentValue = new RecorderOptions { BlackBox = false };
            await first.ConsumeAsync(Frame(), CancellationToken.None);
        }

        _recorder.CurrentValue = new RecorderOptions();
        await using var next = Store();
        (await next.ReadPreviousAsync(start.AddMinutes(-1), start.AddMinutes(1), CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_the_pre_window_and_a_margin_are_kept()
    {
        var start = _time.GetUtcNow();
        await using (var first = Store())
        {
            // 20 minutes, a frame every 10 s; the default pre-window is 10 minutes, plus 2.
            for (var i = 0; i < 120; i++)
            {
                await first.ConsumeAsync(Frame(), CancellationToken.None);
                _time.Advance(TimeSpan.FromSeconds(10));
            }
        }

        await using var next = Store();
        var frames = await next.ReadPreviousAsync(start, _time.GetUtcNow(), CancellationToken.None);

        frames[0].Timestamp.ShouldBeGreaterThanOrEqualTo(_time.GetUtcNow() - TimeSpan.FromMinutes(13));
        frames[^1].Timestamp.ShouldBe(_time.GetUtcNow() - TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Each_run_keeps_only_the_one_before_it()
    {
        var start = _time.GetUtcNow();
        await using (var first = Store())
        {
            await first.ConsumeAsync(Frame(), CancellationToken.None);
        }

        await using (var second = Store())
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await second.ConsumeAsync(Frame(), CancellationToken.None);
            (await second.ReadPreviousAsync(start, start, CancellationToken.None)).Count.ShouldBe(1, "the first run's frame");
        }

        await using var third = Store();
        (await third.ReadPreviousAsync(start, start.AddMinutes(1), CancellationToken.None)).ShouldHaveSingleItem().Timestamp.ShouldBe(start.AddSeconds(5));
    }
}
