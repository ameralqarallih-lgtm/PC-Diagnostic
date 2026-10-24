using PcDiag.Platform.Windows.Gpu;

namespace PcDiag.Collectors.Gpu;

/// <summary>
/// Aggregates per-process, per-engine GPU utilization the way Task Manager does:
/// an engine's utilization is the sum over processes; an engine type's utilization is its busiest engine;
/// the adapter's utilization is its busiest engine overall. A process's utilization is its busiest engine.
/// Collections are reused between ticks to avoid garbage.
/// </summary>
public sealed class GpuUsageAggregator
{
    private readonly Dictionary<(AdapterLuid Luid, int Engine), (GpuEngineKind Kind, double Sum)> _engines = [];
    private readonly Dictionary<(int Pid, AdapterLuid Luid), (double Max, GpuEngineKind Kind, string EngineName)> _processes = [];
    private readonly Dictionary<GpuEngineKind, string> _kindNames = new()
    {
        [GpuEngineKind.ThreeD] = "3D",
        [GpuEngineKind.Compute] = "Compute",
        [GpuEngineKind.VideoDecode] = "VideoDecode",
        [GpuEngineKind.VideoEncode] = "VideoEncode",
        [GpuEngineKind.Copy] = "Copy",
        [GpuEngineKind.Other] = "Other",
    };

    /// <summary>Whether any engine instance had a valid value this tick (false on the first, priming sample).</summary>
    public bool HasData { get; private set; }

    /// <summary>Starts a new tick.</summary>
    public void Reset()
    {
        _engines.Clear();
        _processes.Clear();
        HasData = false;
    }

    /// <summary>Adds one "GPU Engine" instance value.</summary>
    public void Add(ReadOnlySpan<char> instanceName, double utilizationPercent)
    {
        if (!GpuInstanceNames.TryParseEngine(instanceName, out var pid, out var luid, out var engine, out var engineType))
        {
            return;
        }

        HasData = true;
        var kind = GpuInstanceNames.Classify(engineType);
        var value = Math.Clamp(utilizationPercent, 0, 100);

        var key = (luid, engine);
        _engines[key] = _engines.TryGetValue(key, out var existing) ? (existing.Kind, existing.Sum + value) : (kind, value);

        if (value > 0)
        {
            var processKey = (pid, luid);
            if (!_processes.TryGetValue(processKey, out var current) || value > current.Max)
            {
                _processes[processKey] = (value, kind, _kindNames[kind]);
            }
        }
    }

    /// <summary>Busiest engine on an adapter overall and per kind. Values are capped at 100.</summary>
    public (double Overall, IReadOnlyDictionary<GpuEngineKind, double> ByKind) ForAdapter(AdapterLuid luid)
    {
        var overall = 0.0;
        var byKind = new Dictionary<GpuEngineKind, double>();
        foreach (var ((engineLuid, _), (kind, sum)) in _engines)
        {
            if (engineLuid != luid)
            {
                continue;
            }

            var capped = Math.Min(sum, 100);
            overall = Math.Max(overall, capped);
            byKind[kind] = Math.Max(byKind.GetValueOrDefault(kind), capped);
        }

        return (overall, byKind);
    }

    /// <summary>Whether any engine of the adapter appeared this tick.</summary>
    public bool HasAdapter(AdapterLuid luid) => _engines.Keys.Any(k => k.Luid == luid);

    /// <summary>All adapters seen this tick.</summary>
    public IEnumerable<AdapterLuid> Adapters => _engines.Keys.Select(k => k.Luid).Distinct();

    /// <summary>Processes with non-zero GPU utilization this tick.</summary>
    public IEnumerable<(int Pid, AdapterLuid Luid, double Utilization, string Engine)> Processes =>
        _processes.Select(p => (p.Key.Pid, p.Key.Luid, p.Value.Max, p.Value.EngineName));
}
