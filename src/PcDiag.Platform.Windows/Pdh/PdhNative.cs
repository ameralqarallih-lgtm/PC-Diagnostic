using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Pdh;

/// <summary>
/// P/Invoke declarations for the Performance Data Helper API (pdh.dll).
/// Reference: https://learn.microsoft.com/windows/win32/perfctrs/using-the-pdh-functions-to-consume-counter-data
/// </summary>
internal static partial class PdhNative
{
    public const uint ErrorSuccess = 0;

    /// <summary>PDH_MORE_DATA: the buffer passed to PdhGetFormattedCounterArray is too small.</summary>
    public const uint PdhMoreData = 0x800007D2;

    /// <summary>PDH_INVALID_DATA: e.g. a rate counter that has only one raw sample so far.</summary>
    public const uint PdhInvalidData = 0xC0000BC6;

    /// <summary>PDH_CSTATUS_VALID_DATA.</summary>
    public const uint CStatusValidData = 0x00000000;

    /// <summary>PDH_CSTATUS_NEW_DATA.</summary>
    public const uint CStatusNewData = 0x00000001;

    /// <summary>PDH_FMT_DOUBLE.</summary>
    public const uint FormatDouble = 0x00000200;

    /// <summary>PDH_FMT_NOCAP100: do not clamp percentages at 100 (needed for "% Processor Utility").</summary>
    public const uint FormatNoCap100 = 0x00008000;

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    // The English variant accepts English counter paths on localized Windows installations.
    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(nint query, string fullCounterPath, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out PdhFmtCounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static unsafe partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, void* itemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint query);
}

/// <summary>PDH_FMT_COUNTERVALUE (x64 layout: status, padding, 8-byte union).</summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PdhFmtCounterValue
{
    [FieldOffset(0)]
    public uint CStatus;

    [FieldOffset(8)]
    public double DoubleValue;
}

/// <summary>PDH_FMT_COUNTERVALUE_ITEM_W.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValueItem
{
    public nint Name;
    public PdhFmtCounterValue Value;
}
