using System.Runtime.InteropServices;
using PcDiag.Platform.Windows.Processes;

namespace PcDiag.Platform.Windows.Services;

/// <summary>A service's program and description (QueryServiceConfig / QueryServiceConfig2).</summary>
/// <param name="BinaryPath">The command line Windows runs (often quoted, with arguments).</param>
public sealed record ServiceDetails(string? BinaryPath, string? Description);

/// <summary>Starts, stops and reconfigures services through the Service Control Manager (administrator rights).</summary>
public static unsafe partial class ServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceControlStop = 1;
    private const uint ServiceConfigDescription = 1;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ScStatusProcessInfo = 0;
    private const int Stopped = 1;
    private const int Running = 4;

    /// <summary>Starts a service and waits until it runs (at most <paramref name="wait"/>).</summary>
    public static ControlOutcome Start(string name, TimeSpan wait) => WithService(name, ServiceStart | ServiceQueryStatus, service =>
    {
        if (!StartService(service, 0, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 1056 ? ControlOutcome.Done : ControlOutcome.Failed(Describe(error)); // ERROR_SERVICE_ALREADY_RUNNING
        }

        return WaitFor(service, Running, wait) ? ControlOutcome.Done : ControlOutcome.Failed("The service did not finish starting in time; it may still start.");
    });

    /// <summary>Stops a service and waits until it has stopped (at most <paramref name="wait"/>).</summary>
    public static ControlOutcome Stop(string name, TimeSpan wait) => WithService(name, ServiceStop | ServiceQueryStatus, service =>
    {
        var status = stackalloc uint[7];
        if (!ControlService(service, ServiceControlStop, status))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 1062 ? ControlOutcome.Done : ControlOutcome.Failed(Describe(error)); // ERROR_SERVICE_NOT_ACTIVE
        }

        return WaitFor(service, Stopped, wait) ? ControlOutcome.Done : ControlOutcome.Failed("The service did not finish stopping in time; it may still stop.");
    });

    /// <summary>Sets how a service starts: 2 automatic (with <paramref name="delayed"/>), 3 manual, 4 disabled.</summary>
    public static ControlOutcome SetStartType(string name, int startType, bool delayed) => WithService(name, ServiceChangeConfig | ServiceQueryConfig, service =>
    {
        if (!ChangeServiceConfig(service, ServiceNoChange, (uint)startType, ServiceNoChange, null, null, 0, null, null, null, null))
        {
            return ControlOutcome.Failed(Describe(Marshal.GetLastPInvokeError()));
        }

        if (startType == 2)
        {
            var flag = delayed ? 1 : 0;
            _ = ChangeServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, &flag);
        }

        return ControlOutcome.Done;
    });

    /// <summary>The service's program and description, or null when it cannot be queried.</summary>
    public static ServiceDetails? QueryDetails(string name)
    {
        ServiceDetails? details = null;
        _ = WithService(name, ServiceQueryConfig, service =>
        {
            var buffer = new byte[8192];
            string? path = null;
            string? description = null;
            fixed (byte* data = buffer)
            {
                // QUERY_SERVICE_CONFIGW: dwServiceType, dwStartType, dwErrorControl, then LPWSTR lpBinaryPathName at 16 (x64).
                if (QueryServiceConfig(service, data, (uint)buffer.Length, out _) && *(char**)(data + 16) is var p && p != null)
                {
                    path = new string(p);
                }

                // SERVICE_DESCRIPTIONW { LPWSTR lpDescription; }
                if (QueryServiceConfig2(service, ServiceConfigDescription, data, (uint)buffer.Length, out _) && *(char**)data is var d && d != null)
                {
                    description = new string(d);
                }
            }

            details = new ServiceDetails(path, string.IsNullOrWhiteSpace(description) ? null : description);
            return ControlOutcome.Done;
        });
        return details;
    }

    /// <summary>The program file in a service command line (quoted, or up to ".exe").</summary>
    public static string? ProgramOf(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var text = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var path = exe > 0 ? text[..(exe + 4)] : text.Split(' ')[0];
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);
        }
        else if (path.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path);
        }

        return path;
    }

    private static ControlOutcome WithService(string name, uint access, Func<nint, ControlOutcome> action)
    {
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == 0)
        {
            return ControlOutcome.Failed(Describe(Marshal.GetLastPInvokeError()));
        }

        try
        {
            var service = OpenService(scm, name, access);
            if (service == 0)
            {
                return ControlOutcome.Failed(Describe(Marshal.GetLastPInvokeError()));
            }

            try
            {
                return action(service);
            }
            finally
            {
                _ = CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = CloseServiceHandle(scm);
        }
    }

    private static bool WaitFor(nint service, int state, TimeSpan wait)
    {
        var deadline = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        var status = stackalloc uint[9];
        while (true)
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, (byte*)status, 36, out _))
            {
                return false;
            }

            if (status[1] == state)
            {
                return true;
            }

            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            Thread.Sleep(250);
        }
    }

    private static string Describe(int error) => error switch
    {
        5 => "Windows refused: changing this service needs administrator rights, or the service protects itself.",
        1051 => "Other running services depend on it; stop those first.",
        1052 => "The service does not accept a stop request now.",
        1058 => "The service is disabled; set it to Manual or Automatic first.",
        1060 => "There is no such service.",
        1061 => "The service is busy starting or stopping; try again in a moment.",
        _ => $"Windows refused (error {error}).",
    };

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenService(nint scm, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartService(nint service, uint argumentCount, nint arguments);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, uint* status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint service, int infoLevel, byte* buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig(nint service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup, nint tagId, string? dependencies,
        string? serviceStartName, string? password, string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2(nint service, uint infoLevel, void* info);

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
