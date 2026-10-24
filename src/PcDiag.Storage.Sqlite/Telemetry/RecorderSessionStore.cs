using Microsoft.Data.Sqlite;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>Stores recording sessions in telemetry.db (RecorderSessions).</summary>
public sealed class RecorderSessionStore(DataPaths paths, SqliteConnectionFactory connectionFactory) : IRecorderSessionStore
{
    /// <inheritdoc />
    public async Task<long> StartSessionAsync(
        DateTimeOffset startedAt, RecordingStartReason reason, bool elevated, string appVersion, RecordingSelection? selection, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO RecorderSessions (start_ts, start_reason, elevated, app_version, selection_json) VALUES ($start, $reason, $elevated, $version, $selection);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$start", UnixTime.ToUnixMs(startedAt));
        command.Parameters.AddWithValue("$reason", (int)reason);
        command.Parameters.AddWithValue("$elevated", elevated ? 1 : 0);
        command.Parameters.AddWithValue("$version", appVersion);
        command.Parameters.AddWithValue("$selection", selection is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(selection.Items));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public async Task EndSessionAsync(long sessionId, DateTimeOffset endedAt, RecordingEndReason reason, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE RecorderSessions SET end_ts = $end, end_reason = $reason WHERE id = $id AND end_ts IS NULL;";
        command.Parameters.AddWithValue("$end", UnixTime.ToUnixMs(endedAt));
        command.Parameters.AddWithValue("$reason", (int)reason);
        command.Parameters.AddWithValue("$id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CloseAbandonedSessionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenReadWriteAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // A session still open at startup was cut short (crash, power loss, BSOD). End it at its last sample.
        command.CommandText =
            """
            UPDATE RecorderSessions
            SET end_ts = COALESCE((SELECT MAX(ts) FROM SystemSnapshots WHERE ts >= RecorderSessions.start_ts), start_ts),
                end_reason = $reason
            WHERE end_ts IS NULL;
            """;
        command.Parameters.AddWithValue("$reason", (int)RecordingEndReason.Crash);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
