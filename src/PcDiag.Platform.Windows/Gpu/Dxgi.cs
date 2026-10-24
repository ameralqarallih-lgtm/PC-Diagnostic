using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Gpu;

/// <summary>A GPU adapter as described by DXGI (DXGI_ADAPTER_DESC1).</summary>
public sealed record DxgiAdapterInfo(
    string Description,
    uint VendorId,
    uint DeviceId,
    uint SubSystemId,
    uint Revision,
    long DedicatedVideoMemoryBytes,
    long SharedSystemMemoryBytes,
    AdapterLuid Luid,
    bool IsSoftware);

/// <summary>
/// Enumerates adapters with IDXGIFactory1::EnumAdapters1 / IDXGIAdapter1::GetDesc1, calling the COM vtable directly
/// (no COM interop layer needed for three methods). Vtable slots follow dxgi.h declaration order.
/// </summary>
public static unsafe partial class Dxgi
{
    private const int SlotRelease = 2;
    private const int SlotEnumAdapters1 = 12; // IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) → EnumAdapters1
    private const int SlotGetDesc1 = 10;      // IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) → GetDesc1
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint FlagSoftware = 2;

    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>Lists adapters. Returns an empty list if DXGI is unavailable (never throws).</summary>
    public static IReadOnlyList<DxgiAdapterInfo> EnumerateAdapters()
    {
        var result = new List<DxgiAdapterInfo>();
        nint factory;
        try
        {
            if (CreateDXGIFactory1(IidFactory1, out factory) < 0 || factory == 0)
            {
                return result;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return result;
        }

        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)VTable(factory)[SlotEnumAdapters1];
            for (uint i = 0; ; i++)
            {
                nint adapter;
                var hr = enumAdapters(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0)
                {
                    break;
                }

                try
                {
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)VTable(adapter)[SlotGetDesc1];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) >= 0)
                    {
                        result.Add(new DxgiAdapterInfo(
                            new string(desc.Description).TrimEnd('\0'),
                            desc.VendorId,
                            desc.DeviceId,
                            desc.SubSysId,
                            desc.Revision,
                            (long)desc.DedicatedVideoMemory,
                            (long)desc.SharedSystemMemory,
                            new AdapterLuid(desc.LuidHigh, desc.LuidLow),
                            (desc.Flags & FlagSoftware) != 0));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }

        return result;
    }

    private static nint* VTable(nint comObject) => *(nint**)comObject;

    private static void Release(nint comObject) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)VTable(comObject)[SlotRelease])(comObject);

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
