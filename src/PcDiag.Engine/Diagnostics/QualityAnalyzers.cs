using PcDiag.Core.Abstractions;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Quality;
using PcDiag.Engine.Quality;

namespace PcDiag.Engine.Diagnostics;

/// <summary>Turns the quality pages' findings into incident findings.</summary>
internal static class QualityFindings
{
    /// <summary>A possible explanation when it says "may" or "could", an observation otherwise.</summary>
    public static Finding ToFinding(QualityFinding finding, params EvidenceRef[] refs) =>
        finding.Text.Contains(" may ", StringComparison.Ordinal) || finding.Text.Contains(" could ", StringComparison.Ordinal)
            ? new Finding(EvidenceLevel.PossibleExplanation, finding.Text, refs)
            : new Finding(EvidenceLevel.Observation, finding.Text, refs);

    /// <summary>The subsystem a finding's kind belongs to, for the correlation step.</summary>
    public static string? Subsystem(string kind) => kind switch
    {
        "gpu" => Subsystems.Gpu,
        "cpu" => Subsystems.Cpu,
        "thermal" => Subsystems.Thermal,
        "memory" => Subsystems.Memory,
        "storage" => Subsystems.Storage,
        "driver" => Subsystems.System,
        "program" => Subsystems.Application,
        "home-network" or "provider" or "internet" or "dns" or "bufferbloat" => Subsystems.Network,
        _ => null,
    };
}

/// <summary>
/// The game's frame rate around the incident: the drop or hitch nearest to its moment, and what else was measured while
/// it lasted (see <see cref="FrameDrops"/>).
/// </summary>
public sealed class FrameRateAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "frames";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var tracks = evidence.Evidence.Metrics;
        var seconds = FrameDrops.Seconds(tracks);
        if (seconds.Count == 0 || evidence.Evidence.Track("game.fps") is not { } fps)
        {
            yield break;
        }

        var around = seconds.Where(s => evidence.IsAround(s.At)).ToList();
        if (around.Count == 0)
        {
            yield break;
        }

        var drops = FrameDrops.Find(around);
        if (drops.Count == 0)
        {
            yield return Analysis.Observation(
                $"The game ran at {Analysis.N(around.Min(s => s.Fps))}–{Analysis.N(around.Max(s => s.Fps))} fps around {evidence.Incident}, without falling {Analysis.N(FrameDrops.DropPercent)} % below its usual frame rate and without a frame of {Analysis.N(FrameDrops.HitchMs)} ms or more.",
                evidence.Range(fps));
            yield break;
        }

        // The game is the device of the frame rate rules' anomalies (its process name); it is left out of "other programs".
        var game = evidence.Evidence.Anomalies.FirstOrDefault(a => a.RuleId is "game-fps-drop" or "game-stutter" && a.Device is not null)?.Device;
        var (from, to) = drops.MinBy(d => Math.Abs((d.From - evidence.Center).TotalSeconds))!;
        var drop = FrameDrops.Explain(from, to, seconds, tracks, evidence.Evidence.Processes, game, evidence.Evidence.DriverLatency, evidence.Clock);
        var reference = AnalysisContext.Metric(fps, from);
        var usual = drop.UsualFps is { } u ? $" against its usual {Analysis.N(u)} fps" : "";
        var worst = drop.WorstMs is { } w && w >= FrameDrops.HitchMs ? $", with a frame of {Analysis.N(w)} ms" : "";
        var span = to - from >= TimeSpan.FromSeconds(1) ? $"from {evidence.Clock(from)} to {evidence.Clock(to)}" : $"at {evidence.Clock(from)}";
        yield return Analysis.Observation(
            $"{game ?? "The game"} ran at {Analysis.N(drop.LowestFps)} fps{usual} {span} ({evidence.Relative(from)}){worst}.", reference);
        foreach (var finding in drop.Findings)
        {
            if (QualityFindings.Subsystem(finding.Kind) is { } subsystem && finding.Severity >= 2)
            {
                evidence.Note(subsystem, from, finding.Text.Split(':')[0], reference);
            }

            yield return QualityFindings.ToFinding(finding, reference);
        }
    }
}

/// <summary>Drivers' DPC and interrupt time around the incident, and the driver latency measurements in its window.</summary>
public sealed class DriverLatencyAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "driver-latency";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        foreach (var (metric, what) in new[] { ("latency.dpc", "DPCs"), ("latency.isr", "interrupts") })
        {
            if (evidence.Evidence.Track(metric) is { } track && track.Between(evidence.AroundFrom, evidence.AroundTo).ToList() is { Count: > 0 } points
                && points.MaxBy(p => p.Value) is var peak && peak.Value >= LatencyDiagnosis.BusyPercent)
            {
                var reference = AnalysisContext.Metric(track, peak.Timestamp);
                evidence.Note(Subsystems.System, peak.Timestamp, $"drivers held a processor {Analysis.N(peak.Value)} % in {what}", reference);
                yield return Analysis.Observation(
                    $"Drivers held one processor {Analysis.N(peak.Value)} % of the time in {what} at {evidence.Clock(peak.Timestamp)} ({evidence.Relative(peak.Timestamp)}).", reference);
            }
        }

        if (evidence.Evidence.DriverLatency is { Count: > 0 } measurements)
        {
            foreach (var finding in LatencyDiagnosis.Analyze([], measurements, evidence.Clock))
            {
                yield return QualityFindings.ToFinding(finding, new EvidenceRef("range", evidence.Evidence.From, "latency.dpc"));
            }
        }
    }
}

/// <summary>
/// The connection around the incident: the pings to the router, the provider's first hop and the internet, and the
/// DNS checks, compared hop by hop (see <see cref="ConnectionDiagnosis"/>).
/// </summary>
public sealed class ConnectionAnalyzer : IDiagnosticAnalyzer<AnalysisContext>
{
    /// <inheritdoc />
    public string Id => "connection";

    /// <inheritdoc />
    public IEnumerable<Finding> Analyze(AnalysisContext evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var e = evidence.Evidence;
        if (e.Track("conn.internet") is null && e.Track("conn.loss") is null)
        {
            yield break;
        }

        // The minute around the incident as one minute of the quality history.
        var from = evidence.AroundFrom;
        var to = evidence.AroundTo;
        HopMinute Hop(string ms, string loss) => new(Average(e.Track(ms), from, to), Max(e.Track(ms), from, to), Average(e.Track(loss), from, to));
        var internet = e.Track("conn.internet")?.Between(from, to).Select(p => p.Value).ToList() ?? [];
        double? jitter = internet.Count >= 2 ? internet.Zip(internet.Skip(1), (a, b) => Math.Abs(b - a)).Average() : null;
        var dnsFailed = (int)(e.Track("conn.dns_failed")?.Between(from, to).Sum(p => p.Value) ?? 0);
        var dnsChecks = e.Track("conn.dns_failed")?.Between(from, to).Count() ?? 0;
        var seconds = (int)Math.Max(1, (to - from).TotalSeconds);
        var minute = new QualityMinute(from, seconds, null, 0, null, null, null, 0, null, 0, null, null, null,
            Hop("conn.router", "conn.router_loss"), Hop("conn.provider", "conn.provider_loss"), Hop("conn.internet", "conn.loss"), jitter,
            Average(e.Track("conn.dns"), from, to), dnsChecks, dnsFailed, null, null);
        var programs = e.Processes?.Where(p => p.NetDuring > 0).Select(p => new NetworkProgram(p.Name, p.NetDuring, 0, p.NetDuring, 0)).ToList();
        var report = ConnectionDiagnosis.Analyze([minute], programs, evidence.Clock);
        var reference = new EvidenceRef("range", from, "conn.internet");
        foreach (var finding in report.Findings)
        {
            if (finding.Severity >= 2)
            {
                evidence.Note(Subsystems.Network, evidence.Center, finding.Text.Split(':')[0], reference);
            }

            yield return QualityFindings.ToFinding(finding, reference);
        }
    }

    private static double? Average(MetricTrack? track, DateTimeOffset from, DateTimeOffset to) =>
        track?.Between(from, to).Select(p => p.Value).DefaultIfEmpty(double.NaN).Average() is { } v && !double.IsNaN(v) ? Math.Round(v, 2) : null;

    private static double? Max(MetricTrack? track, DateTimeOffset from, DateTimeOffset to) =>
        track?.Between(from, to).Select(p => p.Value).DefaultIfEmpty(double.NaN).Max() is { } v && !double.IsNaN(v) ? v : null;
}
