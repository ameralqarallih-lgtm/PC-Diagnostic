using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PcDiag.Core.Diagnostics;

/// <summary>
/// What each part of the recorder costs in CPU: the thread's CPU cycles spent inside <see cref="Measure"/>, added up
/// by part ("collector.Processes", "overlay.panels"…). Cycles are exact even for work far shorter than the system's
/// clock tick; <see cref="Take"/> hands them over (and starts again), and the reporter turns them into milliseconds
/// with the process's own cycles-to-time ratio. Off Windows nothing is counted.
/// </summary>
public static class CostMeter
{
    private static readonly ConcurrentDictionary<string, long> Cycles = new(StringComparer.Ordinal);
    private static readonly bool Counting = OperatingSystem.IsWindows();

    // The cycles of the parts that ended on this thread so far: a part counts only its own work, not the parts measured
    // inside it (the USB screen's drawing inside the motion timer's tick was counted twice).
    [ThreadStatic]
    private static long t_ended;

    /// <summary>
    /// Counts the current thread's cycles until the scope is disposed (on the same thread), for <paramref name="part"/>:
    /// its own cycles, without those of the parts measured inside it.
    /// </summary>
    public static Scope Measure(string part) => new(part, Counting ? ThreadCycles() : 0, t_ended);

    /// <summary>The cycles counted per part since the last call, and the count started again.</summary>
    public static IReadOnlyDictionary<string, long> Take()
    {
        var taken = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var part in Cycles.Keys)
        {
            if (Cycles.TryRemove(part, out var cycles))
            {
                taken[part] = cycles;
            }
        }

        return taken;
    }

    /// <summary>The last minute's report (null until the first minute has passed).</summary>
    public static RecorderCost? Latest { get; set; }

    /// <summary>The whole process's cycles so far (0 off Windows).</summary>
    public static long ProcessCycles() =>
        Counting && NativeMethods.QueryProcessCycleTime(NativeMethods.GetCurrentProcess(), out var cycles) ? (long)cycles : 0;

    internal static void Add(string part, long cycles)
    {
        if (cycles > 0)
        {
            Cycles.AddOrUpdate(part, cycles, (_, old) => old + cycles);
        }
    }

    private static long ThreadCycles() => NativeMethods.QueryThreadCycleTime(NativeMethods.GetCurrentThread(), out var cycles) ? (long)cycles : 0;

    /// <summary>A part being measured.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string _part;
        private readonly long _start;
        private readonly long _ended;

        internal Scope(string part, long start, long ended)
        {
            _part = part;
            _start = start;
            _ended = ended;
        }

        /// <summary>Adds the cycles spent since it began, less those of the parts measured inside it, to its part.</summary>
        public void Dispose()
        {
            if (Counting && _part is not null)
            {
                var spent = ThreadCycles() - _start;
                Add(_part, spent - (t_ended - _ended));
                t_ended = _ended + spent;
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryProcessCycleTime(IntPtr process, out ulong cycles);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetCurrentProcess();
    }
}

/// <summary>What the recorder cost in CPU over a minute, and how that split over its parts.</summary>
/// <param name="At">When the minute ended.</param>
/// <param name="Seconds">How long it was.</param>
/// <param name="UsedMs">The recorder's CPU time in it, in milliseconds.</param>
/// <param name="SharePercent">Its share of the whole PC (all logical processors).</param>
/// <param name="Parts">Each measured part's CPU time, most first; "other" is what no part counted.</param>
public sealed record RecorderCost(DateTimeOffset At, double Seconds, double UsedMs, double SharePercent, IReadOnlyList<RecorderCostPart> Parts);

/// <summary>A part's CPU time in milliseconds over the minute.</summary>
public sealed record RecorderCostPart(string Part, double Ms);
