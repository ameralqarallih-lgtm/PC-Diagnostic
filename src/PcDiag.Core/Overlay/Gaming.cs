namespace PcDiag.Core.Overlay;

/// <summary>Frame rate of the app drawing frames (the game in front, or the busiest one).</summary>
/// <param name="ProcessId">The app.</param>
/// <param name="ProcessName">Its name, e.g. "Cyberpunk2077".</param>
/// <param name="Fps">Frames in the last second.</param>
/// <param name="FrameTimeMs">Average time between frames over the last second.</param>
/// <param name="Low1Fps">The 1 % low: the frame rate of the slowest 1 % of frames over the last 30 s (null until there are enough).</param>
/// <param name="Foreground">The app is the one in front.</param>
public sealed record FrameStats(int ProcessId, string? ProcessName, double Fps, double FrameTimeMs, double? Low1Fps, bool Foreground);

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

    /// <summary>The app in front if it presented in the last 2 s, else the one that presented most; null when none did.</summary>
    public (int ProcessId, double Fps, double FrameTimeMs, double? Low1Fps, bool Foreground)? Stats(long now, int? foreground)
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
            if (fps < 2 && frameTime > 0)
            {
                // Fewer than two frames a second: the rate from the last frame time.
                fps = 1000 / frameTime;
            }

            double? low = null;
            if (times.Length > LowFrames)
            {
                var intervals = new double[times.Length - 1];
                for (var i = 1; i < times.Length; i++)
                {
                    intervals[i - 1] = (times[i] - times[i - 1]) * 1000.0 / ticksPerSecond;
                }

                Array.Sort(intervals);
                var worst = Math.Max(1, intervals.Length / 100);
                var average = intervals[^worst..].Average();
                low = average > 0 ? 1000 / average : null;
            }

            return (pick, fps, frameTime, low, pick == foreground);
        }
    }
}
