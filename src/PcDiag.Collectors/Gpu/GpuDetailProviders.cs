using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Collectors.Gpu;

/// <summary>An adapter known to the GPU collector, with what detail providers need to match it.</summary>
/// <param name="AdapterKey">Stable adapter key.</param>
/// <param name="Dxgi">DXGI description (vendor/device ids, LUID).</param>
/// <param name="PciAddress">PCI location from D3DKMT, when available.</param>
public sealed record GpuAdapterBinding(string AdapterKey, DxgiAdapterInfo Dxgi, (uint Bus, uint Device, uint Function)? PciAddress);

/// <summary>Detail readings for one adapter. Fields a provider cannot supply are unavailable.</summary>
public sealed record GpuDetails(
    Reading<double> TemperatureC,
    Reading<double> HotspotTemperatureC,
    Reading<double> CoreClockMhz,
    Reading<double> MemoryClockMhz,
    Reading<double> PowerW,
    Reading<double> PowerPercent,
    Reading<double> PowerLimitW,
    Reading<long> ThrottleFlags,
    Reading<double> FanRpm)
{
    /// <summary>All fields unavailable for the same reason.</summary>
    public static GpuDetails None(Availability reason)
    {
        var d = Reading<double>.Unavailable(reason);
        return new GpuDetails(d, d, d, d, d, d, d, Reading<long>.Unavailable(reason), d);
    }

    /// <summary>
    /// Merges providers field by field: the first provider (highest priority first) with a value wins; if none has
    /// one, the most informative reason is kept (RequiresElevation over Error over Unsupported).
    /// </summary>
    public static GpuDetails Merge(IReadOnlyList<GpuDetails> byPriority)
    {
        if (byPriority.Count == 0)
        {
            return None(Availability.Unsupported);
        }

        return new GpuDetails(
            Pick(byPriority, d => d.TemperatureC),
            Pick(byPriority, d => d.HotspotTemperatureC),
            Pick(byPriority, d => d.CoreClockMhz),
            Pick(byPriority, d => d.MemoryClockMhz),
            Pick(byPriority, d => d.PowerW),
            Pick(byPriority, d => d.PowerPercent),
            Pick(byPriority, d => d.PowerLimitW),
            Pick(byPriority, d => d.ThrottleFlags),
            Pick(byPriority, d => d.FanRpm));
    }

    private static Reading<T> Pick<T>(IReadOnlyList<GpuDetails> details, Func<GpuDetails, Reading<T>> field)
        where T : struct
    {
        foreach (var d in details)
        {
            var value = field(d);
            if (value.HasValue)
            {
                return value;
            }
        }

        var reasons = details.Select(field).Select(r => r.Availability).ToList();
        foreach (var preferred in (Availability[])[Availability.RequiresElevation, Availability.Error, Availability.Pending, Availability.Disabled])
        {
            if (reasons.Contains(preferred))
            {
                return Reading<T>.Unavailable(preferred);
            }
        }

        return Reading<T>.Unavailable(Availability.Unsupported);
    }
}

/// <summary>
/// A source of per-adapter GPU details (temperatures, clocks, power). Implementations: D3DKMT (any vendor),
/// NVML (NVIDIA); LibreHardwareMonitor and vendor SDKs can be added later without changing the collector.
/// </summary>
public interface IGpuDetailProvider : IDisposable
{
    /// <summary>Provider id, e.g. "nvml".</summary>
    string Id { get; }

    /// <summary>Higher wins when two providers report the same field.</summary>
    int Priority { get; }

    /// <summary>Called when the adapter list is (re)built. Returns a status message for logs.</summary>
    string Attach(IReadOnlyList<GpuAdapterBinding> adapters, D3dkmtAdapters kmt);

    /// <summary>Whether this provider has data for the adapter.</summary>
    bool Supports(GpuAdapterBinding adapter);

    /// <summary>Reads details for one adapter.</summary>
    GpuDetails Read(GpuAdapterBinding adapter);
}

/// <summary>Vendor-neutral details through D3DKMT (verified in docs/spikes/2026-09-28-gpu-apis.md).</summary>
public sealed class D3dkmtGpuDetailProvider : IGpuDetailProvider
{
    private D3dkmtAdapters? _kmt;

    /// <inheritdoc />
    public string Id => "d3dkmt";

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public string Attach(IReadOnlyList<GpuAdapterBinding> adapters, D3dkmtAdapters kmt)
    {
        _kmt = kmt;
        return $"{adapters.Count(Supports)} adapter(s) with perf data";
    }

    /// <inheritdoc />
    public bool Supports(GpuAdapterBinding adapter) => _kmt?.TryGetPerfData(adapter.Dxgi.Luid) is { AllZero: false };

    /// <inheritdoc />
    public GpuDetails Read(GpuAdapterBinding adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var perf = _kmt?.TryGetPerfData(adapter.Dxgi.Luid);
        if (perf is null || perf.AllZero)
        {
            // Some drivers (e.g. many integrated GPUs) answer with all zeros: that means "not reported", not 0 °C.
            return GpuDetails.None(Availability.Unsupported);
        }

        var node = _kmt!.TryGetNodePerfData(adapter.Dxgi.Luid, 0);
        var unsupported = Reading<double>.Unavailable(Availability.Unsupported);
        return new GpuDetails(
            TemperatureC: perf.TemperatureC > 0 ? Reading<double>.Of(perf.TemperatureC) : unsupported,
            HotspotTemperatureC: unsupported,
            CoreClockMhz: node is null ? unsupported : Reading<double>.Of(node.FrequencyMhz),
            MemoryClockMhz: perf.MemoryClockMhz > 0 ? Reading<double>.Of(perf.MemoryClockMhz) : unsupported,
            PowerW: unsupported,
            PowerPercent: Reading<double>.Of(perf.PowerPercent),
            PowerLimitW: unsupported,
            ThrottleFlags: Reading<long>.Unavailable(Availability.Unsupported),
            FanRpm: Reading<double>.Of(perf.FanRpm));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The D3DKMT adapter set is owned by the collector.
    }
}

/// <summary>NVIDIA details through NVML (nvml.dll from the NVIDIA driver).</summary>
public sealed class NvmlGpuDetailProvider : IGpuDetailProvider
{
    private const uint NvidiaVendorId = 0x10DE;

    private readonly Dictionary<string, nint> _devices = new(StringComparer.Ordinal);
    private NvmlLibrary? _nvml;
    private bool _loadAttempted;
    private string _loadReason = "not loaded";

    /// <inheritdoc />
    public string Id => "nvml";

    /// <inheritdoc />
    public int Priority => 100;

    /// <inheritdoc />
    public string Attach(IReadOnlyList<GpuAdapterBinding> adapters, D3dkmtAdapters kmt)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        if (!_loadAttempted)
        {
            _loadAttempted = true;
            if (adapters.Any(a => a.Dxgi.VendorId == NvidiaVendorId))
            {
                _nvml = NvmlLibrary.TryLoad(out _loadReason);
            }
            else
            {
                _loadReason = "no NVIDIA adapter";
            }
        }

        _devices.Clear();
        if (_nvml is null)
        {
            return _loadReason;
        }

        var devices = _nvml.Devices.Select(d => (Handle: d, Pci: _nvml.Pci(d))).ToList();
        foreach (var adapter in adapters.Where(a => a.Dxgi.VendorId == NvidiaVendorId))
        {
            var pciDeviceId = (adapter.Dxgi.DeviceId << 16) | adapter.Dxgi.VendorId;
            var candidates = devices.Where(d => d.Pci?.PciDeviceId == pciDeviceId && !_devices.ContainsValue(d.Handle)).ToList();
            if (candidates.Count > 1 && adapter.PciAddress is { } address)
            {
                candidates = candidates.Where(d => d.Pci!.Bus == address.Bus).ToList();
            }

            if (candidates.Count >= 1)
            {
                _devices[adapter.AdapterKey] = candidates[0].Handle;
            }
        }

        return $"{_loadReason}; matched {_devices.Count}";
    }

    /// <inheritdoc />
    public bool Supports(GpuAdapterBinding adapter) => adapter is not null && _devices.ContainsKey(adapter.AdapterKey);

    /// <inheritdoc />
    public GpuDetails Read(GpuAdapterBinding adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (_nvml is null || !_devices.TryGetValue(adapter.AdapterKey, out var device))
        {
            return GpuDetails.None(Availability.Unsupported);
        }

        return new GpuDetails(
            TemperatureC: Of(_nvml.TemperatureC(device)),
            HotspotTemperatureC: Reading<double>.Unavailable(Availability.Unsupported), // not in public NVML
            CoreClockMhz: Of(_nvml.CoreClockMhz(device)),
            MemoryClockMhz: Of(_nvml.MemoryClockMhz(device)),
            PowerW: Of(_nvml.PowerW(device)),
            PowerPercent: Reading<double>.Unavailable(Availability.Unsupported),
            PowerLimitW: Of(_nvml.PowerLimitW(device)),
            ThrottleFlags: _nvml.ClockReasons(device) is { } flags ? Reading<long>.Of(flags) : Reading<long>.Unavailable(Availability.Unsupported),
            FanRpm: Reading<double>.Unavailable(Availability.Unsupported)); // NVML reports fan %, not RPM

        static Reading<double> Of(double? value) => value is { } v ? Reading<double>.Of(v) : Reading<double>.Unavailable(Availability.Unsupported);
    }

    /// <inheritdoc />
    public void Dispose() => _nvml?.Dispose();
}

/// <summary>
/// The hotspot of NVIDIA GeForce RTX 50 series (Blackwell) GPUs, which NVAPI and NVML no longer report, from the GPU's
/// thermal registers through PawnIO (<see cref="NvidiaThermalRegisters"/>). An adapter is taken on only when a first
/// read gives a valid value, so other GPUs (which the module refuses) keep their hotspot from LibreHardwareMonitor.
/// Needs the elevated recorder and the PawnIO driver; without them no adapter is taken on and the reason is logged.
/// </summary>
public sealed class PawnIoNvidiaGpuDetailProvider : IGpuDetailProvider
{
    private const uint NvidiaVendorId = 0x10DE;

    private readonly HashSet<string> _supported = new(StringComparer.Ordinal);
    private readonly Func<(NvidiaThermalRegisters? Registers, string Reason)> _open;
    private NvidiaThermalRegisters? _registers;
    private bool _openAttempted;
    private string _openReason = "not loaded";

    /// <summary>Creates the provider (the PawnIO module is loaded when an NVIDIA adapter is first seen).</summary>
    public PawnIoNvidiaGpuDetailProvider()
        : this(() => (NvidiaThermalRegisters.TryOpen(out var reason), reason))
    {
    }

    internal PawnIoNvidiaGpuDetailProvider(Func<(NvidiaThermalRegisters? Registers, string Reason)> open) => _open = open;

    /// <inheritdoc />
    public string Id => "pawnio-nvidia";

    /// <inheritdoc />
    public int Priority => 90;

    /// <inheritdoc />
    public string Attach(IReadOnlyList<GpuAdapterBinding> adapters, D3dkmtAdapters kmt)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _supported.Clear();
        var nvidia = adapters.Where(a => a.Dxgi.VendorId == NvidiaVendorId && a.PciAddress is not null).ToList();
        if (nvidia.Count == 0)
        {
            return "no NVIDIA adapter with a PCI address";
        }

        if (!_openAttempted)
        {
            _openAttempted = true;
            (_registers, _openReason) = _open();
        }

        if (_registers is null)
        {
            return _openReason;
        }

        var refused = new List<string>();
        foreach (var adapter in nvidia)
        {
            var (bus, device, function) = adapter.PciAddress!.Value;
            if (_registers.TryReadHotspot(bus, device, function, out var error) is not null)
            {
                _supported.Add(adapter.AdapterKey);
            }
            else
            {
                refused.Add($"{adapter.Dxgi.Description} ({(error == 50 ? "not an RTX 50 series GPU" : $"error {error}")})");
            }
        }

        return $"hotspot from thermal registers for {_supported.Count} adapter(s)" + (refused.Count > 0 ? $"; not for {string.Join(", ", refused)}" : string.Empty);
    }

    /// <inheritdoc />
    public bool Supports(GpuAdapterBinding adapter) => adapter is not null && _supported.Contains(adapter.AdapterKey);

    /// <inheritdoc />
    public GpuDetails Read(GpuAdapterBinding adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var details = GpuDetails.None(Availability.Unsupported);
        if (_registers is null || adapter.PciAddress is not { } address || !_supported.Contains(adapter.AdapterKey))
        {
            return details;
        }

        // A GPU that stops answering (asleep, or the bus busy with another tool) has a gap for this sample, not a value.
        return details with
        {
            HotspotTemperatureC = _registers.TryReadHotspot(address.Bus, address.Device, address.Function, out _) is { } celsius
                ? Reading<double>.Of(celsius)
                : Reading<double>.Unavailable(Availability.Error),
        };
    }

    /// <inheritdoc />
    public void Dispose() => _registers?.Dispose();
}
