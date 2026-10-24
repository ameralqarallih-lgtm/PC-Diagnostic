using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Tests;

public class TelemetrySchedulerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
    private readonly TierPlan _plan = new(new SamplingOptions());

    [Fact]
    public async Task Frames_carry_sequence_timestamp_and_samples()
    {
        var bus = EngineFactory.Bus();
        var scheduler = EngineFactory.Scheduler(_time, bus, new FakeCollector("cpu", SamplingTier.System));
        await scheduler.InitializeAsync(CancellationToken.None);
        var lastTick = new Dictionary<SamplingTier, long>();

        var first = await scheduler.RunTickAsync(_plan, lastTick, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(1));
        var second = await scheduler.RunTickAsync(_plan, lastTick, CancellationToken.None);

        first.Sequence.ShouldBe(1);
        second.Sequence.ShouldBe(2);
        second.Timestamp.ShouldBe(first.Timestamp.AddSeconds(1));
        second.Cpu!.TotalPercent.ShouldBe(Reading<double>.Of(42));
        second.Diagnostics!.CollectorsRun.ShouldBe(1);
        second.Diagnostics.CollectorsMissed.ShouldBe(0);
    }

    [Fact]
    public async Task Slower_tiers_run_only_on_their_ticks()
    {
        var system = new FakeCollector("system", SamplingTier.System);
        var process = new FakeCollector("process", SamplingTier.Process);
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), system, process);
        await scheduler.InitializeAsync(CancellationToken.None);
        var lastTick = new Dictionary<SamplingTier, long>();

        for (var i = 0; i < 6; i++)
        {
            await scheduler.RunTickAsync(_plan, lastTick, CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        system.Calls.ShouldBe(6);
        process.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task Collectors_receive_the_elapsed_time_of_their_tier()
    {
        var elapsed = new List<TimeSpan>();
        var process = new FakeCollector("process", SamplingTier.Process, (ctx, _) =>
        {
            elapsed.Add(ctx.Elapsed);
            return Task.FromResult(FakeCollector.Sample(ctx.Timestamp, 1));
        });
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), process);
        await scheduler.InitializeAsync(CancellationToken.None);
        var lastTick = new Dictionary<SamplingTier, long>();

        for (var i = 0; i < 3; i++)
        {
            await scheduler.RunTickAsync(_plan, lastTick, CancellationToken.None);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        elapsed.ShouldBe([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)]);
    }

    [Fact]
    public async Task A_failing_collector_is_counted_and_does_not_affect_others()
    {
        var broken = new FakeCollector("broken", SamplingTier.System, (_, _) => throw new InvalidOperationException("boom"));
        var healthy = new FakeCollector("healthy", SamplingTier.System);
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), broken, healthy);
        await scheduler.InitializeAsync(CancellationToken.None);

        var frame = await scheduler.RunTickAsync(_plan, [], CancellationToken.None);

        frame.Cpu.ShouldNotBeNull();
        frame.Diagnostics!.CollectorsMissed.ShouldBe(1);
        frame.Diagnostics.CollectorsRun.ShouldBe(2);
    }

    [Fact]
    public async Task A_hung_collector_times_out_and_is_skipped_while_still_busy()
    {
        var release = new TaskCompletionSource();
        var hung = new FakeCollector("hung", SamplingTier.System, async (ctx, _) =>
        {
            await release.Task; // ignores cancellation on purpose
            return FakeCollector.Sample(ctx.Timestamp, 1);
        });
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), hung);
        await scheduler.InitializeAsync(CancellationToken.None);
        var lastTick = new Dictionary<SamplingTier, long>();

        var tick = scheduler.RunTickAsync(_plan, lastTick, CancellationToken.None);
        await WaitUntilAsync(() => hung.Calls == 1);
        _time.Advance(_plan.CollectorTimeout);
        var first = await tick;

        first.Cpu.ShouldBeNull();
        first.Diagnostics!.CollectorsMissed.ShouldBe(1);

        var second = await scheduler.RunTickAsync(_plan, lastTick, CancellationToken.None);
        second.Diagnostics!.CollectorsMissed.ShouldBe(1);
        second.Diagnostics.CollectorsRun.ShouldBe(0);
        hung.Calls.ShouldBe(1); // not started again while the earlier call is still running

        release.SetResult();
    }

    [Fact]
    public async Task A_collector_that_fails_to_initialize_is_disabled()
    {
        var bad = new FakeCollector("bad", SamplingTier.System)
        {
            Initialize = _ => throw new InvalidOperationException("no driver"),
        };
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), bad);
        await scheduler.InitializeAsync(CancellationToken.None);

        var frame = await scheduler.RunTickAsync(_plan, [], CancellationToken.None);

        bad.Calls.ShouldBe(0);
        frame.Diagnostics!.CollectorsRun.ShouldBe(0);
    }

    [Fact]
    public async Task Frames_are_published_to_the_bus()
    {
        var consumer = new RecordingConsumer();
        var bus = EngineFactory.Bus(consumer);
        await bus.StartAsync(CancellationToken.None);
        var scheduler = EngineFactory.Scheduler(_time, bus, new FakeCollector("cpu", SamplingTier.System));
        await scheduler.InitializeAsync(CancellationToken.None);

        await scheduler.RunTickAsync(_plan, [], CancellationToken.None);
        await bus.FlushAsync(CancellationToken.None);

        consumer.Frames.Count.ShouldBe(1);
        await bus.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Run_loop_ticks_on_the_timer_and_stops_on_cancellation()
    {
        var collector = new FakeCollector("cpu", SamplingTier.System);
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), collector);
        using var cts = new CancellationTokenSource();

        var run = scheduler.RunAsync(cts.Token);
        await WaitUntilAsync(() => collector.Calls == 1); // first tick is immediate
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await WaitUntilAsync(() => collector.Calls == i + 2);
        }

        await cts.CancelAsync();
        await run; // completes without throwing
        collector.Calls.ShouldBe(4);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not reached.");
            }

            await Task.Delay(5);
        }
    }
}
