using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>Stores anomalies in telemetry.db (Anomalies). Incidents copy the ones in their window.</summary>
public sealed class AnomalyStore(DataPaths paths, SqliteConnectionFactory connectionFactory) : IAnomalyStore
{
    /// <inheritdoc />
    public async Task<long> AddAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO Anomalies (start_ts, end_ts, rule_id, series, observed, baseline_mean, baseline_sd, duration_ms, severity, reasoning_json, incident_key, device)
            VALUES ($start, $end, $rule, $series, $observed, $mean, $sd, $duration, $severity, $reasoning, $incident, $device);
            SELECT last_insert_rowid();
            """;
        Bind(command, anomaly);
        command.Parameters.AddWithValue("$rule", anomaly.RuleId);
        command.Parameters.AddWithValue("$series", anomaly.Series);
        command.Parameters.AddWithValue("$severity", (int)anomaly.Severity);
        command.Parameters.AddWithValue("$incident", (object?)anomaly.IncidentKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$device", (object?)anomaly.Device ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Anomaly anomaly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anomaly);
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE Anomalies SET start_ts = $start, end_ts = $end, observed = $observed, baseline_mean = $mean, baseline_sd = $sd,
                   duration_ms = $duration, reasoning_json = $reasoning
            WHERE id = $id;
            """;
        Bind(command, anomaly);
        command.Parameters.AddWithValue("$id", anomaly.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task LinkIncidentAsync(long anomalyId, string incidentKey, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Anomalies SET incident_key = $key WHERE id = $id;";
        command.Parameters.AddWithValue("$key", incidentKey);
        command.Parameters.AddWithValue("$id", anomalyId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CloseAbandonedAsync(DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Anomalies SET end_ts = start_ts + COALESCE(duration_ms, 0) WHERE end_ts IS NULL AND start_ts < $before;";
        command.Parameters.AddWithValue("$before", UnixTime.ToUnixMs(before));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The stored form of an anomaly's reasoning.</summary>
    internal static string ReasoningJson(AnomalyReasoning reasoning) => JsonSerializer.Serialize(new StoredReasoning(
        reasoning.Classification, reasoning.RuleDescription, reasoning.Baseline, reasoning.BaselineMean, reasoning.BaselineStdDev, reasoning.Current,
        Math.Round(reasoning.Duration.TotalSeconds, 1)), StoredReasoning.Json);

    /// <summary>Reads an anomaly row: id, start_ts, end_ts, rule_id, series, observed, severity, reasoning_json, incident_key, device.</summary>
    internal static Anomaly Read(SqliteDataReader row)
    {
        var stored = JsonSerializer.Deserialize<StoredReasoning>(row.GetString(7), StoredReasoning.Json)
            ?? new StoredReasoning(row.GetString(3), row.GetString(3), null, null, null, "", 0);
        return new Anomaly(
            row.GetInt64(0),
            row.GetString(3),
            row.GetString(4),
            UnixTime.FromUnixMs(row.GetInt64(1)),
            row.IsDBNull(2) ? null : UnixTime.FromUnixMs(row.GetInt64(2)),
            row.IsDBNull(5) ? 0 : row.GetDouble(5),
            (Severity)row.GetInt32(6),
            new AnomalyReasoning(stored.Classification, stored.Rule, stored.Baseline, stored.BaselineMean, stored.BaselineSd, stored.Current,
                TimeSpan.FromSeconds(stored.DurationSeconds)),
            row.IsDBNull(9) ? null : row.GetString(9),
            row.IsDBNull(8) ? null : row.GetString(8));
    }

    /// <summary>The columns <see cref="Read"/> expects, in order.</summary>
    internal const string Columns = "id, start_ts, end_ts, rule_id, series, observed, severity, reasoning_json, incident_key, device";

    private static void Bind(SqliteCommand command, Anomaly anomaly)
    {
        command.Parameters.AddWithValue("$start", UnixTime.ToUnixMs(anomaly.StartedAt));
        command.Parameters.AddWithValue("$end", anomaly.EndedAt is { } end ? UnixTime.ToUnixMs(end) : DBNull.Value);
        command.Parameters.AddWithValue("$observed", double.IsFinite(anomaly.Observed) ? anomaly.Observed : DBNull.Value);
        command.Parameters.AddWithValue("$mean", (object?)anomaly.Reasoning.BaselineMean ?? DBNull.Value);
        command.Parameters.AddWithValue("$sd", (object?)anomaly.Reasoning.BaselineStdDev ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (long)anomaly.Reasoning.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("$reasoning", ReasoningJson(anomaly.Reasoning));
    }

    private sealed record StoredReasoning(string Classification, string Rule, string? Baseline, double? BaselineMean, double? BaselineSd, string Current, double DurationSeconds)
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    }
}
