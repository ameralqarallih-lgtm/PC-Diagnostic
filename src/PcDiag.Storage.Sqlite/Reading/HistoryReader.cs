using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PcDiag.Core.History;
using PcDiag.Storage.Sqlite.Incidents;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>
/// Read-only chart queries over a telemetry database (the live telemetry.db or an incident's incident.db, which
/// has the same tables). Series are returned in buckets of min/average/max so the browser never receives more points
/// than it can draw: from raw samples within the retention, from one-minute summaries beyond it.
/// </summary>
public sealed partial class HistoryReader(SqliteConnectionFactory connectionFactory)
{
    /// <summary>Most buckets per series.</summary>
    public const int MaxPoints = 2000;

    /// <summary>Most series per request.</summary>
    public const int MaxSeries = 12;

    /// <summary>Most events per request.</summary>
    public const int MaxEvents = 2000;

    private sealed record Definition(string Suffix, string Label, string Unit, string Group, string Table, string Expression, string? Rollup);

    private static readonly Definition[] SystemSeries =
    [
        new("cpu", "CPU", "%", "System", "SystemSnapshots", "cpu_pct", "cpu/total_pct"),
        new("memory", "Memory in use", "%", "System", "SystemSnapshots", "mem_used_pct", "memory/used_pct"),
        new("gpu", "GPU (busiest)", "%", "System", "SystemSnapshots", "gpu_pct", "gpu/headline_pct"),
        new("vram", "VRAM (fullest GPU)", "%", "System", "SystemSnapshots", "vram_pct", "gpu/vram_pct"),
        new("disk", "Disk active (busiest)", "%", "System", "SystemSnapshots", "disk_active_pct", "disk/active_pct"),
        new("net", "Network (all adapters)", "B/s", "System", "SystemSnapshots", "net_bps", "network/bps"),
        new("cpu.freq", "CPU frequency", "MHz", "CPU", "CpuMetrics", "freq_mhz", "cpu/freq_mhz"),
        new("cpu.temp", "CPU package temperature", "°C", "CPU", "CpuMetrics", "package_temp_c", "cpu/package_temp_c"),
        new("cpu.power", "CPU package power", "W", "CPU", "CpuMetrics", "package_power_w", "cpu/package_power_w"),
        new("cpu.kernel", "CPU kernel time", "%", "CPU", "CpuMetrics", "kernel_pct", null),
        new("cpu.limit", "CPU performance limit", "%", "CPU", "CpuMetrics", "perf_limit_pct", null),
        new("cpu.queue", "Processor queue length", "", "CPU", "CpuMetrics", "queue_len", null),
        new("memory.commit", "Memory commit", "%", "Memory", "MemoryMetrics", "100.0 * commit_b / NULLIF(commit_limit_b, 0)", null),
        new("memory.available", "Memory available", "B", "Memory", "MemoryMetrics", "available_b", "memory/available_b"),
        new("memory.hardfaults", "Hard page faults", "/s", "Memory", "MemoryMetrics", "hard_faults_ps", "memory/hard_faults_ps"),
        new("memory.compressed", "Compressed memory", "B", "Memory", "MemoryMetrics", "compressed_b", null),
        new("agent.cpu", "Recorder CPU", "%", "Recorder", "SystemSnapshots", "agent_cpu_pct", "agent/cpu_pct"),
        new("agent.tick", "Recorder sampling time", "ms", "Recorder", "SystemSnapshots", "tick_ms", "agent/tick_ms"),
        new("agent.ws", "Recorder working set", "B", "Recorder", "SystemSnapshots", "agent_ws_bytes", "agent/ws_bytes"),
    ];

    private static readonly Definition[] GpuSeries =
    [
        new("util", "utilization", "%", "GPU", "GpuMetrics", "util_pct", "util_pct"),
        new("3d", "3D engine", "%", "GPU", "GpuMetrics", "util_3d", null),
        new("decode", "video decode", "%", "GPU", "GpuMetrics", "util_decode", null),
        new("vram", "VRAM used", "B", "GPU", "GpuMetrics", "vram_used_b", "vram_used_b"),
        new("temp", "temperature", "°C", "GPU", "GpuMetrics", "temp_c", "temp_c"),
        new("hotspot", "hotspot", "°C", "GPU", "GpuMetrics", "hotspot_c", "hotspot_c"),
        new("core", "core clock", "MHz", "GPU", "GpuMetrics", "core_mhz", null),
        new("mem", "memory clock", "MHz", "GPU", "GpuMetrics", "mem_mhz", null),
        new("power", "power", "W", "GPU", "GpuMetrics", "power_w", "power_w"),
        new("fan", "fan", "RPM", "GPU", "GpuMetrics", "fan_rpm", null),
    ];

    private static readonly Definition[] DiskSeries =
    [
        new("active", "active time", "%", "Disk", "DiskMetrics", "active_pct", "active_pct"),
        new("read", "read", "B/s", "Disk", "DiskMetrics", "read_bps", "read_bps"),
        new("write", "write", "B/s", "Disk", "DiskMetrics", "write_bps", "write_bps"),
        new("readlat", "read latency", "ms", "Disk", "DiskMetrics", "read_lat_ms", "read_lat_ms"),
        new("writelat", "write latency", "ms", "Disk", "DiskMetrics", "write_lat_ms", "write_lat_ms"),
        new("queue", "queue length", "", "Disk", "DiskMetrics", "queue_len", null),
        new("temp", "temperature", "°C", "Disk", "DiskMetrics", "temp_c", null),
    ];

    private static readonly Definition[] NetworkSeries =
    [
        new("rx", "download", "B/s", "Network", "NetworkMetrics", "rx_bps", "rx_bps"),
        new("tx", "upload", "B/s", "Network", "NetworkMetrics", "tx_bps", "tx_bps"),
        new("errors", "errors", "", "Network", "NetworkMetrics", "COALESCE(rx_errors, 0) + COALESCE(tx_errors, 0)", null),
        new("wifi", "Wi-Fi signal", "%", "Network", "NetworkMetrics", "wifi_quality", null),
    ];

    private static readonly Dictionary<string, (Definition[] Series, string KeyColumn, string RollupPrefix)> DeviceKinds = new(StringComparer.Ordinal)
    {
        ["gpu"] = (GpuSeries, "adapter_id", "gpu"),
        ["disk"] = (DiskSeries, "disk_id", "disk"),
        ["network"] = (NetworkSeries, "if_id", "net"),
    };

    private static readonly Dictionary<string, string> IdPrefix = new(StringComparer.Ordinal) { ["gpu"] = "gpu", ["disk"] = "disk", ["network"] = "net" };

    /// <summary>The series a database can chart (device series only for devices it has seen).</summary>
    public Task<IReadOnlyList<SeriesInfo>> CatalogAsync(string database, CancellationToken cancellationToken) => CatalogAsync(database, false, cancellationToken);

    /// <summary>
    /// The series a database can chart; with <paramref name="sensors"/> also every hardware sensor it has readings of
    /// ("sensor.{id}": fans, voltages, clocks, every temperature), which only charts and widgets ask for.
    /// </summary>
    public async Task<IReadOnlyList<SeriesInfo>> CatalogAsync(string database, bool sensors, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var result = SystemSeries.Select(d => new SeriesInfo(d.Suffix, d.Label, d.Unit, d.Group, null, d.Rollup is not null)).ToList();
        foreach (var (id, kind, name, key, lastSeen) in await DevicesAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            var (series, _, _) = DeviceKinds[kind];
            var device = name ?? $"{kind} {id}";
            result.AddRange(series.Select(d => new SeriesInfo($"{IdPrefix[kind]}.{id}.{d.Suffix}", $"{device} {d.Label}", d.Unit, d.Group, device, d.Rollup is not null, key, lastSeen)));
        }

        if (sensors)
        {
            result.AddRange(await SensorsAsync(connection, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    /// <summary>The hardware sensors of a database (at most <see cref="SystemReader.MaxSensors"/>).</summary>
    private static async Task<List<SeriesInfo>> SensorsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sensors = new List<SeriesInfo>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sensor_id, kind, name, unit, logical_key FROM SensorCatalog ORDER BY kind, name LIMIT $max;";
        command.Parameters.AddWithValue("$max", SystemReader.MaxSensors);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(2);
            var slash = name.IndexOf(" / ", StringComparison.Ordinal);
            var unit = reader.GetString(3) is "C" or "\u00b0C" ? "\u00b0C" : reader.GetString(3);
            sensors.Add(new SeriesInfo(
                $"sensor.{reader.GetInt64(0).ToString(CultureInfo.InvariantCulture)}",
                name,
                unit,
                $"{reader.GetString(1)} sensors",
                slash > 0 ? name[..slash] : null,
                false,
                reader.GetString(4)));
        }

        return sensors;
    }

    /// <summary>Series over a range in at most <paramref name="points"/> buckets; unknown ids are skipped.</summary>
    /// <param name="database">The database file.</param>
    /// <param name="ids">Series ids (see <see cref="CatalogAsync(string, bool, CancellationToken)"/>).</param>
    /// <param name="from">Range start, Unix ms.</param>
    /// <param name="to">Range end, Unix ms.</param>
    /// <param name="points">Buckets wanted.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    public async Task<HistoryResult> SeriesAsync(string database, IEnumerable<string> ids, long from, long to, int points, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (to <= from)
        {
            to = from + 1000;
        }

        points = Math.Clamp(points, 10, MaxPoints);
        var bucket = Math.Max(1000, (long)Math.Ceiling((to - from) / (double)points));
        if (!File.Exists(database))
        {
            return new HistoryResult(from, to, bucket, []);
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var devices = (await DevicesAsync(connection, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.Id, d => d.Kind);
        var sensorStart = ids.Any(i => i.StartsWith("sensor.", StringComparison.Ordinal))
            ? await ScalarLongAsync(connection, "SELECT MIN(ts) FROM SensorReadings;", cancellationToken).ConfigureAwait(false) ?? long.MaxValue
            : long.MaxValue;
        var rawStart = await ScalarLongAsync(connection, "SELECT MIN(ts) FROM SystemSnapshots;", cancellationToken).ConfigureAwait(false) ?? long.MaxValue;
        var hasRollups = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'Rollup1m';", cancellationToken).ConfigureAwait(false) > 0;

        var series = new List<SeriesData>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal).Take(MaxSeries))
        {
            if (Resolve(id, devices) is not { } resolved)
            {
                continue;
            }

            var buckets = new List<double[]>();
            if (hasRollups && resolved.Rollup is not null && from < rawStart)
            {
                buckets.AddRange(await RollupAsync(connection, resolved.Rollup, from, Math.Min(to, rawStart), Math.Max(bucket, 60_000), cancellationToken).ConfigureAwait(false));
            }

            // Hardware sensors are kept as raw readings only (their own table, with its own start).
            var start = resolved.Table == "SensorReadings" ? sensorStart : rawStart;
            if (to >= start)
            {
                buckets.AddRange(await RawAsync(connection, resolved, Math.Max(from, start), to, bucket, cancellationToken).ConfigureAwait(false));
            }

            series.Add(new SeriesData(id, buckets));
        }

        return new HistoryResult(from, to, bucket, series);
    }

    /// <summary>
    /// The recorder's own cost over a range (PROJECT §20: the monitoring application itself must not become the
    /// problem): CPU as a share of the machine (average, 95th percentile, maximum), working set and sampling time.
    /// </summary>
    public async Task<RecorderOverhead> OverheadAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return new RecorderOverhead(from, to, 0, null, null, null, null, null, null, null, 0);
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var cpu = new List<double>();
        double wsSum = 0, wsMax = 0, tickSum = 0, tickMax = 0;
        int wsCount = 0, tickCount = 0, missed = 0, samples = 0;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT agent_cpu_pct, agent_ws_bytes, tick_ms, collectors_missed FROM SystemSnapshots WHERE ts >= $from AND ts <= $to;";
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            samples++;
            if (!reader.IsDBNull(0))
            {
                cpu.Add(reader.GetDouble(0));
            }

            if (!reader.IsDBNull(1))
            {
                var ws = reader.GetDouble(1);
                wsSum += ws;
                wsMax = Math.Max(wsMax, ws);
                wsCount++;
            }

            if (!reader.IsDBNull(2))
            {
                var tick = reader.GetDouble(2);
                tickSum += tick;
                tickMax = Math.Max(tickMax, tick);
                tickCount++;
            }

            if (!reader.IsDBNull(3) && reader.GetInt64(3) > 0)
            {
                missed++;
            }
        }

        cpu.Sort();
        return new RecorderOverhead(
            from,
            to,
            samples,
            cpu.Count > 0 ? Math.Round(cpu.Average(), 3) : null,
            cpu.Count > 0 ? Math.Round(cpu[(int)Math.Clamp(Math.Ceiling(cpu.Count * 0.95) - 1, 0, cpu.Count - 1)], 3) : null,
            cpu.Count > 0 ? Math.Round(cpu[^1], 3) : null,
            wsCount > 0 ? Math.Round(wsSum / wsCount) : null,
            wsCount > 0 ? wsMax : null,
            tickCount > 0 ? Math.Round(tickSum / tickCount, 1) : null,
            tickCount > 0 ? Math.Round(tickMax, 1) : null,
            missed);
    }

    /// <summary>Events between two moments (Windows events, process, service, display and recording changes), oldest first.</summary>
    public async Task<IReadOnlyList<TimelineEntry>> EventsAsync(string database, long from, long to, int minImportance, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var union = string.Join("\nUNION ALL\n", IncidentStore.TimelineSelects.Select(s => s.Trim().TrimEnd(';')));
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT ts, kind, source_id, title, detail_json, importance FROM (
            {union}
            ) WHERE importance >= $min AND ts IS NOT NULL ORDER BY ts DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$p0", from);
        command.Parameters.AddWithValue("$p1", to);
        command.Parameters.AddWithValue("$min", minImportance);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxEvents));
        var entries = new List<TimelineEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new TimelineEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(3),
                reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                ParseJson(reader.IsDBNull(4) ? null : reader.GetString(4)),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }

        entries.Reverse();
        return entries;
    }

    internal static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Resolved(string Table, string Expression, string? KeyColumn, long? Key, string? Rollup);

    private static Resolved? Resolve(string id, Dictionary<long, string> devices)
    {
        if (SystemSeries.FirstOrDefault(d => d.Suffix == id) is { } system)
        {
            return new Resolved(system.Table, system.Expression, null, null, system.Rollup);
        }

        if (SensorSeriesId().Match(id) is { Success: true } sensor
            && long.TryParse(sensor.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var sensorId))
        {
            return new Resolved("SensorReadings", "value", "sensor_id", sensorId, null);
        }

        var match = DeviceSeriesId().Match(id);
        if (!match.Success || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var deviceId)
            || !devices.TryGetValue(deviceId, out var kind) || IdPrefix[kind] != match.Groups[1].Value)
        {
            return null;
        }

        var (series, keyColumn, rollupPrefix) = DeviceKinds[kind];
        return series.FirstOrDefault(d => d.Suffix == match.Groups[3].Value) is { } definition
            ? new Resolved(definition.Table, definition.Expression, keyColumn, deviceId,
                definition.Rollup is null ? null : $"{rollupPrefix}/{deviceId.ToString(CultureInfo.InvariantCulture)}/{definition.Rollup}")
            : null;
    }

    private static async Task<List<double[]>> RawAsync(SqliteConnection connection, Resolved series, long from, long to, long bucket, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        // Table and expression come from the fixed definitions above, never from the request.
        var filter = series.KeyColumn is null ? string.Empty : $" AND {series.KeyColumn} = $key";
        command.CommandText =
            $"""
            SELECT MIN(ts), MIN(v), AVG(v), MAX(v) FROM (
              SELECT ts, ({series.Expression}) AS v FROM {series.Table} WHERE ts >= $from AND ts <= $to{filter}
            ) WHERE v IS NOT NULL GROUP BY (ts - $from) / $bucket ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$bucket", bucket);
        if (series.Key is { } key)
        {
            command.Parameters.AddWithValue("$key", key);
        }

        return await ReadBucketsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<double[]>> RollupAsync(SqliteConnection connection, string name, long from, long to, long bucket, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MIN(r.ts), MIN(r.min), AVG(r.avg), MAX(r.max)
            FROM Rollup1m r JOIN RollupSeries s ON s.series_id = r.series_id
            WHERE s.name = $name AND r.ts >= $from AND r.ts < $to
            GROUP BY (r.ts - $from) / $bucket ORDER BY 1;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$bucket", bucket);
        return await ReadBucketsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<double[]>> ReadBucketsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var buckets = new List<double[]>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(2))
            {
                continue;
            }

            buckets.Add([reader.GetInt64(0), Round(reader.GetDouble(1)), Round(reader.GetDouble(2)), Round(reader.GetDouble(3))]);
        }

        return buckets;
    }

    private static double Round(double value) => Math.Abs(value) >= 1000 ? Math.Round(value) : Math.Round(value, 2);

    private static async Task<List<(long Id, string Kind, string? Name, string Key, long LastSeen)>> DevicesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT device_id, kind, name, stable_key, last_seen FROM Devices WHERE kind IN ('gpu', 'disk', 'network') ORDER BY kind, name, device_id;";
        var devices = new List<(long, string, string?, string, long)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            devices.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        }

        return devices;
    }

    private static async Task<long?> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex("^(gpu|disk|net)\\.([0-9]{1,12})\\.([a-z0-9]{1,12})$")]
    private static partial Regex DeviceSeriesId();

    [GeneratedRegex("^sensor\\.([0-9]{1,12})$")]
    private static partial Regex SensorSeriesId();
}
