using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Storage;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>
/// Write-behind persistence of sample frames into telemetry.db. Frames are buffered and written in one transaction
/// every <see cref="StorageOptions.WriteBehindSeconds"/> (or when the batch is large), never row by row.
/// If the disk is failing, frames are kept in memory up to a bound and retried; the oldest are dropped beyond it. The
/// same happens while the storage manager has paused writing because the drive is almost full.
/// </summary>
public sealed partial class TelemetryStorageConsumer : ISampleConsumer, IAsyncDisposable
{
    /// <summary>Frames written per transaction at most (5 minutes at 1 Hz).</summary>
    public const int MaxBatchFrames = 300;

    /// <summary>Frames retained while writes are failing (30 minutes at 1 Hz).</summary>
    public const int MaxRetainedFrames = 1800;

    /// <summary>Process lists further apart than this leave a gap in which processes may have ended unseen.</summary>
    private const long ProcessListGapMs = 10_000;

    /// <summary>How often every process's figures are considered for storing (not only the busiest).</summary>
    public const long FullListIntervalMs = 30_000;

    /// <summary>A process whose figures did not change is stored again after this long, so it stays within the raw retention.</summary>
    public const long FullListRefreshMs = 600_000;

    private readonly DataPaths _paths;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IOptionsMonitor<StorageOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelemetryStorageConsumer> _logger;
    private readonly List<SampleFrame> _pending = [];
    private readonly AvailabilityTracker _availability = new();
    private readonly Dictionary<string, (long DeviceId, string DetailsJson)> _devices = new(StringComparer.Ordinal);
    private readonly HashSet<long> _devicesSeenInBatch = [];
    private readonly Dictionary<string, (long Ts, long? Free, long? Total)> _volumesWritten = new(StringComparer.OrdinalIgnoreCase);
    private SqliteConnection? _connection;
    private TelemetryCommands? _commands;
    private readonly Func<int> _topProcessCount;
    private readonly SensorDeadband _deadband = new();
    private readonly Dictionary<string, long> _sensorIds = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, SensorReading>? _frameSensors;
    private long _lastMemoryRankingTs;
    private long _lastProcessListTs;
    private long _lastFullListTs;
    private readonly Dictionary<ProcessKey, (SelectedProcess Row, long Ts)> _fullListRows = [];
    private readonly SentOnce _sentOnce = new();
    private long _lastFlush;
    private long _droppedWhileFailing;

    private readonly IStorageGuard? _guard;
    private bool _pauseLogged;

    /// <summary>Creates the consumer.</summary>
    public TelemetryStorageConsumer(
        DataPaths paths,
        SqliteConnectionFactory connectionFactory,
        IOptionsMonitor<StorageOptions> options,
        TimeProvider timeProvider,
        ILogger<TelemetryStorageConsumer> logger,
        IOptionsMonitor<SamplingOptions>? sampling = null,
        IStorageGuard? guard = null)
    {
        _guard = guard;
        _topProcessCount = () => sampling?.CurrentValue.TopProcessCount ?? new SamplingOptions().TopProcessCount;
        _paths = paths;
        _connectionFactory = connectionFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _lastFlush = timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public string Name => "storage";

    /// <inheritdoc />
    public int QueueCapacity => 600;

    /// <inheritdoc />
    public bool StoresBackfill => true;

    /// <inheritdoc />
    public async ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // What is sent once (process identities, the service list) is kept from every frame: a recording that starts

        // later writes it.

        _sentOnce.Note(frame, frame.Recorded);


        // Live-view frames (recorder idle, dashboard open) are never stored; what describes the PC (its hardware, drivers,
        // devices and displays) is, so the System page shows it either way.
        if (!frame.Recorded)
        {
            if (frame.Inventory is not null || frame.Display is not null)
            {
                await StoreDescriptionAsync(frame, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        _pending.Add(frame);

        var due = _timeProvider.GetElapsedTime(_lastFlush) >= TimeSpan.FromSeconds(_options.CurrentValue.WriteBehindSeconds);
        if (due || _pending.Count >= MaxBatchFrames)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores the PC's description from a frame that is not recorded (its inventory and display layout only, no
    /// telemetry), in a transaction of its own; skipped while the drive is almost full.
    /// </summary>
    private async Task StoreDescriptionAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        if (_guard is { WritesPaused: true })
        {
            return;
        }

        try
        {
            var commands = await GetCommandsAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                commands.Enlist(transaction);
                if (frame.Inventory is { } inventory)
                {
                    await WriteInventoryAsync(commands, UnixTime.ToUnixMs(frame.Timestamp), inventory, cancellationToken).ConfigureAwait(false);
                }

                if (frame.Display is { } display)
                {
                    await WriteDisplayAsync(commands, display, cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(_logger, ex, 1);
            await ResetConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        _lastFlush = _timeProvider.GetTimestamp();
        if (_pending.Count == 0)
        {
            return;
        }

        // The drive is almost full: keep the newest frames in memory and write them once there is space again.
        if (_guard is { WritesPaused: true })
        {
            if (!_pauseLogged)
            {
                _pauseLogged = true;
                LogPaused(_logger);
            }

            Bound();
            return;
        }

        _pauseLogged = false;
        try
        {
            var commands = await GetCommandsAsync(cancellationToken).ConfigureAwait(false);
            var transaction = (SqliteTransaction)await _connection!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                commands.Enlist(transaction);
                foreach (var frame in _pending)
                {
                    await WriteFrameAsync(commands, frame, cancellationToken).ConfigureAwait(false);
                }

                var lastTs = UnixTime.ToUnixMs(_pending[^1].Timestamp);
                foreach (var deviceId in _devicesSeenInBatch)
                {
                    await commands.DeviceSeen.ExecuteAsync(cancellationToken, deviceId, lastTs).ConfigureAwait(false);
                }

                foreach (var change in _availability.TakeChanges())
                {
                    await commands.Availability.ExecuteAsync(cancellationToken, change.Series, (int)change.Availability, change.Reason, UnixTime.ToUnixMs(change.Since)).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            LogFlushed(_logger, _pending.Count);
            _pending.Clear();
            _sentOnce.Flushed();
            _devicesSeenInBatch.Clear();
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(_logger, ex, _pending.Count);
            await ResetConnectionAsync().ConfigureAwait(false);
            _devices.Clear(); // the transaction rolled back; device ids may not exist
            _volumesWritten.Clear();
            _sentOnce.RolledBack();
            _sensorIds.Clear();
            _deadband.Reset();
            _devicesSeenInBatch.Clear();
            Bound();
        }
    }

    /// <summary>Drops the oldest frames beyond <see cref="MaxRetainedFrames"/> (while writes fail or are paused).</summary>
    private void Bound()
    {
        if (_pending.Count > MaxRetainedFrames)
        {
            var excess = _pending.Count - MaxRetainedFrames;
            _pending.RemoveRange(0, excess);
            _droppedWhileFailing += excess;
            LogDropped(_logger, _droppedWhileFailing);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await ResetConnectionAsync().ConfigureAwait(false);

    private async Task WriteFrameAsync(TelemetryCommands commands, SampleFrame frame, CancellationToken cancellationToken)
    {
        var ts = UnixTime.ToUnixMs(frame.Timestamp);
        _frameSensors = frame.Sensors?.Readings.GroupBy(r => r.Sensor.LogicalKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var cpu = frame.Cpu;
        var memory = frame.Memory;
        var diagnostics = frame.Diagnostics;

        var memUsedPercent = memory is { TotalBytes.HasValue: true, AvailableBytes.HasValue: true } && memory.TotalBytes.Value > 0
            ? (object)(100.0 * (memory.TotalBytes.Value!.Value - memory.AvailableBytes.Value!.Value) / memory.TotalBytes.Value.Value)
            : DBNull.Value;

        await commands.SystemSnapshot.ExecuteAsync(
            cancellationToken,
            ts,
            frame.Sequence,
            cpu is null ? DBNull.Value : Db(cpu.TotalPercent),
            memUsedPercent,
            HeadlineGpuPercent(frame.Gpu),
            HeadlineVramPercent(frame.Gpu),
            HeadlineDiskActivePercent(frame.Disk),
            HeadlineNetworkBitsPerSecond(frame.Network),
            diagnostics is null ? DBNull.Value : diagnostics.TickDuration.TotalMilliseconds,
            diagnostics is null ? DBNull.Value : diagnostics.CollectorsMissed,
            diagnostics is null ? DBNull.Value : Db(diagnostics.AgentCpuPercent),
            diagnostics is null ? DBNull.Value : diagnostics.AgentWorkingSetBytes).ConfigureAwait(false);

        if (cpu is not null)
        {
            await commands.Cpu.ExecuteAsync(
                cancellationToken,
                ts,
                Track("cpu.total_pct", cpu.TotalPercent, frame.Timestamp),
                Track("cpu.user_pct", cpu.UserPercent, frame.Timestamp),
                Track("cpu.kernel_pct", cpu.KernelPercent, frame.Timestamp),
                Track("cpu.freq_mhz", cpu.FrequencyMhz, frame.Timestamp),
                Track("cpu.max_freq_mhz", cpu.MaxFrequencyMhz, frame.Timestamp),
                Track("cpu.perf_pct", cpu.PerformancePercent, frame.Timestamp),
                Track("cpu.perf_limit_pct", cpu.PerformanceLimitPercent, frame.Timestamp),
                Track("cpu.perf_limit_flags", cpu.PerformanceLimitFlags, frame.Timestamp),
                Track("cpu.queue_len", cpu.ProcessorQueueLength, frame.Timestamp),
                Track("cpu.ctx_switches_ps", cpu.ContextSwitchesPerSec, frame.Timestamp),
                Track("cpu.interrupts_ps", cpu.InterruptsPerSec, frame.Timestamp),
                Track("cpu.package_temp_c", FromSensors(cpu.PackageTemperatureC, _frameSensors, SensorKeys.CpuPackageTemperature), frame.Timestamp),
                Track("cpu.package_power_w", FromSensors(cpu.PackagePowerW, _frameSensors, SensorKeys.CpuPackagePower), frame.Timestamp),
                cpu.Cores.Count == 0 ? DBNull.Value : CoreBlobs.PackUtilization(cpu.Cores),
                cpu.Cores.Count == 0 ? DBNull.Value : CoreBlobs.PackFrequency(cpu.Cores)).ConfigureAwait(false);
        }

        if (frame.Gpu is { } gpu)
        {
            foreach (var adapter in gpu.Adapters)
            {
                await WriteGpuAdapterAsync(commands, ts, adapter, frame.Timestamp, cancellationToken).ConfigureAwait(false);
            }
        }

        if (frame.Disk is { } diskSample)
        {
            await WriteDisksAsync(commands, ts, diskSample, frame.Timestamp, cancellationToken).ConfigureAwait(false);
        }

        if (frame.Network is { } network)
        {
            foreach (var nic in network.Interfaces)
            {
                await WriteNetworkInterfaceAsync(commands, ts, nic, frame.Timestamp, cancellationToken).ConfigureAwait(false);
            }
        }

        if (frame.Processes is { } processes)
        {
            await WriteProcessesAsync(commands, ts, frame, processes, cancellationToken).ConfigureAwait(false);
        }

        if (frame.Sensors is { } sensors)
        {
            await WriteSensorsAsync(commands, ts, sensors, frame.Timestamp, cancellationToken).ConfigureAwait(false);
        }

        if (frame.Inventory is { } inventory)
        {
            await WriteInventoryAsync(commands, ts, inventory, cancellationToken).ConfigureAwait(false);
        }

        if (_sentOnce.ServicesToStore(frame) is { } kept)
        {
            await WriteServicesAsync(commands, UnixTime.ToUnixMs(kept.At), new ServiceSample(kept.At, kept.List, kept.Changes), cancellationToken).ConfigureAwait(false);
        }

        if (frame.Services is { } services)
        {
            await WriteServicesAsync(commands, ts, services, cancellationToken).ConfigureAwait(false);
        }

        if (frame.Events is { Count: > 0 } events)
        {
            await WriteEventsAsync(commands, events, cancellationToken).ConfigureAwait(false);
        }

        if (frame.Display is { } display)
        {
            await WriteDisplayAsync(commands, display, cancellationToken).ConfigureAwait(false);
        }

        if (memory is not null)
        {
            await commands.Memory.ExecuteAsync(
                cancellationToken,
                ts,
                Track("memory.total_b", memory.TotalBytes, frame.Timestamp),
                Track("memory.available_b", memory.AvailableBytes, frame.Timestamp),
                Track("memory.free_b", memory.FreeBytes, frame.Timestamp),
                Track("memory.cache_b", memory.CacheBytes, frame.Timestamp),
                Track("memory.commit_b", memory.CommittedBytes, frame.Timestamp),
                Track("memory.commit_limit_b", memory.CommitLimitBytes, frame.Timestamp),
                Track("memory.paged_pool_b", memory.PagedPoolBytes, frame.Timestamp),
                Track("memory.nonpaged_pool_b", memory.NonPagedPoolBytes, frame.Timestamp),
                Track("memory.compressed_b", memory.CompressedBytes, frame.Timestamp),
                Track("memory.page_faults_ps", memory.PageFaultsPerSec, frame.Timestamp),
                Track("memory.hard_faults_ps", memory.HardFaultsPerSec, frame.Timestamp),
                Track("memory.pages_ps", memory.PagesPerSec, frame.Timestamp),
                Track("memory.pressure", memory.Pressure, frame.Timestamp)).ConfigureAwait(false);
        }
    }

    private async Task WriteGpuAdapterAsync(TelemetryCommands commands, long ts, GpuAdapterSample adapter, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var deviceId = await EnsureDeviceAsync(commands, "gpu", adapter.Adapter.AdapterKey, adapter.Adapter.Name, adapter.Adapter.Vendor, GpuDetailsJson(adapter.Adapter), ts, cancellationToken).ConfigureAwait(false);
        var p = "gpu." + adapter.Adapter.AdapterKey + ".";
        await commands.Gpu.ExecuteAsync(
            cancellationToken,
            ts,
            deviceId,
            Track(p + "util_pct", adapter.UtilizationPercent, at),
            Track(p + "util_3d", adapter.Utilization3dPercent, at),
            Track(p + "util_compute", adapter.UtilizationComputePercent, at),
            Track(p + "util_decode", adapter.UtilizationDecodePercent, at),
            Track(p + "util_encode", adapter.UtilizationEncodePercent, at),
            Track(p + "util_copy", adapter.UtilizationCopyPercent, at),
            Track(p + "vram_used_b", adapter.VramUsedBytes, at),
            Track(p + "vram_total_b", adapter.VramTotalBytes, at),
            Track(p + "shared_used_b", adapter.SharedUsedBytes, at),
            Track(p + "temp_c", adapter.TemperatureC, at),
            Track(p + "hotspot_c", FromSensors(adapter.HotspotTemperatureC, _frameSensors, SensorKeys.GpuHotspot(adapter.Adapter.Name)), at),
            Track(p + "core_mhz", adapter.CoreClockMhz, at),
            Track(p + "mem_mhz", adapter.MemoryClockMhz, at),
            Track(p + "power_w", FromSensors(adapter.PowerW, _frameSensors, SensorKeys.GpuPower(adapter.Adapter.Name)), at),
            Track(p + "power_pct", adapter.PowerPercent, at),
            Track(p + "power_limit_w", adapter.PowerLimitW, at),
            Track(p + "throttle_flags", adapter.ThrottleFlags, at),
            Track(p + "fan_rpm", adapter.FanRpm, at)).ConfigureAwait(false);
    }

    private async Task<long> EnsureDeviceAsync(TelemetryCommands commands, string kind, string key, string name, string vendor, string detailsJson, long ts, CancellationToken cancellationToken)
    {
        if (_devices.TryGetValue(key, out var known) && known.DetailsJson == detailsJson)
        {
            _devicesSeenInBatch.Add(known.DeviceId);
            return known.DeviceId;
        }

        var id = Convert.ToInt64(await commands.Device.ScalarAsync(cancellationToken, kind, key, name, vendor, detailsJson, ts).ConfigureAwait(false), CultureInfo.InvariantCulture);
        _devices[key] = (id, detailsJson);
        return id;
    }

    private static string GpuDetailsJson(GpuAdapterDescriptor adapter) => JsonSerializer.Serialize(new
    {
        vendorId = adapter.VendorId,
        deviceId = adapter.DeviceId,
        dedicatedMemoryBytes = adapter.DedicatedMemoryBytes,
        sharedMemoryBytes = adapter.SharedMemoryBytes,
        temperatureMaxC = adapter.TemperatureMaxC.Value,
        temperatureWarningC = adapter.TemperatureWarningC.Value,
        detailSources = adapter.DetailSources,
    });

    private async Task WriteDisksAsync(TelemetryCommands commands, long ts, DiskSample sample, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var diskIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var disk in sample.Disks)
        {
            var d = disk.Disk;
            var details = JsonSerializer.Serialize(new { number = d.Number, busType = d.BusType, media = d.Media.ToString(), sizeBytes = d.SizeBytes, removable = d.IsRemovable, health = d.Health, volumes = disk.Volumes });
            var deviceId = await EnsureDeviceAsync(commands, "disk", d.DiskKey, d.Model, d.BusType, details, ts, cancellationToken).ConfigureAwait(false);
            diskIds[d.DiskKey] = deviceId;

            var p = "disk." + d.DiskKey + ".";
            await commands.Disk.ExecuteAsync(
                cancellationToken,
                ts,
                deviceId,
                Track(p + "read_bps", disk.ReadBytesPerSec, at),
                Track(p + "write_bps", disk.WriteBytesPerSec, at),
                Track(p + "read_iops", disk.ReadIops, at),
                Track(p + "write_iops", disk.WriteIops, at),
                Track(p + "read_lat_ms", disk.ReadLatencyMs, at),
                Track(p + "write_lat_ms", disk.WriteLatencyMs, at),
                Track(p + "queue_len", disk.QueueLength, at),
                Track(p + "active_pct", disk.ActivePercent, at),
                Track(p + "temp_c", FromSensors(disk.TemperatureC, _frameSensors, SensorKeys.DiskTemperature(d.Number)), at)).ConfigureAwait(false);
        }

        foreach (var volume in sample.Volumes)
        {
            long? free = volume.FreeBytes.HasValue ? volume.FreeBytes.Value : null;
            long? total = volume.TotalBytes.HasValue ? volume.TotalBytes.Value : null;
            if (_volumesWritten.TryGetValue(volume.Mount, out var last)
                && ts - last.Ts < 60_000
                && last.Total == total
                && Math.Abs((free ?? 0) - (last.Free ?? 0)) < 1024 * 1024)
            {
                continue;
            }

            await commands.Volume.ExecuteAsync(
                cancellationToken,
                ts,
                volume.Mount,
                volume.DiskKey is not null && diskIds.TryGetValue(volume.DiskKey, out var id) ? id : DBNull.Value,
                total.HasValue ? total.Value : DBNull.Value,
                free.HasValue ? free.Value : DBNull.Value,
                volume.Label,
                volume.FileSystem,
                volume.DriveType).ConfigureAwait(false);
            _volumesWritten[volume.Mount] = (ts, free, total);
        }
    }

    private async Task WriteNetworkInterfaceAsync(TelemetryCommands commands, long ts, NetworkInterfaceSample nic, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var i = nic.Interface;
        var details = JsonSerializer.Serialize(new { kind = i.Kind.ToString(), description = i.Description, wifiNetworkName = i.WifiNetworkName });
        var deviceId = await EnsureDeviceAsync(commands, "network", "net-" + i.InterfaceKey, i.Name, i.Kind.ToString(), details, ts, cancellationToken).ConfigureAwait(false);
        var p = "net." + i.InterfaceKey + ".";
        await commands.Network.ExecuteAsync(
            cancellationToken,
            ts,
            deviceId,
            Track(p + "rx_bps", nic.ReceiveBytesPerSec, at),
            Track(p + "tx_bps", nic.SendBytesPerSec, at),
            Track(p + "rx_pps", nic.ReceivePacketsPerSec, at),
            Track(p + "tx_pps", nic.SendPacketsPerSec, at),
            Track(p + "rx_errors", nic.ReceiveErrors, at),
            Track(p + "tx_errors", nic.SendErrors, at),
            Track(p + "rx_discards", nic.ReceiveDiscards, at),
            Track(p + "tx_discards", nic.SendDiscards, at),
            Track(p + "link_bps", nic.LinkSpeedBitsPerSec, at),
            Track(p + "util_pct", nic.UtilizationPercent, at),
            nic.IsUp ? 1 : 0,
            Track(p + "wifi_quality", nic.WifiSignalQualityPercent, at),
            Track(p + "wifi_rssi", nic.WifiRssiDbm, at)).ConfigureAwait(false);
    }

    private async Task WriteProcessesAsync(TelemetryCommands commands, long ts, SampleFrame frame, ProcessSample processes, CancellationToken cancellationToken)
    {
        // A process that exited while nothing was recorded never got its end. The first full process list after a gap
        // (or after the Agent started) ends those that are gone at that moment, the latest they can have run, so a
        // look at an earlier moment still lists them and a later one does not. A custom recording without process
        // figures has no list and closes nothing.
        if (processes.Processes.Count > 0)
        {
            if (ts - _lastProcessListTs > ProcessListGapMs)
            {
                var running = JsonSerializer.Serialize(processes.Processes.Select(p => p.Key.ToStorageId()));
                await commands.ProcessesEndedUnseen.ExecuteAsync(cancellationToken, running, ts).ConfigureAwait(false);
            }

            _lastProcessListTs = ts;
        }

        foreach (var identity in processes.Identities)
        {
            await commands.ProcessIdentity.ExecuteAsync(
                cancellationToken,
                identity.Key.ToStorageId(),
                identity.Key.Pid,
                identity.ParentPid,
                identity.Name,
                identity.Path,
                identity.CommandLine,
                identity.User,
                UnixTime.ToUnixMs(identity.StartedAt),
                identity.Signer,
                identity.Signature is { } signature ? (int)signature : null).ConfigureAwait(false);
            _sentOnce.IdentityStored(identity.Key);
        }

        foreach (var e in processes.Events)
        {
            // Identity first, so events for processes that exited before being enriched still have a name.
            await commands.ProcessIdentity.ExecuteAsync(cancellationToken, e.Key.ToStorageId(), e.Key.Pid, DBNull.Value, e.Name, null, null, null, DBNull.Value, null, null).ConfigureAwait(false);
            await commands.ProcessEvent.ExecuteAsync(
                cancellationToken,
                UnixTime.ToUnixMs(e.Timestamp),
                e.Key.ToStorageId(),
                (int)e.Kind,
                JsonSerializer.Serialize(new { name = e.Name, pid = e.Key.Pid, detail = e.Detail })).ConfigureAwait(false);
            if (e.Kind == ProcessEventKind.Stopped)
            {
                await commands.ProcessEnded.ExecuteAsync(cancellationToken, e.Key.ToStorageId(), UnixTime.ToUnixMs(e.Timestamp)).ConfigureAwait(false);
            }
        }

        var includeMemory = ts - _lastMemoryRankingTs >= 10_000;
        if (includeMemory)
        {
            _lastMemoryRankingTs = ts;
        }

        var written = new HashSet<ProcessKey>();
        foreach (var selected in ProcessSelection.Select(processes, frame.Gpu, _topProcessCount(), includeMemory))
        {
            written.Add(selected.Metrics.Key);
            await WriteKeptIdentityAsync(commands, selected.Metrics.Key, cancellationToken).ConfigureAwait(false);
            await WriteProcessRowAsync(commands, ts, selected, cancellationToken).ConfigureAwait(false);
        }

        // Every process, not only the busiest: every FullListIntervalMs the processes whose figures changed since they
        // were last written this way, and each at least every FullListRefreshMs, so any moment of the rolling history has
        // figures for every process (at most that old). Idle processes keep the same figures and cost almost nothing.
        if (processes.Processes.Count > 0 && ts - _lastFullListTs >= FullListIntervalMs)
        {
            _lastFullListTs = ts;
            var running = new HashSet<ProcessKey>();
            foreach (var selected in ProcessSelection.SelectAll(processes, frame.Gpu))
            {
                var key = selected.Metrics.Key;
                running.Add(key);
                var unchanged = _fullListRows.TryGetValue(key, out var last) && last.Row == selected && ts - last.Ts < FullListRefreshMs;
                if (!written.Contains(key) && !unchanged)
                {
                    await WriteKeptIdentityAsync(commands, key, cancellationToken).ConfigureAwait(false);
                    await WriteProcessRowAsync(commands, ts, selected, cancellationToken).ConfigureAwait(false);
                }

                if (written.Contains(key) || !unchanged)
                {
                    _fullListRows[key] = (selected, ts);
                }
            }

            foreach (var gone in _fullListRows.Keys.Where(k => !running.Contains(k)).ToList())
            {
                _fullListRows.Remove(gone);
            }
        }
    }

    /// <summary>A process's identity, kept from a frame that was not stored, written before its first stored figures.</summary>
    private async Task WriteKeptIdentityAsync(TelemetryCommands commands, ProcessKey key, CancellationToken cancellationToken)
    {
        if (_sentOnce.IdentityToStore(key) is not { } identity)
        {
            return;
        }

        await commands.ProcessIdentity.ExecuteAsync(
            cancellationToken,
            identity.Key.ToStorageId(),
            identity.Key.Pid,
            identity.ParentPid,
            identity.Name,
            identity.Path,
            identity.CommandLine,
            identity.User,
            UnixTime.ToUnixMs(identity.StartedAt),
            identity.Signer,
            identity.Signature is { } signature ? (int)signature : null).ConfigureAwait(false);
    }

    private static async Task WriteProcessRowAsync(TelemetryCommands commands, long ts, SelectedProcess selected, CancellationToken cancellationToken)
    {
        var p = selected.Metrics;
        await commands.ProcessSnapshot.ExecuteAsync(
            cancellationToken,
            ts,
            p.Key.ToStorageId(),
            Db(p.CpuPercent),
            Db(p.CpuTimeMs),
            Db(p.WorkingSetBytes),
            Db(p.PrivateBytes),
            Db(p.CommitBytes),
            Db(p.IoReadBytesPerSec),
            Db(p.IoWriteBytesPerSec),
            selected.GpuPercent.HasValue ? selected.GpuPercent.Value : DBNull.Value,
            selected.GpuEngine,
            selected.GpuMemoryBytes.HasValue ? selected.GpuMemoryBytes.Value : DBNull.Value,
            Db(p.ThreadCount),
            Db(p.HandleCount),
            Db(p.BasePriority),
            (int)p.State,
            p.TrueIo is { } io ? io.DiskReadBytesPerSec : DBNull.Value,
            p.TrueIo is { } io2 ? io2.DiskWriteBytesPerSec : DBNull.Value,
            p.TrueIo is { } io3 ? io3.NetReceiveBytesPerSec : DBNull.Value,
            p.TrueIo is { } io4 ? io4.NetSendBytesPerSec : DBNull.Value).ConfigureAwait(false);
    }

    private static async Task WriteServicesAsync(TelemetryCommands commands, long ts, ServiceSample services, CancellationToken cancellationToken)
    {
        foreach (var service in services.Snapshot ?? [])
        {
            await commands.ServiceSnapshot.ExecuteAsync(
                cancellationToken,
                ts,
                service.Name,
                service.DisplayName,
                (int)service.Status,
                (int)service.StartType,
                service.ProcessId.HasValue ? service.ProcessId.Value : DBNull.Value,
                service.ServiceType).ConfigureAwait(false);
        }

        foreach (var change in services.Changes)
        {
            await commands.ServiceEvent.ExecuteAsync(
                cancellationToken,
                UnixTime.ToUnixMs(change.Timestamp),
                change.Name,
                (int)change.Kind,
                change.OldValue,
                change.NewValue,
                change.ProcessId.HasValue ? change.ProcessId.Value : DBNull.Value).ConfigureAwait(false);
        }
    }

    /// <summary>Stores display changes, and the display configuration when it differs from the last stored one.</summary>
    private static async Task WriteDisplayAsync(TelemetryCommands commands, DisplaySample display, CancellationToken cancellationToken)
    {
        if (display.Snapshot is { } snapshot)
        {
            var json = JsonSerializer.Serialize(new { snapshot.IsRemoteSession, snapshot.Displays });
            var hash = Sha256(json);
            if (await commands.LastDisplayHash.ScalarAsync(cancellationToken).ConfigureAwait(false) as string != hash)
            {
                await commands.DisplaySnapshot.ExecuteAsync(cancellationToken, UnixTime.ToUnixMs(snapshot.Timestamp), hash, json).ConfigureAwait(false);
            }
        }

        foreach (var change in display.Changes)
        {
            await commands.DisplayEvent.ExecuteAsync(
                cancellationToken,
                UnixTime.ToUnixMs(change.Timestamp),
                (int)change.Kind,
                change.Target,
                change.OldValue,
                change.NewValue,
                change.TargetKey).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stores classified events. A Windows event already stored (same channel and record ID, e.g. read again by
    /// the backfill after a restart) is skipped, and so are the rows derived from it. Derived rows: an application
    /// crash that identifies its process becomes a Crashed process event, and an SCM failure that names its service
    /// becomes a Failed service event referencing the Windows event.
    /// </summary>
    private static async Task WriteEventsAsync(TelemetryCommands commands, IReadOnlyList<SystemEvent> events, CancellationToken cancellationToken)
    {
        foreach (var e in events)
        {
            var ts = UnixTime.ToUnixMs(e.Timestamp);
            var procKey = e.Process?.ToStorageId();
            var id = await commands.WindowsEvent.ScalarAsync(
                cancellationToken,
                ts,
                e.Channel,
                e.RecordId,
                e.Provider,
                e.EventId,
                (int)e.Level,
                (int)e.Category,
                (int)e.Severity,
                e.Title,
                string.IsNullOrEmpty(e.Message) ? null : e.Message,
                e.ProcessId,
                e.ProcessName,
                e.RelatedDevice,
                e.RuleId,
                procKey,
                e.ServiceName,
                e.Data is { Count: > 0 } data ? JsonSerializer.Serialize(data) : null).ConfigureAwait(false);
            if (id is not long rowId)
            {
                continue; // duplicate
            }

            if (e.Category == EventCategory.ApplicationCrash && e.Process is { } process)
            {
                await commands.ProcessIdentity.ExecuteAsync(cancellationToken, procKey, process.Pid, DBNull.Value, e.ProcessName ?? "unknown", null, null, null, UnixTime.ToUnixMs(new DateTimeOffset(process.CreateTimeUtcTicks, TimeSpan.Zero)), null, null).ConfigureAwait(false);
                await commands.ProcessCrash.ExecuteAsync(
                    cancellationToken,
                    ts,
                    procKey,
                    (int)ProcessEventKind.Crashed,
                    JsonSerializer.Serialize(new { name = e.ProcessName, pid = process.Pid, detail = e.Title, windowsEvent = rowId })).ConfigureAwait(false);
            }

            if (e.Category == EventCategory.Service && !string.IsNullOrEmpty(e.ServiceName))
            {
                await commands.ServiceEventFromLog.ExecuteAsync(cancellationToken, ts, e.ServiceName, (int)ServiceEventKind.Failed, e.Title, rowId).ConfigureAwait(false);
            }
        }
    }

    private async Task WriteSensorsAsync(TelemetryCommands commands, long ts, SensorSample sensors, DateTimeOffset at, CancellationToken cancellationToken)
    {
        foreach (var reading in sensors.Readings)
        {
            _availability.Observe("sensor." + reading.Sensor.LogicalKey, reading.Value.Availability, at);
            if (!_deadband.ShouldWrite(reading, ts))
            {
                continue;
            }

            if (!_sensorIds.TryGetValue(reading.Sensor.LogicalKey, out var sensorId))
            {
                var s = reading.Sensor;
                sensorId = Convert.ToInt64(
                    await commands.SensorCatalog.ScalarAsync(cancellationToken, s.ProviderId, s.Kind.ToString(), $"{s.HardwareName} / {s.Name}", s.Unit, s.LogicalKey).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                _sensorIds[s.LogicalKey] = sensorId;
            }

            await commands.SensorReading.ExecuteAsync(cancellationToken, ts, sensorId, reading.Value.Value!.Value).ConfigureAwait(false);
        }
    }

    private static async Task WriteInventoryAsync(TelemetryCommands commands, long ts, InventorySample inventory, CancellationToken cancellationToken)
    {
        foreach (var (key, value) in inventory.System)
        {
            await commands.SystemInfo.ExecuteAsync(cancellationToken, key, value, ts).ConfigureAwait(false);
        }

        var hardwareHash = Sha256(inventory.HardwareJson);
        if (await commands.LastHardwareHash.ScalarAsync(cancellationToken).ConfigureAwait(false) as string != hardwareHash)
        {
            await commands.HardwareSnapshot.ExecuteAsync(cancellationToken, ts, hardwareHash, inventory.HardwareJson).ConfigureAwait(false);
        }

        var driversHash = Sha256(string.Join('\n', inventory.Drivers.Select(d => $"{d.DeviceClass}|{d.DeviceName}|{d.HardwareId}|{d.Version}|{d.DriverDate}|{d.InfName}")));
        if (await commands.LastHash.ScalarAsync(cancellationToken, "inventory.drivers_hash").ConfigureAwait(false) as string != driversHash)
        {
            foreach (var d in inventory.Drivers)
            {
                await commands.Driver.ExecuteAsync(cancellationToken, ts, d.DeviceClass, d.DeviceName, d.HardwareId, d.Provider, d.Version, d.DriverDate, d.InfName, d.Signer).ConfigureAwait(false);
            }

            await commands.SystemInfo.ExecuteAsync(cancellationToken, "inventory.drivers_hash", driversHash, ts).ConfigureAwait(false);
        }

        if (inventory.IoDevices is { } devices)
        {
            await WriteIoDevicesAsync(commands, ts, devices, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The device register, when it changed: each device's row is updated, and devices no longer listed are marked gone.</summary>
    private static async Task WriteIoDevicesAsync(TelemetryCommands commands, long ts, IReadOnlyList<Core.Devices.IoDevice> devices, CancellationToken cancellationToken)
    {
        var rows = devices.Select(d => (Device: d, Json: JsonSerializer.Serialize(d, IoDeviceJson))).ToList();
        var hash = Sha256(string.Join('\n', rows.Select(r => r.Json)));
        if (await commands.LastHash.ScalarAsync(cancellationToken, "inventory.devices_hash").ConfigureAwait(false) as string == hash)
        {
            return;
        }

        foreach (var (d, json) in rows)
        {
            await commands.IoDevice.ExecuteAsync(cancellationToken, d.Key, d.Name, d.Kind, d.Bus, d.Manufacturer, d.Present ? 1 : 0, d.External ? 1 : 0, d.Technical ? 1 : 0, d.ProblemCode, json, ts)
                .ConfigureAwait(false);
        }

        await commands.IoDevicesGone.ExecuteAsync(cancellationToken, ts).ConfigureAwait(false);
        await commands.SystemInfo.ExecuteAsync(cancellationToken, "inventory.devices_hash", hash, ts).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions IoDeviceJson = new(JsonSerializerDefaults.Web);

    private static string Sha256(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Prefers a collector's own reading; when it has no value, uses the sensor provider's reading for the logical key
    /// (a value, or at least the more specific reason such as DriverMissing).
    /// </summary>
    internal static Reading<double> FromSensors(Reading<double> own, IReadOnlyDictionary<string, SensorReading>? sensors, string key) =>
        SensorKeys.Prefer(own, sensors, key);

    /// <summary>Headline disk %: the busiest physical disk's active time.</summary>
    internal static object HeadlineDiskActivePercent(DiskSample? disk)
    {
        var values = disk?.Disks.Where(d => d.ActivePercent.HasValue).Select(d => d.ActivePercent.Value!.Value).ToList();
        return values is { Count: > 0 } ? values.Max() : DBNull.Value;
    }

    /// <summary>
    /// Headline network throughput in bits per second (receive + send) over physical interfaces.
    /// Virtual and VPN adapters are excluded because their traffic is also counted on the physical adapter.
    /// </summary>
    internal static object HeadlineNetworkBitsPerSecond(NetworkSample? network)
    {
        var physical = network?.Interfaces
            .Where(i => i.Interface.Kind is not (NetworkInterfaceKind.Virtual or NetworkInterfaceKind.Vpn))
            .Where(i => i.ReceiveBytesPerSec.HasValue && i.SendBytesPerSec.HasValue)
            .ToList();
        return physical is { Count: > 0 }
            ? physical.Sum(i => (i.ReceiveBytesPerSec.Value!.Value + i.SendBytesPerSec.Value!.Value) * 8)
            : DBNull.Value;
    }

    /// <summary>Headline GPU %: the busiest adapter.</summary>
    internal static object HeadlineGpuPercent(GpuSample? gpu)
    {
        var values = gpu?.Adapters.Where(a => a.UtilizationPercent.HasValue).Select(a => a.UtilizationPercent.Value!.Value).ToList();
        return values is { Count: > 0 } ? values.Max() : DBNull.Value;
    }

    /// <summary>Headline VRAM %: the adapter with the most dedicated memory (the discrete GPU on hybrid systems).</summary>
    internal static object HeadlineVramPercent(GpuSample? gpu)
    {
        var main = gpu?.Adapters
            .Where(a => a.VramTotalBytes.HasValue && a.VramUsedBytes.HasValue && a.VramTotalBytes.Value > 0)
            .MaxBy(a => a.VramTotalBytes.Value);
        return main is null ? DBNull.Value : 100.0 * main.VramUsedBytes.Value!.Value / main.VramTotalBytes.Value!.Value;
    }

    private object Track<T>(string series, Reading<T> reading, DateTimeOffset at)
        where T : struct
    {
        _availability.Observe(series, reading.Availability, at);
        return Db(reading);
    }

    private static object Db<T>(Reading<T> reading)
        where T : struct => reading.HasValue ? reading.Value!.Value : DBNull.Value;

    private async Task<TelemetryCommands> GetCommandsAsync(CancellationToken cancellationToken)
    {
        if (_commands is not null)
        {
            return _commands;
        }

        _connection = await _connectionFactory.OpenReadWriteAsync(_paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        _commands = new TelemetryCommands(_connection);
        return _commands;
    }

    private async Task ResetConnectionAsync()
    {
        _commands?.Dispose();
        _commands = null;
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Wrote {Count} telemetry frames")]
    private static partial void LogFlushed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write {Count} telemetry frames; they are kept in memory and retried")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The drive is almost full: telemetry is kept in memory (the last 30 minutes) until there is space again")]
    private static partial void LogPaused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Telemetry writes keep failing; {Dropped} frames dropped so far to bound memory use")]
    private static partial void LogDropped(ILogger logger, long dropped);
}
