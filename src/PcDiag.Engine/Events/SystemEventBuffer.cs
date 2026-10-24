using System.Threading.Channels;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;

namespace PcDiag.Engine.Events;

/// <summary>
/// Holds classified events until the next scheduler tick puts them into the frame. Publishing never blocks; if
/// ticks stop being drained (the recorder is idle) the oldest events are dropped and counted.
/// </summary>
public sealed class SystemEventBuffer : ISystemEventSink
{
    /// <summary>Queue capacity.</summary>
    public const int Capacity = 10_000;

    /// <summary>Events moved into one frame at most (the rest follow in the next ticks).</summary>
    public const int MaxPerFrame = 2_000;

    private readonly Channel<SystemEvent> _channel;
    private long _dropped;

    /// <summary>Creates the queue.</summary>
    public SystemEventBuffer()
    {
        _channel = Channel.CreateBounded<SystemEvent>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
            _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Events dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public void Publish(SystemEvent systemEvent)
    {
        ArgumentNullException.ThrowIfNull(systemEvent);
        _channel.Writer.TryWrite(systemEvent);
    }

    /// <summary>Takes the queued events (up to <see cref="MaxPerFrame"/>), oldest first; null when there are none.</summary>
    public IReadOnlyList<SystemEvent>? Drain()
    {
        List<SystemEvent>? events = null;
        while ((events?.Count ?? 0) < MaxPerFrame && _channel.Reader.TryRead(out var systemEvent))
        {
            (events ??= []).Add(systemEvent);
        }

        return events;
    }
}
