using PcDiag.Core.Abstractions;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Control;

/// <summary>
/// Every running process with its latest figures and its identity (path, signer), from the process samples. Process
/// samples carry an identity only when a process first appears (and again when its signature check finishes), so
/// this keeps them for as long as the process runs. Read by the process controls; costs nothing when unused.
/// </summary>
public sealed class ProcessDirectory : ISampleConsumer
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ProcessKey, ProcessIdentityDetails> _identities = [];
    private IReadOnlyList<ProcessMetrics> _metrics = [];
    private DateTimeOffset? _sampledAt;
    private Dictionary<int, (double Percent, long MemoryBytes)> _gpu = [];
    private IReadOnlyList<PhysicalDiskSample> _disks = [];

    /// <inheritdoc />
    public string Name => "process-directory";

    /// <inheritdoc />
    public int QueueCapacity => 4;

    /// <summary>The controls see every process, also those a custom recording leaves out.</summary>
    public bool SeesUnrecordedData => true;

    /// <summary>When the newest process sample was taken.</summary>
    public DateTimeOffset? SampledAt
    {
        get
        {
            lock (_gate)
            {
                return _sampledAt;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ConsumeAsync(SampleFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Gpu is { } gpu)
        {
            var byPid = new Dictionary<int, (double Percent, long MemoryBytes)>();
            foreach (var usage in gpu.Processes)
            {
                var (percent, memory) = byPid.GetValueOrDefault(usage.ProcessId);
                byPid[usage.ProcessId] = (Math.Max(percent, usage.UtilizationPercent), memory + usage.DedicatedMemoryBytes.GetValueOrDefault(0));
            }

            lock (_gate)
            {
                _gpu = byPid;
            }
        }

        if (frame.Disk is { } disk)
        {
            lock (_gate)
            {
                _disks = disk.Disks;
            }
        }

        if (frame.Processes is not { } processes)
        {
            return ValueTask.CompletedTask;
        }

        lock (_gate)
        {
            foreach (var identity in processes.Identities)
            {
                _identities[identity.Key] = identity;
            }

            _metrics = processes.Processes;
            _sampledAt = processes.Timestamp;
            if (_identities.Count > processes.Processes.Count + 64)
            {
                // Forget processes that have ended.
                var running = processes.Processes.Select(p => p.Key).ToHashSet();
                foreach (var gone in _identities.Keys.Where(k => !running.Contains(k)).ToList())
                {
                    _identities.Remove(gone);
                }
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>A process's GPU use and GPU memory (dedicated, all adapters) in the newest sample; null when it uses none.</summary>
    public (double Percent, long MemoryBytes)? Gpu(int pid)
    {
        lock (_gate)
        {
            return _gpu.TryGetValue(pid, out var gpu) ? gpu : null;
        }
    }

    /// <summary>The physical disks in the newest sample (media, volumes, active time).</summary>
    public IReadOnlyList<PhysicalDiskSample> Disks()
    {
        lock (_gate)
        {
            return _disks;
        }
    }

    /// <summary>The running processes with their identity, when known.</summary>
    public IReadOnlyList<(ProcessMetrics Metrics, ProcessIdentityDetails? Identity)> Snapshot()
    {
        lock (_gate)
        {
            return _metrics.Select(m => (m, _identities.GetValueOrDefault(m.Key))).ToList();
        }
    }
}
