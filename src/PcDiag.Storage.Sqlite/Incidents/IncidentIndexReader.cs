using System.Text;
using Microsoft.Data.Sqlite;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Incidents;

/// <summary>Read-only queries over the incident index (used by the Web host; it never writes).</summary>
public sealed class IncidentIndexReader(DataPaths paths, SqliteConnectionFactory connectionFactory)
{
    /// <summary>Largest page.</summary>
    public const int MaxPageSize = 500;

    private const string Columns =
        """
        i.incident_key, i.created_ts, i.center_ts, i.pre_ms, i.post_ms, i.state, i.source, i.user_type, i.title, i.user_notes,
        i.severity, i.snapshot_only, i.pinned, (SELECT COUNT(*) FROM IncidentTriggers t WHERE t.incident_id = i.id), i.id
        """;

    /// <summary>Incidents, newest first.</summary>
    public Task<IncidentPage> ListAsync(int limit, int offset, CancellationToken cancellationToken) =>
        ListAsync(new IncidentQuery(limit, offset), cancellationToken);

    /// <summary>Incidents matching the filters, newest first.</summary>
    public async Task<IncidentPage> ListAsync(IncidentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var limit = Math.Clamp(query.Limit, 1, MaxPageSize);
        var offset = Math.Max(0, query.Offset);
        if (!File.Exists(paths.IncidentIndexDatabase))
        {
            return new IncidentPage(0, []);
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        var where = new StringBuilder("WHERE 1 = 1");
        var parameters = new List<(string, object)>();
        if (query.Type is { } type)
        {
            where.Append(" AND (i.user_type = $type OR EXISTS (SELECT 1 FROM IncidentTriggers t WHERE t.incident_id = i.id AND t.user_type = $type))");
            parameters.Add(("$type", (int)type));
        }

        if (query.Source is { } source)
        {
            where.Append(" AND i.source = $source");
            parameters.Add(("$source", (int)source));
        }

        if (query.From is { } from)
        {
            where.Append(" AND i.center_ts >= $from");
            parameters.Add(("$from", from));
        }

        if (query.To is { } to)
        {
            where.Append(" AND i.center_ts <= $to");
            parameters.Add(("$to", to));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Append(" AND (i.title LIKE $text ESCAPE '\\' OR i.user_notes LIKE $text ESCAPE '\\' OR i.incident_key LIKE $text ESCAPE '\\')");
            var escaped = query.Text.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            parameters.Add(("$text", $"%{escaped}%"));
        }

        await using var count = connection.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM Incidents i {where};";
        Add(count, parameters);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Incidents i {where} ORDER BY i.center_ts DESC, i.id DESC LIMIT $limit OFFSET $offset;";
        Add(command, parameters);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var items = new List<IncidentSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(Read(reader));
        }

        return new IncidentPage(total, items);
    }

    /// <summary>One incident and its index id, or null.</summary>
    public async Task<(IncidentSummary Summary, long Id)?> FindAsync(string key, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.IncidentIndexDatabase))
        {
            return null;
        }

        await using var connection = await connectionFactory.OpenReadOnlyAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM Incidents i WHERE i.incident_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? (Read(reader), reader.GetInt64(14)) : null;
    }

    private static void Add(SqliteCommand command, List<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    private static IncidentSummary Read(SqliteDataReader reader) => new(
        reader.GetString(0),
        UnixTime.FromUnixMs(reader.GetInt64(1)),
        UnixTime.FromUnixMs(reader.GetInt64(2)),
        (int)(reader.GetInt64(3) / 1000),
        (int)(reader.GetInt64(4) / 1000),
        (IncidentState)reader.GetInt32(5),
        (IncidentSource)reader.GetInt32(6),
        reader.IsDBNull(7) ? null : (UserIncidentType)reader.GetInt32(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        (Severity)reader.GetInt32(10),
        reader.GetInt64(11) != 0,
        reader.GetInt64(12) != 0,
        reader.GetInt32(13));
}
