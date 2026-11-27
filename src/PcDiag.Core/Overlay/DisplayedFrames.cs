using System.Globalization;

namespace PcDiag.Core.Overlay;

/// <summary>One frame as PresentMon reports it, once it reached the screen or was replaced before it did.</summary>
/// <param name="StartQpc">When the CPU started working on it (QPC ticks, the clock of <see cref="System.Diagnostics.Stopwatch"/>).</param>
/// <param name="FrameMs">The time between this present and the previous one.</param>
/// <param name="Displayed">It reached the screen.</param>
/// <param name="DisplayedMs">How long it stayed on the screen (null when not displayed).</param>
/// <param name="DisplayLatencyMs">From its start until it was on the screen (null when not displayed or not measured).</param>
/// <param name="GpuBusyMs">How long the GPU worked on it.</param>
/// <param name="CpuBusyMs">How long the CPU worked on it before presenting it.</param>
/// <param name="PresentMode">How Windows showed it, e.g. "Hardware: Independent Flip", "Composed: Flip".</param>
public sealed record PresentedFrame(long StartQpc, double FrameMs, bool Displayed, double? DisplayedMs, double? DisplayLatencyMs, double? GpuBusyMs, double? CpuBusyMs, string? PresentMode);

/// <summary>The last second of a game's frames as they reached the screen.</summary>
/// <param name="PresentedFps">Frames presented.</param>
/// <param name="DisplayedFps">Frames that reached the screen.</param>
/// <param name="Dropped">Frames presented that never reached the screen.</param>
/// <param name="WorstDisplayedMs">The longest time one image stayed on the screen.</param>
/// <param name="DisplayLatencyMs">The average time from a frame's start until it was on the screen.</param>
/// <param name="GpuBusyPercent">The GPU's work as a share of the frames' time.</param>
/// <param name="CpuBusyPercent">The CPU's work before presenting as a share of the frames' time.</param>
/// <param name="PresentMode">The most common way Windows showed them.</param>
public sealed record DisplayedStats(double PresentedFps, double DisplayedFps, int Dropped, double? WorstDisplayedMs, double? DisplayLatencyMs, double? GpuBusyPercent,
    double? CpuBusyPercent, string? PresentMode);

/// <summary>Frames that reached the screen, for the game in front (PresentMon in the recorder).</summary>
public interface IDisplayedFrames
{
    /// <summary>Why it cannot measure (PresentMon missing, not elevated); null when it can.</summary>
    string? Problem { get; }

    /// <summary>Keeps measuring <paramref name="processId"/> for a while (another process replaces it).</summary>
    void Want(int processId);

    /// <summary>The last second of <paramref name="processId"/>'s frames, or null when it is not measured or showed none.</summary>
    DisplayedStats? Current(int processId);
}

/// <summary>
/// Reads PresentMon's CSV lines (<c>--output_stdout --qpc_time</c>): the header names the columns, so their order and the
/// metrics a version adds do not matter; "NA" is no value. A line that is not a frame is skipped.
/// </summary>
public sealed class PresentMonCsv
{
    private Dictionary<string, int>? _columns;

    /// <summary>The frame on a line, or null (the header, an empty or unreadable line).</summary>
    public PresentedFrame? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var cells = line.Split(',');
        if (cells.Length > 0 && cells[0] == "Application")
        {
            _columns = cells.Select((name, i) => (name.Trim(), i)).GroupBy(c => c.Item1).ToDictionary(g => g.Key, g => g.First().i, StringComparer.Ordinal);
            return null;
        }

        if (_columns is null || Number(cells, "CPUStartQPC") is not { } start)
        {
            return null;
        }

        var displayed = Number(cells, "DisplayedTime") ?? Number(cells, "MsBetweenDisplayChange");
        var frame = Number(cells, "MsBetweenPresents") ?? (Number(cells, "MsCPUBusy") + Number(cells, "MsCPUWait")) ?? Number(cells, "MsBetweenAppStart");
        return new PresentedFrame(
            (long)start,
            frame ?? 0,
            displayed is not null,
            displayed,
            Number(cells, "DisplayLatency") ?? Number(cells, "MsUntilDisplayed"),
            Number(cells, "MsGPUBusy"),
            Number(cells, "MsCPUBusy"),
            Text(cells, "PresentMode"));
    }

    private double? Number(string[] cells, string column) =>
        _columns!.TryGetValue(column, out var i) && i < cells.Length && double.TryParse(cells[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : null;

    private string? Text(string[] cells, string column) =>
        _columns!.TryGetValue(column, out var i) && i < cells.Length && cells[i] is { Length: > 0 } text && text != "NA" ? text : null;
}

/// <summary>
/// A game's PresentMon frames of the last 30 s and the last second's figures. PresentMon reports a frame once it reached
/// the screen or was replaced, a little after it was presented, so the second read ends <see cref="Settle"/> before now.
/// Thread-safe.
/// </summary>
public sealed class DisplayedFrameTimes(long ticksPerSecond)
{
    /// <summary>How long before "now" the second read ends.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    private readonly Lock _lock = new();
    private readonly Queue<PresentedFrame> _frames = new();

    /// <summary>Adds a frame.</summary>
    public void Add(PresentedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_lock)
        {
            _frames.Enqueue(frame);
            while (_frames.Count > 0 && frame.StartQpc - _frames.Peek().StartQpc > 30 * ticksPerSecond)
            {
                _frames.Dequeue();
            }
        }
    }

    /// <summary>Forgets every frame (another process is measured).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
        }
    }

    /// <summary>The second ending <see cref="Settle"/> before <paramref name="now"/> (QPC ticks), or null when it has no frame.</summary>
    public DisplayedStats? Stats(long now)
    {
        var end = now - (long)(Settle.TotalSeconds * ticksPerSecond);
        List<PresentedFrame> second;
        lock (_lock)
        {
            second = _frames.Where(f => f.StartQpc <= end && end - f.StartQpc < ticksPerSecond).ToList();
        }

        if (second.Count == 0)
        {
            return null;
        }

        var shown = second.Where(f => f.Displayed).ToList();
        var time = second.Sum(f => f.FrameMs);
        double? Share(Func<PresentedFrame, double?> busy) =>
            time > 0 && second.Any(f => busy(f) is not null) ? Math.Round(Math.Min(100, 100 * second.Sum(f => busy(f) ?? 0) / time), 1) : null;
        return new DisplayedStats(
            second.Count,
            shown.Count,
            second.Count - shown.Count,
            shown.Select(f => f.DisplayedMs).Where(d => d is not null).DefaultIfEmpty(null).Max(),
            shown.Select(f => f.DisplayLatencyMs).Where(l => l is not null).Select(l => l!.Value).DefaultIfEmpty(double.NaN).Average() is var latency && !double.IsNaN(latency)
                ? Math.Round(latency, 1)
                : null,
            Share(f => f.GpuBusyMs),
            Share(f => f.CpuBusyMs),
            second.Where(f => f.PresentMode is not null).GroupBy(f => f.PresentMode).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault());
    }
}
