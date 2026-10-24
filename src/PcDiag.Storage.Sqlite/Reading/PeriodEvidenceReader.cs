using System.Globalization;
using Microsoft.Data.Sqlite;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.History;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite.Incidents;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>
/// Gathers everything recorded in a period of the rolling history for the period diagnostic: every History series
/// (raw within the retention, one-minute summaries beyond it), the recording sessions, events, anomalies, incidents and
/// unclean stops, and per-process, per-program, per-service, per-sensor and per-volume figures from the raw data.
/// Read-only; a day of data takes a few seconds.
/// </summary>
public sealed class PeriodEvidenceReader(DataPaths paths, SqliteConnectionFactory connectionFactory, HistoryReader history, AnomalyReader anomalies, IncidentIndexReader incidents)
{
    /// <summary>Longest period (the one-minute summaries are kept 30 days by default).</summary>
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(31);

    /// <summary>Buckets per series: about 1 minute for a day, so short episodes are still seen.</summary>
    public const int Points = 1500;

    /// <summary>Most events read.</summary>
    public const int MaxEvents = 2000;

    /// <summary>Loads the period [<paramref name="from"/>, <paramref name="to"/>].</summary>
    public async Task<PeriodEvidence> LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var database = paths.TelemetryDatabase;
        var (f, t) = (UnixTime.ToUnixMs(from), UnixTime.ToUnixMs(to));
        if (!File.Exists(database))
        {
            return new PeriodEvidence(from, to, null, [], [], [], [], [], [], [], [], [], [], [], [], [], 0);
        }

        // Every series, in batches the History reader accepts.
        var catalog = await history.CatalogAsync(database, cancellationToken).ConfigureAwait(false);
        var series = new List<PeriodSeries>();
        foreach (var batch in catalog.Chunk(HistoryReader.MaxSeries))
        {
            var result = await history.SeriesAsync(database, batch.Select(s => s.Id), f, t, Points, cancellationToken).ConfigureAwait(false);
            foreach (var data in result.Series.Where(s => s.Points.Count > 0))
            {
                var info = batch.First(s => s.Id == data.Id);
                series.Add(new PeriodSeries(info.Id, info.Label, info.Unit, info.Group, info.Device, result.BucketMs, data.Points));
            }
        }

        var events = (await history.EventsAsync(database, f, t, 1, MaxEvents, cancellationToken).ConfigureAwait(false)).Select(IncidentEvidenceReader.ToEvent).ToList();
        var found = await anomalies.ListAsync(database, f, t, 1000, cancellationToken).ConfigureAwait(false);
        var page = await incidents.ListAsync(new IncidentQuery(Limit: 200, From: f, To: t), cancellationToken).ConfigureAwait(false);
        var periodIncidents = page.Items.Select(i => new PeriodIncident(i.Key, i.CenterAt, i.Title, i.Severity, i.Source.ToString())).OrderBy(i => i.CenterAt).ToList();

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var rawStart = await ScalarAsync(connection, "SELECT MIN(ts) FROM SystemSnapshots;", cancellationToken).ConfigureAwait(false);
        var rf = Math.Max(f, rawStart ?? long.MaxValue);
        var raw = rawStart is not null && rf <= t;
        var logical = await ScalarAsync(connection, "SELECT CAST(value AS INTEGER) FROM SystemInformation WHERE key = 'cpu.0.logical_processors';", cancellationToken).ConfigureAwait(false);

        return new PeriodEvidence(
            from,
            to,
            raw ? UnixTime.FromUnixMs(rf) : null,
            series,
            await SessionsAsync(connection, f, t, cancellationToken).ConfigureAwait(false),
            events,
            found,
            periodIncidents,
            await UncleanStopsAsync(f, t, cancellationToken).ConfigureAwait(false),
            raw ? await ProcessesAsync(connection, rf, t, cancellationToken).ConfigureAwait(false) : [],
            await ProcessNamesAsync(connection, f, t, cancellationToken).ConfigureAwait(false),
            await ServicesAsync(connection, f, t, cancellationToken).ConfigureAwait(false),
            raw ? await SensorsAsync(connection, rf, t, cancellationToken).ConfigureAwait(false) : [],
            raw ? await VolumesAsync(connection, rf, t, cancellationToken).ConfigureAwait(false) : [],
            raw ? await NetworkErrorsAsync(connection, rf, t, cancellationToken).ConfigureAwait(false) : [],
            raw ? await PeaksAsync(connection, series, rf, t, cancellationToken).ConfigureAwait(false) : [],
            (int)(logical ?? 0));
    }

    private static async Task<List<RecordingSpan>> SessionsAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        var spans = new List<RecordingSpan>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await foreach (var row in RowsAsync(connection,
                           "SELECT start_ts, end_ts, selection_json FROM RecorderSessions WHERE start_ts <= $p0 AND (end_ts IS NULL OR end_ts >= $p1) ORDER BY start_ts;",
                           cancellationToken, to, from).ConfigureAwait(false))
        {
            var start = Math.Max(row.GetInt64(0), from);
            var end = Math.Min(row.IsDBNull(1) ? Math.Min(now, to) : row.GetInt64(1), to);
            if (end > start)
            {
                spans.Add(new RecordingSpan(UnixTime.FromUnixMs(start), UnixTime.FromUnixMs(end), !row.IsDBNull(2)));
            }
        }

        return spans;
    }

    private async Task<List<PeriodUncleanStop>> UncleanStopsAsync(long from, long to, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.IncidentIndexDatabase))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        var stops = new List<PeriodUncleanStop>();
        await foreach (var row in RowsAsync(connection,
                           "SELECT detected_ts, last_heartbeat_ts, kind, was_recording, summary FROM BootRecoveries WHERE last_heartbeat_ts BETWEEN $p0 AND $p1 ORDER BY detected_ts;",
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            stops.Add(new PeriodUncleanStop(UnixTime.FromUnixMs(row.GetInt64(0)), UnixTime.FromUnixMs(row.GetInt64(1)), ((UncleanStopKind)row.GetInt32(2)).ToString(),
                row.GetInt64(3) != 0, row.IsDBNull(4) ? string.Empty : row.GetString(4)));
        }

        return stops;
    }

    /// <summary>Each process instance's figures: first and last sample in the period (CPU time used, growth) and peaks.</summary>
    private static async Task<List<ProcessPeriodStats>> ProcessesAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        var list = new List<ProcessPeriodStats>();
        await foreach (var r in RowsAsync(connection,
                           """
                           WITH agg AS (
                             SELECT proc_key, COUNT(*) AS n, MIN(ts) AS first_ts, MAX(ts) AS last_ts, MAX(cpu_pct) AS max_cpu, MAX(private_b) AS max_private,
                                    MAX(gpu_pct) AS max_gpu, MAX(COALESCE(io_read_bps, 0) + COALESCE(io_write_bps, 0)) AS max_io
                             FROM ProcessSnapshots WHERE ts >= $p0 AND ts <= $p1 GROUP BY proc_key)
                           SELECT a.proc_key, i.name, i.pid, i.path, i.sig_status, i.signer, a.first_ts, a.last_ts, a.n,
                                  f.cpu_time_ms, l.cpu_time_ms, a.max_cpu, f.private_b, l.private_b, a.max_private, f.handles, l.handles, a.max_gpu, a.max_io
                           FROM agg a
                           JOIN ProcessIdentity i ON i.proc_key = a.proc_key
                           JOIN ProcessSnapshots f ON f.ts = a.first_ts AND f.proc_key = a.proc_key
                           JOIN ProcessSnapshots l ON l.ts = a.last_ts AND l.proc_key = a.proc_key;
                           """,
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            double? cpuTime = r.IsDBNull(9) || r.IsDBNull(10) ? null : Math.Max(0, r.GetDouble(10) - r.GetDouble(9));
            list.Add(new ProcessPeriodStats(
                r.GetInt64(0), r.GetString(1), r.GetInt32(2), Text(r, 3),
                r.IsDBNull(4) ? null : ((SignatureStatus)r.GetInt32(4)).ToString(), Text(r, 5),
                UnixTime.FromUnixMs(r.GetInt64(6)), UnixTime.FromUnixMs(r.GetInt64(7)), r.GetInt32(8),
                cpuTime, Number(r, 11), Integer(r, 12), Integer(r, 13), Integer(r, 14),
                r.IsDBNull(15) ? null : r.GetInt32(15), r.IsDBNull(16) ? null : r.GetInt32(16), Number(r, 17), Number(r, 18)));
        }

        return list;
    }

    private static async Task<List<ProcessNameStats>> ProcessNamesAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in RowsAsync(connection,
                           "SELECT i.name, e.kind, COUNT(*) FROM ProcessEvents e JOIN ProcessIdentity i ON i.proc_key = e.proc_key WHERE e.ts BETWEEN $p0 AND $p1 GROUP BY i.name, e.kind;",
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            var kind = r.GetInt32(1);
            if (kind is < 0 or > 4)
            {
                continue;
            }

            if (!counts.TryGetValue(r.GetString(0), out var c))
            {
                counts[r.GetString(0)] = c = new int[5];
            }

            c[kind] += r.GetInt32(2);
        }

        return counts.Select(c => new ProcessNameStats(c.Key, c.Value[(int)ProcessEventKind.Started], c.Value[(int)ProcessEventKind.Stopped], c.Value[(int)ProcessEventKind.Hung],
            c.Value[(int)ProcessEventKind.Recovered])).ToList();
    }

    private static async Task<List<ServicePeriodStats>> ServicesAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in RowsAsync(connection, "SELECT name, display_name FROM ServiceSnapshots WHERE ts = (SELECT MAX(ts) FROM ServiceSnapshots WHERE ts <= $p0);",
                           cancellationToken, to).ConfigureAwait(false))
        {
            display[r.GetString(0)] = Text(r, 1) ?? r.GetString(0);
        }

        var counts = new Dictionary<string, (int[] Kinds, long? LastFailure)>(StringComparer.OrdinalIgnoreCase);
        await foreach (var r in RowsAsync(connection,
                           "SELECT name, kind, COUNT(*), MAX(ts) FROM ServiceEvents WHERE ts BETWEEN $p0 AND $p1 GROUP BY name, kind;", cancellationToken, from, to).ConfigureAwait(false))
        {
            var kind = r.GetInt32(1);
            if (kind is < 0 or > 6)
            {
                continue;
            }

            if (!counts.TryGetValue(r.GetString(0), out var c))
            {
                c = (new int[7], null);
            }

            c.Kinds[kind] += r.GetInt32(2);
            if (kind == (int)ServiceEventKind.Failed)
            {
                c.LastFailure = r.GetInt64(3);
            }

            counts[r.GetString(0)] = c;
        }

        return counts.Select(c => new ServicePeriodStats(c.Key, display.GetValueOrDefault(c.Key, c.Key), c.Value.Kinds[(int)ServiceEventKind.Started],
            c.Value.Kinds[(int)ServiceEventKind.Stopped], c.Value.Kinds[(int)ServiceEventKind.Restarted], c.Value.Kinds[(int)ServiceEventKind.Failed],
            c.Value.Kinds[(int)ServiceEventKind.StartTypeChanged], c.Value.LastFailure is { } at ? UnixTime.FromUnixMs(at) : null)).ToList();
    }

    private static async Task<List<SensorPeriodStats>> SensorsAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        // The moment of each maximum (one aggregate, so SQLite takes ts from that row), then the ranges.
        var maxAt = new Dictionary<long, long>();
        await foreach (var r in RowsAsync(connection, "SELECT sensor_id, MAX(value), ts FROM SensorReadings WHERE ts BETWEEN $p0 AND $p1 GROUP BY sensor_id;",
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            maxAt[r.GetInt64(0)] = r.GetInt64(2);
        }

        var list = new List<SensorPeriodStats>();
        await foreach (var r in RowsAsync(connection,
                           """
                           SELECT c.sensor_id, c.name, c.kind, c.unit, MIN(r.value), AVG(r.value), MAX(r.value)
                           FROM SensorReadings r JOIN SensorCatalog c ON c.sensor_id = r.sensor_id
                           WHERE r.ts BETWEEN $p0 AND $p1 AND r.value IS NOT NULL GROUP BY c.sensor_id;
                           """,
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            var kind = r.IsDBNull(2) ? "Other" : r.GetFieldType(2) == typeof(string) ? r.GetString(2) : ((SensorKind)r.GetInt32(2)).ToString();
            list.Add(new SensorPeriodStats(r.GetString(1), kind, Text(r, 3) ?? string.Empty, r.GetDouble(4), r.GetDouble(5), r.GetDouble(6),
                UnixTime.FromUnixMs(maxAt.GetValueOrDefault(r.GetInt64(0), from))));
        }

        return list;
    }

    private static async Task<List<VolumePeriodStats>> VolumesAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        var list = new List<VolumePeriodStats>();
        await foreach (var r in RowsAsync(connection,
                           "SELECT mount, MIN(free_b), ts, label, total_b FROM VolumeMetrics WHERE ts BETWEEN $p0 AND $p1 AND free_b IS NOT NULL GROUP BY mount;",
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            list.Add(new VolumePeriodStats(r.GetString(0), Text(r, 3), Integer(r, 4), Integer(r, 1), UnixTime.FromUnixMs(r.GetInt64(2))));
        }

        return list;
    }

    private static async Task<List<NetworkErrorStats>> NetworkErrorsAsync(SqliteConnection connection, long from, long to, CancellationToken cancellationToken)
    {
        // Each row holds the errors since the previous sample; the bare ts is the row with the most.
        var list = new List<NetworkErrorStats>();
        await foreach (var r in RowsAsync(connection,
                           """
                           SELECT if_id, SUM(COALESCE(rx_errors, 0) + COALESCE(tx_errors, 0)) total, MAX(COALESCE(rx_errors, 0) + COALESCE(tx_errors, 0)), ts
                           FROM NetworkMetrics WHERE ts BETWEEN $p0 AND $p1 GROUP BY if_id HAVING total > 0;
                           """,
                           cancellationToken, from, to).ConfigureAwait(false))
        {
            list.Add(new NetworkErrorStats($"net.{r.GetInt64(0).ToString(CultureInfo.InvariantCulture)}.errors", r.GetInt64(1), r.GetInt64(2), UnixTime.FromUnixMs(r.GetInt64(3))));
        }

        return list;
    }

    /// <summary>Most processes listed at a peak.</summary>
    public const int PeakProcesses = 5;

    /// <summary>
    /// The moments CPU, GPU and disk use were highest and available memory lowest, with the processes using the most
    /// then: CPU, GPU and I/O from the samples of that moment (the busiest processes are stored every second), memory
    /// from each process's latest figures (every process is stored at least every 10 minutes). For CPU, GPU and disk
    /// the moment is the busiest sample within the bucket with the highest average, so a sustained load counts rather
    /// than a one-second spike; for memory it is the lowest sample.
    /// </summary>
    private static async Task<List<PeakMoment>> PeaksAsync(SqliteConnection connection, IReadOnlyList<PeriodSeries> series, long from, long to, CancellationToken cancellationToken)
    {
        (string Id, string Sql, string Rank, long Window)[] peaks =
        [
            ("cpu", "SELECT ts, cpu_pct FROM SystemSnapshots WHERE ts BETWEEN $p0 AND $p1 AND cpu_pct IS NOT NULL ORDER BY cpu_pct DESC, ts LIMIT 1", "cpu_pct", HistoryReader.ProcessSampleWindow),
            ("memory.available", "SELECT ts, available_b FROM MemoryMetrics WHERE ts BETWEEN $p0 AND $p1 AND available_b IS NOT NULL ORDER BY available_b, ts LIMIT 1", "private_b", HistoryReader.LatestFiguresWindow),
            ("gpu", "SELECT ts, gpu_pct FROM SystemSnapshots WHERE ts BETWEEN $p0 AND $p1 AND gpu_pct > 0 ORDER BY gpu_pct DESC, ts LIMIT 1", "gpu_pct", HistoryReader.ProcessSampleWindow),
            ("disk", "SELECT ts, disk_active_pct FROM SystemSnapshots WHERE ts BETWEEN $p0 AND $p1 AND disk_active_pct > 0 ORDER BY disk_active_pct DESC, ts LIMIT 1", "io", HistoryReader.ProcessSampleWindow),
        ];
        var list = new List<PeakMoment>();
        foreach (var (id, sql, rank, window) in peaks)
        {
            var source = series.FirstOrDefault(x => x.Id == id);
            var buckets = source?.Points.Where(b => b[0] + source.BucketMs > from).ToList();
            if (source is null || buckets is not { Count: > 0 })
            {
                continue;
            }

            // Memory: the lowest sample itself (memory does not spike); CPU, GPU, disk: the busiest bucket on average.
            var bucket = id == "memory.available" ? buckets.MinBy(b => b[1])! : buckets.MaxBy(b => b[2])!;
            (long Ts, double Value)? peak = null;
            await foreach (var r in RowsAsync(connection, sql, cancellationToken, Math.Max(from, (long)bucket[0]), Math.Min(to, (long)bucket[0] + source.BucketMs)).ConfigureAwait(false))
            {
                peak = (r.GetInt64(0), r.GetDouble(1));
            }

            if (peak is not { } p)
            {
                continue;
            }

            // A process that had ended by then is left out (its latest row can be older than the moment).
            var rows = await HistoryReader.ProcessRowsAsync(connection, "(i.end_ts IS NULL OR i.end_ts >= $at) AND i.pid <> 0", cancellationToken, ("$from", p.Ts - window), ("$at", p.Ts))
                .ConfigureAwait(false);
            var top = rows
                .Select(x => (x.Name, x.Pid, Value: rank switch
                {
                    "cpu_pct" => x.CpuPercent ?? 0,
                    "gpu_pct" => x.GpuPercent ?? 0,
                    "private_b" => x.PrivateBytes ?? 0,
                    _ => (x.IoReadBytesPerSec ?? 0) + (x.IoWriteBytesPerSec ?? 0),
                }))
                .Where(x => x.Value > 0)
                .OrderByDescending(x => x.Value)
                .Take(PeakProcesses)
                .Select(x => new PeakProcess(x.Name, x.Pid, Math.Round(x.Value, 2)))
                .ToList();
            list.Add(new PeakMoment(id, UnixTime.FromUnixMs(p.Ts), Math.Round(p.Value, 2), top));
        }

        return list;
    }

    private static async IAsyncEnumerable<SqliteDataReader> RowsAsync(
        SqliteConnection connection, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, params object[] values)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), values[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader;
        }
    }

    private static async Task<long?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string? Text(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);

    private static double? Number(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : Math.Round(row.GetDouble(index), 2);

    private static long? Integer(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetInt64(index);
}
