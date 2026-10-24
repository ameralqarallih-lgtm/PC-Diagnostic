using PcDiag.Core.Telemetry;

namespace PcDiag.Storage.Sqlite.Telemetry;

/// <summary>One process row to persist, with its GPU use joined from the GPU collector.</summary>
internal sealed record SelectedProcess(ProcessMetrics Metrics, double? GpuPercent, string? GpuEngine, long? GpuMemoryBytes);

/// <summary>
/// Chooses which processes to persist each process tick: the union of the top N by CPU, private memory, I/O and
/// GPU, plus every process with a lifecycle event or a hung window. The in-memory ring buffer (Phase 8) keeps all
/// processes; the rolling store keeps the ones that matter for diagnosis.
/// </summary>
internal static class ProcessSelection
{
    /// <summary>A process must use at least this much CPU (%) to enter the CPU ranking.</summary>
    public const double MinCpuPercent = 0.1;

    /// <summary>A process must do at least this much I/O (bytes/s) to enter the I/O ranking.</summary>
    public const double MinIoBytesPerSec = 1024;

    /// <summary>Every process (except Idle) with its GPU usage: the full list kept for incidents.</summary>
    public static List<SelectedProcess> SelectAll(ProcessSample processes, GpuSample? gpu)
    {
        var gpuByPid = GpuByPid(gpu);
        return processes.Processes
            .Where(p => p.Key.Pid != 0)
            .Select(p => gpuByPid.TryGetValue(p.Key.Pid, out var g) ? new SelectedProcess(p, g.Util, g.Engine, g.Memory) : new SelectedProcess(p, null, null, null))
            .ToList();
    }

    /// <summary>The processes worth persisting this tick.</summary>
    /// <param name="includeMemoryRanking">Memory changes slowly, so its ranking is persisted less often (every 10 s).</param>
    public static List<SelectedProcess> Select(ProcessSample processes, GpuSample? gpu, int topN, bool includeMemoryRanking = true)
    {
        var gpuByPid = GpuByPid(gpu);

        // The Idle process "uses" all idle CPU; it is never a meaningful top consumer.
        var candidates = processes.Processes.Where(p => p.Key.Pid != 0).ToList();
        var chosen = new HashSet<ProcessKey>();

        void Top(Func<ProcessMetrics, double> score, double minimum)
        {
            foreach (var p in candidates.Where(p => score(p) >= minimum).OrderByDescending(score).Take(topN))
            {
                chosen.Add(p.Key);
            }
        }

        Top(p => p.CpuPercent.GetValueOrDefault(0), MinCpuPercent);
        Top(p => p.IoReadBytesPerSec.GetValueOrDefault(0) + p.IoWriteBytesPerSec.GetValueOrDefault(0), MinIoBytesPerSec);
        Top(p => gpuByPid.TryGetValue(p.Key.Pid, out var g) ? g.Util + (g.Memory / 1e12) : 0, double.Epsilon);
        if (includeMemoryRanking)
        {
            Top(p => p.PrivateBytes.GetValueOrDefault(0), 1);
        }

        foreach (var e in processes.Events)
        {
            chosen.Add(e.Key);
        }

        foreach (var p in candidates.Where(p => p.State == ProcessState.NotResponding))
        {
            chosen.Add(p.Key);
        }

        return candidates
            .Where(p => chosen.Contains(p.Key))
            .Select(p => gpuByPid.TryGetValue(p.Key.Pid, out var g)
                ? new SelectedProcess(p, g.Util, g.Engine, g.Memory)
                : new SelectedProcess(p, null, null, null))
            .ToList();
    }

    // Per-process GPU across adapters: the busiest engine, and total dedicated memory.
    private static Dictionary<int, (double Util, string? Engine, long Memory)> GpuByPid(GpuSample? gpu)
    {
        var gpuByPid = new Dictionary<int, (double Util, string? Engine, long Memory)>();
        foreach (var usage in gpu?.Processes ?? [])
        {
            var existing = gpuByPid.GetValueOrDefault(usage.ProcessId);
            var memory = existing.Memory + usage.DedicatedMemoryBytes.GetValueOrDefault(0);
            gpuByPid[usage.ProcessId] = usage.UtilizationPercent >= existing.Util
                ? (usage.UtilizationPercent, usage.Engine ?? existing.Engine, memory)
                : (existing.Util, existing.Engine, memory);
        }

        return gpuByPid;
    }
}
