using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Storage;

namespace PcDiag.Collectors.Sensors;

/// <summary>
/// Disk temperature, wear, error totals and power-on hours from Windows storage reliability counters
/// (the same data as Get-StorageReliabilityCounter), and for NVMe drives the drive's own health log: Windows often
/// leaves the counters empty for NVMe drives on its standard driver, while the drive answers the health log page
/// (temperature, life used, spare, media errors, unsafe shutdowns, data written). Needs administrator rights; read
/// once a minute. Higher priority than LibreHardwareMonitor for disk temperature because it is the Windows-native source.
/// </summary>
public sealed class StorageReliabilitySensorProvider(ElevationInfo elevation, TimeProvider timeProvider, Func<int, NvmeHealthInfo?>? nvmeHealth = null) : IHardwareSensorProvider
{
    /// <inheritdoc />
    public string ProviderId => "storage";

    /// <inheritdoc />
    public int Priority => 60;

    /// <inheritdoc />
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public ValueTask<string> InitializeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(elevation.IsElevated ? "storage reliability counters (elevated)" : "storage reliability counters need elevation; disk temperatures will be RequiresElevation");

    /// <inheritdoc />
    public ValueTask<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var readings = new List<SensorReading>();
        var numbers = StorageDevices.TryGetHealth().Keys.ToList(); // disk numbers (cheap enough at this interval)

        if (!elevation.IsElevated)
        {
            foreach (var number in numbers)
            {
                readings.Add(Reading(number, "temperature", SensorKind.Temperature, "Temperature", "°C", null, Availability.RequiresElevation));
            }

            return ValueTask.FromResult(new HardwareSnapshot(timeProvider.GetUtcNow(), ProviderId, readings));
        }

        var result = StorageReliability.Read();
        var counters = result.Disks.GroupBy(d => d.DiskNumber).ToDictionary(g => g.Key, g => g.First());
        foreach (var number in counters.Keys.Union(numbers).Order())
        {
            var disk = counters.GetValueOrDefault(number);
            // The drive's own log fills what the counters leave empty (and adds what only NVMe reports).
            var nvme = disk is { TemperatureC: not null, WearPercent: not null, PowerOnHours: not null } && nvmeHealth is null ? null : ReadNvme(number);
            readings.Add(Reading(number, "temperature", SensorKind.Temperature, "Temperature", "°C", disk?.TemperatureC ?? nvme?.TemperatureC));
            readings.Add(Reading(number, "temperature_max", SensorKind.Temperature, "Temperature (max)", "°C", disk?.TemperatureMaxC));
            readings.Add(Reading(number, "wear", SensorKind.Other, "Wear", "%", disk?.WearPercent ?? nvme?.PercentageUsed));
            readings.Add(Reading(number, "read_errors", SensorKind.Other, "Read errors (total)", "count", disk?.ReadErrorsTotal));
            readings.Add(Reading(number, "write_errors", SensorKind.Other, "Write errors (total)", "count", disk?.WriteErrorsTotal));
            readings.Add(Reading(number, "power_on_hours", SensorKind.Other, "Power-on hours", "h", disk?.PowerOnHours ?? nvme?.PowerOnHours));
            if (nvme is not null)
            {
                readings.Add(Reading(number, "available_spare", SensorKind.Other, "Available spare", "%", nvme.AvailableSparePercent));
                readings.Add(Reading(number, "media_errors", SensorKind.Other, "Media and data integrity errors", "count", nvme.MediaErrors));
                readings.Add(Reading(number, "unsafe_shutdowns", SensorKind.Other, "Unsafe shutdowns", "count", nvme.UnsafeShutdowns));
                readings.Add(Reading(number, "critical_warning", SensorKind.Other, "Critical warning flags", "flags", nvme.CriticalWarning));
                readings.Add(Reading(number, "data_written", SensorKind.Other, "Data written", "B", nvme.DataWrittenBytes));
                readings.Add(Reading(number, "data_read", SensorKind.Other, "Data read", "B", nvme.DataReadBytes));
                readings.Add(Reading(number, "power_cycles", SensorKind.Other, "Power cycles", "count", nvme.PowerCycles));
                readings.Add(Reading(number, "error_log_entries", SensorKind.Other, "Error log entries", "count", nvme.ErrorLogEntries));
            }
        }

        return ValueTask.FromResult(new HardwareSnapshot(timeProvider.GetUtcNow(), ProviderId, readings));
    }

    private NvmeHealthInfo? ReadNvme(int number)
    {
        try
        {
            return (nvmeHealth ?? NvmeHealth.TryRead)(number);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private SensorReading Reading(int disk, string metric, SensorKind kind, string name, string unit, double? value, Availability missing = Availability.Unsupported)
    {
        var key = metric == "temperature" ? SensorKeys.DiskTemperature(disk) : $"disk/{disk}/{metric}";
        var descriptor = new SensorDescriptor(ProviderId, $"Disk {disk}", kind, name, unit, key);
        return new SensorReading(descriptor, value is { } v ? Reading<double>.Of(v) : Reading<double>.Unavailable(missing));
    }
}
