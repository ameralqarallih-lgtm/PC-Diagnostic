using System.Globalization;
using Microsoft.Data.Sqlite;
using PcDiag.Core.Quality;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>
/// Reads the quality history (game sessions, the minutes' frame rates, pings and driver time), the recorded seconds of a
/// game's frames, driver latency measurements, and programs' activity, from a telemetry database (the live
/// telemetry.db or an incident's incident.db).
/// </summary>
public sealed class QualityReader(SqliteConnectionFactory connectionFactory, HistoryReader history)
{
    /// <summary>
    /// The metric tracks the analyzers use (as an incident's evidence has them, about one value a second) for a period of
    /// a database: the frame drops are explained with them.
    /// </summary>
    public async Task<List<Core.Diagnostics.MetricTrack>> TracksAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        var catalog = (await history.CatalogAsync(database, cancellationToken).ConfigureAwait(false))
            .Select(info => (Info: info, Metric: IncidentEvidenceReader.MetricOf(info.Id)))
            .Where(x => x.Metric is not null)
            .ToList();
        var points = (int)Math.Clamp((to - from) / 1000, 10, HistoryReader.MaxPoints);
        var tracks = new List<Core.Diagnostics.MetricTrack>();
        foreach (var chunk in catalog.Chunk(HistoryReader.MaxSeries))
        {
            var result = await history.SeriesAsync(database, chunk.Select(c => c.Info.Id), from, to, points, cancellationToken).ConfigureAwait(false);
            foreach (var series in result.Series.Where(s => s.Points.Count > 0))
            {
                var (info, metric) = chunk.First(c => c.Info.Id == series.Id);
                tracks.Add(new Core.Diagnostics.MetricTrack(info.Id, metric!, info.Label, info.Unit, info.Device,
                    series.Points.Select(p => new Core.Diagnostics.MetricPoint(UnixTime.FromUnixMs((long)p[0]), p[2])).ToList()));
            }
        }

        return tracks;
    }

    /// <summary>Most rows of a list.</summary>
    public const int MaxRows = 5000;

    /// <summary>Game sessions that overlap a period, newest first.</summary>
    public async Task<List<GameSession>> SessionsAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        var sessions = new List<GameSession>();
        if (!File.Exists(database))
        {
            return sessions;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM GameSessions WHERE end_ts >= $from AND start_ts <= $to ORDER BY start_ts DESC LIMIT {MaxRows};";
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    /// <summary>One game session, or null.</summary>
    public async Task<GameSession?> SessionAsync(string database, long id, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SessionColumns} FROM GameSessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSession(reader) : null;
    }

    /// <summary>The quality history's minutes in a period, oldest first.</summary>
    public async Task<List<QualityMinute>> MinutesAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        var minutes = new List<QualityMinute>();
        if (!File.Exists(database))
        {
            return minutes;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "QualityMinutes", cancellationToken).ConfigureAwait(false))
        {
            return minutes;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, seconds, game, game_seconds, fps_avg, fps_min, low1_fps, stutters, worst_ms, drop_seconds, dpc_core_max, isr_core_max, dpc_avg,
                   router_ms, router_max, router_loss, provider_ms, provider_max, provider_loss, internet_ms, internet_max, internet_jitter, internet_loss,
                   dns_ms, dns_checks, dns_failed, wifi_min, net_bps, displayed_fps, dropped, display_latency_ms, gpu_busy_pct, cpu_busy_pct, present_mode
            FROM QualityMinutes WHERE ts >= $from AND ts <= $to ORDER BY ts LIMIT 50000;
            """;
        command.Parameters.AddWithValue("$from", from - (from % 60_000));
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            minutes.Add(new QualityMinute(
                UnixTime.FromUnixMs(reader.GetInt64(0)),
                reader.GetInt32(1),
                Text(reader, 2),
                Int(reader, 3),
                Real(reader, 4),
                Real(reader, 5),
                Real(reader, 6),
                Int(reader, 7),
                Real(reader, 8),
                Int(reader, 9),
                Real(reader, 10),
                Real(reader, 11),
                Real(reader, 12),
                new HopMinute(Real(reader, 13), Real(reader, 14), Real(reader, 15)),
                new HopMinute(Real(reader, 16), Real(reader, 17), Real(reader, 18)),
                new HopMinute(Real(reader, 19), Real(reader, 20), Real(reader, 22)),
                Real(reader, 21),
                Real(reader, 23),
                Int(reader, 24),
                Int(reader, 25),
                Real(reader, 26),
                Real(reader, 27),
                Real(reader, 28),
                reader.IsDBNull(29) ? null : reader.GetInt32(29),
                Real(reader, 30),
                Real(reader, 31),
                Real(reader, 32),
                Text(reader, 33)));
        }

        return minutes;
    }

    /// <summary>The recorded seconds of the game's frames in a period (only while recording), oldest first.</summary>
    public async Task<List<FrameSecond>> FrameSecondsAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        var seconds = new List<FrameSecond>();
        if (!File.Exists(database))
        {
            return seconds;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "FrameMetrics", cancellationToken).ConfigureAwait(false))
        {
            return seconds;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, fps, worst_ms, stutters, normal_fps, displayed_fps, dropped, worst_displayed_ms, gpu_busy_pct, cpu_busy_pct, present_mode
            FROM FrameMetrics WHERE ts >= $from AND ts <= $to ORDER BY ts LIMIT 200000;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            seconds.Add(new FrameSecond(UnixTime.FromUnixMs(reader.GetInt64(0)), reader.GetDouble(1), Real(reader, 2), Int(reader, 3), Real(reader, 4), Real(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6), Real(reader, 7), Real(reader, 8), Real(reader, 9), Text(reader, 10)));
        }

        return seconds;
    }

    /// <summary>Driver latency measurements that ended in a period, oldest first, their drivers slowest first.</summary>
    public async Task<List<DriverLatencyMeasurement>> DriverLatencyAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        var measurements = new List<DriverLatencyMeasurement>();
        if (!File.Exists(database))
        {
            return measurements;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "DriverLatency", cancellationToken).ConfigureAwait(false))
        {
            return measurements;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ts, from_ts, seconds, reason, driver, dpcs, dpc_max_us, dpc_total_us, isrs, isr_max_us, isr_total_us, over_500us, over_1ms, problem
            FROM DriverLatency WHERE ts >= $from AND ts <= $to ORDER BY ts, driver LIMIT 20000;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<(long Ts, long From, double Seconds, string Reason, string? Problem, DriverLatency? Driver)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var driver = reader.GetString(4);
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetDouble(2), reader.GetString(3), Text(reader, 13),
                driver.Length == 0 ? null : new DriverLatency(driver, Long(reader, 5), Real(reader, 6) ?? 0, Real(reader, 7) ?? 0, Long(reader, 8), Real(reader, 9) ?? 0,
                    Real(reader, 10) ?? 0, Long(reader, 11), Long(reader, 12))));
        }

        foreach (var group in rows.GroupBy(r => r.Ts))
        {
            var first = group.First();
            measurements.Add(new DriverLatencyMeasurement(UnixTime.FromUnixMs(first.Ts), UnixTime.FromUnixMs(first.From), first.Seconds, first.Reason, first.Problem,
                group.Where(r => r.Driver is not null).Select(r => r.Driver!).OrderByDescending(d => d.LongestUs).ToList()));
        }

        return measurements;
    }

    /// <summary>
    /// Programs' average activity before a moment and during a span (from the stored process figures: the busiest
    /// processes each process tick, so a program not stored in a tick counts as idle in it), the busiest during first.
    /// </summary>
    public async Task<List<ProcessActivity>> ProcessActivityAsync(string database, long beforeFrom, long duringFrom, long duringTo, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        return await ProcessActivityAsync(connection, beforeFrom, duringFrom, duringTo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><see cref="ProcessActivityAsync(string, long, long, long, CancellationToken)"/> on an open connection.</summary>
    internal static async Task<List<ProcessActivity>> ProcessActivityAsync(SqliteConnection connection, long beforeFrom, long duringFrom, long duringTo, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ticks AS (
              SELECT SUM(CASE WHEN ts < $during THEN 1 ELSE 0 END) AS before_ticks, SUM(CASE WHEN ts >= $during THEN 1 ELSE 0 END) AS during_ticks
              FROM (SELECT DISTINCT ts FROM ProcessSnapshots WHERE ts >= $from AND ts <= $to)
            )
            SELECT i.name,
              SUM(CASE WHEN s.ts < $during THEN COALESCE(s.cpu_pct, 0) END) / MAX(ticks.before_ticks, 1),
              SUM(CASE WHEN s.ts >= $during THEN COALESCE(s.cpu_pct, 0) END) / MAX(ticks.during_ticks, 1),
              SUM(CASE WHEN s.ts < $during THEN COALESCE(s.gpu_pct, 0) END) / MAX(ticks.before_ticks, 1),
              SUM(CASE WHEN s.ts >= $during THEN COALESCE(s.gpu_pct, 0) END) / MAX(ticks.during_ticks, 1),
              SUM(CASE WHEN s.ts < $during THEN COALESCE(s.disk_read_bps, s.io_read_bps, 0) + COALESCE(s.disk_write_bps, s.io_write_bps, 0) END) / MAX(ticks.before_ticks, 1),
              SUM(CASE WHEN s.ts >= $during THEN COALESCE(s.disk_read_bps, s.io_read_bps, 0) + COALESCE(s.disk_write_bps, s.io_write_bps, 0) END) / MAX(ticks.during_ticks, 1),
              SUM(CASE WHEN s.ts < $during THEN COALESCE(s.net_rx_bps, 0) + COALESCE(s.net_tx_bps, 0) END) / MAX(ticks.before_ticks, 1),
              SUM(CASE WHEN s.ts >= $during THEN COALESCE(s.net_rx_bps, 0) + COALESCE(s.net_tx_bps, 0) END) / MAX(ticks.during_ticks, 1)
            FROM ProcessSnapshots s JOIN ProcessIdentity i ON i.proc_key = s.proc_key, ticks
            WHERE s.ts >= $from AND s.ts <= $to
            GROUP BY i.name
            ORDER BY 3 DESC
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$from", beforeFrom);
        command.Parameters.AddWithValue("$during", duringFrom);
        command.Parameters.AddWithValue("$to", duringTo);
        var result = new List<ProcessActivity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            result.Add(new ProcessActivity(reader.GetString(0), Real(reader, 1) ?? 0, Real(reader, 2) ?? 0, Real(reader, 3) ?? 0, Real(reader, 4) ?? 0, Real(reader, 5) ?? 0,
                Real(reader, 6) ?? 0, Real(reader, 7) ?? 0, Real(reader, 8) ?? 0));
        }

        return result;
    }

    /// <summary>The programs that used the network most in a period (from the stored process figures), busiest first.</summary>
    public async Task<List<NetworkProgram>> NetworkProgramsAsync(string database, long from, long to, int limit, CancellationToken cancellationToken)
    {
        var programs = new List<NetworkProgram>();
        if (!File.Exists(database))
        {
            return programs;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH ticks AS (SELECT MAX(COUNT(DISTINCT ts), 1) AS n FROM ProcessSnapshots WHERE ts >= $from AND ts <= $to)
            SELECT i.name,
              SUM(COALESCE(s.net_rx_bps, 0)) / ticks.n AS rx, SUM(COALESCE(s.net_tx_bps, 0)) / ticks.n AS tx,
              MAX(COALESCE(s.net_rx_bps, 0) + COALESCE(s.net_tx_bps, 0)), SUM(COALESCE(s.net_retrans_ps, 0)) / ticks.n
            FROM ProcessSnapshots s JOIN ProcessIdentity i ON i.proc_key = s.proc_key, ticks
            WHERE s.ts >= $from AND s.ts <= $to AND (s.net_rx_bps > 0 OR s.net_tx_bps > 0 OR s.net_retrans_ps > 0)
            GROUP BY i.name
            ORDER BY rx + tx DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                programs.Add(new NetworkProgram(reader.GetString(0), Real(reader, 1) ?? 0, Real(reader, 2) ?? 0, Real(reader, 3) ?? 0, Math.Round(Real(reader, 4) ?? 0, 2)));
            }
        }

        return programs;
    }

    private const string SessionColumns = "id, game, start_ts, end_ts, seconds, fps_avg, fps_p1, low1_fps, stutters, drop_seconds, worst_ms, "
        + "displayed_fps, dropped, display_latency_ms, gpu_busy_pct, cpu_busy_pct, present_mode, windowed, recognized";

    private static GameSession ReadSession(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), UnixTime.FromUnixMs(reader.GetInt64(2)), UnixTime.FromUnixMs(reader.GetInt64(3)), reader.GetInt32(4),
        Real(reader, 5), Real(reader, 6), Real(reader, 7), Int(reader, 8), Int(reader, 9), Real(reader, 10), Real(reader, 11), reader.IsDBNull(12) ? null : reader.GetInt32(12),
        Real(reader, 13), Real(reader, 14), Real(reader, 15), Text(reader, 16), Real(reader, 17), Text(reader, 18));

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
    }

    private static double? Real(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetDouble(i);

    private static int Int(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? 0 : reader.GetInt32(i);

    private static long Long(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? 0 : reader.GetInt64(i);

    private static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
}
