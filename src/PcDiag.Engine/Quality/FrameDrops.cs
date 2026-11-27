using System.Globalization;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Quality;

namespace PcDiag.Engine.Quality;

/// <summary>
/// Frame drops in a game's seconds, and what else was measured while each lasted, against the minute before it: the
/// GPU's load, clock and memory, the busiest processor while the GPU waited, the CPU's limits, paging, the drives,
/// drivers' DPC and interrupt time, and other programs. Findings say what was measured and what it may mean, worded
/// like the incident findings (see <see cref="Diagnostics.LanguageGuard"/>).
/// </summary>
public static class FrameDrops
{
    /// <summary>A second this far (%) or more below the game's usual frame rate is part of a drop.</summary>
    public const double DropPercent = 40;

    /// <summary>A frame this long is a hitch even when the second's frame rate is fine.</summary>
    public const double HitchMs = 100;

    /// <summary>Drop seconds this close together are one drop.</summary>
    public static readonly TimeSpan Merge = TimeSpan.FromSeconds(3);

    /// <summary>What the drop is compared with: this long before it…</summary>
    public static readonly TimeSpan Before = TimeSpan.FromSeconds(60);

    /// <summary>…up to this long before it.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

    /// <summary>Most drops listed for a session.</summary>
    public const int MaxDrops = 20;

    /// <summary>Whether a second is part of a drop (well below the usual frame rate, or a hitch).</summary>
    public static bool IsDrop(FrameSecond second) =>
        (second.UsualFps is { } usual && usual > 0 && second.Fps <= usual * (1 - (DropPercent / 100))) || second.WorstMs >= HitchMs;

    /// <summary>The drops in a game's seconds (oldest first), at most <see cref="MaxDrops"/>: the deepest and longest kept.</summary>
    public static List<(DateTimeOffset From, DateTimeOffset To)> Find(IReadOnlyList<FrameSecond> seconds)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        var drops = new List<(DateTimeOffset From, DateTimeOffset To, double Weight)>();
        (DateTimeOffset From, DateTimeOffset To, double Weight)? current = null;
        foreach (var second in seconds.OrderBy(s => s.At))
        {
            if (!IsDrop(second))
            {
                continue;
            }

            var depth = second.UsualFps is { } u && u > 0 ? Math.Max(0, 1 - (second.Fps / u)) : 0.5;
            if (current is { } c && second.At - c.To <= Merge)
            {
                current = (c.From, second.At, c.Weight + depth);
            }
            else
            {
                if (current is { } done)
                {
                    drops.Add(done);
                }

                current = (second.At, second.At, depth);
            }
        }

        if (current is { } last)
        {
            drops.Add(last);
        }

        return drops.OrderByDescending(d => d.Weight).Take(MaxDrops).OrderBy(d => d.From).Select(d => (d.From, d.To)).ToList();
    }

    /// <summary>The game's seconds from the tracks of a recording ("game.fps", "game.usual", "game.worst", "game.stutters").</summary>
    public static List<FrameSecond> Seconds(IReadOnlyList<MetricTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var fps = tracks.FirstOrDefault(t => t.Metric == "game.fps");
        if (fps is null)
        {
            return [];
        }

        var usual = ByTime(tracks, "game.usual");
        var worst = ByTime(tracks, "game.worst");
        var stutters = ByTime(tracks, "game.stutters");
        return fps.Points
            .Select(p => new FrameSecond(p.Timestamp, p.Value, worst.GetValueOrDefault(p.Timestamp), (int)Math.Round(stutters.GetValueOrDefault(p.Timestamp) ?? 0), usual.GetValueOrDefault(p.Timestamp)))
            .ToList();
    }

    /// <summary>
    /// A drop with what else was measured during it. <paramref name="tracks"/> are the recording's metric tracks;
    /// <paramref name="processes"/> other programs' activity before and during it (the game is left out by name).
    /// </summary>
    public static FrameDrop Explain(
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<FrameSecond> seconds,
        IReadOnlyList<MetricTrack> tracks,
        IReadOnlyList<ProcessActivity>? processes,
        string? game,
        IReadOnlyList<DriverLatencyMeasurement>? measurements,
        Func<DateTimeOffset, string> clock)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(clock);
        var inDrop = seconds.Where(s => s.At >= from && s.At <= to).ToList();
        var lowest = inDrop.Count > 0 ? inDrop.Min(s => s.Fps) : 0;
        var usual = inDrop.Select(s => s.UsualFps).LastOrDefault(u => u is not null);
        var worst = inDrop.Select(s => s.WorstMs).Where(w => w is not null).DefaultIfEmpty(null).Max();
        var stutters = inDrop.Sum(s => s.Stutters);

        // The other collectors sample on the same tick as the frames, so the drop's own seconds are compared.
        var w = new Window(from, to, from - Before, from - Gap);
        var findings = new List<QualityFinding>();
        var held = FrameShare(tracks, w, findings);
        Gpu(tracks, w, findings, held);
        Cpu(tracks, w, findings, held);
        Shown(tracks, w, findings);
        Memory(tracks, w, findings);
        Storage(tracks, w, findings);
        Drivers(tracks, w, measurements, clock, findings);
        Programs(processes, game, findings);
        if (findings.Count == 0)
        {
            findings.Add(new QualityFinding("game", 0,
                "Nothing else measured changed during the drop: the game's own work (a heavier scene, loading, or compiling shaders) may be what slowed it."));
        }

        return new FrameDrop(from, to, Math.Round(lowest, 1), usual, worst, stutters, findings.OrderByDescending(f => f.Severity).ToList());
    }

    private sealed record Window(DateTimeOffset From, DateTimeOffset To, DateTimeOffset BeforeFrom, DateTimeOffset BeforeTo);

    /// <summary>
    /// Which side held each frame back, from PresentMon's per-frame times: the GPU working on a frame nearly all its time
    /// is the GPU's limit; the game's CPU work taking nearly all of it while the GPU waits is the CPU's. Returns "gpu",
    /// "cpu" or null (not measured, or neither).
    /// </summary>
    private static string? FrameShare(IReadOnlyList<MetricTrack> tracks, Window w, List<QualityFinding> findings)
    {
        var gpu = Average(tracks, "game.gpubusy", w.From, w.To);
        var cpu = Average(tracks, "game.cpubusy", w.From, w.To);
        if (gpu is >= 90)
        {
            findings.Add(new QualityFinding("gpu", 2,
                $"The GPU worked on each frame {N(gpu.Value)} % of its time during the drop: the graphics card may be the limit in this scene; lower graphics settings or resolution could raise the frame rate."));
            return "gpu";
        }

        if (cpu is >= 85 && gpu is null or <= 70)
        {
            var waiting = gpu is { } g ? $" while the GPU worked on it {N(g)} % of the time" : "";
            findings.Add(new QualityFinding("cpu", 2,
                $"The game's CPU work took {N(cpu.Value)} % of each frame's time during the drop{waiting}: the CPU side (usually the game's main thread) may be the limit; fewer background programs or lower CPU-heavy settings (view distance, crowds) could help."));
            return "cpu";
        }

        return null;
    }

    /// <summary>Frames that stopped reaching the screen while the game still presented them.</summary>
    private static void Shown(IReadOnlyList<MetricTrack> tracks, Window w, List<QualityFinding> findings)
    {
        if (Average(tracks, "game.shown", w.From, w.To) is not { } shown || Average(tracks, "game.fps", w.From, w.To) is not { } presented || presented <= 0)
        {
            return;
        }

        var before = Average(tracks, "game.shown", w.BeforeFrom, w.BeforeTo);
        if (shown <= presented * 0.75 && before is { } b && shown <= b * 0.8)
        {
            findings.Add(new QualityFinding("display", 1,
                $"Only {N(shown)} of the {N(presented)} frames a second the game presented reached the screen during the drop ({N(b)} a second were shown before it): frames may be replaced or held up on their way to the screen (the desktop compositor in windowed mode, or a sync setting)."));
        }
    }

    private static void Gpu(IReadOnlyList<MetricTrack> tracks, Window w, List<QualityFinding> findings, string? held)
    {
        // The GPU that was busiest during the drop.
        var gpu = tracks.Where(t => t.Metric == "gpu.util").Select(t => (Track: t, During: Average(t, w.From, w.To))).Where(x => x.During is not null).OrderByDescending(x => x.During).FirstOrDefault();
        if (gpu.Track is not null)
        {
            var during = gpu.During!.Value;
            var before = Average(gpu.Track, w.BeforeFrom, w.BeforeTo);
            if (during >= 95 && held is null)
            {
                var then = before is >= 95 ? " and before it" : before is { } b ? $" ({N(b)} % before it)" : "";
                findings.Add(new QualityFinding("gpu", 2,
                    $"The GPU was {N(during)} % busy during the drop{then}: the graphics card may be the limit in this scene; lower graphics settings or resolution could raise the frame rate."));
            }

            var clock = Sibling(tracks, gpu.Track, "core");
            if (clock is not null && Average(clock, w.BeforeFrom, w.BeforeTo) is { } clockBefore && clockBefore >= 300 && Min(clock, w.From, w.To) is { } clockDuring
                && clockDuring <= clockBefore * 0.8 && during >= 50)
            {
                var hot = Max(Sibling(tracks, gpu.Track, "hotspot"), w.From, w.To) is { } hotspot && hotspot >= 95 ? $" while its hotspot was at {N(hotspot)} °C"
                    : Max(Sibling(tracks, gpu.Track, "temp"), w.From, w.To) is { } temp && temp >= 85 ? $" while it was at {N(temp)} °C"
                    : null;
                findings.Add(hot is not null
                    ? new QualityFinding("thermal", 2, $"The GPU's clock fell from {N(clockBefore)} to {N(clockDuring)} MHz{hot}: it may have slowed itself down to stay cool.")
                    : new QualityFinding("gpu", 2, $"The GPU's clock fell from {N(clockBefore)} to {N(clockDuring)} MHz during the drop: a power limit or power saving may have slowed it."));
            }
        }

        if (Max(tracks, "vram", w.From, w.To) is { } vram && vram >= 95)
        {
            findings.Add(new QualityFinding("gpu", 2,
                $"The GPU's memory was {N(vram)} % full: the game's textures may not all fit and spill into system memory, which makes frames late; a lower texture setting could help."));
        }
    }

    private static void Cpu(IReadOnlyList<MetricTrack> tracks, Window w, List<QualityFinding> findings, string? held)
    {
        var gpu = tracks.Where(t => t.Metric == "gpu.util").Select(t => Average(t, w.From, w.To)).Where(v => v is not null).DefaultIfEmpty(null).Max();
        // When PresentMon's per-frame times already said which side held the frames back, the loads are not repeated.
        if (held is null && Max(tracks, "cpu.maxcore", w.From, w.To) is { } core && core >= 95 && gpu is null or < 85)
        {
            var waiting = gpu is { } g ? $" while the GPU was only {N(g)} % busy" : "";
            findings.Add(new QualityFinding("cpu", 2,
                $"One processor was {N(core)} % busy{waiting}: the game may be waiting on a single CPU thread (a CPU limit); fewer background programs or lower CPU-heavy settings (view distance, crowds) could help."));
        }
        else if (held is null && Average(tracks, "cpu", w.From, w.To) is { } total && total >= 90)
        {
            findings.Add(new QualityFinding("cpu", 2, $"The CPU was {N(total)} % busy during the drop: it may have had no time to spare for the game."));
        }

        if (Min(tracks, "cpu.limit", w.From, w.To) is { } limit && limit < 90)
        {
            findings.Add(new QualityFinding("thermal", 2, $"Windows limited the CPU to {N(limit)} % of its performance during the drop (power, temperature or firmware limits)."));
        }

        if (Max(tracks, "cpu.temp", w.From, w.To) is { } temp && temp >= 95)
        {
            findings.Add(new QualityFinding("thermal", 2, $"The CPU was at {N(temp)} °C: it may slow itself down at this temperature."));
        }
    }

    private static void Memory(IReadOnlyList<MetricTrack> tracks, Window w, List<QualityFinding> findings)
    {
        if (Max(tracks, "memory.hardfaults", w.From, w.To) is { } faults && faults >= Math.Max(200, 3 * (Average(tracks, "memory.hardfaults", w.BeforeFrom, w.BeforeTo) ?? 0)))
        {
            findings.Add(new QualityFinding("memory", 2,
                $"Windows read up to {N(faults)} pages a second back from disk (hard faults): memory may be short, and the game waits for what was paged out."));
        }

        if (Min(tracks, "memory.available", w.From, w.To) is { } available && available < 1_073_741_824)
        {
            findings.Add(new QualityFinding("memory", 2, $"Only {AnalysisFormat.Bytes(available)} of memory was free during the drop."));
        }
    }

    private static void Storage(IReadOnlyList<MetricTrack> tracks, Window w, List<QualityFinding> findings)
    {
        var latency = tracks.Where(t => t.Metric is "disk.readlat" or "disk.writelat")
            .Select(t => (Track: t, During: Max(t, w.From, w.To), Before: Average(t, w.BeforeFrom, w.BeforeTo)))
            .Where(x => x.During is >= 50 && x.During >= 3 * (x.Before ?? 0))
            .OrderByDescending(x => x.During)
            .FirstOrDefault();
        if (latency.Track is not null)
        {
            findings.Add(new QualityFinding("storage", 2,
                $"{latency.Track.Device ?? "A drive"} answered in up to {N(latency.During!.Value)} ms during the drop: the game may have been waiting for files (loading or streaming)."));
        }
    }

    private static void Drivers(IReadOnlyList<MetricTrack> tracks, Window w, IReadOnlyList<DriverLatencyMeasurement>? measurements, Func<DateTimeOffset, string> clock,
        List<QualityFinding> findings)
    {
        var dpc = Max(tracks, "latency.dpc", w.From, w.To) ?? 0;
        var isr = Max(tracks, "latency.isr", w.From, w.To) ?? 0;
        var dpcBefore = Average(tracks, "latency.dpc", w.BeforeFrom, w.BeforeTo) ?? 0;
        var isrBefore = Average(tracks, "latency.isr", w.BeforeFrom, w.BeforeTo) ?? 0;
        var (what, value, before) = dpc >= isr ? ("DPCs", dpc, dpcBefore) : ("interrupts", isr, isrBefore);
        var measured = measurements?.Where(m => m.At >= w.From - TimeSpan.FromMinutes(5) && m.From <= w.To + TimeSpan.FromMinutes(5) && m.Drivers.Count > 0)
            .Select(m => (Measurement: m, Slowest: m.Drivers.MaxBy(d => d.LongestUs)!)).OrderByDescending(x => x.Slowest.LongestUs).FirstOrDefault();
        var named = measured is { Slowest: { } slowest } x && slowest.LongestUs >= 500
            ? $" The driver measurement at {clock(x.Measurement.From)} found {slowest.Driver} running up to {N(slowest.LongestUs)} µs at a time."
            : "";
        if (value >= LatencyDiagnosis.BusyPercent || (value >= 15 && value >= before + 15))
        {
            findings.Add(new QualityFinding("driver", value >= LatencyDiagnosis.VeryBusyPercent ? 3 : 2,
                $"Drivers held one processor {N(value)} % of the time in {what} during the drop ({N(before)} % before): the game's threads may have had to wait for them.{named}"));
        }
        else if (named.Length > 0)
        {
            findings.Add(new QualityFinding("driver", 1, named.Trim()));
        }
    }

    private static void Programs(IReadOnlyList<ProcessActivity>? processes, string? game, List<QualityFinding> findings)
    {
        if (processes is null)
        {
            return;
        }

        var risen = processes
            .Where(p => !string.Equals(p.Name, game, StringComparison.OrdinalIgnoreCase) && p.Name is not ("Idle" or "System Idle Process"))
            .Select(p => (Process: p, Score: Math.Max((p.CpuDuring - p.CpuBefore) / 5, Math.Max((p.GpuDuring - p.GpuBefore) / 15, (p.DiskDuring - p.DiskBefore) / 30e6))))
            .Where(x => x.Score >= 1)
            .OrderByDescending(x => x.Score)
            .Take(3);
        foreach (var (p, score) in risen)
        {
            var parts = new List<string>();
            if (p.CpuDuring - p.CpuBefore >= 5)
            {
                parts.Add($"{N(p.CpuDuring)} % CPU ({N(p.CpuBefore)} % before)");
            }

            if (p.GpuDuring - p.GpuBefore >= 15)
            {
                parts.Add($"{N(p.GpuDuring)} % GPU ({N(p.GpuBefore)} % before)");
            }

            if (p.DiskDuring - p.DiskBefore >= 30e6)
            {
                parts.Add($"{AnalysisFormat.Bytes(p.DiskDuring)}/s of disk ({AnalysisFormat.Bytes(p.DiskBefore)}/s before)");
            }

            findings.Add(new QualityFinding("program", score >= 3 ? 2 : 1,
                $"{p.Name} used {string.Join(", ", parts)} during the drop: it may have taken time the game needed."));
        }
    }

    private static Dictionary<DateTimeOffset, double?> ByTime(IReadOnlyList<MetricTrack> tracks, string metric)
    {
        var map = new Dictionary<DateTimeOffset, double?>();
        foreach (var p in tracks.FirstOrDefault(t => t.Metric == metric)?.Points ?? [])
        {
            map[p.Timestamp] = p.Value;
        }

        return map;
    }

    private static MetricTrack? Sibling(IReadOnlyList<MetricTrack> tracks, MetricTrack track, string suffix)
    {
        var prefix = track.SeriesId[..(track.SeriesId.LastIndexOf('.') + 1)];
        return tracks.FirstOrDefault(t => t.SeriesId == prefix + suffix);
    }

    private static double? Average(MetricTrack? track, DateTimeOffset from, DateTimeOffset to) =>
        track?.Between(from, to).Select(p => p.Value).DefaultIfEmpty(double.NaN).Average() is { } v && !double.IsNaN(v) ? v : null;

    private static double? Max(MetricTrack? track, DateTimeOffset from, DateTimeOffset to) =>
        track?.Between(from, to).Select(p => p.Value).DefaultIfEmpty(double.NaN).Max() is { } v && !double.IsNaN(v) ? v : null;

    private static double? Min(MetricTrack? track, DateTimeOffset from, DateTimeOffset to) =>
        track?.Between(from, to).Select(p => p.Value).DefaultIfEmpty(double.NaN).Min() is { } v && !double.IsNaN(v) ? v : null;

    private static double? Average(IReadOnlyList<MetricTrack> tracks, string metric, DateTimeOffset from, DateTimeOffset to) => Average(tracks.FirstOrDefault(t => t.Metric == metric), from, to);

    private static double? Max(IReadOnlyList<MetricTrack> tracks, string metric, DateTimeOffset from, DateTimeOffset to) =>
        tracks.Where(t => t.Metric == metric).Select(t => Max(t, from, to)).Where(v => v is not null).DefaultIfEmpty(null).Max();

    private static double? Min(IReadOnlyList<MetricTrack> tracks, string metric, DateTimeOffset from, DateTimeOffset to) =>
        tracks.Where(t => t.Metric == metric).Select(t => Min(t, from, to)).Where(v => v is not null).DefaultIfEmpty(null).Min();

    private static string N(double value) => value.ToString(Math.Abs(value) < 10 && value != Math.Round(value) ? "0.#" : "0", CultureInfo.InvariantCulture);
}

/// <summary>Byte sizes as the findings write them.</summary>
internal static class AnalysisFormat
{
    /// <summary>"4.2 GB", "512 MB", "80 KB".</summary>
    public static string Bytes(double bytes) => Diagnostics.AnalysisContext.FormatBytes(bytes);
}
