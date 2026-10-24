using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Services;

/// <summary>A service as returned by EnumServicesStatusEx (SC_ENUM_PROCESS_INFO).</summary>
/// <param name="State">SERVICE_* current state (1 stopped … 7 paused).</param>
/// <param name="ProcessId">Hosting process ID, 0 when not running.</param>
/// <param name="ServiceType">SERVICE_* type flags.</param>
/// <param name="ControlsAccepted">SERVICE_ACCEPT_* flags (1: it accepts a stop request).</param>
public sealed record ServiceStatusRecord(string Name, string DisplayName, int State, int ProcessId, int ServiceType, int ControlsAccepted = 0);

/// <summary>Start configuration of a service.</summary>
/// <param name="StartType">SERVICE_BOOT_START (0) … SERVICE_DISABLED (4).</param>
/// <param name="DelayedAutoStart">Automatic (delayed start).</param>
public readonly record struct ServiceStartConfig(int StartType, bool DelayedAutoStart);

/// <summary>Read-only Service Control Manager queries (SC_MANAGER_ENUMERATE_SERVICE; no admin needed).</summary>
public static unsafe partial class ServiceControlManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;         // own process + share process
    private const uint ServiceStateAll = 0x3;
    private const int ErrorMoreData = 234;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;

    /// <summary>Lists Win32 services with state and PID in one call. Returns an empty list on failure.</summary>
    public static IReadOnlyList<ServiceStatusRecord> Enumerate()
    {
        var scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == 0)
        {
            return [];
        }

        try
        {
            uint resume = 0;
            _ = EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, null, 0, out var needed, out _, ref resume, null);
            var buffer = new byte[needed + 4096];
            fixed (byte* data = buffer)
            {
                resume = 0;
                if (!EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, data, (uint)buffer.Length, out _, out var count, ref resume, null))
                {
                    return [];
                }

                // ENUM_SERVICE_STATUS_PROCESSW: LPWSTR name, LPWSTR display, SERVICE_STATUS_PROCESS (9 DWORDs), padded to 56 bytes.
                var result = new List<ServiceStatusRecord>((int)count);
                for (var i = 0; i < count; i++)
                {
                    var entry = data + (i * 56);
                    var name = new string(*(char**)entry);
                    var display = new string(*(char**)(entry + 8));
                    var status = (uint*)(entry + 16);
                    result.Add(new ServiceStatusRecord(name, display, (int)status[1], (int)status[7], (int)status[0], (int)status[2]));
                }

                return result;
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    /// <summary>Start types for the given services (QueryServiceConfig + delayed auto-start). Missing entries could not be read.</summary>
    public static IReadOnlyDictionary<string, ServiceStartConfig> QueryStartConfigs(IEnumerable<string> serviceNames)
    {
        ArgumentNullException.ThrowIfNull(serviceNames);
        var result = new Dictionary<string, ServiceStartConfig>(StringComparer.OrdinalIgnoreCase);
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == 0)
        {
            return result;
        }

        var buffer = new byte[8192];
        try
        {
            foreach (var name in serviceNames)
            {
                var service = OpenService(scm, name, ServiceQueryConfig);
                if (service == 0)
                {
                    continue;
                }

                try
                {
                    fixed (byte* data = buffer)
                    {
                        // QUERY_SERVICE_CONFIGW: dwServiceType, dwStartType, ...
                        if (!QueryServiceConfig(service, data, (uint)buffer.Length, out _))
                        {
                            continue;
                        }

                        var startType = *(int*)(data + 4);
                        var delayed = false;
                        if (startType == 2 && QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, data, (uint)buffer.Length, out _))
                        {
                            delayed = *(int*)data != 0; // SERVICE_DELAYED_AUTO_START_INFO { BOOL fDelayedAutostart; }
                        }

                        result[name] = new ServiceStartConfig(startType, delayed);
                    }
                }
                finally
                {
                    _ = CloseServiceHandle(service);
                }
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }

        return result;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint scm, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(nint scm, int infoLevel, uint serviceType, uint serviceState, byte* services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(nint service, byte* config, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2(nint service, uint infoLevel, byte* buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
