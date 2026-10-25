using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Storage;
using PcDiag.Engine.Diagnostics;

namespace PcDiag.Engine.Storage;

/// <summary>
/// Keeps PC Diagnostic's data within the user's limits and the drive from filling up.
/// <list type="bullet">
/// <item><b>Every 30 seconds</b> the drive's free space: below 1 GB nothing new is written (telemetry waits in memory,
/// new incidents are refused) until 1.5 GB are free again.</item>
/// <item><b>Every 10 minutes</b>, a few seconds after the limits change, as soon as the drive drops below the free
/// space to keep, and when asked: the most incidents kept, the space limit and the free space to keep.</item>
/// </list>
/// When space has to be given back, what can be recorded again goes first: full detail is kept for a shorter time
/// (down to 2 hours), then the one-minute summaries and events (down to 7 days); only then, and for the space limit
/// only, the oldest unpinned incidents are removed. Incidents are never removed for the drive's free space, and pinned
/// incidents never are. Every clean-up is written to storage-log.json.
/// </summary>
public sealed partial class StorageManager : BackgroundService, IStorageControl
{
    /// <summary>How often the drive's free space is checked.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the limits are applied.</summary>
    public static readonly TimeSpan PassInterval = TimeSpan.FromMinutes(10);

    /// <summary>Delay before the first pass, so startup stays light.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(45);

    /// <summary>Delay after the limits change (several changes in a row make one pass).</summary>
    public static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(5);

    /// <summary>A folder in incidents\ without its incident is removed once it is this old (a new incident's folder is never touched).</summary>
    public static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    /// <summary>Most rounds of shortening per pass (each measures what it gave back).</summary>
    private const int MaxRounds = 4;

    private readonly DataPaths _paths;
    private readonly IOptionsMonitor<StorageOptions> _options;
    private readonly ITelemetrySpace _telemetry;
    private readonly IIncidentInventory _inventory;
    private readonly IIncidentCapture _capture;
    private readonly StorageGuard _guard;
    private readonly IDiskSpace _disk;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly Lock _writeLock = new();
    private IReadOnlyList<StorageAction> _log;
    private DateTimeOffset? _passDue;
    private DateTimeOffset? _checkedAt;
    private DateTimeOffset? _lastPass;
    private DiskSpace? _drive;
    private long? _used;
    private bool _lowSpace;
    private bool _overLimit;
    private bool _unsaved;

    /// <summary>Creates the manager.</summary>
    public StorageManager(
        DataPaths paths,
        IOptionsMonitor<StorageOptions> options,
        ITelemetrySpace telemetry,
        IIncidentInventory inventory,
        IIncidentCapture capture,
        StorageGuard guard,
        TimeProvider time,
        ILogger<StorageManager> logger,
        IDiskSpace? disk = null)
    {
        _paths = paths;
        _options = options;
        _telemetry = telemetry;
        _inventory = inventory;
        _capture = capture;
        _guard = guard;
        _time = time;
        _logger = logger;
        _disk = disk ?? new DriveDiskSpace();
        _log = StorageLog.Read(paths.StorageLogFile);
    }

    /// <summary>What one "GB" of the limits is (tests use a smaller unit so their files stay small).</summary>
    internal long Unit { get; init; } = StorageLimits.GiB;

    /// <inheritdoc />
    public StorageState State
    {
        get
        {
            lock (_stateLock)
            {
                var (condition, message) = Condition();
                return new StorageState(condition, message, _checkedAt, _drive?.FreeBytes, _used, _lastPass, _log.Take(20).ToList());
            }
        }
    }

    /// <inheritdoc />
    public Task<StorageCleanResult> CleanAsync(bool compact, CancellationToken cancellationToken) => PassAsync(manual: true, compact, cancellationToken);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var changes = _options.OnChange(_ => PassSoon(ChangeDelay));
        try
        {
            CheckSpace();
            var nextPass = _time.GetUtcNow() + StartDelay;
            using var timer = new PeriodicTimer(CheckInterval, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                CheckSpace();
                var now = _time.GetUtcNow();
                if (now >= nextPass || TakeDuePass(now))
                {
                    nextPass = now + PassInterval;
                    try
                    {
                        await PassAsync(manual: false, compact: false, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        LogPassFailed(_logger, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>Reads the drive's free space and pauses or resumes writing. Exposed for tests.</summary>
    internal void CheckSpace()
    {
        var drive = _disk.Read(_paths.Root);
        var now = _time.GetUtcNow();
        var keepFree = _options.CurrentValue.KeepFreeGb * Unit;
        StorageAction? action = null;
        lock (_stateLock)
        {
            _checkedAt = now;
            if (drive is null)
            {
                return;
            }

            _drive = drive;
            if (!_guard.WritesPaused && drive.FreeBytes < StorageLimits.PauseBelowBytes)
            {
                _guard.Set(true);
                action = new StorageAction(now, StorageActionKinds.WritesPaused,
                    $"Recording to disk paused: only {Bytes(drive.FreeBytes)} free on {drive.Name}. The last 30 minutes are kept in memory; new incidents wait for space.", 0, true);
                LogPaused(_logger, drive.Name, drive.FreeBytes);
                _passDue = now;
            }
            else if (_guard.WritesPaused && drive.FreeBytes >= StorageLimits.ResumeAboveBytes)
            {
                _guard.Set(false);
                action = new StorageAction(now, StorageActionKinds.WritesResumed, $"Recording to disk resumed: {Bytes(drive.FreeBytes)} free on {drive.Name}.", 0, true);
                LogResumed(_logger, drive.Name, drive.FreeBytes);
            }

            var low = keepFree > 0 && drive.FreeBytes < keepFree;
            if (low && !_lowSpace)
            {
                // Give back what can be recorded again, now rather than at the next pass.
                _passDue = now;
            }

            _lowSpace = low;
        }

        if (action is not null)
        {
            Record(action);
        }
    }

    /// <summary>Asks for a pass after <paramref name="delay"/> (several requests in a row make one pass).</summary>
    private void PassSoon(TimeSpan delay)
    {
        lock (_stateLock)
        {
            _passDue = _time.GetUtcNow() + delay;
        }
    }

    /// <summary>True (once) when a pass was asked for and its time has come.</summary>
    private bool TakeDuePass(DateTimeOffset now)
    {
        lock (_stateLock)
        {
            if (_passDue is not { } due || now < due)
            {
                return false;
            }

            _passDue = null;
            return true;
        }
    }

    /// <summary>Applies the limits (and compacts when asked). Exposed for tests.</summary>
    internal async Task<StorageCleanResult> PassAsync(bool manual, bool compact, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = _options.CurrentValue;
            var automatic = !manual;
            long freed = 0;
            var done = new List<string>();

            // 1. The most incidents kept: the oldest unpinned ones beyond it.
            if (options.MaxIncidents > 0)
            {
                var incidents = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
                var excess = incidents.Count - options.MaxIncidents;
                if (excess > 0)
                {
                    var usage = StorageMeter.Measure(_paths);
                    var (count, bytes, keys) = await RemoveAsync(incidents.Where(i => !i.Pinned && i.Finished).Take(excess), usage, long.MaxValue, cancellationToken).ConfigureAwait(false);
                    if (count > 0)
                    {
                        freed += bytes;
                        done.Add(Plural(count, "incident") + " removed");
                        Record(new StorageAction(_time.GetUtcNow(), StorageActionKinds.IncidentsRemoved,
                            $"{Plural(count, "oldest incident")} removed to keep at most {options.MaxIncidents.ToString(CultureInfo.InvariantCulture)}: {Keys(keys)}.", bytes, automatic, count));
                    }
                }
            }

            // 2. Leftovers: incident folders whose incident is gone (a deletion that could not remove its files at once).
            freed += await RemoveLeftoversAsync(automatic, cancellationToken).ConfigureAwait(false);

            // 3. Space: the limit and the free space to keep.
            var measured = StorageMeter.Measure(_paths);
            var drive = _disk.Read(_paths.Root);
            var limit = options.DataLimitGb * Unit;
            var keepFree = options.KeepFreeGb * Unit;
            var target = (long)(limit * StorageLimits.LimitTarget);
            // The limit counts what the recorder records (not the character generator's models: StorageUsage.NotLimited).
            var needForLimit = limit > 0 && measured.LimitedBytes > limit ? measured.LimitedBytes - target : 0;
            var needForDrive = 0L;
            string? driveReason = null;
            if (drive is not null)
            {
                if (drive.FreeBytes < StorageLimits.ResumeAboveBytes && _guard.WritesPaused)
                {
                    needForDrive = StorageLimits.ResumeAboveBytes - drive.FreeBytes;
                    driveReason = $"because the drive is almost full ({Bytes(drive.FreeBytes)} free on {drive.Name})";
                }

                if (keepFree > 0 && drive.FreeBytes < keepFree)
                {
                    needForDrive = Math.Max(needForDrive, keepFree + (keepFree / 20) - drive.FreeBytes);
                    driveReason ??= $"because {drive.Name} has {Bytes(drive.FreeBytes)} free, less than the {options.KeepFreeGb.ToString(CultureInfo.InvariantCulture)} GB to keep free";
                }
            }

            var need = Math.Max(needForLimit, needForDrive);
            if (need > 0)
            {
                var reason = needForLimit >= needForDrive || driveReason is null
                    ? $"to stay within the {options.DataLimitGb.ToString(CultureInfo.InvariantCulture)} GB space limit"
                    : driveReason;
                var given = await ShortenTelemetryAsync(need, options, reason, automatic, cancellationToken).ConfigureAwait(false);
                freed += given;
                if (given > 0)
                {
                    done.Add("telemetry shortened");
                }

                // Incidents go for the space limit only, the oldest unpinned first, until it is met.
                if (limit > 0)
                {
                    var usage = StorageMeter.Measure(_paths);
                    if (usage.LimitedBytes > target && needForLimit > 0)
                    {
                        var incidents = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
                        var (count, bytes, keys) = await RemoveAsync(incidents.Where(i => !i.Pinned && i.Finished), usage, usage.LimitedBytes - target, cancellationToken).ConfigureAwait(false);
                        if (count > 0)
                        {
                            freed += bytes;
                            done.Add(Plural(count, "incident") + " removed");
                            Record(new StorageAction(_time.GetUtcNow(), StorageActionKinds.IncidentsRemoved,
                                $"{Plural(count, "oldest incident")} removed {reason}: {Keys(keys)}.", bytes, automatic, count));
                        }
                    }
                }
            }

            // 4. Compact when asked: free pages back to the drive, the write-ahead log emptied.
            if (compact)
            {
                var back = await _telemetry.CompactAsync(cancellationToken).ConfigureAwait(false);
                freed += back;
                done.Add("database compacted");
                Record(new StorageAction(_time.GetUtcNow(), StorageActionKinds.Compacted, "The recording database was compacted: its free space went back to the drive.", back, automatic));
            }

            var after = StorageMeter.Measure(_paths);
            lock (_stateLock)
            {
                _used = after.LimitedBytes;
                _overLimit = limit > 0 && after.LimitedBytes > limit;
                _lastPass = _time.GetUtcNow();
            }

            CheckSpace();
            if (_unsaved)
            {
                Persist();
            }

            var message = done.Count == 0
                ? manual ? "Everything was already within the limits." : "Within the limits."
                : $"{Capitalize(string.Join(", ", done))}: {Bytes(freed)} given back.";
            if (freed > 0 || done.Count > 0)
            {
                LogPass(_logger, freed, string.Join(", ", done));
            }

            return new StorageCleanResult(freed, message, State);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Keeps full detail, then summaries, for a shorter time until <paramref name="need"/> bytes are given back or both
    /// reach their floor. The share of the database each hour holds is estimated from its size and span, and what was
    /// given back is measured after each round. Returns the bytes given back.
    /// </summary>
    private async Task<long> ShortenTelemetryAsync(long need, StorageOptions options, string reason, bool automatic, CancellationToken cancellationToken)
    {
        long given = 0;
        var configuredSummaries = TimeSpan.FromDays(Math.Max(1, options.RollupRetentionDays));
        for (var round = 0; round < MaxRounds && given < need; round++)
        {
            var now = _time.GetUtcNow();
            var coverage = await _telemetry.CoverageAsync(cancellationToken).ConfigureAwait(false);
            var used = Math.Max(1, coverage.FileBytes - coverage.FreeBytes);
            var remaining = need - given;

            // Free pages inside the file count first: compacting alone gives them back.
            if (coverage.FreeBytes >= remaining)
            {
                var back = await _telemetry.CompactAsync(cancellationToken).ConfigureAwait(false);
                given += back;
                Record(new StorageAction(now, StorageActionKinds.Compacted, $"The recording database was compacted {reason}.", back, automatic));
                if (back == 0)
                {
                    break;
                }

                continue;
            }

            var fullFloor = now - StorageLimits.FullDetailFloor;
            var summariesCutoff = now - configuredSummaries;
            if (coverage.FullDetailFrom is { } from && from < fullFloor - TimeSpan.FromMinutes(1))
            {
                // Each hour of full detail holds about used / span bytes (summaries included, so this errs on removing too little).
                var span = now - from;
                var hours = remaining * 1.1 / (used / Math.Max(1.0 / 60, span.TotalHours));
                var cutoff = from + TimeSpan.FromHours(Math.Max(1.0 / 60, hours));
                if (cutoff > fullFloor)
                {
                    cutoff = fullFloor;
                }

                await _telemetry.TrimAsync(cutoff, summariesCutoff, cancellationToken).ConfigureAwait(false);
                var back = await _telemetry.CompactAsync(cancellationToken).ConfigureAwait(false);
                given += back;
                Record(new StorageAction(now, StorageActionKinds.FullDetailShortened,
                    $"Full detail kept for {Duration(now - cutoff)} instead of {options.TelemetryRetentionHours.ToString(CultureInfo.InvariantCulture)} h {reason}.", back, automatic));
                continue;
            }

            var summariesFloor = now - StorageLimits.SummariesFloor;
            if (coverage.SummariesFrom is { } first && first < summariesFloor - TimeSpan.FromMinutes(10))
            {
                // Summaries are small next to full detail: remove a share of their span proportional to what is still needed.
                var span = summariesFloor - first;
                var share = Math.Clamp(remaining * 1.2 / used, 0.1, 1);
                var cutoff = first + (span * share);
                await _telemetry.TrimAsync(fullFloor, cutoff, cancellationToken).ConfigureAwait(false);
                var back = await _telemetry.CompactAsync(cancellationToken).ConfigureAwait(false);
                given += back;
                Record(new StorageAction(now, StorageActionKinds.SummariesShortened,
                    $"One-minute summaries and events kept for {Duration(now - cutoff)} instead of {options.RollupRetentionDays.ToString(CultureInfo.InvariantCulture)} days {reason}.", back, automatic));
                continue;
            }

            break;
        }

        return given;
    }

    /// <summary>Removes incidents in order until <paramref name="need"/> bytes are given back. Returns how many, the bytes and their keys.</summary>
    private async Task<(int Count, long Bytes, List<string> Keys)> RemoveAsync(IEnumerable<StoredIncident> incidents, StorageUsage usage, long need, CancellationToken cancellationToken)
    {
        var count = 0;
        long bytes = 0;
        var keys = new List<string>();
        foreach (var incident in incidents)
        {
            if (bytes >= need)
            {
                break;
            }

            if (!IncidentKey.TryParse(incident.Key, CultureInfo.InvariantCulture, out var key))
            {
                continue;
            }

            if (await _capture.DeleteAsync(key, cancellationToken).ConfigureAwait(false) == IncidentEditResult.Done)
            {
                count++;
                bytes += usage.IncidentBytes.GetValueOrDefault(incident.Key);
                keys.Add(incident.Key);
                LogRemoved(_logger, incident.Key);
            }
        }

        return (count, bytes, keys);
    }

    /// <summary>Removes incident folders whose incident is no longer in the index (and that are old enough not to be new).</summary>
    private async Task<long> RemoveLeftoversAsync(bool automatic, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.IncidentsDirectory))
        {
            return 0;
        }

        var known = (await _inventory.ListAsync(cancellationToken).ConfigureAwait(false)).Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = _time.GetUtcNow();
        long freed = 0;
        var removed = new List<string>();
        foreach (var folder in new DirectoryInfo(_paths.IncidentsDirectory).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            // Only folders named like an incident, never one a new incident is being written into.
            if (!IncidentKey.TryParse(folder.Name, CultureInfo.InvariantCulture, out _) || known.Contains(folder.Name)
                || now - new DateTimeOffset(folder.LastWriteTimeUtc, TimeSpan.Zero) < LeftoverAge)
            {
                continue;
            }

            var (bytes, _) = StorageMeter.Size(folder);
            try
            {
                folder.Delete(recursive: true);
                freed += bytes;
                removed.Add(folder.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use: tried again at the next pass.
                LogLeftoverBusy(_logger, folder.Name);
            }
        }

        if (removed.Count > 0)
        {
            Record(new StorageAction(now, StorageActionKinds.IncidentsRemoved,
                $"Files left by {Plural(removed.Count, "deleted incident")} removed: {Keys(removed)}.", freed, automatic, removed.Count));
        }

        return freed;
    }

    private (StorageCondition Condition, string? Message) Condition()
    {
        var options = _options.CurrentValue;
        if (_guard.WritesPaused)
        {
            return (StorageCondition.WritesPaused,
                $"Only {Bytes(_drive?.FreeBytes ?? 0)} free on {_drive?.Name ?? "the data drive"}: nothing new is recorded until {Bytes(StorageLimits.ResumeAboveBytes)} are free (the last 30 minutes wait in memory).");
        }

        if (_lowSpace && _drive is { } drive)
        {
            return (StorageCondition.LowSpace,
                $"{drive.Name} has {Bytes(drive.FreeBytes)} free, less than the {options.KeepFreeGb.ToString(CultureInfo.InvariantCulture)} GB to keep free: full detail and summaries are kept for a shorter time until there is more space.");
        }

        if (_overLimit)
        {
            return (StorageCondition.OverLimit,
                $"PC Diagnostic's recorded data uses {Bytes(_used ?? 0)}, more than the {options.DataLimitGb.ToString(CultureInfo.InvariantCulture)} GB limit, and what is left is pinned or still recording: unpin or delete incidents, or raise the limit.");
        }

        return (StorageCondition.Normal, null);
    }

    private void Record(StorageAction action)
    {
        lock (_stateLock)
        {
            _log = StorageLog.Add(_log, action);
        }

        Persist();
    }

    private void Persist()
    {
        IReadOnlyList<StorageAction> log;
        lock (_stateLock)
        {
            log = _log;
        }

        lock (_writeLock)
        {
            try
            {
                StorageLog.Write(_paths.StorageLogFile, log);
                _unsaved = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The drive may be full: the log is kept in memory and written at the next pass.
                _unsaved = true;
                LogLogFailed(_logger, ex);
            }
        }
    }

    private static string Bytes(long bytes) => AnalysisContext.FormatBytes(bytes);

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : string.Create(CultureInfo.InvariantCulture, $"{count} {noun}s");

    private static string Keys(List<string> keys) => keys.Count <= 5 ? string.Join(", ", keys) : $"{string.Join(", ", keys.Take(5))} and {(keys.Count - 5).ToString(CultureInfo.InvariantCulture)} more";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string Duration(TimeSpan span) => span.TotalHours >= 48
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(span.TotalDays):0} days")
        : span.TotalMinutes >= 90
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(span.TotalHours, 1):0.#} h")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(span.TotalMinutes)):0} min");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording to disk paused: {Free} bytes free on {Drive}")]
    private static partial void LogPaused(ILogger logger, string drive, long free);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording to disk resumed: {Free} bytes free on {Drive}")]
    private static partial void LogResumed(ILogger logger, string drive, long free);

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage limits applied: {Freed} bytes given back ({Done})")]
    private static partial void LogPass(ILogger logger, long freed, string done);

    [LoggerMessage(Level = LogLevel.Information, Message = "Incident {Key} removed by the storage limits")]
    private static partial void LogRemoved(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "The leftover incident folder {Folder} is in use; it is removed later")]
    private static partial void LogLeftoverBusy(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The storage limits could not be applied; they are applied at the next pass")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The clean-up log could not be written")]
    private static partial void LogLogFailed(ILogger logger, Exception exception);
}
