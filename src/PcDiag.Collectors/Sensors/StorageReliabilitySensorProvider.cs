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
/// For an NVMe drive the drive's own log comes first: behind Intel RST (VMD) the counters hold placeholders (a fixed
/// 60 °C and 0% wear on every drive). What neither reaches (RST passes no health request on), LibreHardwareMonitor's
/// own NVMe paths fill under the same keys.
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
            var nvme = disk is { IsNvme: false, TemperatureC: not null, WearPercent: not null, PowerOnHours: not null } && nvmeHealth is null ? null : ReadNvme(number);
            readings.AddRange(Readings(number, disk, nvme));
        }

        return ValueTask.FromResult(new HardwareSnapshot(timeProvider.GetUtcNow(), ProviderId, readings));
    }

    /// <summary>
    /// One disk's readings from its counters and, for NVMe, its health log: an NVMe drive's temperature only from the
    /// log (behind Intel RST the counter is a fixed placeholder), its wear and hours from the log first.
    /// </summary>
    internal IEnumerable<SensorReading> Readings(int number, StorageReliabilityInfo? disk, NvmeHealthInfo? nvme)
    {
        var isNvme = disk?.IsNvme == true || nvme is not null;
        yield return Reading(number, "temperature", SensorKind.Temperature, "Temperature", "°C", isNvme ? nvme?.TemperatureC : disk?.TemperatureC);
        yield return Reading(number, "temperature_max", SensorKind.Temperature, "Temperature (max)", "°C", isNvme ? null : disk?.TemperatureMaxC);
        yield return Reading(number, "wear", SensorKind.Other, "Wear", "%", nvme?.PercentageUsed ?? disk?.WearPercent);
        yield return Reading(number, "read_errors", SensorKind.Other, "Read errors (total)", "count", disk?.ReadErrorsTotal);
        yield return Reading(number, "write_errors", SensorKind.Other, "Write errors (total)", "count", disk?.WriteErrorsTotal);
        yield return Reading(number, "power_on_hours", SensorKind.Other, "Power-on hours", "h", nvme?.PowerOnHours ?? disk?.PowerOnHours);
        if (nvme is not null)
        {
            yield return Reading(number, "available_spare", SensorKind.Other, "Available spare", "%", nvme.AvailableSparePercent);
            yield return Reading(number, "media_errors", SensorKind.Other, "Media and data integrity errors", "count", nvme.MediaErrors);
            yield return Reading(number, "unsafe_shutdowns", SensorKind.Other, "Unsafe shutdowns", "count", nvme.UnsafeShutdowns);
            yield return Reading(number, "critical_warning", SensorKind.Other, "Critical warning flags", "flags", nvme.CriticalWarning);
            yield return Reading(number, "data_written", SensorKind.Other, "Data written", "B", nvme.DataWrittenBytes);
            yield return Reading(number, "data_read", SensorKind.Other, "Data read", "B", nvme.DataReadBytes);
            yield return Reading(number, "power_cycles", SensorKind.Other, "Power cycles", "count", nvme.PowerCycles);
            yield return Reading(number, "error_log_entries", SensorKind.Other, "Error log entries", "count", nvme.ErrorLogEntries);
        }
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
