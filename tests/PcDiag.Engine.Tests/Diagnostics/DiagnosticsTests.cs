using PcDiag.Core.Anomalies;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Engine.Diagnostics;

namespace PcDiag.Engine.Tests.Diagnostics;

public sealed class LanguageGuardTests
{
    [Theory]
    [InlineData(EvidenceLevel.Observation, "The GPU caused the flicker.")]
    [InlineData(EvidenceLevel.Observation, "Memory ran out because of the browser.")]
    [InlineData(EvidenceLevel.Correlation, "The driver reset led to the crash, which coincided with the report.")]
    [InlineData(EvidenceLevel.PossibleExplanation, "This may be due to overheating.")]
    [InlineData(EvidenceLevel.Observation, "This proves the disk is failing.")]
    [InlineData(EvidenceLevel.ConfirmedEvent, "Windows logged a reset that resulted in the crash.")]
    public void Causal_wording_is_rejected(EvidenceLevel level, string text) =>
        LanguageGuard.Violation(new Finding(level, text, [])).ShouldNotBeNull().ShouldContain("causal wording");

    [Fact]
    public void Quoted_windows_text_may_say_anything()
    {
        var finding = new Finding(EvidenceLevel.ConfirmedEvent, "Windows logged \"The previous shutdown was caused by a power loss\" (Event 41, Kernel-Power) at 12:00:01.", []);

        LanguageGuard.Violation(finding).ShouldBeNull();
    }

    [Fact]
    public void Each_level_has_its_own_required_wording()
    {
        LanguageGuard.Violation(new Finding(EvidenceLevel.Correlation, "GPU activity and the flicker are linked.", [])).ShouldNotBeNull();
        LanguageGuard.Violation(new Finding(EvidenceLevel.Correlation, "GPU activity coincided with the reported flicker.", [])).ShouldBeNull();
        LanguageGuard.Violation(new Finding(EvidenceLevel.PossibleExplanation, "It is a driver problem.", [])).ShouldNotBeNull();
        LanguageGuard.Violation(new Finding(EvidenceLevel.PossibleExplanation, "It could be consistent with a driver problem.", [])).ShouldBeNull();
        LanguageGuard.Violation(new Finding(EvidenceLevel.NotProven, "The GPU caused the problem.", [])).ShouldNotBeNull();
        LanguageGuard.Violation(new Finding(EvidenceLevel.NotProven, "The collected telemetry does not establish that the GPU caused the problem.", [])).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => LanguageGuard.Check(new Finding(EvidenceLevel.Observation, "", [])));
    }
}

public sealed class DiagnosticScenarioTests
{
    private static readonly DateTimeOffset Center = new(2026, 10, 6, 12, 31, 0, TimeSpan.Zero);
    private static readonly IncidentKey Key = IncidentKey.Create(new DateOnly(2026, 10, 6), 7);

    private static MetricTrack Track(string seriesId, string metric, string unit, Func<int, double?> value, string? device = null, int fromSecond = -600, int toSecond = 120)
    {
        var points = new List<MetricPoint>();
        for (var s = fromSecond; s <= toSecond; s++)
        {
            if (value(s) is { } v)
            {
                points.Add(new MetricPoint(Center.AddSeconds(s), v));
            }
        }

        return new MetricTrack(seriesId, metric, $"{device} {metric}".Trim(), unit, device, points);
    }

    private static EvidenceEvent Windows(int second, string title, EventCategory category, int eventId, string provider, int importance = 3, long id = 1) =>
        new(Center.AddSeconds(second), "windows-event", title, importance, "WindowsEvents", id, category, provider, eventId);

    private static IncidentEvidence Evidence(UserIncidentType? type, IReadOnlyList<MetricTrack> metrics, IReadOnlyList<EvidenceEvent> events, IReadOnlyList<Anomaly>? anomalies = null) =>
        new(Key, Center, Center.AddMinutes(-10), Center.AddMinutes(2), IncidentSource.Hotkey, type, false, metrics, events, anomalies ?? [], []);

    private static IReadOnlyList<Finding> Analyze(IncidentEvidence evidence)
    {
        var rejected = new List<(Finding, string)>();
        var findings = IncidentDiagnostics.Analyze(evidence, AnomalyRuleCatalog.Default.Rules, TimeZoneInfo.Utc, rejected);
        rejected.ShouldBeEmpty("every analyzer must use allowed wording");
        findings.ShouldAllBe(f => LanguageGuard.Violation(f) == null);
        return findings;
    }

    private static string[] Texts(IEnumerable<Finding> findings, EvidenceLevel level) => findings.Where(f => f.Level == level).Select(f => f.Text).ToArray();

    /// <summary>The PROJECT §19 example: GPU busy, its clock collapses, a display event, the user reports a flicker.</summary>
    [Fact]
    public void A_screen_flicker_with_gpu_and_display_activity_reads_like_the_specification()
    {
        const string gpu = "NVIDIA GeForce RTX 4090";
        var metrics = new[]
        {
            Track("cpu", "cpu", "%", _ => 30),
            Track("memory.available", "memory.available", "B", _ => 9.1 * (1L << 30)),
            Track("memory", "memory", "%", _ => 71),
            Track("gpu.3.util", "gpu.util", "%", s => s is >= -10 and <= 2 ? 99 : 55 + (10 * Math.Sin(s)), gpu),
            Track("gpu.3.core", "gpu.core", "MHz", s => s is >= -3 and <= 2 ? 300 : 2050, gpu),
            Track("gpu.3.temp", "gpu.temp", "°C", _ => 64, gpu),
            Track("disk.5.readlat", "disk.readlat", "ms", _ => 0.4, "Samsung SSD 990 PRO"),
            Track("disk.5.writelat", "disk.writelat", "ms", _ => 1.1, "Samsung SSD 990 PRO"),
            Track("net.9.errors", "net.errors", "", _ => 0, "Ethernet"),
        };
        var events = new[]
        {
            Windows(-2, "Display driver nvlddmkm stopped responding and has successfully recovered", EventCategory.Display, 4101, "Display"),
            new EvidenceEvent(Center.AddSeconds(-25), "process", "obs64.exe started", 0, "ProcessEvents", 4),
        };

        var findings = Analyze(Evidence(UserIncidentType.ScreenFlicker, metrics, events));
        var observations = findings.Where(f => f.Level is EvidenceLevel.Observation or EvidenceLevel.ConfirmedEvent).Select(f => f.Text).ToList();

        // OBSERVATIONS: what happened, in time order, then what did not.
        observations.ShouldContain($"GPU utilization of {gpu} reached 99 % at 12:30:50 (about 10 s before the reported screen flicker). Earlier in the window it was typically 46–64 %.");
        observations.ShouldContain($"The core clock of {gpu} dropped from 2050 MHz to 300 MHz between 12:30:56 and 12:30:57 (about 3 s before the reported screen flicker). GPU utilization was 99 % at that moment.");
        observations.ShouldContain("Windows logged \"Display driver nvlddmkm stopped responding and has successfully recovered\" (Event 4101, Display) at 12:30:58, about 2 s before the reported screen flicker.");
        observations.ShouldContain("No significant RAM pressure was observed (available memory stayed at or above 9.1 GB throughout the window).");
        observations.ShouldContain("Disk latency remained below the configured limit of 200 ms (highest: 1.1 ms on Samsung SSD 990 PRO).");
        observations.ShouldContain("No network anomaly was detected: no packet errors and no network events were recorded in the incident window.");
        observations.ShouldContain("1 process start recorded in the 30 s before the reported screen flicker: obs64.exe.");
        observations.IndexOf(observations.First(o => o.StartsWith("GPU utilization", StringComparison.Ordinal)))
            .ShouldBeLessThan(observations.IndexOf(observations.First(o => o.StartsWith("No significant RAM", StringComparison.Ordinal))), "what was seen comes before what was not");

        // POSSIBLE CORRELATIONS.
        var correlation = Texts(findings, EvidenceLevel.Correlation).ShouldHaveSingleItem();
        correlation.ShouldStartWith("GPU and display activity coincided with the reported screen flicker: ");
        correlation.ShouldContain("\"Display driver nvlddmkm stopped responding and has successfully recovered\" (2 s before)");
        correlation.ShouldContain("GPU clock drop from 2050 MHz to 300 MHz (3 s before)");
        Texts(findings, EvidenceLevel.PossibleExplanation).ShouldHaveSingleItem()
            .ShouldBe("The GPU and display activity may be related to the reported screen flicker; it could be consistent with a display driver or GPU problem.");

        // NOT PROVEN, always last.
        Texts(findings, EvidenceLevel.NotProven).ShouldBe([
            "The collected telemetry does not establish that the GPU or the display driver caused the reported screen flicker.",
            "Timing alone does not establish a connection: events that occur close together can be unrelated.",
        ]);
        findings[^1].Level.ShouldBe(EvidenceLevel.NotProven);

        // Every positive finding links to its data.
        findings.Where(f => f.Level is EvidenceLevel.ConfirmedEvent or EvidenceLevel.Correlation).ShouldAllBe(f => f.EvidenceRefs.Count > 0);
        findings.Single(f => f.Text.StartsWith("The core clock", StringComparison.Ordinal)).EvidenceRefs.ShouldBe([new EvidenceRef("metric", Center.AddSeconds(-3), "gpu.3.core")]);
    }

    /// <summary>Seen on real data: an idle GPU lowering its clock is power saving, and identical adapters repeat statements.</summary>
    [Fact]
    public void An_idle_gpu_lowering_its_clock_is_not_reported_and_idle_gpus_share_one_statement()
    {
        var metrics = new[]
        {
            Track("gpu.3.util", "gpu.util", "%", _ => 1, "NVIDIA GeForce RTX 4090"),
            Track("gpu.3.core", "gpu.core", "MHz", s => s < -1 ? 2580 : 300, "NVIDIA GeForce RTX 4090"),
            Track("gpu.4.util", "gpu.util", "%", _ => 1, "NVIDIA GeForce RTX 4090"),
            Track("gpu.4.core", "gpu.core", "MHz", s => s < -1 ? 2580 : 300, "NVIDIA GeForce RTX 4090"),
            Track("gpu.5.util", "gpu.util", "%", _ => 0, "Intel(R) Graphics"),
        };

        var findings = Analyze(Evidence(UserIncidentType.ScreenFlicker, metrics, []));

        Texts(findings, EvidenceLevel.Observation).ShouldNotContain(t => t.Contains("dropped", StringComparison.Ordinal));
        Texts(findings, EvidenceLevel.Observation).Count(t => t.StartsWith("No sudden drop of the GPU core clock under load", StringComparison.Ordinal)).ShouldBe(1);
        Texts(findings, EvidenceLevel.Observation).ShouldContain(
            "GPU utilization stayed low around the reported screen flicker (NVIDIA GeForce RTX 4090 up to 1 %; Intel(R) Graphics up to 0 %).");
        Texts(findings, EvidenceLevel.Correlation).ShouldBeEmpty();
    }

    /// <summary>Seen on real data: a GPU that peaked at 84 % was described as having "stayed low".</summary>
    [Fact]
    public void A_busy_but_not_saturated_gpu_is_reported_as_its_peak_not_as_low()
    {
        var metrics = new[]
        {
            Track("gpu.3.util", "gpu.util", "%", s => s == -4 ? 84 : 1, "NVIDIA GeForce RTX 4090"),
            Track("gpu.5.util", "gpu.util", "%", _ => 0, "Intel(R) Graphics"),
        };

        var findings = Analyze(Evidence(UserIncidentType.ScreenFlicker, metrics, []));

        var observations = Texts(findings, EvidenceLevel.Observation);
        observations.ShouldContain(t => t.StartsWith("GPU utilization of NVIDIA GeForce RTX 4090 peaked at 84 %", StringComparison.Ordinal));
        observations.ShouldContain("GPU utilization stayed low around the reported screen flicker (Intel(R) Graphics up to 0 %).");
        observations.ShouldNotContain(t => t.Contains("stayed low", StringComparison.Ordinal) && t.Contains("RTX 4090", StringComparison.Ordinal));
        Texts(findings, EvidenceLevel.Correlation).ShouldBeEmpty(); // a peak short of saturation is not notable on its own
    }

    [Fact]
    public void A_quiet_incident_says_that_nothing_stood_out()
    {
        var metrics = new[]
        {
            Track("cpu", "cpu", "%", _ => 12),
            Track("memory.available", "memory.available", "B", _ => 20.0 * (1L << 30)),
            Track("cpu.temp", "cpu.temp", "°C", _ => 55),
        };

        var findings = Analyze(Evidence(UserIncidentType.Slowdown, metrics, []));

        Texts(findings, EvidenceLevel.Correlation).ShouldBeEmpty();
        Texts(findings, EvidenceLevel.PossibleExplanation).ShouldBeEmpty();
        Texts(findings, EvidenceLevel.Observation).ShouldContain("CPU utilization stayed at or below 12 % around the reported slowdown.");
        Texts(findings, EvidenceLevel.Observation).ShouldContain("Temperatures stayed below the configured limits (CPU package temperature up to 55 °C).");
        Texts(findings, EvidenceLevel.NotProven).ShouldHaveSingleItem()
            .ShouldBe("The collected telemetry does not establish what caused the reported slowdown; no measured activity stood out around that moment.");
    }

    /// <summary>
    /// A custom recording that kept only CPU usage and network error counts: nothing is claimed about the display,
    /// network events or crashes, which were never recorded, and the analysis says what was left out.
    /// </summary>
    [Fact]
    public void A_custom_recording_claims_no_absence_of_what_it_did_not_record()
    {
        var metrics = new[]
        {
            Track("cpu", "cpu", "%", _ => 12),
            Track("net.5.errors", "net.errors", "", _ => 0, device: "Ethernet 3"),
        };
        var items = new HashSet<string>(StringComparer.Ordinal) { RecordingItems.CpuUsage, RecordingItems.Network("net-1", "errors") };
        var custom = Evidence(UserIncidentType.Slowdown, metrics, []) with { RecordedItems = items };

        var observations = Texts(Analyze(custom), EvidenceLevel.Observation);
        var everything = Texts(Analyze(Evidence(UserIncidentType.Slowdown, metrics, [])), EvidenceLevel.Observation);

        everything.ShouldContain(t => t.StartsWith("No network anomaly", StringComparison.Ordinal));
        everything.ShouldContain(t => t.StartsWith("No display configuration change", StringComparison.Ordinal));
        everything.ShouldContain("No application crash or hang was recorded in the incident window.");
        everything.ShouldNotContain(t => t.Contains("custom selection", StringComparison.Ordinal));

        observations.ShouldNotContain(t => t.StartsWith("No network anomaly", StringComparison.Ordinal));
        observations.ShouldNotContain(t => t.StartsWith("No display configuration change", StringComparison.Ordinal));
        observations.ShouldNotContain(t => t.StartsWith("No application crash", StringComparison.Ordinal));
        observations.ShouldContain("CPU utilization stayed at or below 12 % around the reported slowdown.");
        var selection = observations.Single(t => t.Contains("custom selection", StringComparison.Ordinal));
        selection.ShouldStartWith("This incident was recorded with a custom selection of 2 items; not recorded, so nothing is said about them: per-core CPU figures;");
        selection.ShouldContain("GPUs; disks");
        selection.ShouldNotContain("network adapters");
        selection.ShouldContain("Windows events");

        // When most event categories are left out, the few that were kept are named instead.
        var display = Evidence(UserIncidentType.Slowdown, metrics, []) with
        {
            RecordedItems = new HashSet<string>(items, StringComparer.Ordinal) { RecordingItems.Events(EventCategory.Display), RecordingItems.Events(EventCategory.Gpu) },
        };
        Texts(Analyze(display), EvidenceLevel.Observation).Single(t => t.Contains("custom selection", StringComparison.Ordinal))
            .ShouldContain("Windows events other than display, GPU and graphics driver.");
    }

    [Fact]
    public void A_freeze_with_memory_pressure_and_slow_storage_hedges_both()
    {
        var metrics = new[]
        {
            Track("memory.available", "memory.available", "B", s => s > -40 ? 0.4 * (1L << 30) : 6.0 * (1L << 30)),
            Track("memory.commit", "memory.commit", "%", s => s > -40 ? 97 : 60),
            Track("disk.1.writelat", "disk.writelat", "ms", s => s is > -20 and < 5 ? 850 : 3, "WDC WD40EFRX"),
        };
        var events = new[] { Windows(-15, "Windows successfully diagnosed a low virtual memory condition", EventCategory.ResourceExhaustion, 2004, "Resource-Exhaustion-Detector") };

        var findings = Analyze(Evidence(UserIncidentType.SystemFreeze, metrics, events));

        Texts(findings, EvidenceLevel.Observation).ShouldContain(t => t.StartsWith("Available memory fell to 410 MB", StringComparison.Ordinal));
        Texts(findings, EvidenceLevel.Observation).ShouldContain(t => t.StartsWith("The write latency of WDC WD40EFRX reached 850 ms", StringComparison.Ordinal));
        Texts(findings, EvidenceLevel.ConfirmedEvent).ShouldHaveSingleItem().ShouldContain("(Event 2004, Resource-Exhaustion-Detector)");
        Texts(findings, EvidenceLevel.PossibleExplanation).ShouldBe([
            "The memory activity may be related to the reported system freeze; it could be consistent with the system running short of memory.",
            "The storage activity may be related to the reported system freeze; it could be consistent with slow or failing storage.",
        ]);
        Texts(findings, EvidenceLevel.NotProven).ShouldContain("The collected telemetry does not establish that the memory activity caused the reported system freeze.");
    }

    [Fact]
    public void Anomalies_are_reported_with_their_reasoning()
    {
        var anomaly = new Anomaly(12, "gpu-baseline", "gpu.util", Center.AddSeconds(-18), Center.AddSeconds(5), 99, Severity.Low,
            new AnomalyReasoning("GPU utilization anomaly", "GPU utilization more than 3 σ above its 30 min baseline for at least 15 s", "40–70 % (mean 55, sd 8, 30 min)", 55, 8,
                "99 % (z = 5.5)", TimeSpan.FromSeconds(18)), "NVIDIA GeForce RTX 4090");
        var eventAnomaly = anomaly with { Id = 13, RuleId = "gpu-reset", Series = "event:gpu.tdr" };

        var findings = Analyze(Evidence(null, [], [], [anomaly, eventAnomaly]));

        var finding = findings.Single(f => f.Level == EvidenceLevel.Observation && f.EvidenceRefs.Any(r => r.Kind == "anomaly"));
        finding.Text.ShouldBe(
            "GPU utilization anomaly on NVIDIA GeForce RTX 4090 detected from 12:30:42 (about 18 s before the capture): normally 40–70 % (mean 55, sd 8, 30 min); current 99 % (z = 5.5) for 18 s. Rule: GPU utilization more than 3 σ above its 30 min baseline for at least 15 s.");
        finding.EvidenceRefs.ShouldBe([new EvidenceRef("anomaly", Center.AddSeconds(-18), "12")]);
        Texts(findings, EvidenceLevel.Correlation).ShouldHaveSingleItem().ShouldStartWith("GPU and display activity coincided with the capture");
        Texts(findings, EvidenceLevel.PossibleExplanation).ShouldBeEmpty("an automatic or unlabelled capture has nothing to explain");
    }

    [Fact]
    public void A_crash_logged_by_windows_is_a_confirmed_event_and_never_a_cause()
    {
        var events = new[]
        {
            Windows(-1, "Application Error: game.exe (exception 0xc0000005)", EventCategory.ApplicationCrash, 1000, "Application Error"),
            new EvidenceEvent(Center.AddSeconds(-1), "process", "game.exe crashed", 3, "ProcessEvents", 9),
            Windows(-30, "Display driver nvlddmkm stopped responding and has successfully recovered", EventCategory.Display, 4101, "Display", id: 2),
        };

        var findings = Analyze(Evidence(UserIncidentType.GameCrash, [], events));

        Texts(findings, EvidenceLevel.ConfirmedEvent).Length.ShouldBe(2);
        Texts(findings, EvidenceLevel.Observation).ShouldContain("game.exe crashed at 12:30:59 (about 1 s before the reported game crash).");
        Texts(findings, EvidenceLevel.PossibleExplanation).ShouldContain(
            "The application activity may be related to the reported game crash; it could be consistent with a problem in that application.");
        Texts(findings, EvidenceLevel.NotProven).ShouldContain("The collected telemetry does not establish that the GPU or the display driver caused the reported game crash.");
    }
}
