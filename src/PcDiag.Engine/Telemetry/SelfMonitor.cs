using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Telemetry;

/// <summary>
/// Measures the recorder's own CPU and memory so its overhead is visible next to the data it records.
/// CPU is reported as a share of total machine capacity, like Task Manager. The measurements ask Windows about this
/// process only (GetProcessTimes, GetProcessMemoryInfo): System.Diagnostics.Process would take a snapshot of every
/// process on the machine each second just to read its own numbers.
/// </summary>
public sealed class SelfMonitor : IDisposable
{
    private readonly TimeProvider _timeProvider;
    private readonly int _processorCount = Environment.ProcessorCount;
    private TimeSpan _lastCpuTime;
    private long _lastTimestamp;

    /// <summary>Creates the monitor.</summary>
    public SelfMonitor(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _lastCpuTime = Environment.CpuUsage.TotalTime;
        _lastTimestamp = timeProvider.GetTimestamp();
    }

    /// <summary>The managed heap after the last garbage collection (the rest of the working set is the runtime, native libraries and caches).</summary>
    public static long ManagedHeapBytes => GC.GetGCMemoryInfo().HeapSizeBytes;

    /// <summary>Takes a measurement covering the time since the previous one.</summary>
    public (Reading<double> CpuPercent, long WorkingSetBytes) Measure()
    {
        var cpuTime = Environment.CpuUsage.TotalTime;
        var now = _timeProvider.GetTimestamp();
        var wall = _timeProvider.GetElapsedTime(_lastTimestamp, now);

        var cpu = wall > TimeSpan.Zero
            ? Reading<double>.Of(Math.Clamp((cpuTime - _lastCpuTime) / wall / _processorCount * 100.0, 0, 100))
            : Reading<double>.Pending;

        _lastCpuTime = cpuTime;
        _lastTimestamp = now;
        return (cpu, Environment.WorkingSet);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release; kept so registrations that dispose it stay valid.
    }
}
