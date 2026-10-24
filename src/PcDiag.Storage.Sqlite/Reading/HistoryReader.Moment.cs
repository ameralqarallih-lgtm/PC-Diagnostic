using Microsoft.Data.Sqlite;
using PcDiag.Core.Events;
using PcDiag.Core.History;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Reading;

public sealed partial class HistoryReader
{
    /// <summary>How far from the moment a process sample may be (process samples are 2 s apart).</summary>
    public const long ProcessSampleWindow = 5000;

    /// <summary>How far back a process's latest stored figures are looked for (every process is stored at least every 10 minutes).</summary>
    public const long LatestFiguresWindow = 11 * 60_000;

    /// <summary>
    /// Everything recorded about one moment, incident or not: every series' value around it, the hardware sensors, and
    /// the processes running then. The busiest processes (the top of each ranking, stored every process tick) come
    /// with their figures; every other process is known from its start and end time. A process with no end time ended
    /// unseen (while the recorder was not recording) unless it is still running, which <paramref name="runningNow"/>
    /// tells (process ID and start time, Unix ms).
    /// </summary>
    public async Task<HistoryMoment> MomentAsync(string database, long at, Func<int, long, bool> runningNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runningNow);
        if (!File.Exists(database))
        {
            return new HistoryMoment(at, false, [], null, []);
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        var recorded = await ScalarLongAsync(connection,
            $"SELECT COUNT(*) FROM RecorderSessions WHERE start_ts <= {at} AND (end_ts IS NULL OR end_ts >= {at});", cancellationToken).ConfigureAwait(false) > 0;
        if (!recorded)
        {
            return new HistoryMoment(at, false, [], null, []);
        }

        // Beyond the raw data only one-minute summaries remain, and no process list: ended processes are forgotten
        // with it, so the processes still on record would not be the ones running then.
        var rawStart = await ScalarLongAsync(connection, "SELECT MIN(ts) FROM SystemSnapshots;", cancellationToken).ConfigureAwait(false) ?? long.MaxValue;
        var raw = at >= rawStart - MomentValue.Window;
        var values = await MomentValuesAsync(connection, database, at, raw, cancellationToken).ConfigureAwait(false);
        var (sampleTs, processes) = raw ? await MomentProcessesAsync(connection, at, runningNow, cancellationToken).ConfigureAwait(false) : (null, []);
        var services = raw ? await MomentServicesAsync(connection, at, processes, cancellationToken).ConfigureAwait(false) : [];
        return new HistoryMoment(at, true, values, sampleTs, processes, services);
    }

    private async Task<List<MomentValue>> MomentValuesAsync(SqliteConnection connection, string database, long at, bool raw, CancellationToken cancellationToken)
    {
        var devices = (await DevicesAsync(connection, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.Id, d => d.Kind);
        var minute = at - (at % 60_000);
        var values = new List<MomentValue>();
        foreach (var info in await CatalogAsync(database, cancellationToken).ConfigureAwait(false))
        {
            if (Resolve(info.Id, devices) is not { } series)
            {
                continue;
            }

            // Raw samples around the moment; beyond the raw data, the one-minute summary it falls in.
            var buckets = raw
                ? await RawAsync(connection, series, at - MomentValue.Window, at + MomentValue.Window, (2 * MomentValue.Window) + 1, cancellationToken).ConfigureAwait(false)
                : series.Rollup is { } rollup
                    ? await RollupAsync(connection, rollup, minute, minute + 60_000, 60_000, cancellationToken).ConfigureAwait(false)
                    : [];
            if (buckets.Count > 0)
            {
                values.Add(new MomentValue(info.Id, info.Label, info.Unit, info.Group, buckets[0][2]));
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.sensor_id, c.name, c.unit, AVG(r.value)
            FROM SensorReadings r JOIN SensorCatalog c ON c.sensor_id = r.sensor_id
            WHERE r.ts >= $from AND r.ts <= $to AND r.value IS NOT NULL
            GROUP BY c.sensor_id ORDER BY c.name;
            """;
        command.Parameters.AddWithValue("$from", at - MomentValue.Window);
        command.Parameters.AddWithValue("$to", at + MomentValue.Window);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new MomentValue($"sensor.{reader.GetInt64(0)}", reader.GetString(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                "Hardware sensors", Round(reader.GetDouble(3))));
        }

        return values;
    }

    private static async Task<(long? SampleTs, List<ProcessAtTime> Processes)> MomentProcessesAsync(
        SqliteConnection connection, long at, Func<int, long, bool> runningNow, CancellationToken cancellationToken)
    {
        var before = await ScalarLongAsync(connection, $"SELECT MAX(ts) FROM ProcessSnapshots WHERE ts <= {at} AND ts >= {at - ProcessSampleWindow};", cancellationToken).ConfigureAwait(false);
        var after = await ScalarLongAsync(connection, $"SELECT MIN(ts) FROM ProcessSnapshots WHERE ts >= {at} AND ts <= {at + ProcessSampleWindow};", cancellationToken).ConfigureAwait(false);
        long? sampleTs = (before, after) switch
        {
            ({ } b, { } a) => at - b <= a - at ? b : a,
            ({ } b, null) => b,
            (null, { } a) => a,
            _ => null,
        };

        // Figures: the moment's own sample (the busiest processes), else each process's latest stored figures, which
        // every process gets every 30 s when they change and every 10 minutes when not.
        var figures = new Dictionary<long, ProcessAtTime>();
        if (sampleTs is { } ts)
        {
            foreach (var row in await ProcessRowsAsync(connection, "p.ts = $ts", cancellationToken, ("$ts", ts), ("$from", ts - 1), ("$at", ts)).ConfigureAwait(false))
            {
                figures[row.ProcKey] = row;
            }
        }

        var latest = await ProcessRowsAsync(connection, "1 = 1", cancellationToken, ("$from", at - LatestFiguresWindow), ("$at", at)).ConfigureAwait(false);
        foreach (var row in latest.Where(r => !figures.ContainsKey(r.ProcKey)))
        {
            figures[row.ProcKey] = row;
        }

        // Every process known to be running then.
        var running = new List<ProcessAtTime>();
        var listed = new HashSet<long>();
        if (sampleTs is { } own)
        {
            foreach (var row in figures.Values.Where(r => r.FiguresTs == own))
            {
                running.Add(row with { FiguresTs = null });
                listed.Add(row.ProcKey);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT proc_key, pid, name, path, signer, sig_status, start_ts, end_ts FROM ProcessIdentity
                WHERE (start_ts IS NULL OR start_ts <= $at) AND (end_ts IS NULL OR end_ts > $at);
                """;
            command.Parameters.AddWithValue("$at", at);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var key = reader.GetInt64(0);
                var pid = reader.GetInt32(1);
                var start = Integer(reader, 6);
                if (listed.Contains(key) || (reader.IsDBNull(7) && !runningNow(pid, start ?? 0)))
                {
                    continue;
                }

                listed.Add(key);
                running.Add(figures.TryGetValue(key, out var measured)
                    ? measured
                    : new ProcessAtTime(
                        key, pid, reader.GetString(2), Text(reader, 3), null, null, null, null, null, null, null, null, null,
                        ProcessState.Running.ToString(), null, null, Text(reader, 4),
                        reader.IsDBNull(5) ? null : ((SignatureStatus)reader.GetInt32(5)).ToString(), start, Measured: false));
            }
        }

        var ordered = running.Where(p => p.Measured).OrderByDescending(p => p.CpuPercent ?? 0).ThenByDescending(p => p.PrivateBytes ?? p.WorkingSetBytes ?? 0)
            .Concat(running.Where(p => !p.Measured).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Pid))
            .ToList();
        return (sampleTs, ordered);
    }

    /// <summary>
    /// Each process's latest row in ($from, $at] with its identity; <see cref="ProcessAtTime.FiguresTs"/> holds the row's
    /// time. The latest row per process comes from one grouped pass over the range and a key lookup per process.
    /// </summary>
    internal static async Task<List<ProcessAtTime>> ProcessRowsAsync(SqliteConnection connection, string where, CancellationToken cancellationToken, params (string Name, long Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT p.proc_key, i.pid, i.name, i.path, p.cpu_pct, p.ws_b, p.private_b, p.io_read_bps, p.io_write_bps, p.gpu_pct, p.gpu_mem_b,
                   p.threads, p.handles, p.state, p.disk_read_bps + p.disk_write_bps, p.net_rx_bps + p.net_tx_bps, i.signer, i.sig_status, i.start_ts, p.ts
            FROM (SELECT proc_key AS k, MAX(ts) AS m FROM ProcessSnapshots WHERE ts > $from AND ts <= $at GROUP BY proc_key) last
            JOIN ProcessSnapshots p ON p.ts = last.m AND p.proc_key = last.k
            JOIN ProcessIdentity i ON i.proc_key = p.proc_key
            WHERE {where};
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var rows = new List<ProcessAtTime>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ProcessAtTime(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), Text(reader, 3),
                Number(reader, 4), Integer(reader, 5), Integer(reader, 6), Number(reader, 7), Number(reader, 8), Number(reader, 9), Integer(reader, 10),
                reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.IsDBNull(13) ? ProcessState.Unknown.ToString() : ((ProcessState)reader.GetInt32(13)).ToString(),
                Number(reader, 14), Number(reader, 15), Text(reader, 16),
                reader.IsDBNull(17) ? null : ((SignatureStatus)reader.GetInt32(17)).ToString(),
                Integer(reader, 18), FiguresTs: reader.GetInt64(19)));
        }

        return rows;
    }

    /// <summary>
    /// The services at the moment: the last service snapshot (hourly) with the changes after it applied, each mapped
    /// to its process in the moment's list by process ID.
    /// </summary>
    private static async Task<List<MomentService>> MomentServicesAsync(SqliteConnection connection, long at, IReadOnlyList<ProcessAtTime> processes, CancellationToken cancellationToken)
    {
        if (await ScalarLongAsync(connection, $"SELECT MAX(ts) FROM ServiceSnapshots WHERE ts <= {at};", cancellationToken).ConfigureAwait(false) is not { } snapshot)
        {
            return [];
        }

        var services = new Dictionary<string, (string Display, ServiceStatus Status, ServiceStartType Start, int? Pid)>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, display_name, status, start_type, pid FROM ServiceSnapshots WHERE ts = $ts;";
            command.Parameters.AddWithValue("$ts", snapshot);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                services[reader.GetString(0)] = (Text(reader, 1) ?? reader.GetString(0), (ServiceStatus)reader.GetInt32(2), (ServiceStartType)reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4));
            }
        }

        // Changes since then, in order. Their process ID is known from schema v10 on.
        var hasPid = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM pragma_table_info('ServiceEvents') WHERE name = 'pid';", cancellationToken).ConfigureAwait(false) > 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT name, kind, new_value, {(hasPid ? "pid" : "NULL")} FROM ServiceEvents WHERE ts > $from AND ts <= $at ORDER BY ts, id;";
            command.Parameters.AddWithValue("$from", snapshot);
            command.Parameters.AddWithValue("$at", at);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var known = services.TryGetValue(name, out var current);
                var value = Text(reader, 2);
                int? pid = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                switch ((ServiceEventKind)reader.GetInt32(1))
                {
                    case ServiceEventKind.Started or ServiceEventKind.Restarted or ServiceEventKind.Installed:
                        services[name] = (known ? current.Display : name, Status(value, ServiceStatus.Running), known ? current.Start : ServiceStartType.Manual, pid);
                        break;
                    case ServiceEventKind.Stopped or ServiceEventKind.Failed when known:
                        services[name] = current with { Status = Status(value, ServiceStatus.Stopped), Pid = null };
                        break;
                    case ServiceEventKind.StartTypeChanged when known && Enum.TryParse<ServiceStartType>(value, out var start):
                        services[name] = current with { Start = start };
                        break;
                    case ServiceEventKind.Removed:
                        services.Remove(name);
                        break;
                }
            }
        }

        var byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First().ProcKey);
        return services
            .Select(s => new MomentService(s.Key, s.Value.Display, s.Value.Status.ToString(), s.Value.Start.ToString(),
                s.Value.Status == ServiceStatus.Running ? s.Value.Pid : null,
                s.Value.Status == ServiceStatus.Running && s.Value.Pid is { } pid && byPid.TryGetValue(pid, out var key) ? key : null))
            .OrderBy(s => s.Status == nameof(ServiceStatus.Running) ? 0 : 1)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        static ServiceStatus Status(string? value, ServiceStatus fallback) => Enum.TryParse<ServiceStatus>(value, out var status) ? status : fallback;
    }

    private static string? Text(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetString(index);

    private static double? Number(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : Math.Round(row.GetDouble(index), 2);

    private static long? Integer(SqliteDataReader row, int index) => row.IsDBNull(index) ? null : row.GetInt64(index);
}
