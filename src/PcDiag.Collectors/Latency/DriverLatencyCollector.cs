using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;

namespace PcDiag.Collectors.Latency;

/// <summary>
/// Driver latency measurements: on request, and by themselves (when <see cref="RecorderOptions.DriverLatency"/> is on)
/// when a processor spends <see cref="AutoPercent"/> % or more of its time in DPCs or interrupts for
/// <see cref="AutoSeconds"/> s in a row, or a game stutters <see cref="AutoStutters"/> times within 10 s; at most once
/// every <see cref="AutoCooldown"/>. Watches the frames as a sample consumer; each finished measurement goes into the
/// next frame through the collector side, once.
/// </summary>
public sealed partial class DriverLatencyCollector(IEnumerable<IDriverLatencyTracer> tracers, IOptionsMonitor<RecorderOptions> options, ILogger<DriverLatencyCollector> logger)
    : IPolledCollector<DriverLatencySample?>, ISampleConsumer, IDriverLatencyMonitor
{
    /// <summary>How long a measurement runs.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A processor's DPC or interrupt time at or above this share starts an automatic measurement… (a game at 200 fps on a
    /// 240 Hz screen keeps one processor 20-24 % in short GPU and USB routines, which is normal)
    /// </summary>
    public const double AutoPercent = 30;

    /// <summary>…when it lasts this many seconds in a row.</summary>
    public const int AutoSeconds = 3;

    /// <summary>Stutters within 10 s that start an automatic measurement.</summary>
    public const int AutoStutters = 5;

    /// <summary>Automatic measurements are at least this far apart.</summary>
    public static readonly TimeSpan AutoCooldown = TimeSpan.FromMinutes(15);

    private readonly IDriverLatencyTracer? _tracer = tracers.FirstOrDefault();
    private readonly ConcurrentQueue<DriverLatencySample> _finished = new();
    private readonly Queue<(DateTimeOffset At, int Stutters)> _stutters = new();
    private DateTimeOffset _lastAuto = DateTimeOffset.MinValue;
    private int _busySeconds;
    private int _measuring;

    /// <inheritdoc />
    public string Id => "driver-latency";

    /// <inheritdoc />
    public string Name => "driver-latency";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public int QueueCapacity => 30;

    /// <inheritdoc />
    public string? Problem => _tracer is null ? "Driver latency cannot be measured in this process." : _tracer.Problem;

    /// <inheritdoc />
    public bool Measuring => Volatile.Read(ref _measuring) == 1;

    /// <inheritdoc />
    public DriverLatencySample? Last { get; private set; }

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Driver latency", RequiredPrivilege.Elevated, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CollectorStatus(Problem is null ? Availability.Available : Availability.Unsupported, Problem));

    /// <inheritdoc />
    public ValueTask<DriverLatencySample?> CollectAsync(SampleContext context, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_finished.TryDequeue(out var sample) ? sample : null);

    /// <inheritdoc />
    public bool MeasureNow() => Start("measure");

    /// <inheritdoc />
    public ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (Wanted(frame) is { } reason && options.CurrentValue.DriverLatency && frame.Timestamp - _lastAuto >= AutoCooldown && Start(reason))
        {
            _lastAuto = frame.Timestamp;
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Why this frame asks for a measurement ("dpc" or "stutter"), or null (exposed for tests).</summary>
    internal string? Wanted(SampleFrame frame)
    {
        if (frame.Cpu is { } cpu)
        {
            var busiest = Math.Max(cpu.BusiestDpcCore?.Percent ?? 0, cpu.BusiestInterruptCore?.Percent ?? 0);
            _busySeconds = busiest >= AutoPercent ? _busySeconds + 1 : 0;
            if (_busySeconds >= AutoSeconds)
            {
                _busySeconds = 0;
                return "dpc";
            }
        }

        if (frame.Frames is { Stutters: > 0 } frames)
        {
            _stutters.Enqueue((frame.Timestamp, frames.Stutters));
        }

        while (_stutters.Count > 0 && frame.Timestamp - _stutters.Peek().At > TimeSpan.FromSeconds(10))
        {
            _stutters.Dequeue();
        }

        if (_stutters.Sum(s => s.Stutters) >= AutoStutters)
        {
            _stutters.Clear();
            return "stutter";
        }

        return null;
    }

    private bool Start(string reason)
    {
        if (_tracer is null || _tracer.Problem is not null || Interlocked.Exchange(ref _measuring, 1) == 1)
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var sample = await _tracer.MeasureAsync(Duration, reason, CancellationToken.None).ConfigureAwait(false);
                Last = sample;
                _finished.Enqueue(sample);
                var slowest = sample.Slowest;
                LogMeasured(logger, reason, sample.Seconds, slowest?.Driver ?? "-", slowest?.LongestUs ?? 0, sample.Problem ?? "");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(logger, ex, reason);
            }
            finally
            {
                Volatile.Write(ref _measuring, 0);
            }
        });
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Driver latency measured ({Reason}, {Seconds} s): slowest {Driver} at {Us} µs {Problem}")]
    private static partial void LogMeasured(ILogger logger, string reason, double seconds, string driver, double us, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Driver latency measurement ({Reason}) failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, string reason);
}
