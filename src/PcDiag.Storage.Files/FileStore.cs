using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Configuration;

namespace PcDiag.Storage.Files;

/// <summary>
/// Where files are kept, like Video Clipper's storage: storage locations (This PC, folders, S3-compatible services),
/// each keeping some kinds of file. A file is written to every location that keeps its kind and read from the first
/// that has it; every kind is kept somewhere, and what the recorder reads (pictures, driver packages) is always kept on
/// This PC. After a location is added or changed, a background job copies to it what it should keep (checking each
/// copy's size); a location removed first gives its files to the others. Locations are saved in the data folder, an S3
/// service's keys protected for this Windows user (never sent to the browser).
/// <para>
/// Two processes use it, both without administrator rights: the Web host (the page, uploads, layout saves) and the
/// storage host the recorder starts with Windows (<see cref="StorageUpkeep"/>). They share its files: each takes in
/// the locations and keys the other saved, one job runs at a time (a lock file), and the job's state and the problems
/// seen are shared, the newest winning.
/// </para>
/// </summary>
public sealed partial class FileStore : IDisposable
{
    private static readonly byte[] Entropy = "PcDiag.Storage.Keys.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _root;
    private readonly string _pictures;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (string Signature, IStorageProvider Provider)> _providers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly Dictionary<string, string?> _problems = new(StringComparer.Ordinal);
    private List<StorageLocation> _locations;
    private Dictionary<string, StorageKeys> _keys;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private StorageJob _job = new(false, 0, 0, 0, null, null);
    private DateTimeOffset _stateAt = DateTimeOffset.MinValue;
    private string _stamp = string.Empty;

    /// <summary>The store for the data folder (its pictures where the recorder reads them).</summary>
    public FileStore(DataPaths paths, ILogger<FileStore> logger)
        : this(paths?.Root ?? throw new ArgumentNullException(nameof(paths)), paths.OverlayImagesDirectory, logger)
    {
    }

    internal FileStore(string root, string picturesFolder, ILogger logger)
    {
        _root = root;
        _pictures = picturesFolder;
        _logger = logger;
        _stamp = Stamp();
        _locations = LoadLocations();
        _keys = LoadKeys();

        // A change not yet carried out (the program stopped meanwhile): carried out now.
        if (File.Exists(PendingFile))
        {
            StartSync();
        }
    }

    private string Folder => Path.Combine(_root, "storage");

    private string LocationsFile => Path.Combine(Folder, "locations.json");

    private string KeysFile => Path.Combine(Folder, "keys.dat");

    private string PendingFile => Path.Combine(Folder, "sync-pending");

    private string StateFile => Path.Combine(Folder, "state.json");

    private string SyncLockFile => Path.Combine(Folder, "sync.lock");

    /// <summary>How long a job waits for the other process's job before going ahead anyway (copies can be repeated safely).</summary>
    internal TimeSpan LockPatience { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>This PC's own location: everything by default.</summary>
    public static StorageLocation ThisPc { get; } = new("pc", "This PC", LocationKinds.ThisPc, FileKinds.All, Default: true);

    /// <summary>The locations, This PC first, then in their order.</summary>
    public IReadOnlyList<StorageLocation> Locations
    {
        get
        {
            Refresh();
            lock (_lock)
            {
                return [.. _locations];
            }
        }
    }

    /// <summary>The job bringing the locations up to date (this process's or the other's, whichever is newer).</summary>
    public StorageJob Job
    {
        get
        {
            var shared = SharedState();
            lock (_lock)
            {
                return shared is not null && shared.At > _stateAt ? shared.Job : _job;
            }
        }
    }

    /// <summary>The locations as the browser sees them (keys only as set, with their last four characters).</summary>
    public IReadOnlyList<LocationView> Views()
    {
        Refresh();
        var shared = SharedState();
        lock (_lock)
        {
            var problems = shared is not null && shared.At > _stateAt ? shared.Problems : _problems;
            return [.. _locations.Select(l =>
            {
                var keys = _keys.GetValueOrDefault(l.Id);
                var problem = problems.GetValueOrDefault(l.Id);
                return new LocationView(l, keys is not null, keys is { AccessKey.Length: >= 4 } k ? k.AccessKey[^4..] : null, problem is null, problem);
            })];
        }
    }

    /// <summary>Sets the storage up on its first start: This PC's folders and the locations file.</summary>
    public void Initialize()
    {
        Refresh();
        lock (_lock)
        {
            Directory.CreateDirectory(_pictures);
            foreach (var kind in FileKinds.All.Where(k => k is not (FileKinds.Pictures or FileKinds.Characters)))
            {
                Directory.CreateDirectory(Path.Combine(_root, "files", kind));
            }

            if (!File.Exists(LocationsFile))
            {
                Save();
            }
        }
    }

    // ---- Files -------------------------------------------------------------------------------------------------

    /// <summary>Keeps a file in every location that keeps its kind; the stored file (with its SHA-256).</summary>
    public async Task<StoredFile> PutAsync(string kind, string name, Stream content, CancellationToken cancellationToken)
    {
        Check(kind, name);
        ArgumentNullException.ThrowIfNull(content);
        var temp = Path.Combine(Path.GetTempPath(), $"pcdiag-store-{Guid.NewGuid():N}.tmp");
        try
        {
            string sha;
            long size;
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                size = file.Length;
            }

            await PutEverywhereAsync(kind, name, temp, sha, skip: null, cancellationToken).ConfigureAwait(false);
            return new StoredFile(kind, name, size, DateTimeOffset.UtcNow, sha);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>
    /// A file already on This PC (a picture the Web host wrote where the recorder reads it): copied to the other
    /// locations that keep its kind.
    /// </summary>
    public async Task MirrorAsync(string kind, string name, string path, CancellationToken cancellationToken)
    {
        Check(kind, name);
        if (Locations.All(l => l.Id == ThisPc.Id || !l.Keeps.Contains(kind)))
        {
            return;
        }

        string sha;
        await using (var file = File.OpenRead(path))
        {
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
        }

        await PutEverywhereAsync(kind, name, path, sha, skip: ThisPc.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A file's content from the first location that has it; null when none does.</summary>
    public async Task<Stream?> OpenAsync(string kind, string name, CancellationToken cancellationToken)
    {
        Check(kind, name);
        foreach (var location in Locations.Where(l => l.Keeps.Contains(kind)))
        {
            try
            {
                if (await Provider(location).OpenAsync(kind, name, cancellationToken).ConfigureAwait(false) is { } stream)
                {
                    return stream;
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                Problem(location, ex);
            }
        }

        return null;
    }

    /// <summary>The files of a kind in any location that keeps it (newest first).</summary>
    public async Task<IReadOnlyList<StoredFile>> ListAsync(string kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        var files = new Dictionary<string, StoredFile>(StringComparer.Ordinal);
        foreach (var location in Locations.Where(l => l.Keeps.Contains(kind)))
        {
            try
            {
                foreach (var file in await Provider(location).ListAsync(kind, cancellationToken).ConfigureAwait(false))
                {
                    files.TryAdd(file.Name, file);
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                Problem(location, ex);
            }
        }

        return [.. files.Values.OrderByDescending(f => f.Updated)];
    }

    /// <summary>Removes a file from every location (one that cannot be reached keeps it).</summary>
    public async Task DeleteAsync(string kind, string name, CancellationToken cancellationToken)
    {
        Check(kind, name);
        foreach (var location in Locations.Where(l => l.Keeps.Contains(kind)))
        {
            try
            {
                await Provider(location).DeleteAsync(kind, name, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                Problem(location, ex);
            }
        }
    }

    // ---- Locations ---------------------------------------------------------------------------------------------

    /// <summary>Whether a location (not saved yet) works: null when it does, otherwise why not.</summary>
    public async Task<string?> TestAsync(StorageLocation location, StorageKeys? keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (Shape(location) is { } problem)
        {
            return problem;
        }

        keys ??= _keys.GetValueOrDefault(location.Id);
        if (location.Kind == LocationKinds.S3 && keys is null)
        {
            return "Give the service's access key and secret key.";
        }

        using var provider = Make(location, keys);
        return await provider.TestAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds a location (after testing it); its files come in the background.</summary>
    public async Task<(StorageLocation? Location, string? Problem)> AddAsync(StorageLocation candidate, StorageKeys? keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Kind is not (LocationKinds.Folder or LocationKinds.S3))
        {
            return (null, "A location is a folder or an S3-compatible service.");
        }

        Refresh();
        var location = candidate with { Id = "loc-" + Guid.NewGuid().ToString("N")[..10], Keeps = [.. candidate.Keeps.Distinct().Where(FileKinds.All.Contains)] };
        if (await TestAsync(location, keys, cancellationToken).ConfigureAwait(false) is { } problem)
        {
            return (null, problem);
        }

        lock (_lock)
        {
            _locations = [.. _locations, location];
            if (keys is not null)
            {
                _keys[location.Id] = keys;
            }

            Save();
        }

        StartSync();
        return (location, null);
    }

    /// <summary>Changes a location's name, what it keeps, or its keys; null when done, otherwise why not.</summary>
    public string? Update(string id, string? name, IReadOnlyList<string>? keeps, StorageKeys? keys)
    {
        Refresh();
        lock (_lock)
        {
            var index = _locations.FindIndex(l => l.Id == id);
            if (index < 0)
            {
                return "There is no such location.";
            }

            var location = _locations[index];
            var kept = keeps is null ? location.Keeps : [.. keeps.Distinct().Where(FileKinds.All.Contains)];
            if (location.Default)
            {
                // The default location keeps every kind.
                kept = FileKinds.All;
            }
            else if (location.Id == ThisPc.Id)
            {
                // What the recorder reads stays on this PC.
                kept = [.. FileKinds.All.Where(k => FileKinds.OnThisPc.Contains(k) || kept.Contains(k))];
            }

            var next = _locations.ToList();
            next[index] = location with { Name = string.IsNullOrWhiteSpace(name) || location.Id == ThisPc.Id ? location.Name : name.Trim()[..Math.Min(60, name.Trim().Length)], Keeps = kept };
            if (Unkept(next) is { } kind)
            {
                return $"Every kind of file is kept somewhere: nothing else keeps {kind}.";
            }

            _locations = next;
            if (keys is not null && location.Kind == LocationKinds.S3)
            {
                _keys[id] = keys;
            }

            Save();
        }

        StartSync();
        return null;
    }

    /// <summary>The location files are kept in by default (This PC when no other is).</summary>
    public StorageLocation Default => Locations.FirstOrDefault(l => l.Default) ?? Locations[0];

    /// <summary>
    /// Makes a location where files are kept by default: it keeps every kind. With <paramref name="moveFromPrevious"/>
    /// the previous default stops keeping them (This PC keeps what the recorder reads; another location keeps nothing
    /// until told to): their files are copied to the new default first, by the job. Null when done, otherwise why not.
    /// </summary>
    public string? SetDefault(string id, bool moveFromPrevious)
    {
        Refresh();
        lock (_lock)
        {
            var index = _locations.FindIndex(l => l.Id == id);
            if (index < 0)
            {
                return "There is no such location.";
            }

            var previous = _locations.FirstOrDefault(l => l.Default) ?? _locations[0];
            var next = _locations.Select(l => l with { Default = l.Id == id }).ToList();
            next[index] = next[index] with { Keeps = FileKinds.All };
            if (moveFromPrevious && previous.Id != id)
            {
                var at = next.FindIndex(l => l.Id == previous.Id);
                next[at] = next[at] with { Keeps = previous.Id == ThisPc.Id ? [.. FileKinds.All.Where(FileKinds.OnThisPc.Contains)] : [] };
            }

            _locations = next;
            Save();
        }

        StartSync();
        return null;
    }

    /// <summary>
    /// Adds the object storage run on this PC as a location (not tested: it is not running yet; the set-up tests it
    /// once it is). Its files come when the set-up has checked it.
    /// </summary>
    public StorageLocation AddLocal(LocalServer server, StorageKeys keys, IReadOnlyList<string> keeps)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(keys);
        Refresh();
        lock (_lock)
        {
            if (_locations.FirstOrDefault(l => l.Local is not null) is { } existing)
            {
                return existing;
            }

            var location = LocalObjectStore.LocationFor("local", server, [.. keeps.Distinct().Where(FileKinds.All.Contains)], isDefault: false);
            _locations = [.. _locations, location];
            _keys[location.Id] = keys;
            Save();
            return location;
        }
    }

    /// <summary>Takes a location out without moving its files (a set-up that did not finish).</summary>
    public void Forget(string id)
    {
        Refresh();
        lock (_lock)
        {
            if (id == ThisPc.Id || _locations.All(l => l.Id != id))
            {
                return;
            }

            var wasDefault = _locations.First(l => l.Id == id).Default;
            _locations = [.. _locations.Where(l => l.Id != id)];
            if (wasDefault)
            {
                _locations[0] = _locations[0] with { Default = true, Keeps = FileKinds.All };
            }

            _keys.Remove(id);
            if (_providers.Remove(id, out var provider))
            {
                provider.Provider.Dispose();
            }

            Save();
        }
    }

    /// <summary>Stops the object storage on this PC, or starts it again (the process looking after it does). Null when done.</summary>
    public string? SetLocalStopped(bool stopped)
    {
        Refresh();
        lock (_lock)
        {
            var index = _locations.FindIndex(l => l.Local is not null);
            if (index < 0)
            {
                return "PC Diagnostic Storage is not set up.";
            }

            _locations[index] = _locations[index] with { Local = _locations[index].Local! with { Stopped = stopped } };
            Save();
        }

        if (!stopped)
        {
            StartSync();
        }

        return null;
    }

    /// <summary>A location's keys (for the object storage on this PC, which is given them as it starts).</summary>
    internal StorageKeys? KeysOf(string id)
    {
        Refresh();
        lock (_lock)
        {
            return _keys.GetValueOrDefault(id);
        }
    }

    /// <summary>Puts the locations in this order (This PC stays first); unknown ids are ignored.</summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Refresh();
        lock (_lock)
        {
            var others = _locations.Where(l => l.Id != ThisPc.Id).OrderBy(l => ids.ToList().IndexOf(l.Id) is var i and >= 0 ? i : int.MaxValue).ToList();
            _locations = [_locations[0], .. others];
            Save();
        }
    }

    /// <summary>
    /// Removes a location: its files first go to the other locations that keep their kinds (it keeps its own copies;
    /// nothing is deleted there). With <paramref name="force"/>, removed even when they cannot all be copied (it cannot
    /// be reached): what only it has stays there. Null when done, otherwise why not.
    /// </summary>
    public async Task<string?> RemoveAsync(string id, CancellationToken cancellationToken, bool force = false)
    {
        StorageLocation location;
        Refresh();
        lock (_lock)
        {
            if (id == ThisPc.Id || _locations.FirstOrDefault(l => l.Id == id) is not { } found)
            {
                return id == ThisPc.Id ? "This PC cannot be removed." : "There is no such location.";
            }

            location = found;

            // The default going: This PC is the default again (it takes every kind, copied there first).
            if (found.Default)
            {
                _locations = [.. _locations.Select(l => l.Id == ThisPc.Id ? l with { Default = true, Keeps = FileKinds.All } : l with { Default = false })];
                Save();
            }
            else if (Unkept([.. _locations.Where(l => l.Id != id)]) is { } kind)
            {
                return $"Every kind of file is kept somewhere: only this location keeps {kind}. Choose another one to keep it first.";
            }
        }

        // Its files to the others before it goes.
        var problem = await SyncAsync(cancellationToken).ConfigureAwait(false);
        if (problem is not null && !force)
        {
            return "Its files could not all be copied to the other locations: " + problem;
        }

        // The object storage on this PC removed by the user: not set up again by itself (its files stay in its folder).
        if (location.Local is not null)
        {
            try
            {
                File.WriteAllText(Path.Combine(Folder, "local-store.auto"), DateTimeOffset.UtcNow.ToString("O"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It may be set up again at the next start.
            }
        }

        Refresh();
        lock (_lock)
        {
            _locations = [.. _locations.Where(l => l.Id != location.Id)];
            _keys.Remove(location.Id);
            if (_providers.Remove(location.Id, out var provider))
            {
                provider.Provider.Dispose();
            }

            Save();
        }

        return null;
    }

    /// <summary>Starts bringing every location up to date in the background (one job at a time).</summary>
    public void StartSync()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PendingFile, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex);
        }

        // Running from now on (the page follows it closely while it runs), until the job says how it went.
        SetJob(new StorageJob(true, 0, 0, 0, null, null));
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogSyncFailed(_logger, ex);
                SetJob(new StorageJob(false, 0, 0, 0, ex.Message, DateTimeOffset.UtcNow));
            }
        });
    }

    /// <summary>
    /// Copies to each location the files of the kinds it keeps that it does not have, from one that has them (each
    /// copy's size checked). Null when everything is in place.
    /// </summary>
    internal async Task<string?> SyncAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? last = null;
        try
        {
            using var held = await HoldSyncLockAsync(cancellationToken).ConfigureAwait(false);
            var started = DateTime.UtcNow;
            var locations = Locations;
            var plan = new List<(string Kind, StoredFile File, StorageLocation From, StorageLocation To)>();
            foreach (var kind in FileKinds.All)
            {
                var keepers = locations.Where(l => l.Keeps.Contains(kind)).ToList();
                var have = new Dictionary<string, List<StoredFile>>(StringComparer.Ordinal);
                foreach (var location in locations.Where(l => l.Keeps.Contains(kind) || l.Id == ThisPc.Id))
                {
                    try
                    {
                        have[location.Id] = [.. await Provider(location).ListAsync(kind, cancellationToken).ConfigureAwait(false)];
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        Problem(location, ex);
                        last = $"{location.Name}: {ex.Message}";
                    }
                }

                var everyFile = have.Values.SelectMany(f => f).GroupBy(f => f.Name, StringComparer.Ordinal).Select(g => g.First());
                foreach (var file in everyFile)
                {
                    var from = locations.First(l => have.TryGetValue(l.Id, out var files) && files.Any(f => f.Name == file.Name));
                    foreach (var to in keepers.Where(l => have.TryGetValue(l.Id, out var files) && files.All(f => f.Name != file.Name)))
                    {
                        plan.Add((kind, file, from, to));
                    }
                }
            }

            SetJob(new StorageJob(true, 0, plan.Count, 0, null, null));
            var copied = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                var (kind, file, from, to) = plan[i];
                try
                {
                    await using var source = await Provider(from).OpenAsync(kind, file.Name, cancellationToken).ConfigureAwait(false);
                    if (source is not null)
                    {
                        var temp = Path.Combine(Path.GetTempPath(), $"pcdiag-sync-{Guid.NewGuid():N}.tmp");
                        try
                        {
                            await using (var write = File.Create(temp))
                            {
                                await source.CopyToAsync(write, cancellationToken).ConfigureAwait(false);
                            }

                            string sha;
                            await using (var read = File.OpenRead(temp))
                            {
                                sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(read, cancellationToken).ConfigureAwait(false));
                            }

                            if (new FileInfo(temp).Length != file.Size)
                            {
                                throw new IOException($"{file.Name} read {new FileInfo(temp).Length} bytes of {file.Size}.");
                            }

                            await using (var read = File.OpenRead(temp))
                            {
                                await Provider(to).PutAsync(kind, file.Name, read, sha, cancellationToken).ConfigureAwait(false);
                            }

                            copied++;
                        }
                        finally
                        {
                            File.Delete(temp);
                        }
                    }
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    Problem(to, ex);
                    last = $"{file.Name} to {to.Name}: {ex.Message}";
                }

                SetJob(new StorageJob(true, i + 1, plan.Count, copied, last, null));
            }

            SetJob(new StorageJob(false, plan.Count, plan.Count, copied, last, DateTimeOffset.UtcNow));

            // Done, unless something asked for another run meanwhile (that run follows this one).
            if (last is null && File.Exists(PendingFile) && File.GetLastWriteTimeUtc(PendingFile) <= started)
            {
                File.Delete(PendingFile);
            }

            return last;
        }
        finally
        {
            _sync.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var (_, provider) in _providers.Values)
            {
                provider.Dispose();
            }

            _providers.Clear();
        }

        _sync.Dispose();
    }

    // ---- Inside ------------------------------------------------------------------------------------------------

    private async Task PutEverywhereAsync(string kind, string name, string path, string sha, string? skip, CancellationToken cancellationToken)
    {
        var keepers = Locations.Where(l => l.Keeps.Contains(kind) && l.Id != skip).ToList();
        string? failed = null;
        foreach (var location in keepers)
        {
            try
            {
                await using var read = File.OpenRead(path);
                await Provider(location).PutAsync(kind, name, read, sha, cancellationToken).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_problems.Remove(location.Id))
                    {
                        Persist();
                    }
                }
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // This PC must take it; another location gets it at the next sync.
                if (location.Id == ThisPc.Id)
                {
                    throw;
                }

                Problem(location, ex);
                failed = location.Name;
            }
        }

        if (failed is not null)
        {
            StartSync();
        }
    }

    private IStorageProvider Provider(StorageLocation location)
    {
        lock (_lock)
        {
            var keys = _keys.GetValueOrDefault(location.Id);
            var signature = JsonSerializer.Serialize(location) + (keys is null ? string.Empty : keys.AccessKey + keys.SecretKey.GetHashCode(StringComparison.Ordinal));
            if (_providers.TryGetValue(location.Id, out var cached) && cached.Signature == signature)
            {
                return cached.Provider;
            }

            cached.Provider?.Dispose();
            var provider = Make(location, keys);
            _providers[location.Id] = (signature, provider);
            return provider;
        }
    }

    private IStorageProvider Make(StorageLocation location, StorageKeys? keys) => location.Kind switch
    {
        // Pictures and characters stay where the recorder reads them; the other kinds in the store's own folders.
        LocationKinds.ThisPc => new FolderProvider(kind => kind is FileKinds.Pictures or FileKinds.Characters ? _pictures : Path.Combine(_root, "files", kind)),
        LocationKinds.Folder => new FolderProvider(kind => Path.Combine(location.Path!, (location.Prefix ?? "PcDiagnostic").Trim('/', '\\'), kind)),
        LocationKinds.S3 when keys is not null => new S3Provider(location, keys),
        _ => throw new InvalidOperationException($"The location {location.Name} cannot be used (its keys are missing)."),
    };

    /// <summary>What is wrong with a location's settings, before trying it.</summary>
    private static string? Shape(StorageLocation location)
    {
        if (string.IsNullOrWhiteSpace(location.Name) || location.Name.Length > 60)
        {
            return "Give it a name (at most 60 characters).";
        }

        if (location.Keeps.Count == 0 || location.Keeps.Any(k => !FileKinds.All.Contains(k)))
        {
            return "Choose what it keeps: " + string.Join(", ", FileKinds.All) + ".";
        }

        return location.Kind switch
        {
            LocationKinds.Folder when string.IsNullOrWhiteSpace(location.Path) || !Path.IsPathFullyQualified(location.Path) => "Give the folder's full path, e.g. D:\\Backups or \\\\server\\share.",
            LocationKinds.S3 when string.IsNullOrWhiteSpace(location.Bucket) => "Say which bucket.",
            LocationKinds.S3 when !string.IsNullOrWhiteSpace(location.Endpoint) && !(Uri.TryCreate(location.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http") => "The service's address starts with https:// (or http:// on your own network).",
            LocationKinds.ThisPc or LocationKinds.Folder or LocationKinds.S3 => null,
            _ => "A location is a folder or an S3-compatible service.",
        };
    }

    /// <summary>A kind no location keeps.</summary>
    private static string? Unkept(IReadOnlyList<StorageLocation> locations) => FileKinds.All.FirstOrDefault(k => !locations.Any(l => l.Keeps.Contains(k)));

    private static void Check(string kind, string name)
    {
        if (!FileKinds.All.Contains(kind) || !FileKinds.IsName(name))
        {
            throw new ArgumentException($"Not a stored file: {kind}/{name}.");
        }
    }

    private static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
        or Amazon.Runtime.AmazonServiceException or Amazon.Runtime.AmazonClientException or InvalidOperationException or FormatException;

    private void Problem(StorageLocation location, Exception ex)
    {
        lock (_lock)
        {
            _problems[location.Id] = ex.Message;
            Persist();
        }

        LogLocationProblem(_logger, location.Name, ex.Message);
    }

    private void SetJob(StorageJob job)
    {
        lock (_lock)
        {
            _job = job;
            Persist();
        }
    }

    /// <summary>The job's state and the problems seen, for the other process (best effort). Call holding the lock.</summary>
    private void Persist()
    {
        _stateAt = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(Folder);
            Write(StateFile, JsonSerializer.SerializeToUtf8Bytes(new SharedStateFile(_instance, Environment.ProcessId, _stateAt, _job, new(_problems, StringComparer.Ordinal)), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The other process shows its own state meanwhile.
        }
    }

    /// <summary>The other process's state, when it saved one; a job of a process that has gone is not running.</summary>
    private SharedStateFile? SharedState()
    {
        try
        {
            if (!File.Exists(StateFile) || JsonSerializer.Deserialize<SharedStateFile>(File.ReadAllBytes(StateFile), Json) is not { } state || state.Instance == _instance)
            {
                return null;
            }

            state = state with { Problems = new Dictionary<string, string?>(state.Problems ?? [], StringComparer.Ordinal) };
            return state.Job.Running && !Alive(state.Pid) ? state with { Job = state.Job with { Running = false } } : state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool Alive(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Waits for the other process's job (one job at a time across the Web host and the storage host); after
    /// <see cref="LockPatience"/>, or when the lock cannot be had at all, goes ahead without it.
    /// </summary>
    private async Task<FileStream?> HoldSyncLockAsync(CancellationToken cancellationToken)
    {
        var retry = TimeSpan.FromMilliseconds(250);
        var waited = TimeSpan.Zero;
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                return new FileStream(SyncLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waited < LockPatience)
            {
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
                waited += retry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogLockFailed(_logger, ex);
                return null;
            }
        }
    }

    /// <summary>Takes in the locations and keys the other process saved.</summary>
    private void Refresh()
    {
        var stamp = Stamp();
        lock (_lock)
        {
            if (stamp == _stamp)
            {
                return;
            }

            _locations = LoadLocations();
            _keys = LoadKeys();
            _stamp = stamp;
        }
    }

    /// <summary>When the locations and keys files were last written (and their sizes).</summary>
    private string Stamp()
    {
        static string Of(string path)
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.LastWriteTimeUtc.Ticks}:{file.Length}" : "-";
        }

        return Of(LocationsFile) + "|" + Of(KeysFile);
    }

    /// <summary>What one process tells the other: its job and the problems it saw.</summary>
    private sealed record SharedStateFile(string Instance, int Pid, DateTimeOffset At, StorageJob Job, Dictionary<string, string?> Problems);

    private List<StorageLocation> LoadLocations()
    {
        try
        {
            if (File.Exists(LocationsFile) && JsonSerializer.Deserialize<List<StorageLocation>>(File.ReadAllText(LocationsFile), Json) is { Count: > 0 } list)
            {
                // This PC always first, keeping what the recorder reads.
                var pc = list.FirstOrDefault(l => l.Id == ThisPc.Id) ?? ThisPc;
                pc = pc with { Kind = LocationKinds.ThisPc, Keeps = [.. FileKinds.All.Where(k => FileKinds.OnThisPc.Contains(k) || pc.Keeps.Contains(k))] };
                List<StorageLocation> locations = [pc, .. list.Where(l => l.Id != ThisPc.Id && l.Kind is LocationKinds.Folder or LocationKinds.S3)];

                // One default: the first marked so, else This PC. It keeps every kind (also kinds added since it was set).
                var chosen = locations.FirstOrDefault(l => l.Default)?.Id ?? ThisPc.Id;
                return [.. locations.Select(l => l with { Default = l.Id == chosen, Keeps = l.Id == chosen ? FileKinds.All : l.Keeps })];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogLoadFailed(_logger, ex);
        }

        return [ThisPc];
    }

    private Dictionary<string, StorageKeys> LoadKeys()
    {
        try
        {
            if (File.Exists(KeysFile))
            {
                var bytes = File.ReadAllBytes(KeysFile);
                var plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser) : bytes;
                return JsonSerializer.Deserialize<Dictionary<string, StorageKeys>>(plain, Json) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            LogLoadFailed(_logger, ex);
        }

        return new Dictionary<string, StorageKeys>(StringComparer.Ordinal);
    }

    /// <summary>Saves the locations and the keys (protected for this Windows user). Call holding the lock.</summary>
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Write(LocationsFile, JsonSerializer.SerializeToUtf8Bytes(_locations, Json));
            var plain = JsonSerializer.SerializeToUtf8Bytes(_keys, Json);
            Write(KeysFile, OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser) : plain);
            _stamp = Stamp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LogSaveFailed(_logger, ex);
            throw;
        }
    }

    private static void Write(string path, byte[] content)
    {
        var temp = $"{path}.{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..6]}.tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage location {Location}: {Problem}")]
    private static partial void LogLocationProblem(ILogger logger, string location, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage locations could not be read; This PC only")]
    private static partial void LogLoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage locations could not be saved")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage locations could not be brought up to date")]
    private static partial void LogSyncFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage job went ahead without waiting for the other process's job")]
    private static partial void LogLockFailed(ILogger logger, Exception exception);
}
