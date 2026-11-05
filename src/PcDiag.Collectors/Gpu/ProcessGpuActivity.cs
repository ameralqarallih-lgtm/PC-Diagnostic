using PcDiag.Core.Telemetry;

namespace PcDiag.Collectors.Gpu;

/// <summary>
/// Each process's GPU use at the GPU collector's latest tick, for the process collector: a program that goes on drawing is
/// alive even when its window has stopped handling messages. Only the latest list is kept; it is searched when asked.
/// </summary>
public sealed class ProcessGpuActivity
{
    private volatile IReadOnlyList<GpuProcessUsage> _latest = [];

    /// <summary>The GPU collector's latest list of processes using a GPU.</summary>
    public void Update(IReadOnlyList<GpuProcessUsage> usage) => _latest = usage;

    /// <summary>The share of the GPU this process uses (the busiest adapter's), 0 when it is not listed.</summary>
    public double PercentOf(int processId)
    {
        double most = 0;
        foreach (var usage in _latest)
        {
            if (usage.ProcessId == processId && usage.UtilizationPercent > most)
            {
                most = usage.UtilizationPercent;
            }
        }

        return most;
    }
}
