using Microsoft.Extensions.Logging;

namespace PcDiag.Storage.Files;

/// <summary>
/// Sets PC Diagnostic Storage up (the Storage page's "Set it up", and on the first start when the installer shipped
/// SeaweedFS), the steps of Video Clipper's: the program (shipped, or downloaded once and checked), free ports, keys made
/// up for it, the storage location (where files are kept by default unless asked otherwise), then SeaweedFS started
/// and checked by writing, reading and deleting a file in its bucket. When a step fails nothing is kept (the location
/// and its keys are taken out again) and the page says why. One at a time.
/// </summary>
public sealed partial class LocalStoreSetup
{
    private readonly FileStore store;
    private readonly string root;
    private readonly Func<HttpClient> http;
    private readonly ILogger logger;
    private readonly string? baseDirectory;
    private readonly Lock _lock = new();

    /// <summary>For the storage host (no download there: it sets up only what the installer shipped, or what is here).</summary>
    public LocalStoreSetup(FileStore store, string root, ILogger logger, string? baseDirectory = null)
        : this(store, root, () => new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, logger, baseDirectory)
    {
    }

    /// <param name="store">The storage locations.</param>
    /// <param name="root">The data folder.</param>
    /// <param name="http">Makes the client the program is downloaded with.</param>
    /// <param name="logger">Its log.</param>
    /// <param name="baseDirectory">Where the shipped program is looked for (tests give another).</param>
    public LocalStoreSetup(FileStore store, string root, Func<HttpClient> http, ILogger logger, string? baseDirectory = null)
    {
        this.store = store;
        this.root = root;
        this.http = http;
        this.logger = logger;
        this.baseDirectory = baseDirectory;
    }
    private LocalStoreSetupState _state = new(false, null, 0, null, null);

    /// <summary>How long SeaweedFS may take to be started by the process looking after it.</summary>
    internal TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A marker: it was set up automatically once (or removed by the user), so it is not set up again by itself.</summary>
    private string DoneMarker => Path.Combine(root, "storage", "local-store.auto");

    /// <summary>How far it is.</summary>
    public LocalStoreSetupState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Starts setting it up in the background; false when it is under way or set up already.</summary>
    public bool Start(string? folder, bool makeDefault)
    {
        lock (_lock)
        {
            if (_state.Running || store.Locations.Any(l => l.Local is not null))
            {
                return false;
            }

            _state = new LocalStoreSetupState(true, "Getting ready", 0, null, null);
        }

        _ = Task.Run(() => RunAsync(folder, makeDefault, CancellationToken.None));
        return true;
    }

    /// <summary>
    /// On a first start with SeaweedFS shipped (installed by the installer): set up as the default location, unless it
    /// was set up before or the user removed it.
    /// </summary>
    public async Task<bool> SetUpOnFirstStartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(DoneMarker) || LocalObjectStore.BundledExe(baseDirectory) is null || store.Locations.Any(l => l.Local is not null))
        {
            return false;
        }

        lock (_lock)
        {
            if (_state.Running)
            {
                return false;
            }

            _state = new LocalStoreSetupState(true, "Getting ready", 0, null, null);
        }

        var problem = await RunAsync(null, makeDefault: true, cancellationToken).ConfigureAwait(false);
        if (problem is null)
        {
            Mark();
        }

        return problem is null;
    }

    /// <summary>Remembers it should not be set up by itself again (after a set-up, or when the user removes it).</summary>
    public void Mark()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DoneMarker)!);
            File.WriteAllText(DoneMarker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Asked again at the next start.
        }
    }

    private void Set(string step, double done)
    {
        lock (_lock)
        {
            _state = _state with { Step = step, Done = done };
        }
    }

    /// <summary>The whole set-up; null when done, otherwise why not (and nothing kept).</summary>
    private async Task<string?> RunAsync(string? folder, bool makeDefault, CancellationToken cancellationToken)
    {
        StorageLocation? added = null;
        string? problem;
        try
        {
            problem = await SetUpAsync(folder, makeDefault, l => added = l, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or InvalidDataException or InvalidOperationException)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            if (added is not null)
            {
                store.Forget(added.Id);
            }

            LogFailed(logger, problem);
        }
        else
        {
            LogDone(logger, added?.Local?.S3Port ?? 0);
        }

        lock (_lock)
        {
            _state = new LocalStoreSetupState(false, problem is null ? "Done" : null, problem is null ? 1 : 0, problem, DateTimeOffset.UtcNow);
        }

        return problem;
    }

    private async Task<string?> SetUpAsync(string? folder, bool makeDefault, Action<StorageLocation> added, CancellationToken cancellationToken)
    {
        if (store.Locations.Any(l => l.Local is not null))
        {
            return "PC Diagnostic Storage is set up already.";
        }

        folder = string.IsNullOrWhiteSpace(folder) ? LocalObjectStore.DefaultFolder(root) : folder.Trim();
        if (LocalObjectStore.CheckFolder(folder) is { } bad)
        {
            return bad;
        }

        // 1. The program: shipped, downloaded before, or downloaded now (most of the time this takes).
        if (LocalObjectStore.Exe(root, baseDirectory) is null)
        {
            Set("Downloading SeaweedFS (once, about 47 MB)", 0.02);
            using var client = http();
            await LocalObjectStore.DownloadAsync(root, client, new Progress<double>(d => Set("Downloading SeaweedFS (once, about 47 MB)", 0.02 + d * 0.78)), cancellationToken).ConfigureAwait(false);
        }

        // 2. Ports, keys and the location; the process looking after it starts it.
        Set("Starting it", 0.82);
        var server = LocalObjectStore.PickPorts(folder);
        var keys = LocalObjectStore.NewKeys();
        var location = store.AddLocal(server, keys, makeDefault ? FileKinds.All : [FileKinds.Backups, FileKinds.Exports]);
        added(location);
        var until = DateTime.UtcNow + StartTimeout;
        LocalStoreState? state = null;
        while (DateTime.UtcNow < until)
        {
            state = LocalObjectStoreHost.Read(root);
            if (state is { Running: true } && state.Port == server.S3Port)
            {
                break;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (state is not { Running: true } || state.Port != server.S3Port)
        {
            return state?.Problem ?? "PC Diagnostic Storage did not start within a minute (the storage host may not be running).";
        }

        // 3. Checked: a file written, read back and deleted in its bucket (made as it starts: tried for a moment).
        Set("Checking it", 0.92);
        string? problem = null;
        for (var i = 0; i < 20; i++)
        {
            problem = await store.TestAsync(location, keys, cancellationToken).ConfigureAwait(false);
            if (problem is null)
            {
                break;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        if (problem is not null)
        {
            return "PC Diagnostic Storage started but a file could not be kept in it: " + problem;
        }

        // 4. Where files are kept by default (the files there so far are copied to it).
        if (makeDefault && store.SetDefault(location.Id, moveFromPrevious: true) is { } notDefault)
        {
            return notDefault;
        }

        store.StartSync();
        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic Storage was set up (S3 on 127.0.0.1:{Port})")]
    private static partial void LogDone(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PC Diagnostic Storage was not set up: {Problem}")]
    private static partial void LogFailed(ILogger logger, string problem);
}
