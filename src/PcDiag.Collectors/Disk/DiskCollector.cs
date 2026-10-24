using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Pdh;
using PcDiag.Platform.Windows.Storage;

namespace PcDiag.Collectors.Disk;

/// <summary>
/// Physical disk throughput, IOPS, latency, queue length and active time (PDH "PhysicalDisk"), disk identity
/// (IOCTL, no admin), health (WMI MSFT_PhysicalDisk) and volume capacity/free space.
/// Inventory (identity, health, free space) refreshes in the background so it never delays a sampling tick.
/// Disk temperature and SMART data come from hardware sensor providers in Phase 6.
/// </summary>
public sealed partial class DiskCollector : IDiskCollector, IDisposable
{
    /// <summary>How often volume free space is re-read.</summary>
    public static readonly TimeSpan VolumeRefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>How often disk identity and health are re-read (also when the set of disks changes).</summary>
    public static readonly TimeSpan InventoryRefreshInterval = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiskCollector> _logger;
    private readonly Dictionary<string, DiskCounters> _byInstance = new(StringComparer.OrdinalIgnoreCase);

    private PdhQuery? _query;
    private readonly PdhCounter?[] _counters = new PdhCounter?[8];
    private volatile Inventory _inventory = Inventory.Empty;
    private Task? _refresh;
    private long _lastInventory;
    private long _lastVolumes;

    /// <summary>Creates the collector.</summary>
    public DiskCollector(TimeProvider timeProvider, ILogger<DiskCollector> logger)
    {
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private enum Counter
    {
        ReadBytes,
        WriteBytes,
        Reads,
        Writes,
        ReadLatency,
        WriteLatency,
        Queue,
        Idle,
    }

    /// <inheritdoc />
    public string Id => "disk";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Disk", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _query = PdhQuery.Open();
            string[] names =
            [
                "Disk Read Bytes/sec", "Disk Write Bytes/sec", "Disk Reads/sec", "Disk Writes/sec",
                "Avg. Disk sec/Read", "Avg. Disk sec/Write", "Current Disk Queue Length", "% Idle Time",
            ];
            for (var i = 0; i < names.Length; i++)
            {
                var path = @"\PhysicalDisk(*)\" + names[i];
                _counters[i] = _query.TryAddCounter(path, out var status);
                if (_counters[i] is null)
                {
                    LogCounterMissing(_logger, path, status);
                }
            }

            _query.Collect();
            ReadCounters(pdhOk: true); // values are Pending, but instance names map volumes to disks
        }
        catch (PdhException ex)
        {
            LogPdhUnavailable(_logger, ex);
        }

        _inventory = BuildInventory(includeHealth: true, knownNumbers: null, LettersByDisk());
        _lastInventory = _lastVolumes = _timeProvider.GetTimestamp();
        var summary = string.Join("; ", _inventory.Disks.Values.OrderBy(d => d.Number).Select(d => $"{d.Number}: {d.Model} ({d.BusType}, {d.Media}, {d.Health})"));
        return ValueTask.FromResult(new CollectorStatus(_inventory.Disks.Count == 0 ? Availability.NotPresent : Availability.Available, summary));
    }

    /// <inheritdoc />
    public ValueTask<DiskSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pdhOk = _query is not null && _query.Collect() == 0;
        ReadCounters(pdhOk);
        ScheduleInventoryRefresh();

        var inventory = _inventory;
        var disks = new List<PhysicalDiskSample>(_byInstance.Count);
        foreach (var (instance, values) in _byInstance)
        {
            if (!TryParseInstance(instance, out var number, out var volumes))
            {
                continue;
            }

            var descriptor = inventory.Disks.TryGetValue(number, out var known)
                ? known
                : new DiskDescriptor($"disk-number-{number}", number, $"Disk {number}", "Unknown", StorageMediaKind.Unknown, 0, false, "Unknown");

            disks.Add(new PhysicalDiskSample(
                descriptor,
                volumes,
                values.Get(Counter.ReadBytes),
                values.Get(Counter.WriteBytes),
                values.Get(Counter.Reads),
                values.Get(Counter.Writes),
                values.Get(Counter.ReadLatency).Map(s => s * 1000.0),
                values.Get(Counter.WriteLatency).Map(s => s * 1000.0),
                values.Get(Counter.Queue),
                values.Get(Counter.Idle).Map(idle => Math.Clamp(100.0 - idle, 0, 100)),
                Reading<double>.Unavailable(Availability.Unsupported)));
        }

        disks.Sort((a, b) => a.Disk.Number.CompareTo(b.Disk.Number));

        // New disk numbers (hot-plug) trigger an identity refresh on the next opportunity.
        if (disks.Any(d => !inventory.Disks.ContainsKey(d.Disk.Number)))
        {
            _lastInventory = 0;
        }

        return ValueTask.FromResult(new DiskSample(context.Timestamp, disks, inventory.Volumes));
    }

    /// <inheritdoc />
    public void Dispose() => _query?.Dispose();

    /// <summary>Parses a PhysicalDisk instance such as "0 g: f:" into the disk number and its volumes (["G:", "F:"]).</summary>
    internal static bool TryParseInstance(string instance, out int number, out IReadOnlyList<string> volumes)
    {
        volumes = [];
        var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            number = -1;
            return false;
        }

        volumes = parts.Skip(1).Where(p => p.Length == 2 && p[1] == ':').Select(p => p.ToUpperInvariant()).ToList();
        return true;
    }

    /// <summary>Stable disk key from model and serial; the raw serial number is never stored.</summary>
    internal static string DiskKey(string model, string serial, long size, int number)
    {
        var identity = string.IsNullOrWhiteSpace(serial) ? $"{model}|{size}|#{number}" : $"{model}|{serial}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "disk-" + Convert.ToHexStringLower(hash.AsSpan(0, 6));
    }

    internal static StorageMediaKind MediaFor(bool? seeksPenalty, string busType) => seeksPenalty switch
    {
        true => StorageMediaKind.Hdd,
        false => StorageMediaKind.Ssd,
        null when busType is "NVMe" or "SCM" => StorageMediaKind.Ssd,
        _ => StorageMediaKind.Unknown,
    };

    private void ReadCounters(bool pdhOk)
    {
        foreach (var values in _byInstance.Values)
        {
            values.Clear();
        }

        if (!pdhOk)
        {
            return;
        }

        for (var i = 0; i < _counters.Length; i++)
        {
            if (_counters[i] is not { } counter)
            {
                continue;
            }

            var state = (Map: _byInstance, Index: i);
            counter.VisitArray(ref state, static (ref (Dictionary<string, DiskCounters> Map, int Index) s, ReadOnlySpan<char> name, Reading<double> value) =>
            {
                if (name.StartsWith("_", StringComparison.Ordinal))
                {
                    return; // _Total
                }

                var key = name.ToString();
                if (!s.Map.TryGetValue(key, out var entry))
                {
                    entry = new DiskCounters();
                    s.Map[key] = entry;
                }

                entry.Values[s.Index] = value;
            });
        }

        // Disks that disappeared since the last tick have no values left.
        foreach (var gone in _byInstance.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList())
        {
            _byInstance.Remove(gone);
        }
    }

    private void ScheduleInventoryRefresh()
    {
        if (_refresh is { IsCompleted: false })
        {
            return;
        }

        var identityDue = _lastInventory == 0 || _timeProvider.GetElapsedTime(_lastInventory) >= InventoryRefreshInterval;
        var volumesDue = _timeProvider.GetElapsedTime(_lastVolumes) >= VolumeRefreshInterval;
        if (!identityDue && !volumesDue)
        {
            return;
        }

        _lastVolumes = _timeProvider.GetTimestamp();
        if (identityDue)
        {
            _lastInventory = _lastVolumes;
        }

        var previous = _inventory;
        var letters = LettersByDisk(); // snapshot on the tick thread; the background task must not touch _byInstance
        _refresh = Task.Run(() =>
        {
            try
            {
                _inventory = identityDue
                    ? BuildInventory(includeHealth: true, knownNumbers: null, letters)
                    : previous with { Volumes = ReadVolumes(previous.Disks, letters) };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogInventoryFailed(_logger, ex);
            }
        });
    }

    private Dictionary<string, int> LettersByDisk()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in _byInstance.Keys)
        {
            if (TryParseInstance(instance, out var number, out var letters))
            {
                foreach (var letter in letters)
                {
                    map[letter] = number;
                }
            }
        }

        return map;
    }

    private static Inventory BuildInventory(bool includeHealth, IReadOnlyCollection<int>? knownNumbers, IReadOnlyDictionary<string, int> letters)
    {
        var numbers = knownNumbers ?? Enumerable.Range(0, 64).ToList();
        var health = includeHealth ? StorageDevices.TryGetHealth() : new Dictionary<int, string>();
        var disks = new Dictionary<int, DiskDescriptor>();
        var misses = 0;
        foreach (var number in numbers)
        {
            var info = StorageDevices.TryQuery(number);
            if (info is null)
            {
                // Disk numbers are dense in practice; stop after a run of gaps.
                if (++misses >= 8)
                {
                    break;
                }

                continue;
            }

            misses = 0;
            var model = string.Join(' ', new[] { info.Vendor, info.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            disks[number] = new DiskDescriptor(
                DiskKey(model, info.Serial, info.SizeBytes, number),
                number,
                string.IsNullOrWhiteSpace(model) ? $"Disk {number}" : model,
                info.BusType,
                MediaFor(info.SeeksPenalty, info.BusType),
                info.SizeBytes,
                info.IsRemovable,
                health.GetValueOrDefault(number, "Unknown"));
        }

        return new Inventory(disks, ReadVolumes(disks, letters));
    }

    private static List<VolumeSample> ReadVolumes(IReadOnlyDictionary<int, DiskDescriptor> disks, IReadOnlyDictionary<string, int> letters)
    {
        // Volume → disk mapping comes from the PDH instance names ("0 g: f:").
        var diskByLetter = letters
            .Where(kv => disks.ContainsKey(kv.Value))
            .ToDictionary(kv => kv.Key, kv => disks[kv.Value].DiskKey, StringComparer.OrdinalIgnoreCase);

        var volumes = new List<VolumeSample>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            // Network and optical drives can block for seconds; they are not physical-disk evidence.
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                volumes.Add(new VolumeSample(
                    drive.Name,
                    diskByLetter.GetValueOrDefault(drive.Name[..2]),
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.DriveType.ToString(),
                    Reading<long>.Of(drive.TotalSize),
                    Reading<long>.Of(drive.AvailableFreeSpace)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                volumes.Add(new VolumeSample(drive.Name, diskByLetter.GetValueOrDefault(drive.Name[..2]), null, null, drive.DriveType.ToString(),
                    Reading<long>.Unavailable(Availability.Error), Reading<long>.Unavailable(Availability.Error)));
            }
        }

        return volumes;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDH counter {Path} is not available on this system (status 0x{Status:X8})")]
    private static partial void LogCounterMissing(ILogger logger, string path, uint status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Performance counters (PDH) are unavailable; disk activity cannot be collected")]
    private static partial void LogPdhUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disk inventory refresh failed; keeping the previous inventory")]
    private static partial void LogInventoryFailed(ILogger logger, Exception exception);

    private sealed record Inventory(IReadOnlyDictionary<int, DiskDescriptor> Disks, IReadOnlyList<VolumeSample> Volumes)
    {
        public static Inventory Empty { get; } = new(new Dictionary<int, DiskDescriptor>(), []);
    }

    private sealed class DiskCounters
    {
        public Reading<double>?[] Values { get; } = new Reading<double>?[8];

        public bool IsEmpty => Values.All(v => v is null);

        public Reading<double> Get(Counter counter) => Values[(int)counter] ?? Reading<double>.Unavailable(Availability.Unsupported);

        public void Clear() => Array.Clear(Values);
    }
}
