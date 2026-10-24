using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PcDiag.Storage.Sqlite.Migrations;

/// <summary>Brings a database up to the latest schema version for its <see cref="DatabaseKind"/>.</summary>
public sealed partial class SchemaMigrator(ILogger<SchemaMigrator> logger)
{
    /// <summary>
    /// Applies all pending migrations, each in its own transaction, and returns the resulting version.
    /// </summary>
    /// <exception cref="SchemaVersionException">The database was written by a newer version of the application.</exception>
    public async Task<int> MigrateAsync(SqliteConnection connection, DatabaseKind kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var current = await GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        var latest = MigrationCatalog.LatestVersion(kind);

        if (current > latest)
        {
            throw new SchemaVersionException(connection.DataSource, kind, current, latest);
        }

        foreach (var migration in MigrationCatalog.For(kind).Where(m => m.Version > current))
        {
            var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                foreach (var script in migration.ScriptNames)
                {
                    await SqliteConnectionFactory.ExecuteAsync(connection, MigrationCatalog.LoadScript(script), cancellationToken, transaction).ConfigureAwait(false);
                }

                await SetVersionAsync(connection, transaction, migration.Version, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            LogMigrationApplied(logger, kind, migration.Version, migration.Description, connection.DataSource);
            current = migration.Version;
        }

        return current;
    }

    /// <summary>Reads the schema version; 0 means an empty database.</summary>
    public static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'SchemaVersion';";
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            return 0;
        }

        command.CommandText = "SELECT version FROM SchemaVersion LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task SetVersionAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS SchemaVersion (version INTEGER NOT NULL);
            DELETE FROM SchemaVersion;
            INSERT INTO SchemaVersion (version) VALUES ($version);
            """;
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Kind} schema migration {Version} ({Description}) to {Path}")]
    private static partial void LogMigrationApplied(ILogger logger, DatabaseKind kind, int version, string description, string path);
}

/// <summary>Thrown when a database has a newer schema than this version of the application understands.</summary>
public sealed class SchemaVersionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SchemaVersionException(string path, DatabaseKind kind, int foundVersion, int supportedVersion)
        : base($"The {kind} database '{path}' has schema version {foundVersion}, but this version of PC Diagnostic supports up to {supportedVersion}. " +
               "It was probably written by a newer version of the application; update the application instead of downgrading.")
    {
        Path = path;
        Kind = kind;
        FoundVersion = foundVersion;
        SupportedVersion = supportedVersion;
    }

    /// <summary>Creates the exception (serialization/analyzer convention).</summary>
    public SchemaVersionException()
        : this(string.Empty, DatabaseKind.Telemetry, 0, 0)
    {
    }

    /// <summary>Creates the exception with a custom message.</summary>
    public SchemaVersionException(string message)
        : base(message)
    {
        Path = string.Empty;
    }

    /// <summary>Creates the exception with a custom message and inner exception.</summary>
    public SchemaVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Path = string.Empty;
    }

    /// <summary>Database file path.</summary>
    public string Path { get; }

    /// <summary>Database kind.</summary>
    public DatabaseKind Kind { get; }

    /// <summary>Schema version found in the file.</summary>
    public int FoundVersion { get; }

    /// <summary>Latest schema version this build supports.</summary>
    public int SupportedVersion { get; }
}
