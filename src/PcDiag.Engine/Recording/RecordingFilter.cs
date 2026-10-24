using PcDiag.Core.Events;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using static PcDiag.Core.Recording.RecordingItems;

namespace PcDiag.Engine.Recording;

/// <summary>
/// Keeps only the selected data of a frame for a custom recording. A device or source with nothing selected is left
/// out; within a source, unselected readings become <see cref="Availability.NotSelected"/> so stored series show why
/// they have no value. The recorder's own diagnostics are always kept. Unchanged objects (process rows, sensor
/// readings) keep their identity, so the ring buffer still shares them between frames.
/// </summary>
public static class RecordingFilter
{
    /// <summary>The part of <paramref name="frame"/> a recording of <paramref name="items"/> stores.</summary>
    public static SampleFrame Apply(SampleFrame frame, IReadOnlySet<string> items)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(items);
        return frame with
        {
            Cpu = Cpu(frame.Cpu, items),
            Memory = Memory(frame.Memory, items),
            Gpu = Gpu(frame.Gpu, items),
            Disk = Disk(frame.Disk, items),
            Network = Network(frame.Network, items),
            Processes = Processes(frame.Processes, items),
            Sensors = frame.Sensors is { } sensors ? sensors with { Readings = sensors.Readings.Where(r => Selected(r.Sensor, items)).ToList() } : null,
            Services = items.Contains(RecordingItems.Services) ? frame.Services : null,
            Inventory = items.Contains(RecordingItems.Inventory) ? frame.Inventory : null,
            Events = frame.Events?.Where(e => items.Contains(RecordingItems.Events(e.Category))).ToList() is { Count: > 0 } events ? events : null,
            Display = items.Contains(RecordingItems.Display) ? frame.Display : null,
        };
    }

    /// <summary>A sensor reading is kept when its sensor, or the sensor it stands in for (a shared key), is selected.</summary>
    public static bool Selected(SensorDescriptor sensor, IReadOnlySet<string> items)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        ArgumentNullException.ThrowIfNull(items);
        return items.Contains(Sensor(sensor.LogicalKey)) || (sensor.AliasOf is { } original && items.Contains(Sensor(original)));
    }

    private static Reading<T> Keep<T>(bool selected, Reading<T> reading)
        where T : struct => selected ? reading : Reading<T>.Unavailable(Availability.NotSelected);

    private static CpuSample? Cpu(CpuSample? cpu, IReadOnlySet<string> items)
    {
        if (cpu is null)
        {
            return null;
        }

        var (usage, cores, clock, scheduler, thermal) =
            (items.Contains(CpuUsage), items.Contains(CpuCores), items.Contains(CpuClock), items.Contains(CpuScheduler), items.Contains(CpuThermal));
        if (!(usage || cores || clock || scheduler || thermal))
        {
            return null;
        }

        return cpu with
        {
            TotalPercent = Keep(usage, cpu.TotalPercent),
            UserPercent = Keep(usage, cpu.UserPercent),
            KernelPercent = Keep(usage, cpu.KernelPercent),
            FrequencyMhz = Keep(clock, cpu.FrequencyMhz),
            MaxFrequencyMhz = Keep(clock, cpu.MaxFrequencyMhz),
            PerformancePercent = Keep(clock, cpu.PerformancePercent),
            PerformanceLimitPercent = Keep(clock, cpu.PerformanceLimitPercent),
            PerformanceLimitFlags = Keep(clock, cpu.PerformanceLimitFlags),
            ProcessorQueueLength = Keep(scheduler, cpu.ProcessorQueueLength),
            ContextSwitchesPerSec = Keep(scheduler, cpu.ContextSwitchesPerSec),
            InterruptsPerSec = Keep(scheduler, cpu.InterruptsPerSec),
            PackageTemperatureC = Keep(thermal, cpu.PackageTemperatureC),
            PackagePowerW = Keep(thermal, cpu.PackagePowerW),
            Cores = cores ? cpu.Cores : [],
        };
    }

    private static MemorySample? Memory(MemorySample? memory, IReadOnlySet<string> items)
    {
        if (memory is null)
        {
            return null;
        }

        var (usage, paging) = (items.Contains(MemoryUsage), items.Contains(MemoryPaging));
        if (!(usage || paging))
        {
            return null;
        }

        return memory with
        {
            TotalBytes = Keep(usage, memory.TotalBytes),
            AvailableBytes = Keep(usage, memory.AvailableBytes),
            FreeBytes = Keep(usage, memory.FreeBytes),
            CacheBytes = Keep(usage, memory.CacheBytes),
            CommittedBytes = Keep(usage, memory.CommittedBytes),
            CommitLimitBytes = Keep(usage, memory.CommitLimitBytes),
            PagedPoolBytes = Keep(usage, memory.PagedPoolBytes),
            NonPagedPoolBytes = Keep(usage, memory.NonPagedPoolBytes),
            CompressedBytes = Keep(usage, memory.CompressedBytes),
            PageFaultsPerSec = Keep(paging, memory.PageFaultsPerSec),
            HardFaultsPerSec = Keep(paging, memory.HardFaultsPerSec),
            PagesPerSec = Keep(paging, memory.PagesPerSec),
            Pressure = Keep(paging, memory.Pressure),
        };
    }

    private static GpuSample? Gpu(GpuSample? gpu, IReadOnlySet<string> items)
    {
        if (gpu is null)
        {
            return null;
        }

        var adapters = new List<GpuAdapterSample>();
        foreach (var a in gpu.Adapters)
        {
            var key = a.Adapter.AdapterKey;
            var (usage, vram, thermal, clocks, power) = (items.Contains(RecordingItems.Gpu(key, "usage")), items.Contains(RecordingItems.Gpu(key, "vram")),
                items.Contains(RecordingItems.Gpu(key, "thermal")), items.Contains(RecordingItems.Gpu(key, "clocks")), items.Contains(RecordingItems.Gpu(key, "power")));
            if (!(usage || vram || thermal || clocks || power))
            {
                continue;
            }

            adapters.Add(a with
            {
                UtilizationPercent = Keep(usage, a.UtilizationPercent),
                Utilization3dPercent = Keep(usage, a.Utilization3dPercent),
                UtilizationComputePercent = Keep(usage, a.UtilizationComputePercent),
                UtilizationDecodePercent = Keep(usage, a.UtilizationDecodePercent),
                UtilizationEncodePercent = Keep(usage, a.UtilizationEncodePercent),
                UtilizationCopyPercent = Keep(usage, a.UtilizationCopyPercent),
                VramUsedBytes = Keep(vram, a.VramUsedBytes),
                VramTotalBytes = Keep(vram, a.VramTotalBytes),
                SharedUsedBytes = Keep(vram, a.SharedUsedBytes),
                TemperatureC = Keep(thermal, a.TemperatureC),
                HotspotTemperatureC = Keep(thermal, a.HotspotTemperatureC),
                FanRpm = Keep(thermal, a.FanRpm),
                CoreClockMhz = Keep(clocks, a.CoreClockMhz),
                MemoryClockMhz = Keep(clocks, a.MemoryClockMhz),
                PowerW = Keep(power, a.PowerW),
                PowerPercent = Keep(power, a.PowerPercent),
                PowerLimitW = Keep(power, a.PowerLimitW),
                ThrottleFlags = Keep(power, a.ThrottleFlags),
            });
        }

        var processes = gpu.Processes.Where(p => items.Contains(RecordingItems.Gpu(p.AdapterKey, "processes"))).ToList();
        return adapters.Count == 0 && processes.Count == 0 ? null : gpu with { Adapters = adapters, Processes = processes };
    }

    private static DiskSample? Disk(DiskSample? disk, IReadOnlySet<string> items)
    {
        if (disk is null)
        {
            return null;
        }

        var disks = new List<PhysicalDiskSample>();
        foreach (var d in disk.Disks)
        {
            var key = d.Disk.DiskKey;
            var (activity, latency, temperature) =
                (items.Contains(RecordingItems.Disk(key, "activity")), items.Contains(RecordingItems.Disk(key, "latency")), items.Contains(RecordingItems.Disk(key, "temperature")));
            if (!(activity || latency || temperature))
            {
                continue;
            }

            disks.Add(d with
            {
                ReadBytesPerSec = Keep(activity, d.ReadBytesPerSec),
                WriteBytesPerSec = Keep(activity, d.WriteBytesPerSec),
                ReadIops = Keep(activity, d.ReadIops),
                WriteIops = Keep(activity, d.WriteIops),
                QueueLength = Keep(activity, d.QueueLength),
                ActivePercent = Keep(activity, d.ActivePercent),
                ReadLatencyMs = Keep(latency, d.ReadLatencyMs),
                WriteLatencyMs = Keep(latency, d.WriteLatencyMs),
                TemperatureC = Keep(temperature, d.TemperatureC),
            });
        }

        var volumes = items.Contains(VolumeSpace) ? disk.Volumes : [];
        return disks.Count == 0 && volumes.Count == 0 ? null : disk with { Disks = disks, Volumes = volumes };
    }

    private static NetworkSample? Network(NetworkSample? network, IReadOnlySet<string> items)
    {
        if (network is null)
        {
            return null;
        }

        var interfaces = new List<NetworkInterfaceSample>();
        foreach (var n in network.Interfaces)
        {
            var key = n.Interface.InterfaceKey;
            var (throughput, errors, link) = (items.Contains(RecordingItems.Network(key, "throughput")), items.Contains(RecordingItems.Network(key, "errors")),
                items.Contains(RecordingItems.Network(key, "link")));
            if (!(throughput || errors || link))
            {
                continue;
            }

            interfaces.Add(n with
            {
                ReceiveBytesPerSec = Keep(throughput, n.ReceiveBytesPerSec),
                SendBytesPerSec = Keep(throughput, n.SendBytesPerSec),
                ReceivePacketsPerSec = Keep(throughput, n.ReceivePacketsPerSec),
                SendPacketsPerSec = Keep(throughput, n.SendPacketsPerSec),
                UtilizationPercent = Keep(throughput, n.UtilizationPercent),
                ReceiveErrors = Keep(errors, n.ReceiveErrors),
                SendErrors = Keep(errors, n.SendErrors),
                ReceiveDiscards = Keep(errors, n.ReceiveDiscards),
                SendDiscards = Keep(errors, n.SendDiscards),
                LinkSpeedBitsPerSec = Keep(link, n.LinkSpeedBitsPerSec),
                WifiSignalQualityPercent = Keep(link, n.WifiSignalQualityPercent),
                WifiRssiDbm = Keep(link, n.WifiRssiDbm),
            });
        }

        return interfaces.Count == 0 ? null : network with { Interfaces = interfaces };
    }

    private static ProcessSample? Processes(ProcessSample? sample, IReadOnlySet<string> items)
    {
        if (sample is null)
        {
            return null;
        }

        var (metrics, lifecycle) = (items.Contains(RecordingItems.ProcessMetrics), items.Contains(ProcessLifecycle));
        if (!(metrics || lifecycle))
        {
            return null;
        }

        // Identities name the processes in both the figures and the lifecycle events.
        return sample with { Processes = metrics ? sample.Processes : [], Events = lifecycle ? sample.Events : [] };
    }
}
