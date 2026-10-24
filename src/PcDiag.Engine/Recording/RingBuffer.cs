using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Recording;

/// <summary>What the ring buffer currently holds.</summary>
/// <param name="Frames">Frames held.</param>
/// <param name="EstimatedBytes">Estimated memory held by those frames.</param>
/// <param name="Oldest">Oldest frame time.</param>
/// <param name="Newest">Newest frame time.</param>
/// <param name="Evicted">Frames evicted so far (by time, size or slot count).</param>
public sealed record RingBufferStats(int Frames, long EstimatedBytes, DateTimeOffset? Oldest, DateTimeOffset? Newest, long Evicted);

/// <summary>
/// T0: the last minutes of complete frames in memory, including every process (telemetry.db keeps only the
/// top processes). It holds the configured pre-incident window plus one minute, capped by
/// <see cref="RecorderOptions.RingBufferMaxMegabytes"/> (an estimate; see <see cref="SampleFrameSize"/>). The slot
/// array is allocated once for the longest possible window; there is one writer (its bus queue) and readers take
/// a copy of the frame references under a short lock.
/// </summary>
public sealed partial class RingBuffer : ISampleConsumer
{
    /// <summary>Longest window the slots are sized for (30-minute pre-window plus one minute).</summary>
    public const int MaxWindowMinutes = 31;

    private readonly IOptionsMonitor<RecorderOptions> _options;
    private readonly ILogger<RingBuffer> _logger;
    private readonly Slot[] _slots;
    private readonly Lock _gate = new();
    private readonly FrameSharing _sharing = new();
    private int _head;
    private int _count;
    private long _bytes;
    private long _evicted;
    private bool _windowFullLogged;
    private bool _sizeCapLogged;

    /// <summary>Creates the buffer, sized for the fastest configured tick.</summary>
    public RingBuffer(IOptionsMonitor<RecorderOptions> options, IOptionsMonitor<SamplingOptions> sampling, ILogger<RingBuffer> logger)
    {
        _options = options;
        _logger = logger;
        var framesPerSecond = Math.Ceiling(1000.0 / Math.Max(250, sampling.CurrentValue.SystemIntervalMs));
        _slots = new Slot[(int)(MaxWindowMinutes * 60 * framesPerSecond) + 16];
    }

    /// <inheritdoc />
    public string Name => "ring";

    /// <inheritdoc />
    public int QueueCapacity => 64;

    /// <summary>Slot count.</summary>
    public int Capacity => _slots.Length;

    /// <summary>Current contents.</summary>
    public RingBufferStats Stats
    {
        get
        {
            lock (_gate)
            {
                return new RingBufferStats(
                    _count,
                    _bytes,
                    _count == 0 ? null : _slots[_head].Frame.Timestamp,
                    _count == 0 ? null : _slots[(_head + _count - 1) % _slots.Length].Frame.Timestamp,
                    _evicted);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        // Live-view frames are for the dashboard only; watched ones (recorder not recording) are kept so a problem found
        // then has the minutes before it.
        if (frame.Recorded || frame.Watched)
        {
            Append(frame);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Frames with <paramref name="from"/> ≤ timestamp ≤ <paramref name="to"/>, oldest first.</summary>
    public IReadOnlyList<SampleFrame> Snapshot(DateTimeOffset from, DateTimeOffset to)
    {
        var frames = new List<SampleFrame>();
        lock (_gate)
        {
            for (var i = 0; i < _count; i++)
            {
                var frame = _slots[(_head + i) % _slots.Length].Frame;
                if (frame.Timestamp >= from && frame.Timestamp <= to)
                {
                    frames.Add(frame);
                }
            }
        }

        return frames;
    }

    /// <summary>Adds a frame and evicts what falls outside the window or the size cap.</summary>
    internal void Append(SampleFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var options = _options.CurrentValue;
        var window = TimeSpan.FromMinutes(Math.Clamp(options.PreIncidentMinutes, 1, MaxWindowMinutes - 1) + 1);
        var maxBytes = (long)Math.Max(1, options.RingBufferMaxMegabytes) * 1024 * 1024;
        var size = SampleFrameSize.Estimate(frame, _sharing);

        lock (_gate)
        {
            if (_count == _slots.Length)
            {
                EvictOldest();
            }

            _slots[(_head + _count) % _slots.Length] = new Slot(frame, size);
            _count++;
            _bytes += size;

            // Keep at least the newest frame, even if it alone exceeds the cap.
            var bySize = false;
            var byTime = false;
            while (_count > 1 && (_bytes > maxBytes || _slots[_head].Frame.Timestamp < frame.Timestamp - window))
            {
                bySize |= _bytes > maxBytes;
                byTime |= _slots[_head].Frame.Timestamp < frame.Timestamp - window;
                EvictOldest();
            }

            if (byTime && !_windowFullLogged)
            {
                _windowFullLogged = true;
                LogWindowFull(_logger, _count, _bytes / (1024.0 * 1024.0), window.TotalMinutes);
            }

            if (bySize && !_sizeCapLogged)
            {
                _sizeCapLogged = true;
                LogSizeCap(_logger, options.RingBufferMaxMegabytes, (frame.Timestamp - _slots[_head].Frame.Timestamp).TotalMinutes);
            }
        }
    }

    private void EvictOldest()
    {
        _bytes -= _slots[_head].Bytes;
        _slots[_head] = default;
        _head = (_head + 1) % _slots.Length;
        _count--;
        _evicted++;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ring buffer window is full: {Frames} frames, about {Megabytes:F1} MB, covering {Minutes} minutes")]
    private static partial void LogWindowFull(ILogger logger, int frames, double megabytes, double minutes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ring buffer reached its {Megabytes} MB cap; it now covers {Minutes:F1} minutes, less than the pre-incident window (raise Recorder:RingBufferMaxMegabytes to keep the full window)")]
    private static partial void LogSizeCap(ILogger logger, int megabytes, double minutes);

    private readonly record struct Slot(SampleFrame Frame, long Bytes);
}

/// <summary>
/// The process rows and sensor readings of the latest frame that had them. Collectors hand back the same object
/// for a process or sensor that reads exactly as before, so consecutive frames share most of them.
/// </summary>
public sealed class FrameSharing
{
    internal HashSet<object> Processes { get; set; } = new(ReferenceEqualityComparer.Instance);

    internal HashSet<object> Sensors { get; set; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>
/// Estimates how much memory a frame keeps alive, from element counts and string lengths (object headers,
/// fields, readings). Used only to cap the ring buffer; it is deliberately simple and errs on the high side.
/// </summary>
public static class SampleFrameSize
{
    private const long Reference = 8;

    /// <summary>The estimate in bytes, as if the frame shared nothing with other frames.</summary>
    public static long Estimate(SampleFrame frame) => Estimate(frame, null);

    /// <summary>
    /// The estimate in bytes, charging process rows and sensor readings already held by the previous frame that had
    /// them (see <paramref name="sharing"/>, which this updates) only for the reference. A shared object is then
    /// counted once, in the oldest frame holding it; once that frame is evicted the estimate is low by at most one
    /// frame's rows and readings.
    /// </summary>
    public static long Estimate(SampleFrame frame, FrameSharing? sharing)
    {
        ArgumentNullException.ThrowIfNull(frame);
        long bytes = 512;
        if (frame.Cpu is { } cpu)
        {
            bytes += 320 + (cpu.Cores.Count * 48L);
        }

        if (frame.Memory is not null)
        {
            bytes += 320;
        }

        if (frame.Gpu is { } gpu)
        {
            bytes += (gpu.Adapters.Count * 480L) + (gpu.Processes.Count * 96L);
        }

        if (frame.Disk is { } disk)
        {
            bytes += (disk.Disks.Count * 360L) + (disk.Volumes.Count * 200L);
        }

        if (frame.Network is { } network)
        {
            bytes += network.Interfaces.Count * 360L;
        }

        if (frame.Processes is { } processes)
        {
            bytes += Charge(processes.Processes, 240, sharing?.Processes, out var rows) + (processes.Events.Count * 160L);
            if (sharing is not null)
            {
                sharing.Processes = rows;
            }

            foreach (var identity in processes.Identities)
            {
                bytes += 240 + (2L * (identity.Name.Length + (identity.Path?.Length ?? 0) + (identity.CommandLine?.Length ?? 0) + (identity.User?.Length ?? 0)));
            }
        }

        if (frame.Sensors is { } sensors)
        {
            bytes += Charge(sensors.Readings, 160, sharing?.Sensors, out var readings);
            if (sharing is not null)
            {
                sharing.Sensors = readings;
            }
        }

        if (frame.Services is { } services)
        {
            bytes += ((services.Snapshot?.Count ?? 0) * 200L) + (services.Changes.Count * 200L);
        }

        if (frame.Inventory is { } inventory)
        {
            bytes += (2L * inventory.HardwareJson.Length) + (inventory.Drivers.Count * 400L) + (inventory.System.Count * 160L);
        }

        foreach (var e in frame.Events ?? [])
        {
            bytes += 600 + (2L * (e.Title.Length + e.Message.Length));
        }

        if (frame.Display is { } display)
        {
            bytes += 400 + ((display.Snapshot?.Displays.Count ?? 0) * 600L) + (display.Changes.Count * 240L);
        }

        return bytes;
    }

    private static long Charge<T>(IReadOnlyList<T> items, long each, HashSet<object>? held, out HashSet<object> current)
        where T : class
    {
        current = new HashSet<object>(items.Count, ReferenceEqualityComparer.Instance);
        long bytes = 0;
        foreach (var item in items)
        {
            bytes += held is not null && held.Contains(item) ? Reference : each;
            current.Add(item);
        }

        return bytes;
    }
}
