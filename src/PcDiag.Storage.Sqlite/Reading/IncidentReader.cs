using System.Globalization;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite.Incidents;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>Read-only access to one incident: its index row, triggers and the contents of its incident.db.</summary>
public sealed class IncidentReader(DataPaths paths, SqliteConnectionFactory connectionFactory, IncidentIndexReader index)
{
    /// <summary>Most processes returned for a moment.</summary>
    public const int MaxProcesses = 200;

    /// <summary>The incident's own database, or null when the key is not a valid incident key.</summary>
    public string? DatabasePath(string key) =>
        IncidentKey.TryParse(key, CultureInfo.InvariantCulture, out var parsed) ? Path.Combine(paths.GetIncidentDirectory(parsed), "incident.db") : null;

    /// <summary>The incident, or null when it does not exist.</summary>
    public async Task<IncidentDetail?> DetailAsync(string key, CancellationToken cancellationToken)
    {
        if (DatabasePath(key) is not { } database || await index.FindAsync(key, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var (summary, id) = found;
        var triggers = new List<TriggerEntry>();
        await using (var connection = await connectionFactory.OpenReadOnlyAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false))
        {
            await foreach (var row in SystemReader.RowsAsync(connection,
                               "SELECT ts, source, user_type, rule_id, detail, note FROM IncidentTriggers WHERE incident_id = $p0 ORDER BY ts, id;",
                               cancellationToken, id).ConfigureAwait(false))
            {
                triggers.Add(new TriggerEntry(row.GetInt64(0), (IncidentSource)row.GetInt32(1), row.IsDBNull(2) ? null : (UserIncidentType)row.GetInt32(2),
                    row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4), row.IsDBNull(5) ? null : row.GetString(5)));
            }
        }

        var info = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var hasData = File.Exists(database);
        if (hasData)
        {
            await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
            await foreach (var row in SystemReader.RowsAsync(connection, "SELECT key, value FROM IncidentInfo;", cancellationToken).ConfigureAwait(false))
            {
                info[row.GetString(0)] = row.IsDBNull(1) ? null : row.GetString(1);
            }
        }

        var center = summary.CenterAt.ToUnixTimeMilliseconds();
        return new IncidentDetail(summary, center - (summary.PreSeconds * 1000L), center + (summary.PostSeconds * 1000L), triggers, info, hasData);
    }

    /// <summary>The incident's timeline (oldest first).</summary>
    public async Task<IReadOnlyList<TimelineEntry>> TimelineAsync(string key, CancellationToken cancellationToken)
    {
        if (DatabasePath(key) is not { } database || !File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var entries = new List<TimelineEntry>();
        await foreach (var row in SystemReader.RowsAsync(connection,
                           "SELECT ts, kind, title, importance, detail_json, source_id FROM IncidentEvents ORDER BY ts, id LIMIT $p0;",
                           cancellationToken, HistoryReader.MaxEvents).ConfigureAwait(false))
        {
            entries.Add(new TimelineEntry(row.GetInt64(0), row.GetString(1), row.GetString(2), row.GetInt32(3),
                HistoryReader.ParseJson(row.IsDBNull(4) ? null : row.GetString(4)), row.IsDBNull(5) ? null : row.GetInt64(5)));
        }

        return entries;
    }

    /// <summary>Processes at the sample nearest to <paramref name="at"/>, busiest CPU first.</summary>
    public async Task<ProcessesAtTime> ProcessesAsync(string key, long at, CancellationToken cancellationToken)
    {
        if (DatabasePath(key) is not { } database || !File.Exists(database))
        {
            return new ProcessesAtTime(null, []);
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        long? before = null;
        long? after = null;
        await foreach (var row in SystemReader.RowsAsync(connection,
                           "SELECT (SELECT MAX(ts) FROM ProcessSnapshots WHERE ts <= $p0), (SELECT MIN(ts) FROM ProcessSnapshots WHERE ts >= $p0);",
                           cancellationToken, at).ConfigureAwait(false))
        {
            before = row.IsDBNull(0) ? null : row.GetInt64(0);
            after = row.IsDBNull(1) ? null : row.GetInt64(1);
        }

        var sample = (before, after) switch
        {
            ({ } b, { } a) => at - b <= a - at ? b : a,
            ({ } b, null) => b,
            (null, { } a) => a,
            _ => (long?)null,
        };
        if (sample is not { } ts)
        {
            return new ProcessesAtTime(null, []);
        }

        // Incidents recorded before Enhanced mode existed have no true-I/O columns.
        var trueIo = false;
        await foreach (var row in SystemReader.RowsAsync(connection,
                           "SELECT COUNT(*) FROM pragma_table_info('ProcessSnapshots') WHERE name = 'disk_read_bps';", cancellationToken).ConfigureAwait(false))
        {
            trueIo = row.GetInt64(0) > 0;
        }

        var trueIoColumns = trueIo
            ? "p.disk_read_bps + p.disk_write_bps, p.net_rx_bps + p.net_tx_bps"
            : "NULL, NULL";
        var processes = new List<ProcessAtTime>();
        await foreach (var row in SystemReader.RowsAsync(connection,
                           $$"""
                           SELECT p.proc_key, i.pid, i.name, i.path, p.cpu_pct, p.ws_b, p.private_b, p.io_read_bps, p.io_write_bps,
                                  p.gpu_pct, p.gpu_mem_b, p.threads, p.handles, p.state, {{trueIoColumns}}, i.signer, i.sig_status
                           FROM ProcessSnapshots p JOIN ProcessIdentity i ON i.proc_key = p.proc_key
                           WHERE p.ts = $p0
                           ORDER BY COALESCE(p.cpu_pct, 0) DESC, COALESCE(p.private_b, p.ws_b, 0) DESC
                           LIMIT $p1;
                           """,
                           cancellationToken, ts, MaxProcesses).ConfigureAwait(false))
        {
            processes.Add(new ProcessAtTime(
                row.GetInt64(0), row.GetInt32(1), row.GetString(2), row.IsDBNull(3) ? null : row.GetString(3),
                Double(row, 4), Long(row, 5), Long(row, 6), Double(row, 7), Double(row, 8), Double(row, 9), Long(row, 10),
                row.IsDBNull(11) ? null : row.GetInt32(11), row.IsDBNull(12) ? null : row.GetInt32(12),
                row.IsDBNull(13) ? ProcessState.Unknown.ToString() : ((ProcessState)row.GetInt32(13)).ToString(),
                Double(row, 14), Double(row, 15),
                row.IsDBNull(16) ? null : row.GetString(16),
                row.IsDBNull(17) ? null : ((SignatureStatus)row.GetInt32(17)).ToString()));
        }

        return new ProcessesAtTime(ts, processes);
    }

    /// <summary>Diagnostic statements (filled by the analyzers).</summary>
    public async Task<IReadOnlyList<FindingEntry>> FindingsAsync(string key, CancellationToken cancellationToken)
    {
        if (DatabasePath(key) is not { } database || !File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var findings = new List<FindingEntry>();
        await foreach (var row in SystemReader.RowsAsync(connection, "SELECT ordinal, level, text, evidence_refs_json FROM Findings ORDER BY ordinal;", cancellationToken).ConfigureAwait(false))
        {
            findings.Add(new FindingEntry(row.GetInt32(0), ((EvidenceLevel)row.GetInt32(1)).ToString(), row.GetString(2),
                HistoryReader.ParseJson(row.IsDBNull(3) ? null : row.GetString(3))));
        }

        return findings;
    }

    private static double? Double(Microsoft.Data.Sqlite.SqliteDataReader row, int index) => row.IsDBNull(index) ? null : Math.Round(row.GetDouble(index), 2);

    private static long? Long(Microsoft.Data.Sqlite.SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetInt64(index);
}
