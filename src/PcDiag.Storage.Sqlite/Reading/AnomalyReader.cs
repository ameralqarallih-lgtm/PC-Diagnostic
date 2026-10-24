using PcDiag.Core.Anomalies;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>Read-only anomaly queries over telemetry.db or an incident's incident.db.</summary>
public sealed class AnomalyReader(SqliteConnectionFactory connectionFactory)
{
    /// <summary>Most anomalies per request.</summary>
    public const int MaxAnomalies = 1000;

    /// <summary>Anomalies that overlap [<paramref name="from"/>, <paramref name="to"/>] (Unix ms), newest first.</summary>
    public async Task<IReadOnlyList<AnomalyInfo>> ListAsync(string database, long from, long to, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(database))
        {
            return [];
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);

        // Incidents recorded before anomalies named their device have no device column.
        var columns = AnomalyStore.Columns;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Anomalies') WHERE name = 'device';";
            if ((long)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 0)
            {
                columns = columns.Replace(", device", ", NULL AS device", StringComparison.Ordinal);
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT {columns} FROM Anomalies
            WHERE start_ts <= $to AND (end_ts IS NULL OR end_ts >= $from)
            ORDER BY start_ts DESC, id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaxAnomalies));
        var result = new List<AnomalyInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var anomaly = AnomalyStore.Read(reader);
            result.Add(anomaly.ToInfo(AnomalyRuleCatalog.Default.Find(anomaly.RuleId)?.MetricInfo?.Unit ?? ""));
        }

        return result;
    }
}
