using PcDiag.Core.Diagnostics;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Telemetry;

/// <summary>Mutable accumulator for one tick; turned into an immutable <see cref="SampleFrame"/>.</summary>
public sealed class SampleFrameBuilder
{
    /// <summary>CPU slot.</summary>
    public CpuSample? Cpu { get; set; }

    /// <summary>Memory slot.</summary>
    public MemorySample? Memory { get; set; }

    /// <summary>GPU slot.</summary>
    public GpuSample? Gpu { get; set; }

    /// <summary>Disk slot.</summary>
    public DiskSample? Disk { get; set; }

    /// <summary>Network slot.</summary>
    public NetworkSample? Network { get; set; }

    /// <summary>Process slot.</summary>
    public ProcessSample? Processes { get; set; }

    /// <summary>Sensor slot.</summary>
    public SensorSample? Sensors { get; set; }

    /// <summary>Service slot.</summary>
    public ServiceSample? Services { get; set; }

    /// <summary>Inventory slot (only filled when a new inventory is available).</summary>
    public InventorySample? Inventory { get; set; }

    /// <summary>Events that arrived since the previous tick.</summary>
    public IReadOnlyList<Core.Events.SystemEvent>? Events { get; set; }

    /// <summary>Display slot (only filled when something changed).</summary>
    public DisplaySample? Display { get; set; }

    /// <summary>Creates the frame.</summary>
    public SampleFrame Build(long sequence, DateTimeOffset timestamp, TickDiagnostics diagnostics, bool recorded = true, bool watched = false) =>
        new(sequence, timestamp, Cpu, Memory, Gpu, Disk, Network, Processes, Sensors, diagnostics, Services, Inventory, Events, Display, recorded, watched);
}

/// <summary>
/// A collector as seen by the scheduler: it runs on a tier and yields a function that stores its sample in
/// the frame. The scheduler applies results on its own thread, so a late result can never alter a published frame.
/// </summary>
public interface IScheduledCollector
{
    /// <summary>The underlying collector.</summary>
    ICollector Collector { get; }

    /// <summary>Sampling tier.</summary>
    SamplingTier Tier { get; }

    /// <summary>Collects a sample and returns how to store it in the frame.</summary>
    ValueTask<Action<SampleFrameBuilder>> CollectAsync(SampleContext context, CancellationToken cancellationToken);
}

/// <summary>Adapts a typed <see cref="IPolledCollector{TSample}"/> to <see cref="IScheduledCollector"/>.</summary>
/// <typeparam name="TSample">Sample type.</typeparam>
public sealed class ScheduledCollector<TSample>(IPolledCollector<TSample> collector, Action<SampleFrameBuilder, TSample> assign) : IScheduledCollector
{
    /// <inheritdoc />
    public ICollector Collector => collector;

    /// <inheritdoc />
    public SamplingTier Tier => collector.Tier;

    /// <inheritdoc />
    public async ValueTask<Action<SampleFrameBuilder>> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ValueTask<TSample> pending;
        using (CostMeter.Measure("collect." + collector.Id))
        {
            pending = collector.CollectAsync(context, cancellationToken);
        }

        var sample = await pending.ConfigureAwait(false);
        return builder => assign(builder, sample);
    }
}
