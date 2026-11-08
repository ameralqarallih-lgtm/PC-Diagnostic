using PcDiag.Core.Diagnostics;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;

namespace PcDiag.Sensors.LibreHardwareMonitor;

/// <summary>
/// Hardware sensors from LibreHardwareMonitorLib: CPU (temperatures, package power, clocks), motherboard Super I/O
/// (temperatures, fans, voltages), GPUs (including hotspot where the vendor API exposes it), memory, storage.
/// CPU MSR and Super I/O access needs administrator rights and the PawnIO driver; missing prerequisites are reported
/// per reading as <see cref="Availability.RequiresElevation"/> or <see cref="Availability.DriverMissing"/>.
/// Storage (SMART) is refreshed less often because it is slow.
/// Hardware is read on a background thread about once a second and the sensor collector gets the latest readings
/// without waiting: elevated, an update of a 24-core Core Ultra 9 285K's sensors through PawnIO takes 400–500 ms
/// (measured), which made every sensor tick late when it was read on the sampling path. LibreHardwareMonitor is
/// only ever used from that one thread.
/// </summary>
public sealed partial class LhmSensorProvider : IHardwareSensorProvider, IDisposable
{
    /// <summary>Storage sensors (SMART) are read at most this often.</summary>
    public static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// GPU hardware is read at most this often: an update costs 60–80 ms (measured), and the GPU collector already
    /// records utilization, clocks and power every second. LHM adds hotspot/junction temperatures and fans.
    /// </summary>
    public static readonly TimeSpan GpuInterval = TimeSpan.FromSeconds(5);

    private const uint IntelVendorId = 0x8086;
    private static int _intelControlLibraryBlocked;

    private readonly ElevationInfo _elevation;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LhmSensorProvider> _logger;
    private readonly Dictionary<Identifier, long> _lastUpdate = [];
    private readonly HashSet<Identifier> _reportedSlow = [];
    private readonly Dictionary<Identifier, IReadOnlyList<SensorReading>> _drives = [];
    private readonly CancellationTokenSource _stop = new();
    private Computer? _computer;
    private bool _pawnIoInstalled;
    private volatile HardwareSnapshot? _latest;
    private Thread? _reader;

    // Per sensor, what the previous read returned. The ring buffer keeps every frame's readings for the whole
    // pre-incident window, so a sensor whose value did not change hands back the same reading, and a changed one
    // keeps its descriptor: no new descriptor, key string and reading per sensor every second (about 220 bytes each).
    private Dictionary<ISensor, Cached> _cache = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates the provider.</summary>
    public LhmSensorProvider(ElevationInfo elevation, TimeProvider timeProvider, ILogger<LhmSensorProvider> logger)
    {
        _elevation = elevation;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Whether LibreHardwareMonitor may load Intel's Graphics Control Library (ControlLib.dll), which it uses for Intel
    /// Arc discrete cards. Loading it also loads Level Zero and Intel's graphics compiler: on the dev PC (Core Ultra 9
    /// 285K, integrated graphics only) about 18 MB of working set and 48 MB of private commit, for a fixed core clock
    /// reading. Integrated graphics report power, loads and shared memory without it. Set before
    /// <see cref="InitializeAsync"/>; once turned off it stays off for the process.
    /// </summary>
    public bool UseIntelControlLibrary { get; init; } = true;

    /// <inheritdoc />
    public string ProviderId => "lhm";

    /// <summary>
    /// Whether an Intel discrete card is present: an Intel adapter with at least 1 GB of its own memory (integrated
    /// graphics have about 128 MB; Arc cards 4 GB or more).
    /// </summary>
    public static bool HasIntelDiscreteGpu(IEnumerable<(uint VendorId, long DedicatedVideoMemoryBytes)> adapters) =>
        adapters.Any(a => a.VendorId == IntelVendorId && a.DedicatedVideoMemoryBytes >= 1L << 30);

    /// <inheritdoc />
    public int Priority => 50;

    /// <inheritdoc />
    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    /// <summary>Whether the PawnIO driver service is registered (HKLM\SYSTEM\CurrentControlSet\Services\PawnIO).</summary>
    public static bool IsPawnIoInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return key is not null;
    }

    /// <inheritdoc />
    public ValueTask<string> InitializeAsync(CancellationToken cancellationToken)
    {
        _pawnIoInstalled = IsPawnIoInstalled();
        if (!UseIntelControlLibrary)
        {
            BlockIntelControlLibrary();
        }

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
            IsNetworkEnabled = false, // the network collector covers this
        };
        _computer.Open();

        var hardware = Flatten(_computer.Hardware).ToList();
        var status = $"{hardware.Count} hardware items, {hardware.Sum(h => h.Sensors.Length)} sensors; elevated={_elevation.IsElevated}, PawnIO={(_pawnIoInstalled ? "installed" : "not installed")}";
        if (!_elevation.IsElevated || !_pawnIoInstalled)
        {
            LogPrerequisitesMissing(_logger, _elevation.IsElevated, _pawnIoInstalled);
        }

        // First readings now, so the first tick has them; then the background thread keeps them fresh.
        _latest = Read();
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "PcDiag hardware sensors" };
        _reader.Start();
        return ValueTask.FromResult(status);
    }

    /// <inheritdoc />
    /// <remarks>Returns the latest readings at once (at most about <see cref="RefreshInterval"/> old).</remarks>
    public ValueTask<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_latest ?? new HardwareSnapshot(_timeProvider.GetUtcNow(), ProviderId, []));

    private void ReadLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var started = _timeProvider.GetTimestamp();
            try
            {
                using (CostMeter.Measure("sensors.lhm"))
                {
                    _latest = Read();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogReadFailed(_logger, ex);
            }

            var wait = RefreshInterval - _timeProvider.GetElapsedTime(started);
            _stop.Token.WaitHandle.WaitOne(wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Updates the hardware that is due and returns every reading (background thread only, after start).</summary>
    private HardwareSnapshot Read()
    {
        var now = _timeProvider.GetTimestamp();
        var readings = new List<SensorReading>();
        if (_computer is null)
        {
            return new HardwareSnapshot(_timeProvider.GetUtcNow(), ProviderId, readings);
        }

        var sawCpuPackageTemperature = false;
        var sawCpuPackagePower = false;
        var cache = new Dictionary<ISensor, Cached>(_cache.Count, ReferenceEqualityComparer.Instance);
        foreach (var hardware in _computer.Hardware)
        {
            // Without elevation and PawnIO, CPU and Super I/O readings are zeros or stale; don't pay for reading them.
            var blocked = PrerequisiteReason(hardware.HardwareType);
            var updated = blocked is null && UpdateIfDue(hardware, now);

            // A drive's health under the storage provider's keys, built again only when the drive was read.
            if (hardware is StorageDevice drive)
            {
                if (updated || !_drives.TryGetValue(hardware.Identifier, out var kept))
                {
                    _drives[hardware.Identifier] = kept = DriveReadings(drive);
                }

                readings.AddRange(kept);
            }

            foreach (var item in Flatten([hardware]))
            {
                foreach (var sensor in item.Sensors)
                {
                    if (MapKind(sensor.SensorType) is not { } kind)
                    {
                        continue;
                    }

                    // Intel iGPU power comes from the CPU's energy counters (MSR) and has the same prerequisites.
                    var sensorBlocked = blocked ?? (item.HardwareType == HardwareType.GpuIntel && kind == SensorKind.Power ? PrerequisiteReason(HardwareType.Cpu) : null);
                    var value = sensorBlocked is { } reason && kind is SensorKind.Temperature or SensorKind.Power or SensorKind.Voltage or SensorKind.Fan
                        ? Reading<double>.Unavailable(reason)
                        : sensor.Value is { } v && float.IsFinite(v)
                            ? Reading<double>.Of(v)
                            : Reading<double>.Unavailable(blocked ?? Availability.Unsupported);
                    var cached = Reuse(_cache.GetValueOrDefault(sensor), item, sensor, kind, value);
                    cache[sensor] = cached;
                    readings.Add(cached.Reading!);

                    // Shared logical keys so other providers (and the CPU/GPU tables) can use the value.
                    if (cached.Alias is { } alias)
                    {
                        sawCpuPackageTemperature |= alias.Sensor.LogicalKey == SensorKeys.CpuPackageTemperature;
                        sawCpuPackagePower |= alias.Sensor.LogicalKey == SensorKeys.CpuPackagePower;
                        readings.Add(alias);
                    }
                }
            }
        }

        _cache = cache;

        // Without the driver, LHM may not create the CPU sensors at all; the gap must still carry its reason.
        if (!sawCpuPackageTemperature)
        {
            readings.Add(Missing(SensorKeys.CpuPackageTemperature, SensorKind.Temperature, "CPU Package", "°C"));
        }

        if (!sawCpuPackagePower)
        {
            readings.Add(Missing(SensorKeys.CpuPackagePower, SensorKind.Power, "CPU Package", "W"));
        }

        return new HardwareSnapshot(_timeProvider.GetUtcNow(), ProviderId, readings);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _reader?.Join(TimeSpan.FromSeconds(5));
        _computer?.Close();
        _stop.Dispose();
    }

    /// <summary>
    /// Maps well-known sensors to shared logical keys: CPU package temperature ("CPU Package" on Intel, "Core (Tctl/Tdie)"
    /// or "Tctl" on AMD), CPU package power ("CPU Package" / "Package"), GPU hotspot ("GPU Hot Spot").
    /// </summary>
    /// <summary>LHM finds Intel's control library missing and leaves out only what it would have read through it.</summary>
    private void BlockIntelControlLibrary()
    {
        if (Interlocked.Exchange(ref _intelControlLibraryBlocked, 1) != 0)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Computer).Assembly, static (name, _, _) => name.StartsWith("ControlLib", StringComparison.OrdinalIgnoreCase)
                ? throw new DllNotFoundException("Intel's control library is only loaded for Intel discrete GPUs.")
                : IntPtr.Zero);
            LogIntelControlLibrarySkipped(_logger);
        }
        catch (InvalidOperationException ex)
        {
            LogIntelControlLibraryNotBlocked(_logger, ex);
        }
    }

    /// <summary>The previous readings when the value is unchanged, the previous descriptors with the new value otherwise.</summary>
    private Cached Reuse(Cached previous, IHardware item, ISensor sensor, SensorKind kind, Reading<double> value)
    {
        if (previous.Reading is { } reading && previous.HardwareName == item.Name && previous.SensorName == sensor.Name
            && reading.Sensor.Kind == kind)
        {
            return reading.Value == value
                ? previous
                : previous with { Reading = reading with { Value = value }, Alias = previous.Alias is { } a ? a with { Value = value } : null };
        }

        var descriptor = new SensorDescriptor(ProviderId, item.Name, kind, sensor.Name, UnitFor(sensor.SensorType), "lhm" + sensor.Identifier);
        var key = CanonicalKey(item.HardwareType, item.Name, sensor.SensorType, sensor.Name);
        return new Cached(item.Name, sensor.Name, new SensorReading(descriptor, value),
            key is null ? null : new SensorReading(descriptor with { LogicalKey = key, AliasOf = descriptor.LogicalKey }, value));
    }

    internal static string? CanonicalKey(HardwareType type, string hardwareName, SensorType sensorType, string sensorName)
    {
        if (type == HardwareType.Cpu && sensorType == SensorType.Temperature
            && (sensorName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) || sensorName.Contains("Tctl", StringComparison.OrdinalIgnoreCase)))
        {
            return SensorKeys.CpuPackageTemperature;
        }

        if (type == HardwareType.Cpu && sensorType == SensorType.Power
            && (sensorName.Equals("CPU Package", StringComparison.OrdinalIgnoreCase) || sensorName.Equals("Package", StringComparison.OrdinalIgnoreCase)))
        {
            return SensorKeys.CpuPackagePower;
        }

        if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && sensorType == SensorType.Temperature
            && sensorName.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
        {
            return SensorKeys.GpuHotspot(hardwareName);
        }

        if (type is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel && sensorType == SensorType.Power
            && (sensorName.Equals("GPU Power", StringComparison.OrdinalIgnoreCase) || sensorName.Equals("GPU Package", StringComparison.OrdinalIgnoreCase)))
        {
            return SensorKeys.GpuPower(hardwareName);
        }

        return null;
    }

    internal static SensorKind? MapKind(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Power => SensorKind.Power,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Voltage => SensorKind.Voltage,
        SensorType.Load => SensorKind.Load,
        SensorType.Control => SensorKind.Other, // fan duty %
        _ => null, // data sizes, throughput and factors are covered by the dedicated collectors
    };

    private static string UnitFor(SensorType type) => type switch
    {
        SensorType.Temperature => "°C",
        SensorType.Power => "W",
        SensorType.Fan => "RPM",
        SensorType.Clock => "MHz",
        SensorType.Voltage => "V",
        SensorType.Load or SensorType.Control => "%",
        _ => string.Empty,
    };

    /// <summary>
    /// Why a hardware type cannot be read, or null when it can. CPU MSRs and Super I/O / embedded-controller chips need
    /// the PawnIO driver in an elevated process; without both, LibreHardwareMonitor reports zeros (observed: CPU Package
    /// power 0.0 W) rather than nothing, which must not be recorded as a real value.
    /// </summary>
    internal Availability? PrerequisiteReason(HardwareType type)
    {
        var needsDriver = type is HardwareType.Cpu or HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController;
        return !needsDriver ? null
            : !_elevation.IsElevated ? Availability.RequiresElevation
            : !_pawnIoInstalled ? Availability.DriverMissing
            : null;
    }

    private SensorReading Missing(string key, SensorKind kind, string name, string unit) =>
        new(new SensorDescriptor(ProviderId, "CPU", kind, name, unit, key), Reading<double>.Unavailable(PrerequisiteReason(HardwareType.Cpu) ?? Availability.Unsupported));

    /// <summary>Updates the hardware when its interval has passed; whether it did.</summary>
    private bool UpdateIfDue(IHardware hardware, long now)
    {
        var interval = hardware.HardwareType switch
        {
            HardwareType.Storage => StorageInterval,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => GpuInterval,
            _ => TimeSpan.Zero,
        };
        if (_lastUpdate.TryGetValue(hardware.Identifier, out var last) && _timeProvider.GetElapsedTime(last, now) < interval)
        {
            return false;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Update();
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > TimeSpan.FromMilliseconds(50) && _reportedSlow.Add(hardware.Identifier))
            {
                LogSlowHardware(_logger, hardware.Name, hardware.HardwareType.ToString(), elapsed.TotalMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUpdateFailed(_logger, ex, hardware.Name);
        }

        _lastUpdate[hardware.Identifier] = now;
        return true;
    }

    /// <summary>
    /// A drive's health as LibreHardwareMonitor's storage library reads it, under the storage provider's keys
    /// ("disk/{number}/wear"). That library has its own NVMe paths (Intel RST and VROC, Samsung's and other makers'
    /// drivers) and reaches drives whose driver passes Windows' health request on to nobody: the storage provider's
    /// own values win the merge, these fill what it cannot reach (a Samsung 970 EVO behind Intel RST VMD on the dev PC).
    /// </summary>
    private List<SensorReading> DriveReadings(StorageDevice drive)
    {
        try
        {
            var storage = drive.Storage;
            var smart = storage.Smart;
            var log = storage.IsNVMe
                ? (smart.SmartAttributes ?? []).Where(a => a.Info is not null).GroupBy(a => a.Info.ID).ToDictionary(g => g.Key, g => (double)g.First().Attribute.RawValueULong)
                : null;
            return DriveReadings(storage.DriveNumber, smart.Temperature, smart.Life, smart.MeasuredPowerOnHours, log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogUpdateFailed(_logger, ex, drive.Name);
            return [];
        }
    }

    /// <summary>
    /// The readings for drive <paramref name="number"/>: temperature, and from an NVMe drive's health log (attribute
    /// IDs as CrystalDiskInfo numbers its fields: 03 available spare, 05 percentage used, 06/07 data units read and
    /// written, 0B power cycles, 0C power-on hours, 0D unsafe shutdowns, 0E media errors, 0F error log entries) the
    /// rest; for other drives wear from the remaining life and the measured power-on hours.
    /// </summary>
    internal List<SensorReading> DriveReadings(int number, double? temperatureC, double? lifePercent, double powerOnHours, IReadOnlyDictionary<byte, double>? nvmeLog)
    {
        var readings = new List<SensorReading>();
        if (number < 0)
        {
            return readings;
        }

        void Add(string metric, SensorKind kind, string name, string unit, double? value)
        {
            if (value is { } v && double.IsFinite(v))
            {
                var key = metric == "temperature" ? SensorKeys.DiskTemperature(number) : $"disk/{number.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{metric}";
                readings.Add(new SensorReading(new SensorDescriptor(ProviderId, $"Disk {number.ToString(System.Globalization.CultureInfo.InvariantCulture)}", kind, name, unit, key), Reading<double>.Of(v)));
            }
        }

        double? Log(byte id) => nvmeLog is not null && nvmeLog.TryGetValue(id, out var v) ? v : null;

        Add("temperature", SensorKind.Temperature, "Temperature", "°C", temperatureC is > 0 and < 150 ? temperatureC : null);
        if (nvmeLog is not null)
        {
            Add("wear", SensorKind.Other, "Wear", "%", Log(0x05));
            Add("power_on_hours", SensorKind.Other, "Power-on hours", "h", Log(0x0C));
            Add("available_spare", SensorKind.Other, "Available spare", "%", Log(0x03));
            Add("media_errors", SensorKind.Other, "Media and data integrity errors", "count", Log(0x0E));
            Add("unsafe_shutdowns", SensorKind.Other, "Unsafe shutdowns", "count", Log(0x0D));
            Add("critical_warning", SensorKind.Other, "Critical warning flags", "flags", Log(0x01));
            Add("data_written", SensorKind.Other, "Data written", "B", Log(0x07) * 512_000);
            Add("data_read", SensorKind.Other, "Data read", "B", Log(0x06) * 512_000);
            Add("power_cycles", SensorKind.Other, "Power cycles", "count", Log(0x0B));
            Add("error_log_entries", SensorKind.Other, "Error log entries", "count", Log(0x0F));
        }
        else
        {
            Add("wear", SensorKind.Other, "Wear", "%", lifePercent is >= 0 and <= 100 ? 100 - lifePercent : null);
            Add("power_on_hours", SensorKind.Other, "Power-on hours", "h", powerOnHours > 0 ? powerOnHours : null);
        }

        return readings;
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware) =>
        hardware.SelectMany(h => new[] { h }.Concat(Flatten(h.SubHardware)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "CPU and motherboard sensors are limited: elevated={Elevated}, PawnIO driver installed={PawnIo}. Both are required for CPU temperature and power (winget install namazso.PawnIO; run the Agent elevated).")]
    private static partial void LogPrerequisitesMissing(ILogger logger, bool elevated, bool pawnIo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sensor hardware {Hardware} ({Type}) took {Milliseconds:F0} ms to update")]
    private static partial void LogSlowHardware(ILogger logger, string hardware, string type, double milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the hardware sensors failed; the previous readings are kept")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "No Intel discrete GPU: Intel's graphics control library is not loaded (integrated graphics are read without it)")]
    private static partial void LogIntelControlLibrarySkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Intel's graphics control library could not be left out")]
    private static partial void LogIntelControlLibraryNotBlocked(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Updating sensor hardware {Hardware} failed")]
    private static partial void LogUpdateFailed(ILogger logger, Exception exception, string hardware);

    private readonly record struct Cached(string HardwareName, string SensorName, SensorReading? Reading, SensorReading? Alias);
}
