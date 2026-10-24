using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Tests;

public class SampleBusTests
{
    private static SampleFrame Frame(long sequence) => new(sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence));

    [Fact]
    public async Task Every_consumer_receives_every_frame_in_order()
    {
        var a = new RecordingConsumer("a");
        var b = new RecordingConsumer("b");
        var bus = EngineFactory.Bus(a, b);
        await bus.StartAsync(CancellationToken.None);

        for (var i = 1; i <= 5; i++)
        {
            bus.Publish(Frame(i));
        }

        await bus.FlushAsync(CancellationToken.None);

        a.Frames.Select(f => f.Sequence).ShouldBe([1, 2, 3, 4, 5]);
        b.Frames.Select(f => f.Sequence).ShouldBe([1, 2, 3, 4, 5]);
        a.Flushes.ShouldBe(1);
        await bus.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_stalled_consumer_drops_its_oldest_frames_without_blocking_others()
    {
        var stalled = new RecordingConsumer("stalled", capacity: 3) { Gate = new TaskCompletionSource() };
        var healthy = new RecordingConsumer("healthy");
        var bus = EngineFactory.Bus(stalled, healthy);
        await bus.StartAsync(CancellationToken.None);

        for (var i = 1; i <= 10; i++)
        {
            bus.Publish(Frame(i)); // never blocks
        }

        stalled.Gate.SetResult();
        await bus.FlushAsync(CancellationToken.None);

        healthy.Frames.Count.ShouldBe(10);
        stalled.Frames.Count.ShouldBeLessThan(10);
        stalled.Frames.Last().Sequence.ShouldBe(10); // newest data is kept
        bus.DropCounts.Single(d => d.Consumer == "stalled").Dropped.ShouldBeGreaterThan(0);
        await bus.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_throwing_consumer_keeps_receiving_frames()
    {
        var calls = 0;
        var throwing = new DelegateConsumer(_ =>
        {
            calls++;
            throw new InvalidOperationException("disk full");
        });
        var bus = EngineFactory.Bus(throwing);
        await bus.StartAsync(CancellationToken.None);

        bus.Publish(Frame(1));
        bus.Publish(Frame(2));
        await bus.FlushAsync(CancellationToken.None);

        calls.ShouldBe(2);
        await bus.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stop_drains_queued_frames_and_flushes()
    {
        var consumer = new RecordingConsumer();
        var bus = EngineFactory.Bus(consumer);
        await bus.StartAsync(CancellationToken.None);

        bus.Publish(Frame(1));
        bus.Publish(Frame(2));
        await bus.StopAsync(CancellationToken.None);

        consumer.Frames.Count.ShouldBe(2);
        consumer.Flushes.ShouldBe(1);

        // After stopping, a flush request completes immediately instead of hanging.
        await bus.FlushAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class DelegateConsumer(Action<SampleFrame> onFrame) : Core.Abstractions.ISampleConsumer
    {
        public string Name => "delegate";

        public int QueueCapacity => 16;

        public ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
        {
            onFrame(frame);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
