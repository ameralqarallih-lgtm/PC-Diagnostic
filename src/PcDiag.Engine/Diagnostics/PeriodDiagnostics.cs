using System.Globalization;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;

namespace PcDiag.Engine.Diagnostics;

/// <summary>
/// The period diagnostic: everything recorded in a period, aspect by aspect (recording coverage, CPU, memory, GPUs,
/// storage, network, processes, services, display, Windows events, hardware sensors, anomalies and incidents), with
/// summary figures, tables and findings ranked by severity. Thresholds are the user's anomaly rules (Settings →
/// Triggers); further checks cover what the rules do not (memory growth, frequent restarts, invalid signatures, free
/// space, sensor temperatures). Findings state what was measured or logged; their wording passes the
/// <see cref="LanguageGuard"/> like incident findings, and one that does not is left out.
/// </summary>
public static class PeriodDiagnostics
{
    /// <summary>Most findings listed in the summary.</summary>
    public const int TopFindings = 12;

    private const double Gib = 1024d * 1024 * 1024;

    private static readonly (string Suffix, string Word)[] Directions = [("rx", "download"), ("tx", "upload")];

    /// <summary>Analyzes a period.</summary>
    public static PeriodReport Analyze(PeriodEvidence evidence, IReadOnlyList<AnomalyRule> rules, TimeZoneInfo zone, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(rules);
        var p = new PeriodContext(evidence, rules, zone);
        var sections = new List<PeriodSection>
        {
            Coverage(p), Incidents(p), Cpu(p), Memory(p), Gpu(p), Storage(p), Network(p), Processes(p), Services(p), Display(p), WindowsEvents(p), Sensors(p), Anomalies(p),
        }.Select(Checked).ToList();

        // The anomaly recap repeats what the other sections cover, so it is not counted; incidents below High are listed
        // in their own section only.
        var all = sections.Where(s => s.Id != "anomalies").SelectMany(s => s.Findings.Select(f => f with { Section = s.Id })).ToList();
        var bySeverity = Enum.GetValues<Severity>().Reverse().ToDictionary(s => s.ToString(), s => all.Count(f => f.Severity == s));
        var top = all.Where(f => f.Severity >= Severity.Low && (f.Section != "incidents" || f.Severity >= Severity.High)).OrderByDescending(f => f.Severity).ThenByDescending(f => f.Occurrences).ThenBy(f => f.From).Take(TopFindings).ToList();
        var recorded = evidence.Sessions.Sum(s => (s.End - s.Start).TotalMinutes);
        string? note = evidence.RawFrom is { } rawFrom && evidence.Sessions.Any(x => x.Start < rawFrom - TimeSpan.FromMinutes(1))
            ? $"Process, sensor and free-space details cover {p.Clock(rawFrom)} to {p.Clock(evidence.To)} (raw data is kept for a day); before that only one-minute summaries of the values remain."
            : evidence.RawFrom is null && evidence.Series.Count > 0
                ? "Only one-minute summaries of the values remain for this period; process, sensor and free-space details are kept for a day."
                : null;
        return new PeriodReport(evidence.From, evidence.To, generatedAt, new PeriodSummary(Math.Round((evidence.To - evidence.From).TotalMinutes, 1), Math.Round(recorded, 1), bySeverity, top, note), sections);
    }

    /// <summary>Leaves out findings whose wording the language guard rejects; most severe first.</summary>
    private static PeriodSection Checked(PeriodSection section) => section with
    {
        Findings = section.Findings
            .Where(f => LanguageGuard.Violation(new Finding(f.Level, f.Text, [])) is null)
            .OrderByDescending(f => f.Severity).ThenByDescending(f => f.Occurrences).ThenBy(f => f.From)
            .ToList(),
    };

    private static PeriodSection Coverage(PeriodContext p)
    {
        var e = p.Evidence;
        var period = e.To - e.From;
        var recorded = TimeSpan.FromMinutes(e.Sessions.Sum(s => (s.End - s.Start).TotalMinutes));
        var stats = new List<PeriodStat>
        {
            new("Period", $"{p.Clock(e.From)} – {p.Clock(e.To)} ({PeriodContext.Duration(period)})"),
            new("Recorded", $"{PeriodContext.Duration(recorded)} ({Analysis.N(period.TotalSeconds > 0 ? 100 * recorded.TotalSeconds / period.TotalSeconds : 0)} %)"),
            new("Recording sessions", Analysis.N(e.Sessions.Count)),
        };
        if (e.Series.Count > 0)
        {
            stats.Add(new("Detail", $"values analyzed in steps of {PeriodContext.Duration(TimeSpan.FromMilliseconds(e.Series.Max(x => x.BucketMs)))} (minimum, average and maximum of each)"));
        }

        var findings = new List<PeriodFinding>();
        if (e.Sessions.Count == 0)
        {
            findings.Add(new(Severity.Info, EvidenceLevel.Observation, "Nothing recorded", "The recorder was not recording in this period, so there is nothing to analyze."));
            return new("coverage", "Recording", stats, findings, []);
        }

        // Gaps between sessions (and at the ends) of a minute or more.
        var gaps = new List<(DateTimeOffset From, DateTimeOffset To)>();
        var cursor = e.From;
        foreach (var s in e.Sessions.OrderBy(s => s.Start))
        {
            if (s.Start - cursor >= TimeSpan.FromMinutes(1))
            {
                gaps.Add((cursor, s.Start));
            }

            cursor = s.End > cursor ? s.End : cursor;
        }

        if (e.To - cursor >= TimeSpan.FromMinutes(1))
        {
            gaps.Add((cursor, e.To));
        }

        if (gaps.Count > 0)
        {
            var longest = gaps.MaxBy(g => g.To - g.From);
            findings.Add(new(Severity.Info, EvidenceLevel.Observation, "Not recorded throughout",
                $"Not recorded for {PeriodContext.Duration(TimeSpan.FromSeconds(gaps.Sum(g => (g.To - g.From).TotalSeconds)))} in total ({Count(gaps.Count, "gap")}), the longest from {p.Clock(longest.From)} to {p.Clock(longest.To)}. Nothing is known about those times.",
                longest.From, longest.To, Occurrences: gaps.Count));
        }

        if (e.Sessions.Count(s => s.Custom) is var custom and > 0)
        {
            stats.Add(new("Custom recordings", Analysis.N(custom)));
            findings.Add(new(Severity.Info, EvidenceLevel.Observation, "Custom recording",
                $"{Count(custom, "recording session")} in this period recorded a custom selection; data they left out was not stored, so nothing is said about it for those times.",
                Occurrences: custom));
        }

        // Windows stopping uncleanly is listed one by one; the recorder stopping (Windows kept running) once, with how often.
        stats.Add(new("Unclean stops", Analysis.N(e.UncleanStops.Count)));
        foreach (var stop in e.UncleanStops.Where(x => x.Kind is "BugCheck" or "UnexpectedShutdown"))
        {
            findings.Add(new(stop.Kind == "BugCheck" ? Severity.Critical : Severity.High, EvidenceLevel.ConfirmedEvent, Words(stop.Kind),
                $"{stop.Summary} Detected at {p.Clock(stop.DetectedAt)}.", stop.LastHeartbeat, stop.DetectedAt));
        }

        foreach (var kind in e.UncleanStops.Where(x => x.Kind is not ("BugCheck" or "UnexpectedShutdown")).GroupBy(x => x.Kind))
        {
            var list = kind.OrderBy(x => x.DetectedAt).ToList();
            findings.Add(new(list.Any(x => x.WasRecording) ? Severity.Low : Severity.Info, EvidenceLevel.ConfirmedEvent, Words(kind.Key),
                list.Count == 1
                    ? $"{list[0].Summary} Detected at {p.Clock(list[0].DetectedAt)}."
                    : $"{Count(list.Count, "unclean stop")} of this kind, the last detected at {p.Clock(list[^1].DetectedAt)}: {list[^1].Summary}",
                list[0].LastHeartbeat, list[^1].DetectedAt, Occurrences: list.Count));
        }

        var tables = new List<PeriodTable>();
        if (e.UncleanStops.Count > 0)
        {
            tables.Add(new("Unclean stops", ["Last heartbeat", "Detected", "Kind", "Recording"], e.UncleanStops.OrderBy(x => x.DetectedAt)
                .Select(x => (IReadOnlyList<string>)[p.Clock(x.LastHeartbeat), p.Clock(x.DetectedAt), Words(x.Kind), x.WasRecording ? "yes" : "no"]).ToList()));
        }

        return new("coverage", "Recording", stats, findings, tables);
    }

    private static PeriodSection Cpu(PeriodContext p)
    {
        var stats = new List<PeriodStat>();
        var findings = new List<PeriodFinding>();
        if (p.Series("cpu") is { } cpu)
        {
            var s = PeriodContext.Stats(cpu);
            stats.Add(new("Utilization", $"average {Analysis.N(s.Average)} %, 95th percentile {Analysis.N(s.P95)} %, highest {Analysis.N(s.Max)} % at {p.Clock(s.MaxAt)}", cpu.Id));
            var high = p.Rule("cpu-high");
            findings.AddRange(p.Above(cpu, high.Threshold, high.DurationSeconds, high.Severity, "High CPU load", $"CPU at or above {Analysis.N(high.Threshold)} %", "%"));
            stats.Add(new($"Time at or above {Analysis.N(high.Threshold)} %", PeriodContext.Duration(PeriodContext.TimeAbove(cpu, high.Threshold)), cpu.Id));
        }

        if (p.Series("cpu.temp") is { } temp)
        {
            var s = PeriodContext.Stats(temp);
            stats.Add(new("Package temperature", $"average {Analysis.N(s.Average)} °C, highest {Analysis.N(s.Max)} °C at {p.Clock(s.MaxAt)}", temp.Id));
            var rule = p.Rule("cpu-temp-high");
            var hot = p.Above(temp, rule.Threshold, rule.DurationSeconds, rule.Severity, "High CPU temperature", $"CPU package temperature at or above {Analysis.N(rule.Threshold)} °C", "°C").ToList();
            findings.AddRange(hot);
            if (hot.Count == 0)
            {
                findings.AddRange(p.Above(temp, rule.Threshold - 10, 300, Severity.Low, "CPU temperature close to its limit",
                    $"CPU package temperature within 10 °C of the {Analysis.N(rule.Threshold)} °C limit", "°C"));
            }
        }

        if (p.Series("cpu.power") is { } power)
        {
            var s = PeriodContext.Stats(power);
            stats.Add(new("Package power", $"average {Analysis.N(s.Average)} W, highest {Analysis.N(s.Max)} W", power.Id));
        }

        if (p.Series("cpu.freq") is { } freq)
        {
            var s = PeriodContext.Stats(freq);
            stats.Add(new("Frequency", $"average {AnalysisContext.Format(s.Average, "MHz")}, lowest {AnalysisContext.Format(s.Min, "MHz")}", freq.Id));
        }

        if (p.Series("cpu.limit") is { } limit)
        {
            var s = PeriodContext.Stats(limit);
            stats.Add(new("Performance limit", $"lowest {Analysis.N(s.Min)} % at {p.Clock(s.MinAt)}", limit.Id));
            findings.AddRange(p.Below(limit, 95, 60, Severity.Low, "CPU performance limited",
                "Windows reported the CPU's performance limited (thermal, power or platform limits) below 95 %", "%"));
        }

        p.AddPeak(stats, "cpu", "Busiest processes at the peak", v => $"{Analysis.N(v, "0.#")} %");
        var tables = new List<PeriodTable>();
        if (p.TopCpu(10) is { Count: > 0 } top)
        {
            tables.Add(new("Most CPU time", ["Process", "PID", "CPU time", "Share of the CPU", "Highest"], top));
        }

        return new("cpu", "CPU", stats, findings, tables);
    }

    private static PeriodSection Memory(PeriodContext p)
    {
        var e = p.Evidence;
        var stats = new List<PeriodStat>();
        var findings = new List<PeriodFinding>();
        if (p.Series("memory") is { } used)
        {
            var s = PeriodContext.Stats(used);
            stats.Add(new("In use", $"average {Analysis.N(s.Average)} %, highest {Analysis.N(s.Max)} % at {p.Clock(s.MaxAt)}", used.Id));
            findings.AddRange(p.Above(used, 90, 60, Severity.Medium, "Memory nearly full", "Memory in use at or above 90 %", "%"));
        }

        if (p.Series("memory.available") is { } available)
        {
            var s = PeriodContext.Stats(available);
            // The exact moment from the raw samples when known (a bucket's time is its start).
            var lowAt = e.Peaks.FirstOrDefault(x => x.SeriesId == "memory.available")?.At ?? s.MinAt;
            stats.Add(new("Available", $"lowest {AnalysisContext.FormatBytes(s.Min)} at {p.Clock(lowAt)}", available.Id));
            var rule = p.Rule("memory-low");
            findings.AddRange(p.Below(available, rule.Threshold * Gib, rule.DurationSeconds, rule.Severity, "Low available memory",
                $"Available memory below {Analysis.N(rule.Threshold, "0.#")} GB", "B"));
        }

        if (p.Series("memory.commit") is { } commit)
        {
            var s = PeriodContext.Stats(commit);
            stats.Add(new("Commit", $"highest {Analysis.N(s.Max)} % of the limit at {p.Clock(s.MaxAt)}", commit.Id));
            var rule = p.Rule("memory-commit-high");
            findings.AddRange(p.Above(commit, rule.Threshold, rule.DurationSeconds, rule.Severity, "Commit near its limit",
                $"Committed memory at or above {Analysis.N(rule.Threshold)} % of the commit limit", "%"));
        }

        if (p.Series("memory.hardfaults") is { } faults)
        {
            var s = PeriodContext.Stats(faults);
            stats.Add(new("Hard page faults", $"95th percentile {Analysis.N(s.P95)}/s, highest {Analysis.N(s.Max)}/s", faults.Id));
            findings.AddRange(p.Above(faults, 1000, 60, Severity.Low, "Heavy paging", "Hard page faults above 1,000/s (memory read back from disk)", "/s"));
        }

        if (p.Series("memory.compressed") is { } compressed)
        {
            stats.Add(new("Compressed", $"highest {AnalysisContext.FormatBytes(PeriodContext.Stats(compressed).Max)}", compressed.Id));
        }

        p.AddPeak(stats, "memory.available", "Most memory at the low point", v => AnalysisContext.FormatBytes(v));

        // Processes whose private memory grew a lot while they ran through the period.
        var rawSpan = e.RawFrom is { } rawFrom ? e.To - rawFrom : TimeSpan.Zero;
        var growing = e.Processes
            .Where(x => x.FirstPrivateBytes is > 0 && x.LastPrivateBytes is { } last && x.LastSample - x.FirstSample >= rawSpan / 2 && x.LastSample - x.FirstSample >= TimeSpan.FromMinutes(30))
            .Select(x => (Process: x, Growth: (double)(x.LastPrivateBytes!.Value - x.FirstPrivateBytes!.Value)))
            .Where(x => x.Growth >= Gib || (x.Growth >= 300 * 1024 * 1024 && x.Growth >= x.Process.FirstPrivateBytes!.Value))
            .OrderByDescending(x => x.Growth)
            .ToList();
        foreach (var (x, _) in growing.Take(5))
        {
            var grew = $"Private memory of {x.Name} (PID {x.Pid}) grew from {AnalysisContext.FormatBytes(x.FirstPrivateBytes!.Value)} to {AnalysisContext.FormatBytes(x.LastPrivateBytes!.Value)} between {p.Clock(x.FirstSample)} and {p.Clock(x.LastSample)}.";
            findings.Add(VirtualMachineHost(x.Name)
                ? new(Severity.Low, EvidenceLevel.PossibleExplanation, $"{x.Name} memory grew",
                    $"{grew} {x.Name} holds the memory of a virtual machine (WSL, Hyper-V, Windows Sandbox), so its growth may be what runs inside the machine using more memory.",
                    x.FirstSample, x.LastSample)
                : new(Severity.Medium, EvidenceLevel.PossibleExplanation, $"{x.Name} memory grew", $"{grew} Growth that keeps going may be a memory leak.", x.FirstSample, x.LastSample));
        }

        foreach (var x in e.Processes.Where(x => x.FirstHandles is { } a && x.LastHandles is { } b && b - a >= 10_000).OrderByDescending(x => x.LastHandles - x.FirstHandles).Take(3))
        {
            findings.Add(new(Severity.Low, EvidenceLevel.PossibleExplanation, $"{x.Name} handles grew",
                $"Open handles of {x.Name} (PID {x.Pid}) grew from {Analysis.N(x.FirstHandles!.Value)} to {Analysis.N(x.LastHandles!.Value)} between {p.Clock(x.FirstSample)} and {p.Clock(x.LastSample)}; a count that keeps rising may be a handle leak.",
                x.FirstSample, x.LastSample));
        }

        var tables = new List<PeriodTable>();
        var byMemory = e.Processes.Where(x => x.MaxPrivateBytes is > 0).OrderByDescending(x => x.MaxPrivateBytes).Take(10)
            .Select(x => (IReadOnlyList<string>)[x.Name, Analysis.N(x.Pid), AnalysisContext.FormatBytes(x.MaxPrivateBytes!.Value), x.LastPrivateBytes is { } l ? AnalysisContext.FormatBytes(l) : "—"])
            .ToList();
        if (byMemory.Count > 0)
        {
            tables.Add(new("Most memory (private)", ["Process", "PID", "Highest", "At its last sample"], byMemory));
        }

        if (growing.Count > 0)
        {
            tables.Add(new("Growing memory", ["Process", "PID", "From", "To", "Growth"], growing.Take(10).Select(x => (IReadOnlyList<string>)
                [x.Process.Name, Analysis.N(x.Process.Pid), AnalysisContext.FormatBytes(x.Process.FirstPrivateBytes!.Value), AnalysisContext.FormatBytes(x.Process.LastPrivateBytes!.Value), "+" + AnalysisContext.FormatBytes(x.Growth)]).ToList()));
        }

        return new("memory", "Memory", stats, findings, tables);
    }

    private static PeriodSection Gpu(PeriodContext p)
    {
        var stats = new List<PeriodStat>();
        var findings = new List<PeriodFinding>();
        foreach (var device in p.Devices("gpu."))
        {
            var parts = new List<string>();
            if (p.DeviceSeries(device, "gpu.", "util") is { } util)
            {
                var s = PeriodContext.Stats(util);
                parts.Add($"utilization average {Analysis.N(s.Average)} %, highest {Analysis.N(s.Max)} %");
                var busy = p.Rule("gpu-high");
                findings.AddRange(p.Above(util, busy.Threshold, busy.DurationSeconds, Severity.Info, $"{device} fully busy", $"{device} at or above {Analysis.N(busy.Threshold)} % utilization", "%"));
            }

            if (p.DeviceSeries(device, "gpu.", "temp") is { } temp)
            {
                var s = PeriodContext.Stats(temp);
                parts.Add($"temperature highest {Analysis.N(s.Max)} °C");
                var rule = p.Rule("gpu-temp-high");
                findings.AddRange(p.Above(temp, rule.Threshold, rule.DurationSeconds, rule.Severity, $"{device} hot", $"{device} temperature at or above {Analysis.N(rule.Threshold)} °C", "°C"));
            }

            if (p.DeviceSeries(device, "gpu.", "hotspot") is { } hotspot)
            {
                var s = PeriodContext.Stats(hotspot);
                parts.Add($"hotspot highest {Analysis.N(s.Max)} °C");
                var rule = p.Rule("gpu-hotspot-high");
                findings.AddRange(p.Above(hotspot, rule.Threshold, rule.DurationSeconds, rule.Severity, $"{device} hotspot hot", $"{device} hotspot at or above {Analysis.N(rule.Threshold)} °C", "°C"));
            }

            if (p.DeviceSeries(device, "gpu.", "vram") is { } vram)
            {
                parts.Add($"VRAM highest {AnalysisContext.FormatBytes(PeriodContext.Stats(vram).Max)}");
            }

            if (p.DeviceSeries(device, "gpu.", "power") is { } power)
            {
                parts.Add($"power highest {Analysis.N(PeriodContext.Stats(power).Max)} W");
            }

            if (parts.Count > 0)
            {
                stats.Add(new(device, string.Join("; ", parts), p.DeviceSeries(device, "gpu.", "util")?.Id));
            }
        }

        if (p.Series("vram") is { } fullest)
        {
            var rule = p.Rule("gpu-vram-full");
            findings.AddRange(p.Above(fullest, rule.Threshold, rule.DurationSeconds, rule.Severity, "VRAM full", $"GPU memory at or above {Analysis.N(rule.Threshold)} % of its size", "%"));
        }

        var drops = p.Evidence.Anomalies.Where(a => a.RuleId == "gpu-clock-drop").ToList();
        foreach (var device in drops.GroupBy(a => a.Device ?? "the GPU"))
        {
            var list = device.OrderBy(a => a.StartedAt).ToList();
            findings.Add(new(Severity.Medium, EvidenceLevel.Observation, $"{device.Key} clock drops under load",
                $"The core clock of {device.Key} dropped sharply while it was busy {Count(list.Count, "time")}, the first at {p.Clock(PeriodContext.Time(list[0].StartedAt))}, the last at {p.Clock(PeriodContext.Time(list[^1].StartedAt))}.",
                PeriodContext.Time(list[0].StartedAt), PeriodContext.Time(list[^1].StartedAt), Occurrences: list.Count));
        }

        p.AddPeak(stats, "gpu", "Busiest processes at the GPU peak", v => $"{Analysis.N(v, "0.#")} %");
        var tables = new List<PeriodTable>();
        var gpuUsers = p.Evidence.Processes.Where(x => x.MaxGpuPercent is > 1).OrderByDescending(x => x.MaxGpuPercent).Take(10)
            .Select(x => (IReadOnlyList<string>)[x.Name, Analysis.N(x.Pid), $"{Analysis.N(x.MaxGpuPercent!.Value)} %"]).ToList();
        if (gpuUsers.Count > 0)
        {
            tables.Add(new("GPU users", ["Process", "PID", "Highest GPU"], gpuUsers));
        }

        return new("gpu", "Graphics (GPU)", stats, findings, tables);
    }

    private static PeriodSection Storage(PeriodContext p)
    {
        var stats = new List<PeriodStat>();
        var findings = new List<PeriodFinding>();
        var latency = p.Rule("disk-latency-high");
        var queue = p.Rule("disk-queue-high");
        foreach (var device in p.Devices("disk."))
        {
            var parts = new List<string>();
            if (p.DeviceSeries(device, "disk.", "active") is { } active)
            {
                var s = PeriodContext.Stats(active);
                parts.Add($"active average {Analysis.N(s.Average)} %, 95th percentile {Analysis.N(s.P95)} %");
            }

            foreach (var (suffix, word) in new[] { ("readlat", "read"), ("writelat", "write") })
            {
                if (p.DeviceSeries(device, "disk.", suffix) is { } lat)
                {
                    var s = PeriodContext.Stats(lat);
                    parts.Add($"{word} latency 95th percentile {Analysis.N(s.P95, "0.#")} ms, highest {Analysis.N(s.Max, "0.#")} ms");
                    findings.AddRange(p.Above(lat, latency.Threshold, latency.DurationSeconds, latency.Severity, $"{device} slow {word}s",
                        $"{Capital(word)} latency of {device} at or above {Analysis.N(latency.Threshold)} ms", "ms"));
                }
            }

            if (p.DeviceSeries(device, "disk.", "queue") is { } q)
            {
                parts.Add($"queue highest {Analysis.N(PeriodContext.Stats(q).Max, "0.#")}");
                findings.AddRange(p.Above(q, queue.Threshold, queue.DurationSeconds, queue.Severity, $"{device} queue long", $"Queue length of {device} at or above {Analysis.N(queue.Threshold)}", ""));
            }

            if (p.DeviceSeries(device, "disk.", "temp") is { } temp)
            {
                var s = PeriodContext.Stats(temp);
                parts.Add($"temperature highest {Analysis.N(s.Max)} °C");
                findings.AddRange(p.Above(temp, 70, 60, s.Max >= 80 ? Severity.High : Severity.Medium, $"{device} hot", $"{device} temperature at or above 70 °C", "°C"));
            }

            if (parts.Count > 0)
            {
                stats.Add(new(device, string.Join("; ", parts), p.DeviceSeries(device, "disk.", "active")?.Id));
            }
        }

        foreach (var v in p.Evidence.Volumes.Where(v => v.TotalBytes is > 0 && v.MinFreeBytes is not null))
        {
            var share = 100.0 * v.MinFreeBytes!.Value / v.TotalBytes!.Value;
            var name = v.Label is { Length: > 0 } label ? $"{v.Mount} ({label})" : v.Mount;
            stats.Add(new($"Free space on {name}", $"lowest {AnalysisContext.FormatBytes(v.MinFreeBytes.Value)} of {AnalysisContext.FormatBytes(v.TotalBytes.Value)} ({Analysis.N(share, "0.#")} %)"));
            if (share < 10)
            {
                findings.Add(new(share < 5 || v.MinFreeBytes < 5 * Gib ? Severity.High : Severity.Medium, EvidenceLevel.Observation, $"{v.Mount} nearly full",
                    $"Free space on {name} fell to {AnalysisContext.FormatBytes(v.MinFreeBytes.Value)} ({Analysis.N(share, "0.#")} %) at {p.Clock(v.MinFreeAt!.Value)}.", v.MinFreeAt));
            }
        }

        p.AddPeak(stats, "disk", "Most I/O at the busiest disk moment", v => AnalysisContext.Format(v, "B/s"));
        findings.AddRange(p.LoggedGroups([EventCategory.Disk, EventCategory.FileSystem], Severity.High));
        return new("storage", "Storage", stats, findings, []);
    }

    private static PeriodSection Network(PeriodContext p)
    {
        var stats = new List<PeriodStat>();
        var findings = new List<PeriodFinding>();
        foreach (var device in p.Devices("net."))
        {
            // Adapters that carried next to nothing (virtual switches, disconnected ports) are left out.
            var traffic = Directions.Select(x => p.DeviceSeries(device, "net.", x.Suffix)).OfType<PeriodSeries>().Select(x => PeriodContext.Stats(x).Max).DefaultIfEmpty(0).Max();
            if (traffic < 10 * 1024 && !p.Evidence.NetworkErrors.Any(x => x.SeriesId == p.DeviceSeries(device, "net.", "errors")?.Id))
            {
                continue;
            }

            var parts = new List<string>();
            foreach (var (suffix, word) in Directions)
            {
                if (p.DeviceSeries(device, "net.", suffix) is { } series)
                {
                    var s = PeriodContext.Stats(series);
                    parts.Add($"{word} average {AnalysisContext.Format(s.Average, "B/s")}, highest {AnalysisContext.Format(s.Max, "B/s")}");
                }
            }

            if (p.DeviceSeries(device, "net.", "errors") is { } errors && p.Evidence.NetworkErrors.FirstOrDefault(x => x.SeriesId == errors.Id) is { } counted)
            {
                var rule = p.Rule("network-errors");
                parts.Add($"packet errors {Analysis.N(counted.Total)}");
                findings.Add(new(counted.Total >= 1000 || counted.Most >= rule.Threshold ? Severity.Medium : Severity.Low, EvidenceLevel.Observation, $"{device} packet errors",
                    $"{device} counted {Count((int)Math.Min(int.MaxValue, counted.Total), "packet error")} in the period, the most in one sample {Analysis.N(counted.Most)} at {p.Clock(counted.MostAt)}.",
                    counted.MostAt, SeriesIds: [errors.Id], Occurrences: (int)Math.Min(int.MaxValue, counted.Total)));
            }

            if (p.DeviceSeries(device, "net.", "wifi") is { } wifi)
            {
                parts.Add($"Wi-Fi signal lowest {Analysis.N(PeriodContext.Stats(wifi).Min)} %");
                findings.AddRange(p.Below(wifi, 30, 30, Severity.Low, $"{device} weak Wi-Fi", $"Wi-Fi signal of {device} below 30 %", "%"));
            }

            if (parts.Count > 0)
            {
                stats.Add(new(device, string.Join("; ", parts), p.DeviceSeries(device, "net.", "rx")?.Id));
            }
        }

        findings.AddRange(p.LoggedGroups([EventCategory.Network], Severity.Medium));
        return new("network", "Network", stats, findings, []);
    }

    private static PeriodSection Processes(PeriodContext p)
    {
        var e = p.Evidence;
        var findings = new List<PeriodFinding>();
        var stats = new List<PeriodStat>
        {
            new("Processes seen", $"{Analysis.N(e.Processes.Count)} ({Analysis.N(e.Processes.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count())} programs)"),
            new("Started / exited", $"{Analysis.N(e.ProcessNames.Sum(x => x.Started))} / {Analysis.N(e.ProcessNames.Sum(x => x.Stopped))}"),
            new("Stopped responding", $"{Analysis.N(e.ProcessNames.Sum(x => x.Hung))} seen by the recorder, {Analysis.N(p.Logged(EventCategory.ApplicationHang).Count())} logged by Windows"),
            new("Crashed (logged by Windows)", Analysis.N(p.Logged(EventCategory.ApplicationCrash).Count())),
        };

        findings.AddRange(p.LoggedGroups([EventCategory.ApplicationCrash], Severity.High));
        findings.AddRange(p.LoggedGroups([EventCategory.ApplicationHang], Severity.Medium));
        findings.AddRange(p.LoggedGroups([EventCategory.WindowsErrorReporting, EventCategory.Process], Severity.Low));

        foreach (var x in e.ProcessNames.Where(x => x.Hung > 0).OrderByDescending(x => x.Hung))
        {
            findings.Add(new(x.Hung >= 3 ? Severity.Medium : Severity.Low, EvidenceLevel.Observation, $"{x.Name} stopped responding",
                $"{x.Name} stopped responding {Count(x.Hung, "time")}{(x.Recovered > 0 ? $" and responded again {Count(x.Recovered, "time")}" : "")}.", Occurrences: x.Hung));
        }

        var hours = Math.Max(1, (e.To - e.From).TotalHours);
        foreach (var x in e.ProcessNames.Where(x => x.Started >= Math.Max(20, 10 * hours)).OrderByDescending(x => x.Started).Take(5))
        {
            findings.Add(new(Severity.Low, EvidenceLevel.PossibleExplanation, $"{x.Name} started often",
                $"{x.Name} started {Count(x.Started, "time")} (about {Analysis.N(x.Started / hours)} an hour). A program started this often may be relaunched in a loop by another one, or keep exiting.",
                Occurrences: x.Started));
        }

        foreach (var x in e.Processes.Where(x => x.Signature == "NotTrusted").GroupBy(x => x.Path ?? x.Name, StringComparer.OrdinalIgnoreCase))
        {
            var first = x.OrderBy(y => y.FirstSample).First();
            findings.Add(new(Severity.Medium, EvidenceLevel.PossibleExplanation, $"{first.Name}: signature not trusted",
                $"{first.Name} ran although Windows does not trust its signature{(first.Signer is { } s ? $" (signed as {s})" : "")}. The file may have been changed after it was signed, or signed by a publisher this PC does not trust ({first.Path ?? "path unknown"}).",
                first.FirstSample, Occurrences: x.Count()));
        }

        var tables = new List<PeriodTable>();
        if (p.TopCpu(10) is { Count: > 0 } top)
        {
            tables.Add(new("Most CPU time", ["Process", "PID", "CPU time", "Share of the CPU", "Highest"], top));
        }

        var started = e.ProcessNames.Where(x => x.Started > 0).OrderByDescending(x => x.Started).Take(10)
            .Select(x => (IReadOnlyList<string>)[x.Name, Analysis.N(x.Started), Analysis.N(x.Stopped), Analysis.N(x.Hung)]).ToList();
        if (started.Count > 0)
        {
            tables.Add(new("Most started programs", ["Program", "Started", "Exited", "Stopped responding"], started));
        }

        return new("processes", "Processes and applications", stats, findings, tables);
    }

    private static PeriodSection Services(PeriodContext p)
    {
        var e = p.Evidence;
        var findings = new List<PeriodFinding>();
        var stats = new List<PeriodStat>
        {
            new("Services that changed", Analysis.N(e.Services.Count)),
            new("Failures", Analysis.N(e.Services.Sum(s => s.Failed))),
            new("Restarts", Analysis.N(e.Services.Sum(s => s.Restarted))),
        };
        foreach (var s in e.Services.Where(s => s.Failed > 0).OrderByDescending(s => s.Failed))
        {
            findings.Add(new(s.Failed >= 3 ? Severity.High : Severity.Medium, EvidenceLevel.ConfirmedEvent, $"{s.DisplayName} failed",
                $"The service {s.DisplayName} ({s.Name}) failed {Count(s.Failed, "time")}{(s.LastFailure is { } at ? $", the last at {p.Clock(at)}" : "")}.", s.LastFailure, Occurrences: s.Failed));
        }

        foreach (var s in e.Services.Where(s => s.Restarted >= 5).OrderByDescending(s => s.Restarted).Take(8))
        {
            findings.Add(new(s.Restarted >= 50 ? Severity.Low : Severity.Info, EvidenceLevel.Observation, $"{s.DisplayName} restarted often",
                $"The service {s.DisplayName} ({s.Name}) stopped and started again {Count(s.Restarted, "time")}. Windows starts some services on demand and stops them when idle, so frequent restarts are not necessarily a problem.",
                Occurrences: s.Restarted));
        }

        foreach (var s in e.Services.Where(s => s.StartTypeChanged > 0))
        {
            findings.Add(new(Severity.Info, EvidenceLevel.Observation, $"{s.DisplayName} start type changed",
                $"The start type of {s.DisplayName} ({s.Name}) changed {Count(s.StartTypeChanged, "time")}.", Occurrences: s.StartTypeChanged));
        }

        // A service error Windows logged is counted above as a failure of the service it names; the events are listed
        // as logged only when some did not name one.
        if (p.Logged(EventCategory.Service).Count() > e.Services.Sum(s => s.Failed))
        {
            findings.AddRange(p.LoggedGroups([EventCategory.Service], Severity.Medium));
        }

        var tables = new List<PeriodTable>();
        var changed = e.Services.OrderByDescending(s => s.Started + s.Stopped + s.Restarted + s.Failed).Take(10)
            .Select(s => (IReadOnlyList<string>)[s.DisplayName, Analysis.N(s.Started), Analysis.N(s.Stopped), Analysis.N(s.Restarted), Analysis.N(s.Failed)]).ToList();
        if (changed.Count > 0)
        {
            tables.Add(new("Most changed services", ["Service", "Started", "Stopped", "Restarted", "Failed"], changed));
        }

        return new("services", "Services", stats, findings, tables);
    }

    private static PeriodSection Display(PeriodContext p)
    {
        var changes = p.Evidence.Events.Where(x => x.Kind == "display").ToList();
        var findings = new List<PeriodFinding>();
        findings.AddRange(p.LoggedGroups([EventCategory.Display, EventCategory.Gpu], Severity.High));
        if (changes.Count > 0)
        {
            findings.Add(new(Severity.Info, EvidenceLevel.Observation, "Display changes",
                $"{Count(changes.Count, "display change")} (modes, refresh rates, connections, power), the first at {p.Clock(changes[0].Timestamp)}: {changes[0].Title}.",
                changes[0].Timestamp, changes[^1].Timestamp, Occurrences: changes.Count));
        }

        var stats = new List<PeriodStat>
        {
            new("Display changes", Analysis.N(changes.Count)),
            new("Display and graphics driver events", Analysis.N(p.Logged(EventCategory.Display, EventCategory.Gpu).Count())),
        };
        return new("display", "Display and graphics driver", stats, findings, []);
    }

    private static PeriodSection WindowsEvents(PeriodContext p)
    {
        var findings = new List<PeriodFinding>();
        findings.AddRange(p.LoggedGroups([EventCategory.BugCheck], Severity.Critical));
        findings.AddRange(p.LoggedGroups([EventCategory.UnexpectedShutdown, EventCategory.HardwareError, EventCategory.ResourceExhaustion], Severity.High));
        findings.AddRange(p.LoggedGroups([EventCategory.Kernel], Severity.Medium));
        findings.AddRange(p.LoggedGroups([EventCategory.Device, EventCategory.Other], Severity.Low));
        findings.AddRange(p.LoggedGroups([EventCategory.Power, EventCategory.TimeChange], Severity.Info));
        var stats = p.Evidence.Events.Where(x => x.Kind == "windows-event" && x.Category is not null)
            .GroupBy(x => x.Category!.Value)
            .OrderByDescending(g => g.Count())
            .Select(g => new PeriodStat(Recording.RecordingCatalogBuilder.EventLabel(g.Key), Analysis.N(g.Count())))
            .Prepend(new PeriodStat("Problems logged by Windows", Analysis.N(p.Evidence.Events.Count(x => x.Kind == "windows-event"))))
            .ToList();
        return new("system", "Windows and system events", stats, findings, []);
    }

    private static PeriodSection Sensors(PeriodContext p)
    {
        var temps = p.Evidence.Sensors.Where(s => s.Kind == "Temperature" && Measured(s.Name)).OrderByDescending(s => s.Max).ToList();
        var findings = new List<PeriodFinding>();
        foreach (var s in temps.Where(s => s.Max >= 95).Take(5))
        {
            var reached = $"{s.Name} reached {Analysis.N(s.Max)} °C at {p.Clock(s.MaxAt)} (average {Analysis.N(s.Average)} °C).";
            findings.Add(UnlabeledBoardSensor(s.Name)
                ? new(Severity.Low, EvidenceLevel.PossibleExplanation, $"{s.Name} hot",
                    $"{reached} It is an unlabeled motherboard sensor input; such inputs may be unconnected and read wrong values, so check the board's own tool before trusting it.", s.MaxAt)
                : new(s.Max >= 100 ? Severity.High : Severity.Medium, EvidenceLevel.Observation, $"{s.Name} hot", reached, s.MaxAt));
        }

        var stats = new List<PeriodStat> { new("Sensors recorded", Analysis.N(p.Evidence.Sensors.Count)) };
        var fans = p.Evidence.Sensors.Where(s => s.Kind == "Fan").ToList();
        if (fans.Count > 0)
        {
            stats.Add(new("Fans", string.Join("; ", fans.Take(6).Select(f => $"{f.Name} {Analysis.N(f.Min)}–{Analysis.N(f.Max)} RPM"))));
        }

        var tables = new List<PeriodTable>();
        if (temps.Count > 0)
        {
            tables.Add(new("Hottest sensors", ["Sensor", "Highest", "Average", "At"], temps.Take(15)
                .Select(s => (IReadOnlyList<string>)[s.Name, $"{Analysis.N(s.Max)} °C", $"{Analysis.N(s.Average)} °C", p.Clock(s.MaxAt)]).ToList()));
        }

        return new("sensors", "Hardware sensors", stats, findings, tables);
    }

    private static PeriodSection Incidents(PeriodContext p)
    {
        var findings = p.Evidence.Incidents
            .Select(incident => new PeriodFinding(incident.Severity, EvidenceLevel.Observation, $"Incident {incident.Key}",
                $"Incident {incident.Key} at {p.Clock(incident.CenterAt)}: {incident.Title} (captured by {Source(incident.Source)}).", incident.CenterAt, IncidentKey: incident.Key))
            .ToList();
        return new("incidents", "Incidents", [new("Incidents", Analysis.N(findings.Count))], findings, []);
    }

    /// <summary>What the anomaly rules detected while recording (the other sections cover the same aspects over the whole period).</summary>
    private static PeriodSection Anomalies(PeriodContext p)
    {
        var e = p.Evidence;
        var findings = new List<PeriodFinding>();

        foreach (var rule in e.Anomalies.Where(a => a.RuleId != "gpu-clock-drop").GroupBy(a => (a.RuleId, a.Device)))
        {
            var list = rule.OrderBy(a => a.StartedAt).ToList();
            var first = list[0];
            findings.Add(new(list.Max(a => a.Severity), EvidenceLevel.Observation, first.Classification + (first.Device is { } d ? $" ({d})" : ""),
                $"{first.Classification}{(first.Device is { } device ? $" on {device}" : "")} detected {Count(list.Count, "time")}, the first at {p.Clock(PeriodContext.Time(first.StartedAt))}: {first.Current}. Rule: {first.Rule}",
                PeriodContext.Time(first.StartedAt), PeriodContext.Time(list[^1].EndedAt ?? list[^1].StartedAt), Occurrences: list.Count));
        }

        var stats = new List<PeriodStat> { new("Anomalies", Analysis.N(e.Anomalies.Count)) };
        var tables = new List<PeriodTable>();
        if (e.Anomalies.Count > 0)
        {
            tables.Add(new("Anomalies by rule", ["Anomaly", "Times", "First", "Last"], e.Anomalies.GroupBy(a => a.Classification).OrderByDescending(g => g.Count())
                .Select(g => (IReadOnlyList<string>)[g.Key, Analysis.N(g.Count()), p.Clock(PeriodContext.Time(g.Min(a => a.StartedAt))), p.Clock(PeriodContext.Time(g.Max(a => a.StartedAt)))])
                .ToList()));
        }

        return new("anomalies", "Anomalies detected while recording", stats, findings, tables);
    }

    private static string Source(string source) => source switch
    {
        "Hotkey" => "the hotkey",
        "Dashboard" => "the dashboard",
        "Tray" => "the tray",
        "Rule" => "an anomaly rule",
        "Event" => "a Windows event rule",
        "BootRecovery" => "the restart check",
        _ => Words(source).ToLowerInvariant(),
    };

    /// <summary>Processes that hold a virtual machine's memory.</summary>
    private static bool VirtualMachineHost(string name) =>
        name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase) || name.Equals("vmwp.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>A measured temperature, not a fixed limit (e.g. "Thermal Sensor Critical High Limit", "Warning Temperature") or a distance to one.</summary>
    private static bool Measured(string name) =>
        !(name.Contains("Limit", StringComparison.OrdinalIgnoreCase) || name.Contains("TjMax", StringComparison.OrdinalIgnoreCase)
          || name.Contains("Resolution", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Critical Temperature", StringComparison.OrdinalIgnoreCase)
          || name.EndsWith("Warning Temperature", StringComparison.OrdinalIgnoreCase));

    /// <summary>A generic input of a motherboard monitoring chip ("Nuvoton NCT6701D / Temperature #6").</summary>
    private static bool UnlabeledBoardSensor(string name) =>
        name.Contains("/ Temperature #", StringComparison.Ordinal)
        && (name.StartsWith("Nuvoton", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ITE", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Fintek", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Winbond", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SMSC", StringComparison.OrdinalIgnoreCase));

    private static string Count(int count, string noun) => $"{count.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(count == 1 ? "" : "s")}";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>"UnexpectedShutdown" → "Unexpected shutdown".</summary>
    private static string Words(string name) =>
        Capital(string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString())));
}

/// <summary>Series lookups, statistics, episodes and wording shared by the period analyzers.</summary>
internal sealed class PeriodContext(PeriodEvidence evidence, IReadOnlyList<AnomalyRule> rules, TimeZoneInfo zone)
{
    private static readonly AnomalyRule Missing = new() { Id = "missing", Name = "", Classification = "", Kind = AnomalyRuleKind.Threshold, Threshold = double.MaxValue };

    public PeriodEvidence Evidence { get; } = evidence;

    public PeriodSeries? Series(string id) => Evidence.Series.FirstOrDefault(s => s.Id == id);

    /// <summary>Device names that have series with this prefix (e.g. "gpu.").</summary>
    public IEnumerable<string> Devices(string prefix) =>
        Evidence.Series.Where(s => s.Id.StartsWith(prefix, StringComparison.Ordinal) && s.Device is not null).Select(s => s.Device!).Distinct(StringComparer.Ordinal);

    public PeriodSeries? DeviceSeries(string device, string prefix, string suffix) =>
        Evidence.Series.FirstOrDefault(s => s.Device == device && s.Id.StartsWith(prefix, StringComparison.Ordinal) && s.Id.EndsWith("." + suffix, StringComparison.Ordinal));

    public AnomalyRule Rule(string id) => rules.FirstOrDefault(r => r.Id == id) ?? AnomalyRuleCatalog.Default.Find(id) ?? Missing;

    public static DateTimeOffset Time(double unixMs) => DateTimeOffset.FromUnixTimeMilliseconds((long)unixMs);

    public string Clock(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return Evidence.To - Evidence.From > TimeSpan.FromHours(20)
            ? local.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string Duration(TimeSpan span) => span.TotalSeconds switch
    {
        <= 0 => "none",
        < 90 => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(span.TotalSeconds)):0} s"),
        < 5400 => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(span.TotalMinutes):0} min"),
        < 172_800 => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(span.TotalHours):0} h {span.Minutes:0} min"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(span.TotalDays):0} d {span.Hours:0} h"),
    };

    public sealed record SeriesStats(double Average, double P95, double Max, DateTimeOffset MaxAt, double Min, DateTimeOffset MinAt);

    /// <summary>Average and 95th percentile of the bucket averages; the extremes of the bucket minimums and maximums.</summary>
    public static SeriesStats Stats(PeriodSeries series)
    {
        var averages = series.Points.Select(b => b[2]).Order().ToList();
        var max = series.Points.MaxBy(b => b[3])!;
        var min = series.Points.MinBy(b => b[1])!;
        return new(averages.Average(), Analysis.Percentile(averages, 95), max[3], Time(max[0]), min[1], Time(min[0]));
    }

    public static TimeSpan TimeAbove(PeriodSeries series, double threshold) =>
        TimeSpan.FromMilliseconds(series.Points.Count(b => b[2] >= threshold) * (double)series.BucketMs);

    /// <summary>Times the series stayed at or above a threshold (a bucket counts when its average is), with the highest value.</summary>
    public IEnumerable<PeriodFinding> Above(PeriodSeries series, double threshold, double minimumSeconds, Severity severity, string title, string condition, string unit) =>
        Episodes(series, b => b[2] >= threshold, b => b[3], true, minimumSeconds, severity, title, condition, unit);

    /// <summary>Times the series stayed below a threshold (a bucket counts when its average is), with the lowest value.</summary>
    public IEnumerable<PeriodFinding> Below(PeriodSeries series, double threshold, double minimumSeconds, Severity severity, string title, string condition, string unit) =>
        Episodes(series, b => b[2] < threshold, b => b[1], false, minimumSeconds, severity, title, condition, unit);

    /// <summary>
    /// Runs of buckets in the condition (adjacent buckets join), lasting at least <paramref name="minimumSeconds"/>
    /// (a bucket counts for its width): one finding with the total time, how often, the longest run and the extreme.
    /// </summary>
    private IEnumerable<PeriodFinding> Episodes(
        PeriodSeries series, Func<double[], bool> inCondition, Func<double[], double> value, bool highest, double minimumSeconds, Severity severity, string title, string condition, string unit)
    {
        var runs = new List<(double From, double To, double[] Peak)>();
        (double From, double To, double[] Peak)? run = null;
        foreach (var b in series.Points)
        {
            if (!inCondition(b))
            {
                if (run is { } done)
                {
                    runs.Add(done);
                    run = null;
                }

                continue;
            }

            if (run is { } current && b[0] - current.To <= series.BucketMs * 1.5)
            {
                var peak = highest ? (value(b) > value(current.Peak) ? b : current.Peak) : (value(b) < value(current.Peak) ? b : current.Peak);
                run = (current.From, b[0] + series.BucketMs, peak);
            }
            else
            {
                if (run is { } previous)
                {
                    runs.Add(previous);
                }

                run = (b[0], b[0] + series.BucketMs, b);
            }
        }

        if (run is { } last)
        {
            runs.Add(last);
        }

        runs = runs.Where(r => r.To - r.From >= minimumSeconds * 1000).ToList();
        if (runs.Count == 0)
        {
            yield break;
        }

        var total = TimeSpan.FromMilliseconds(runs.Sum(r => r.To - r.From));
        var longest = runs.MaxBy(r => r.To - r.From);
        var extreme = highest ? runs.MaxBy(r => value(r.Peak)).Peak : runs.MinBy(r => value(r.Peak)).Peak;
        var times = runs.Count == 1 ? "once" : $"{runs.Count} times";
        yield return new PeriodFinding(severity, EvidenceLevel.Observation, title,
            $"{condition} for {Duration(total)} in total ({times}), the longest {Duration(TimeSpan.FromMilliseconds(longest.To - longest.From))} from {Clock(Time(longest.From))} to {Clock(Time(longest.To))}; {(highest ? "highest" : "lowest")} {AnalysisContext.Format(value(extreme), unit)} at {Clock(Time(extreme[0]))}.",
            Time(longest.From), Time(longest.To), [series.Id], runs.Count);
    }

    /// <summary>Windows events of these categories.</summary>
    public IEnumerable<EvidenceEvent> Logged(params EventCategory[] categories) =>
        Evidence.Events.Where(x => x.Kind == "windows-event" && x.Category is { } c && categories.Contains(c));

    /// <summary>Windows events of these categories grouped by title: one finding per kind of event, with how often and when.</summary>
    public IEnumerable<PeriodFinding> LoggedGroups(EventCategory[] categories, Severity severity)
    {
        foreach (var group in Logged(categories).GroupBy(x => x.Title, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            var list = group.OrderBy(x => x.Timestamp).ToList();
            var source = AnalysisContext.EventSource(list[0]);
            var text = list.Count == 1
                ? $"Windows logged \"{group.Key}\" ({source}) at {Clock(list[0].Timestamp)}."
                : $"Windows logged \"{group.Key}\" ({source}) {list.Count.ToString("N0", CultureInfo.InvariantCulture)} times, the first at {Clock(list[0].Timestamp)}, the last at {Clock(list[^1].Timestamp)}.";
            yield return new PeriodFinding(severity, EvidenceLevel.ConfirmedEvent, group.Key, text, list[0].Timestamp, list[^1].Timestamp, Occurrences: list.Count);
        }
    }

    /// <summary>Adds "label (time): process value, …" for a peak moment, when there is one with processes.</summary>
    public void AddPeak(List<PeriodStat> stats, string seriesId, string label, Func<double, string> value)
    {
        if (Evidence.Peaks.FirstOrDefault(x => x.SeriesId == seriesId) is { Processes.Count: > 0 } peak)
        {
            stats.Add(new($"{label} ({Clock(peak.At)})", string.Join(", ", peak.Processes.Select(x => $"{x.Name} {value(x.Value)}")), seriesId));
        }
    }

    /// <summary>The processes that used the most CPU time, as table rows.</summary>
    public List<IReadOnlyList<string>> TopCpu(int count)
    {
        var span = Evidence.RawFrom is { } rawFrom ? (Evidence.To - rawFrom).TotalMilliseconds : 0;
        var capacity = span * Math.Max(1, Evidence.LogicalProcessors);
        return Evidence.Processes.Where(x => x.CpuTimeMs is > 0 && x.Pid != 0)
            .OrderByDescending(x => x.CpuTimeMs)
            .Take(count)
            .Select(x => (IReadOnlyList<string>)
            [
                x.Name,
                Analysis.N(x.Pid),
                Duration(TimeSpan.FromMilliseconds(x.CpuTimeMs!.Value)),
                capacity > 0 ? $"{Analysis.N(100 * x.CpuTimeMs.Value / capacity, "0.##")} %" : "—",
                x.MaxCpuPercent is { } m ? $"{Analysis.N(m, "0.#")} %" : "—",
            ])
            .ToList();
    }
}
