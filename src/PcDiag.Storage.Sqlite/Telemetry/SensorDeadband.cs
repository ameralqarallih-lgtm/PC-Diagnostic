using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>
/// Decides which sensor readings to persist. A sensor is written when it changed by more than its deadband or when
/// <see cref="Heartbeat"/> has passed since it was last written, so charts stay continuous while an idle PC's
/// hundreds of sensors do not become millions of rows a day.
/// Clocks and loads are not persisted from sensor providers: the CPU and GPU collectors already record them.
/// </summary>
internal sealed class SensorDeadband
{
    /// <summary>Maximum time between writes of an unchanged sensor.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, (double Value, long Ts)> _lastWritten = new(StringComparer.Ordinal);

    public static bool IsPersisted(SensorKind kind) => kind is not (SensorKind.Clock or SensorKind.Load);

    /// <summary>Change needed before a new value is written.</summary>
    public static double Threshold(SensorKind kind, double value) => kind switch
    {
        SensorKind.Temperature => 0.5,
        SensorKind.Power => Math.Max(1.0, Math.Abs(value) * 0.05),
        SensorKind.Fan => 50,
        SensorKind.Voltage => 0.01,
        _ => 0.5,
    };

    /// <summary>Whether the reading should be written now; records it as written when true.</summary>
    public bool ShouldWrite(SensorReading reading, long ts)
    {
        if (!reading.Value.HasValue || !IsPersisted(reading.Sensor.Kind) || reading.Sensor.AliasOf is not null)
        {
            return false;
        }

        var value = reading.Value.Value!.Value;
        var key = reading.Sensor.LogicalKey;
        if (_lastWritten.TryGetValue(key, out var last)
            && ts - last.Ts < Heartbeat.TotalMilliseconds
            && Math.Abs(value - last.Value) < Threshold(reading.Sensor.Kind, value))
        {
            return false;
        }

        _lastWritten[key] = (value, ts);
        return true;
    }

    public void Reset() => _lastWritten.Clear();
}
