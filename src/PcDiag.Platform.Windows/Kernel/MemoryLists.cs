using System.Runtime.InteropServices;
using PcDiag.Platform.Windows.Pdh;

namespace PcDiag.Platform.Windows.Kernel;

/// <summary>
/// The standby list (memory Windows keeps as cache and gives away whenever it is needed) and emptying it, as tools
/// like RAMMap do. Emptying it is rarely needed: it helps only when a program stutters because Windows hands out
/// standby pages too slowly; afterwards files are read from disk again until the cache refills.
/// </summary>
public static unsafe partial class MemoryLists
{
    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;

    /// <summary>Standby and free memory now (bytes), from the Memory performance counters; nulls when unreadable.</summary>
    public static (long? Standby, long? Free) Query()
    {
        try
        {
            using var query = PdhQuery.Open();
            var core = query.TryAddCounter(@"\Memory\Standby Cache Core Bytes", out _);
            var normal = query.TryAddCounter(@"\Memory\Standby Cache Normal Priority Bytes", out _);
            var reserve = query.TryAddCounter(@"\Memory\Standby Cache Reserve Bytes", out _);
            var free = query.TryAddCounter(@"\Memory\Free & Zero Page List Bytes", out _);
            if (query.Collect() != 0)
            {
                return (null, null);
            }

            double? Value(PdhCounter? counter) => counter?.GetDouble().Value;
            var standby = new[] { Value(core), Value(normal), Value(reserve) }.Where(v => v is not null).Sum(v => v!.Value);
            return (core is null && normal is null && reserve is null ? null : (long)standby, Value(free) is { } f ? (long)f : null);
        }
        catch (PdhException)
        {
            return (null, null);
        }
    }

    /// <summary>Empties the standby list (needs administrator rights). Returns null when done, else why not.</summary>
    public static string? PurgeStandby()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
        {
            return "Windows did not grant the right to empty the standby list (the recorder must run as administrator).";
        }

        var command = MemoryPurgeStandbyList;
        var status = NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int));
        return status == 0 ? null : $"Windows refused (status 0x{status:X8}).";
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SePrivilegeEnabled };
            return AdjustTokenPrivileges(token, false, &privileges, 0, 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtSetSystemInformation(int informationClass, void* information, int length);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TokenPrivileges* newState, uint bufferLength, nint previousState, nint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>The Recycle Bin of the user (all drives) and the DNS client cache.</summary>
public static unsafe partial class ShellCleanup
{
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    /// <summary>Size and item count of the Recycle Bin (all drives).</summary>
    public static (long Bytes, long Items) RecycleBinSize()
    {
        var info = new RecycleBinInfo { Size = sizeof(RecycleBinInfo) };
        return SHQueryRecycleBin(null, &info) == 0 ? (info.Bytes, info.Items) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin without asking. Returns null when done, else why not.</summary>
    public static string? EmptyRecycleBin()
    {
        var result = SHEmptyRecycleBin(0, null, NoConfirmation | NoProgressUi | NoSound);
        return result is 0 or unchecked((int)0x8000FFFF) ? null : $"Windows refused (0x{result:X8})."; // E_UNEXPECTED: already empty
    }

    /// <summary>Clears the DNS client cache (as "ipconfig /flushdns").</summary>
    public static bool FlushDns() => DnsFlushResolverCache();

    // SHQUERYRBINFO: 8-byte packing on 64-bit Windows (cbSize, padding, i64Size, i64NumItems).
    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? rootPath, RecycleBinInfo* info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(nint window, string? rootPath, uint flags);

    [LibraryImport("dnsapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();
}
