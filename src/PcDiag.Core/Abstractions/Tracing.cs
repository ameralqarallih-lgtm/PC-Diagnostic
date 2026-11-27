namespace PcDiag.Core.Abstractions;

/// <summary>An ETW flight recorder: detailed activity kept in memory and saved to a trace file on request.</summary>
public interface IFlightRecorder
{
    /// <summary>Whether it is recording now.</summary>
    bool Running { get; }

    /// <summary>Why it cannot run, when it cannot (e.g. not elevated, another recording owns the session).</summary>
    string? Problem { get; }

    /// <summary>Starts recording into memory. False (with <see cref="Problem"/>) when it cannot.</summary>
    Task<bool> StartAsync(CancellationToken cancellationToken);

    /// <summary>Saves what is in memory to <paramref name="etlPath"/> and starts again. False when nothing could be saved.</summary>
    Task<bool> SaveAsync(string etlPath, string description, CancellationToken cancellationToken);

    /// <summary>Stops recording without saving.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>Bytes a process moved since the previous read, by kind.</summary>
/// <param name="DiskReadBytes">Read from disk.</param>
/// <param name="DiskWriteBytes">Written to disk.</param>
/// <param name="NetReceiveBytes">Received over the network.</param>
/// <param name="NetSendBytes">Sent over the network.</param>
/// <param name="NetRetransmits">TCP segments sent again (not acknowledged in time).</param>
public readonly record struct ProcessIoTotals(long DiskReadBytes, long DiskWriteBytes, long NetReceiveBytes, long NetSendBytes, long NetRetransmits = 0);

/// <summary>
/// Measures how long drivers hold the processors: every DPC and ISR for a few seconds, put in the driver it belongs to
/// (a kernel trace; needs administrator rights). One measurement at a time.
/// </summary>
public interface IDriverLatencyTracer
{
    /// <summary>Why it cannot measure on this PC, when it cannot (e.g. not elevated).</summary>
    string? Problem { get; }

    /// <summary>Measures for <paramref name="duration"/>; the result says why it could not when it could not.</summary>
    Task<Telemetry.DriverLatencySample> MeasureAsync(TimeSpan duration, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// Driver latency in the recorder: measured by itself when drivers hold the processors long or a game stutters, and on
/// request. Each result goes into the next sample frame, so it is stored and analyzed like the rest.
/// </summary>
public interface IDriverLatencyMonitor
{
    /// <summary>Why it cannot measure on this PC, when it cannot.</summary>
    string? Problem { get; }

    /// <summary>A measurement is running.</summary>
    bool Measuring { get; }

    /// <summary>The newest result, or null.</summary>
    Telemetry.DriverLatencySample? Last { get; }

    /// <summary>Starts a measurement now (unless one is running); false when it did not start.</summary>
    bool MeasureNow();
}

/// <summary>True per-process disk and network I/O from kernel events (Enhanced mode).</summary>
public interface IProcessIoMonitor
{
    /// <summary>Whether it is collecting now.</summary>
    bool Running { get; }

    /// <summary>Why it cannot collect, when it cannot.</summary>
    string? Problem { get; }

    /// <summary>Starts collecting. False (with <see cref="Problem"/>) when it cannot.</summary>
    bool StartCollecting();

    /// <summary>Stops collecting.</summary>
    void StopCollecting();

    /// <summary>Totals per process ID since the previous call (and resets them).</summary>
    IReadOnlyDictionary<int, ProcessIoTotals> Take();
}
