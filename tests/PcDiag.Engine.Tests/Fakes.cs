using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Events;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Tests;

/// <summary>A collector whose behaviour is scripted per call.</summary>
internal sealed class FakeCollector(string id, SamplingTier tier, Func<SampleContext, CancellationToken, Task<CpuSample>>? behaviour = null)
    : IPolledCollector<CpuSample>
{
    public int Calls;

    public string Id => id;

    public SamplingTier Tier => tier;

    public Func<CancellationToken, ValueTask<CollectorStatus>> Initialize { get; set; } =
        _ => ValueTask.FromResult(new CollectorStatus(Availability.Available));

    public CollectorInfo Describe() => new(id, id, RequiredPrivilege.StandardUser, CostClass.Cheap);

    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken) => Initialize(cancellationToken);

    public async ValueTask<CpuSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        return behaviour is null ? Sample(context.Timestamp, 42) : await behaviour(context, cancellationToken);
    }

    public static CpuSample Sample(DateTimeOffset timestamp, double total)
    {
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        return new CpuSample(timestamp, Reading<double>.Of(total), none, none, none, none, none, none,
            Reading<long>.Unavailable(Availability.Unsupported), none, none, none, none, none, []);
    }
}

/// <summary>Records frames it receives; can be told to block.</summary>
internal sealed class RecordingConsumer(string name = "test", int capacity = 64) : ISampleConsumer
{
    public List<SampleFrame> Frames { get; } = [];

    public int Flushes;

    public TaskCompletionSource? Gate { get; set; }

    public string Name => name;

    public int QueueCapacity => capacity;

    /// <summary>True to act as the live view, which gets frames whole during a custom recording.</summary>
    public bool SeesUnrecordedData { get; init; }

    public async ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken);
        }

        lock (Frames)
        {
            Frames.Add(frame);
        }
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Flushes);
        return ValueTask.CompletedTask;
    }
}

internal sealed class InMemorySessionStore : IRecorderSessionStore
{
    public List<(long Id, RecordingStartReason Start, RecordingEndReason? End)> Sessions { get; } = [];

    /// <summary>Each session's selection (null = everything), in session order.</summary>
    public List<RecordingSelection?> Selections { get; } = [];

    public Task<long> StartSessionAsync(DateTimeOffset startedAt, RecordingStartReason reason, bool elevated, string appVersion, RecordingSelection? selection, CancellationToken cancellationToken)
    {
        var id = Sessions.Count + 1;
        Sessions.Add((id, reason, null));
        Selections.Add(selection);
        return Task.FromResult((long)id);
    }

    public Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, RecordingEndReason reason, CancellationToken cancellationToken)
    {
        var index = Sessions.FindIndex(s => s.Id == sessionId);
        Sessions[index] = Sessions[index] with { End = reason };
        return Task.CompletedTask;
    }

    public Task<int> CloseAbandonedSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class FakePowerEvents : ISystemPowerEvents
{
    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public event EventHandler? SessionEnding;

    public void RaiseSuspending() => Suspending?.Invoke(this, EventArgs.Empty);

    public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);

    public void RaiseSessionEnding() => SessionEnding?.Invoke(this, EventArgs.Empty);
}

internal sealed class InMemoryHeartbeatStore : IAgentHeartbeatStore
{
    public AgentHeartbeatState? State { get; set; }

    public List<BootRecovery> Recoveries { get; } = [];

    public int Beats { get; private set; }

    public Task<AgentHeartbeatState?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

    public Task BeatAsync(DateTimeOffset at, string bootId, bool recording, CancellationToken cancellationToken)
    {
        Beats++;
        State = new AgentHeartbeatState(at, bootId, false, recording);
        return Task.CompletedTask;
    }

    public Task MarkCleanShutdownAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        State = (State ?? new AgentHeartbeatState(at, null, true, false)) with { LastBeat = at, CleanShutdown = true, Recording = false };
        return Task.CompletedTask;
    }

    public Task<long> RecordBootRecoveryAsync(BootRecovery recovery, CancellationToken cancellationToken)
    {
        Recoveries.Add(recovery);
        return Task.FromResult((long)Recoveries.Count);
    }
}

internal sealed class FakeBootEvidence(DateTimeOffset bootTime, params PcDiag.Core.Events.SystemEvent[] events) : IBootEvidenceSource
{
    public bool Fail { get; init; }

    public DateTimeOffset BootTime => bootTime;

    public Task<IReadOnlyList<PcDiag.Core.Events.SystemEvent>> ReadSinceAsync(DateTimeOffset since, CancellationToken cancellationToken) =>
        Fail ? throw new InvalidOperationException("event log unavailable") : Task.FromResult<IReadOnlyList<PcDiag.Core.Events.SystemEvent>>(events);
}

internal sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
{
    private readonly List<Action<T, string?>> _listeners = [];

    public T CurrentValue { get; set; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener)
    {
        _listeners.Add(listener);
        return null;
    }

    /// <summary>Changes the value and notifies listeners, like a reloaded settings file.</summary>
    public void Set(T value)
    {
        CurrentValue = value;
        foreach (var listener in _listeners)
        {
            listener(value, null);
        }
    }
}

internal static class EngineFactory
{
    public static TelemetryScheduler Scheduler(FakeTimeProvider time, SampleBus bus, params IPolledCollector<CpuSample>[] collectors) =>
        Scheduler(time, bus, Pump(time), collectors);

    public static EventPump Pump(FakeTimeProvider time, params IEventCollector[] collectors) =>
        new(collectors, new SystemEventBuffer(), time, NullLogger<EventPump>.Instance);

    public static TelemetryScheduler Scheduler(FakeTimeProvider time, SampleBus bus, EventPump pump, params IPolledCollector<CpuSample>[] collectors) =>
        new(
            collectors.Select(c => (IScheduledCollector)new ScheduledCollector<CpuSample>(c, (f, s) => f.Cpu = s)),
            bus,
            pump,
            new SelfMonitor(time),
            new StaticOptionsMonitor<SamplingOptions>(new SamplingOptions()),
            time,
            new ElevationInfo(false),
            NullLogger<TelemetryScheduler>.Instance);

    public static SampleBus Bus(params ISampleConsumer[] consumers) => new(consumers, NullLogger<SampleBus>.Instance);
}
