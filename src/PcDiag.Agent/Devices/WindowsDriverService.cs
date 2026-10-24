using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PcDiag.Agent.Screens;
using PcDiag.Core.Configuration;
using PcDiag.Core.Devices;
using PcDiag.Platform.Windows.Devices;
using PcDiag.Platform.Windows.Processes;
using PcDiag.Screens;

namespace PcDiag.Agent.Devices;

/// <summary>
/// Windows drivers for connected devices that have none (problem 28, 1 or 18), never the ones Windows starts with
/// (storage controllers, disks, processors, the system's own devices). A package is a zip with one INF and its files:
/// checked like any driver package (size, names, zip bombs), kept where only administrators reach it, read for what it
/// installs on this PC's architecture and who signed its catalog. Installing asks the person at the PC on its own
/// screen (2 minutes, else no), makes a restore point first when asked (and stops if it cannot), then lets Windows
/// install it for the device without forcing it. What was installed is written down so it can be removed again.
/// </summary>
internal sealed partial class WindowsDriverService(DataPaths paths, ILogger<WindowsDriverService> logger) : IWindowsDrivers, IDisposable
{
    private static readonly TimeSpan ConfirmFor = TimeSpan.FromMinutes(2);
    private static readonly HashSet<int> NoDriverProblems = [28, 1, 18];
    private static readonly HashSet<string> BootClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SCSIAdapter", "HDC", "System", "Processor", "Computer", "Volume", "VolumeSnapshot", "DiskDrive", "FloppyDisk", "CDROM",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly string _folder = DataPaths.WindowsDriversDirectory;

    /// <summary>The architecture the INF's models must be decorated with on this PC.</summary>
    internal static string Architecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "NTarm64" : "NTamd64";

    public void Dispose() => _one.Dispose();

    public WindowsDriversState State() => new(Devices(), Packages(Devices()));

    public string? Import(string upload)
    {
        EnsureFolder();
        var (id, problem) = DriverImport.WithUnpacked(upload, paths.DriverUploadsDirectory, DataPaths.DriverStagingDirectory, InfTop, "The zip holds no INF file, or more than one (a Windows driver package has one INF with its files).", unpacked =>
        {
            var inf = Directory.GetFiles(unpacked, "*.inf").Single();
            var file = InfFile.Parse(File.ReadAllText(inf));
            if (file.HardwareIdsFor(Architecture).Count == 0)
            {
                return ((string?)null, $"It installs nothing for this PC ({Architecture}): it is for another kind of Windows, or not a device driver.");
            }

            if (file.CatalogFor(Architecture) is not { } catalog || !File.Exists(Path.Combine(unpacked, catalog)))
            {
                return (null, "It has no signed catalog (.cat) for this PC; Windows would not install it.");
            }

            var hash = ScreenDriverPackage.HashFolder(unpacked, out var hashProblem);
            if (hash is null)
            {
                return (null, hashProblem);
            }

            var name = new string([.. Path.GetFileNameWithoutExtension(inf).ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);
            var packageId = $"{(name.Length > 40 ? name[..40] : name)}-{hash[..8]}";
            var target = Path.Combine(_folder, packageId);
            if (!Directory.Exists(target))
            {
                DriverImport.CopyFolder(unpacked, target);
            }

            return (packageId, null);
        });
        if (id is not null)
        {
            LogImported(logger, id);
        }

        return problem;
    }

    public async Task<string> InstallAsync(string id, string instanceId, bool restorePoint, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = Devices();
            var package = Packages(devices).FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("That driver package is not here.");
            var device = devices.FirstOrDefault(d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("That device is not connected, or it has a driver now.");
            if (!package.Matches.Contains(device.InstanceId, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("This driver package is not made for that device.");
            }

            if (package.Signature != "Signed")
            {
                throw new InvalidOperationException("Its catalog is not signed by a publisher Windows trusts; it is not installed.");
            }

            var folder = Path.Combine(_folder, id);
            var inf = Directory.GetFiles(folder, "*.inf").Single();
            var hardwareId = InfFile.Matching(InfFile.Parse(File.ReadAllText(inf)).HardwareIdsFor(Architecture), device)[0];
            var confirmed = await PcConfirm.AskAsync(
                "install a driver",
                $"Install a driver for {device.Name}?",
                [
                    ("Device", device.Name),
                    ("Matches", hardwareId),
                    ("Driver", $"{package.Inf} {package.Version} ({package.Date})"),
                    ("From", package.Provider ?? "(not said)"),
                    ("Signed by", package.Signer ?? "(unknown)"),
                    ("Restore point", restorePoint ? "Made first" : "None"),
                ],
                "Install",
                ConfirmFor).ConfigureAwait(false);
            if (!confirmed)
            {
                return "Not installed: it was not confirmed on the PC.";
            }

            if (restorePoint && DriverInstaller.CreateRestorePoint($"PC Diagnostic: before {package.Inf}") is { } restoreProblem)
            {
                return "Not installed: " + restoreProblem;
            }

            var result = await Task.Run(() => DriverInstaller.Install(inf, hardwareId), cancellationToken).ConfigureAwait(false);
            if (result.PublishedInf is { } published)
            {
                var installed = Installed();
                installed[id] = published;
                File.WriteAllText(Path.Combine(_folder, "installed.json"), JsonSerializer.Serialize(installed, Json));
            }

            LogInstalled(logger, id, device.InstanceId, result.Message);
            return result.Message;
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task<string?> RemoveAsync(string id, CancellationToken cancellationToken)
    {
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var folder = Path.Combine(_folder, id);
            if (!IsId(id) || !Directory.Exists(folder))
            {
                return "That driver package is not here.";
            }

            var installed = Installed();
            if (installed.TryGetValue(id, out var published))
            {
                var confirmed = await PcConfirm.AskAsync("remove a driver", $"Remove the driver {published} from Windows?", [("Package", id), ("In Windows as", published)], "Remove", ConfirmFor).ConfigureAwait(false);
                if (!confirmed)
                {
                    return "Not removed: it was not confirmed on the PC.";
                }

                var (removed, _, message) = await Task.Run(() => DriverInstaller.Uninstall(published), cancellationToken).ConfigureAwait(false);
                if (!removed)
                {
                    return "Not removed: " + message;
                }

                installed.Remove(id);
                File.WriteAllText(Path.Combine(_folder, "installed.json"), JsonSerializer.Serialize(installed, Json));
            }

            Directory.Delete(folder, recursive: true);
            LogRemoved(logger, id);
            return null;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>A Windows driver package's top: the folder with its one INF.</summary>
    internal static string? InfTop(IReadOnlyList<string> names)
    {
        var infs = names.Where(n => n.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (infs.Count != 1)
        {
            return null;
        }

        var at = infs[0].LastIndexOf('/');
        var top = at < 0 ? string.Empty : infs[0][..(at + 1)];
        return names.All(n => n.StartsWith(top, StringComparison.OrdinalIgnoreCase)) ? top : null;
    }

    private static bool IsId(string id) => id.Length is > 0 and <= 60 && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static string ProblemText(int code) => code switch
    {
        28 => "Windows has no driver for it",
        1 => "It is not set up correctly",
        18 => "Its driver needs installing again",
        _ => $"Problem {code}",
    };

    private static List<DeviceWithoutDriver> Devices() =>
        [.. DeviceTree.Nodes(n => n.Present && NoDriverProblems.Contains(n.ProblemCode) && !(n.Class is { } c && BootClasses.Contains(c)))
            .Select(n => new DeviceWithoutDriver(n.InstanceId, n.FriendlyName ?? n.Description ?? n.BusReportedName ?? n.InstanceId, n.Class, n.ProblemCode, ProblemText(n.ProblemCode), n.HardwareIds, n.CompatibleIds ?? []))];

    private List<WindowsDriverInfo> Packages(List<DeviceWithoutDriver> devices)
    {
        if (!Directory.Exists(_folder))
        {
            return [];
        }

        var installed = Installed();
        var list = new List<WindowsDriverInfo>();
        foreach (var folder in Directory.GetDirectories(_folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(folder);
            try
            {
                var inf = Directory.GetFiles(folder, "*.inf").Single();
                var file = InfFile.Parse(File.ReadAllText(inf));
                var ids = file.HardwareIdsFor(Architecture);
                var catalog = file.CatalogFor(Architecture) is { } cat ? Path.Combine(folder, cat) : null;
                var signature = catalog is not null && File.Exists(catalog) ? FileSignatures.Verify(catalog) : new FileSignature(Core.Telemetry.SignatureStatus.NotSigned, null);
                var matches = devices.Where(d => InfFile.Matching(ids, d).Count > 0).Select(d => d.InstanceId).ToList();
                var signed = signature.Status is Core.Telemetry.SignatureStatus.Embedded or Core.Telemetry.SignatureStatus.Catalog;
                var problem = !signed ? "Its catalog is not signed by a publisher Windows trusts."
                    : matches.Count == 0 && !installed.ContainsKey(id) ? "No connected device without a driver matches it."
                    : null;
                list.Add(new WindowsDriverInfo(id, Path.GetFileName(inf), file.Provider, file.DriverVersion, file.DriverDate, file.Class, signed ? "Signed" : signature.Status.ToString(), signature.Signer, ids, matches, installed.GetValueOrDefault(id), problem));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                list.Add(new WindowsDriverInfo(id, "?", null, null, null, null, "Unknown", null, [], [], installed.GetValueOrDefault(id), "It could not be read: " + ex.Message));
            }
        }

        return list;
    }

    private Dictionary<string, string> Installed()
    {
        try
        {
            var file = Path.Combine(_folder, "installed.json");
            return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The packages' folder: administrators and SYSTEM only (set each time).</summary>
    private void EnsureFolder()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(_folder);
        if (info.Exists)
        {
            info.SetAccessControl(security);
        }
        else
        {
            info.Create(security);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows driver package {Id} imported")]
    private static partial void LogImported(ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows driver package {Id} for {Device}: {Outcome}")]
    private static partial void LogInstalled(ILogger logger, string id, string device, string outcome);

    [LoggerMessage(Level = LogLevel.Information, Message = "Windows driver package {Id} removed")]
    private static partial void LogRemoved(ILogger logger, string id);
}
