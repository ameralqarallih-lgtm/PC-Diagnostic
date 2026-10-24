using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PcDiag.Platform.Windows.Storage;

/// <summary>Windows storage reliability counters for one disk (MSFT_StorageReliabilityCounter). Null fields are not reported.</summary>
/// <param name="IsNvme">The disk is on the NVMe bus (MSFT_PhysicalDisk.BusType 17).</param>
public sealed record StorageReliabilityInfo(int DiskNumber, double? TemperatureC, double? TemperatureMaxC, double? WearPercent, long? ReadErrorsTotal, long? WriteErrorsTotal, long? PowerOnHours, bool IsNvme = false);

/// <summary>Result of a reliability query.</summary>
/// <param name="AccessDenied">The query needs administrator rights.</param>
public sealed record StorageReliabilityResult(IReadOnlyList<StorageReliabilityInfo> Disks, bool AccessDenied);

/// <summary>
/// Reads MSFT_StorageReliabilityCounter through each MSFT_PhysicalDisk (root\Microsoft\Windows\Storage) —
/// the source behind Get-StorageReliabilityCounter. Requires administrator rights. Slow; call about once a minute.
/// </summary>
public static class StorageReliability
{
    private const int BusTypeNvme = 17;

    /// <summary>Reads counters for all physical disks.</summary>
    public static StorageReliabilityResult Read()
    {
        var disks = new List<StorageReliabilityInfo>();
        var physicalDisks = 0;
        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
            using var physical = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            physical.Options.Timeout = TimeSpan.FromSeconds(15);
            foreach (var disk in physical.Get().Cast<ManagementObject>())
            {
                using (disk)
                {
                    if (!int.TryParse(disk["DeviceId"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    {
                        continue;
                    }

                    physicalDisks++;
                    var nvme = disk["BusType"] is { } bus && Convert.ToInt32(bus, CultureInfo.InvariantCulture) == BusTypeNvme;

                    foreach (var counter in disk.GetRelated("MSFT_StorageReliabilityCounter").Cast<ManagementObject>())
                    {
                        using (counter)
                        {
                            disks.Add(new StorageReliabilityInfo(
                                number,
                                Number(counter, "Temperature"),
                                Number(counter, "TemperatureMax"),
                                Number(counter, "Wear"),
                                Integer(counter, "ReadErrorsTotal"),
                                Integer(counter, "WriteErrorsTotal"),
                                Integer(counter, "PowerOnHours"),
                                nvme));
                        }
                    }
                }
            }

            // Without administrator rights WMI returns no related counters instead of an access-denied error.
            return new StorageReliabilityResult(disks, AccessDenied: disks.Count == 0 && physicalDisks > 0 && !IsElevated());
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new StorageReliabilityResult([], AccessDenied: true);
        }
        catch (UnauthorizedAccessException)
        {
            return new StorageReliabilityResult([], AccessDenied: true);
        }
        catch (Exception ex) when (ex is ManagementException or COMException)
        {
            return new StorageReliabilityResult(disks, AccessDenied: false);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // A value of 0 for temperature means "not reported" in practice (devices without the log page).
    private static double? Number(ManagementObject o, string property) =>
        o[property] is { } value && Convert.ToDouble(value, CultureInfo.InvariantCulture) is var d && d > 0 ? d : null;

    private static long? Integer(ManagementObject o, string property) =>
        o[property] is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;
}
