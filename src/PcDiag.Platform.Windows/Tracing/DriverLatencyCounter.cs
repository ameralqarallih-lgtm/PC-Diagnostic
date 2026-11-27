using PcDiag.Core.Telemetry;

namespace PcDiag.Platform.Windows.Tracing;

/// <summary>A kernel module (a driver or the kernel itself) and where its code is loaded.</summary>
/// <param name="Base">Its load address.</param>
/// <param name="Size">Its size in bytes (0 when not known: it then reaches up to the next module).</param>
/// <param name="Name">Its file name, e.g. "nvlddmkm.sys".</param>
internal readonly record struct KernelModule(ulong Base, uint Size, string Name);

/// <summary>
/// Sums DPCs and ISRs per routine as they come (one routine is one driver's handler), then puts each routine in the
/// kernel module whose code it is in. A routine in no known module counts as "unknown". Not thread-safe: the trace
/// hands events over on one thread.
/// </summary>
internal sealed class DriverLatencyCounter(long ticksPerSecond)
{
    private readonly Dictionary<ulong, Routine> _routines = [];

    /// <summary>Events counted.</summary>
    public long Events { get; private set; }

    /// <summary>Adds one DPC or ISR: its routine, and when it started and ended (in ticks of the trace's clock).</summary>
    public void Add(bool isr, ulong routine, long started, long ended)
    {
        if (ended < started)
        {
            return;
        }

        Events++;
        if (!_routines.TryGetValue(routine, out var r))
        {
            r = new Routine();
            _routines[routine] = r;
        }

        var us = (ended - started) * 1_000_000.0 / ticksPerSecond;
        if (isr)
        {
            r.Isrs++;
            r.IsrTotalUs += us;
            r.IsrMaxUs = Math.Max(r.IsrMaxUs, us);
        }
        else
        {
            r.Dpcs++;
            r.DpcTotalUs += us;
            r.DpcMaxUs = Math.Max(r.DpcMaxUs, us);
        }

        if (us >= 500)
        {
            r.Over500Us++;
        }

        if (us >= 1000)
        {
            r.Over1Ms++;
        }
    }

    /// <summary>The totals per driver, the slowest single DPC or ISR first.</summary>
    public List<DriverLatency> ByDriver(IReadOnlyList<KernelModule> modules)
    {
        var sorted = modules.Where(m => m.Base != 0).OrderBy(m => m.Base).ToList();
        var drivers = new Dictionary<string, Routine>(StringComparer.OrdinalIgnoreCase);
        foreach (var (address, routine) in _routines)
        {
            var name = ModuleOf(sorted, address) ?? "unknown";
            if (!drivers.TryGetValue(name, out var sum))
            {
                drivers[name] = sum = new Routine();
            }

            sum.Add(routine);
        }

        return drivers
            .Select(d => new DriverLatency(d.Key, d.Value.Dpcs, Math.Round(d.Value.DpcMaxUs, 1), Math.Round(d.Value.DpcTotalUs), d.Value.Isrs, Math.Round(d.Value.IsrMaxUs, 1),
                Math.Round(d.Value.IsrTotalUs), d.Value.Over500Us, d.Value.Over1Ms))
            .OrderByDescending(d => d.LongestUs)
            .ThenByDescending(d => d.DpcTotalUs + d.IsrTotalUs)
            .ToList();
    }

    /// <summary>The module whose code holds <paramref name="address"/>, from modules sorted by base.</summary>
    internal static string? ModuleOf(List<KernelModule> sorted, ulong address)
    {
        // The last module that starts at or below the address.
        int low = 0, high = sorted.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (sorted[mid].Base <= address)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0)
        {
            return null;
        }

        var module = sorted[found];
        var end = module.Size > 0 ? module.Base + module.Size : found + 1 < sorted.Count ? sorted[found + 1].Base : ulong.MaxValue;
        return address < end ? module.Name : null;
    }

    private sealed class Routine
    {
        public long Dpcs;
        public double DpcMaxUs;
        public double DpcTotalUs;
        public long Isrs;
        public double IsrMaxUs;
        public double IsrTotalUs;
        public long Over500Us;
        public long Over1Ms;

        public void Add(Routine other)
        {
            Dpcs += other.Dpcs;
            DpcMaxUs = Math.Max(DpcMaxUs, other.DpcMaxUs);
            DpcTotalUs += other.DpcTotalUs;
            Isrs += other.Isrs;
            IsrMaxUs = Math.Max(IsrMaxUs, other.IsrMaxUs);
            IsrTotalUs += other.IsrTotalUs;
            Over500Us += other.Over500Us;
            Over1Ms += other.Over1Ms;
        }
    }
}
