using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Sqlite.BlackBox;

/// <summary>
/// The crash black box. While the recorder only watches (the default Manual mode), its minutes are in memory, so a crash lost
/// them: the dev PC crashed 12 times in 30 days (Oct–Nov 2026) and no crash incident had data from before it. Each watched
/// frame is also written here, compressed, with only the busiest processes (and their names) and without the inventory, and
/// the file keeps <see cref="RecorderOptions.PreIncidentMinutes"/> plus <see cref="Margin"/>. At the start the previous run's
/// file is moved aside (<see cref="DataPaths.PreviousBlackBoxDatabase"/>), so a crash's incident can read it however long
/// the PC was off; its frames then go through the same backfill as a watched capture. Recorded frames are not written: they
/// are in telemetry.db already. A failure is logged and never stops the recording.
/// </summary>
public sealed partial class BlackBoxStore(
    DataPaths paths,
    SqliteConnectionFactory connectionFactory,
    IOptionsMonitor<RecorderOptions> recorder,
    IOptionsMonitor<SamplingOptions> sampling,
    TimeProvider timeProvider,
    ILogger<BlackBoxStore> logger) : ISampleConsumer, IBlackBox, IAsyncDisposable
{
    /// <summary>Kept beyond the incident's pre-window: the seconds between the last heartbeat and the crash, and slack.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan TrimEvery = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<ProcessKey, ProcessIdentityDetails> _identities = [];
    private SqliteConnection? _connection;
    private SqliteCommand? _insert;
    private bool _rotated;
    private bool _failed;
    private DateTimeOffset _trimmedAt;

    /// <inheritdoc />
    public string Name => "black-box";

    /// <inheritdoc />
    public int QueueCapacity => 120;

    /// <inheritdoc />
    public async ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // Names are sent once, when a process appears: kept from every frame, so a frame written later can carry them.
        if (frame.Processes is { } processes)
        {
            Remember(processes);
        }

        if (!frame.Watched || !recorder.CurrentValue.BlackBox)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var insert = await InsertCommandAsync(cancellationToken).ConfigureAwait(false);
            if (insert is null)
            {
                return;
            }

            insert.Parameters["$ts"].Value = UnixTime.ToUnixMs(frame.Timestamp);
            insert.Parameters["$seq"].Value = frame.Sequence;
            insert.Parameters["$data"].Value = Pack(Compact(frame));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            if (now - _trimmedAt >= TrimEvery)
            {
                _trimmedAt = now;
                await using var trim = _connection!.CreateCommand();
                trim.CommandText = "DELETE FROM Frames WHERE ts < $cutoff;";
                trim.Parameters.AddWithValue("$cutoff", UnixTime.ToUnixMs(now - Keep()));
                await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SampleFrame>> ReadPreviousAsync(DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Rotate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The crash's incident is made without these minutes rather than not at all.
            LogReadFailed(logger, ex, paths.PreviousBlackBoxDatabase);
            return [];
        }
        finally
        {
            _gate.Release();
        }

        if (!File.Exists(paths.PreviousBlackBoxDatabase))
        {
            return [];
        }

        var frames = new List<SampleFrame>();
        try
        {
            // Read-write: the previous run may have ended before its write-ahead log was folded in. Not pooled, so the next
            // run can move the file.
            await using var connection = await connectionFactory.OpenReadWriteAsync(paths.PreviousBlackBoxDatabase, cancellationToken).ConfigureAwait(false);
            using var unpooled = new Unpooled(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT data FROM Frames WHERE ts >= $from AND ts <= $to ORDER BY ts, seq;";
            command.Parameters.AddWithValue("$from", UnixTime.ToUnixMs(since));
            command.Parameters.AddWithValue("$to", UnixTime.ToUnixMs(until));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Unpack((byte[])reader[0]) is { } frame)
                {
                    frames.Add(frame);
                }
            }
        }
        catch (SqliteException ex)
        {
            LogReadFailed(logger, ex, paths.PreviousBlackBoxDatabase);
        }

        return frames;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_insert is not null)
        {
            await _insert.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            SqliteConnection.ClearPool(_connection);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    /// <summary>A frame as stored: compressed JSON. Exposed for tests.</summary>
    internal static byte[] Pack(SampleFrame frame)
    {
        using var buffer = new MemoryStream();
        // Optimal: 11 KB a frame on the dev PC (Nov 2026) against 12.4 KB at Fastest, for 0.8 ms; frames are written one at a time,
        // so a crash loses none (five together would be 5.5 KB, but the last seconds would be lost).
        using (var brotli = new BrotliStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            JsonSerializer.Serialize(brotli, frame, BlackBoxJson.Default.SampleFrame);
        }

        return buffer.ToArray();
    }

    /// <summary>A stored frame back; null when it cannot be read (it is skipped). Exposed for tests.</summary>
    internal static SampleFrame? Unpack(byte[] data)
    {
        try
        {
            using var brotli = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
            return JsonSerializer.Deserialize(brotli, BlackBoxJson.Default.SampleFrame);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What a frame keeps: the processes stored for a recording (the busiest, with their names), no inventory.</summary>
    internal SampleFrame Compact(SampleFrame frame)
    {
        if (frame.Processes is not { } processes)
        {
            return frame with { Inventory = null };
        }

        var kept = ProcessSelection.Select(processes, frame.Gpu, sampling.CurrentValue.TopProcessCount).Select(p => p.Metrics).ToList();
        var names = kept.Select(p => _identities.GetValueOrDefault(p.Key)).OfType<ProcessIdentityDetails>().ToList();
        return frame with { Inventory = null, Processes = processes with { Processes = kept, Identities = names } };
    }

    private void Remember(ProcessSample processes)
    {
        foreach (var identity in processes.Identities)
        {
            _identities[identity.Key] = identity;
        }

        if (_identities.Count > processes.Processes.Count + 256)
        {
            var running = processes.Processes.Select(p => p.Key).ToHashSet();
            foreach (var gone in _identities.Keys.Where(k => !running.Contains(k)).ToList())
            {
                _identities.Remove(gone);
            }
        }
    }

    private TimeSpan Keep() => TimeSpan.FromMinutes(recorder.CurrentValue.PreIncidentMinutes) + Margin;

    private async Task<SqliteCommand?> InsertCommandAsync(CancellationToken cancellationToken)
    {
        if (_insert is not null || _failed)
        {
            return _insert;
        }

        Rotate();
        _connection = await connectionFactory.OpenReadWriteAsync(paths.BlackBoxDatabase, cancellationToken).ConfigureAwait(false);
        await using (var create = _connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS Frames (ts INTEGER NOT NULL, seq INTEGER NOT NULL, data BLOB NOT NULL, PRIMARY KEY (ts, seq)) WITHOUT ROWID;";
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _insert = _connection.CreateCommand();
        _insert.CommandText = "INSERT OR REPLACE INTO Frames (ts, seq, data) VALUES ($ts, $seq, $data);";
        _insert.Parameters.Add("$ts", SqliteType.Integer);
        _insert.Parameters.Add("$seq", SqliteType.Integer);
        _insert.Parameters.Add("$data", SqliteType.Blob);
        return _insert;
    }

    /// <summary>Once per run, before the first write: the previous run's file (with its write-ahead log) becomes the previous black box.</summary>
    private void Rotate()
    {
        if (_rotated)
        {
            return;
        }

        _rotated = true;
        if (!File.Exists(paths.BlackBoxDatabase))
        {
            return;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var previous = paths.PreviousBlackBoxDatabase + suffix;
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(paths.BlackBoxDatabase + suffix))
            {
                File.Move(paths.BlackBoxDatabase + suffix, paths.PreviousBlackBoxDatabase + suffix);
            }
        }
    }

    /// <summary>Takes a connection out of the pool when done, so its file is not held open (and can be moved).</summary>
    private sealed class Unpooled(SqliteConnection connection) : IDisposable
    {
        public void Dispose() => SqliteConnection.ClearPool(connection);
    }

    private void Fail(Exception exception)
    {
        if (!_failed)
        {
            _failed = true;
            LogWriteFailed(logger, exception, paths.BlackBoxDatabase);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The crash black box could not be written ({Path}); a crash while watching will have no minutes from before it")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The previous run's black box could not be read ({Path})")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string path);
}

/// <summary>Serialization metadata for the black box's frames (generated at compile time).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(SampleFrame))]
internal sealed partial class BlackBoxJson : JsonSerializerContext;
