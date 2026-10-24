using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Core.Storage;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>What one prune pass removed.</summary>
/// <param name="RowsByTable">Rows deleted per table (only tables that had something to delete).</param>
public sealed record PruneResult(IReadOnlyDictionary<string, long> RowsByTable)
{
    /// <summary>Total rows deleted.</summary>
    public long Total => RowsByTable.Values.Sum();
}

/// <summary>
/// Keeps telemetry.db bounded and useful over time:
/// <list type="bullet">
/// <item><b>Rollups</b>: min/avg/max per minute for headline series (system, CPU, memory, and per GPU, disk and
/// network interface) into Rollup1m, for complete minutes only.</item>
/// <item><b>Retention</b>: full-resolution telemetry is kept for <see cref="StorageOptions.TelemetryRetentionHours"/>;
/// events (Windows, service, display), rollups, sessions and older snapshots for
/// <see cref="StorageOptions.RollupRetentionDays"/>. The latest hardware, display and driver snapshots are always
/// kept. Deletes run in 15-minute time slices so each write transaction stays short next to the recorder.</item>
/// <item><b>Space</b>: freed pages are returned to the file system with incremental vacuum, in small steps so the
/// write-ahead log stays small, and the log is emptied afterwards.</item>
/// </list>
/// The storage manager also trims ahead of the retention (<see cref="ITelemetrySpace"/>) to keep within the space
/// limit and the free space to keep. Only telemetry.db is maintained; incident databases are never pruned.
/// </summary>
public sealed partial class TelemetryMaintenance : ITelemetrySpace
{
    /// <summary>Minutes are rolled up only once they ended this long ago (the recorder writes every 5 s).</summary>
    public static readonly TimeSpan RollupDelay = TimeSpan.FromSeconds(15);

    /// <summary>Most minutes rolled up in one pass (a backlog is worked off over several passes).</summary>
    public const int MaxMinutesPerPass = 120;

    /// <summary>Time slice per delete statement.</summary>
    public static readonly TimeSpan PruneSlice = TimeSpan.FromMinutes(15);

    /// <summary>Most slices deleted per table in one pass.</summary>
    public const int MaxSlicesPerPass = 200;

    /// <summary>Free pages above which space is returned to the file system.</summary>
    public const long ReclaimThresholdPages = 1024;

    /// <summary>Most pages returned per pass (100 MB with 4 KB pages).</summary>
    public const long MaxReclaimPagesPerPass = 25_600;

    /// <summary>Pages returned per statement (10 MB with 4 KB pages): each step passes through the write-ahead log, which a checkpoint then empties.</summary>
    public const long ReclaimStepPages = 2_560;

    internal const string RollupMarkerKey = "rollup.through_ts";

    /// <summary>Headline series: source table and column, series name ({key} = device/interface id), optional key column.</summary>
    internal static readonly RollupSource[] Sources =
    [
        new("SystemSnapshots", "cpu_pct", "cpu/total_pct"),
        new("SystemSnapshots", "mem_used_pct", "memory/used_pct"),
        new("SystemSnapshots", "gpu_pct", "gpu/headline_pct"),
        new("SystemSnapshots", "vram_pct", "gpu/vram_pct"),
        new("SystemSnapshots", "disk_active_pct", "disk/active_pct"),
        new("SystemSnapshots", "net_bps", "network/bps"),
        new("SystemSnapshots", "agent_cpu_pct", "agent/cpu_pct"),
        new("SystemSnapshots", "tick_ms", "agent/tick_ms"),
        new("SystemSnapshots", "agent_ws_bytes", "agent/ws_bytes"),
        new("CpuMetrics", "package_temp_c", "cpu/package_temp_c"),
        new("CpuMetrics", "package_power_w", "cpu/package_power_w"),
        new("CpuMetrics", "freq_mhz", "cpu/freq_mhz"),
        new("MemoryMetrics", "available_b", "memory/available_b"),
        new("MemoryMetrics", "commit_b", "memory/commit_b"),
        new("MemoryMetrics", "hard_faults_ps", "memory/hard_faults_ps"),
        new("GpuMetrics", "util_pct", "gpu/{key}/util_pct", "adapter_id"),
        new("GpuMetrics", "vram_used_b", "gpu/{key}/vram_used_b", "adapter_id"),
        new("GpuMetrics", "temp_c", "gpu/{key}/temp_c", "adapter_id"),
        new("GpuMetrics", "hotspot_c", "gpu/{key}/hotspot_c", "adapter_id"),
        new("GpuMetrics", "power_w", "gpu/{key}/power_w", "adapter_id"),
        new("DiskMetrics", "active_pct", "disk/{key}/active_pct", "disk_id"),
        new("DiskMetrics", "read_bps", "disk/{key}/read_bps", "disk_id"),
        new("DiskMetrics", "write_bps", "disk/{key}/write_bps", "disk_id"),
        new("DiskMetrics", "read_lat_ms", "disk/{key}/read_lat_ms", "disk_id"),
        new("DiskMetrics", "write_lat_ms", "disk/{key}/write_lat_ms", "disk_id"),
        new("NetworkMetrics", "rx_bps", "net/{key}/rx_bps", "if_id"),
        new("NetworkMetrics", "tx_bps", "net/{key}/tx_bps", "if_id"),
    ];

    /// <summary>Time-series tables and how long they are kept.</summary>
    internal static readonly PruneTarget[] Targets =
    [
        new("SystemSnapshots", "ts", Keep.Raw),
        new("CpuMetrics", "ts", Keep.Raw),
        new("MemoryMetrics", "ts", Keep.Raw),
        new("GpuMetrics", "ts", Keep.Raw),
        new("DiskMetrics", "ts", Keep.Raw),
        new("NetworkMetrics", "ts", Keep.Raw),
        new("VolumeMetrics", "ts", Keep.Raw),
        new("ProcessSnapshots", "ts", Keep.Raw),
        new("ProcessEvents", "ts", Keep.Raw),
        new("ServiceSnapshots", "ts", Keep.Raw),
        new("SensorReadings", "ts", Keep.Raw),
        new("ExtensionMetrics", "ts", Keep.Raw),
        new("WindowsEvents", "ts", Keep.Long),
        new("ServiceEvents", "ts", Keep.Long),
        new("DisplayEvents", "ts", Keep.Long),
        new("Anomalies", "start_ts", Keep.Long),
        new("Rollup1m", "ts", Keep.Long),
    ];

    private readonly DataPaths _paths;
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IOptionsMonitor<StorageOptions> _options;
    private readonly ILogger<TelemetryMaintenance> _logger;
    private readonly Dictionary<string, long> _seriesIds = new(StringComparer.Ordinal);
    private bool _vacuumUnavailableLogged;

    /// <summary>Creates the maintenance job.</summary>
    public TelemetryMaintenance(DataPaths paths, SqliteConnectionFactory connectionFactory, IOptionsMonitor<StorageOptions> options, ILogger<TelemetryMaintenance> logger)
    {
        _paths = paths;
        _connectionFactory = connectionFactory;
        _options = options;
        _logger = logger;
    }

    internal enum Keep
    {
        Raw,
        Long,
    }

    /// <summary>Rolls up the complete minutes since the last pass. Returns the number of minutes covered.</summary>
    public async Task<int> RollUpAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenReadWriteAsync(_paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);

        var end = Floor(UnixTime.ToUnixMs(now - RollupDelay));
        long from;
        if (await ScalarAsync(connection, null, "SELECT value FROM SystemInformation WHERE key = $p0;", cancellationToken, RollupMarkerKey).ConfigureAwait(false) is string marker)
        {
            from = long.Parse(marker, CultureInfo.InvariantCulture);
        }
        else if (await ScalarAsync(connection, null, "SELECT MIN(ts) FROM SystemSnapshots;", cancellationToken).ConfigureAwait(false) is long first)
        {
            from = Floor(first);
        }
        else
        {
            return 0;
        }

        // A clock set back after the marker was written: start again from what is still there.
        if (from > end + 60_000)
        {
            from = end;
        }

        var to = Math.Min(end, from + (MaxMinutesPerPass * 60_000L));
        if (to <= from)
        {
            return 0;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in Sources)
        {
            await RollUpSourceAsync(connection, transaction, source, from, to, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction,
            "INSERT INTO SystemInformation (key, value, updated_ts) VALUES ($p0, $p1, $p2) ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_ts = excluded.updated_ts;",
            cancellationToken, RollupMarkerKey, to.ToString(CultureInfo.InvariantCulture), UnixTime.ToUnixMs(now)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (int)((to - from) / 60_000);
    }

    /// <summary>Deletes data past its retention.</summary>
    public Task<PruneResult> PruneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        return PruneAsync(
            UnixTime.ToUnixMs(now - TimeSpan.FromHours(Math.Max(1, options.TelemetryRetentionHours))),
            UnixTime.ToUnixMs(now - TimeSpan.FromDays(Math.Max(1, options.RollupRetentionDays))),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<long> TrimAsync(DateTimeOffset fullDetailBefore, DateTimeOffset summariesBefore, CancellationToken cancellationToken) =>
        (await PruneAsync(UnixTime.ToUnixMs(fullDetailBefore), UnixTime.ToUnixMs(summariesBefore), cancellationToken).ConfigureAwait(false)).Total;

    /// <inheritdoc />
    public async Task<TelemetryCoverage> CoverageAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.TelemetryDatabase))
        {
            return new TelemetryCoverage(null, null, 0, 0);
        }

        await using var connection = await _connectionFactory.OpenReadWriteAsync(_paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        return await ReadCoverageAsync(connection, _paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How far back a telemetry database goes and how large it is (also used by the Web host, read-only).</summary>
    public static async Task<TelemetryCoverage> ReadCoverageAsync(SqliteConnection connection, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var fullDetail = await ScalarAsync(connection, null, "SELECT MIN(ts) FROM SystemSnapshots;", cancellationToken).ConfigureAwait(false) as long?;
        var summaries = await ScalarAsync(connection, null, "SELECT MIN(ts) FROM Rollup1m;", cancellationToken).ConfigureAwait(false) as long?;
        var pageSize = await ScalarAsync(connection, null, "PRAGMA page_size;", cancellationToken).ConfigureAwait(false) as long? ?? 4096L;
        var free = await ScalarAsync(connection, null, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false) as long? ?? 0L;
        return new TelemetryCoverage(
            fullDetail is { } f ? UnixTime.FromUnixMs(f) : null,
            summaries is { } r ? UnixTime.FromUnixMs(r) : null,
            FileBytes(path),
            free * pageSize);
    }

    /// <inheritdoc />
    public async Task<long> CompactAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_paths.TelemetryDatabase))
        {
            return 0;
        }

        var before = FileBytes(_paths.TelemetryDatabase);
        await ReclaimAsync(long.MaxValue, cancellationToken).ConfigureAwait(false);
        return Math.Max(0, before - FileBytes(_paths.TelemetryDatabase));
    }

    /// <summary>The database and its write-ahead log on disk.</summary>
    internal static long FileBytes(string database)
    {
        long total = 0;
        foreach (var file in new[] { database, database + "-wal" })
        {
            try
            {
                total += new FileInfo(file) is { Exists: true } info ? info.Length : 0;
            }
            catch (IOException)
            {
                // Gone while measuring.
            }
        }

        return total;
    }

    private async Task<PruneResult> PruneAsync(long rawCutoff, long longCutoff, CancellationToken cancellationToken)
    {
        var deleted = new Dictionary<string, long>(StringComparer.Ordinal);

        await using var connection = await _connectionFactory.OpenReadWriteAsync(_paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        foreach (var target in Targets)
        {
            var cutoff = target.Keep == Keep.Raw ? rawCutoff : longCutoff;
            var rows = await PruneTableAsync(connection, target.Table, target.Column, cutoff, cancellationToken).ConfigureAwait(false);
            Add(deleted, target.Table, rows);
        }

        // Snapshots: older ones go with the long retention, but the latest always stays (it is the current state).
        Add(deleted, "HardwareSnapshots", await ExecuteAsync(connection, null,
            "DELETE FROM HardwareSnapshots WHERE ts < $p0 AND id < (SELECT MAX(id) FROM HardwareSnapshots);", cancellationToken, longCutoff).ConfigureAwait(false));
        Add(deleted, "DisplaySnapshots", await ExecuteAsync(connection, null,
            "DELETE FROM DisplaySnapshots WHERE ts < $p0 AND id < (SELECT MAX(id) FROM DisplaySnapshots);", cancellationToken, longCutoff).ConfigureAwait(false));
        Add(deleted, "Drivers", await ExecuteAsync(connection, null,
            "DELETE FROM Drivers WHERE snapshot_ts < $p0 AND snapshot_ts < (SELECT MAX(snapshot_ts) FROM Drivers);", cancellationToken, longCutoff).ConfigureAwait(false));

        // Process identities: ended before the raw cutoff, or started before the long cutoff and never seen ending.
        Add(deleted, "ProcessIdentity", await ExecuteAsync(connection, null,
            "DELETE FROM ProcessIdentity WHERE (end_ts IS NOT NULL AND end_ts < $p0) OR start_ts < $p1;", cancellationToken, rawCutoff, longCutoff).ConfigureAwait(false));
        Add(deleted, "RecorderSessions", await ExecuteAsync(connection, null,
            "DELETE FROM RecorderSessions WHERE end_ts IS NOT NULL AND end_ts < $p0;", cancellationToken, longCutoff).ConfigureAwait(false));

        var result = new PruneResult(deleted);
        if (result.Total > 0)
        {
            LogPruned(_logger, result.Total, string.Join(", ", deleted.OrderByDescending(d => d.Value).Take(5).Select(d => $"{d.Key} {d.Value}")));
        }

        return result;
    }

    /// <summary>Returns free pages to the file system (incremental vacuum). Returns the number of pages released.</summary>
    public Task<long> ReclaimAsync(CancellationToken cancellationToken) => ReclaimAsync(MaxReclaimPagesPerPass, cancellationToken);

    /// <summary>
    /// Returns up to <paramref name="maxPages"/> free pages (all of them when compacting) in steps of
    /// <see cref="ReclaimStepPages"/>, each followed by a checkpoint, then empties the write-ahead log.
    /// </summary>
    private async Task<long> ReclaimAsync(long maxPages, CancellationToken cancellationToken)
    {
        var compacting = maxPages == long.MaxValue;
        await using var connection = await _connectionFactory.OpenReadWriteAsync(_paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        if (await ScalarAsync(connection, null, "PRAGMA auto_vacuum;", cancellationToken).ConfigureAwait(false) is not 2L)
        {
            if (!_vacuumUnavailableLogged)
            {
                _vacuumUnavailableLogged = true;
                LogVacuumUnavailable(_logger);
            }

            return 0;
        }

        var before = (long)(await ScalarAsync(connection, null, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false))!;
        if (before < (compacting ? 1 : ReclaimThresholdPages))
        {
            if (compacting)
            {
                await CheckpointAsync(connection, "TRUNCATE", cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        // One large incremental_vacuum would pass every page it moves through the write-ahead log at once (and leave
        // the log that large): steps with a checkpoint after each keep it small.
        var remaining = Math.Min(before, maxPages);
        while (remaining > 0)
        {
            var step = Math.Min(remaining, ReclaimStepPages);
            await ExecuteAsync(connection, null, $"PRAGMA incremental_vacuum({step.ToString(CultureInfo.InvariantCulture)});", cancellationToken).ConfigureAwait(false);
            await CheckpointAsync(connection, "PASSIVE", cancellationToken).ConfigureAwait(false);
            remaining -= step;
            if ((long)(await ScalarAsync(connection, null, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false))! == 0)
            {
                break;
            }
        }

        await CheckpointAsync(connection, "TRUNCATE", cancellationToken).ConfigureAwait(false);
        var after = (long)(await ScalarAsync(connection, null, "PRAGMA freelist_count;", cancellationToken).ConfigureAwait(false))!;
        LogReclaimed(_logger, before - after);
        return before - after;
    }

    /// <summary>Copies the write-ahead log into the database; TRUNCATE also empties the log file (when no reader holds it).</summary>
    private static async Task CheckpointAsync(SqliteConnection connection, string mode, CancellationToken cancellationToken)
    {
        // Returns a row (busy, log, checkpointed) rather than failing when a reader is still using the log.
        await ScalarAsync(connection, null, $"PRAGMA wal_checkpoint({mode});", cancellationToken).ConfigureAwait(false);
    }

    private static long Floor(long unixMs) => unixMs - (((unixMs % 60_000) + 60_000) % 60_000);

    private static void Add(Dictionary<string, long> totals, string table, long rows)
    {
        if (rows > 0)
        {
            totals[table] = totals.GetValueOrDefault(table) + rows;
        }
    }

    private static async Task<long> PruneTableAsync(SqliteConnection connection, string table, string column, long cutoff, CancellationToken cancellationToken)
    {
        long total = 0;
        for (var slice = 0; slice < MaxSlicesPerPass; slice++)
        {
            if (await ScalarAsync(connection, null, $"SELECT MIN({column}) FROM {table};", cancellationToken).ConfigureAwait(false) is not long oldest || oldest >= cutoff)
            {
                break;
            }

            var sliceEnd = Math.Min(cutoff, oldest + (long)PruneSlice.TotalMilliseconds);
            total += await ExecuteAsync(connection, null, $"DELETE FROM {table} WHERE {column} < $p0;", cancellationToken, sliceEnd).ConfigureAwait(false);
        }

        return total;
    }

    private async Task RollUpSourceAsync(SqliteConnection connection, SqliteTransaction transaction, RollupSource source, long from, long to, CancellationToken cancellationToken)
    {
        var key = source.KeyColumn is null ? "NULL" : source.KeyColumn;
        var groupBy = source.KeyColumn is null ? "m" : $"m, {source.KeyColumn}";
        var rows = new List<(long Minute, long? Key, double Min, double Avg, double Max)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                $"""
                SELECT (ts / 60000) * 60000 AS m, {key}, MIN({source.Column}), AVG({source.Column}), MAX({source.Column})
                FROM {source.Table}
                WHERE ts >= $from AND ts < $to AND {source.Column} IS NOT NULL
                GROUP BY {groupBy};
                """;
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4)));
            }
        }

        foreach (var (minute, deviceKey, min, avg, max) in rows)
        {
            var name = deviceKey is { } k ? source.Series.Replace("{key}", k.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) : source.Series;
            var seriesId = await SeriesIdAsync(connection, transaction, name, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO Rollup1m (ts, series_id, min, avg, max) VALUES ($p0, $p1, $p2, $p3, $p4);",
                cancellationToken, minute, seriesId, min, avg, max).ConfigureAwait(false);
        }
    }

    private async Task<long> SeriesIdAsync(SqliteConnection connection, SqliteTransaction transaction, string name, CancellationToken cancellationToken)
    {
        if (_seriesIds.TryGetValue(name, out var id))
        {
            return id;
        }

        id = (long)(await ScalarAsync(connection, transaction,
            "INSERT INTO RollupSeries (name) VALUES ($p0) ON CONFLICT (name) DO UPDATE SET name = excluded.name RETURNING series_id;",
            cancellationToken, name).ConfigureAwait(false))!;
        _seriesIds[name] = id;
        return id;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DBNull ? null : result;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, object[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), values[i]);
        }

        return command;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention removed {Rows} rows (largest: {Tables})")]
    private static partial void LogPruned(ILogger logger, long rows, string tables);

    [LoggerMessage(Level = LogLevel.Information, Message = "Returned {Pages} free pages to the file system")]
    private static partial void LogReclaimed(ILogger logger, long pages);

    [LoggerMessage(Level = LogLevel.Warning, Message = "telemetry.db was created without incremental auto-vacuum; free space is reused but the file does not shrink")]
    private static partial void LogVacuumUnavailable(ILogger logger);

    internal sealed record RollupSource(string Table, string Column, string Series, string? KeyColumn = null);

    internal sealed record PruneTarget(string Table, string Column, Keep Keep);
}

/// <summary>Runs <see cref="TelemetryMaintenance"/>: rollups every minute, retention every 10 minutes, space reclaim hourly.</summary>
public sealed partial class TelemetryMaintenanceService : BackgroundService
{
    /// <summary>Delay before the first pass, so startup stays light.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    private readonly TelemetryMaintenance _maintenance;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TelemetryMaintenanceService> _logger;

    /// <summary>Creates the service.</summary>
    public TelemetryMaintenanceService(TelemetryMaintenance maintenance, TimeProvider timeProvider, ILogger<TelemetryMaintenanceService> logger)
    {
        _maintenance = maintenance;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _timeProvider);
            var pass = 0L;
            do
            {
                await RunSafelyAsync(() => _maintenance.RollUpAsync(_timeProvider.GetUtcNow(), stoppingToken), "rollup").ConfigureAwait(false);
                if (pass % 10 == 0)
                {
                    await RunSafelyAsync(() => _maintenance.PruneAsync(_timeProvider.GetUtcNow(), stoppingToken), "retention").ConfigureAwait(false);
                }

                if (pass % 60 == 0)
                {
                    await RunSafelyAsync(() => _maintenance.ReclaimAsync(stoppingToken), "space reclaim").ConfigureAwait(false);
                }

                pass++;
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    private async Task RunSafelyAsync(Func<Task> pass, string name)
    {
        try
        {
            await pass().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
        {
            LogFailed(_logger, ex, name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry maintenance ({Pass}) failed; it is retried on the next pass")]
    private static partial void LogFailed(ILogger logger, Exception exception, string pass);
}
