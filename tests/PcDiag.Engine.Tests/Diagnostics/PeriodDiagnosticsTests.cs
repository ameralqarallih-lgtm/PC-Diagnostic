using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Engine.Diagnostics;

namespace PcDiag.Engine.Tests.Diagnostics;

public sealed class PeriodDiagnosticsTests
{
    private const double Gib = 1024d * 1024 * 1024;

    private static readonly DateTimeOffset From = new(2026, 10, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddHours(2);
    private static readonly IReadOnlyList<AnomalyRule> Rules = AnomalyRuleCatalog.Default.Apply(new TriggerOptions());

    [Fact]
    public void A_period_without_recording_says_there_is_nothing_to_analyze()
    {
        var report = Analyze(Evidence() with { Sessions = [] });

        var coverage = report.Sections.Single(s => s.Id == "coverage");
        coverage.Findings.ShouldHaveSingleItem().Title.ShouldBe("Nothing recorded");
        report.Summary.RecordedMinutes.ShouldBe(0);
        report.Summary.Top.ShouldBeEmpty();
    }

    [Fact]
    public void Sustained_values_over_a_rule_threshold_are_episodes_with_total_time_longest_run_and_peak()
    {
        // CPU at 99 % on average for 3 minutes (minute buckets) from 10:30, with a one-bucket spike at 11:00 whose
        // average stays under the threshold (a peak, not a sustained load).
        var cpu = Series("cpu", "%", 60_000, i => i is >= 30 and < 33 ? (98, 99, 100) : i == 60 ? (5, 40, 100) : (5, 10, 20));
        var report = Analyze(Evidence(series: [cpu]));

        var section = report.Sections.Single(s => s.Id == "cpu");
        var high = section.Findings.ShouldHaveSingleItem();
        high.Title.ShouldBe("High CPU load");
        high.Severity.ShouldBe(Severity.Medium);
        high.Text.ShouldContain("for 3 min in total (once)");
        high.Text.ShouldContain("highest 100 %");
        high.From.ShouldBe(From.AddMinutes(30));
        high.To.ShouldBe(From.AddMinutes(33));
        high.SeriesIds.ShouldBe(["cpu"]);
        section.Stats.Single(s => s.Label == "Time at or above 95 %").Value.ShouldBe("3 min");
    }

    [Fact]
    public void Short_episodes_under_the_rules_duration_are_not_reported()
    {
        // 10 s buckets: 20 s above 95 % is shorter than cpu-high's 30 s.
        var cpu = Series("cpu", "%", 10_000, i => i is 100 or 101 ? (97, 98, 99) : (5, 10, 20));

        Analyze(Evidence(series: [cpu])).Sections.Single(s => s.Id == "cpu").Findings.ShouldBeEmpty();
    }

    [Fact]
    public void User_thresholds_apply()
    {
        var cpu = Series("cpu", "%", 60_000, i => i is >= 30 and < 33 ? (80, 85, 90) : (5, 10, 20));
        var rules = AnomalyRuleCatalog.Default.Apply(new TriggerOptions { Rules = { ["cpu-high"] = new TriggerRuleSettings { Threshold = 80 } } });

        var report = PeriodDiagnostics.Analyze(Evidence(series: [cpu]), rules, TimeZoneInfo.Utc, To);

        report.Sections.Single(s => s.Id == "cpu").Findings.ShouldHaveSingleItem().Text.ShouldStartWith("CPU at or above 80 %");
    }

    [Fact]
    public void Low_memory_uses_the_rule_in_gigabytes_and_names_who_held_memory_then()
    {
        var available = Series("memory.available", "B", 60_000, i => i is >= 50 and < 52 ? (0.5 * Gib, 0.7 * Gib, 0.9 * Gib) : (8 * Gib, 9 * Gib, 10 * Gib));
        var peak = new PeakMoment("memory.available", From.AddMinutes(50), 0.5 * Gib, [new("vmmemWSL", 18940, 38 * Gib), new("chrome.exe", 1200, 2 * Gib)]);

        var section = Analyze(Evidence(series: [available]) with { Peaks = [peak] }).Sections.Single(s => s.Id == "memory");

        var low = section.Findings.ShouldHaveSingleItem();
        low.Title.ShouldBe("Low available memory");
        low.Severity.ShouldBe(Severity.High);
        low.Text.ShouldContain("lowest 512 MB");
        section.Stats.ShouldContain(s => s.Label == "Most memory at the low point (10:50:00)" && s.Value == "vmmemWSL 38.0 GB, chrome.exe 2.0 GB");
    }

    [Fact]
    public void Memory_that_keeps_growing_is_a_possible_leak_except_a_virtual_machine_host()
    {
        var processes = new[]
        {
            Process("leaky.exe", 100, firstPrivate: (long)(0.5 * Gib), lastPrivate: (long)(2 * Gib)),
            Process("vmmemWSL", 200, firstPrivate: (long)(3 * Gib), lastPrivate: (long)(16 * Gib)),
            Process("steady.exe", 300, firstPrivate: (long)(1 * Gib), lastPrivate: (long)(1.1 * Gib)),
        };

        var findings = Analyze(Evidence() with { Processes = processes }).Sections.Single(s => s.Id == "memory").Findings;

        var leak = findings.Single(f => f.Title == "leaky.exe memory grew");
        leak.Level.ShouldBe(EvidenceLevel.PossibleExplanation);
        leak.Severity.ShouldBe(Severity.Medium);
        leak.Text.ShouldContain("may be a memory leak");
        var vm = findings.Single(f => f.Title == "vmmemWSL memory grew");
        vm.Severity.ShouldBe(Severity.Low);
        vm.Text.ShouldContain("virtual machine");
        findings.ShouldNotContain(f => f.Title.StartsWith("steady.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_stopping_uncleanly_is_listed_one_by_one_and_recorder_stops_together()
    {
        var stops = new[]
        {
            new PeriodUncleanStop(From.AddMinutes(20), From.AddMinutes(19), "BugCheck", true, "Windows restarted after a bug check (0x50). The recorder's last heartbeat was at 10:19."),
            new PeriodUncleanStop(From.AddMinutes(40), From.AddMinutes(39), "RecorderStopped", true, "The recorder stopped unexpectedly after 10:39; Windows kept running."),
            new PeriodUncleanStop(From.AddMinutes(60), From.AddMinutes(59), "RecorderStopped", false, "The recorder stopped unexpectedly after 10:59; Windows kept running."),
        };

        var coverage = Analyze(Evidence() with { UncleanStops = stops }).Sections.Single(s => s.Id == "coverage");

        coverage.Findings.Single(f => f.Title == "Bug check").Severity.ShouldBe(Severity.Critical);
        var recorder = coverage.Findings.Single(f => f.Title == "Recorder stopped");
        recorder.Occurrences.ShouldBe(2);
        recorder.Severity.ShouldBe(Severity.Low);
        coverage.Tables.ShouldHaveSingleItem().Rows.Count.ShouldBe(3);
    }

    [Fact]
    public void Gaps_in_recording_are_reported()
    {
        var report = Analyze(Evidence() with { Sessions = [new(From, From.AddMinutes(30), false), new(From.AddMinutes(90), To, true)] });

        var coverage = report.Sections.Single(s => s.Id == "coverage");
        coverage.Findings.Single(f => f.Title == "Not recorded throughout").Text.ShouldContain("Not recorded for 60 min in total (1 gap)");
        coverage.Findings.ShouldContain(f => f.Title == "Custom recording");
        report.Summary.RecordedMinutes.ShouldBe(60);
    }

    [Fact]
    public void Sensor_limits_are_not_temperatures_and_unlabeled_board_inputs_are_doubted()
    {
        var sensors = new[]
        {
            new SensorPeriodStats("Corsair DIMM / Thermal Sensor Critical High Limit", "Temperature", "°C", 85, 85, 105, From),
            new SensorPeriodStats("Intel Core Ultra 9 / P-Core #1 Distance to TjMax", "Temperature", "°C", 20, 60, 99, From),
            new SensorPeriodStats("Nuvoton NCT6701D / Temperature #6", "Temperature", "°C", 26, 82, 125, From.AddMinutes(5)),
            new SensorPeriodStats("Intel Core Ultra 9 / CPU Package", "Temperature", "°C", 40, 60, 101, From.AddMinutes(10)),
        };

        var section = Analyze(Evidence() with { Sensors = sensors }).Sections.Single(s => s.Id == "sensors");

        section.Findings.Count.ShouldBe(2);
        section.Findings.Single(f => f.Title.StartsWith("Intel Core Ultra 9 / CPU Package", StringComparison.Ordinal)).Severity.ShouldBe(Severity.High);
        var board = section.Findings.Single(f => f.Title.StartsWith("Nuvoton", StringComparison.Ordinal));
        board.Severity.ShouldBe(Severity.Low);
        board.Level.ShouldBe(EvidenceLevel.PossibleExplanation);
        section.Tables.ShouldHaveSingleItem().Rows.Select(r => r[0]).ShouldNotContain("Corsair DIMM / Thermal Sensor Critical High Limit");
    }

    [Fact]
    public void Windows_events_are_grouped_by_title_with_severity_by_category()
    {
        var events = new[]
        {
            Logged(10, "Display driver nvlddmkm stopped responding and has successfully recovered", EventCategory.Display),
            Logged(20, "Display driver nvlddmkm stopped responding and has successfully recovered", EventCategory.Display),
            Logged(30, "The computer restarted after a bug check", EventCategory.BugCheck),
            Logged(40, "Chrome crashed", EventCategory.ApplicationCrash),
        };

        var report = Analyze(Evidence() with { Events = events });

        var display = report.Sections.Single(s => s.Id == "display").Findings.Single(f => f.Level == EvidenceLevel.ConfirmedEvent);
        display.Occurrences.ShouldBe(2);
        display.Severity.ShouldBe(Severity.High);
        display.Text.ShouldContain("2 times");
        report.Sections.Single(s => s.Id == "system").Findings.ShouldHaveSingleItem().Severity.ShouldBe(Severity.Critical);
        report.Sections.Single(s => s.Id == "processes").Findings.ShouldContain(f => f.Title == "Chrome crashed" && f.Severity == Severity.High);
        report.Summary.Top[0].Severity.ShouldBe(Severity.Critical);
        report.Summary.Top[0].Section.ShouldBe("system");
    }

    [Fact]
    public void The_summary_counts_by_severity_and_leaves_out_the_anomaly_recap_and_lesser_incidents()
    {
        var anomaly = new AnomalyInfo(1, "cpu-high", "High CPU", "cpu", null, From.AddMinutes(5).ToUnixTimeMilliseconds(), null, 99, "%", Severity.High,
            "CPU above 95 % for at least 30 s", null, "99 %", 40, null, "High CPU");
        var evidence = Evidence() with
        {
            Anomalies = [anomaly],
            Incidents = [new("INC-1", From.AddMinutes(10), "Screen Flicker", Severity.Medium, "Hotkey"), new("INC-2", From.AddMinutes(20), "Display driver error", Severity.High, "Event")],
        };

        var report = Analyze(evidence);

        report.Summary.Top.ShouldNotContain(f => f.Section == "anomalies");
        report.Summary.Top.ShouldContain(f => f.IncidentKey == "INC-2");
        report.Summary.Top.ShouldNotContain(f => f.IncidentKey == "INC-1");
        report.Sections.Single(s => s.Id == "incidents").Findings.Count.ShouldBe(2);
        report.Sections.Single(s => s.Id == "anomalies").Findings.ShouldHaveSingleItem().Severity.ShouldBe(Severity.High);
        report.Summary.FindingsBySeverity["High"].ShouldBe(report.Sections.Where(s => s.Id != "anomalies").Sum(s => s.Findings.Count(f => f.Severity == Severity.High)));
    }

    [Fact]
    public void Programs_started_very_often_and_untrusted_signatures_are_findings()
    {
        var names = new[] { new ProcessNameStats("docker.exe", 3000, 2990, 0, 0), new ProcessNameStats("notepad.exe", 3, 3, 0, 0) };
        var processes = new[] { Process("tool.exe", 400) with { Signature = "NotTrusted", Signer = "Contoso", Path = @"C:\Tools\tool.exe" } };

        var findings = Analyze(Evidence() with { ProcessNames = names, Processes = processes }).Sections.Single(s => s.Id == "processes").Findings;

        findings.Single(f => f.Title == "docker.exe started often").Text.ShouldContain("3,000 times");
        findings.ShouldNotContain(f => f.Title.StartsWith("notepad", StringComparison.Ordinal));
        findings.Single(f => f.Title == "tool.exe: signature not trusted").Text.ShouldContain(@"C:\Tools\tool.exe");
    }

    [Fact]
    public void Network_errors_and_full_volumes_are_reported_and_idle_adapters_left_out()
    {
        var ethernet = Series("net.5.rx", "B/s", 60_000, _ => (1e5, 2e5, 5e6), device: "Ethernet");
        var errors = Series("net.5.errors", "", 60_000, _ => (0, 0, 3), device: "Ethernet");
        var idle = Series("net.6.rx", "B/s", 60_000, _ => (0, 0, 10), device: "vEthernet");
        var evidence = Evidence(series: [ethernet, errors, idle]) with
        {
            NetworkErrors = [new("net.5.errors", 120, 30, From.AddMinutes(15))],
            Volumes = [new("D:\\", "Data", (long)(1000 * Gib), (long)(30 * Gib), From.AddMinutes(70))],
        };

        var report = Analyze(evidence);

        var network = report.Sections.Single(s => s.Id == "network");
        network.Stats.ShouldHaveSingleItem().Label.ShouldBe("Ethernet");
        network.Findings.ShouldHaveSingleItem().Text.ShouldContain("120 packet errors");
        var full = report.Sections.Single(s => s.Id == "storage").Findings.ShouldHaveSingleItem();
        full.Title.ShouldBe("D:\\ nearly full");
        full.Severity.ShouldBe(Severity.High);
    }

    [Fact]
    public void Every_finding_passes_the_language_guard()
    {
        var cpu = Series("cpu", "%", 60_000, i => i is >= 30 and < 33 ? (98, 99, 100) : (5, 10, 20));
        var evidence = Evidence(series: [cpu]) with
        {
            Processes = [Process("leaky.exe", 100, firstPrivate: (long)(0.5 * Gib), lastPrivate: (long)(2 * Gib))],
            ProcessNames = [new("docker.exe", 3000, 2990, 4, 4)],
            Services = [new("wuauserv", "Windows Update", 3, 3, 8, 2, 1, From.AddMinutes(30))],
            Events = [Logged(30, "The previous shutdown was caused by a power loss", EventCategory.UnexpectedShutdown)],
        };

        var report = Analyze(evidence);

        var all = report.Sections.SelectMany(s => s.Findings).ToList();
        all.Count.ShouldBeGreaterThan(5);
        all.Select(f => LanguageGuard.Violation(new Finding(f.Level, f.Text, []))).ShouldAllBe(problem => problem == null);
    }

    [Fact]
    public void Durations_read_naturally()
    {
        PeriodContext.Duration(TimeSpan.Zero).ShouldBe("none");
        PeriodContext.Duration(TimeSpan.FromSeconds(45)).ShouldBe("45 s");
        PeriodContext.Duration(TimeSpan.FromMinutes(11)).ShouldBe("11 min");
        PeriodContext.Duration(TimeSpan.FromMinutes(150)).ShouldBe("2 h 30 min");
        PeriodContext.Duration(TimeSpan.FromHours(80)).ShouldBe("3 d 8 h");
    }

    private static PeriodReport Analyze(PeriodEvidence evidence) => PeriodDiagnostics.Analyze(evidence, Rules, TimeZoneInfo.Utc, To);

    private static PeriodEvidence Evidence(IReadOnlyList<PeriodSeries>? series = null) =>
        new(From, To, From, series ?? [], [new(From, To, false)], [], [], [], [], [], [], [], [], [], [], [], 16);

    /// <summary>A series over the period in buckets of <paramref name="bucketMs"/>; <paramref name="value"/> gives each bucket's (min, avg, max).</summary>
    private static PeriodSeries Series(string id, string unit, long bucketMs, Func<int, (double Min, double Avg, double Max)> value, string? device = null)
    {
        var count = (int)((To - From).TotalMilliseconds / bucketMs);
        var points = Enumerable.Range(0, count).Select(i =>
        {
            var (min, avg, max) = value(i);
            return new[] { From.ToUnixTimeMilliseconds() + (i * bucketMs), min, avg, max };
        }).ToList();
        return new PeriodSeries(id, id, unit, "System", device, bucketMs, points);
    }

    private static ProcessPeriodStats Process(string name, int pid, long? firstPrivate = null, long? lastPrivate = null) =>
        new(pid, name, pid, null, "Embedded", null, From, To, 100, 1000, 5, firstPrivate, lastPrivate, lastPrivate, 100, 120, null, null);

    private static EvidenceEvent Logged(int minute, string title, EventCategory category) =>
        new(From.AddMinutes(minute), "windows-event", title, 3, "WindowsEvents", minute, category, "Provider", 1);
}
