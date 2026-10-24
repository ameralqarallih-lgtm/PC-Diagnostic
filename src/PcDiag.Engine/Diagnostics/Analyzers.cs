using System.Globalization;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;

namespace PcDiag.Engine.Diagnostics;

/// <summary>Helpers shared by the analyzers.</summary>
internal static class Analysis
{
    /// <summary>Most findings an analyzer reports for one kind of event.</summary>
    public const int MaxEventsPerKind = 6;

    public static Finding Observation(string text, params EvidenceRef[] refs) => new(EvidenceLevel.Observation, text, refs);

    public static Finding Confirmed(string text, params EvidenceRef[] refs) => new(EvidenceLevel.ConfirmedEvent, text, refs);

    public static string N(double value, string format = "0") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>The value at <paramref name="percentile"/> (0–100) of a non-empty list.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        var index = (int)Math.Clamp(Math.Round(percentile / 100 * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>The device's track of another metric, e.g. "gpu.3.util" for "gpu.3.core".</summary>
    public static MetricTrack? Sibling(IncidentEvidence evidence, MetricTrack track, string suffix)
    {
        var prefix = track.SeriesId[..(track.SeriesId.LastIndexOf('.') + 1)];
        return evidence.Metrics.FirstOrDefault(m => m.SeriesId == prefix + suffix);
    }

    /// <summary>The value nearest to a moment (within 3 s), or null.</summary>
    public static double? ValueAt(MetricTrack? track, DateTimeOffset at)
    {
        if (track is null || track.Points.Count == 0)
        {
            return null;
        }

        var nearest = track.Points.MinBy(p => (p.Timestamp - at).Duration());
        return (nearest.Timestamp - at).Duration() <= TimeSpan.FromSeconds(3) ? nearest.Value : null;
    }

    /// <summary>Windows events of the given categories inside the incident window, oldest first.</summary>
    public static IEnumerable<EvidenceEvent> WindowsEvents(AnalysisContext context, params EventCategory[] categories) =>
        context.Evidence.Events.Where(e => e.Kind == "windows-event" && e.Category is { } c && categories.Contains(c)
                                           && e.Timestamp >= context.Evidence.From && e.Timestamp <= context.Evidence.To);

    /// <summary>A ConfirmedEvent finding quoting what Windows logged.</summary>
    public static Finding Logged(AnalysisContext context, EvidenceEvent e) =>
        Confirmed($"Windows logged \"{e.Title}\" ({AnalysisContext.EventSource(e)}) at {context.Clock(e.Timestamp)}, {context.Relative(e.Timestamp)}.", e.ToRef());
}

/// <summary>CPU load and Windows-reported performance limits.</summary>
public sealed class CpuAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "cpu";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Evidence.Track("cpu") is { } cpu && cpu.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } around)
        {
            var peak = around.MaxBy(p => p.Value);
            if (peak.Value >= 90)
            {
                var reference = AnalysisContext.Metric(cpu, peak.Timestamp);
                evidence.Note(Subsystems.Cpu, peak.Timestamp, $"CPU at {Analysis.N(peak.Value)} %", reference);
                yield return Analysis.Observation($"CPU utilization reached {Analysis.N(peak.Value)} % at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).", reference);
            }
            else
            {
                yield return Analysis.Observation($"CPU utilization stayed at or below {Analysis.N(Math.Ceiling(peak.Value))} % around {evidence.Incident}.", evidence.Range(cpu));
            }
        }

        if (evidence.Evidence.Track("cpu.limit") is { } limit && limit.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } limits)
        {
            var lowest = limits.MinBy(p => p.Value);
            if (lowest.Value < 80)
            {
                var reference = AnalysisContext.Metric(limit, lowest.Timestamp);
                evidence.Note(Subsystems.Thermal, lowest.Timestamp, $"CPU performance limited to {Analysis.N(lowest.Value)} %", reference);
                yield return Analysis.Observation(
                    $"Windows reported the CPU limited to {Analysis.N(lowest.Value)} % of its performance at {evidence.Clock(lowest.Timestamp)} ({evidence.Relative(lowest.Timestamp)}) by power, thermal or firmware limits.",
                    reference);
            }
        }
    }
}

/// <summary>GPU utilization and VRAM around the incident.</summary>
public sealed class GpuActivityAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "gpu-activity";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var low = new List<(string Device, double Peak, EvidenceRef Range)>();
        foreach (var track in evidence.Evidence.Tracks("gpu.util"))
        {
            var around = track.Between(evidence.AroundFrom, evidence.AroundTo).ToList();
            if (around.Count == 0)
            {
                continue;
            }

            var device = track.Device ?? "the GPU";
            var peak = around.MaxBy(p => p.Value);
            if (peak.Value >= 90)
            {
                var earlier = track.Between(evidence.Evidence.From, evidence.AroundFrom).Select(p => p.Value).Order().ToList();
                var typical = earlier.Count >= 30
                    ? $" Earlier in the window it was typically {Analysis.N(Analysis.Percentile(earlier, 10))}–{Analysis.N(Analysis.Percentile(earlier, 90))} %."
                    : "";
                var reference = AnalysisContext.Metric(track, peak.Timestamp);
                evidence.Note(Subsystems.Gpu, peak.Timestamp, $"GPU utilization {Analysis.N(peak.Value)} %", reference);
                yield return Analysis.Observation(
                    $"GPU utilization of {device} reached {Analysis.N(peak.Value)} % at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).{typical}", reference);
            }
            else if (peak.Value >= 50)
            {
                // Busy but short of saturated: say what was seen, without calling it low or notable.
                yield return Analysis.Observation(
                    $"GPU utilization of {device} peaked at {Analysis.N(peak.Value)} % at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).",
                    AnalysisContext.Metric(track, peak.Timestamp));
            }
            else
            {
                low.Add((device, Math.Ceiling(peak.Value), evidence.Range(track)));
            }
        }

        if (low.Count > 0)
        {
            // Identical adapters (same name and value) are listed once.
            var parts = low.Select(l => $"{l.Device} up to {Analysis.N(l.Peak)} %").Distinct(StringComparer.Ordinal);
            yield return Analysis.Observation($"GPU utilization stayed low around {evidence.Incident} ({string.Join("; ", parts)}).", low.Select(l => l.Range).ToArray());
        }

        if (evidence.Evidence.Track("vram") is { } vram && vram.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } points)
        {
            var peak = points.MaxBy(p => p.Value);
            if (peak.Value >= 95)
            {
                var reference = AnalysisContext.Metric(vram, peak.Timestamp);
                evidence.Note(Subsystems.Gpu, peak.Timestamp, $"VRAM {Analysis.N(peak.Value)} % full", reference);
                yield return Analysis.Observation($"Dedicated GPU memory was {Analysis.N(peak.Value)} % full at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).", reference);
            }
        }
    }
}

/// <summary>Sudden GPU core clock drops (PROJECT §19: "GPU clock dropped from 2050 MHz to 300 MHz").</summary>
public sealed class GpuClockAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <summary>A drop counts when the clock falls by this share…</summary>
    public const double MinDropPercent = 50;

    /// <summary>…while the GPU is at least this busy (just before or at the drop); an idle GPU lowering its clock is normal power saving.</summary>
    public const double MinUtilizationPercent = 30;

    /// <summary>…within this time.</summary>
    public static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public string Id => "gpu-clock";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var track in evidence.Evidence.Tracks("gpu.core"))
        {
            var points = track.Between(evidence.Center - TimeSpan.FromMinutes(2), evidence.AroundTo).ToList();
            if (points.Count < 2)
            {
                continue;
            }

            var utilizationTrack = Analysis.Sibling(evidence.Evidence, track, "util");
            (MetricPoint From, MetricPoint To, double Drop)? best = null;
            for (var i = 1; i < points.Count; i++)
            {
                var current = points[i];
                MetricPoint? reference = null;
                for (var j = i - 1; j >= 0 && current.Timestamp - points[j].Timestamp <= DropWindow; j--)
                {
                    if (reference is null || points[j].Value > reference.Value.Value)
                    {
                        reference = points[j];
                    }
                }

                if (reference is { Value: >= 300 } r)
                {
                    var drop = (r.Value - current.Value) / r.Value * 100;
                    var busy = utilizationTrack is null
                        || Analysis.ValueAt(utilizationTrack, r.Timestamp) >= MinUtilizationPercent
                        || Analysis.ValueAt(utilizationTrack, current.Timestamp) >= MinUtilizationPercent;
                    if (drop >= MinDropPercent && busy && (best is null || drop > best.Value.Drop))
                    {
                        best = (r, current, drop);
                    }
                }
            }

            var device = track.Device ?? "the GPU";
            if (best is { } b)
            {
                var utilization = Analysis.ValueAt(utilizationTrack, b.To.Timestamp);
                var reference = AnalysisContext.Metric(track, b.To.Timestamp);
                evidence.Note(Subsystems.Gpu, b.To.Timestamp, $"GPU clock drop from {Analysis.N(b.From.Value)} MHz to {Analysis.N(b.To.Value)} MHz", reference);

                var load = utilization is { } u ? $" GPU utilization was {Analysis.N(u)} % at that moment." : "";
                yield return Analysis.Observation(
                    $"The core clock of {device} dropped from {Analysis.N(b.From.Value)} MHz to {Analysis.N(b.To.Value)} MHz between {evidence.Clock(b.From.Timestamp)} and {evidence.Clock(b.To.Timestamp)} ({evidence.Relative(b.To.Timestamp)}).{load}",
                    reference);
            }
            else
            {
                yield return Analysis.Observation(
                    $"No sudden drop of the GPU core clock under load ({Analysis.N(MinDropPercent)} % or more within 5 s while the GPU was at least {Analysis.N(MinUtilizationPercent)} % busy) was recorded on {device}.",
                    evidence.Range(track));
            }
        }
    }
}

/// <summary>Display configuration changes and display or graphics driver events.</summary>
public sealed class DisplayEventAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "display";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var logged = Analysis.WindowsEvents(evidence, EventCategory.Display, EventCategory.Gpu).Take(Analysis.MaxEventsPerKind).ToList();
        foreach (var e in logged)
        {
            if (evidence.IsAround(e.Timestamp))
            {
                evidence.Note(Subsystems.Gpu, e.Timestamp, $"\"{e.Title}\"", e.ToRef());
            }

            yield return Analysis.Logged(evidence, e);
        }

        var changes = evidence.Evidence.Events
            .Where(e => e.Kind == "display" && e.Importance >= 1 && e.Timestamp >= evidence.Evidence.From && e.Timestamp <= evidence.Evidence.To)
            .Take(Analysis.MaxEventsPerKind)
            .ToList();
        foreach (var e in changes)
        {
            if (evidence.IsAround(e.Timestamp))
            {
                evidence.Note(Subsystems.Gpu, e.Timestamp, e.Title, e.ToRef());
            }

            yield return Analysis.Observation($"Display change recorded at {evidence.Clock(e.Timestamp)} ({evidence.Relative(e.Timestamp)}): {e.Title}.", e.ToRef());
        }

        if (logged.Count == 0 && changes.Count == 0
            && evidence.Evidence.Recorded(RecordingItems.Display, RecordingItems.Events(EventCategory.Display), RecordingItems.Events(EventCategory.Gpu)))
        {
            yield return Analysis.Observation("No display configuration change and no display or graphics driver event was recorded in the incident window.");
        }
    }
}

/// <summary>Available memory, commit and hard faults.</summary>
public sealed class MemoryPressureAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "memory";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        var lowGb = evidence.Threshold("memory-low", 1);
        if (e.Track("memory.available") is { } available && available.Between(e.From, e.To).ToList() is { Count: > 0 } points)
        {
            var lowest = points.MinBy(p => p.Value);
            var used = Analysis.ValueAt(e.Track("memory"), lowest.Timestamp);
            if (lowest.Value < lowGb * 1_073_741_824 || used >= 95)
            {
                var reference = AnalysisContext.Metric(available, lowest.Timestamp);
                if (evidence.IsAround(lowest.Timestamp) || points.Any(p => evidence.IsAround(p.Timestamp) && p.Value < lowGb * 1_073_741_824))
                {
                    evidence.Note(Subsystems.Memory, lowest.Timestamp, $"available memory down to {AnalysisContext.FormatBytes(lowest.Value)}", reference);
                }

                var share = used is { } u ? $" ({Analysis.N(u)} % of memory in use)" : "";
                yield return Analysis.Observation(
                    $"Available memory fell to {AnalysisContext.FormatBytes(lowest.Value)}{share} at {evidence.Clock(lowest.Timestamp)} ({evidence.Relative(lowest.Timestamp)}).", reference);
            }
            else
            {
                yield return Analysis.Observation(
                    $"No significant RAM pressure was observed (available memory stayed at or above {AnalysisContext.FormatBytes(lowest.Value)} throughout the window).", evidence.Range(available));
            }
        }

        if (e.Track("memory.commit") is { } commit && commit.Between(e.From, e.To).ToList() is { Count: > 0 } commits)
        {
            var peak = commits.MaxBy(p => p.Value);
            if (peak.Value >= 90)
            {
                var reference = AnalysisContext.Metric(commit, peak.Timestamp);
                if (evidence.IsAround(peak.Timestamp))
                {
                    evidence.Note(Subsystems.Memory, peak.Timestamp, $"memory commit at {Analysis.N(peak.Value)} % of its limit", reference);
                }

                yield return Analysis.Observation($"Memory commit reached {Analysis.N(peak.Value)} % of the commit limit at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).", reference);
            }
        }

        if (e.Track("memory.hardfaults") is { } faults && faults.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } fault)
        {
            var peak = fault.MaxBy(p => p.Value);
            if (peak.Value >= 2000)
            {
                var reference = AnalysisContext.Metric(faults, peak.Timestamp);
                evidence.Note(Subsystems.Memory, peak.Timestamp, $"{Analysis.N(peak.Value)} hard page faults/s", reference);
                yield return Analysis.Observation(
                    $"Hard page faults (memory pages read back from disk) peaked at {Analysis.N(peak.Value)}/s at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).", reference);
            }
        }

        foreach (var logged in Analysis.WindowsEvents(evidence, EventCategory.ResourceExhaustion).Take(Analysis.MaxEventsPerKind))
        {
            evidence.Note(Subsystems.Memory, logged.Timestamp, $"\"{logged.Title}\"", logged.ToRef());
            yield return Analysis.Logged(evidence, logged);
        }
    }
}

/// <summary>Disk latency, queue length and storage errors.</summary>
public sealed class DiskLatencyAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "disk";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        var limit = evidence.Threshold("disk-latency-high", 200);
        var queueLimit = evidence.Threshold("disk-queue-high", 10);
        var over = false;
        (double Value, string Device)? highest = null;
        var checkedTracks = new List<MetricTrack>();
        foreach (var track in e.Tracks("disk.readlat").Concat(e.Tracks("disk.writelat")))
        {
            var points = track.Between(e.From, e.To).ToList();
            if (points.Count == 0)
            {
                continue;
            }

            checkedTracks.Add(track);
            var peak = points.MaxBy(p => p.Value);
            var device = track.Device ?? "a disk";
            if (highest is null || peak.Value > highest.Value.Value)
            {
                highest = (peak.Value, device);
            }

            if (peak.Value >= limit)
            {
                over = true;
                var reference = AnalysisContext.Metric(track, peak.Timestamp);
                var kind = track.Metric == "disk.readlat" ? "read" : "write";
                if (points.Any(p => p.Value >= limit && evidence.IsAround(p.Timestamp)))
                {
                    evidence.Note(Subsystems.Storage, peak.Timestamp, $"{kind} latency {Analysis.N(peak.Value)} ms on {device}", reference);
                }

                yield return Analysis.Observation(
                    $"The {kind} latency of {device} reached {Analysis.N(peak.Value)} ms at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}), above the configured limit of {Analysis.N(limit)} ms.",
                    reference);
            }
        }

        if (!over && highest is { } h)
        {
            yield return Analysis.Observation(
                $"Disk latency remained below the configured limit of {Analysis.N(limit)} ms (highest: {Analysis.N(h.Value, "0.#")} ms on {h.Device}).",
                checkedTracks.Select(evidence.Range).ToArray());
        }

        foreach (var track in e.Tracks("disk.queue"))
        {
            if (track.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } points && points.MaxBy(p => p.Value) is var peak && peak.Value >= queueLimit)
            {
                var reference = AnalysisContext.Metric(track, peak.Timestamp);
                evidence.Note(Subsystems.Storage, peak.Timestamp, $"disk queue {Analysis.N(peak.Value)} on {track.Device}", reference);
                yield return Analysis.Observation($"The disk queue of {track.Device ?? "a disk"} reached {Analysis.N(peak.Value)} at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).", reference);
            }
        }

        foreach (var logged in Analysis.WindowsEvents(evidence, EventCategory.Disk, EventCategory.FileSystem).Take(Analysis.MaxEventsPerKind))
        {
            evidence.Note(Subsystems.Storage, logged.Timestamp, $"\"{logged.Title}\"", logged.ToRef());
            yield return Analysis.Logged(evidence, logged);
        }
    }
}

/// <summary>Network errors, Wi-Fi signal and network events.</summary>
public sealed class NetworkAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "network";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        var anything = false;
        foreach (var track in e.Tracks("net.errors"))
        {
            var points = track.Between(e.From, e.To).ToList();
            var total = points.Sum(p => p.Value);
            if (total <= 0)
            {
                continue;
            }

            anything = true;
            var peak = points.MaxBy(p => p.Value);
            var reference = AnalysisContext.Metric(track, peak.Timestamp);
            if (evidence.IsAround(peak.Timestamp))
            {
                evidence.Note(Subsystems.Network, peak.Timestamp, $"{Analysis.N(total)} packet errors on {track.Device}", reference);
            }

            yield return Analysis.Observation(
                $"{track.Device ?? "A network adapter"} recorded {Analysis.N(total)} packet errors in the incident window, the most at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).",
                reference);
        }

        foreach (var track in e.Tracks("net.wifi"))
        {
            if (track.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } points && points.MinBy(p => p.Value) is var lowest && lowest.Value < 30)
            {
                anything = true;
                var reference = AnalysisContext.Metric(track, lowest.Timestamp);
                evidence.Note(Subsystems.Network, lowest.Timestamp, $"Wi-Fi signal {Analysis.N(lowest.Value)} %", reference);
                yield return Analysis.Observation($"The Wi-Fi signal of {track.Device ?? "the adapter"} fell to {Analysis.N(lowest.Value)} % at {evidence.Clock(lowest.Timestamp)} ({evidence.Relative(lowest.Timestamp)}).", reference);
            }
        }

        foreach (var logged in Analysis.WindowsEvents(evidence, EventCategory.Network).Take(Analysis.MaxEventsPerKind))
        {
            anything = true;
            if (evidence.IsAround(logged.Timestamp))
            {
                evidence.Note(Subsystems.Network, logged.Timestamp, $"\"{logged.Title}\"", logged.ToRef());
            }

            yield return Analysis.Logged(evidence, logged);
        }

        if (!anything && e.Tracks("net.errors").Any() && e.Recorded(RecordingItems.Events(EventCategory.Network)))
        {
            yield return Analysis.Observation("No network anomaly was detected: no packet errors and no network events were recorded in the incident window.");
        }
    }
}

/// <summary>Application crashes and hangs, service failures and processes that started just before.</summary>
public sealed class ProcessLifecycleAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <summary>Processes started this long before the incident are listed.</summary>
    public static readonly TimeSpan StartedWithin = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public string Id => "processes";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        var inWindow = e.Events.Where(x => x.Timestamp >= e.From && x.Timestamp <= e.To).ToList();
        var problems = inWindow.Where(x => x.Kind == "process" && x.Importance >= 2).Take(Analysis.MaxEventsPerKind).ToList();
        foreach (var p in problems)
        {
            if (evidence.IsAround(p.Timestamp))
            {
                evidence.Note(Subsystems.Application, p.Timestamp, p.Title, p.ToRef());
            }

            yield return Analysis.Observation($"{p.Title} at {evidence.Clock(p.Timestamp)} ({evidence.Relative(p.Timestamp)}).", p.ToRef());
        }

        var logged = Analysis.WindowsEvents(evidence, EventCategory.ApplicationCrash, EventCategory.ApplicationHang, EventCategory.WindowsErrorReporting)
            .Where(x => x.Importance >= 2)
            .Take(Analysis.MaxEventsPerKind)
            .ToList();
        foreach (var x in logged)
        {
            if (evidence.IsAround(x.Timestamp))
            {
                evidence.Note(Subsystems.Application, x.Timestamp, $"\"{x.Title}\"", x.ToRef());
            }

            yield return Analysis.Logged(evidence, x);
        }

        foreach (var s in inWindow.Where(x => x.Kind == "service" && x.Importance >= 2).Take(Analysis.MaxEventsPerKind))
        {
            if (evidence.IsAround(s.Timestamp))
            {
                evidence.Note(Subsystems.Application, s.Timestamp, s.Title, s.ToRef());
            }

            yield return Analysis.Observation($"Service change at {evidence.Clock(s.Timestamp)} ({evidence.Relative(s.Timestamp)}): {s.Title}.", s.ToRef());
        }

        var started = inWindow
            .Where(x => x.Kind == "process" && x.Title.EndsWith(" started", StringComparison.Ordinal) && x.Timestamp >= evidence.Center - StartedWithin && x.Timestamp <= evidence.Center)
            .ToList();
        if (started.Count > 0)
        {
            var names = started.Select(x => x.Title[..^" started".Length]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var list = string.Join(", ", names.Take(5)) + (names.Count > 5 ? $" and {names.Count - 5} more" : "");
            yield return Analysis.Observation(
                $"{started.Count} process start{(started.Count == 1 ? "" : "s")} recorded in the 30 s before {evidence.Incident}: {list}.",
                started.Take(5).Select(x => x.ToRef()).ToArray());
        }

        if (problems.Count == 0 && logged.Count == 0 && e.Recorded(
                RecordingItems.ProcessLifecycle,
                RecordingItems.Events(EventCategory.ApplicationCrash),
                RecordingItems.Events(EventCategory.ApplicationHang),
                RecordingItems.Events(EventCategory.WindowsErrorReporting)))
        {
            yield return Analysis.Observation("No application crash or hang was recorded in the incident window.");
        }
    }
}

/// <summary>CPU and GPU temperatures against the configured limits.</summary>
public sealed class ThermalAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "thermal";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        var normal = new List<string>();
        var ranges = new List<EvidenceRef>();
        var checks = new List<(MetricTrack Track, double Limit, string What)>();
        if (e.Track("cpu.temp") is { } cpu)
        {
            checks.Add((cpu, evidence.Threshold("cpu-temp-high", 95), "CPU package temperature"));
        }

        checks.AddRange(e.Tracks("gpu.temp").Select(t => (t, evidence.Threshold("gpu-temp-high", 87), $"temperature of {t.Device ?? "the GPU"}")));
        checks.AddRange(e.Tracks("gpu.hotspot").Select(t => (t, evidence.Threshold("gpu-hotspot-high", 105), $"hotspot temperature of {t.Device ?? "the GPU"}")));
        foreach (var (track, limit, what) in checks)
        {
            if (track.Between(e.From, e.To).ToList() is not { Count: > 0 } points)
            {
                continue;
            }

            var peak = points.MaxBy(p => p.Value);
            if (peak.Value >= limit)
            {
                var reference = AnalysisContext.Metric(track, peak.Timestamp);
                evidence.Note(Subsystems.Thermal, peak.Timestamp, $"{what} {Analysis.N(peak.Value)} °C", reference);
                yield return Analysis.Observation(
                    $"The {what} reached {Analysis.N(peak.Value)} °C at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}), at or above the configured limit of {Analysis.N(limit)} °C.",
                    reference);
            }
            else
            {
                normal.Add($"{what} up to {Analysis.N(peak.Value)} °C");
                ranges.Add(evidence.Range(track));
            }
        }

        if (normal.Count > 0)
        {
            yield return Analysis.Observation($"Temperatures stayed below the configured limits ({string.Join("; ", normal.Distinct(StringComparer.Ordinal))}).", ranges.ToArray());
        }

        if (e.Track("cpu.temp") is null)
        {
            yield return Analysis.Observation("The CPU temperature was not recorded, so CPU overheating cannot be assessed for this incident.");
        }
    }
}

/// <summary>What Windows logged about its own stability: bug checks, unexpected shutdowns, hardware errors, kernel reports, device problems.</summary>
public sealed class SystemEventAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "system";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var logged in Analysis.WindowsEvents(evidence, EventCategory.BugCheck, EventCategory.UnexpectedShutdown, EventCategory.HardwareError, EventCategory.Kernel, EventCategory.Device)
                     .Where(x => x.Importance >= 1)
                     .Take(Analysis.MaxEventsPerKind * 2))
        {
            if (evidence.IsAround(logged.Timestamp) || logged.Category is EventCategory.BugCheck or EventCategory.UnexpectedShutdown)
            {
                evidence.Note(Subsystems.System, logged.Timestamp, $"\"{logged.Title}\"", logged.ToRef());
            }

            yield return Analysis.Logged(evidence, logged);
        }
    }
}

/// <summary>Anomalies the rules detected in the window, with their reasoning, and how an automatic incident was triggered.</summary>
public sealed class AnomalyAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "anomalies";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        foreach (var trigger in e.Triggers.Where(t => t.RuleId is not null))
        {
            yield return Analysis.Observation($"Captured automatically at {evidence.Clock(trigger.Timestamp)} by the rule \"{trigger.RuleId}\": {trigger.Detail}.",
                new EvidenceRef("trigger", trigger.Timestamp, trigger.RuleId));
        }

        // Event anomalies repeat Windows events and process changes that other analyzers report.
        foreach (var anomaly in e.Anomalies
                     .Where(a => !a.Series.Contains(':', StringComparison.Ordinal))
                     .OrderBy(a => a.StartedAt)
                     .Take(Analysis.MaxEventsPerKind * 2))
        {
            var reference = new EvidenceRef("anomaly", anomaly.StartedAt, anomaly.Id.ToString(CultureInfo.InvariantCulture));
            var end = anomaly.EndedAt ?? e.To;
            if (anomaly.StartedAt <= evidence.AroundTo && end >= evidence.AroundFrom)
            {
                evidence.Note(SubsystemOf(anomaly.Series), anomaly.StartedAt, anomaly.Reasoning.Classification.ToLowerInvariant(), reference);
            }

            var device = anomaly.Device is { Length: > 0 } d ? $" on {d}" : "";
            var r = anomaly.Reasoning;
            var baseline = r.Baseline is { } b ? $"normally {b}; " : "";
            var duration = r.Duration >= TimeSpan.FromSeconds(1) ? $" for {Core.Anomalies.AnomalyReasoning.FormatDuration(r.Duration)}" : "";
            yield return Analysis.Observation(
                $"{r.Classification}{device} detected from {evidence.Clock(anomaly.StartedAt)} ({evidence.Relative(anomaly.StartedAt)}): {baseline}current {r.Current}{duration}. Rule: {r.RuleDescription}.",
                reference);
        }
    }

    private static string SubsystemOf(string series) => series.Split('.')[0] switch
    {
        "gpu" => Subsystems.Gpu,
        "memory" => Subsystems.Memory,
        "disk" => Subsystems.Storage,
        "net" => Subsystems.Network,
        "cpu" when series.EndsWith("temp", StringComparison.Ordinal) => Subsystems.Thermal,
        _ => Subsystems.Cpu,
    };
}

/// <summary>
/// For an incident recorded with a custom selection: what was not recorded, so no statement about it can be made.
/// Statements of absence elsewhere are made only for data that was recorded.
/// </summary>
public sealed class RecordingSelectionAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    private static readonly (string Item, string Label)[] Fixed =
    [
        (RecordingItems.CpuUsage, "CPU utilization"),
        (RecordingItems.CpuCores, "per-core CPU figures"),
        (RecordingItems.CpuClock, "CPU frequency"),
        (RecordingItems.CpuScheduler, "CPU queue and interrupts"),
        (RecordingItems.CpuThermal, "CPU temperature and power"),
        (RecordingItems.MemoryUsage, "memory use"),
        (RecordingItems.MemoryPaging, "paging"),
        (RecordingItems.VolumeSpace, "free disk space"),
        (RecordingItems.ProcessMetrics, "per-process figures"),
        (RecordingItems.ProcessLifecycle, "process starts and exits"),
        (RecordingItems.Display, "the display configuration"),
        (RecordingItems.Services, "services"),
        (RecordingItems.Inventory, "the hardware and driver inventory"),
    ];

    /// <inheritdoc />
    public string Id => "selection";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Evidence.RecordedItems is not { } items)
        {
            yield break;
        }

        var missing = Fixed.Where(f => !items.Contains(f.Item)).Select(f => f.Label).ToList();
        foreach (var (prefix, label) in new[] { ("gpu:", "GPUs"), ("disk:", "disks"), ("net:", "network adapters"), ("sensor:", "hardware sensors") })
        {
            if (!items.Any(i => i.StartsWith(prefix, StringComparison.Ordinal)))
            {
                missing.Add(label);
            }
        }

        // Windows events by what was kept when that is the shorter list.
        var categories = Enum.GetValues<EventCategory>();
        var kept = categories.Where(c => items.Contains(RecordingItems.Events(c))).ToList();
        var left = categories.Except(kept).ToList();
        static string Lower(string label) => label.Length > 1 && char.IsLower(label[1]) ? char.ToLowerInvariant(label[0]) + label[1..] : label;
        string Words(IEnumerable<EventCategory> list) => string.Join(", ", list.Select(c => Lower(Recording.RecordingCatalogBuilder.EventLabel(c))));
        if (kept.Count == 0)
        {
            missing.Add("Windows events");
        }
        else if (left.Count > 0)
        {
            missing.Add(kept.Count <= left.Count ? $"Windows events other than {Words(kept)}" : $"Windows events about {Words(left)}");
        }

        yield return Analysis.Observation(missing.Count == 0
            ? $"This incident was recorded with a custom selection of {items.Count} items."
            : $"This incident was recorded with a custom selection of {items.Count} items; not recorded, so nothing is said about them: {string.Join("; ", missing)}.");
    }
}
