using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;
using PcDiag.Screens;

namespace PcDiag.Agent.Screens;

/// <summary>
/// The values the approved sensor drivers read (sent by the screen host), checked again here: only drivers the host
/// loaded, at most <see cref="SensorDrivers.MaxValues"/> each, names and units cut to size, keys of letters, digits
/// and dashes under ext/{driver}/{device}/{sensor}, so a driver can never stand for a built-in sensor or another
/// driver. Read by <see cref="ExternalSensorProvider"/>.
/// </summary>
internal static class ExternalReadings
{
    private static readonly Lock Gate = new();
    private static Dictionary<string, (DateTimeOffset At, ScreenHostProtocol.DriverReadings Readings)> _latest = new(StringComparer.Ordinal);

    /// <summary>Keeps the values of the drivers the host has loaded (the others are dropped).</summary>
    public static void Put(IEnumerable<ScreenHostProtocol.DriverReadings> readings, IReadOnlySet<string> loaded, DateTimeOffset now)
    {
        lock (Gate)
        {
            var next = new Dictionary<string, (DateTimeOffset, ScreenHostProtocol.DriverReadings)>(_latest, StringComparer.Ordinal);
            foreach (var driver in readings.Where(r => loaded.Contains(r.DriverId)).Take(64))
            {
                next[driver.DriverId] = (now, driver with { Values = [.. driver.Values.Take(SensorDrivers.MaxValues)] });
            }

            foreach (var gone in next.Keys.Where(k => !loaded.Contains(k)).ToList())
            {
                next.Remove(gone);
            }

            _latest = next;
        }
    }

    /// <summary>The drivers' problems now (a driver set aside, a read that failed), by driver id.</summary>
    public static IReadOnlyDictionary<string, string?> Problems()
    {
        lock (Gate)
        {
            return _latest.ToDictionary(p => p.Key, p => p.Value.Readings.Problem, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The readings as sensors: a value older than <paramref name="stale"/> (its driver stopped answering) reads as
    /// unavailable.
    /// </summary>
    public static List<SensorReading> Readings(DateTimeOffset now, TimeSpan stale)
    {
        Dictionary<string, (DateTimeOffset At, ScreenHostProtocol.DriverReadings Readings)> latest;
        lock (Gate)
        {
            latest = _latest;
        }

        var readings = new List<SensorReading>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (driver, (at, set)) in latest)
        {
            foreach (var v in set.Values)
            {
                var key = $"ext/{Part(driver)}/{Part(v.Device)}/{Part(v.Sensor)}";
                if (!keys.Add(key))
                {
                    continue;
                }

                var descriptor = new SensorDescriptor(ExternalSensorProvider.Id, Cut(v.Device, 40), KindOf(v.Kind), Cut(v.Name, 80), Cut(v.Unit, 12), key);
                var value = now - at > stale || v.Value is not { } x || !double.IsFinite(x)
                    ? Reading<double>.Unavailable(Availability.Error)
                    : Reading<double>.Of(x);
                readings.Add(new SensorReading(descriptor, value));
            }
        }

        return readings;
    }

    /// <summary>A key part: letters, digits and dashes only (anything else becomes a dash), lower case, at most 40.</summary>
    internal static string Part(string text)
    {
        var part = new string([.. (text ?? string.Empty).Take(40).Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')]);
        return part.Length == 0 ? "-" : part;
    }

    private static string Cut(string text, int max) => string.IsNullOrEmpty(text) ? string.Empty : text.Length <= max ? text : text[..max];

    private static SensorKind KindOf(string kind) => kind switch
    {
        "temperature" => SensorKind.Temperature,
        "clock" => SensorKind.Clock,
        "power" => SensorKind.Power,
        "fan" => SensorKind.Fan,
        "voltage" => SensorKind.Voltage,
        "load" => SensorKind.Load,
        _ => SensorKind.Other,
    };
}

/// <summary>
/// The sensors of approved sensor driver packages, merged with the built-in ones at the lowest priority (their keys
/// are under ext/, so they never replace one). Never fails to start: with no driver it has no readings.
/// </summary>
internal sealed class ExternalSensorProvider(TimeProvider time) : IHardwareSensorProvider
{
    /// <summary>The provider's id.</summary>
    public const string Id = "ext";

    public string ProviderId => Id;

    public int Priority => -100;

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(1);

    public ValueTask<string> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult("sensors of approved driver packages, when there are any");

    public ValueTask<HardwareSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        return ValueTask.FromResult(new HardwareSnapshot(now, Id, ExternalReadings.Readings(now, TimeSpan.FromMinutes(2))));
    }
}
