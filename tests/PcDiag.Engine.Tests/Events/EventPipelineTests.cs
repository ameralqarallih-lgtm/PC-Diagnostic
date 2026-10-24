using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Events;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Tests.Events;

public class EventStormGuardTests
{
    private static SystemEvent At(DateTimeOffset ts) =>
        new(ts, "System", null, "disk", 153, EventLevel.Warning, EventCategory.Disk, Severity.Medium, "A disk I/O operation was retried (disk 153)", "m", RuleId: "disk.retry");

    [Fact]
    public void Events_over_the_per_minute_limit_are_counted_and_summarised_once()
    {
        var guard = new EventStormGuard(maxPerMinute: 20);
        var minute = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        var admitted = Enumerable.Range(0, 25).Count(i => guard.Admit("disk.retry", At(minute.AddSeconds(i))));
        guard.Flush(minute.AddSeconds(30)).ShouldBeEmpty(); // the minute is still open

        var summary = guard.Flush(minute.AddMinutes(2)).ShouldHaveSingleItem();
        admitted.ShouldBe(20);
        summary.Provider.ShouldBe(EventStormGuard.SummaryProvider);
        summary.RecordId.ShouldBeNull();
        summary.Data!["suppressed"].ShouldBe("5");
        summary.Title.ShouldBe("5 more \"A disk I/O operation was retried (disk 153)\" events in this minute were not recorded (event storm)");
        summary.Timestamp.ShouldBe(minute.AddSeconds(24));
        guard.Flush(minute.AddMinutes(3)).ShouldBeEmpty();
    }

    [Fact]
    public void Limits_are_per_rule_and_per_minute()
    {
        var guard = new EventStormGuard(maxPerMinute: 2);
        var t = new DateTimeOffset(2026, 10, 2, 12, 0, 50, TimeSpan.Zero);

        guard.Admit("a", At(t)).ShouldBeTrue();
        guard.Admit("a", At(t)).ShouldBeTrue();
        guard.Admit("a", At(t)).ShouldBeFalse();
        guard.Admit("b", At(t)).ShouldBeTrue();
        guard.Admit("a", At(t.AddSeconds(15))).ShouldBeTrue(); // next minute
        guard.Flush(t, all: true).Count.ShouldBe(1);
    }
}

public class EventPumpTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));

    private static SystemEvent Event(string title) =>
        new(DateTimeOffset.UnixEpoch, "System", 1, "p", 1, EventLevel.Error, EventCategory.Other, Severity.Low, title, string.Empty);

    [Fact]
    public async Task Each_frame_carries_the_events_that_arrived_since_the_previous_tick()
    {
        var pump = EngineFactory.Pump(_time);
        var scheduler = EngineFactory.Scheduler(_time, EngineFactory.Bus(), pump);
        await scheduler.InitializeAsync(CancellationToken.None);
        var plan = new TierPlan(new Core.Configuration.SamplingOptions());
        var lastTick = new Dictionary<SamplingTier, long>();

        pump.Buffer.Publish(Event("a"));
        pump.Buffer.Publish(Event("b"));
        var first = await scheduler.RunTickAsync(plan, lastTick, CancellationToken.None);
        var second = await scheduler.RunTickAsync(plan, lastTick, CancellationToken.None);

        first.Events!.Select(e => e.Title).ShouldBe(["a", "b"]);
        second.Events.ShouldBeNull();
    }

    [Fact]
    public async Task Collector_events_reach_the_buffer_and_a_failed_collector_is_restarted()
    {
        var collector = new ScriptedCollector();
        var pump = EngineFactory.Pump(_time, collector);
        await pump.InitializeAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();

        var run = pump.RunAsync(cts.Token);
        await collector.FirstRunFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => _time.Advance(EventPump.RestartDelay), () => collector.Runs >= 2);
        await collector.SecondRunStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        pump.Buffer.Drain()!.Select(e => e.Title).ShouldBe(["before failure", "after restart"]);
        collector.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_collector_that_fails_to_initialize_is_not_run()
    {
        var collector = new ScriptedCollector { FailInitialize = true };
        var pump = EngineFactory.Pump(_time, collector);
        await pump.InitializeAsync(CancellationToken.None);

        await pump.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        collector.Runs.ShouldBe(0);
    }

    [Fact]
    public void The_buffer_drops_oldest_events_when_ticks_stop_draining()
    {
        var buffer = new SystemEventBuffer();
        for (var i = 0; i < SystemEventBuffer.Capacity + 5; i++)
        {
            buffer.Publish(Event(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        buffer.Dropped.ShouldBe(5);
        buffer.Drain()!.Count.ShouldBe(SystemEventBuffer.MaxPerFrame);
        buffer.Drain()![0].Title.ShouldBe((5 + SystemEventBuffer.MaxPerFrame).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static async Task WaitUntilAsync(Action step, Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            step();
            await Task.Delay(10);
        }
    }

    private sealed class ScriptedCollector : IEventCollector
    {
        private int _runs;

        public bool FailInitialize { get; init; }

        public TaskCompletionSource FirstRunFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondRunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Runs => Volatile.Read(ref _runs);

        public string Id => "scripted";

        public CollectorInfo Describe() => new(Id, "Scripted", RequiredPrivilege.StandardUser, CostClass.Cheap);

        public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken) =>
            FailInitialize ? throw new InvalidOperationException("no access") : ValueTask.FromResult(new CollectorStatus(Availability.Available));

        public async IAsyncEnumerable<SystemEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var run = Interlocked.Increment(ref _runs);
            if (run == 1)
            {
                yield return Event("before failure");
                FirstRunFailed.TrySetResult();
                throw new InvalidOperationException("event log service restarted");
            }

            yield return Event("after restart");
            SecondRunStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
