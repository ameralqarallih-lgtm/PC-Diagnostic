using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Control;

/// <summary>
/// Notices a program that may be stuck in a loop: a spinning thread uses one CPU core, steadily, for as long as it runs. Nothing
/// else catches that: one core is only a few percent of a PC with many of them (a script on the dev PC spun for 21 hours of CPU
/// time at 4 % of 24 threads), and it never stops responding. A process counts after <see cref="DefaultAfter"/> of
/// <see cref="DefaultSlice"/> slices that each averaged <see cref="LowestCores"/> to <see cref="HighestCores"/> cores (CPU time,
/// so the number of cores in the PC plays no part) without drawing. One slice in <see cref="ForgivenOneIn"/> may fall outside, never
/// two in a row, because a busy PC takes CPU from a spinning thread too: that script dipped to 0.8 twice in six hours. Measured on the
/// dev PC (Nov 2026, the samples of 11 days): only that script and WSL's virtual machine reached 2 hours; compilers, Defender, games
/// and node.exe did not, since they use several cores or go up and down. A long job on one thread looks the same as a stuck one, so
/// this is advice to look, not a finding.
/// <para>
/// Costs one pass over the processes per slice. It lives in memory, so a restarted recorder begins again. Time the recorder or the PC
/// was not running neither counts nor ends a run. Not thread-safe: <see cref="ProcessDirectory"/> calls it under its lock.
/// </para>
/// </summary>
/// <param name="after">How long a run must last; only a live test shortens it.</param>
/// <param name="slice">How long each slice is; only a live test shortens it.</param>
public sealed class SteadyCoreWatch(TimeSpan? after = null, TimeSpan? slice = null)
{
    /// <summary>How long a process must have held about one core.</summary>
    public static readonly TimeSpan DefaultAfter = TimeSpan.FromHours(2);

    /// <summary>How long each slice is.</summary>
    public static readonly TimeSpan DefaultSlice = TimeSpan.FromMinutes(5);

    /// <summary>The fewest cores that count as about one.</summary>
    public const double LowestCores = 0.8;

    /// <summary>The most cores that count as about one.</summary>
    public const double HighestCores = 1.2;

    /// <summary>From this GPU share a process is drawing, so it is a game or a 3D program at work, not stuck.</summary>
    public const double DrawingPercent = 1;

    /// <summary>One slice in this many may fall outside.</summary>
    public const int ForgivenOneIn = 12;

    /// <summary>A longer pause between ticks means the recorder or the PC was not running (ticks are about 2 s apart).</summary>
    private static readonly TimeSpan LongestTick = TimeSpan.FromMinutes(1);

    private readonly TimeSpan _after = after ?? DefaultAfter;
    private readonly TimeSpan _slice = slice ?? DefaultSlice;
    private readonly Dictionary<ProcessKey, Run> _runs = [];
    private DateTimeOffset _lastTick;
    private DateTimeOffset _sliceStart;

    /// <summary>Takes the process list of a tick. The GPU share is read once per slice: a run needs many, and one drawing slice in a row of them ends it.</summary>
    public void Observe(IReadOnlyList<ProcessMetrics> processes, DateTimeOffset now, Func<int, double> gpuPercent)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(gpuPercent);
        var sinceLast = now - _lastTick;
        _lastTick = now;
        var running = sinceLast > TimeSpan.Zero && sinceLast <= LongestTick;
        if (running && now - _sliceStart < _slice)
        {
            return;
        }

        var span = now - _sliceStart;
        _sliceStart = now;
        var seen = new HashSet<ProcessKey>(processes.Count);
        foreach (var process in processes)
        {
            if (process.CpuTimeMs.Value is not { } cpuMs)
            {
                continue;
            }

            seen.Add(process.Key);
            if (!_runs.TryGetValue(process.Key, out var run))
            {
                _runs[process.Key] = new Run(cpuMs);
                continue;
            }

            if (running)
            {
                run.Judge((cpuMs - run.CpuMs) / span.TotalMilliseconds, gpuPercent(process.Key.Pid), span);
            }

            run.CpuMs = cpuMs;
        }

        foreach (var gone in _runs.Keys.Where(key => !seen.Contains(key)).ToList())
        {
            _runs.Remove(gone);
        }
    }

    /// <summary>How long this process has held about one core, once that is long enough to mention and still so at the latest slice.</summary>
    public TimeSpan? SteadyFor(ProcessKey key) => _runs.TryGetValue(key, out var run) && run.InBand && run.Steady >= _after ? run.Steady : null;

    private sealed class Run(long cpuMs)
    {
        private int _slices;
        private int _forgiven;

        public long CpuMs { get; set; } = cpuMs;

        public TimeSpan Steady { get; private set; }

        public bool InBand { get; private set; }

        public void Judge(double cores, double gpuPercent, TimeSpan span)
        {
            if (cores is >= LowestCores and <= HighestCores && gpuPercent < DrawingPercent)
            {
                _slices++;
                Steady += span;
                InBand = true;
            }
            else if (InBand && (_forgiven + 1) * ForgivenOneIn <= _slices + _forgiven + 1)
            {
                _forgiven++;
                Steady += span;
                InBand = false;
            }
            else
            {
                _slices = _forgiven = 0;
                Steady = TimeSpan.Zero;
                InBand = false;
            }
        }
    }
}
