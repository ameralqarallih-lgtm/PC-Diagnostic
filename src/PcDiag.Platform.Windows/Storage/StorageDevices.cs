using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.Storage;

/// <summary>Identity of a physical disk from IOCTL_STORAGE_QUERY_PROPERTY and IOCTL_DISK_GET_DRIVE_GEOMETRY_EX.</summary>
/// <param name="SeeksPenalty">True for rotational media, false for SSDs, null when the device does not say.</param>
public sealed record StorageDeviceInfo(
    int Number,
    string Vendor,
    string Product,
    string Serial,
    string BusType,
    bool IsRemovable,
    bool? SeeksPenalty,
    long SizeBytes);

/// <summary>
/// Reads physical disk identity without administrator rights: the device is opened with no access rights,
/// which is sufficient for these FILE_ANY_ACCESS IOCTLs.
/// </summary>
public static partial class StorageDevices
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    /// <summary>Queries disk <paramref name="number"/> (\\.\PhysicalDriveN). Returns null if it cannot be opened or queried.</summary>
    public static StorageDeviceInfo? TryQuery(int number)
    {
        using var handle = CreateFile($@"\\.\PhysicalDrive{number.ToString(CultureInfo.InvariantCulture)}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var descriptor = QueryProperty(handle, StorageDeviceProperty, 1024);
        if (descriptor is null || descriptor.Length < 36)
        {
            return null;
        }

        var span = descriptor.AsSpan();
        var penalty = QueryProperty(handle, StorageDeviceSeekPenaltyProperty, 12);
        bool? seeks = penalty is { Length: >= 9 } ? penalty[8] != 0 : null;

        return new StorageDeviceInfo(
            number,
            Vendor: ReadString(descriptor, 12),
            Product: ReadString(descriptor, 16),
            Serial: ReadString(descriptor, 24),
            BusType: BusTypeName(BitConverter.ToInt32(span.Slice(28, 4))),
            IsRemovable: span[10] != 0,
            SeeksPenalty: seeks,
            SizeBytes: QuerySize(handle));
    }

    /// <summary>Reads a NUL-terminated ASCII string whose offset is stored at <paramref name="offsetField"/> (STORAGE_DEVICE_DESCRIPTOR).</summary>
    internal static string ReadString(ReadOnlySpan<byte> descriptor, int offsetField)
    {
        var offset = BitConverter.ToInt32(descriptor.Slice(offsetField, 4));
        if (offset <= 0 || offset >= descriptor.Length)
        {
            return string.Empty;
        }

        var end = descriptor[offset..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(descriptor.Slice(offset, end < 0 ? descriptor.Length - offset : end)).Trim();
    }

    /// <summary>STORAGE_BUS_TYPE names (winioctl.h).</summary>
    internal static string BusTypeName(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE1394",
        5 => "SSA",
        6 => "FibreChannel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "FileBackedVirtual",
        16 => "Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };

    /// <summary>
    /// Windows storage health per disk number (MSFT_PhysicalDisk.HealthStatus in root\Microsoft\Windows\Storage).
    /// WMI is slow, so call it on the inventory tier only. Returns an empty map when WMI is unavailable.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyDictionary<int, string> TryGetHealth()
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DeviceId, HealthStatus FROM MSFT_PhysicalDisk");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (var disk in searcher.Get().Cast<ManagementObject>())
            {
                using (disk)
                {
                    if (int.TryParse(disk["DeviceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        result[id] = Convert.ToUInt16(disk["HealthStatus"], CultureInfo.InvariantCulture) switch
                        {
                            0 => "Healthy",
                            1 => "Warning",
                            2 => "Unhealthy",
                            _ => "Unknown",
                        };
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            // Health is optional; callers report it as Unknown.
        }

        return result;
    }

    private static unsafe byte[]? QueryProperty(SafeFileHandle handle, int propertyId, int outputSize)
    {
        var query = stackalloc int[3];
        query[0] = propertyId;
        query[1] = PropertyStandardQuery;
        query[2] = 0;
        var output = new byte[outputSize];
        fixed (byte* buffer = output)
        {
            return DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, buffer, (uint)outputSize, out _, 0) ? output : null;
        }
    }

    private static unsafe long QuerySize(SafeFileHandle handle)
    {
        // DISK_GEOMETRY_EX: DISK_GEOMETRY (24 bytes) followed by LARGE_INTEGER DiskSize.
        var output = stackalloc byte[256];
        return DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, output, 256, out var returned, 0) && returned >= 32
            ? *(long*)(output + 24)
            : 0;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
