using PcDiag.Core.Abstractions;
using PcDiag.Core.Events;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Display;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Live;

/// <summary>
/// Keeps what the dashboard shows live: the newest sample of each collector (they run at different rates), recent
/// notable events, and top processes. Snapshots are built on request (the Web host asks about once a second while
/// a browser is watching), so there is no cost when nobody is looking.
/// </summary>
/// <remarks>
/// It must not depend on the recorder: the recorder depends on the sample bus, which depends on every consumer,
/// including this one. The caller passes the recording state to <see cref="Snapshot"/> instead.
/// </remarks>
public sealed class LiveFeed : ISampleConsumer
{
    /// <summary>Processes per top list.</summary>
    public const int TopProcesses = 8;

    /// <summary>Recent events kept.</summary>
    public const int MaxEvents = 30;

    /// <summary>Longest trend kept and returned, in minutes.</summary>
    public const int TrendMinutes = 10;

    /// <summary>Trend points kept: ten minutes at the fastest sampling interval (1 s).</summary>
    public const int TrendCapacity = TrendMinutes * 60;

    private readonly RingBuffer _ring;
    private readonly Lock _gate = new();
    private readonly LinkedList<LiveEvent> _events = new();
    private readonly TrendPoint[] _trend = new TrendPoint[TrendCapacity];
    private int _trendStart;
    private int _trendCount;
    private DateTimeOffset? _timestamp;
    private CpuSample? _cpu;
    private MemorySample? _memory;
    private GpuSample? _gpu;
    private DiskSample? _disk;
    private NetworkSample? _network;
    private ProcessSample? _processes;
    private SensorSample? _sensors;
    private TickDiagnostics? _diagnostics;
    private DisplaySnapshot? _display;

    /// <summary>Creates the feed.</summary>
    public LiveFeed(RingBuffer ring) => _ring = ring;

    /// <inheritdoc />
    public string Name => "live";

    /// <inheritdoc />
    public int QueueCapacity => 8;

    /// <summary>The live view shows everything, also what a custom recording leaves out.</summary>
    public bool SeesUnrecordedData => true;

    /// <summary>What a custom recording can select, from the latest samples (devices and sensors seen so far).</summary>
    public RecordingCatalog Catalog()
    {
        lock (_gate)
        {
            return RecordingCatalogBuilder.Build(_gpu, _disk, _network, _sensors);
        }
    }

    /// <summary>Whether devices and sensors have been sampled, so <see cref="Catalog"/> lists them.</summary>
    public bool HasSamples
    {
        get
        {
            lock (_gate)
            {
                return _sensors is not null && _gpu is not null && _disk is not null && _network is not null;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            _timestamp = frame.Timestamp;
            _cpu = frame.Cpu ?? _cpu;
            _memory = frame.Memory ?? _memory;
            _gpu = frame.Gpu ?? _gpu;
            _disk = frame.Disk ?? _disk;
            _network = frame.Network ?? _network;
            _processes = frame.Processes ?? _processes;
            _sensors = frame.Sensors ?? _sensors;
            _diagnostics = frame.Diagnostics ?? _diagnostics;
            _display = frame.Display?.Snapshot ?? _display;
            if (frame.Cpu is not null)
            {
                AddTrendPoint(frame.Timestamp);
            }

            foreach (var e in EventsOf(frame))
            {
                _events.AddFirst(e);
                if (_events.Count > MaxEvents)
                {
                    _events.RemoveLast();
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>The current live view.</summary>
    /// <param name="recording">Whether the recorder is recording now.</param>
    public LiveSnapshot Snapshot(bool recording)
    {
        var ring = _ring.Stats;
        lock (_gate)
        {
            var sensors = SensorMap();
            return new LiveSnapshot(
                _timestamp,
                recording,
                _cpu is { } cpu ? new LiveCpu(M(cpu.TotalPercent), M(cpu.FrequencyMhz), M(SensorKeys.Prefer(cpu.PackageTemperatureC, sensors, SensorKeys.CpuPackageTemperature)),
                    M(SensorKeys.Prefer(cpu.PackagePowerW, sensors, SensorKeys.CpuPackagePower)), cpu.Cores.Count) : null,
                _memory is { } memory ? Memory(memory) : null,
                _gpu?.Adapters.Select(a => new LiveGpu(a.Adapter.AdapterKey, a.Adapter.Name, M(a.UtilizationPercent), M(a.VramUsedBytes), M(a.VramTotalBytes), M(a.TemperatureC),
                    M(SensorKeys.Prefer(a.HotspotTemperatureC, sensors, SensorKeys.GpuHotspot(a.Adapter.Name))),
                    M(SensorKeys.Prefer(a.PowerW, sensors, SensorKeys.GpuPower(a.Adapter.Name))), M(a.CoreClockMhz))).ToList() ?? [],
                _disk?.Disks.Select(d => new LiveDisk(DiskName(d), string.Join(" ", d.Volumes), M(d.ActivePercent), M(d.ReadBytesPerSec), M(d.WriteBytesPerSec),
                    M(SensorKeys.Prefer(d.TemperatureC, sensors, SensorKeys.DiskTemperature(d.Disk.Number))))).ToList() ?? [],
                _network?.Interfaces.OrderByDescending(n => n.IsUp).Select(n => new LiveNetwork(n.Interface.Name, n.Interface.Kind.ToString(), n.IsUp, M(n.ReceiveBytesPerSec),
                    M(n.SendBytesPerSec), M(n.LinkSpeedBitsPerSec), M(n.WifiSignalQualityPercent))).ToList() ?? [],
                _processes is { } processes ? Top(processes, _gpu) : null,
                [.. _events],
                _display is { } display ? new LiveDisplay(display.IsRemoteSession, display.Displays.Select(d => $"{DisplayDiff.Name(d)}: {DisplayDiff.Mode(d)}").ToList()) : null,
                new LiveAgent(
                    _diagnostics is { } diag ? M(diag.AgentCpuPercent) : new LiveMetric(null, "Pending"),
                    _diagnostics is { } d2 ? new LiveMetric(d2.AgentWorkingSetBytes) : new LiveMetric(null, "Pending"),
                    _diagnostics is { } d3 ? new LiveMetric(Math.Round(d3.TickDuration.TotalMilliseconds, 1)) : new LiveMetric(null, "Pending"),
                    ring.Frames,
                    Math.Round(ring.EstimatedBytes / (1024.0 * 1024.0), 1),
                    new LiveMetric(SelfMonitor.ManagedHeapBytes)));
        }
    }

    /// <summary>The newest sample of each collector (for the on-screen display).</summary>
    public Overlay.LiveSamples Latest()
    {
        lock (_gate)
        {
            return new Overlay.LiveSamples(_timestamp, _cpu, _memory, _gpu, _disk, _network, _sensors, _processes);
        }
    }

    /// <summary>The trend over the last <paramref name="span"/> (at most <see cref="TrendMinutes"/>), oldest first.</summary>
    public LiveTrend Trend(TimeSpan span)
    {
        lock (_gate)
        {
            var points = new List<TrendPoint>(_trendCount);
            for (var i = 0; i < _trendCount; i++)
            {
                points.Add(_trend[(_trendStart + i) % TrendCapacity]);
            }

            if (points.Count > 0)
            {
                var from = points[^1].Time - (long)span.TotalMilliseconds;
                points = points.Where(p => p.Time >= from).ToList();
            }

            return new LiveTrend(
                points.Select(p => p.Time).ToList(),
                points.Select(p => p.Cpu).ToList(),
                points.Select(p => p.Memory).ToList(),
                points.Select(p => p.Gpu).ToList(),
                points.Select(p => p.Vram).ToList(),
                points.Select(p => p.Disk).ToList(),
                points.Select(p => p.NetReceive).ToList(),
                points.Select(p => p.NetSend).ToList(),
                points.Select(p => p.CpuTemperature).ToList(),
                points.Select(p => p.GpuTemperature).ToList(),
                points.Select(p => p.GpuHotspot).ToList());
        }
    }

    private Dictionary<string, SensorReading>? SensorMap() =>
        _sensors?.Readings.GroupBy(r => r.Sensor.LogicalKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>One trend point from the newest sample of each collector (same rules as the snapshot).</summary>
    private void AddTrendPoint(DateTimeOffset at)
    {
        var sensors = SensorMap();
        var adapters = _gpu?.Adapters ?? [];
        var up = _network?.Interfaces.Where(n => n.IsUp).ToList() ?? [];
        double? memory = _memory is { } m && m.TotalBytes.HasValue && m.AvailableBytes.HasValue && m.TotalBytes.Value > 0
            ? 100.0 * (m.TotalBytes.Value!.Value - m.AvailableBytes.Value!.Value) / m.TotalBytes.Value.Value
            : null;
        var point = new TrendPoint(
            at.ToUnixTimeMilliseconds(),
            R(_cpu?.TotalPercent),
            R(memory),
            Max(adapters.Select(a => V(a.UtilizationPercent))),
            Max(adapters.Select(a => a.VramUsedBytes.HasValue && a.VramTotalBytes.HasValue && a.VramTotalBytes.Value > 0
                ? 100.0 * a.VramUsedBytes.Value!.Value / a.VramTotalBytes.Value!.Value
                : (double?)null)),
            Max(_disk?.Disks.Select(d => V(d.ActivePercent)) ?? []),
            Sum(up.Select(n => V(n.ReceiveBytesPerSec))),
            Sum(up.Select(n => V(n.SendBytesPerSec))),
            _cpu is { } cpu ? R(SensorKeys.Prefer(cpu.PackageTemperatureC, sensors, SensorKeys.CpuPackageTemperature)) : null,
            Max(adapters.Select(a => V(a.TemperatureC))),
            Max(adapters.Select(a => V(SensorKeys.Prefer(a.HotspotTemperatureC, sensors, SensorKeys.GpuHotspot(a.Adapter.Name))))));

        if (_trendCount < TrendCapacity)
        {
            _trend[(_trendStart + _trendCount) % TrendCapacity] = point;
            _trendCount++;
        }
        else
        {
            _trend[_trendStart] = point;
            _trendStart = (_trendStart + 1) % TrendCapacity;
        }
    }

    private static double? V(Reading<double> reading) => reading.HasValue ? reading.Value : null;

    private static double? R(Reading<double>? reading) => reading is { HasValue: true } r ? Math.Round(r.Value!.Value, 1) : null;

    private static double? R(double? value) => value is { } v ? Math.Round(v, 1) : null;

    private static double? Max(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Max(), 1);
    }

    private static double? Sum(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : Math.Round(present.Sum(), 0);
    }

    private readonly record struct TrendPoint(
        long Time, double? Cpu, double? Memory, double? Gpu, double? Vram, double? Disk, double? NetReceive, double? NetSend,
        double? CpuTemperature, double? GpuTemperature, double? GpuHotspot);

    private static LiveMetric M(Reading<double> reading) => reading.HasValue ? new LiveMetric(Math.Round(reading.Value!.Value, 2)) : new LiveMetric(null, reading.Availability.ToString());

    private static LiveMetric M(Reading<long> reading) => reading.HasValue ? new LiveMetric(reading.Value!.Value) : new LiveMetric(null, reading.Availability.ToString());

    private static LiveMemory Memory(MemorySample m)
    {
        LiveMetric used = new(null, m.TotalBytes.HasValue ? m.AvailableBytes.Availability.ToString() : m.TotalBytes.Availability.ToString());
        LiveMetric usedPercent = used;
        if (m.TotalBytes.HasValue && m.AvailableBytes.HasValue && m.TotalBytes.Value > 0)
        {
            var bytes = m.TotalBytes.Value!.Value - m.AvailableBytes.Value!.Value;
            used = new LiveMetric(bytes);
            usedPercent = new LiveMetric(Math.Round(100.0 * bytes / m.TotalBytes.Value.Value, 1));
        }

        var commit = m.CommittedBytes.HasValue && m.CommitLimitBytes.HasValue && m.CommitLimitBytes.Value > 0
            ? new LiveMetric(Math.Round(100.0 * m.CommittedBytes.Value!.Value / m.CommitLimitBytes.Value!.Value, 1))
            : new LiveMetric(null, m.CommittedBytes.Availability.ToString());
        return new LiveMemory(usedPercent, used, M(m.TotalBytes), commit, M(m.HardFaultsPerSec));
    }

    private static string DiskName(PhysicalDiskSample d) =>
        string.IsNullOrWhiteSpace(d.Disk.Model) ? $"Disk {d.Disk.Number}" : d.Disk.Model;

    private static LiveProcesses Top(ProcessSample processes, GpuSample? gpu)
    {
        var gpuByPid = new Dictionary<int, double>();
        var vramByPid = new Dictionary<int, long>();
        foreach (var usage in gpu?.Processes ?? [])
        {
            gpuByPid[usage.ProcessId] = Math.Max(gpuByPid.GetValueOrDefault(usage.ProcessId), usage.UtilizationPercent);
            if (usage.DedicatedMemoryBytes.HasValue)
            {
                vramByPid[usage.ProcessId] = vramByPid.GetValueOrDefault(usage.ProcessId) + usage.DedicatedMemoryBytes.Value!.Value;
            }
        }

        var all = processes.Processes.Where(p => p.Key.Pid != 0).Select(p => new LiveProcess(
            p.Key.Pid,
            p.Name,
            p.CpuPercent.HasValue ? Math.Round(p.CpuPercent.Value!.Value, 1) : null,
            p.WorkingSetBytes.HasValue ? p.WorkingSetBytes.Value : null,
            p.PrivateBytes.HasValue ? p.PrivateBytes.Value : null,
            gpuByPid.TryGetValue(p.Key.Pid, out var g) ? Math.Round(g, 1) : null,
            p.IoReadBytesPerSec.HasValue || p.IoWriteBytesPerSec.HasValue ? p.IoReadBytesPerSec.GetValueOrDefault(0) + p.IoWriteBytesPerSec.GetValueOrDefault(0) : null,
            p.State == ProcessState.NotResponding,
            p.TrueIo is { } io ? io.DiskReadBytesPerSec + io.DiskWriteBytesPerSec : null,
            p.TrueIo is { } net ? net.NetReceiveBytesPerSec + net.NetSendBytesPerSec : null,
            vramByPid.TryGetValue(p.Key.Pid, out var vram) && vram > 0 ? vram : null)).ToList();
        var trueIo = processes.Processes.Any(p => p.TrueIo is not null);

        return new LiveProcesses(
            all.Count,
            all.Where(p => p.CpuPercent > 0).OrderByDescending(p => p.CpuPercent).Take(TopProcesses).ToList(),
            all.OrderByDescending(p => p.PrivateBytes ?? p.WorkingSetBytes ?? 0).Take(TopProcesses).ToList(),
            all.Where(p => p.GpuPercent > 0).OrderByDescending(p => p.GpuPercent).Take(TopProcesses).ToList(),
            all.Where(p => p.IoBytesPerSec > 0).OrderByDescending(p => p.IoBytesPerSec).Take(TopProcesses).ToList(),
            trueIo ? all.Where(p => p.NetBytesPerSec > 0).OrderByDescending(p => p.NetBytesPerSec).Take(TopProcesses).ToList() : null,
            all.Where(p => p.GpuMemoryBytes > 0).OrderByDescending(p => p.GpuMemoryBytes).Take(TopProcesses).ToList());
    }

    private static IEnumerable<LiveEvent> EventsOf(SampleFrame frame)
    {
        foreach (var e in frame.Events ?? [])
        {
            yield return new LiveEvent(e.Timestamp, e.Channel.StartsWith("pcdiag.", StringComparison.Ordinal) ? "recorder" : "windows", e.Title, e.Category.ToString(), e.Severity);
        }

        foreach (var c in frame.Display?.Changes ?? [])
        {
            var severity = c.Kind is DisplayEventKind.Disconnected or DisplayEventKind.Reconfigured or DisplayEventKind.PowerStateChanged ? Severity.Medium : Severity.Low;
            var change = c.OldValue is not null && c.NewValue is not null ? $"{c.OldValue} → {c.NewValue}" : c.NewValue ?? c.Kind.ToString();
            yield return new LiveEvent(c.Timestamp, "display", $"{c.Target ?? "Display"}: {change}", EventCategory.Display.ToString(), severity);
        }

        foreach (var p in frame.Processes?.Events ?? [])
        {
            var (text, severity) = p.Kind switch
            {
                ProcessEventKind.Crashed => ("crashed", Severity.High),
                ProcessEventKind.Hung => ("stopped responding", Severity.Medium),
                ProcessEventKind.Recovered => ("is responding again", Severity.Low),
                _ => (null, Severity.Info),
            };
            if (text is not null)
            {
                yield return new LiveEvent(p.Timestamp, "process", $"{p.Name} {text}", EventCategory.Process.ToString(), severity);
            }
        }

        foreach (var s in frame.Services?.Changes ?? [])
        {
            if (s.Kind is ServiceEventKind.Failed or ServiceEventKind.Restarted or ServiceEventKind.Installed)
            {
                yield return new LiveEvent(s.Timestamp, "service", $"{s.DisplayName}: {s.Kind.ToString().ToLowerInvariant()}", EventCategory.Service.ToString(),
                    s.Kind == ServiceEventKind.Failed ? Severity.Medium : Severity.Low);
            }
        }
    }
}
