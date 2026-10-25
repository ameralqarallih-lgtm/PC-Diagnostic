using System.Text.Json;
using System.Text.Json.Serialization;
using PcDiag.Core.Configuration;

namespace PcDiag.Core.Storage;

/// <summary>How the space for PC Diagnostic's data stands, worst first when several apply.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageCondition>))]
public enum StorageCondition
{
    /// <summary>Within every limit.</summary>
    Normal = 0,

    /// <summary>Above the space limit even after cleaning up (what is left is pinned or still recording).</summary>
    OverLimit = 1,

    /// <summary>The drive has less free space than the user keeps free: full detail is kept for a shorter time.</summary>
    LowSpace = 2,

    /// <summary>The drive is almost full: nothing new is written until there is space again.</summary>
    WritesPaused = 3,
}

/// <summary>What one clean-up did (kept in storage-log.json so it can be looked up later).</summary>
/// <param name="At">When (the latest pass, for repeated automatic clean-ups merged into one entry).</param>
/// <param name="Kind">One of <see cref="StorageActionKinds"/>.</param>
/// <param name="Detail">What was removed and why, for the user.</param>
/// <param name="FreedBytes">Space given back.</param>
/// <param name="Automatic">Done by the recorder on its own (false: asked for on the Storage page).</param>
/// <param name="Count">How many items (incidents removed) or passes (merged entries) it covers.</param>
public sealed record StorageAction(DateTimeOffset At, string Kind, string Detail, long FreedBytes, bool Automatic, int Count = 1);

/// <summary>The kinds of <see cref="StorageAction"/>.</summary>
public static class StorageActionKinds
{
    /// <summary>Incidents removed (the most kept, or the space limit).</summary>
    public const string IncidentsRemoved = "incidents-removed";

    /// <summary>Full-detail telemetry removed earlier than its retention.</summary>
    public const string FullDetailShortened = "full-detail-shortened";

    /// <summary>One-minute summaries and events removed earlier than their retention.</summary>
    public const string SummariesShortened = "summaries-shortened";

    /// <summary>Free space in the database returned to the drive.</summary>
    public const string Compacted = "compacted";

    /// <summary>Writing stopped: the drive is almost full.</summary>
    public const string WritesPaused = "writes-paused";

    /// <summary>Writing started again.</summary>
    public const string WritesResumed = "writes-resumed";
}

/// <summary>The recorder's view of its storage: the condition, the drive's free space and recent clean-ups.</summary>
/// <param name="Condition">How things stand.</param>
/// <param name="Message">What it means for the user (null when normal).</param>
/// <param name="CheckedAt">The last check of the drive.</param>
/// <param name="FreeBytes">The drive's free space then.</param>
/// <param name="UsedBytes">What the space limit counts (the data folder without the character generator's folder) at the last full pass.</param>
/// <param name="LastPassAt">The last full pass (limits applied).</param>
/// <param name="Recent">Recent clean-ups, newest first.</param>
public sealed record StorageState(
    StorageCondition Condition,
    string? Message,
    DateTimeOffset? CheckedAt,
    long? FreeBytes,
    long? UsedBytes,
    DateTimeOffset? LastPassAt,
    IReadOnlyList<StorageAction> Recent);

/// <summary>The short form of a storage problem carried in every status (null when normal).</summary>
public sealed record StorageNotice(StorageCondition Condition, string Message);

/// <summary>The result of a clean-up asked for on the Storage page.</summary>
/// <param name="FreedBytes">Space given back.</param>
/// <param name="Message">What was done, for the user.</param>
/// <param name="State">The state afterwards.</param>
public sealed record StorageCleanResult(long FreedBytes, string Message, StorageState State);

/// <summary>Applies the storage limits; the recorder implements it.</summary>
public interface IStorageControl
{
    /// <summary>The current state.</summary>
    StorageState State { get; }

    /// <summary>Applies the limits now and, when <paramref name="compact"/>, returns the databases' free space to the drive.</summary>
    Task<StorageCleanResult> CleanAsync(bool compact, CancellationToken cancellationToken);
}

/// <summary>Asked before writing: false while the drive is almost full.</summary>
public interface IStorageGuard
{
    /// <summary>True while nothing new may be written (the drive is almost full).</summary>
    bool WritesPaused { get; }
}

/// <summary>
/// The paused flag on its own (the storage manager sets it; the telemetry writer and the incident manager read it),
/// so the writers do not depend on the manager, which depends on them.
/// </summary>
public sealed class StorageGuard : IStorageGuard
{
    private volatile bool _paused;

    /// <inheritdoc />
    public bool WritesPaused => _paused;

    /// <summary>Pauses or resumes writing.</summary>
    public void Set(bool paused) => _paused = paused;
}

/// <summary>A write refused because the drive is almost full.</summary>
public sealed class StorageFullException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public StorageFullException()
        : base(StorageLimits.PausedMessage)
    {
    }

    /// <summary>Creates the exception with the reason shown to the user.</summary>
    public StorageFullException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the reason shown to the user and the cause.</summary>
    public StorageFullException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An incident as far as storage is concerned.</summary>
/// <param name="Key">The incident key.</param>
/// <param name="CenterAt">When it happened (the oldest go first).</param>
/// <param name="Pinned">Pinned incidents are never removed automatically.</param>
/// <param name="Finished">Finished incidents only are removed (one still recording or finalizing stays).</param>
public sealed record StoredIncident(string Key, DateTimeOffset CenterAt, bool Pinned, bool Finished);

/// <summary>Lists the incidents kept (the recorder's incident store implements it).</summary>
public interface IIncidentInventory
{
    /// <summary>Every incident in the index, oldest first.</summary>
    Task<IReadOnlyList<StoredIncident>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>How far back telemetry.db goes and how large it is.</summary>
/// <param name="FullDetailFrom">The oldest full-detail sample (null: none).</param>
/// <param name="SummariesFrom">The oldest one-minute summary (null: none).</param>
/// <param name="FileBytes">The database and its write-ahead log on disk.</param>
/// <param name="FreeBytes">Free pages inside the database (given back by compacting).</param>
public sealed record TelemetryCoverage(DateTimeOffset? FullDetailFrom, DateTimeOffset? SummariesFrom, long FileBytes, long FreeBytes);

/// <summary>Removes telemetry ahead of its retention and gives space back (the recorder's maintenance implements it).</summary>
public interface ITelemetrySpace
{
    /// <summary>How far back it goes and how large it is.</summary>
    Task<TelemetryCoverage> CoverageAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes full detail before <paramref name="fullDetailBefore"/> and summaries and events before
    /// <paramref name="summariesBefore"/> (the latest hardware, display and driver snapshots always stay). Returns the rows deleted.
    /// </summary>
    Task<long> TrimAsync(DateTimeOffset fullDetailBefore, DateTimeOffset summariesBefore, CancellationToken cancellationToken);

    /// <summary>Returns the database's free pages to the drive and empties its write-ahead log. Returns the bytes given back.</summary>
    Task<long> CompactAsync(CancellationToken cancellationToken);
}

/// <summary>A drive's size and free space.</summary>
public sealed record DiskSpace(string Name, long TotalBytes, long FreeBytes);

/// <summary>Reads the free space of the drive holding a folder.</summary>
public interface IDiskSpace
{
    /// <summary>The drive holding <paramref name="path"/>, or null when it cannot be read.</summary>
    DiskSpace? Read(string path);
}

/// <summary>The drive's space from the file system.</summary>
public sealed class DriveDiskSpace : IDiskSpace
{
    /// <inheritdoc />
    public DiskSpace? Read(string path)
    {
        try
        {
            if (Path.GetPathRoot(Path.GetFullPath(path)) is not { Length: > 0 } root)
            {
                return null;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady ? new DiskSpace(drive.Name.TrimEnd('\\'), drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The fixed floors and thresholds of the storage manager.</summary>
public static class StorageLimits
{
    /// <summary>One gibibyte.</summary>
    public const long GiB = 1024L * 1024 * 1024;

    /// <summary>Below this free space nothing new is written.</summary>
    public const long PauseBelowBytes = GiB;

    /// <summary>Writing starts again above this free space (half a gigabyte above the pause, so it does not flap).</summary>
    public const long ResumeAboveBytes = GiB + (GiB / 2);

    /// <summary>Full detail is never shortened below this (an incident's pre-window must still be there).</summary>
    public static readonly TimeSpan FullDetailFloor = TimeSpan.FromHours(2);

    /// <summary>One-minute summaries and events are never shortened below this.</summary>
    public static readonly TimeSpan SummariesFloor = TimeSpan.FromDays(7);

    /// <summary>Over the space limit, data is removed until it uses at most this share of it (so it is not trimmed every pass).</summary>
    public const double LimitTarget = 0.95;

    /// <summary>What a paused recorder tells the user.</summary>
    public const string PausedMessage =
        "The drive with PC Diagnostic's data has less than 1 GB free: nothing new is recorded (the last 30 minutes are kept in memory) until there is space again.";
}

/// <summary>What one kind of data uses in the data folder.</summary>
/// <param name="Id">incidents, telemetry, index, logs, diagnostics, reports, characters or other.</param>
/// <param name="Bytes">Size on disk.</param>
/// <param name="Files">Files (for incidents: incidents).</param>
public sealed record StorageKindUsage(string Id, long Bytes, int Files);

/// <summary>What the data folder uses, by kind and by incident.</summary>
/// <param name="TotalBytes">The whole data folder.</param>
/// <param name="Kinds">By kind, largest first.</param>
/// <param name="IncidentBytes">Each incident folder's size, by incident key.</param>
public sealed record StorageUsage(long TotalBytes, IReadOnlyList<StorageKindUsage> Kinds, IReadOnlyDictionary<string, long> IncidentBytes)
{
    /// <summary>
    /// Kinds the space limit does not count: the character generator's folder (its AI models, about 55 GB, its own
    /// Python and the characters made with it) is what the user installed and made, not what the recorder records,
    /// and no clean-up of recordings can make room for it. Counted, it held recordings at their shortest for nothing.
    /// </summary>
    public static readonly IReadOnlySet<string> NotLimited = new HashSet<string>(StringComparer.Ordinal) { "characters" };

    /// <summary>What the space limit counts: everything but <see cref="NotLimited"/>.</summary>
    public long LimitedBytes => TotalBytes - Kinds.Where(k => NotLimited.Contains(k.Id)).Sum(k => k.Bytes);
}

/// <summary>Measures the data folder (file sizes only; nothing is opened).</summary>
public static class StorageMeter
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Measures every kind of data in <paramref name="paths"/>' folder.</summary>
    public static StorageUsage Measure(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var root = new DirectoryInfo(paths.Root);
        if (!root.Exists)
        {
            return new StorageUsage(0, [], new Dictionary<string, long>());
        }

        var kinds = new Dictionary<string, (long Bytes, int Files)>(StringComparer.Ordinal);
        void Add(string kind, long bytes, int files)
        {
            var (b, f) = kinds.GetValueOrDefault(kind);
            kinds[kind] = (b + bytes, f + files);
        }

        var incidents = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFileName(paths.IncidentsDirectory)] = "incidents",
            [Path.GetFileName(paths.LogsDirectory)] = "logs",
            [Path.GetFileName(paths.DiagnosticsDirectory)] = "diagnostics",
            [Path.GetFileName(paths.ReportsDirectory)] = "reports",
            ["characters"] = "characters",
        };
        var telemetry = Path.GetFileName(paths.TelemetryDatabase);
        var index = Path.GetFileName(paths.IncidentIndexDatabase);

        foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (entry is FileInfo file)
            {
                // telemetry.db, telemetry.db-wal and telemetry.db-shm belong together, as do incidents.db's.
                var kind = file.Name.StartsWith(telemetry, StringComparison.OrdinalIgnoreCase) ? "telemetry"
                    : file.Name.StartsWith(index, StringComparison.OrdinalIgnoreCase) ? "index"
                    : "other";
                Add(kind, Length(file), 1);
            }
            else if (entry is DirectoryInfo directory)
            {
                var kind = folders.GetValueOrDefault(directory.Name, "other");
                if (kind == "incidents")
                {
                    foreach (var incident in directory.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        var (bytes, _) = Size(incident);
                        incidents[incident.Name] = bytes;
                        Add(kind, bytes, 1);
                    }

                    // Loose files in the incidents folder count too.
                    foreach (var loose in directory.EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }))
                    {
                        Add(kind, Length(loose), 0);
                    }
                }
                else
                {
                    var (bytes, files) = Size(directory);
                    Add(kind, bytes, files);
                }
            }
        }

        var list = kinds.Select(k => new StorageKindUsage(k.Key, k.Value.Bytes, k.Value.Files)).OrderByDescending(k => k.Bytes).ThenBy(k => k.Id, StringComparer.Ordinal).ToList();
        return new StorageUsage(list.Sum(k => k.Bytes), list, incidents);
    }

    /// <summary>A folder's size and file count (links are not followed).</summary>
    public static (long Bytes, int Files) Size(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        long bytes = 0;
        var files = 0;
        try
        {
            foreach (var file in directory.EnumerateFiles("*", Recursive))
            {
                bytes += Length(file);
                files++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone while measuring: what was counted stands.
        }

        return (bytes, files);
    }

    private static long Length(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
/// The clean-up log (storage-log.json in the data folder, written by the recorder): the most recent
/// <see cref="MaxEntries"/> clean-ups, newest first. Repeated automatic clean-ups of one kind within
/// <see cref="MergeWindow"/> are merged into one entry, so a recorder trimming every pass does not flood it.
/// </summary>
public static class StorageLog
{
    /// <summary>Entries kept.</summary>
    public const int MaxEntries = 100;

    /// <summary>Automatic clean-ups of one kind this close together are one entry.</summary>
    public static readonly TimeSpan MergeWindow = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The log, newest first; empty when there is none or it cannot be read.</summary>
    public static IReadOnlyList<StorageAction> Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<StorageAction>>(File.ReadAllText(path), Json) ?? [];
            return entries.Where(e => e is { Kind.Length: > 0, Detail: not null }).OrderByDescending(e => e.At).Take(MaxEntries).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The log with <paramref name="action"/> added (merged into the newest entry when it repeats it).</summary>
    public static IReadOnlyList<StorageAction> Add(IReadOnlyList<StorageAction> log, StorageAction action)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(action);
        var list = log.ToList();
        var mergeable = action.Kind is StorageActionKinds.FullDetailShortened or StorageActionKinds.SummariesShortened or StorageActionKinds.Compacted;
        if (mergeable && action.Automatic && list.Count > 0 && list[0] is { Automatic: true } last && last.Kind == action.Kind && action.At - last.At <= MergeWindow)
        {
            list[0] = action with { FreedBytes = last.FreedBytes + action.FreedBytes, Count = last.Count + action.Count };
        }
        else
        {
            list.Insert(0, action);
        }

        return list.Take(MaxEntries).ToList();
    }

    /// <summary>Writes the log (to a temporary file first, so a reader never sees half of it).</summary>
    public static void Write(string path, IReadOnlyList<StorageAction> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(log.Take(MaxEntries), Json));
        File.Move(temp, path, overwrite: true);
    }
}
