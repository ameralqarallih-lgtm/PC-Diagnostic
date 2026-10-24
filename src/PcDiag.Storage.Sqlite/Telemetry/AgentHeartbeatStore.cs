using System.Text.Json;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>Heartbeat in telemetry.db (AgentHeartbeat, a single row); boot recoveries in incidents.db (precious).</summary>
public sealed class AgentHeartbeatStore(DataPaths paths, SqliteConnectionFactory connectionFactory) : IAgentHeartbeatStore
{
    /// <inheritdoc />
    public async Task<AgentHeartbeatState?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT last_ts, boot_id, clean_shutdown, recording FROM AgentHeartbeat WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AgentHeartbeatState(
            UnixTime.FromUnixMs(reader.GetInt64(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt64(2) != 0,
            reader.GetInt64(3) != 0);
    }

    /// <inheritdoc />
    public async Task BeatAsync(DateTimeOffset at, string bootId, bool recording, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AgentHeartbeat (id, last_ts, boot_id, clean_shutdown, recording) VALUES (1, $ts, $boot, 0, $recording)
            ON CONFLICT (id) DO UPDATE SET last_ts = excluded.last_ts, boot_id = excluded.boot_id, clean_shutdown = 0, recording = excluded.recording;
            """;
        command.Parameters.AddWithValue("$ts", UnixTime.ToUnixMs(at));
        command.Parameters.AddWithValue("$boot", bootId);
        command.Parameters.AddWithValue("$recording", recording ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MarkCleanShutdownAsync(DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO AgentHeartbeat (id, last_ts, boot_id, clean_shutdown, recording) VALUES (1, $ts, NULL, 1, 0)
            ON CONFLICT (id) DO UPDATE SET last_ts = excluded.last_ts, clean_shutdown = 1, recording = 0;
            """;
        command.Parameters.AddWithValue("$ts", UnixTime.ToUnixMs(at));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> RecordBootRecoveryAsync(BootRecovery recovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recovery);
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO BootRecoveries (detected_ts, last_heartbeat_ts, boot_ts, kind, was_recording, summary, details_json)
            VALUES ($detected, $beat, $boot, $kind, $recording, $summary, $details)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$detected", UnixTime.ToUnixMs(recovery.DetectedAt));
        command.Parameters.AddWithValue("$beat", UnixTime.ToUnixMs(recovery.LastHeartbeat));
        command.Parameters.AddWithValue("$boot", UnixTime.ToUnixMs(recovery.BootTime));
        command.Parameters.AddWithValue("$kind", (int)recovery.Kind);
        command.Parameters.AddWithValue("$recording", recovery.WasRecording ? 1 : 0);
        command.Parameters.AddWithValue("$summary", recovery.Summary);
        command.Parameters.AddWithValue("$details", JsonSerializer.Serialize(recovery.Details));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
