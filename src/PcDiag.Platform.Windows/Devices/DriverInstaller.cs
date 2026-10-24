using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcDiag.Platform.Windows.Devices;

/// <summary>What installing a driver did.</summary>
/// <param name="Installed">The device now uses it.</param>
/// <param name="RestartNeeded">Windows needs a restart to finish.</param>
/// <param name="PublishedInf">The name Windows gave the package in its driver store (oemNN.inf).</param>
/// <param name="Message">What happened, in words.</param>
public sealed record DriverInstallResult(bool Installed, bool RestartNeeded, string? PublishedInf, string Message);

/// <summary>
/// Installs and removes Windows drivers with Windows' own functions (administrator): the package is put in the
/// driver store (SetupCopyOEMInf), then offered to the devices with a hardware id (UpdateDriverForPlugAndPlayDevices,
/// never forced: Windows installs it only when it suits the device better than what it has); removed with
/// DiUninstallDriver. A System Restore point can be made first.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class DriverInstaller
{
    private const uint SpostPath = 1;
    private const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInfInUseByDevices = unchecked((int)0xE000023D);

    /// <summary>Puts an INF (and its files beside it) in the driver store, then installs it for the devices with <paramref name="hardwareId"/>.</summary>
    public static DriverInstallResult Install(string infPath, string hardwareId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);
        var published = new char[260];
        if (!SetupCopyOEMInfW(infPath, Path.GetDirectoryName(infPath), SpostPath, 0, published, (uint)published.Length, out _, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            return new(false, false, null, $"Windows did not take the driver package into its driver store ({Describe(error)}).");
        }

        var publishedPath = new string(published).TrimEnd('\0');
        var publishedName = Path.GetFileName(publishedPath);
        if (!UpdateDriverForPlugAndPlayDevicesW(0, hardwareId, publishedPath, 0, out var reboot))
        {
            var error = Marshal.GetLastPInvokeError();
            return error switch
            {
                ErrorNoMoreItems => new(false, false, publishedName, "Windows kept the device's current driver: this one does not suit it better. It is in the driver store; remove it to take it out."),
                ErrorNoSuchDevinst => new(false, false, publishedName, "No connected device has that hardware id now."),
                _ => new(false, false, publishedName, $"Windows did not install it ({Describe(error)})."),
            };
        }

        return new(true, reboot, publishedName, reboot ? "Installed: restart Windows to finish." : "Installed: the device uses it now.");
    }

    /// <summary>Uninstalls a driver package from the devices using it and removes it from the driver store (oemNN.inf).</summary>
    public static (bool Removed, bool RestartNeeded, string Message) Uninstall(string publishedInf)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedInf);
        if (!System.Text.RegularExpressions.Regex.IsMatch(publishedInf, @"^oem\d{1,5}\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return (false, false, "Only drivers installed here (oemNN.inf) are removed.");
        }

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", publishedInf);
        if (!DiUninstallDriverW(0, path, 0, out var reboot))
        {
            var error = Marshal.GetLastPInvokeError();
            return (false, false, error == ErrorInfInUseByDevices ? "A device still uses it." : $"Windows did not remove it ({Describe(error)}).");
        }

        return (true, reboot, reboot ? "Removed: restart Windows to finish." : "Removed.");
    }

    /// <summary>
    /// Makes a System Restore point (a device driver install), or says why it could not. Windows may decline to make
    /// one when another was made in the last 24 hours (it then reports success).
    /// </summary>
    public static string? CreateRestorePoint(string description)
    {
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var parameters = restore.GetMethodParameters("CreateRestorePoint");
            parameters["Description"] = description.Length > 64 ? description[..64] : description;
            parameters["RestorePointType"] = 10u;
            parameters["EventType"] = 100u;
            using var result = restore.InvokeMethod("CreateRestorePoint", parameters, null);
            var code = Convert.ToUInt32(result?["ReturnValue"] ?? 1u, System.Globalization.CultureInfo.InvariantCulture);
            return code == 0 ? null : $"System Restore did not make a restore point (code {code}); is it turned on for the system drive?";
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return "System Restore did not make a restore point: " + ex.Message;
        }
    }

    private static string Describe(int error) => $"{new System.ComponentModel.Win32Exception(error).Message} 0x{error:X8}";

    [LibraryImport("setupapi.dll", EntryPoint = "SetupCopyOEMInfW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupCopyOEMInfW(string sourceInf, string? sourceMedia, uint mediaType, uint copyStyle, [Out] char[] destination, uint destinationSize, out uint requiredSize, nint destinationComponent);

    [LibraryImport("newdev.dll", EntryPoint = "UpdateDriverForPlugAndPlayDevicesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateDriverForPlugAndPlayDevicesW(nint parent, string hardwareId, string fullInfPath, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);

    [LibraryImport("newdev.dll", EntryPoint = "DiUninstallDriverW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiUninstallDriverW(nint parent, string infPath, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool reboot);
}
