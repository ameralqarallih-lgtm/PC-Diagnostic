namespace PcDiag.Collectors.Processes;

/// <summary>
/// Decides which windows really stopped responding. Windows calls a window hung (IsHungAppWindow) once it has not handled
/// messages for about 5 seconds, and programs do that while working: a game's main thread is busy loading while it goes on
/// drawing. Taking the first such answer, VALORANT and ARK were "not responding" many times a day while they played
/// normally. Measured on the dev PC (Nov 2026, 158 periods): of the 153 that ended, 68 % took under 5 s and all under a
/// minute; the games were still drawing through every period of 15 s or more; and only two programs that never came back and
/// did nothing would have counted. So a window counts after
/// <see cref="After"/> of being hung while its process is not drawing (under <see cref="DrawingPercent"/> of the GPU on
/// average over <see cref="DrawingWindow"/>: one sample is not enough, ARK's read 0 % in the middle of a load). Without GPU
/// readings the time alone decides. It stays counted until Windows no longer calls the window hung.
/// </summary>
internal sealed class HangConfirmation(Func<int, double> gpuPercent)
{
    /// <summary>How long a window must have been hung (Windows' own 5 seconds come on top).</summary>
    public static readonly TimeSpan After = TimeSpan.FromSeconds(30);

    /// <summary>How far back the process's GPU use is averaged.</summary>
    public static readonly TimeSpan DrawingWindow = TimeSpan.FromSeconds(15);

    /// <summary>From this GPU share a process is drawing, so it is alive whatever its window's messages do.</summary>
    public const double DrawingPercent = 1;

    private readonly Dictionary<int, Hang> _hangs = [];

    /// <summary>The processes that count as not responding now, given those Windows calls hung at this tick.</summary>
    public HashSet<int> Observe(HashSet<int> hungNow, DateTimeOffset now)
    {
        foreach (var answered in _hangs.Keys.Where(pid => !hungNow.Contains(pid)).ToList())
        {
            _hangs.Remove(answered);
        }

        var confirmed = new HashSet<int>();
        foreach (var pid in hungNow)
        {
            if (!_hangs.TryGetValue(pid, out var hang))
            {
                _hangs[pid] = hang = new Hang(now);
            }

            hang.Note(now, gpuPercent(pid));
            hang.Confirmed |= now - hang.Since >= After && !hang.Drawing;
            if (hang.Confirmed)
            {
                confirmed.Add(pid);
            }
        }

        return confirmed;
    }

    /// <summary>When Windows first called this process's window hung, while it still does.</summary>
    public DateTimeOffset? Since(int pid) => _hangs.TryGetValue(pid, out var hang) ? hang.Since : null;

    private sealed class Hang(DateTimeOffset since)
    {
        private readonly Queue<(DateTimeOffset At, double Percent)> _recent = new();

        public DateTimeOffset Since { get; } = since;

        public bool Confirmed { get; set; }

        public bool Drawing => _recent.Average(s => s.Percent) >= DrawingPercent;

        public void Note(DateTimeOffset now, double percent)
        {
            _recent.Enqueue((now, percent));
            while (now - _recent.Peek().At > DrawingWindow)
            {
                _recent.Dequeue();
            }
        }
    }
}
