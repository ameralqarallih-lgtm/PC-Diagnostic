using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Configuration;
using PcDiag.Storage.Sqlite.Migrations;

namespace PcDiag.Storage.Sqlite;

/// <summary>
/// Creates the data folders and brings telemetry.db and incidents.db to the latest schema.
/// A corrupt telemetry.db is quarantined and rebuilt (it is disposable); a corrupt incidents.db is never touched.
/// </summary>
public sealed partial class DatabaseInitializer(
    DataPaths paths,
    SqliteConnectionFactory connectionFactory,
    SchemaMigrator migrator,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    // SQLite primary result codes that indicate a damaged or foreign file.
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    /// <summary>Initializes all databases.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        paths.EnsureCreated();
        await InitializeTelemetryAsync(cancellationToken).ConfigureAwait(false);
        await MigrateAsync(paths.IncidentIndexDatabase, DatabaseKind.IncidentIndex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates or migrates one incident's database.</summary>
    public Task<int> InitializeIncidentDatabaseAsync(string path, CancellationToken cancellationToken) =>
        MigrateAsync(path, DatabaseKind.Incident, cancellationToken);

    private async Task InitializeTelemetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await MigrateAsync(paths.TelemetryDatabase, DatabaseKind.Telemetry, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            var quarantined = Quarantine(paths.TelemetryDatabase);
            LogTelemetryQuarantined(logger, ex, paths.TelemetryDatabase, quarantined);
            await MigrateAsync(paths.TelemetryDatabase, DatabaseKind.Telemetry, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<int> MigrateAsync(string path, DatabaseKind kind, CancellationToken cancellationToken)
    {
        var connection = await connectionFactory.OpenReadWriteAsync(path, cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            var version = await migrator.MigrateAsync(connection, kind, cancellationToken).ConfigureAwait(false);
            LogDatabaseReady(logger, kind, path, version);
            return version;
        }
    }

    private string Quarantine(string path)
    {
        // Pooled connections keep the file open; release them before renaming (only this file's: clearing every
        // pool can close a connection another thread is taking).
        connectionFactory.ReleasePooledConnections(path);

        var suffix = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var target = Path.ChangeExtension(path, $".corrupt-{suffix}.db");
        File.Move(path, target);

        foreach (var sidecar in new[] { "-wal", "-shm" })
        {
            if (File.Exists(path + sidecar))
            {
                File.Move(path + sidecar, target + sidecar);
            }
        }

        return target;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} database ready at {Path} (schema version {Version})")]
    private static partial void LogDatabaseReady(ILogger logger, DatabaseKind kind, string path, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry database {Path} was damaged; moved it to {QuarantinedPath} and created a new one. Incidents are not affected.")]
    private static partial void LogTelemetryQuarantined(ILogger logger, Exception exception, string path, string quarantinedPath);
}
