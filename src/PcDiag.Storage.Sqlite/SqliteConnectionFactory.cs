using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace PcDiag.Storage.Sqlite;

/// <summary>Opens SQLite connections with the application's standard pragmas.</summary>
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected service; instance API allows substitution in tests.")]
public sealed class SqliteConnectionFactory
{
    /// <summary>Milliseconds a connection waits for a lock before failing with SQLITE_BUSY.</summary>
    public const int BusyTimeoutMs = 5000;

    /// <summary>Page cache limit per connection, in KiB.</summary>
    public const int CacheSizeKib = 8192;

    /// <summary>
    /// The write-ahead log is cut back to this size whenever a checkpoint resets it (without a limit it keeps the size
    /// of its largest moment, such as a long clean-up).
    /// </summary>
    public const long JournalSizeLimitBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Opens a read-write connection, creating the file if needed. Enables incremental auto-vacuum (new files only),
    /// WAL (so the Web host can read while the Agent writes), NORMAL synchronous mode, in-memory temp storage, a
    /// bounded write-ahead log and a bounded page cache.
    /// </summary>
    public async Task<SqliteConnection> OpenReadWriteAsync(string path, CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(path, SqliteOpenMode.ReadWriteCreate, cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(
                connection,
                // auto_vacuum only takes effect before the file's first page is written, and switching to WAL writes it,
                // so it must come first. On existing files it is a harmless no-op.
                "PRAGMA auto_vacuum = INCREMENTAL; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; "
                + $"PRAGMA journal_size_limit = {JournalSizeLimitBytes};",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens a read-only connection. Fails if the database does not exist.</summary>
    public Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken) =>
        OpenAsync(path, SqliteOpenMode.ReadOnly, cancellationToken);

    /// <summary>
    /// Closes this process's idle pooled connections (read-only and read-write) to a file, so a change that needs the
    /// file to itself (switching the journal mode, deleting it) is not blocked by connections nobody is using.
    /// </summary>
    public void ReleasePooledConnections(string path)
    {
        foreach (var mode in new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWriteCreate })
        {
            using var connection = new SqliteConnection(ConnectionString(path, mode));
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Closes this process's idle pooled connections to every database in a folder and its subfolders (before the
    /// folder is moved or deleted). Unlike <see cref="SqliteConnection.ClearAllPools"/> it leaves other files' pools
    /// alone: clearing a pool while another thread takes a connection from it can hand that thread a closed one.
    /// </summary>
    public void ReleasePooledConnectionsUnder(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.db", SearchOption.AllDirectories))
        {
            ReleasePooledConnections(file);
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = mode,
        Cache = SqliteCacheMode.Private,
        Pooling = true,
    }.ToString();

    private static async Task<SqliteConnection> OpenAsync(string path, SqliteOpenMode mode, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var connection = new SqliteConnection(ConnectionString(path, mode));
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                $"PRAGMA busy_timeout = {BusyTimeoutMs}; PRAGMA foreign_keys = ON; PRAGMA cache_size = -{CacheSizeKib};",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
