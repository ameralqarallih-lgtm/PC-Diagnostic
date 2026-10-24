using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PcDiag.Agent.Ipc;
using PcDiag.Agent.Tray;
using PcDiag.Collectors;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Engine;
using PcDiag.Hosting;
using PcDiag.Platform.Windows.Gpu;
using PcDiag.Sensors.LibreHardwareMonitor;
using PcDiag.Storage.Sqlite;

namespace PcDiag.Agent;

/// <summary>The Agent's service composition, shared by Program and the composition tests.</summary>
internal static class AgentHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddPcDiagHost("agent");
        builder.Services.AddSingleton(ElevationDetector.Detect());
        builder.Services.AddTelemetryPersistence();
        builder.Services.AddSingleton<IHardwareSensorProvider>(sp => new LhmSensorProvider(
            sp.GetRequiredService<ElevationInfo>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<LhmSensorProvider>>())
        {
            UseIntelControlLibrary = LhmSensorProvider.HasIntelDiscreteGpu(Dxgi.EnumerateAdapters().Select(a => (a.VendorId, a.DedicatedVideoMemoryBytes))),
        });
        builder.Services.AddWindowsCollectors();

        // Sensors of approved driver packages (read by the screen host without administrator rights).
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHardwareSensorProvider, Screens.ExternalSensorProvider>());

        builder.Services.AddPcDiagEngine();
        builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration.GetSection(AgentOptions.SectionName)).ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AgentOptions>, AgentOptionsValidator>());
        builder.Services.AddHostedService<AgentLifetimeService>();
        builder.Services.AddHostedService<AgentIpcService>();
        builder.Services.AddHostedService<TrayService>();

        // Windows drivers for devices that have none (installed after a confirmation on the PC itself).
        builder.Services.AddSingleton<Core.Devices.IWindowsDrivers, Devices.WindowsDriverService>();

        // What the recorder costs in CPU, by part, in its log every minute.
        builder.Services.AddHostedService<CostReportService>();

        // The file storage's upkeep, in a process of its own without administrator rights, from the moment the recorder starts.
        builder.Services.AddHostedService<Storage.StorageHostService>();

        // Readings on the screens (on-screen display), drawn by the recorder on its desktop; frame rates and round
        // trips for its gaming readings, measured only while they are wanted.
        builder.Services.AddSingleton<Core.Overlay.IFrameMonitor, Platform.Windows.Tracing.EtwFrameMonitor>();
        builder.Services.AddSingleton<Engine.Overlay.LatencyProbe>();
        builder.Services.AddSingleton<Core.Overlay.ILatencyProbe>(sp => sp.GetRequiredService<Engine.Overlay.LatencyProbe>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Engine.Overlay.LatencyProbe>());
        builder.Services.AddSingleton<Overlay.OverlayService>();

        // The hardware the user expects, checked every few seconds (missing devices, drives, monitors…).
        builder.Services.AddSingleton<Devices.HardwareWatch>();
        builder.Services.AddSingleton<Core.Devices.IHardwareWatch>(sp => sp.GetRequiredService<Devices.HardwareWatch>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Devices.HardwareWatch>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Overlay.OverlayService>());
        builder.Services.AddSingleton<Core.Overlay.IOverlayControl>(sp => sp.GetRequiredService<Overlay.OverlayService>());
        return builder;
    }
}
