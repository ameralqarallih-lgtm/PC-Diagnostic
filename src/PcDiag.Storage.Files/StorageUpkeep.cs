using Microsoft.Extensions.Logging;

namespace PcDiag.Storage.Files;

/// <summary>
/// The storage's own upkeep, run by the storage host from the moment the recorder starts (no dashboard needed):
/// <list type="bullet">
/// <item>sets the storage up on its first start (This PC's folders and the locations file);</item>
/// <item>carries out a change left unfinished (the store resumes it when it is made);</item>
/// <item>brings the locations up to date every 15 minutes, so a share or a service that was away gets what it missed;</item>
/// <item>keeps a backup of the on-screen layout as it was before it changed outside the editor (arranged or closed on
/// the screens), by the same rules as the editor's saves.</item>
/// </list>
/// </summary>
public sealed partial class StorageUpkeep(FileStore store, string layoutFile, ILogger logger)
{
    /// <summary>How often the locations are brought up to date.</summary>
    public static readonly TimeSpan SyncEvery = TimeSpan.FromMinutes(15);

    /// <summary>How often the locations are brought up to date (shorter in tests).</summary>
    internal TimeSpan SyncInterval { get; init; } = SyncEvery;

    /// <summary>How long the layout file has to stay unchanged before it is read (the recorder writes it in steps).</summary>
    internal TimeSpan Settle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Runs until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        store.Initialize();
        LogStarted(logger, store.Locations.Count);

        var folder = Path.GetDirectoryName(Path.GetFullPath(layoutFile))!;
        Directory.CreateDirectory(folder);
        using var changed = new SemaphoreSlim(0, 1);
        void Signal(object? sender, FileSystemEventArgs e)
        {
            try
            {
                changed.Release();
            }
            catch (SemaphoreFullException)
            {
                // Already signalled.
            }
            catch (ObjectDisposedException)
            {
                // Stopping.
            }
        }

        using var watcher = new FileSystemWatcher(folder, Path.GetFileName(layoutFile)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        watcher.Changed += Signal;
        watcher.Created += Signal;
        watcher.Renamed += Signal;
        watcher.EnableRaisingEvents = true;

        // The layout as it is now: what a change is compared with (and kept as the backup "before").
        var last = await LayoutBackups.ReadAsync(layoutFile, cancellationToken).ConfigureAwait(false);
        var nextSync = DateTime.UtcNow + SyncInterval;
        while (true)
        {
            var wait = nextSync - DateTime.UtcNow;
            if (wait > TimeSpan.Zero && await changed.WaitAsync(wait, cancellationToken).ConfigureAwait(false))
            {
                while (await changed.WaitAsync(Settle, cancellationToken).ConfigureAwait(false))
                {
                    // Still being written.
                }

                var now = await LayoutBackups.ReadAsync(layoutFile, cancellationToken).ConfigureAwait(false);
                if (now is not null && last is not null && !now.AsSpan().SequenceEqual(last)
                    && await LayoutBackups.KeepAsync(store, last, always: false, logger, cancellationToken).ConfigureAwait(false))
                {
                    LogLayoutKept(logger);
                }

                last = now ?? last;
                continue;
            }

            nextSync = DateTime.UtcNow + SyncInterval;
            if (store.Locations.Count > 1)
            {
                try
                {
                    if (await store.SyncAsync(cancellationToken).ConfigureAwait(false) is { } problem)
                    {
                        LogSyncProblem(logger, problem);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    LogSyncProblem(logger, ex.Message);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage upkeep started ({Locations} storage locations)")]
    private static partial void LogStarted(ILogger logger, int locations);

    [LoggerMessage(Level = LogLevel.Information, Message = "The on-screen layout changed outside the editor: the layout before was kept as a backup")]
    private static partial void LogLayoutKept(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage locations were not all brought up to date: {Problem}")]
    private static partial void LogSyncProblem(ILogger logger, string problem);
}
