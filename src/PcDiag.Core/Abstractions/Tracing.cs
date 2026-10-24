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
public readonly record struct ProcessIoTotals(long DiskReadBytes, long DiskWriteBytes, long NetReceiveBytes, long NetSendBytes);

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
