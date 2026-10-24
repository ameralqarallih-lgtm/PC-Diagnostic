using System.Globalization;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Gpu;
using PcDiag.Platform.Windows.Pdh;

namespace PcDiag.Collectors.Gpu;

/// <summary>
/// GPU metrics for every hardware adapter: utilization per engine type and per process (PDH "GPU Engine"),
/// system-wide VRAM use (PDH "GPU Adapter Memory"), per-process VRAM (PDH "GPU Process Memory"), inventory (DXGI),
/// and temperatures/clocks/power from <see cref="IGpuDetailProvider"/>s merged by priority.
/// </summary>
public sealed partial class GpuCollector : IGpuCollector, IDisposable
{
    /// <summary>At most this many processes are reported per tick (busiest first).</summary>
    public const int MaxProcesses = 50;

    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IGpuDetailProvider> _providers;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GpuCollector> _logger;
    private readonly GpuUsageAggregator _usage = new();
    private readonly Dictionary<AdapterLuid, long> _dedicatedUsed = [];
    private HashSet<AdapterLuid> _ignoredLuids = [];
    private readonly Dictionary<AdapterLuid, long> _sharedUsed = [];
    private readonly Dictionary<(int Pid, AdapterLuid Luid), long> _processMemory = [];
    private readonly HashSet<int> _implausibleReported = [];

    private PdhQuery? _query;
    private PdhCounter? _engineUtilization;
    private PdhCounter? _adapterDedicated;
    private PdhCounter? _adapterShared;
    private PdhCounter? _processDedicated;
    private D3dkmtAdapters? _kmt;
    private List<(GpuAdapterBinding Binding, GpuAdapterDescriptor Descriptor, IReadOnlyList<IGpuDetailProvider> Providers)> _adapters = [];
    private long _lastRefresh;

    /// <summary>Creates the collector.</summary>
    public GpuCollector(IEnumerable<IGpuDetailProvider> providers, TimeProvider timeProvider, ILogger<GpuCollector> logger)
    {
        _providers = providers.OrderByDescending(p => p.Priority).ToList();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Id => "gpu";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "GPU", RequiredPrivilege.StandardUser, CostClass.Moderate);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _query = PdhQuery.Open();
            _engineUtilization = Add(@"\GPU Engine(*)\Utilization Percentage");
            _adapterDedicated = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _adapterShared = Add(@"\GPU Adapter Memory(*)\Shared Usage");
            _processDedicated = Add(@"\GPU Process Memory(*)\Dedicated Usage");
            _query.Collect();
        }
        catch (PdhException ex)
        {
            LogPdhUnavailable(_logger, ex);
        }

        RefreshAdapters();
        var summary = _adapters.Count == 0
            ? "no hardware GPU adapters found"
            : string.Join("; ", _adapters.Select(a => $"{a.Descriptor.Name} [{string.Join('+', a.Descriptor.DetailSources.DefaultIfEmpty("no detail source"))}]"));
        return ValueTask.FromResult(new CollectorStatus(_adapters.Count == 0 ? Availability.NotPresent : Availability.Available, summary));
    }

    /// <inheritdoc />
    public ValueTask<GpuSample> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var pdhOk = _query is not null && _query.Collect() == 0;
        ReadPdh(pdhOk);

        // A new LUID in PDH (hot-plug, driver reset) means the adapter list is stale; virtual twins are known and skipped.
        if (_usage.Adapters.Any(l => !_ignoredLuids.Contains(l) && _adapters.All(a => a.Binding.Dxgi.Luid != l))
            && _timeProvider.GetElapsedTime(_lastRefresh) >= MinRefreshInterval)
        {
            RefreshAdapters();

            // What the refresh still did not bind (a software or virtual adapter DXGI does not offer as hardware, the
            // remote display adapter): known now, so it does not set off a refresh every 30 seconds.
            _ignoredLuids.UnionWith(_usage.Adapters.Where(l => _adapters.All(a => a.Binding.Dxgi.Luid != l)));
        }

        var adapters = new List<GpuAdapterSample>(_adapters.Count);
        foreach (var (binding, descriptor, providers) in _adapters)
        {
            adapters.Add(BuildAdapterSample(binding, descriptor, providers, pdhOk));
        }

        return ValueTask.FromResult(new GpuSample(context.Timestamp, adapters, BuildProcesses()));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _query?.Dispose();
        _kmt?.Dispose();
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }
    }

    /// <summary>
    /// Drops virtual twins: an adapter without a PCI location (bus or device all ones) that has the same vendor, device
    /// and subsystem ids as one that has a real PCI location is another view of that card - for example the GPU
    /// paravirtualization adapter Windows creates for WSL2, whose LUID changes on every start. Reported as a second
    /// identical GPU it showed the real card's clocks and temperatures with none of its load. Two real identical cards
    /// both have PCI locations and are kept, as is a GPU that has no PCI location at all (e.g. some ARM SoCs). An adapter
    /// at the same PCI location as one listed before it is the same card seen again (Remote Desktop's view of the card
    /// listed it twice) and is dropped too.
    /// </summary>
    internal static IReadOnlyList<DxgiAdapterInfo> WithoutVirtualTwins(IReadOnlyList<DxgiAdapterInfo> adapters, Func<AdapterLuid, (uint Bus, uint Device, uint Function)?> pciAddress)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(pciAddress);
        static bool Real((uint Bus, uint Device, uint Function)? pci) => pci is { } p && p.Bus != uint.MaxValue && p.Device != 0xFFFF && p.Function != 0xFFFF;

        var real = adapters.Where(a => !a.IsSoftware && Real(pciAddress(a.Luid))).ToList();
        var seen = new HashSet<(uint, uint, uint)>();
        return adapters
            .Where(a => a.IsSoftware
                || (Real(pciAddress(a.Luid)) ? seen.Add(pciAddress(a.Luid)!.Value)
                    : !real.Any(r => r.VendorId == a.VendorId && r.DeviceId == a.DeviceId && r.SubSystemId == a.SubSystemId)))
            .ToList();
    }

    /// <summary>Keys for the hardware adapters, in DXGI order (shared with the display collector so both refer to the same GPU).</summary>
    internal static List<(DxgiAdapterInfo Adapter, string Key)> AssignKeys(IEnumerable<DxgiAdapterInfo> adapters)
    {
        var keyed = new List<(DxgiAdapterInfo Adapter, string Key)>();
        foreach (var adapter in adapters.Where(a => !a.IsSoftware))
        {
            var ordinal = keyed.Count(k => k.Adapter.VendorId == adapter.VendorId && k.Adapter.DeviceId == adapter.DeviceId && k.Adapter.SubSystemId == adapter.SubSystemId);
            keyed.Add((adapter, AdapterKey(adapter, ordinal)));
        }

        return keyed;
    }

    /// <summary>Stable adapter key: vendor, device and subsystem ids, plus an ordinal when identical cards are installed.</summary>
    internal static string AdapterKey(DxgiAdapterInfo adapter, int ordinal) =>
        string.Create(CultureInfo.InvariantCulture, $"gpu-{adapter.VendorId:x4}-{adapter.DeviceId:x4}-{adapter.SubSystemId:x8}") +
        (ordinal == 0 ? string.Empty : "-" + ordinal.ToString(CultureInfo.InvariantCulture));

    internal static string VendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        0x5143 => "Qualcomm",
        _ => "0x" + vendorId.ToString("X4", CultureInfo.InvariantCulture),
    };

    private GpuAdapterSample BuildAdapterSample(GpuAdapterBinding binding, GpuAdapterDescriptor descriptor, IReadOnlyList<IGpuDetailProvider> providers, bool pdhOk)
    {
        var luid = binding.Dxgi.Luid;
        Reading<double> Util(GpuEngineKind? kind)
        {
            if (!pdhOk || _engineUtilization is null)
            {
                return Reading<double>.Unavailable(_engineUtilization is null ? Availability.Unsupported : Availability.Error);
            }

            if (!_usage.HasData)
            {
                return Reading<double>.Pending;
            }

            var (overall, byKind) = _usage.ForAdapter(luid);
            return Reading<double>.Of(kind is null ? overall : byKind.GetValueOrDefault(kind.Value));
        }

        Reading<long> Memory(Dictionary<AdapterLuid, long> source, PdhCounter? counter) =>
            counter is null ? Reading<long>.Unavailable(Availability.Unsupported)
            : !pdhOk ? Reading<long>.Unavailable(Availability.Error)
            : source.TryGetValue(luid, out var bytes) ? Reading<long>.Of(bytes)
            : Reading<long>.Of(0);

        var details = GpuDetails.Merge(providers.Select(p => SafeRead(p, binding)).ToList());

        return new GpuAdapterSample(
            descriptor,
            UtilizationPercent: Util(null),
            Utilization3dPercent: Util(GpuEngineKind.ThreeD),
            UtilizationComputePercent: Util(GpuEngineKind.Compute),
            UtilizationDecodePercent: Util(GpuEngineKind.VideoDecode),
            UtilizationEncodePercent: Util(GpuEngineKind.VideoEncode),
            UtilizationCopyPercent: Util(GpuEngineKind.Copy),
            VramUsedBytes: Memory(_dedicatedUsed, _adapterDedicated),
            VramTotalBytes: descriptor.DedicatedMemoryBytes > 0 ? Reading<long>.Of(descriptor.DedicatedMemoryBytes) : Reading<long>.Unavailable(Availability.Unsupported),
            SharedUsedBytes: Memory(_sharedUsed, _adapterShared),
            details.TemperatureC,
            details.HotspotTemperatureC,
            details.CoreClockMhz,
            details.MemoryClockMhz,
            details.PowerW,
            details.PowerPercent,
            details.PowerLimitW,
            details.ThrottleFlags,
            details.FanRpm);
    }

    private GpuDetails SafeRead(IGpuDetailProvider provider, GpuAdapterBinding binding)
    {
        try
        {
            return provider.Read(binding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogProviderFailed(_logger, ex, provider.Id);
            return GpuDetails.None(Availability.Error);
        }
    }

    private List<GpuProcessUsage> BuildProcesses()
    {
        var keys = _adapters.ToDictionary(a => a.Binding.Dxgi.Luid, a => a.Binding.AdapterKey);
        var totals = _adapters.ToDictionary(a => a.Binding.Dxgi.Luid, a => a.Descriptor.DedicatedMemoryBytes);
        var result = new List<GpuProcessUsage>();
        var seen = new HashSet<(int, AdapterLuid)>();

        foreach (var (pid, luid, utilization, engine) in _usage.Processes)
        {
            if (keys.TryGetValue(luid, out var key))
            {
                seen.Add((pid, luid));
                result.Add(new GpuProcessUsage(pid, key, utilization, engine, MemoryFor(pid, luid)));
            }
        }

        // Processes holding VRAM without using an engine this tick are still worth knowing about.
        foreach (var ((pid, luid), bytes) in _processMemory)
        {
            if (bytes > 0 && !seen.Contains((pid, luid)) && keys.TryGetValue(luid, out var key))
            {
                result.Add(new GpuProcessUsage(pid, key, 0, null, MemoryFor(pid, luid)));
            }
        }

        return result
            .OrderByDescending(p => p.UtilizationPercent)
            .ThenByDescending(p => p.DedicatedMemoryBytes.GetValueOrDefault(0))
            .Take(MaxProcesses)
            .ToList();

        Reading<long> MemoryFor(int pid, AdapterLuid luid)
        {
            if (_processDedicated is null)
            {
                return Reading<long>.Unavailable(Availability.Unsupported);
            }

            var bytes = _processMemory.GetValueOrDefault((pid, luid));
            return IsPlausibleProcessMemory(bytes, totals.GetValueOrDefault(luid)) ? Reading<long>.Of(bytes) : Implausible(pid, bytes, totals.GetValueOrDefault(luid));
        }
    }

    /// <summary>
    /// Windows' per-process "Dedicated Usage" counter can exceed the adapter's physical VRAM (observed: dwm.exe at
    /// 33 GB on a 24 GB RTX 4090 while the adapter counter read 7.9 GB). Such values are not presented as fact.
    /// </summary>
    internal static bool IsPlausibleProcessMemory(long bytes, long adapterDedicatedBytes) =>
        adapterDedicatedBytes <= 0 || bytes <= adapterDedicatedBytes * 1.05;

    private Reading<long> Implausible(int pid, long bytes, long total)
    {
        if (_implausibleReported.Add(pid))
        {
            LogImplausibleProcessMemory(_logger, pid, bytes / (1024 * 1024), total / (1024 * 1024));
        }

        return Reading<long>.Unavailable(Availability.Error);
    }

    private void ReadPdh(bool pdhOk)
    {
        _usage.Reset();
        _dedicatedUsed.Clear();
        _sharedUsed.Clear();
        _processMemory.Clear();
        if (!pdhOk)
        {
            return;
        }

        var usage = _usage;
        _engineUtilization?.VisitArray(ref usage, static (ref GpuUsageAggregator u, ReadOnlySpan<char> name, Reading<double> value) =>
        {
            if (value.HasValue)
            {
                u.Add(name, value.Value!.Value);
            }
        });

        VisitAdapterMemory(_adapterDedicated, _dedicatedUsed);
        VisitAdapterMemory(_adapterShared, _sharedUsed);

        var processMemory = _processMemory;
        _processDedicated?.VisitArray(ref processMemory, static (ref Dictionary<(int, AdapterLuid), long> map, ReadOnlySpan<char> name, Reading<double> value) =>
        {
            if (value.HasValue && GpuInstanceNames.TryParseProcessMemory(name, out var pid, out var luid))
            {
                map[(pid, luid)] = map.GetValueOrDefault((pid, luid)) + (long)value.Value!.Value;
            }
        });
    }

    private static void VisitAdapterMemory(PdhCounter? counter, Dictionary<AdapterLuid, long> target)
    {
        counter?.VisitArray(ref target, static (ref Dictionary<AdapterLuid, long> map, ReadOnlySpan<char> name, Reading<double> value) =>
        {
            if (value.HasValue && GpuInstanceNames.TryParseAdapter(name, out var luid))
            {
                map[luid] = map.GetValueOrDefault(luid) + (long)value.Value!.Value; // sum physical adapters of a linked adapter
            }
        });
    }

    private void RefreshAdapters()
    {
        _lastRefresh = _timeProvider.GetTimestamp();
        _kmt?.Dispose();
        _kmt = D3dkmtAdapters.Open();

        var all = Dxgi.EnumerateAdapters();
        var kept = WithoutVirtualTwins(all, _kmt.TryGetPciAddress);
        _ignoredLuids = all.Where(a => a.IsSoftware || !kept.Contains(a)).Select(a => a.Luid).ToHashSet();
        var bindings = AssignKeys(kept)
            .Select(a => new GpuAdapterBinding(a.Key, a.Adapter, _kmt.TryGetPciAddress(a.Adapter.Luid)))
            .ToList();

        foreach (var provider in _providers)
        {
            try
            {
                LogProviderAttached(_logger, provider.Id, provider.Attach(bindings, _kmt));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogProviderFailed(_logger, ex, provider.Id);
            }
        }

        _adapters = bindings.Select(b =>
        {
            var providers = _providers.Where(p => SafeSupports(p, b)).ToList();
            var caps = _kmt.TryGetPerfCaps(b.Dxgi.Luid);
            var descriptor = new GpuAdapterDescriptor(
                b.AdapterKey,
                b.Dxgi.Description,
                VendorName(b.Dxgi.VendorId),
                b.Dxgi.VendorId,
                b.Dxgi.DeviceId,
                b.Dxgi.DedicatedVideoMemoryBytes,
                b.Dxgi.SharedSystemMemoryBytes,
                caps is { TemperatureMaxC: > 0 } ? Reading<double>.Of(caps.TemperatureMaxC) : Reading<double>.Unavailable(Availability.Unsupported),
                caps is { TemperatureWarningC: > 0 } ? Reading<double>.Of(caps.TemperatureWarningC) : Reading<double>.Unavailable(Availability.Unsupported),
                providers.Select(p => p.Id).ToList());
            LogAdapter(_logger, descriptor.Name, descriptor.AdapterKey, b.Dxgi.Luid.ToString(), string.Join(", ", descriptor.DetailSources));
            return (b, descriptor, (IReadOnlyList<IGpuDetailProvider>)providers);
        }).ToList();
    }

    private bool SafeSupports(IGpuDetailProvider provider, GpuAdapterBinding binding)
    {
        try
        {
            return provider.Supports(binding);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogProviderFailed(_logger, ex, provider.Id);
            return false;
        }
    }

    private PdhCounter? Add(string path)
    {
        var counter = _query!.TryAddCounter(path, out var status);
        if (counter is null)
        {
            LogCounterMissing(_logger, path, status);
        }

        return counter;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows reports {UsedMb} MB dedicated GPU memory for process {Pid}, more than the adapter's {TotalMb} MB; the value is recorded as an error, not as fact")]
    private static partial void LogImplausibleProcessMemory(ILogger logger, int pid, long usedMb, long totalMb);

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU adapter {Name} ({Key}, LUID {Luid}); detail sources: {Sources}")]
    private static partial void LogAdapter(ILogger logger, string name, string key, string luid, string sources);

    [LoggerMessage(Level = LogLevel.Information, Message = "GPU detail provider {Provider}: {Status}")]
    private static partial void LogProviderAttached(ILogger logger, string provider, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GPU detail provider {Provider} failed; its values are reported as errors")]
    private static partial void LogProviderFailed(ILogger logger, Exception exception, string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "PDH counter {Path} is not available on this system (status 0x{Status:X8})")]
    private static partial void LogCounterMissing(ILogger logger, string path, uint status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Performance counters (PDH) are unavailable; GPU utilization and memory use cannot be collected")]
    private static partial void LogPdhUnavailable(ILogger logger, Exception exception);
}
