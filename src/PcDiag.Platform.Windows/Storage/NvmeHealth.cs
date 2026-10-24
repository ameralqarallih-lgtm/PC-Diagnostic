using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.Storage;

/// <summary>
/// An NVMe drive's SMART / health information log (log page 02h, NVMe base specification §5.16.1.3): what the
/// drive itself reports about its temperature, wear and errors.
/// </summary>
/// <param name="CriticalWarning">Bit flags: 0 spare below threshold, 1 temperature, 2 reliability degraded, 3 read-only, 4 volatile backup failed.</param>
/// <param name="TemperatureC">Composite temperature.</param>
/// <param name="AvailableSparePercent">Spare capacity left.</param>
/// <param name="AvailableSpareThresholdPercent">Below this the drive warns.</param>
/// <param name="PercentageUsed">Estimate of the drive's life used (can exceed 100).</param>
/// <param name="DataReadBytes">Data read from the drive.</param>
/// <param name="DataWrittenBytes">Data written to the drive.</param>
/// <param name="PowerCycles">Times the drive was powered on.</param>
/// <param name="PowerOnHours">Hours powered on.</param>
/// <param name="UnsafeShutdowns">Power lost without a proper shutdown.</param>
/// <param name="MediaErrors">Unrecovered data integrity errors.</param>
/// <param name="ErrorLogEntries">Entries in the drive's error log over its life.</param>
/// <param name="WarningTemperatureMinutes">Minutes spent above the warning temperature.</param>
/// <param name="CriticalTemperatureMinutes">Minutes spent above the critical temperature.</param>
public sealed record NvmeHealthInfo(
    int CriticalWarning,
    double? TemperatureC,
    int AvailableSparePercent,
    int AvailableSpareThresholdPercent,
    int PercentageUsed,
    double DataReadBytes,
    double DataWrittenBytes,
    double PowerCycles,
    double PowerOnHours,
    double UnsafeShutdowns,
    double MediaErrors,
    double ErrorLogEntries,
    long WarningTemperatureMinutes,
    long CriticalTemperatureMinutes);

/// <summary>
/// Reads the NVMe health log through IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceProtocolSpecificProperty), the way
/// Windows' own tools do with the standard NVMe driver (stornvme). Needs administrator rights; drives that are not
/// NVMe, or drivers that do not pass the request on, return null.
/// </summary>
public static partial class NvmeHealth
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProtocolSpecificProperty = 50;
    private const int PropertyStandardQuery = 0;
    private const int ProtocolTypeNvme = 3;
    private const int NvmeDataTypeLogPage = 2;
    private const int HealthLogPage = 0x02;
    private const int LogLength = 512;

    /// <summary>STORAGE_PROPERTY_QUERY up to AdditionalParameters.</summary>
    private const int QueryHeader = 8;

    /// <summary>STORAGE_PROTOCOL_SPECIFIC_DATA.</summary>
    private const int ProtocolData = 40;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;

    /// <summary>The health log of disk <paramref name="number"/> (\\.\PhysicalDriveN), or null.</summary>
    public static NvmeHealthInfo? TryRead(int number)
    {
        var path = $@"\\.\PhysicalDrive{number.ToString(CultureInfo.InvariantCulture)}";
        using var handle = CreateFile(path, GenericRead | GenericWrite, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[QueryHeader + ProtocolData + LogLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(span, StorageDeviceProtocolSpecificProperty);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], PropertyStandardQuery);
        var protocol = span[QueryHeader..];
        BinaryPrimitives.WriteInt32LittleEndian(protocol, ProtocolTypeNvme);
        BinaryPrimitives.WriteInt32LittleEndian(protocol[4..], NvmeDataTypeLogPage);
        BinaryPrimitives.WriteInt32LittleEndian(protocol[8..], HealthLogPage); // ProtocolDataRequestValue
        BinaryPrimitives.WriteInt32LittleEndian(protocol[12..], 0); // ProtocolDataRequestSubValue
        BinaryPrimitives.WriteInt32LittleEndian(protocol[16..], ProtocolData); // ProtocolDataOffset (after this structure)
        BinaryPrimitives.WriteInt32LittleEndian(protocol[20..], LogLength); // ProtocolDataLength

        if (!Query(handle, buffer, out var returned))
        {
            return null;
        }

        // The answer is a STORAGE_PROTOCOL_DATA_DESCRIPTOR (Version, Size, then the protocol data with the log after it).
        var answer = span[QueryHeader..];
        var offset = BinaryPrimitives.ReadInt32LittleEndian(answer[16..]);
        var length = BinaryPrimitives.ReadInt32LittleEndian(answer[20..]);
        var start = QueryHeader + offset;
        if (offset < ProtocolData || length < 200 || start + 200 > Math.Min(returned, (uint)buffer.Length))
        {
            return null;
        }

        return Parse(span.Slice(start, Math.Min(length, LogLength)));
    }

    /// <summary>Decodes a health log page (at least the first 200 bytes).</summary>
    public static NvmeHealthInfo? Parse(ReadOnlySpan<byte> log)
    {
        if (log.Length < 200)
        {
            return null;
        }

        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        return new NvmeHealthInfo(
            CriticalWarning: log[0],
            TemperatureC: kelvin is > 200 and < 500 ? kelvin - 273 : null,
            AvailableSparePercent: log[3],
            AvailableSpareThresholdPercent: log[4],
            PercentageUsed: log[5],
            // Data units are thousands of 512-byte units.
            DataReadBytes: Counter(log[32..]) * 512_000,
            DataWrittenBytes: Counter(log[48..]) * 512_000,
            PowerCycles: Counter(log[112..]),
            PowerOnHours: Counter(log[128..]),
            UnsafeShutdowns: Counter(log[144..]),
            MediaErrors: Counter(log[160..]),
            ErrorLogEntries: Counter(log[176..]),
            WarningTemperatureMinutes: BinaryPrimitives.ReadUInt32LittleEndian(log[192..]),
            CriticalTemperatureMinutes: BinaryPrimitives.ReadUInt32LittleEndian(log[196..]));
    }

    /// <summary>A 128-bit little-endian counter as a double (exact far beyond any real drive's counts).</summary>
    private static double Counter(ReadOnlySpan<byte> bytes) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes) + (BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) * 18446744073709551616.0);

    private static unsafe bool Query(SafeFileHandle handle, byte[] buffer, out uint returned)
    {
        fixed (byte* pointer = buffer)
        {
            return DeviceIoControl(handle, IoctlStorageQueryProperty, pointer, (uint)buffer.Length, pointer, (uint)buffer.Length, out returned, 0);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize, void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);
}
