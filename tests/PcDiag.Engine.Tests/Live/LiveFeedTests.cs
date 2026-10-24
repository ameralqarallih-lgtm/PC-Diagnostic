using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Ipc;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Live;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Tests.Live;

public class LiveFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly Reading<double> NoDouble = Reading<double>.Unavailable(Availability.Unsupported);
    private static readonly Reading<long> NoLong = Reading<long>.Unavailable(Availability.Unsupported);
    private static readonly Reading<int> NoInt = Reading<int>.Unavailable(Availability.Unsupported);

    private readonly LiveFeed _feed;

    public LiveFeedTests()
    {
        var ring = new RingBuffer(
            new StaticOptionsMonitor<RecorderOptions>(new RecorderOptions()),
            new StaticOptionsMonitor<SamplingOptions>(new SamplingOptions()),
            NullLogger<RingBuffer>.Instance);
        _feed = new LiveFeed(ring);
    }

    [Fact]
    public void Before_any_sample_everything_is_empty_and_the_agent_stats_are_pending()
    {
        var live = _feed.Snapshot(recording: false);

        live.Timestamp.ShouldBeNull();
        live.Cpu.ShouldBeNull();
        live.Gpus.ShouldBeEmpty();
        live.Agent.CpuPercent.Unavailable.ShouldBe("Pending");
    }

    [Fact]
    public async Task Each_collector_keeps_its_newest_sample_across_frames()
    {
        await _feed.ConsumeAsync(new SampleFrame(1, T0, Cpu: Cpu(T0, 40), Memory: Memory(T0)), CancellationToken.None);
        await _feed.ConsumeAsync(new SampleFrame(2, T0.AddSeconds(1), Cpu: Cpu(T0.AddSeconds(1), 55)), CancellationToken.None);

        var live = _feed.Snapshot(recording: false);

        live.Timestamp.ShouldBe(T0.AddSeconds(1));
        live.Cpu!.TotalPercent.Value.ShouldBe(55);
        live.Cpu.LogicalProcessors.ShouldBe(2);
        live.Memory.ShouldNotBeNull(); // from the first frame: memory is sampled less often
        live.Memory.UsedBytes.Value.ShouldBe(12L * 1024 * 1024 * 1024);
        live.Memory.UsedPercent.Value.ShouldBe(75);
        live.Memory.CommitPercent.Value.ShouldBe(50);
    }

    [Fact]
    public async Task Missing_values_carry_the_reason_instead_of_a_number()
    {
        await _feed.ConsumeAsync(new SampleFrame(1, T0, Cpu: Cpu(T0, 10)), CancellationToken.None);

        var cpu = _feed.Snapshot(recording: false).Cpu!;

        cpu.PackageTemperatureC.Value.ShouldBeNull();
        cpu.PackageTemperatureC.Unavailable.ShouldBe(nameof(Availability.Unsupported));
    }

    [Fact]
    public async Task Sensor_readings_fill_in_what_the_collector_could_not_measure()
    {
        var sensors = new SensorSample(T0, [
            new SensorReading(new SensorDescriptor("lhm", "CPU", SensorKind.Temperature, "Package", "°C", SensorKeys.CpuPackageTemperature), Reading<double>.Of(71.25)),
        ]);

        await _feed.ConsumeAsync(new SampleFrame(1, T0, Cpu: Cpu(T0, 10), Sensors: sensors), CancellationToken.None);

        _feed.Snapshot(recording: false).Cpu!.PackageTemperatureC.Value.ShouldBe(71.25);
    }

    [Fact]
    public async Task Top_process_lists_are_sorted_bounded_and_join_gpu_usage()
    {
        var processes = Enumerable.Range(0, 20).Select(i => Process(i, $"p{i}.exe", cpu: i, privateBytes: (20 - i) * 1_000_000L)).ToList();
        var gpu = new GpuSample(T0, [], [new GpuProcessUsage(5, "gpu0", 30, "3D", NoLong), new GpuProcessUsage(5, "gpu0", 45, "Compute", NoLong)]);

        await _feed.ConsumeAsync(new SampleFrame(1, T0, Processes: new ProcessSample(T0, processes, [], []), Gpu: gpu), CancellationToken.None);

        var top = _feed.Snapshot(recording: false).Processes!;
        top.Count.ShouldBe(19); // the Idle pseudo-process (PID 0) is left out
        top.ByCpu.Count.ShouldBe(LiveFeed.TopProcesses);
        top.ByCpu[0].Name.ShouldBe("p19.exe");
        top.ByMemory[0].Name.ShouldBe("p1.exe");
        top.ByGpu.ShouldHaveSingleItem().GpuPercent.ShouldBe(45); // busiest engine wins
        top.ByIo.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recent_events_are_newest_first_and_bounded()
    {
        for (var i = 0; i < LiveFeed.MaxEvents + 5; i++)
        {
            var at = T0.AddSeconds(i);
            await _feed.ConsumeAsync(new SampleFrame(i, at, Events: [Event(at, $"event {i}")]), CancellationToken.None);
        }

        var events = _feed.Snapshot(recording: false).RecentEvents;

        events.Count.ShouldBe(LiveFeed.MaxEvents);
        events[0].Title.ShouldBe($"event {LiveFeed.MaxEvents + 4}");
        events[0].Source.ShouldBe("windows");
    }

    [Fact]
    public async Task Process_crashes_and_service_failures_become_events()
    {
        var processes = new ProcessSample(T0, [], [], [new ProcessLifecycleEvent(T0, new ProcessKey(42, 1), "game.exe", ProcessEventKind.Crashed)]);
        var services = new ServiceSample(T0, null, [new ServiceChange(T0, "Spooler", "Print Spooler", ServiceEventKind.Failed, null, null)]);

        await _feed.ConsumeAsync(new SampleFrame(1, T0, Processes: processes, Services: services), CancellationToken.None);

        var events = _feed.Snapshot(recording: false).RecentEvents;
        events.ShouldContain(e => e.Title == "game.exe crashed" && e.Severity == Severity.High);
        events.ShouldContain(e => e.Title == "Print Spooler: failed" && e.Source == "service");
    }

    [Fact]
    public async Task The_trend_has_one_point_per_fast_sample_and_carries_slower_collectors()
    {
        await _feed.ConsumeAsync(new SampleFrame(1, T0, Cpu: Cpu(T0, 10), Memory: Memory(T0)), CancellationToken.None);
        await _feed.ConsumeAsync(new SampleFrame(2, T0.AddSeconds(1), Cpu: Cpu(T0.AddSeconds(1), 20)), CancellationToken.None);
        await _feed.ConsumeAsync(new SampleFrame(3, T0.AddSeconds(2), Processes: new ProcessSample(T0, [], [], [])), CancellationToken.None); // no CPU: no point

        var trend = _feed.Trend(TimeSpan.FromMinutes(LiveFeed.TrendMinutes));

        trend.Times.ShouldBe([T0.ToUnixTimeMilliseconds(), T0.AddSeconds(1).ToUnixTimeMilliseconds()]);
        trend.Cpu.ShouldBe([10, 20]);
        trend.Memory.ShouldBe([75, 75]); // carried forward
        trend.Gpu.ShouldBe([null, null]);
        trend.CpuTemperature.ShouldBe([null, null]);
    }

    [Fact]
    public async Task The_trend_keeps_ten_minutes_and_returns_the_requested_span()
    {
        for (var s = 0; s < LiveFeed.TrendCapacity + 100; s++)
        {
            await _feed.ConsumeAsync(new SampleFrame(s, T0.AddSeconds(s), Cpu: Cpu(T0.AddSeconds(s), s % 100)), CancellationToken.None);
        }

        _feed.Trend(TimeSpan.FromMinutes(LiveFeed.TrendMinutes)).Times.Count.ShouldBe(LiveFeed.TrendCapacity);
        var minute = _feed.Trend(TimeSpan.FromMinutes(1));
        minute.Times.Count.ShouldBe(61);
        minute.Times[^1].ShouldBe(T0.AddSeconds(LiveFeed.TrendCapacity + 99).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_ten_minute_trend_fits_in_a_response()
    {
        for (var s = 0; s < LiveFeed.TrendCapacity; s++)
        {
            await _feed.ConsumeAsync(new SampleFrame(s, T0.AddSeconds(s), Cpu: Cpu(T0.AddSeconds(s), 12.3456), Memory: Memory(T0)), CancellationToken.None);
        }

        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new AgentResponse("1", true, Trend: _feed.Trend(TimeSpan.FromMinutes(10))), AgentIpcJsonContext.Default.AgentResponse);

        bytes.Length.ShouldBeLessThan(AgentIpc.MaxResponseBytes / 8);
    }

    [Fact]
    public async Task The_snapshot_fits_in_one_pipe_message_even_when_busy()
    {
        var processes = Enumerable.Range(1, 2000).Select(i => Process(i, new string('x', 60) + ".exe", cpu: i % 100, privateBytes: i, io: i)).ToList();
        var gpu = new GpuSample(T0, [], processes.Select(p => new GpuProcessUsage(p.Key.Pid, "gpu0", 1, "3D", NoLong)).ToList());
        for (var i = 0; i < LiveFeed.MaxEvents; i++)
        {
            await _feed.ConsumeAsync(new SampleFrame(i, T0, Events: [Event(T0, new string('e', 200))]), CancellationToken.None);
        }

        await _feed.ConsumeAsync(new SampleFrame(99, T0, Cpu: Cpu(T0, 1), Memory: Memory(T0), Gpu: gpu, Processes: new ProcessSample(T0, processes, [], [])), CancellationToken.None);
        var response = new AgentResponse("1", true, Live: _feed.Snapshot(recording: true));
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(response, AgentIpcJsonContext.Default.AgentResponse);

        bytes.Length.ShouldBeLessThan(AgentIpc.MaxMessageBytes / 2);
        var back = System.Text.Json.JsonSerializer.Deserialize(bytes, AgentIpcJsonContext.Default.AgentResponse)!;
        back.Live!.Recording.ShouldBeTrue();
        back.Live.Processes!.ByIo.Count.ShouldBe(LiveFeed.TopProcesses);
    }

    private static CpuSample Cpu(DateTimeOffset at, double total) => new(
        at, Reading<double>.Of(total), NoDouble, NoDouble, Reading<double>.Of(3600), NoDouble, NoDouble, NoDouble, NoLong, NoDouble, NoDouble, NoDouble, NoDouble, NoDouble,
        [new CpuCoreSample(0, NoDouble, NoDouble), new CpuCoreSample(1, NoDouble, NoDouble)]);

    private static MemorySample Memory(DateTimeOffset at) => new(
        at, Reading<long>.Of(16L * 1024 * 1024 * 1024), Reading<long>.Of(4L * 1024 * 1024 * 1024), NoLong, NoLong, Reading<long>.Of(10), Reading<long>.Of(20),
        NoLong, NoLong, NoLong, NoDouble, Reading<double>.Of(3), NoDouble, NoDouble);

    private static ProcessMetrics Process(int pid, string name, double cpu, long privateBytes, double? io = null) => new(
        new ProcessKey(pid, pid), name, Reading<double>.Of(cpu), NoLong, Reading<long>.Of(privateBytes / 2), Reading<long>.Of(privateBytes), NoLong,
        io is { } r ? Reading<double>.Of(r) : NoDouble, NoDouble, NoInt, NoInt, NoInt, ProcessState.Running);

    private static SystemEvent Event(DateTimeOffset at, string title) =>
        new(at, "System", 1, "Test", 1, EventLevel.Warning, EventCategory.Other, Severity.Low, title, title);
}
