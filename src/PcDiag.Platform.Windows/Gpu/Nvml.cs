using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Gpu;

/// <summary>PCI identity of an NVML device, used to match it to a DXGI/D3DKMT adapter.</summary>
public sealed record NvmlPciInfo(uint Bus, uint Device, uint PciDeviceId, uint PciSubSystemId);

/// <summary>
/// NVIDIA Management Library (nvml.dll, installed with the NVIDIA driver). Loaded dynamically from System32 only;
/// when absent, NVIDIA-specific metrics are simply unavailable. nvml.dll is never redistributed with this app.
/// </summary>
public sealed unsafe class NvmlLibrary : IDisposable
{
    /// <summary>NVML_SUCCESS.</summary>
    public const int Success = 0;

    private const int TemperatureGpu = 0;   // NVML_TEMPERATURE_GPU
    private const int ClockGraphics = 0;    // NVML_CLOCK_GRAPHICS
    private const int ClockMemory = 2;      // NVML_CLOCK_MEM

    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<int> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<nint, int, uint*, int> _getTemperature;
    private readonly delegate* unmanaged[Cdecl]<nint, int, uint*, int> _getClockInfo;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getPowerUsage;
    private readonly delegate* unmanaged[Cdecl]<nint, uint*, int> _getEnforcedPowerLimit;
    private readonly delegate* unmanaged[Cdecl]<nint, ulong*, int> _getClockReasons;
    private readonly delegate* unmanaged[Cdecl]<nint, PciInfo*, int> _getPciInfo;
    private bool _disposed;

    private NvmlLibrary(nint library, IReadOnlyList<nint> devices)
    {
        _library = library;
        Devices = devices;
        _shutdown = (delegate* unmanaged[Cdecl]<int>)Export("nvmlShutdown");
        _getTemperature = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Export("nvmlDeviceGetTemperature");
        _getClockInfo = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Export("nvmlDeviceGetClockInfo");
        _getPowerUsage = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Export("nvmlDeviceGetPowerUsage");
        _getEnforcedPowerLimit = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Export("nvmlDeviceGetEnforcedPowerLimit");
        _getPciInfo = (delegate* unmanaged[Cdecl]<nint, PciInfo*, int>)Export("nvmlDeviceGetPciInfo_v3");

        // Renamed in newer drivers; the older name remains as a deprecated alias.
        _getClockReasons = (delegate* unmanaged[Cdecl]<nint, ulong*, int>)(
            OptionalExport("nvmlDeviceGetCurrentClocksEventReasons") is var reasons and not 0
                ? reasons
                : Export("nvmlDeviceGetCurrentClocksThrottleReasons"));
    }

    /// <summary>Device handles, in NVML index order.</summary>
    public IReadOnlyList<nint> Devices { get; }

    /// <summary>Loads and initializes NVML. Returns null with a reason when unavailable (never throws).</summary>
    public static NvmlLibrary? TryLoad(out string reason)
    {
        var path = Path.Combine(Environment.SystemDirectory, "nvml.dll");
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var library))
        {
            reason = "nvml.dll not found (no NVIDIA driver installed)";
            return null;
        }

        try
        {
            var init = (delegate* unmanaged[Cdecl]<int>)NativeLibrary.GetExport(library, "nvmlInit_v2");
            var status = init();
            if (status != Success)
            {
                reason = $"nvmlInit_v2 failed ({status})";
                NativeLibrary.Free(library);
                return null;
            }

            var getCount = (delegate* unmanaged[Cdecl]<uint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetCount_v2");
            var getHandle = (delegate* unmanaged[Cdecl]<uint, nint*, int>)NativeLibrary.GetExport(library, "nvmlDeviceGetHandleByIndex_v2");
            uint count;
            var devices = new List<nint>();
            if (getCount(&count) == Success)
            {
                for (uint i = 0; i < count; i++)
                {
                    nint device;
                    if (getHandle(i, &device) == Success)
                    {
                        devices.Add(device);
                    }
                }
            }

            reason = $"{devices.Count} NVIDIA device(s)";
            return new NvmlLibrary(library, devices);
        }
        catch (EntryPointNotFoundException ex)
        {
            reason = "nvml.dll is missing an expected function: " + ex.Message;
            NativeLibrary.Free(library);
            return null;
        }
    }

    /// <summary>GPU core temperature in °C.</summary>
    public double? TemperatureC(nint device)
    {
        uint value;
        return _getTemperature(device, TemperatureGpu, &value) == Success ? value : null;
    }

    /// <summary>Current graphics clock in MHz.</summary>
    public double? CoreClockMhz(nint device)
    {
        uint value;
        return _getClockInfo(device, ClockGraphics, &value) == Success ? value : null;
    }

    /// <summary>Current memory clock in MHz.</summary>
    public double? MemoryClockMhz(nint device)
    {
        uint value;
        return _getClockInfo(device, ClockMemory, &value) == Success ? value : null;
    }

    /// <summary>Board power draw in watts.</summary>
    public double? PowerW(nint device)
    {
        uint milliwatts;
        return _getPowerUsage(device, &milliwatts) == Success ? milliwatts / 1000.0 : null;
    }

    /// <summary>Enforced power limit in watts.</summary>
    public double? PowerLimitW(nint device)
    {
        uint milliwatts;
        return _getEnforcedPowerLimit(device, &milliwatts) == Success ? milliwatts / 1000.0 : null;
    }

    /// <summary>Clock event ("throttle") reason bitmask; see <see cref="NvmlClockReasons"/>.</summary>
    public long? ClockReasons(nint device)
    {
        ulong value;
        return _getClockReasons(device, &value) == Success ? (long)value : null;
    }

    /// <summary>PCI identity of a device.</summary>
    public NvmlPciInfo? Pci(nint device)
    {
        PciInfo info;
        return _getPciInfo(device, &info) == Success ? new NvmlPciInfo(info.Bus, info.Device, info.PciDeviceId, info.PciSubSystemId) : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = _shutdown();
        NativeLibrary.Free(_library);
    }

    private nint Export(string name) => NativeLibrary.GetExport(_library, name);

    private nint OptionalExport(string name) => NativeLibrary.TryGetExport(_library, name, out var address) ? address : 0;

    /// <summary>nvmlPciInfo_t (v3).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;       // (device id << 16) | vendor id
        public uint PciSubSystemId;
        public fixed byte BusId[32];
    }
}

/// <summary>Bits of nvmlDeviceGetCurrentClocksEventReasons (nvml.h, nvmlClocksEventReason*).</summary>
public static class NvmlClockReasons
{
    /// <summary>Human-readable names for the documented bits.</summary>
    public static readonly IReadOnlyDictionary<long, string> Names = new Dictionary<long, string>
    {
        [0x1] = "GPU idle",
        [0x2] = "Applications clocks setting",
        [0x4] = "Software power cap",
        [0x8] = "Hardware slowdown",
        [0x10] = "Sync boost",
        [0x20] = "Software thermal slowdown",
        [0x40] = "Hardware thermal slowdown",
        [0x80] = "Hardware power brake slowdown",
        [0x100] = "Display clock setting",
    };

    /// <summary>Describes a bitmask, e.g. "Software power cap, Hardware thermal slowdown".</summary>
    public static string Describe(long flags) =>
        flags == 0 ? "None" : string.Join(", ", Names.Where(kv => (flags & kv.Key) != 0).Select(kv => kv.Value));
}
