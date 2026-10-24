using PcDiag.Core.Anomalies;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Tests.Anomalies;

/// <summary>Builds sample frames with just the values a test needs.</summary>
internal static class Frames
{
    public static readonly GpuAdapterDescriptor Rtx4090 = new("10de:2684:0", "NVIDIA GeForce RTX 4090", "NVIDIA", 0x10de, 0x2684, 24L << 30, 32L << 30, NoD, NoD, ["nvml"]);

    public static readonly NetworkInterfaceDescriptor Ethernet = new("{guid-1}", "Ethernet", "Intel(R) Ethernet", NetworkInterfaceKind.Ethernet, null);

    private static Reading<double> NoD => Reading<double>.Unavailable(Availability.Unsupported);

    private static Reading<long> NoL => Reading<long>.Unavailable(Availability.Unsupported);

    public static SampleFrame At(
        DateTimeOffset ts,
        double? cpu = null,
        double? gpuUtil = null,
        double? gpuClock = null,
        double? gpuTemp = null,
        double? availableGb = null,
        long? netErrors = null,
        IReadOnlyList<SystemEvent>? events = null,
        IReadOnlyList<ProcessLifecycleEvent>? processEvents = null,
        IReadOnlyList<DisplayChange>? displayChanges = null,
        bool recorded = true)
    {
        var cpuSample = cpu is { } c ? FakeCollector.Sample(ts, c) : null;
        var memory = availableGb is { } gb
            ? new MemorySample(ts, Reading<long>.Of(32L << 30), Reading<long>.Of((long)(gb * (1L << 30))), NoL, NoL, NoL, NoL, NoL, NoL, NoL, NoD, NoD, NoD, NoD)
            : null;
        var gpu = gpuUtil is not null || gpuClock is not null || gpuTemp is not null
            ? new GpuSample(ts, [new GpuAdapterSample(Rtx4090, Of(gpuUtil), NoD, NoD, NoD, NoD, NoD, NoL, NoL, NoL, Of(gpuTemp), NoD, Of(gpuClock), NoD, NoD, NoD, NoD, NoL, NoD)], [])
            : null;
        var network = netErrors is { } errors
            ? new NetworkSample(ts, [new NetworkInterfaceSample(Ethernet, NoD, NoD, NoD, NoD, Reading<long>.Of(errors), Reading<long>.Of(0), NoL, NoL, NoL, NoD, true, NoD, NoD)])
            : null;
        var processes = processEvents is null ? null : new ProcessSample(ts, [], [], processEvents);
        var display = displayChanges is null ? null : new DisplaySample(ts, null, displayChanges);
        return new SampleFrame(0, ts, Cpu: cpuSample, Memory: memory, Gpu: gpu, Network: network, Processes: processes, Events: events, Display: display, Recorded: recorded);
    }

    public static SystemEvent Event(DateTimeOffset ts, string ruleId, string title, int eventId = 4101, string provider = "Display", string? process = null) =>
        new(ts, "System", 1, provider, eventId, EventLevel.Warning, EventCategory.Gpu, Severity.High, title, title, ProcessName: process, RuleId: ruleId);

    private static Reading<double> Of(double? value) => value is { } v ? Reading<double>.Of(v) : NoD;
}

/// <summary>Keeps anomalies in memory.</summary>
internal sealed class InMemoryAnomalyStore : IAnomalyStore
{
    private long _next;

    public Dictionary<long, Anomaly> Rows { get; } = [];

    public List<(long Id, string Key)> Links { get; } = [];

    public bool Fail { get; set; }

    public Task<long> AddAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new IOException("disk full");
        }

        var id = ++_next;
        Rows[id] = anomaly with { Id = id };
        return Task.FromResult(id);
    }

    public Task UpdateAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        Rows[anomaly.Id] = anomaly;
        return Task.CompletedTask;
    }

    public Task LinkIncidentAsync(long anomalyId, string incidentKey, CancellationToken cancellationToken)
    {
        Links.Add((anomalyId, incidentKey));
        Rows[anomalyId] = Rows[anomalyId] with { IncidentKey = incidentKey };
        return Task.CompletedTask;
    }

    public Task<int> CloseAbandonedAsync(DateTimeOffset before, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>Records captures; a capture while <see cref="Active"/> is set merges.</summary>
internal sealed class FakeCapture(TimeProvider time) : IIncidentCapture
{
    private int _next;

    public List<IncidentTrigger> Triggers { get; } = [];

    public Incident? Active { get; set; }

    public DateTimeOffset? ActiveUntil { get; set; }

    public event EventHandler<IncidentCapturedEventArgs>? Captured;

    public event EventHandler<IncidentCapturedEventArgs>? Finished;

    public Task<CaptureOutcome> CaptureAsync(IncidentTrigger trigger, CancellationToken cancellationToken)
    {
        Triggers.Add(trigger);
        var now = time.GetUtcNow();
        if (Active is { } active && ActiveUntil >= now)
        {
            return Task.FromResult(new CaptureOutcome(active, true, false, ActiveUntil.Value));
        }

        var incident = new Incident(++_next, IncidentKey.Create(new DateOnly(2026, 10, 6), _next), now, now, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2),
            IncidentState.Recording, trigger.Source, null, trigger.Detail ?? "", null, trigger.Severity ?? Severity.Medium);
        var outcome = new CaptureOutcome(incident, false, false, now.AddMinutes(2));
        Captured?.Invoke(this, new IncidentCapturedEventArgs(outcome));
        return Task.FromResult(outcome);
    }

    public Task<IncidentEditResult> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken) => Task.FromResult(IncidentEditResult.Done);

    public Task<IncidentEditResult> DeleteAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        Finished?.Invoke(this, null!);
        return Task.FromResult(IncidentEditResult.Done);
    }
}
