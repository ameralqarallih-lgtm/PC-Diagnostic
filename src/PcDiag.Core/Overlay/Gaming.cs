namespace PcDiag.Core.Overlay;

/// <summary>Frame rate of the app drawing frames (the game in front, or the busiest one).</summary>
/// <param name="ProcessId">The app.</param>
/// <param name="ProcessName">Its name, e.g. "Cyberpunk2077".</param>
/// <param name="Fps">Frames in the last second.</param>
/// <param name="FrameTimeMs">Average time between frames over the last second.</param>
/// <param name="Low1Fps">The 1 % low: the frame rate of the slowest 1 % of frames over the last 30 s (null until there are enough).</param>
/// <param name="Foreground">The app is the one in front.</param>
/// <param name="WorstFrameTimeMs">The longest time between two frames in the last second.</param>
/// <param name="Stutters">Frames in the last second far longer than the frames around them (hitches).</param>
public sealed record FrameStats(int ProcessId, string? ProcessName, double Fps, double FrameTimeMs, double? Low1Fps, bool Foreground, double WorstFrameTimeMs = 0, int Stutters = 0);

/// <summary>Counts the frames apps present (DirectX present calls), while something wants them.</summary>
public interface IFrameMonitor
{
    /// <summary>Why it cannot count (not elevated, the session could not start); null when it can.</summary>
    string? Problem { get; }

    /// <summary>Keeps counting for a while (it stops by itself a minute after the last call).</summary>
    void Want();

    /// <summary>The app in front if it draws frames, else the busiest app drawing them; null when none does.</summary>
    FrameStats? Current(int? foregroundProcessId);
}

/// <summary>Round trips to the router and to the internet, in milliseconds.</summary>
/// <param name="RouterMs">To the default gateway.</param>
/// <param name="InternetMs">To a public resolver (1.1.1.1).</param>
public sealed record LatencyStats(double? RouterMs, double? InternetMs);

/// <summary>Pings the router and the internet once a second while the recorder runs.</summary>
public interface ILatencyProbe
{
    /// <summary>The newest round trips.</summary>
    LatencyStats Current();
}

/// <summary>What the gaming readings show now.</summary>
/// <param name="Frames">The frame rate, if an app draws frames.</param>
/// <param name="Latency">The round trips, if pinging.</param>
/// <param name="ForegroundProcessId">The app in front.</param>
public sealed record GameState(FrameStats? Frames, LatencyStats? Latency, int? ForegroundProcessId);

/// <summary>
/// Frame times per app from present timestamps (QPC ticks), kept for 30 s: frames in the last second, the average
/// frame time, and the 1 % low (the average of the slowest 1 % of frame times, as a frame rate). Thread-safe.
/// </summary>
public sealed class FrameTimes(long ticksPerSecond)
{
    /// <summary>How long presents are kept (for the 1 % low).</summary>
    public const int KeepSeconds = 30;

    /// <summary>Fewest frame times for a 1 % low.</summary>
    public const int LowFrames = 100;

    /// <summary>
    /// A stutter is a frame that took at least this many times as long as the frames around it (the median of the
    /// <see cref="StutterNeighbors"/> before and after it): a hitch, not a slow stretch (all its frames are alike; that is a
    /// drop, counted against the usual frame rate)…
    /// </summary>
    public const double StutterFactor = 2.5;

    /// <summary>…and at least this much longer than them, in ms (at a high frame rate a few ms more is not seen).</summary>
    public const double StutterMinExtraMs = 20;

    /// <summary>The frames on each side a frame is compared with.</summary>
    public const int StutterNeighbors = 5;

    /// <summary>Fewest frame times in the last 30 s before stutters are counted.</summary>
    public const int StutterFrames = 30;

    /// <summary>
    /// How long before "now" the measured second ends. ETW hands frames over in buffers (every 100 ms for the frame
    /// session), so the last moments are not all in yet; counting them would read low.
    /// </summary>
    public const double SettleSeconds = 0.2;

    private readonly Lock _lock = new();
    private readonly Dictionary<int, Queue<long>> _presents = [];
    private readonly Dictionary<(int Process, ushort Event), Queue<long>> _kernel = [];

    /// <summary>An app presented a frame at <paramref name="ticks"/> (its graphics runtime said so: DXGI, Direct3D 9).</summary>
    public void Add(int processId, long ticks)
    {
        lock (_lock)
        {
            Push(_presents, processId, ticks);
        }
    }

    /// <summary>
    /// The graphics kernel handled a present of an app (any graphics API: OpenGL and Vulkan too), as event
    /// <paramref name="eventId"/>; used for apps whose runtime says nothing.
    /// </summary>
    public void AddKernel(int processId, ushort eventId, long ticks)
    {
        lock (_lock)
        {
            Push(_kernel, (processId, eventId), ticks);
        }
    }

    private void Push<TKey>(Dictionary<TKey, Queue<long>> map, TKey key, long ticks)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var queue))
        {
            map[key] = queue = new Queue<long>();
        }

        queue.Enqueue(ticks);
        while (queue.Count > 0 && ticks - queue.Peek() > KeepSeconds * ticksPerSecond)
        {
            queue.Dequeue();
        }
    }

    private void Forget<TKey>(Dictionary<TKey, Queue<long>> map, long now)
        where TKey : notnull
    {
        foreach (var gone in map.Where(p => p.Value.Count == 0 || now - p.Value.Last() > KeepSeconds * ticksPerSecond).Select(p => p.Key).ToList())
        {
            map.Remove(gone);
        }
    }

    /// <summary>
    /// The app in front if it presented in the last 2 s, else the one that presented most; null when none did. Also the
    /// longest frame of the last second and how many of its frames were stutters (see <see cref="StutterFactor"/>).
    /// </summary>
    public (int ProcessId, double Fps, double FrameTimeMs, double? Low1Fps, bool Foreground, double WorstFrameTimeMs, int Stutters)? Stats(long now, int? foreground)
    {
        lock (_lock)
        {
            // Forget apps that stopped presenting.
            Forget(_presents, now);
            Forget(_kernel, now);

            // Frames after the end are not all in yet; frames "after now" mean a clock that does not match the frames'
            // (nothing is shown rather than a wrong rate).
            var end = now - (long)(SettleSeconds * ticksPerSecond);
            int Recent(Queue<long> queue) => queue.Count(t => t <= end && end - t <= 2 * ticksPerSecond);

            // Each app's frames: from its graphics runtime when it reports them; otherwise from the graphics kernel, the
            // present event it logs most (each of an app's presents takes one path, so one event per frame).
            var apps = new Dictionary<int, (Queue<long> Queue, int Count)>();
            foreach (var (pid, queue) in _presents)
            {
                if (Recent(queue) is var count and >= 2)
                {
                    apps[pid] = (queue, count);
                }
            }

            foreach (var group in _kernel.GroupBy(k => k.Key.Process).Where(g => !apps.ContainsKey(g.Key)))
            {
                var best = group.Select(k => (Queue: k.Value, Count: Recent(k.Value))).MaxBy(k => k.Count);
                if (best.Count >= 2)
                {
                    apps[group.Key] = best;
                }
            }

            if (apps.Count == 0)
            {
                return null;
            }

            var pick = foreground is { } f && apps.ContainsKey(f) ? f : apps.MaxBy(p => p.Value.Count).Key;
            var times = apps[pick].Queue.Where(t => t <= end).ToArray();
            var lastSecond = times.Where(t => end - t < ticksPerSecond).ToArray();
            double fps = lastSecond.Length;
            var window = lastSecond.Length >= 2 ? lastSecond : times[^Math.Min(times.Length, 2)..];
            var frameTime = window.Length >= 2 ? (window[^1] - window[0]) * 1000.0 / ticksPerSecond / (window.Length - 1) : 0;

            // Since its last frame: while an app is frozen no frame comes, and the wait is the frame being late.
            var sinceLast = times.Length > 0 ? (end - times[^1]) * 1000.0 / ticksPerSecond : 0;
            if (fps < 2 && frameTime > 0)
            {
                // Fewer than two frames a second: the rate from the last frame time, or from the wait when it is longer.
                fps = 1000 / Math.Max(frameTime, sinceLast);
            }

            var intervals = new double[Math.Max(0, times.Length - 1)];
            for (var i = 1; i < times.Length; i++)
            {
                intervals[i - 1] = (times[i] - times[i - 1]) * 1000.0 / ticksPerSecond;
            }

            // The frames that ended in the last second (also the one that ended its first frame, so a hitch across two
            // seconds is counted once, in the second its frame came): the longest, and the stutters against the usual
            // (median) frame time of the 30 s.
            var lastIntervals = intervals.AsSpan(intervals.Length - Math.Min(intervals.Length, lastSecond.Length));
            var worstMs = sinceLast > frameTime ? sinceLast : 0;
            foreach (var interval in lastIntervals)
            {
                worstMs = Math.Max(worstMs, interval);
            }

            var sorted = intervals.ToArray();
            Array.Sort(sorted);
            var stutters = 0;
            if (sorted.Length >= StutterFrames)
            {
                var first = intervals.Length - lastIntervals.Length;
                for (var i = first; i < intervals.Length; i++)
                {
                    var around = Neighbors(intervals, i);
                    if (around > 0 && intervals[i] >= Math.Max(around * StutterFactor, around + StutterMinExtraMs))
                    {
                        stutters++;
                    }
                }
            }

            double? low = null;
            if (times.Length > LowFrames)
            {
                var worst = Math.Max(1, sorted.Length / 100);
                var average = sorted.AsSpan(sorted.Length - worst).ToArray().Average();
                low = average > 0 ? 1000 / average : null;
            }

            return (pick, fps, frameTime, low, pick == foreground, worstMs, stutters);
        }
    }

    /// <summary>The median of the frame times around <paramref name="index"/> (up to <see cref="StutterNeighbors"/> on each side, not itself).</summary>
    internal static double Neighbors(double[] intervals, int index)
    {
        var around = new List<double>(2 * StutterNeighbors);
        for (var i = Math.Max(0, index - StutterNeighbors); i <= Math.Min(intervals.Length - 1, index + StutterNeighbors); i++)
        {
            if (i != index)
            {
                around.Add(intervals[i]);
            }
        }

        if (around.Count == 0)
        {
            return 0;
        }

        around.Sort();
        return around.Count % 2 == 1 ? around[around.Count / 2] : (around[(around.Count / 2) - 1] + around[around.Count / 2]) / 2;
    }
}

/// <summary>
/// The game's usual frame rate: the median of its per-second frame rates over the last two minutes, after its first
/// 30 s (loading screens and shader compiling right after a start are not its usual). A frame rate well below it is a
/// drop. Not thread-safe (one per collector).
/// </summary>
public sealed class UsualFrameRate
{
    /// <summary>How far back the median looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    /// <summary>How long a game must have been measured before it has a usual frame rate.</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(30);

    /// <summary>A pause in its frames longer than this starts it over (another session).</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(60);

    private readonly Queue<(DateTimeOffset At, double Fps)> _recent = new();
    private int _processId;
    private DateTimeOffset _since;
    private DateTimeOffset _last;

    /// <summary>Adds a second's frame rate of <paramref name="processId"/> and returns its usual frame rate, or null while warming up.</summary>
    public double? Add(int processId, DateTimeOffset at, double fps)
    {
        if (processId != _processId || at - _last > Gap)
        {
            _recent.Clear();
            _processId = processId;
            _since = at;
        }

        _last = at;
        _recent.Enqueue((at, fps));
        while (_recent.Count > 0 && at - _recent.Peek().At > Window)
        {
            _recent.Dequeue();
        }

        if (at - _since < Warmup)
        {
            return null;
        }

        var values = _recent.Select(r => r.Fps).Order().ToList();
        return values[values.Count / 2];
    }
}
