using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Incidents;
using PcDiag.Engine.Anomalies;
using PcDiag.Engine.Diagnostics;
using PcDiag.Engine.Events;
using PcDiag.Engine.Incidents;
using PcDiag.Engine.Ipc;
using PcDiag.Engine.Live;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine;

/// <summary>DI registration for the engine and collector plugins.</summary>
public static class EngineServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scheduler, sample bus, event pump, ring buffer, self-monitor, recording controller and heartbeat.
    /// Requires <see cref="ElevationInfo"/>, <see cref="TimeProvider"/>, an <see cref="IRecorderSessionStore"/>, an
    /// <see cref="IAgentHeartbeatStore"/>, an <see cref="IBootEvidenceSource"/>, <see cref="ISystemPowerEvents"/>, an
    /// <see cref="Core.Anomalies.IAnomalyStore"/>, an <see cref="Core.Diagnostics.IIncidentEvidenceSource"/>, and for
    /// the storage manager an <see cref="Core.Storage.ITelemetrySpace"/> and an <see cref="Core.Storage.IIncidentInventory"/>.
    /// </summary>
    public static IServiceCollection AddPcDiagEngine(this IServiceCollection services)
    {
        services.TryAddSingleton<SelfMonitor>();
        services.TryAddSingleton<SampleBus>();
        services.TryAddSingleton<SystemEventBuffer>();
        services.TryAddSingleton<ISystemEventSink>(sp => sp.GetRequiredService<SystemEventBuffer>());
        services.TryAddSingleton<EventPump>();
        services.TryAddSingleton<EventRuleProvider>();
        services.TryAddSingleton<TelemetryScheduler>();
        services.TryAddSingleton<RingBuffer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISampleConsumer, RingBuffer>(sp => sp.GetRequiredService<RingBuffer>()));
        services.TryAddSingleton<Control.ProcessDirectory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISampleConsumer, Control.ProcessDirectory>(sp => sp.GetRequiredService<Control.ProcessDirectory>()));
        services.TryAddSingleton<LiveFeed>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISampleConsumer, LiveFeed>(sp => sp.GetRequiredService<LiveFeed>()));
        services.TryAddSingleton<AnomalyTriggerChannel>();
        services.TryAddSingleton<AnomalyEngine>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISampleConsumer, AnomalyEngine>(sp => sp.GetRequiredService<AnomalyEngine>()));
        services.TryAddSingleton<TriggerService>();
        services.TryAddSingleton<IncidentDiagnostics>();
        services.TryAddSingleton<RecordingController>();
        services.TryAddSingleton<HeartbeatService>();
        services.TryAddSingleton<IncidentManager>();
        services.TryAddSingleton<IIncidentCapture>(sp => sp.GetRequiredService<IncidentManager>());
        services.TryAddSingleton<Core.Storage.StorageGuard>();
        services.TryAddSingleton<Core.Storage.IStorageGuard>(sp => sp.GetRequiredService<Core.Storage.StorageGuard>());
        services.TryAddSingleton<Storage.StorageManager>();
        services.TryAddSingleton<Core.Storage.IStorageControl>(sp => sp.GetRequiredService<Storage.StorageManager>());
        services.TryAddSingleton<AgentCommandHandler>();
        services.TryAddSingleton<IRecordingControl>(sp => sp.GetRequiredService<RecordingController>());
        services.TryAddSingleton<EnhancedModeService>();

        // Order matters: hosted services stop in reverse order, so the controller stops (and flushes through the
        // bus) before the bus itself stops, and the heartbeat marks a clean stop last.
        services.AddHostedService(sp => sp.GetRequiredService<HeartbeatService>());
        services.AddHostedService(sp => sp.GetRequiredService<SampleBus>());

        // The anomaly engine stops after the recorder, so running anomalies end where the recorded data ends.
        services.AddHostedService(sp => sp.GetRequiredService<AnomalyEngine>());
        services.AddHostedService(sp => sp.GetRequiredService<RecordingController>());

        // Stops first: the active incident is finished while the recorder and bus are still running.
        services.AddHostedService(sp => sp.GetRequiredService<IncidentManager>());

        // Stops before the incident manager, so no automatic capture starts while the active incident is finished.
        services.AddHostedService(sp => sp.GetRequiredService<TriggerService>());

        // Enhanced mode (optional ETW tracing) follows the recorder's state; it stops first.
        services.AddHostedService(sp => sp.GetRequiredService<EnhancedModeService>());

        // The storage manager: the space limit, the free space to keep, writing paused when the drive is almost full.
        services.AddHostedService(sp => sp.GetRequiredService<Storage.StorageManager>());
        return services;
    }

    /// <summary>
    /// Registers a polled collector plugin and where its sample goes in the frame, e.g.
    /// <c>services.AddPolledCollector&lt;CpuCollector, CpuSample&gt;((frame, sample) =&gt; frame.Cpu = sample)</c>.
    /// </summary>
    public static IServiceCollection AddPolledCollector<TCollector, TSample>(this IServiceCollection services, Action<SampleFrameBuilder, TSample> assign)
        where TCollector : class, IPolledCollector<TSample>
    {
        ArgumentNullException.ThrowIfNull(assign);
        services.TryAddSingleton<TCollector>();
        services.AddSingleton<IScheduledCollector>(sp => new ScheduledCollector<TSample>(sp.GetRequiredService<TCollector>(), assign));
        return services;
    }
}
