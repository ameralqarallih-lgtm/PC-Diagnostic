using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Kernel;
using PcDiag.Platform.Windows.Pdh;
using PcDiag.Platform.Windows.Processes;

namespace PcDiag.Collectors.Memory;

/// <summary>
/// Physical memory, commit and paging metrics (GlobalMemoryStatusEx, GetPerformanceInfo and the "Memory" PDH object),
/// and compressed memory: the memory Windows' compression store uses, which is the working set of its "Memory
/// Compression" process (as Task Manager shows it), read from the system's process list (no process handle, which
/// that protected process would refuse).
/// </summary>
public sealed partial class MemoryCollector(ILogger<MemoryCollector> logger, TimeProvider? timeProvider = null) : IMemoryCollector, IDisposable
{
    /// <summary>How often compressed memory is read (it changes slowly; the process list is the costly part).</summary>
    private static readonly TimeSpan CompressionInterval = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly NtProcesses _processes = new();
    private Reading<long> _compressed = Reading<long>.Unavailable(Availability.Pending);
    private DateTimeOffset _nextCompressionRead = DateTimeOffset.MinValue;

    private PdhQuery? _query;
    private PdhCounter? _pageFaults;
    private PdhCounter? _pageReads;
    private PdhCounter? _pages;
    private PdhCounter? _freeAndZero;

    /// <inheritdoc />
    public string Id => "memory";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Memory", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _query = PdhQuery.Open();
            _pageFaults = Add(@"\Memory\Page Faults/sec");
            _pageReads = Add(@"\Memory\Page Reads/sec"); // hard-fault read operations
            _pages = Add(@"\Memory\Pages/sec");
            _freeAndZero = Add(@"\Memory\Free & Zero Page List Bytes");
            _query.Collect();
        }
        catch (PdhException ex)
        {
            // Paging rates are lost, but the API-based figures still work.
            LogPdhUnavailable(logger, ex);
        }

        var info = SystemInfo.TryGetMemoryInfo();
        return ValueTask.FromResult(info is null
            ? new CollectorStatus(Availability.Error, "GlobalMemoryStatusEx/GetPerformanceInfo failed")
            : new CollectorStatus(Availability.Available, $"{info.TotalBytes / (1024 * 1024)} MB physical memory"));
    }

    /// <inheritdoc />
    public ValueTask<MemorySample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pdhOk = _query is not null && _query.Collect() == 0;
        var info = SystemInfo.TryGetMemoryInfo();

        Reading<long> Bytes(Func<MemoryInfo, long> read) =>
            info is null ? Reading<long>.Unavailable(Availability.Error) : Reading<long>.Of(read(info));

        Reading<double> Rate(PdhCounter? counter) =>
            counter is null ? Reading<double>.Unavailable(Availability.Unsupported)
            : pdhOk ? counter.GetDouble()
            : Reading<double>.Unavailable(Availability.Error);

        var sample = new MemorySample(
            context.Timestamp,
            TotalBytes: Bytes(i => i.TotalBytes),
            AvailableBytes: Bytes(i => i.AvailableBytes),
            FreeBytes: Rate(_freeAndZero).Map(v => (long)v),
            CacheBytes: Bytes(i => i.SystemCacheBytes),
            CommittedBytes: Bytes(i => i.CommittedBytes),
            CommitLimitBytes: Bytes(i => i.CommitLimitBytes),
            PagedPoolBytes: Bytes(i => i.PagedPoolBytes),
            NonPagedPoolBytes: Bytes(i => i.NonPagedPoolBytes),
            CompressedBytes: ReadCompressed(),
            PageFaultsPerSec: Rate(_pageFaults),
            HardFaultsPerSec: Rate(_pageReads),
            PagesPerSec: Rate(_pages),
            Pressure: info is null ? Reading<double>.Unavailable(Availability.Error) : Reading<double>.Of(ComputePressure(info)));

        return ValueTask.FromResult(sample);
    }

    /// <summary>
    /// Memory pressure (0–1), formula v1: the larger of physical memory in use and commit charge relative to the
    /// commit limit. Transparent by design; hard-fault rate is reported separately.
    /// </summary>
    internal static double ComputePressure(MemoryInfo info)
    {
        var physical = info.TotalBytes > 0 ? 1.0 - ((double)info.AvailableBytes / info.TotalBytes) : 0;
        var commit = info.CommitLimitBytes > 0 ? (double)info.CommittedBytes / info.CommitLimitBytes : 0;
        return Math.Clamp(Math.Max(physical, commit), 0, 1);
    }

    /// <summary>
    /// The compression store's memory, read every <see cref="CompressionInterval"/> (the last value in between). With
    /// memory compression turned off there is no such process, and it reads as unsupported.
    /// </summary>
    private Reading<long> ReadCompressed()
    {
        var now = _time.GetUtcNow();
        if (now < _nextCompressionRead)
        {
            return _compressed;
        }

        _nextCompressionRead = now + CompressionInterval;
        long? bytes = null;
        _compressed = _processes.Visit(ref bytes, static (ref long? found, in RawProcess process) =>
            {
                if (process.Name.SequenceEqual(CompressionProcess))
                {
                    found = process.WorkingSet;
                }
            })
            ? bytes is { } value ? Reading<long>.Of(value) : Reading<long>.Unavailable(Availability.Unsupported)
            : Reading<long>.Unavailable(Availability.Error);
        return _compressed;
    }

    private const string CompressionProcess = "Memory Compression";

    /// <inheritdoc />
    public void Dispose() => _query?.Dispose();

    private PdhCounter? Add(string path)
    {
        var counter = _query!.TryAddCounter(path, out var status);
        if (counter is null)
        {
            LogCounterMissing(logger, path, status);
        }

        return counter;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDH counter {Path} is not available on this system (status 0x{Status:X8})")]
    private static partial void LogCounterMissing(ILogger logger, string path, uint status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Performance counters (PDH) are unavailable; paging rates will be reported as unavailable")]
    private static partial void LogPdhUnavailable(ILogger logger, Exception exception);
}
