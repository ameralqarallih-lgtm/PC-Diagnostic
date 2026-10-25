using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PcDiag.Collectors.Cpu;
using PcDiag.Collectors.Devices;
using PcDiag.Collectors.Disk;
using PcDiag.Collectors.Display;
using PcDiag.Collectors.Events;
using PcDiag.Collectors.Gpu;
using PcDiag.Collectors.Inventory;
using PcDiag.Collectors.Memory;
using PcDiag.Collectors.Network;
using PcDiag.Collectors.Processes;
using PcDiag.Collectors.Recording;
using PcDiag.Collectors.Sensors;
using PcDiag.Collectors.Services;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Engine;
using PcDiag.Platform.Windows.Power;
using PcDiag.Platform.Windows.Tracing;

namespace PcDiag.Collectors;

/// <summary>Registers the Windows collector plugins.</summary>
public static class CollectorServiceCollectionExtensions
{
    /// <summary>Adds every Windows collector implemented so far.</summary>
    public static IServiceCollection AddWindowsCollectors(this IServiceCollection services)
    {
        services.AddPolledCollector<CpuCollector, CpuSample>((frame, sample) => frame.Cpu = sample);
        services.AddPolledCollector<MemoryCollector, MemorySample>((frame, sample) => frame.Memory = sample);

        // GPU: detail providers are plugins; the collector merges them by priority.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGpuDetailProvider, NvmlGpuDetailProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGpuDetailProvider, D3dkmtGpuDetailProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGpuDetailProvider, PawnIoNvidiaGpuDetailProvider>());
        services.AddPolledCollector<GpuCollector, GpuSample>((frame, sample) => frame.Gpu = sample);
        services.AddPolledCollector<DiskCollector, DiskSample>((frame, sample) => frame.Disk = sample);
        services.AddPolledCollector<NetworkCollector, NetworkSample>((frame, sample) => frame.Network = sample);
        services.AddPolledCollector<DisplayCollector, DisplaySample?>((frame, sample) => frame.Display = sample);
        services.TryAddSingleton<IDisplayCollector>(sp => sp.GetRequiredService<DisplayCollector>());

        // Enhanced mode (off by default; needs elevation): an ETW flight recorder and true per-process disk/network I/O.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFlightRecorder, WprFlightRecorder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProcessIoMonitor, EtwProcessIo>());
        services.TryAddSingleton<IFileSignatureVerifier, WindowsFileSignatureVerifier>();
        services.TryAddSingleton<ProcessSignatures>();
        services.TryAddSingleton<Core.Control.ISystemControl, Control.SystemControlService>();
        services.AddPolledCollector<ProcessCollector, ProcessSample>((frame, sample) => frame.Processes = sample);
        services.AddPolledCollector<ServiceCollector, ServiceSample>((frame, sample) => frame.Services = sample);
        services.TryAddSingleton<IServiceCollector>(sp => sp.GetRequiredService<ServiceCollector>());

        // Hardware sensors: providers are plugins (LibreHardwareMonitor is registered by the host); merged by priority.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHardwareSensorProvider, StorageReliabilitySensorProvider>());
        services.AddPolledCollector<SensorCollector, SensorSample>((frame, sample) => frame.Sensors = sample);
        services.AddPolledCollector<InventoryCollector, InventorySample?>((frame, sample) => frame.Inventory = sample);

        // Recorder health: sleep/wake and end-of-session notifications, and Windows events about past shutdowns.
        services.TryAddSingleton<SystemPowerEvents>();
        services.TryAddSingleton<ISystemPowerEvents>(sp => sp.GetRequiredService<SystemPowerEvents>());
        services.TryAddSingleton<IBootEvidenceSource, WindowsBootEvidenceSource>();

        // Event collectors run while recording; their events arrive with the next frame.
        services.TryAddSingleton<WindowsEventLogCollector>();
        services.AddSingleton<IEventCollector>(sp => sp.GetRequiredService<WindowsEventLogCollector>());
        services.TryAddSingleton<DeviceChangeCollector>();
        services.AddSingleton<IEventCollector>(sp => sp.GetRequiredService<DeviceChangeCollector>());
        return services;
    }
}
