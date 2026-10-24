using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>Reusable parameterized insert statements for telemetry tables (one set per connection).</summary>
internal sealed class TelemetryCommands : IDisposable
{
    public TelemetryCommands(SqliteConnection connection)
    {
        SystemSnapshot = new InsertCommand(connection, "SystemSnapshots",
            "ts", "seq", "cpu_pct", "mem_used_pct", "gpu_pct", "vram_pct", "disk_active_pct", "net_bps",
            "tick_ms", "collectors_missed", "agent_cpu_pct", "agent_ws_bytes");
        Cpu = new InsertCommand(connection, "CpuMetrics",
            "ts", "total_pct", "user_pct", "kernel_pct", "freq_mhz", "max_freq_mhz", "perf_pct", "perf_limit_pct",
            "perf_limit_flags", "queue_len", "ctx_switches_ps", "interrupts_ps", "package_temp_c", "package_power_w",
            "per_core_util", "per_core_mhz");
        Memory = new InsertCommand(connection, "MemoryMetrics",
            "ts", "total_b", "available_b", "free_b", "cache_b", "commit_b", "commit_limit_b", "paged_pool_b",
            "nonpaged_pool_b", "compressed_b", "page_faults_ps", "hard_faults_ps", "pages_ps", "pressure");
        Gpu = new InsertCommand(connection, "GpuMetrics",
            "ts", "adapter_id", "util_pct", "util_3d", "util_compute", "util_decode", "util_encode", "util_copy",
            "vram_used_b", "vram_total_b", "shared_used_b", "temp_c", "hotspot_c", "core_mhz", "mem_mhz",
            "power_w", "power_pct", "power_limit_w", "throttle_flags", "fan_rpm");
        Disk = new InsertCommand(connection, "DiskMetrics",
            "ts", "disk_id", "read_bps", "write_bps", "read_iops", "write_iops", "read_lat_ms", "write_lat_ms",
            "queue_len", "active_pct", "temp_c");
        Network = new InsertCommand(connection, "NetworkMetrics",
            "ts", "if_id", "rx_bps", "tx_bps", "rx_pps", "tx_pps", "rx_errors", "tx_errors", "rx_discards", "tx_discards",
            "link_bps", "util_pct", "oper_status", "wifi_quality", "wifi_rssi");
        Volume = new InsertCommand(connection, "VolumeMetrics",
            "ts", "mount", "disk_id", "total_b", "free_b", "label", "file_system", "drive_type");
        ProcessSnapshot = new InsertCommand(connection, "ProcessSnapshots",
            "ts", "proc_key", "cpu_pct", "cpu_time_ms", "ws_b", "private_b", "commit_b", "io_read_bps", "io_write_bps",
            "gpu_pct", "gpu_engine", "gpu_mem_b", "threads", "handles", "priority", "state", "disk_read_bps", "disk_write_bps", "net_rx_bps", "net_tx_bps");
        ProcessIdentity = new InsertCommand(connection,
            """
            INSERT INTO ProcessIdentity (proc_key, pid, ppid, name, path, cmdline, user, start_ts, signer, sig_status) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9)
            ON CONFLICT (proc_key) DO UPDATE SET
              path = COALESCE(excluded.path, path), cmdline = COALESCE(excluded.cmdline, cmdline), user = COALESCE(excluded.user, user),
              signer = COALESCE(excluded.signer, signer), sig_status = COALESCE(excluded.sig_status, sig_status);
            """,
            parameterCount: 10);
        ProcessEnded = new InsertCommand(connection, "UPDATE ProcessIdentity SET end_ts = $p1 WHERE proc_key = $p0;", parameterCount: 2);
        ProcessesEndedUnseen = new InsertCommand(connection,
            "UPDATE ProcessIdentity SET end_ts = $p1 WHERE end_ts IS NULL AND (start_ts IS NULL OR start_ts < $p1) AND proc_key NOT IN (SELECT value FROM json_each($p0));",
            parameterCount: 2);
        ProcessEvent = new InsertCommand(connection, "INSERT INTO ProcessEvents (ts, proc_key, kind, detail_json) VALUES ($p0, $p1, $p2, $p3);", parameterCount: 4);
        WindowsEvent = new InsertCommand(connection,
            """
            INSERT INTO WindowsEvents (ts, channel, record_id, provider, event_id, level, category, severity, title, message,
                                       pid, process_name, related_device, rule_id, proc_key, service_name, data_json)
            VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p11, $p12, $p13, $p14, $p15, $p16)
            ON CONFLICT (channel, record_id) DO NOTHING
            RETURNING id;
            """,
            parameterCount: 17);

        // One Crashed entry per process: a crash is often reported twice (Application Error 1000 and .NET 1026).
        ProcessCrash = new InsertCommand(connection,
            """
            INSERT INTO ProcessEvents (ts, proc_key, kind, detail_json)
            SELECT $p0, $p1, $p2, $p3
            WHERE NOT EXISTS (SELECT 1 FROM ProcessEvents WHERE proc_key = $p1 AND kind = $p2);
            """,
            parameterCount: 4);
        ServiceEventFromLog = new InsertCommand(connection,
            "INSERT INTO ServiceEvents (ts, name, kind, old_value, new_value, event_ref) VALUES ($p0, $p1, $p2, NULL, $p3, $p4);", parameterCount: 5);
        DisplaySnapshot = new InsertCommand(connection, "INSERT INTO DisplaySnapshots (ts, hash, json) VALUES ($p0, $p1, $p2);", parameterCount: 3);
        LastDisplayHash = new InsertCommand(connection, "SELECT hash FROM DisplaySnapshots ORDER BY ts DESC, id DESC LIMIT 1;", parameterCount: 0);
        DisplayEvent = new InsertCommand(connection,
            "INSERT INTO DisplayEvents (ts, kind, target, old_value, new_value, target_key) VALUES ($p0, $p1, $p2, $p3, $p4, $p5);", parameterCount: 6);
        ServiceSnapshot = new InsertCommand(connection, "ServiceSnapshots",
            "ts", "name", "display_name", "status", "start_type", "pid", "svc_type");
        ServiceEvent = new InsertCommand(connection,
            "INSERT INTO ServiceEvents (ts, name, kind, old_value, new_value, pid) VALUES ($p0, $p1, $p2, $p3, $p4, $p5);", parameterCount: 6);
        SensorCatalog = new InsertCommand(connection,
            """
            INSERT INTO SensorCatalog (provider, device_id, kind, name, unit, logical_key) VALUES ($p0, NULL, $p1, $p2, $p3, $p4)
            ON CONFLICT (logical_key) DO UPDATE SET provider = excluded.provider, name = excluded.name, unit = excluded.unit
            RETURNING sensor_id;
            """,
            parameterCount: 5);
        SensorReading = new InsertCommand(connection, "SensorReadings", "ts", "sensor_id", "value");
        SystemInfo = new InsertCommand(connection,
            """
            INSERT INTO SystemInformation (key, value, updated_ts) VALUES ($p0, $p1, $p2)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_ts = excluded.updated_ts;
            """,
            parameterCount: 3);
        HardwareSnapshot = new InsertCommand(connection, "INSERT INTO HardwareSnapshots (ts, hash, json) VALUES ($p0, $p1, $p2);", parameterCount: 3);
        Driver = new InsertCommand(connection,
            "INSERT INTO Drivers (snapshot_ts, device_class, device_name, hardware_id, provider, version, driver_date, inf, signer) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8);",
            parameterCount: 9);
        LastHash = new InsertCommand(connection, "SELECT value FROM SystemInformation WHERE key = $p0;", parameterCount: 1);
        IoDevice = new InsertCommand(connection,
            """
            INSERT INTO IoDevices (device_key, name, kind, bus, manufacturer, present, external, technical, problem_code, details_json, first_seen, last_seen, updated)
            VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p10, $p10)
            ON CONFLICT (device_key) DO UPDATE SET name = excluded.name, kind = excluded.kind, bus = excluded.bus, manufacturer = excluded.manufacturer,
              last_seen = CASE WHEN excluded.present = 1 OR IoDevices.present = 1 THEN excluded.updated ELSE IoDevices.last_seen END,
              present = excluded.present, external = excluded.external, technical = excluded.technical, problem_code = excluded.problem_code,
              details_json = excluded.details_json, updated = excluded.updated;
            """,
            parameterCount: 11);
        IoDevicesGone = new InsertCommand(connection, "UPDATE IoDevices SET last_seen = CASE WHEN present = 1 THEN $p0 ELSE last_seen END, present = 0 WHERE updated < $p0;", parameterCount: 1);
        LastHardwareHash = new InsertCommand(connection, "SELECT hash FROM HardwareSnapshots ORDER BY ts DESC LIMIT 1;", parameterCount: 0);
        Device = new InsertCommand(connection,
            """
            INSERT INTO Devices (kind, stable_key, name, vendor, details_json, first_seen, last_seen) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p5)
            ON CONFLICT (stable_key) DO UPDATE SET name = excluded.name, vendor = excluded.vendor, details_json = excluded.details_json, last_seen = excluded.last_seen
            RETURNING device_id;
            """,
            parameterCount: 6);
        DeviceSeen = new InsertCommand(connection, "UPDATE Devices SET last_seen = $p1 WHERE device_id = $p0;", parameterCount: 2);
        Availability = new InsertCommand(connection,
            """
            INSERT INTO SensorAvailability (series, availability, reason, since_ts) VALUES ($p0, $p1, $p2, $p3)
            ON CONFLICT (series) DO UPDATE SET availability = excluded.availability, reason = excluded.reason, since_ts = excluded.since_ts;
            """,
            parameterCount: 4);
    }

    public InsertCommand SystemSnapshot { get; }

    public InsertCommand Cpu { get; }

    public InsertCommand Memory { get; }

    public InsertCommand Gpu { get; }

    public InsertCommand Disk { get; }

    public InsertCommand Network { get; }

    public InsertCommand Volume { get; }

    public InsertCommand ProcessSnapshot { get; }

    public InsertCommand ProcessIdentity { get; }

    public InsertCommand ProcessEnded { get; }

    /// <summary>Ends every open process identity not in the running list ($p0, a JSON array of keys) at $p1.</summary>
    public InsertCommand ProcessesEndedUnseen { get; }

    public InsertCommand ProcessEvent { get; }

    public InsertCommand DisplaySnapshot { get; }

    public InsertCommand LastDisplayHash { get; }

    public InsertCommand DisplayEvent { get; }

    public InsertCommand WindowsEvent { get; }

    public InsertCommand ProcessCrash { get; }

    public InsertCommand ServiceEventFromLog { get; }

    public InsertCommand ServiceSnapshot { get; }

    public InsertCommand ServiceEvent { get; }

    public InsertCommand SensorCatalog { get; }

    public InsertCommand SensorReading { get; }

    public InsertCommand SystemInfo { get; }

    public InsertCommand HardwareSnapshot { get; }

    /// <summary>Registers or updates one I/O device.</summary>
    public InsertCommand IoDevice { get; }

    /// <summary>Marks the devices a register no longer lists as not connected.</summary>
    public InsertCommand IoDevicesGone { get; }

    public InsertCommand Driver { get; }

    public InsertCommand LastHash { get; }

    public InsertCommand LastHardwareHash { get; }

    public InsertCommand Device { get; }

    public InsertCommand DeviceSeen { get; }

    public InsertCommand Availability { get; }

    private IEnumerable<InsertCommand> All => [SystemSnapshot, Cpu, Memory, Gpu, Disk, Network, Volume, ProcessSnapshot, ProcessIdentity, ProcessEnded, ProcessesEndedUnseen, ProcessEvent, DisplaySnapshot, LastDisplayHash, DisplayEvent, WindowsEvent, ProcessCrash, ServiceEventFromLog, ServiceSnapshot, ServiceEvent, SensorCatalog, SensorReading, SystemInfo, HardwareSnapshot, IoDevice, IoDevicesGone, Driver, LastHash, LastHardwareHash, Device, DeviceSeen, Availability];

    public void Enlist(SqliteTransaction transaction)
    {
        foreach (var command in All)
        {
            command.Command.Transaction = transaction;
        }
    }

    public void Dispose()
    {
        foreach (var command in All)
        {
            command.Dispose();
        }
    }
}

/// <summary>A reusable command with positional parameters $p0..$pN.</summary>
internal sealed class InsertCommand : IDisposable
{
    private readonly SqliteParameter[] _parameters;

    /// <summary>INSERT OR REPLACE into <paramref name="table"/> with the given columns.</summary>
    public InsertCommand(SqliteConnection connection, string table, params string[] columns)
        : this(connection, BuildInsert(table, columns), columns.Length)
    {
    }

    /// <summary>Arbitrary SQL using $p0..$p(N-1).</summary>
    public InsertCommand(SqliteConnection connection, string sql, int parameterCount)
    {
        Command = connection.CreateCommand();
        Command.CommandText = sql;
        _parameters = new SqliteParameter[parameterCount];
        for (var i = 0; i < parameterCount; i++)
        {
            _parameters[i] = Command.Parameters.Add(new SqliteParameter("$p" + i.ToString(CultureInfo.InvariantCulture), null));
        }
    }

    public SqliteCommand Command { get; }

    public Task<int> ExecuteAsync(CancellationToken cancellationToken, params object?[] values)
    {
        Bind(values);
        return Command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<object?> ScalarAsync(CancellationToken cancellationToken, params object?[] values)
    {
        Bind(values);
        return await Command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => Command.Dispose();

    private void Bind(object?[] values)
    {
        if (values.Length != _parameters.Length)
        {
            throw new ArgumentException($"Expected {_parameters.Length} values but got {values.Length}.", nameof(values));
        }

        for (var i = 0; i < values.Length; i++)
        {
            _parameters[i].Value = values[i] ?? DBNull.Value;
        }
    }

    private static string BuildInsert(string table, string[] columns) =>
        $"INSERT OR REPLACE INTO {table} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)))});";
}
