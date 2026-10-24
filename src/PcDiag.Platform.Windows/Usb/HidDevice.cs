using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.Usb;

/// <summary>
/// A HID device read for its input reports (a sensor's values), found by its USB vendor and product id among the HID
/// interfaces Windows lists. Opened for reading only, shared (Windows keeps its own use of a keyboard or mouse).
/// </summary>
public sealed partial class HidDevice : IDisposable
{
    private static readonly Guid HidInterface = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private readonly FileStream _stream;
    private readonly byte[] _buffer;

    private HidDevice(FileStream stream, int reportBytes)
    {
        _stream = stream;
        _buffer = new byte[reportBytes];
    }

    /// <summary>The input report's size in bytes (the report id first).</summary>
    public int ReportBytes => _buffer.Length;

    /// <summary>The paths of the HID interfaces with this vendor and product id that are present now.</summary>
    public static IReadOnlyList<string> Find(int vendorId, int productId)
    {
        var match = FormattableString.Invariant($"vid_{vendorId:x4}&pid_{productId:x4}");
        return [.. InterfacePaths(HidInterface).Where(p => p.Contains(match, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>Opens the first present device with this vendor and product id that can be read; null when none.</summary>
    public static HidDevice? Open(int vendorId, int productId)
    {
        foreach (var path in Find(vendorId, productId))
        {
            var handle = CreateFile(path, GenericRead, ShareReadWrite, 0, OpenExisting, FlagOverlapped, 0);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                continue;
            }

            var length = InputReportLength(handle);
            if (length <= 0)
            {
                handle.Dispose();
                continue;
            }

            return new HidDevice(new FileStream(handle, FileAccess.Read, 0, isAsync: true), length);
        }

        return null;
    }

    /// <summary>The next input report, or null when none came within the time.</summary>
    public byte[]? Read(TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource(timeout);
        try
        {
            var read = _stream.ReadAsync(_buffer, limit.Token).AsTask().GetAwaiter().GetResult();
            return read > 0 ? _buffer[..read] : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Closes the device.</summary>
    public void Dispose() => _stream.Dispose();

    /// <summary>The device's input report length (HidP_GetCaps), or 0 when it cannot be read.</summary>
    private static int InputReportLength(SafeFileHandle handle)
    {
        if (!HidD_GetPreparsedData(handle, out var data))
        {
            return 0;
        }

        try
        {
            var caps = new HidCaps();
            return HidP_GetCaps(data, ref caps) == HidpStatusSuccess ? caps.InputReportByteLength : 0;
        }
        finally
        {
            _ = HidD_FreePreparsedData(data);
        }
    }

    /// <summary>The device interfaces of a class that are present now (CM_Get_Device_Interface_List).</summary>
    private static List<string> InterfacePaths(Guid interfaceClass)
    {
        var guid = interfaceClass;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_Size(out var size, ref guid, 0, CmGetDeviceInterfaceListPresent) != CrSuccess || size <= 1)
            {
                return [];
            }

            var buffer = new char[size];
            int result;
            unsafe
            {
                fixed (char* chars = buffer)
                {
                    result = CM_Get_Device_Interface_List(ref guid, 0, chars, size, CmGetDeviceInterfaceListPresent);
                }
            }

            if (result == CrBufferSmall)
            {
                continue;
            }

            return result == CrSuccess ? [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)] : [];
        }

        return [];
    }

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FlagOverlapped = 0x40000000;
    private const uint CmGetDeviceInterfaceListPresent = 0;
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);

    [LibraryImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_FreePreparsedData(nint data);

    [DllImport("hid.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int HidP_GetCaps(nint data, ref HidCaps caps);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    private static partial int CM_Get_Device_Interface_List_Size(out uint size, ref Guid interfaceClass, nint deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_Interface_ListW")]
    private static unsafe partial int CM_Get_Device_Interface_List(ref Guid interfaceClass, nint deviceId, char* buffer, uint size, uint flags);
}
