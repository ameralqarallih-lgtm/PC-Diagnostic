using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>
/// Streams an incident's data as CSV for exports: every metric at its recorded resolution, every process sample,
/// services, and the timeline with the Windows events' details. Rows are written as they are read, so a large
/// incident never sits in memory.
/// </summary>
public sealed class ExportReader(SqliteConnectionFactory connectionFactory, HistoryReader history)
{
    /// <summary>
    /// metrics.csv: time_utc, series, label, unit, device, min, avg, max (one row per series and second; for long
    /// windows the buckets are a little wider, and min/max keep the extremes).
    /// </summary>
    public async Task WriteMetricsAsync(string database, long from, long to, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        await Csv.HeaderAsync(output, "time_utc", "series", "label", "unit", "device", "min", "avg", "max").ConfigureAwait(false);
        var catalog = await history.CatalogAsync(database, cancellationToken).ConfigureAwait(false);
        var points = (int)Math.Clamp((to - from) / 1000, 10, HistoryReader.MaxPoints);
        foreach (var chunk in catalog.Chunk(HistoryReader.MaxSeries))
        {
            var result = await history.SeriesAsync(database, chunk.Select(c => c.Id), from, to, points, cancellationToken).ConfigureAwait(false);
            foreach (var series in result.Series)
            {
                var info = chunk.First(c => c.Id == series.Id);
                foreach (var p in series.Points)
                {
                    await Csv.RowAsync(output, Csv.Time((long)p[0]), info.Id, info.Label, info.Unit, info.Device, p[1], p[2], p[3]).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>processes.csv: every process sample in the incident (all processes from the in-memory buffer, the busiest from storage).</summary>
    public async Task WriteProcessesAsync(string database, TextWriter output, CancellationToken cancellationToken)
    {
        // Incidents recorded before true per-process I/O existed have no such columns (and are only read here).
        var trueIo = File.Exists(database) && await HasColumnAsync(database, "ProcessSnapshots", "disk_read_bps", cancellationToken).ConfigureAwait(false)
            ? "p.disk_read_bps, p.disk_write_bps, p.net_rx_bps, p.net_tx_bps"
            : "NULL, NULL, NULL, NULL";
        await QueryToCsvAsync(database, output,
            ["time_utc", "pid", "name", "path", "cpu_percent", "working_set_bytes", "private_bytes", "commit_bytes", "io_read_bytes_per_sec",
             "io_write_bytes_per_sec", "gpu_percent", "gpu_engine", "gpu_memory_bytes", "threads", "handles", "priority", "state",
             "disk_read_bytes_per_sec", "disk_write_bytes_per_sec", "net_receive_bytes_per_sec", "net_send_bytes_per_sec", "signer", "signature"],
            $$"""
            SELECT p.ts, i.pid, i.name, i.path, p.cpu_pct, p.ws_b, p.private_b, p.commit_b, p.io_read_bps, p.io_write_bps, p.gpu_pct, p.gpu_engine,
                   p.gpu_mem_b, p.threads, p.handles, p.priority, p.state, {{trueIo}}, i.signer,
                   CASE i.sig_status WHEN 1 THEN 'Embedded' WHEN 2 THEN 'Catalog' WHEN 3 THEN 'NotSigned' WHEN 4 THEN 'NotTrusted' WHEN 5 THEN 'Package' WHEN 0 THEN 'Unknown' END
            FROM ProcessSnapshots p JOIN ProcessIdentity i ON i.proc_key = p.proc_key
            ORDER BY p.ts, COALESCE(p.cpu_pct, 0) DESC;
            """,
            row => [Csv.Time(row.GetInt64(0)), row.GetInt32(1), row.GetString(2), Text(row, 3), Real(row, 4), Long(row, 5), Long(row, 6), Long(row, 7),
                Real(row, 8), Real(row, 9), Real(row, 10), Text(row, 11), Long(row, 12), Long(row, 13), Long(row, 14), Long(row, 15),
                row.IsDBNull(16) ? null : ((ProcessState)row.GetInt32(16)).ToString(), Real(row, 17), Real(row, 18), Real(row, 19), Real(row, 20)], cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HasColumnAsync(string database, string table, string column, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0;
    }

    /// <summary>services.csv: the services when the incident was captured ("state") and every change in the window ("change").</summary>
    public Task WriteServicesAsync(string database, TextWriter output, CancellationToken cancellationToken) =>
        QueryToCsvAsync(database, output,
            ["record", "time_utc", "name", "display_name", "status", "start_type", "pid", "change", "old_value", "new_value"],
            """
            SELECT 'state', ts, name, display_name, status, start_type, pid, NULL, NULL, NULL FROM ServiceSnapshots
            WHERE ts = (SELECT MAX(ts) FROM ServiceSnapshots)
            UNION ALL
            SELECT 'change', ts, name, NULL, NULL, NULL, NULL, kind, old_value, new_value FROM ServiceEvents
            ORDER BY 1 DESC, 2, 3;
            """,
            row => [row.GetString(0), Csv.Time(row.GetInt64(1)), row.GetString(2), Text(row, 3),
                row.IsDBNull(4) ? null : ((Core.Snapshots.ServiceStatus)row.GetInt32(4)).ToString(),
                row.IsDBNull(5) ? null : ((Core.Snapshots.ServiceStartType)row.GetInt32(5)).ToString(),
                Long(row, 6), row.IsDBNull(7) ? null : ((ServiceEventKind)row.GetInt32(7)).ToString(), Text(row, 8), Text(row, 9)], cancellationToken);

    /// <summary>events.csv: the incident timeline, with the Windows events' provider, ID, channel and message.</summary>
    public Task WriteEventsAsync(string database, TextWriter output, CancellationToken cancellationToken) =>
        QueryToCsvAsync(database, output,
            ["time_utc", "kind", "importance", "title", "provider", "event_id", "channel", "category", "rule", "process", "message"],
            """
            SELECT e.ts, e.kind, e.importance, e.title, w.provider, w.event_id, w.channel, w.category, w.rule_id, w.process_name, w.message
            FROM IncidentEvents e LEFT JOIN WindowsEvents w ON e.source_table = 'WindowsEvents' AND w.id = e.source_id
            ORDER BY e.ts, e.id;
            """,
            row => [Csv.Time(row.GetInt64(0)), row.GetString(1), row.GetInt32(2), row.GetString(3), Text(row, 4), Long(row, 5), Text(row, 6),
                row.IsDBNull(7) ? null : ((EventCategory)row.GetInt32(7)).ToString(), Text(row, 8), Text(row, 9), Text(row, 10)], cancellationToken);

    /// <summary>The display changes in the window (for display.json).</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> DisplayChangesAsync(string database, CancellationToken cancellationToken)
    {
        var changes = new List<Dictionary<string, object?>>();
        if (!File.Exists(database))
        {
            return changes;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await foreach (var row in SystemReader.RowsAsync(connection, "SELECT ts, kind, target, old_value, new_value FROM DisplayEvents ORDER BY ts, id;", cancellationToken)
                           .ConfigureAwait(false))
        {
            changes.Add(new Dictionary<string, object?>
            {
                ["time_utc"] = Csv.Time(row.GetInt64(0)),
                ["change"] = ((DisplayEventKind)row.GetInt32(1)).ToString(),
                ["display"] = Text(row, 2),
                ["old"] = Text(row, 3),
                ["new"] = Text(row, 4),
            });
        }

        return changes;
    }

    private async Task QueryToCsvAsync(string database, TextWriter output, string[] header, string sql, Func<SqliteDataReader, object?[]> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        await Csv.HeaderAsync(output, header).ConfigureAwait(false);
        if (!File.Exists(database))
        {
            return;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await foreach (var row in SystemReader.RowsAsync(connection, sql, cancellationToken).ConfigureAwait(false))
        {
            await Csv.RowAsync(output, map(row)).ConfigureAwait(false);
        }
    }

    private static string? Text(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetString(i);

    private static double? Real(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetDouble(i);

    private static long? Long(SqliteDataReader row, int i) => row.IsDBNull(i) ? null : row.GetInt64(i);
}

/// <summary>
/// RFC 4180 CSV with spreadsheet-formula protection: text that starts with =, +, -, @, tab or carriage return
/// (which a spreadsheet would run as a formula; a process or window title can be anything) gets a leading
/// apostrophe. Numbers are written with the invariant culture.
/// </summary>
public static class Csv
{
    /// <summary>Unix milliseconds as ISO 8601 UTC.</summary>
    public static string Time(long unixMs) => UnixTime.FromUnixMs(unixMs).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>Writes the header row.</summary>
    public static Task HeaderAsync(TextWriter output, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.WriteLineAsync(string.Join(',', columns));
    }

    /// <summary>Writes one row.</summary>
    public static Task RowAsync(TextWriter output, params object?[] values)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(values);
        var line = new StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                line.Append(',');
            }

            line.Append(Cell(values[i]));
        }

        return output.WriteLineAsync(line.ToString());
    }

    /// <summary>One cell, quoted and protected as needed.</summary>
    public static string Cell(object? value)
    {
        switch (value)
        {
            case null:
                return "";
            case double d:
                return double.IsFinite(d) ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
            case float f:
                return float.IsFinite(f) ? f.ToString("0.###", CultureInfo.InvariantCulture) : "";
            case IFormattable number:
                return number.ToString(null, CultureInfo.InvariantCulture);
        }

        var text = value.ToString() ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>A value as JSON (for files that pair CSV with JSON).</summary>
    public static string Json(object? value) => JsonSerializer.Serialize(value);
}
