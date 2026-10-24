using System.Globalization;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Recording;

/// <summary>What a custom recording can select on this PC: fixed sources plus the devices and sensors last sampled.</summary>
public static class RecordingCatalogBuilder
{
    private static readonly Dictionary<EventCategory, string> EventLabels = new()
    {
        [EventCategory.Display] = "Display",
        [EventCategory.Gpu] = "GPU and graphics driver",
        [EventCategory.ApplicationCrash] = "Application crashes",
        [EventCategory.ApplicationHang] = "Application hangs",
        [EventCategory.WindowsErrorReporting] = "Windows Error Reporting",
        [EventCategory.Kernel] = "Kernel",
        [EventCategory.Disk] = "Disk",
        [EventCategory.FileSystem] = "File system",
        [EventCategory.Network] = "Network",
        [EventCategory.Device] = "Devices (plug and play)",
        [EventCategory.Service] = "Services",
        [EventCategory.UnexpectedShutdown] = "Unexpected shutdowns",
        [EventCategory.BugCheck] = "Blue screens (bug checks)",
        [EventCategory.HardwareError] = "Hardware errors (WHEA)",
        [EventCategory.Power] = "Power and sleep",
        [EventCategory.TimeChange] = "Time changes",
        [EventCategory.ResourceExhaustion] = "Resource exhaustion",
        [EventCategory.Process] = "Processes",
        [EventCategory.Other] = "Other events",
    };

    /// <summary>A Windows event category in words.</summary>
    public static string EventLabel(EventCategory category) => EventLabels.GetValueOrDefault(category, category.ToString());

    /// <summary>The catalog from the latest samples of the devices and sensors (null when not sampled yet).</summary>
    public static RecordingCatalog Build(GpuSample? gpu, DiskSample? disk, NetworkSample? network, SensorSample? sensors)
    {
        var sources = new List<RecordingSource>
        {
            new("cpu", "CPU", "cpu",
            [
                new(RecordingItems.CpuUsage, "Utilization", "Total, user and kernel time"),
                new(RecordingItems.CpuCores, "Per-core utilization and frequency"),
                new(RecordingItems.CpuClock, "Frequency and performance limits"),
                new(RecordingItems.CpuScheduler, "Queue, context switches and interrupts"),
                new(RecordingItems.CpuThermal, "Package temperature and power"),
            ]),
            new("memory", "Memory", "memory",
            [
                new(RecordingItems.MemoryUsage, "Usage", "In use, available, cache, commit and pools"),
                new(RecordingItems.MemoryPaging, "Paging", "Page faults, hard faults and memory pressure"),
            ]),
        };

        foreach (var a in gpu?.Adapters ?? [])
        {
            var key = a.Adapter.AdapterKey;
            sources.Add(new($"gpu:{key}", a.Adapter.Name, "gpu", RecordingItems.GpuData.Select(d => new RecordingItem(RecordingItems.Gpu(key, d.Suffix), d.Label)).ToList()));
        }

        foreach (var d in disk?.Disks ?? [])
        {
            var key = d.Disk.DiskKey;
            var label = string.Create(CultureInfo.InvariantCulture, $"Disk {d.Disk.Number}: {d.Disk.Model}");
            sources.Add(new($"disk:{key}", label, "disk", RecordingItems.DiskData.Select(i => new RecordingItem(RecordingItems.Disk(key, i.Suffix), i.Label)).ToList()));
        }

        sources.Add(new("volumes", "Volumes", "volumes", [new(RecordingItems.VolumeSpace, "Capacity and free space")]));

        foreach (var n in network?.Interfaces ?? [])
        {
            var key = n.Interface.InterfaceKey;
            sources.Add(new($"net:{key}", n.Interface.Name, "network", RecordingItems.NetworkData.Select(i => new RecordingItem(RecordingItems.Network(key, i.Suffix), i.Label)).ToList()));
        }

        // Hardware sensors one by one, grouped by device; shared keys (aliases) follow the sensor they stand in for.
        foreach (var device in (sensors?.Readings ?? []).Where(r => r.Sensor.AliasOf is null).GroupBy(r => r.Sensor.HardwareName, StringComparer.Ordinal))
        {
            var items = device
                .GroupBy(r => r.Sensor.LogicalKey, StringComparer.Ordinal)
                .Select(g => g.First().Sensor)
                .Select(s => new RecordingItem(RecordingItems.Sensor(s.LogicalKey), s.Name, string.IsNullOrEmpty(s.Unit) ? s.Kind.ToString() : $"{s.Kind}, {s.Unit}"))
                .ToList();
            sources.Add(new($"sensors:{device.Key}", device.Key, "sensors", items));
        }

        sources.Add(new("processes", "Processes", "processes",
        [
            new(RecordingItems.ProcessMetrics, "Per-process figures", "CPU, memory, disk and GPU use of every process"),
            new(RecordingItems.ProcessLifecycle, "Starts, exits and hangs"),
        ]));
        sources.Add(new("events", "Windows events", "events",
            Enum.GetValues<EventCategory>().Select(c => new RecordingItem(RecordingItems.Events(c), EventLabel(c))).ToList()));
        sources.Add(new("display", "Display", "display", [new(RecordingItems.Display, "Configuration and changes", "Monitors, modes, refresh rates, HDR")]));
        sources.Add(new("services", "Services", "services", [new(RecordingItems.Services, "Service states and changes")]));
        sources.Add(new("inventory", "System inventory", "inventory", [new(RecordingItems.Inventory, "Hardware, drivers and system facts")]));
        return new RecordingCatalog(sources);
    }
}
