using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Storage;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Sqlite.Incidents;

/// <summary>
/// Incident persistence. The index (key, state, triggers) lives in incidents.db; each incident's evidence lives in
/// its own folder's incident.db, which has the telemetry schema plus IncidentInfo, IncidentEvents and Findings, so
/// the investigation UI reads an incident with the same queries as live history. Evidence is copied from
/// telemetry.db with ATTACH + INSERT … SELECT over the columns both databases have; row IDs are kept, so references
/// between tables (e.g. a service failure's Windows event) stay valid. Copies are idempotent (INSERT OR IGNORE).
/// </summary>
public sealed class IncidentStore(DataPaths paths, SqliteConnectionFactory connectionFactory, DatabaseInitializer initializer, TimeProvider timeProvider) : IIncidentStore, IIncidentInventory
{
    /// <summary>Tables copied for the window by their ts column.</summary>
    internal static readonly string[] WindowTables =
    [
        "SystemSnapshots", "CpuMetrics", "MemoryMetrics", "GpuMetrics", "DiskMetrics", "NetworkMetrics", "VolumeMetrics",
        "ProcessSnapshots", "ProcessEvents", "ServiceEvents", "ServiceSnapshots", "SensorReadings", "ExtensionMetrics",
        "DisplayEvents", "DisplaySnapshots",
    ];

    /// <summary>Small reference tables copied whole.</summary>
    internal static readonly string[] ReferenceTables = ["Devices", "SensorCatalog", "SensorAvailability", "SystemInformation", "ExtensionSeries"];

    /// <inheritdoc />
    public async Task<Incident> CreateAsync(IncidentDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Incident incident;
        await using (var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false))
        {
            await using var transaction = (SqliteTransaction)await index.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var next = (long)(await ScalarAsync(index, transaction, "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'Incidents'), 0) + 1;", cancellationToken).ConfigureAwait(false))!;
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(draft.CreatedAt, timeProvider.LocalTimeZone).DateTime);
            var key = IncidentKey.Create(localDate, (int)next);

            await ExecuteAsync(index, transaction,
                """
                INSERT INTO Incidents (id, incident_key, created_ts, center_ts, pre_ms, post_ms, state, source, user_type, title, user_notes, severity, folder, app_version, snapshot_only)
                VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6, $p7, $p8, $p9, $p10, $p11, $p12, $p13, $p14);
                """,
                cancellationToken, next, key.ToString(), Ms(draft.CreatedAt), Ms(draft.CenterAt), (long)draft.PreWindow.TotalMilliseconds, (long)draft.PostWindow.TotalMilliseconds,
                (int)IncidentState.Recording, (int)draft.Source, draft.UserType is { } t ? (int)t : null, draft.Title, draft.Notes, (int)draft.Severity, key.ToString(), draft.AppVersion,
                draft.SnapshotOnly ? 1 : 0).ConfigureAwait(false);
            await InsertTriggerAsync(index, transaction, next, draft.Trigger, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            incident = new Incident(next, key, draft.CreatedAt, draft.CenterAt, draft.PreWindow, draft.PostWindow, IncidentState.Recording, draft.Source, draft.UserType, draft.Title, draft.Notes, draft.Severity);
        }

        // The folder name comes from the generated key, never from input.
        Directory.CreateDirectory(paths.GetIncidentDirectory(incident.Key));
        var database = IncidentDatabase(incident.Key);
        await initializer.InitializeIncidentDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        await using var connection = await connectionFactory.OpenReadWriteAsync(database, cancellationToken).ConfigureAwait(false);
        await using var info = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (name, value) in new Dictionary<string, string?>
        {
            ["incident_key"] = incident.Key.ToString(),
            ["title"] = draft.Title,
            ["notes"] = draft.Notes,
            ["source"] = draft.Source.ToString(),
            ["user_type"] = draft.UserType?.ToString(),
            ["severity"] = draft.Severity.ToString(),
            ["created_utc"] = draft.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["center_utc"] = draft.CenterAt.ToString("O", CultureInfo.InvariantCulture),
            ["pre_seconds"] = ((long)draft.PreWindow.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["post_seconds"] = ((long)draft.PostWindow.TotalSeconds).ToString(CultureInfo.InvariantCulture),
            ["snapshot_only"] = draft.SnapshotOnly ? "true" : "false",
            ["state"] = IncidentState.Recording.ToString(),
            ["app_version"] = draft.AppVersion,
            ["recording_selection"] = draft.Selection is null ? null : System.Text.Json.JsonSerializer.Serialize(draft.Selection.Items),
        })
        {
            await ExecuteAsync(connection, info, "INSERT OR REPLACE INTO IncidentInfo (key, value) VALUES ($p0, $p1);", cancellationToken, name, value).ConfigureAwait(false);
        }

        await info.CommitAsync(cancellationToken).ConfigureAwait(false);
        return incident;
    }

    /// <inheritdoc />
    public async Task AddTriggerAsync(long incidentId, IncidentTrigger trigger, CancellationToken cancellationToken)
    {
        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await InsertTriggerAsync(index, null, incidentId, trigger, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task CaptureWindowAsync(Incident incident, DateTimeOffset windowStart, DateTimeOffset windowEnd, IReadOnlyList<SampleFrame> frames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        ArgumentNullException.ThrowIfNull(frames);
        var start = Ms(windowStart);
        var end = Ms(windowEnd);

        // Events cover the whole incident window: some are logged late (e.g. Windows Error Reporting).
        var eventsFrom = Math.Min(start, Ms(incident.CenterAt - incident.PreWindow));
        var eventsTo = Math.Max(end, Ms(incident.CenterAt + incident.PostWindow));

        await using var connection = await connectionFactory.OpenReadWriteAsync(IncidentDatabase(incident.Key), cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "ATTACH DATABASE $p0 AS t;", cancellationToken, paths.TelemetryDatabase).ConfigureAwait(false);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var table in WindowTables)
            {
                await CopyAsync(connection, transaction, table, "ts >= $p0 AND ts <= $p1", cancellationToken, start, end).ConfigureAwait(false);
            }

            await CopyAsync(connection, transaction, "WindowsEvents", "ts >= $p0 AND ts <= $p1", cancellationToken, eventsFrom, eventsTo).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "Anomalies", "start_ts <= $p1 AND (end_ts IS NULL OR end_ts >= $p0)", cancellationToken, eventsFrom, eventsTo).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "RecorderSessions", "start_ts <= $p1 AND (end_ts IS NULL OR end_ts >= $p0)", cancellationToken, eventsFrom, eventsTo).ConfigureAwait(false);

            // State at the start of the window: the latest snapshots taken before it.
            await CopyAsync(connection, transaction, "HardwareSnapshots", "id = (SELECT id FROM t.HardwareSnapshots WHERE ts <= $p0 ORDER BY ts DESC LIMIT 1) OR (ts >= $p1 AND ts <= $p0)", cancellationToken, end, start).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "DisplaySnapshots", "id = (SELECT id FROM t.DisplaySnapshots WHERE ts <= $p0 ORDER BY ts DESC LIMIT 1)", cancellationToken, start).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "Drivers", "snapshot_ts = (SELECT MAX(snapshot_ts) FROM t.Drivers WHERE snapshot_ts <= $p0)", cancellationToken, end).ConfigureAwait(false);
            await CopyAsync(connection, transaction, "ServiceSnapshots", "ts = (SELECT MAX(ts) FROM t.ServiceSnapshots WHERE ts <= $p0)", cancellationToken, start).ConfigureAwait(false);
            foreach (var table in ReferenceTables)
            {
                await CopyAsync(connection, transaction, table, "1 = 1", cancellationToken).ConfigureAwait(false);
            }

            // Every process from the in-memory frames (telemetry.db keeps only the busiest ones).
            await WriteFramesAsync(connection, transaction, frames, start, end, cancellationToken).ConfigureAwait(false);

            await CopyAsync(connection, transaction, "ProcessIdentity",
                "proc_key IN (SELECT proc_key FROM main.ProcessSnapshots UNION SELECT proc_key FROM main.ProcessEvents UNION SELECT proc_key FROM main.WindowsEvents WHERE proc_key IS NOT NULL)",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteAsync(connection, null, "DETACH DATABASE t;", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SaveSnapshotsAsync(Incident incident, DisplaySnapshot? display, ServiceSnapshot? services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        await using var connection = await connectionFactory.OpenReadWriteAsync(IncidentDatabase(incident.Key), cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (display is not null)
        {
            var json = JsonSerializer.Serialize(new { display.IsRemoteSession, display.Displays });
            await ExecuteAsync(connection, transaction, "INSERT INTO DisplaySnapshots (ts, hash, json) VALUES ($p0, $p1, $p2);", cancellationToken,
                Ms(display.Timestamp), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json))), json).ConfigureAwait(false);
        }

        foreach (var service in services?.Services ?? [])
        {
            await ExecuteAsync(connection, transaction,
                "INSERT OR REPLACE INTO ServiceSnapshots (ts, name, display_name, status, start_type, pid, svc_type) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6);",
                cancellationToken, Ms(services!.Timestamp), service.Name, service.DisplayName, (int)service.Status, (int)service.StartType, service.ProcessId, service.ServiceType).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO IncidentInfo (key, value) VALUES ('snapshots_utc', $p0);", cancellationToken,
            timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task FinalizeAsync(Incident incident, IncidentState state, BootRecovery? recovery, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incident);
        var from = Ms(incident.CenterAt - incident.PreWindow);
        var to = Ms(incident.CenterAt + incident.PostWindow);
        if (recovery is not null)
        {
            to = Math.Max(to, Ms(recovery.DetectedAt));
        }

        var triggers = await ReadTriggersAsync(incident.Id, cancellationToken).ConfigureAwait(false);

        // An incident left unfinished by an older version gets the current schema before its timeline is built.
        await initializer.InitializeIncidentDatabaseAsync(IncidentDatabase(incident.Key), cancellationToken).ConfigureAwait(false);
        string? headline;
        await using (var connection = await connectionFactory.OpenReadWriteAsync(IncidentDatabase(incident.Key), cancellationToken).ConfigureAwait(false))
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM IncidentEvents;", cancellationToken).ConfigureAwait(false);
            foreach (var sql in TimelineSql)
            {
                await ExecuteAsync(connection, transaction, sql, cancellationToken, from, to).ConfigureAwait(false);
            }

            foreach (var trigger in triggers)
            {
                var title = trigger.UserType is { } type ? $"Captured: {type.ToDisplayName()} ({Describe(trigger.Source)})" : $"Captured ({Describe(trigger.Source)})";
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO IncidentEvents (ts, kind, source_table, title, detail_json, importance) VALUES ($p0, 'trigger', 'IncidentTriggers', $p1, $p2, 3);",
                    cancellationToken, Ms(trigger.Timestamp), trigger.Source == IncidentSource.BootRecovery ? trigger.Detail ?? title : title,
                    JsonSerializer.Serialize(new { source = trigger.Source.ToString(), userType = trigger.UserType?.ToString(), trigger.Note, trigger.Detail, trigger.RuleId })).ConfigureAwait(false);
            }

            if (recovery is not null)
            {
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO IncidentEvents (ts, kind, source_table, title, detail_json, importance) VALUES ($p0, 'recorder', 'BootRecoveries', $p1, $p2, 2);",
                    cancellationToken, Ms(recovery.LastHeartbeat), "Last heartbeat of the recorder before the restart", JsonSerializer.Serialize(recovery.Details)).ConfigureAwait(false);
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO IncidentEvents (ts, kind, source_table, title, detail_json, importance) VALUES ($p0, 'recorder', 'BootRecoveries', 'Windows started', NULL, 1);",
                    cancellationToken, Ms(recovery.BootTime)).ConfigureAwait(false);
            }

            headline = await HeadlineAsync(connection, transaction, Ms(incident.CenterAt), cancellationToken).ConfigureAwait(false);
            foreach (var (name, value) in new Dictionary<string, string?>
            {
                ["state"] = state.ToString(),
                ["finalized_utc"] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                ["headline"] = headline,
                ["samples"] = (await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM SystemSnapshots;", cancellationToken).ConfigureAwait(false))?.ToString(),
                ["timeline_entries"] = (await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM IncidentEvents;", cancellationToken).ConfigureAwait(false))?.ToString(),
            })
            {
                await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO IncidentInfo (key, value) VALUES ($p0, $p1);", cancellationToken, name, value).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await MakeSelfContainedAsync(IncidentDatabase(incident.Key), cancellationToken).ConfigureAwait(false);

        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(index, null, "UPDATE Incidents SET state = $p0, headline_json = $p1 WHERE id = $p2;", cancellationToken, (int)state, headline, incident.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// A finished incident is a single file: the write-ahead log is merged and the database switched to rollback
    /// journaling, so copying or zipping the folder never misses data. This process's idle pooled connections (the
    /// readers' too) are closed first, because the journal mode can only change while no other connection has the
    /// file open. When another process is reading the incident at that moment (the Web host), the file stays in WAL
    /// mode, which is just as correct, and the next write tries again. Returns whether the switch happened.
    /// </summary>
    internal async Task<bool> MakeSelfContainedAsync(string database, CancellationToken cancellationToken)
    {
        connectionFactory.ReleasePooledConnections(database);
        await using var connection = await connectionFactory.OpenReadWriteAsync(database, cancellationToken).ConfigureAwait(false);

        // Keeps this connection from returning to the pool.
        SqliteConnection.ClearPool(connection);
        try
        {
            await ExecuteAsync(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            return string.Equals(await ScalarAsync(connection, null, "PRAGMA journal_mode = DELETE;", cancellationToken).ConfigureAwait(false) as string, "delete",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            // SQLITE_BUSY / SQLITE_LOCKED: someone else has the file open right now.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Incident>> ListUnfinishedAsync(CancellationToken cancellationToken)
    {
        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = index.CreateCommand();
        command.CommandText =
            "SELECT id, incident_key, created_ts, center_ts, pre_ms, post_ms, state, source, user_type, title, user_notes, severity FROM Incidents WHERE state IN ($p0, $p1) ORDER BY id;";
        command.Parameters.AddWithValue("$p0", (int)IncidentState.Recording);
        command.Parameters.AddWithValue("$p1", (int)IncidentState.Finalizing);
        var incidents = new List<Incident>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            incidents.Add(new Incident(
                reader.GetInt64(0),
                IncidentKey.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                UnixTime.FromUnixMs(reader.GetInt64(2)),
                UnixTime.FromUnixMs(reader.GetInt64(3)),
                TimeSpan.FromMilliseconds(reader.GetInt64(4)),
                TimeSpan.FromMilliseconds(reader.GetInt64(5)),
                (IncidentState)reader.GetInt32(6),
                (IncidentSource)reader.GetInt32(7),
                reader.IsDBNull(8) ? null : (UserIncidentType)reader.GetInt32(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                (Severity)reader.GetInt32(11)));
        }

        return incidents;
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<StoredIncident>> IIncidentInventory.ListAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.IncidentIndexDatabase))
        {
            return [];
        }

        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = index.CreateCommand();
        command.CommandText = "SELECT incident_key, center_ts, pinned, state FROM Incidents ORDER BY center_ts, id;";
        var incidents = new List<StoredIncident>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Finished: complete or partial (one still recording, finalizing or being analyzed is not).
            incidents.Add(new StoredIncident(reader.GetString(0), UnixTime.FromUnixMs(reader.GetInt64(1)), reader.GetInt64(2) != 0, reader.GetInt32(3) >= (int)IncidentState.Complete));
        }

        return incidents;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingBootRecovery>> PendingBootRecoveriesAsync(CancellationToken cancellationToken)
    {
        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = index.CreateCommand();
        command.CommandText =
            "SELECT id, detected_ts, last_heartbeat_ts, boot_ts, kind, was_recording, summary, details_json FROM BootRecoveries WHERE processed_ts IS NULL ORDER BY id;";
        var pending = new List<PendingBootRecovery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var details = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7));
            pending.Add(new PendingBootRecovery(reader.GetInt64(0), new BootRecovery(
                UnixTime.FromUnixMs(reader.GetInt64(1)),
                UnixTime.FromUnixMs(reader.GetInt64(2)),
                UnixTime.FromUnixMs(reader.GetInt64(3)),
                (UncleanStopKind)reader.GetInt32(4),
                reader.GetInt64(5) != 0,
                reader.GetString(6),
                details ?? [])));
        }

        return pending;
    }

    /// <inheritdoc />
    public async Task MarkBootRecoveryAsync(long recoveryId, long? incidentId, CancellationToken cancellationToken)
    {
        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(index, null, "UPDATE BootRecoveries SET processed_ts = $p0, incident_id = $p1 WHERE id = $p2;", cancellationToken,
            Ms(timeProvider.GetUtcNow()), incidentId, recoveryId).ConfigureAwait(false);
    }

    /// <summary>The incident's database file.</summary>
    /// <inheritdoc />
    public async Task<bool> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var keyText = key.ToString();
        await using (var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false))
        {
            await using var command = index.CreateCommand();
            command.CommandText =
                """
                UPDATE Incidents SET
                  title = COALESCE($title, title),
                  user_notes = CASE WHEN $notes IS NULL THEN user_notes WHEN $notes = '' THEN NULL ELSE $notes END,
                  user_type = COALESCE($type, user_type),
                  pinned = COALESCE($pinned, pinned)
                WHERE incident_key = $key;
                """;
            command.Parameters.AddWithValue("$title", (object?)update.Title ?? DBNull.Value);
            command.Parameters.AddWithValue("$notes", (object?)update.Notes ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", update.UserType is { } t ? (int)t : DBNull.Value);
            command.Parameters.AddWithValue("$pinned", update.Pinned is { } p ? (p ? 1 : 0) : DBNull.Value);
            command.Parameters.AddWithValue("$key", keyText);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        // Keep the folder self-describing.
        var database = IncidentDatabase(key);
        if (File.Exists(database))
        {
            await using (var connection = await connectionFactory.OpenReadWriteAsync(database, cancellationToken).ConfigureAwait(false))
            {
                foreach (var (name, value) in new (string, string?)[] { ("title", update.Title), ("notes", update.Notes), ("user_type", update.UserType?.ToString()) })
                {
                    if (value is not null)
                    {
                        await ExecuteAsync(connection, null, "INSERT OR REPLACE INTO IncidentInfo (key, value) VALUES ($p0, $p1);", cancellationToken, name, value.Length == 0 ? null : value).ConfigureAwait(false);
                    }
                }
            }

            // Back to a single file (no WAL) once this connection is closed.
            await MakeSelfContainedAsync(database, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        await using (var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false))
        {
            await using var transaction = (SqliteTransaction)await index.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(index, transaction, "DELETE FROM IncidentTriggers WHERE incident_id = (SELECT id FROM Incidents WHERE incident_key = $p0);", cancellationToken, key.ToString()).ConfigureAwait(false);
            await using var command = index.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM Incidents WHERE incident_key = $key;";
            command.Parameters.AddWithValue("$key", key.ToString());
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (!deleted)
            {
                return false;
            }
        }

        // The folder name comes from the generated key, never from input.
        var folder = paths.GetIncidentDirectory(key);
        if (Directory.Exists(folder))
        {
            var database = IncidentDatabase(key);
            if (File.Exists(database))
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = true }.ToString());
                SqliteConnection.ClearPool(connection);
                connectionFactory.ReleasePooledConnections(database);
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still open elsewhere (a viewer reading it): the incident is gone from the index, and the
                // storage manager removes the folder it left once nothing holds it.
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SaveFindingsAsync(IncidentKey key, IReadOnlyList<Finding> findings, int analyzerVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var database = IncidentDatabase(key);
        if (!File.Exists(database))
        {
            return false;
        }

        await initializer.InitializeIncidentDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        await using (var connection = await connectionFactory.OpenReadWriteAsync(database, cancellationToken).ConfigureAwait(false))
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM Findings;", cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < findings.Count; i++)
            {
                var finding = findings[i];
                var refs = JsonSerializer.Serialize(finding.EvidenceRefs.Select(r => new { kind = r.Kind, ts = Ms(r.Timestamp), sourceId = r.SourceId }));
                await ExecuteAsync(connection, transaction, "INSERT INTO Findings (ordinal, level, text, evidence_refs_json) VALUES ($p0, $p1, $p2, $p3);", cancellationToken,
                    i + 1, (int)finding.Level, finding.Text, refs).ConfigureAwait(false);
            }

            foreach (var (name, value) in new Dictionary<string, string>
            {
                ["analysis_version"] = analyzerVersion.ToString(CultureInfo.InvariantCulture),
                ["analyzed_utc"] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
            })
            {
                await ExecuteAsync(connection, transaction, "INSERT OR REPLACE INTO IncidentInfo (key, value) VALUES ($p0, $p1);", cancellationToken, name, value).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false))
        {
            await ExecuteAsync(index, null, "UPDATE Incidents SET analysis_version = $p0 WHERE incident_key = $p1;", cancellationToken, analyzerVersion, key.ToString()).ConfigureAwait(false);
        }

        await MakeSelfContainedAsync(database, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IncidentKey>> ListUnanalyzedAsync(int analyzerVersion, int limit, CancellationToken cancellationToken)
    {
        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = index.CreateCommand();
        command.CommandText =
            """
            SELECT incident_key FROM Incidents
            WHERE state IN ($complete, $partial) AND (analysis_version IS NULL OR analysis_version < $version)
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$complete", (int)IncidentState.Complete);
        command.Parameters.AddWithValue("$partial", (int)IncidentState.Partial);
        command.Parameters.AddWithValue("$version", analyzerVersion);
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var keys = new List<IncidentKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (IncidentKey.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, out var key))
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    internal string IncidentDatabase(IncidentKey key) => Path.Combine(paths.GetIncidentDirectory(key), "incident.db");

    /// <summary>Timeline entries built from the copied tables ($p0..$p1 = incident window).</summary>
    /// <summary>
    /// The timeline queries (one per event table), as SELECTs returning ts, kind, source_table, source_id, title,
    /// detail_json and importance for $p0 &lt;= ts &lt;= $p1. Used to build an incident's timeline and, on the live
    /// database, for history markers.
    /// </summary>
    internal static readonly string[] TimelineSelects =
    [
        """
        SELECT ts AS ts, 'windows-event' AS kind, 'WindowsEvents' AS source_table, id AS source_id, title AS title,
               json_object('provider', provider, 'eventId', event_id, 'channel', channel, 'category', category, 'ruleId', rule_id) AS detail_json,
               severity AS importance
        FROM WindowsEvents WHERE ts >= $p0 AND ts <= $p1;
        """,
        """
        SELECT e.ts, 'process', 'ProcessEvents', e.id,
               COALESCE(i.name, json_extract(e.detail_json, '$.name'), 'A process') || CASE e.kind
                   WHEN 0 THEN ' started' WHEN 1 THEN ' stopped' WHEN 2 THEN ' crashed'
                   WHEN 3 THEN ' stopped responding' WHEN 4 THEN ' is responding again' ELSE '' END,
               e.detail_json,
               CASE e.kind WHEN 2 THEN 3 WHEN 3 THEN 2 WHEN 4 THEN 1 ELSE 0 END
        FROM ProcessEvents e LEFT JOIN ProcessIdentity i ON i.proc_key = e.proc_key
        WHERE e.ts >= $p0 AND e.ts <= $p1;
        """,
        """
        SELECT ts, 'service', 'ServiceEvents', id,
               name || CASE kind
                   WHEN 0 THEN ' started' WHEN 1 THEN ' stopped' WHEN 2 THEN ' restarted' WHEN 3 THEN ' start type changed'
                   WHEN 4 THEN ' failed' WHEN 5 THEN ' installed' WHEN 6 THEN ' removed' ELSE ' changed' END,
               json_object('old', old_value, 'new', new_value, 'windowsEvent', event_ref),
               CASE kind WHEN 4 THEN 2 ELSE 1 END
        FROM ServiceEvents WHERE ts >= $p0 AND ts <= $p1;
        """,
        """
        SELECT ts, 'display', 'DisplayEvents', id,
               COALESCE(target, 'Display') || ': ' || CASE kind
                   WHEN 0 THEN 'connected' WHEN 1 THEN 'disconnected' WHEN 2 THEN 'layout changed' WHEN 3 THEN 'resolution changed'
                   WHEN 4 THEN 'refresh rate changed' WHEN 5 THEN 'HDR changed' WHEN 6 THEN 'orientation changed'
                   WHEN 7 THEN 'power ' || COALESCE(new_value, 'changed') WHEN 8 THEN 'display change with no configuration change'
                   WHEN 9 THEN COALESCE(new_value, 'session changed') ELSE 'changed' END
                   || CASE WHEN kind IN (2, 3, 4, 5, 6) AND old_value IS NOT NULL AND new_value IS NOT NULL THEN ' (' || old_value || ' → ' || new_value || ')' ELSE '' END,
               json_object('old', old_value, 'new', new_value, 'targetKey', target_key),
               CASE WHEN kind IN (0, 1, 7, 8) THEN 2 ELSE 1 END
        FROM DisplayEvents WHERE ts >= $p0 AND ts <= $p1;
        """,
        """
        SELECT start_ts, 'anomaly', 'Anomalies', id,
               COALESCE(json_extract(reasoning_json, '$.classification'), rule_id)
                   || CASE WHEN device IS NOT NULL AND instr(series, ':') = 0 THEN ' on ' || device ELSE '' END
                   || ': ' || COALESCE(json_extract(reasoning_json, '$.current'), ''),
               json_object('ruleId', rule_id, 'series', series, 'device', device, 'rule', json_extract(reasoning_json, '$.rule'),
                           'baseline', json_extract(reasoning_json, '$.baseline'), 'durationSeconds', json_extract(reasoning_json, '$.durationSeconds'),
                           'endTs', end_ts, 'incidentKey', incident_key),
               CASE WHEN severity >= 3 THEN 3 WHEN severity = 2 THEN 2 ELSE 1 END
        FROM Anomalies WHERE start_ts >= $p0 AND start_ts <= $p1;
        """,
        """
        SELECT start_ts, 'recorder', 'RecorderSessions', id, 'Recording started', NULL, 0 FROM RecorderSessions WHERE start_ts >= $p0 AND start_ts <= $p1
        UNION ALL
        SELECT end_ts, 'recorder', 'RecorderSessions', id, 'Recording stopped', NULL, 0 FROM RecorderSessions WHERE end_ts >= $p0 AND end_ts <= $p1;
        """,
    ];

    /// <summary>Statements that fill IncidentEvents from <see cref="TimelineSelects"/>.</summary>
    internal static readonly string[] TimelineSql = TimelineSelects
        .Select(select => "INSERT INTO IncidentEvents (ts, kind, source_table, source_id, title, detail_json, importance) " + select)
        .ToArray();

    private static string Describe(IncidentSource source) => source switch
    {
        IncidentSource.Hotkey => "hotkey",
        IncidentSource.Tray => "tray",
        IncidentSource.User => "dashboard or command",
        IncidentSource.Rule => "rule",
        IncidentSource.Event => "event",
        IncidentSource.BootRecovery => "after restart",
        _ => source.ToString(),
    };

    private static long Ms(DateTimeOffset value) => UnixTime.ToUnixMs(value);

    private static async Task<string?> HeadlineAsync(SqliteConnection connection, SqliteTransaction transaction, long center, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT json_object('ts', ts, 'cpuPct', cpu_pct, 'memUsedPct', mem_used_pct, 'gpuPct', gpu_pct, 'vramPct', vram_pct, 'diskActivePct', disk_active_pct, 'netBps', net_bps)
            FROM SystemSnapshots WHERE ts <= $p0 ORDER BY ts DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$p0", center);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task InsertTriggerAsync(SqliteConnection index, SqliteTransaction? transaction, long incidentId, IncidentTrigger trigger, CancellationToken cancellationToken) =>
        await ExecuteAsync(index, transaction,
            "INSERT INTO IncidentTriggers (incident_id, ts, source, user_type, rule_id, detail, note) VALUES ($p0, $p1, $p2, $p3, $p4, $p5, $p6);",
            cancellationToken, incidentId, Ms(trigger.Timestamp), (int)trigger.Source, trigger.UserType is { } t ? (int)t : null, trigger.RuleId, trigger.Detail, trigger.Note).ConfigureAwait(false);

    private static async Task WriteFramesAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<SampleFrame> frames, long start, long end, CancellationToken cancellationToken)
    {
        if (!frames.Any(f => f.Processes is not null))
        {
            return;
        }

        using var commands = new TelemetryCommands(connection);
        commands.Enlist(transaction);
        foreach (var frame in frames)
        {
            var ts = Ms(frame.Timestamp);
            if (frame.Processes is not { } processes || ts < start || ts > end)
            {
                continue;
            }

            foreach (var identity in processes.Identities)
            {
                await commands.ProcessIdentity.ExecuteAsync(cancellationToken, identity.Key.ToStorageId(), identity.Key.Pid, identity.ParentPid, identity.Name, identity.Path,
                    identity.CommandLine, identity.User, Ms(identity.StartedAt), identity.Signer, identity.Signature is { } signature ? (int)signature : null).ConfigureAwait(false);
            }

            foreach (var selected in ProcessSelection.SelectAll(processes, frame.Gpu))
            {
                var p = selected.Metrics;
                await commands.ProcessIdentity.ExecuteAsync(cancellationToken, p.Key.ToStorageId(), p.Key.Pid, DBNull.Value, p.Name, null, null, null, DBNull.Value, null, null).ConfigureAwait(false);
                await commands.ProcessSnapshot.ExecuteAsync(
                    cancellationToken,
                    ts,
                    p.Key.ToStorageId(),
                    Db(p.CpuPercent),
                    Db(p.CpuTimeMs),
                    Db(p.WorkingSetBytes),
                    Db(p.PrivateBytes),
                    Db(p.CommitBytes),
                    Db(p.IoReadBytesPerSec),
                    Db(p.IoWriteBytesPerSec),
                    selected.GpuPercent.HasValue ? selected.GpuPercent.Value : DBNull.Value,
                    selected.GpuEngine,
                    selected.GpuMemoryBytes.HasValue ? selected.GpuMemoryBytes.Value : DBNull.Value,
                    Db(p.ThreadCount),
                    Db(p.HandleCount),
                    Db(p.BasePriority),
                    (int)p.State,
                    p.TrueIo is { } io ? io.DiskReadBytesPerSec : DBNull.Value,
                    p.TrueIo is { } io2 ? io2.DiskWriteBytesPerSec : DBNull.Value,
                    p.TrueIo is { } io3 ? io3.NetReceiveBytesPerSec : DBNull.Value,
                    p.TrueIo is { } io4 ? io4.NetSendBytesPerSec : DBNull.Value).ConfigureAwait(false);
            }
        }
    }

    private static object Db<T>(Reading<T> reading)
        where T : struct =>
        reading.HasValue ? reading.Value!.Value : DBNull.Value;

    private static async Task CopyAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string where, CancellationToken cancellationToken, params object[] values)
    {
        // Only the columns both databases have, in the incident database's order.
        var target = await ColumnsAsync(connection, transaction, "main", table, cancellationToken).ConfigureAwait(false);
        var source = await ColumnsAsync(connection, transaction, "t", table, cancellationToken).ConfigureAwait(false);
        var columns = string.Join(", ", target.Where(source.Contains).Select(c => $"\"{c}\""));
        if (columns.Length == 0)
        {
            return;
        }

        // Rows are immutable except anomalies, which end (and get their incident) after an incident has copied them.
        var verb = table == "Anomalies" ? "INSERT OR REPLACE" : "INSERT OR IGNORE";
        await ExecuteAsync(connection, transaction, $"{verb} INTO main.\"{table}\" ({columns}) SELECT {columns} FROM t.\"{table}\" WHERE {where};", cancellationToken, values).ConfigureAwait(false);
    }

    private static async Task<List<string>> ColumnsAsync(SqliteConnection connection, SqliteTransaction transaction, string schema, string table, CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}', '{schema}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private async Task<List<IncidentTrigger>> ReadTriggersAsync(long incidentId, CancellationToken cancellationToken)
    {
        await using var index = await connectionFactory.OpenReadWriteAsync(paths.IncidentIndexDatabase, cancellationToken).ConfigureAwait(false);
        await using var command = index.CreateCommand();
        command.CommandText = "SELECT ts, source, user_type, rule_id, detail, note FROM IncidentTriggers WHERE incident_id = $p0 ORDER BY ts, id;";
        command.Parameters.AddWithValue("$p0", incidentId);
        var triggers = new List<IncidentTrigger>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            triggers.Add(new IncidentTrigger(
                UnixTime.FromUnixMs(reader.GetInt64(0)),
                (IncidentSource)reader.GetInt32(1),
                reader.IsDBNull(2) ? null : (UserIncidentType)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return triggers;
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DBNull ? null : result;
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, object?[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("$p" + i.ToString(CultureInfo.InvariantCulture), values[i] ?? DBNull.Value);
        }

        return command;
    }
}
