using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Telemetry;

/// <summary>
/// Fans each <see cref="SampleFrame"/> out to every <see cref="ISampleConsumer"/> through its own bounded queue.
/// Publishing never blocks: when a consumer's queue is full, its oldest frame is dropped and counted.
/// </summary>
public sealed partial class SampleBus : IHostedService, IAsyncDisposable
{
    private readonly List<ConsumerQueue> _queues;
    private readonly ILogger<SampleBus> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;

    /// <summary>Creates the bus.</summary>
    public SampleBus(IEnumerable<ISampleConsumer> consumers, ILogger<SampleBus> logger)
    {
        _logger = logger;
        _queues = consumers.Select(c => new ConsumerQueue(c, this)).ToList();
    }

    /// <summary>Consumers and how many frames each has dropped.</summary>
    public IReadOnlyList<(string Consumer, long Dropped)> DropCounts =>
        _queues.Select(q => (q.Consumer.Name, Interlocked.Read(ref q.Dropped))).ToList();

    /// <summary>Queues a frame for every consumer.</summary>
    public void Publish(SampleFrame frame) => Publish(frame, frame);

    /// <summary>
    /// Queues a frame whole for the consumers that see unrecorded data (the live view) and as <paramref name="recorded"/>
    /// (only what a custom recording selected) for the others.
    /// </summary>
    public void Publish(SampleFrame frame, SampleFrame recorded)
    {
        foreach (var queue in _queues)
        {
            queue.Channel.Writer.TryWrite(new BusItem(queue.Consumer.SeesUnrecordedData ? frame : recorded, null));
        }
    }

    /// <summary>
    /// Stores watched minutes kept in memory, as recorded (they became an incident's history), through the consumers that
    /// store them (<see cref="ISampleConsumer.StoresBackfill"/>). Sent in parts, each waited for, so a long history never
    /// overflows a queue.
    /// </summary>
    public async Task PublishBackfillAsync(IReadOnlyList<SampleFrame> frames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var targets = _queues.Where(q => q.Consumer.StoresBackfill).ToList();
        foreach (var part in frames.Chunk(200))
        {
            var pending = new List<Task>();
            foreach (var queue in targets)
            {
                foreach (var frame in part)
                {
                    queue.Channel.Writer.TryWrite(new BusItem(frame with { Recorded = true, Watched = false }, null));
                }

                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!queue.Channel.Writer.TryWrite(new BusItem(null, done)))
                {
                    done.TrySetResult();
                }

                pending.Add(done.Task);
            }

            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until every consumer has processed everything queued so far, then flushes each consumer.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var pending = new List<Task>();
        foreach (var queue in _queues)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // With DropOldest, TryWrite only fails once the bus has stopped; StopAsync already flushed then.
            if (!queue.Channel.Writer.TryWrite(new BusItem(null, done)))
            {
                done.TrySetResult();
            }

            pending.Add(done.Task);
        }

        await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues)
        {
            queue.Worker = Task.Run(() => queue.RunAsync(_stopping.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues)
        {
            queue.Channel.Writer.TryComplete();
        }

        // Let workers drain what is queued; then give each consumer a final flush.
        await Task.WhenAll(_queues.Select(q => q.Worker)).WaitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var queue in _queues)
        {
            await queue.SafeFlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent: the bus is registered both as itself and as a hosted service, so the container disposes it twice.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} is falling behind; {Dropped} frames dropped so far")]
    private static partial void LogDropped(ILogger logger, string consumer, long dropped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer {Consumer} failed to process a frame")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception, string consumer);

    private sealed record BusItem(SampleFrame? Frame, TaskCompletionSource? FlushDone);

    private sealed class ConsumerQueue
    {
        private readonly SampleBus _bus;
        public long Dropped;

        public ConsumerQueue(ISampleConsumer consumer, SampleBus bus)
        {
            Consumer = consumer;
            _bus = bus;
            Channel = System.Threading.Channels.Channel.CreateBounded<BusItem>(
                new BoundedChannelOptions(Math.Max(1, consumer.QueueCapacity))
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                OnDropped);
        }

        public ISampleConsumer Consumer { get; }

        public Channel<BusItem> Channel { get; }

        public Task Worker { get; set; } = Task.CompletedTask;

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await foreach (var item in Channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Frame is not null)
                {
                    try
                    {
                        await Consumer.ConsumeAsync(item.Frame, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        LogConsumerFailed(_bus._logger, ex, Consumer.Name);
                    }
                }

                if (item.FlushDone is not null)
                {
                    await SafeFlushAsync(cancellationToken).ConfigureAwait(false);
                    item.FlushDone.TrySetResult();
                }
            }
        }

        public async Task SafeFlushAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Consumer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogConsumerFailed(_bus._logger, ex, Consumer.Name);
            }
        }

        private void OnDropped(BusItem item)
        {
            // A dropped flush request must still complete, or FlushAsync would wait forever.
            item.FlushDone?.TrySetResult();
            if (item.Frame is null)
            {
                return;
            }

            var dropped = Interlocked.Increment(ref Dropped);
            if (dropped == 1 || dropped % 60 == 0)
            {
                LogDropped(_bus._logger, Consumer.Name, dropped);
            }
        }
    }
}
