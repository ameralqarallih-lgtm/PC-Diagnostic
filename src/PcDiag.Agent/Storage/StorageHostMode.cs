using System.IO.Pipes;
using System.Text;
using PcDiag.Core.Configuration;
using PcDiag.Hosting;
using PcDiag.Storage.Files;

namespace PcDiag.Agent.Storage;

/// <summary>
/// "PcDiag.Agent.exe storage-host --pipe NAME --token TOKEN --data FOLDER": the storage host, started by the recorder
/// when it starts, without administrator rights (so files are written to folders and services as the signed-in user,
/// never as administrator). It runs the file storage's upkeep (<see cref="StorageUpkeep"/>): it sets the storage up
/// on its first start, carries out unfinished changes, brings the storage locations up to date and keeps backups of
/// the on-screen layout, whether the dashboard is open or not. It looks after PC Diagnostic Storage, SeaweedFS run on
/// this PC (<see cref="LocalObjectStoreHost"/>), and sets it up on the first start when the installer shipped it
/// (<see cref="LocalStoreSetup"/>). It ends when the recorder goes; one runs at a time.
/// </summary>
internal static class StorageHostMode
{
    /// <summary>The command-line verb.</summary>
    public const string Verb = "storage-host";

    /// <summary>Whether the arguments ask for the storage host.</summary>
    public static bool IsCommand(string[] args) => args.Length > 0 && string.Equals(args[0], Verb, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs until the recorder goes (0); 64 for bad arguments, 1 when it cannot connect, 2 when its upkeep stopped,
    /// 3 when another storage host is running for this data folder.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        if (Arg("--pipe") is not { } pipeName || Arg("--token") is not { } token)
        {
            return 64;
        }

        DataPaths paths;
        try
        {
            paths = new DataPaths(Arg("--data"));
        }
        catch (ArgumentException)
        {
            return 64;
        }

        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(10_000, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(token + "\n"), cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return 1;
        }

        // One storage host at a time for a data folder.
        var folder = Path.Combine(paths.Root, "storage");
        Directory.CreateDirectory(folder);
        FileStream single;
        try
        {
            single = new FileStream(Path.Combine(folder, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return 3;
        }

        using (single)
        using (var loggers = PcDiagHostBuilderExtensions.CreateLoggerFactory(paths, "storage"))
        using (var store = new FileStore(paths, loggers.CreateLogger<FileStore>()))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var logger = loggers.CreateLogger("PcDiag.Agent.Storage.StorageHost");
            HostLog.Started(logger, Environment.ProcessId, paths.Root);
            var upkeep = new StorageUpkeep(store, paths.OverlayFile, logger).RunAsync(stop.Token);
            var recorder = UntilClosedAsync(pipe, stop.Token);

            // PC Diagnostic Storage: looked after from now on, and set up on a first start when it was installed.
            using var local = new LocalObjectStoreHost(store, paths.Root, loggers.CreateLogger<LocalObjectStoreHost>());
            var looking = local.RunAsync(stop.Token);
            var setup = new LocalStoreSetup(store, paths.Root, loggers.CreateLogger<LocalStoreSetup>());
            _ = setup.SetUpOnFirstStartAsync(stop.Token);

            var first = await Task.WhenAny(upkeep, recorder).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await looking.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped with the storage host (SeaweedFS with it).
            }

            try
            {
                await upkeep.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Ended with the recorder.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HostLog.Stopped(logger, ex);
                return 2;
            }

            HostLog.Ended(logger, first == recorder ? "the recorder stopped" : "the upkeep ended");
        }

        return 0;
    }

    /// <summary>Waits until the recorder's end of the pipe closes (the recorder stopped).</summary>
    private static async Task UntilClosedAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[64];
        try
        {
            while (await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
                // The recorder sends nothing; reading notices when it goes.
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Gone.
        }
    }
}

/// <summary>The storage host's log lines.</summary>
internal static partial class HostLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Storage host started (pid {Pid}, data {Root})")]
    public static partial void Started(ILogger logger, int pid, string root);

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage host ended: {Why}")]
    public static partial void Ended(ILogger logger, string why);

    [LoggerMessage(Level = LogLevel.Error, Message = "The storage upkeep stopped")]
    public static partial void Stopped(ILogger logger, Exception exception);
}
