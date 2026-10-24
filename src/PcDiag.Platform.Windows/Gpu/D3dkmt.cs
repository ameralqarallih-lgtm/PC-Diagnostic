using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Gpu;

/// <summary>A GPU adapter's locally unique identifier (valid until reboot or driver restart).</summary>
/// <param name="HighPart">LUID high part.</param>
/// <param name="LowPart">LUID low part.</param>
public readonly record struct AdapterLuid(int HighPart, uint LowPart)
{
    /// <summary>Formats like PDH instance names: <c>0x00000000_0x0001C5A5</c>.</summary>
    public override string ToString() => $"0x{HighPart:X8}_0x{LowPart:X8}";
}

/// <summary>Adapter performance data (D3DKMT_ADAPTER_PERFDATA). Units converted: °C, MHz, percent.</summary>
public sealed record AdapterPerfData(double TemperatureC, double MemoryClockMhz, double PowerPercent, double FanRpm, bool AllZero);

/// <summary>Driver-reported adapter limits (D3DKMT_ADAPTER_PERFDATACAPS).</summary>
public sealed record AdapterPerfCaps(double TemperatureMaxC, double TemperatureWarningC, double MaxFanRpm);

/// <summary>Engine (node) performance data (D3DKMT_NODE_PERFDATA). Units converted to MHz.</summary>
public sealed record NodePerfData(double FrequencyMhz, double MaxFrequencyMhz);

/// <summary>
/// Vendor-neutral GPU performance data through the D3DKMT thunks in gdi32.dll (what Task Manager uses).
/// Enum values and struct sizes were verified empirically; see docs/spikes/2026-09-28-gpu-apis.md.
/// </summary>
public sealed class D3dkmtAdapters : IDisposable
{
    private readonly List<(uint Handle, AdapterLuid Luid)> _adapters;
    private readonly Dictionary<uint, int> _nodeStructSize = [];

    private D3dkmtAdapters(List<(uint, AdapterLuid)> adapters) => _adapters = adapters;

    /// <summary>Adapters opened by D3DKMTEnumAdapters2.</summary>
    public IReadOnlyList<AdapterLuid> Luids => _adapters.Select(a => a.Luid).ToList();

    /// <summary>Opens every adapter. Returns an empty set if the thunks are unavailable (never throws).</summary>
    public static unsafe D3dkmtAdapters Open()
    {
        try
        {
            var enumArgs = default(D3dkmtNative.EnumAdapters2);
            if (D3dkmtNative.D3DKMTEnumAdapters2(ref enumArgs) != 0 || enumArgs.NumAdapters == 0)
            {
                return new D3dkmtAdapters([]);
            }

            var infos = new D3dkmtNative.AdapterInfo[enumArgs.NumAdapters];
            fixed (D3dkmtNative.AdapterInfo* buffer = infos)
            {
                enumArgs.Adapters = buffer;
                if (D3dkmtNative.D3DKMTEnumAdapters2(ref enumArgs) != 0)
                {
                    return new D3dkmtAdapters([]);
                }
            }

            return new D3dkmtAdapters(infos.Take((int)enumArgs.NumAdapters)
                .Select(i => (i.Adapter, new AdapterLuid(i.LuidHigh, i.LuidLow)))
                .ToList());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return new D3dkmtAdapters([]);
        }
    }

    /// <summary>Reads adapter perf data (temperature, memory clock, power %, fan). Null when unsupported.</summary>
    public unsafe AdapterPerfData? TryGetPerfData(AdapterLuid luid)
    {
        if (!TryGetHandle(luid, out var handle))
        {
            return null;
        }

        var data = default(D3dkmtNative.AdapterPerfData);
        if (!Query(handle, D3dkmtNative.TypeAdapterPerfData, &data, sizeof(D3dkmtNative.AdapterPerfData)))
        {
            return null;
        }

        var allZero = data.Temperature == 0 && data.MemoryFrequency == 0 && data.Power == 0 && data.FanRpm == 0;
        return new AdapterPerfData(data.Temperature / 10.0, data.MemoryFrequency / 1_000_000.0, data.Power / 10.0, data.FanRpm, allZero);
    }

    /// <summary>Reads driver-reported limits. Null when unsupported or all zero.</summary>
    public unsafe AdapterPerfCaps? TryGetPerfCaps(AdapterLuid luid)
    {
        if (!TryGetHandle(luid, out var handle))
        {
            return null;
        }

        var caps = default(D3dkmtNative.AdapterPerfDataCaps);
        if (!Query(handle, D3dkmtNative.TypeAdapterPerfDataCaps, &caps, sizeof(D3dkmtNative.AdapterPerfDataCaps))
            || (caps.TemperatureMax == 0 && caps.TemperatureWarning == 0 && caps.MaxFanRpm == 0))
        {
            return null;
        }

        return new AdapterPerfCaps(caps.TemperatureMax / 10.0, caps.TemperatureWarning / 10.0, caps.MaxFanRpm);
    }

    /// <summary>Reads one engine's clock. Null when unsupported or zero.</summary>
    public unsafe NodePerfData? TryGetNodePerfData(AdapterLuid luid, uint nodeOrdinal)
    {
        if (!TryGetHandle(luid, out var handle))
        {
            return null;
        }

        var data = default(D3dkmtNative.NodePerfData);
        data.NodeOrdinal = nodeOrdinal;

        // Documented as 64 bytes; this OS accepts 56 (verified). Try documented first, remember what works.
        var ok = false;
        if (_nodeStructSize.TryGetValue(handle, out var knownSize))
        {
            ok = Query(handle, D3dkmtNative.TypeNodePerfData, &data, knownSize);
        }
        else
        {
            foreach (var size in (int[])[sizeof(D3dkmtNative.NodePerfData), 56])
            {
                if (Query(handle, D3dkmtNative.TypeNodePerfData, &data, size))
                {
                    _nodeStructSize[handle] = size;
                    ok = true;
                    break;
                }
            }
        }

        if (!ok || data.Frequency == 0)
        {
            return null;
        }

        return new NodePerfData(data.Frequency / 1_000_000.0, data.MaxFrequency / 1_000_000.0);
    }

    /// <summary>PCI location (bus, device, function), used to match vendor APIs to adapters.</summary>
    public unsafe (uint Bus, uint Device, uint Function)? TryGetPciAddress(AdapterLuid luid)
    {
        if (!TryGetHandle(luid, out var handle))
        {
            return null;
        }

        var address = default(D3dkmtNative.AdapterAddress);
        return Query(handle, D3dkmtNative.TypeAdapterAddress, &address, sizeof(D3dkmtNative.AdapterAddress))
            ? (address.BusNumber, address.DeviceNumber, address.FunctionNumber)
            : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (handle, _) in _adapters)
        {
            var close = new D3dkmtNative.CloseAdapter { Adapter = handle };
            _ = D3dkmtNative.D3DKMTCloseAdapter(ref close);
        }

        _adapters.Clear();
    }

    private bool TryGetHandle(AdapterLuid luid, out uint handle)
    {
        foreach (var (h, l) in _adapters)
        {
            if (l == luid)
            {
                handle = h;
                return true;
            }
        }

        handle = 0;
        return false;
    }

    private static unsafe bool Query(uint handle, int type, void* data, int size)
    {
        var query = new D3dkmtNative.QueryAdapterInfo { Adapter = handle, Type = type, PrivateDriverData = data, PrivateDriverDataSize = (uint)size };
        return D3dkmtNative.D3DKMTQueryAdapterInfo(ref query) == 0;
    }
}

internal static unsafe partial class D3dkmtNative
{
    public const int TypeAdapterAddress = 6;
    public const int TypeNodePerfData = 61;
    public const int TypeAdapterPerfData = 62;
    public const int TypeAdapterPerfDataCaps = 63;

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTEnumAdapters2(ref EnumAdapters2 args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);

    [LibraryImport("gdi32.dll")]
    public static partial int D3DKMTCloseAdapter(ref CloseAdapter args);

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterInfo
    {
        public uint Adapter;
        public uint LuidLow;
        public int LuidHigh;
        public uint NumOfSources;
        public int PrecisePresentRegionsPreferred;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnumAdapters2
    {
        public uint NumAdapters;
        public AdapterInfo* Adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public void* PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CloseAdapter
    {
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterAddress
    {
        public uint BusNumber;
        public uint DeviceNumber;
        public uint FunctionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AdapterPerfDataCaps
    {
        public uint PhysicalAdapterIndex;
        public ulong MaxMemoryBandwidth;
        public ulong MaxPcieBandwidth;
        public uint MaxFanRpm;
        public uint TemperatureMax;
        public uint TemperatureWarning;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NodePerfData
    {
        public uint NodeOrdinal;
        public uint PhysicalAdapterIndex;
        public ulong Frequency;
        public ulong MaxFrequency;
        public ulong MaxFrequencyOC;
        public uint Voltage;
        public uint VoltageMax;
        public uint VoltageMaxOC;
        public ulong MaxTransitionLatency;
        public ulong Reserved;
    }
}
