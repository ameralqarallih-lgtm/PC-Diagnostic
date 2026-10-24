using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Network;

/// <summary>NDIS interface flags from MIB_IF_ROW2.InterfaceAndOperStatusFlags.</summary>
/// <param name="IsHardware">HardwareInterface: backed by a physical device.</param>
/// <param name="IsFilter">FilterInterface: an NDIS filter layer (WFP, QoS, vSwitch extensions) stacked on another interface.</param>
public readonly record struct InterfaceFlags(bool IsHardware, bool IsFilter);

/// <summary>
/// Reads interface flags with GetIfTable2. .NET's NetworkInterface also lists NDIS filter layers as separate interfaces,
/// which would count the same traffic several times; the FilterInterface flag identifies them reliably.
/// Offsets verified on Windows 11 against NetworkInterface.Id (see docs/spikes/2026-09-29-network-interfaces.md).
/// </summary>
public static partial class InterfaceFlagsReader
{
    private const int RowSize = 1352;       // sizeof(MIB_IF_ROW2) on x64
    private const int TableOffset = 8;      // MIB_IF_TABLE2.Table (8-byte aligned after ULONG NumEntries)
    private const int GuidOffset = 12;      // after NET_LUID (8) and InterfaceIndex (4)
    private const int FlagsOffset = 1152;   // InterfaceAndOperStatusFlags bitfield
    private const byte HardwareBit = 0x1;
    private const byte FilterBit = 0x2;

    /// <summary>Flags by interface GUID. Empty when the call fails (callers then fall back to descriptions).</summary>
    public static unsafe IReadOnlyDictionary<Guid, InterfaceFlags> Read()
    {
        var result = new Dictionary<Guid, InterfaceFlags>();
        if (GetIfTable2(out var table) != 0 || table == 0)
        {
            return result;
        }

        try
        {
            var count = *(uint*)table;
            for (var i = 0; i < count; i++)
            {
                var row = (byte*)table + TableOffset + (i * RowSize);
                var guid = *(Guid*)(row + GuidOffset);
                var flags = row[FlagsOffset];
                result[guid] = new InterfaceFlags((flags & HardwareBit) != 0, (flags & FilterBit) != 0);
            }
        }
        finally
        {
            FreeMibTable(table);
        }

        return result;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetIfTable2(out nint table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(nint memory);
}
