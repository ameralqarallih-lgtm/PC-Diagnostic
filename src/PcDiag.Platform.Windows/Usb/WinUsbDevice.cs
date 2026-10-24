using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.Usb;

/// <summary>A USB device on Windows' WinUSB driver, found by its vendor and product id.</summary>
/// <param name="InstanceId">Its device instance id, e.g. USB\VID_1CBE&amp;PID_A088\34314314969EC304.</param>
/// <param name="Serial">Its serial number (the instance id's last part).</param>
/// <param name="Path">The device path WinUSB opens.</param>
public sealed record WinUsbDeviceInfo(string InstanceId, string Serial, string Path);

/// <summary>
/// Bulk transfers with a device on Windows' generic WinUSB driver (WinUsb_Initialize, WinUsb_WritePipe/ReadPipe with
/// a per-pipe timeout). WinUSB lets one program open a device at a time: while another holds it, opening fails with
/// "access denied". No administrator rights are needed.
/// </summary>
public sealed unsafe partial class WinUsbDevice : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 1 | 2;
    private const uint OpenExisting = 3;
    private const uint FlagOverlapped = 0x40000000;
    private const uint PipeTransferTimeout = 0x03;
    private const int CrSuccess = 0;
    private const uint FilterEnumerator = 0x1;
    private const uint FilterPresent = 0x100;

    private readonly SafeFileHandle _file;
    private readonly nint _winusb;
    private readonly byte _out;
    private readonly byte _in;
    private bool _disposed;

    private WinUsbDevice(SafeFileHandle file, nint winusb, byte outPipe, byte inPipe)
    {
        _file = file;
        _winusb = winusb;
        _out = outPipe;
        _in = inPipe;
    }

    /// <summary>
    /// The present devices with a vendor and product id that WinUSB drives (composite functions excluded), with the
    /// path to open each by: its device interface (the class the driver registered in Device Parameters).
    /// </summary>
    public static IReadOnlyList<WinUsbDeviceInfo> Find(int vendorId, int productId)
    {
        var prefix = FormattableString.Invariant($@"USB\VID_{vendorId:X4}&PID_{productId:X4}\");
        var found = new List<WinUsbDeviceInfo>();
        foreach (var id in PresentUsbDevices().Where(i => i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            using var parameters = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters");
            var guid = parameters?.GetValue("DeviceInterfaceGUID") as string
                ?? (parameters?.GetValue("DeviceInterfaceGUIDs") as string[])?.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(guid))
            {
                continue;
            }

            var serial = id[(id.LastIndexOf('\\') + 1)..];
            found.Add(new WinUsbDeviceInfo(id, serial, $@"\\?\{id.Replace('\\', '#').ToLowerInvariant()}#{guid.ToLowerInvariant()}"));
        }

        return found;
    }

    /// <summary>
    /// Opens a device for bulk transfers on two pipes (OUT, e.g. 0x01, and IN, e.g. 0x81). Null when it cannot be
    /// opened, with the reason (Windows' error 5 "access denied" when another program holds it).
    /// </summary>
    public static WinUsbDevice? Open(string path, byte outPipe, byte inPipe, out int error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = CreateFile(path, GenericRead | GenericWrite, ShareReadWrite, nint.Zero, OpenExisting, FlagOverlapped, nint.Zero);
        if (file.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            file.Dispose();
            return null;
        }

        if (!WinUsb_Initialize(file, out var handle))
        {
            error = Marshal.GetLastPInvokeError();
            file.Dispose();
            return null;
        }

        error = 0;
        return new WinUsbDevice(file, handle, outPipe, inPipe);
    }

    /// <summary>Writes one transfer; false when it failed or timed out.</summary>
    public bool Write(ReadOnlySpan<byte> data, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetTimeout(_out, timeout);
        fixed (byte* p = data)
        {
            return WinUsb_WritePipe(_winusb, _out, p, (uint)data.Length, out _, nint.Zero);
        }
    }

    /// <summary>Reads one transfer of up to <paramref name="max"/> bytes; null when nothing came in time.</summary>
    public byte[]? Read(int max, TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SetTimeout(_in, timeout);
        var buffer = new byte[max];
        fixed (byte* p = buffer)
        {
            return WinUsb_ReadPipe(_winusb, _in, p, (uint)max, out var read, nint.Zero) ? buffer[..(int)read] : null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WinUsb_Free(_winusb);
        _file.Dispose();
    }

    /// <summary>A Windows error as text.</summary>
    public static string Describe(int error) => error == 5
        ? "Another program is using it."
        : new System.ComponentModel.Win32Exception(error).Message;

    private void SetTimeout(byte pipe, TimeSpan timeout)
    {
        var ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue);
        WinUsb_SetPipePolicy(_winusb, pipe, PipeTransferTimeout, sizeof(uint), &ms);
    }

    /// <summary>The instance ids of the USB devices present now.</summary>
    private static List<string> PresentUsbDevices()
    {
        const uint flags = FilterEnumerator | FilterPresent;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, "USB", flags) != CrSuccess || length == 0)
            {
                return [];
            }

            var buffer = new char[length];
            fixed (char* p = buffer)
            {
                // A device can arrive between the two calls (the list grew): try again.
                if (CM_Get_Device_ID_ListW("USB", p, length, flags) == CrSuccess)
                {
                    return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
                }
            }
        }

        return [];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Initialize(SafeFileHandle device, out nint handle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WinUsb_Free(nint handle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WinUsb_WritePipe(nint handle, byte pipe, byte* buffer, uint length, out uint written, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WinUsb_ReadPipe(nint handle, byte pipe, byte* buffer, uint length, out uint read, nint overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WinUsb_SetPipePolicy(nint handle, byte pipe, uint policy, uint length, void* value);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int CM_Get_Device_ID_ListW(string filter, char* buffer, uint length, uint flags);
}
