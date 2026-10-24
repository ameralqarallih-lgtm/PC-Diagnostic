using PcDiag.Core.Anomalies;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Anomalies;

/// <summary>One value of an anomaly metric in a frame.</summary>
/// <param name="Metric">Metric ID (see <see cref="AnomalyMetrics"/>).</param>
/// <param name="DeviceKey">Stable device key ("" for system-wide metrics).</param>
/// <param name="Device">Device name, for per-device metrics.</param>
/// <param name="Value">The value.</param>
internal readonly record struct MetricValue(string Metric, string DeviceKey, string? Device, double Value);

/// <summary>
/// Turns a sample frame into the values anomaly rules watch. Hardware sensors may be sampled less often than
/// system metrics, so the latest sensor readings are kept for a few seconds.
/// </summary>
internal sealed class MetricExtractor
{
    private static readonly TimeSpan SensorsValidFor = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan SamplesValidFor = TimeSpan.FromSeconds(10);

    private IReadOnlyDictionary<string, SensorReading>? _sensors;
    private DateTimeOffset _sensorsAt;
    private (CpuSample? Cpu, MemorySample? Memory, GpuSample? Gpu, DiskSample? Disk, NetworkSample? Network, SensorSample? Sensors) _latest;
    private (DateTimeOffset Cpu, DateTimeOffset Memory, DateTimeOffset Gpu, DateTimeOffset Disk, DateTimeOffset Network, DateTimeOffset Sensors) _latestAt;

    /// <summary>The system's readings the rules watch (without the "reading:" prefix); empty: none, nothing extra is computed.</summary>
    public IReadOnlySet<string> Readings { get; set; } = new HashSet<string>();

    /// <summary>Adds the frame's values to <paramref name="output"/>.</summary>
    public void Extract(SampleFrame frame, List<MetricValue> output)
    {
        if (frame.Sensors is { } sensors)
        {
            _sensors = sensors.Readings.GroupBy(r => r.Sensor.LogicalKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            _sensorsAt = frame.Timestamp;
        }

        var current = frame.Timestamp - _sensorsAt <= SensorsValidFor ? _sensors : null;
        if (Readings.Count > 0)
        {
            AddReadings(frame, output);
        }

        if (frame.Cpu is { } cpu)
        {
            Add(output, AnomalyMetrics.Cpu, cpu.TotalPercent);
            Add(output, AnomalyMetrics.CpuTemperature, SensorKeys.Prefer(cpu.PackageTemperatureC, current, SensorKeys.CpuPackageTemperature));
        }

        if (frame.Memory is { } memory)
        {
            if (memory.AvailableBytes.HasValue)
            {
                output.Add(new(AnomalyMetrics.MemoryAvailable, "", null, memory.AvailableBytes.Value!.Value / 1_073_741_824.0));
            }

            if (memory.CommittedBytes.HasValue && memory.CommitLimitBytes is { HasValue: true, Value: > 0 } limit)
            {
                output.Add(new(AnomalyMetrics.MemoryCommit, "", null, 100.0 * memory.CommittedBytes.Value!.Value / limit.Value!.Value));
            }
        }

        foreach (var gpu in frame.Gpu?.Adapters ?? [])
        {
            var key = gpu.Adapter.AdapterKey;
            var name = gpu.Adapter.Name;
            Add(output, AnomalyMetrics.GpuUtilization, gpu.UtilizationPercent, key, name);
            Add(output, AnomalyMetrics.GpuTemperature, gpu.TemperatureC, key, name);
            Add(output, AnomalyMetrics.GpuHotspot, SensorKeys.Prefer(gpu.HotspotTemperatureC, current, SensorKeys.GpuHotspot(name)), key, name);
            Add(output, AnomalyMetrics.GpuCoreClock, gpu.CoreClockMhz, key, name);
            if (gpu.VramUsedBytes.HasValue && gpu.VramTotalBytes is { HasValue: true, Value: > 0 } total)
            {
                output.Add(new(AnomalyMetrics.GpuVram, key, name, 100.0 * gpu.VramUsedBytes.Value!.Value / total.Value!.Value));
            }
        }

        foreach (var disk in frame.Disk?.Disks ?? [])
        {
            var key = disk.Disk.DiskKey;
            var name = disk.Disk.Model;
            if (disk.ReadLatencyMs.HasValue || disk.WriteLatencyMs.HasValue)
            {
                output.Add(new(AnomalyMetrics.DiskLatency, key, name, Math.Max(disk.ReadLatencyMs.GetValueOrDefault(0), disk.WriteLatencyMs.GetValueOrDefault(0))));
            }

            Add(output, AnomalyMetrics.DiskQueue, disk.QueueLength, key, name);
        }

        foreach (var network in frame.Network?.Interfaces ?? [])
        {
            if (network.ReceiveErrors.HasValue || network.SendErrors.HasValue)
            {
                output.Add(new(AnomalyMetrics.NetworkErrors, network.Interface.InterfaceKey, network.Interface.Name,
                    network.ReceiveErrors.GetValueOrDefault(0) + network.SendErrors.GetValueOrDefault(0)));
            }
        }
    }

    /// <summary>
    /// The readings the rules watch, as the on-screen panels compute them, from the newest sample of each collector
    /// (collectors run on different tiers, so a frame rarely has them all).
    /// </summary>
    private void AddReadings(SampleFrame frame, List<MetricValue> output)
    {
        var at = frame.Timestamp;
        if (frame.Cpu is { } cpu) { _latest.Cpu = cpu; _latestAt.Cpu = at; }
        if (frame.Memory is { } memory) { _latest.Memory = memory; _latestAt.Memory = at; }
        if (frame.Gpu is { } gpu) { _latest.Gpu = gpu; _latestAt.Gpu = at; }
        if (frame.Disk is { } disk) { _latest.Disk = disk; _latestAt.Disk = at; }
        if (frame.Network is { } network) { _latest.Network = network; _latestAt.Network = at; }
        if (frame.Sensors is { } sensors) { _latest.Sensors = sensors; _latestAt.Sensors = at; }

        T? Fresh<T>(T? sample, DateTimeOffset sampledAt) where T : class => at - sampledAt <= SamplesValidFor ? sample : null;
        var samples = new Overlay.LiveSamples(at, Fresh(_latest.Cpu, _latestAt.Cpu), Fresh(_latest.Memory, _latestAt.Memory), Fresh(_latest.Gpu, _latestAt.Gpu),
            Fresh(_latest.Disk, _latestAt.Disk), Fresh(_latest.Network, _latestAt.Network), Fresh(_latest.Sensors, _latestAt.Sensors));
        foreach (var source in Overlay.OverlaySources.Build(samples, at, recording: true))
        {
            if (source.Value is { } value && double.IsFinite(value) && Readings.Contains(source.Id))
            {
                output.Add(new(AnomalyMetrics.ReadingPrefix + source.Id, "", source.Group, value));
            }
        }
    }

    private static void Add(List<MetricValue> output, string metric, Reading<double> reading, string deviceKey = "", string? device = null)
    {
        if (reading.HasValue && double.IsFinite(reading.Value!.Value))
        {
            output.Add(new(metric, deviceKey, device, reading.Value.Value));
        }
    }
}
