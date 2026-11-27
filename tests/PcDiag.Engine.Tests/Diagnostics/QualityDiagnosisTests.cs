using System.Globalization;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Quality;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Anomalies;
using PcDiag.Engine.Diagnostics;
using PcDiag.Engine.Quality;

namespace PcDiag.Engine.Tests.Diagnostics;

public sealed class QualityDiagnosisTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);

    private static string Clock(DateTimeOffset at) => at.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>A game at 120 fps for 3 minutes with a drop to 50 fps for 5 s at 2:00 and a 300 ms hitch at 2:40.</summary>
    private static List<FrameSecond> Game()
    {
        var seconds = new List<FrameSecond>();
        for (var s = 0; s < 180; s++)
        {
            var fps = s is >= 120 and < 125 ? 50 : 120;
            seconds.Add(new FrameSecond(T0.AddSeconds(s), fps, s == 160 ? 300 : 1000.0 / fps, s == 160 ? 1 : 0, 120));
        }

        return seconds;
    }

    private static MetricTrack Track(string seriesId, string metric, Func<int, double> value, string? device = null) =>
        new(seriesId, metric, metric, "", device, Enumerable.Range(0, 180).Select(s => new MetricPoint(T0.AddSeconds(s), value(s))).ToList());

    private static bool InDrop(int s) => s is >= 120 and < 125;

    [Fact]
    public void Drops_and_hitches_are_found_and_merged()
    {
        var drops = FrameDrops.Find(Game());

        drops.Count.ShouldBe(2);
        drops[0].ShouldBe((T0.AddSeconds(120), T0.AddSeconds(124)));
        drops[1].ShouldBe((T0.AddSeconds(160), T0.AddSeconds(160)));
    }

    [Fact]
    public void A_drop_with_the_gpu_at_its_limit_and_its_clock_falling_while_hot()
    {
        var tracks = new List<MetricTrack>
        {
            Track("gpu.1.util", "gpu.util", s => InDrop(s) ? 99 : 80, "RTX"),
            Track("gpu.1.core", "gpu.core", s => InDrop(s) ? 1200 : 2600, "RTX"),
            Track("gpu.1.hotspot", "gpu.hotspot", s => InDrop(s) ? 104 : 85, "RTX"),
            Track("cpu.maxcore", "cpu.maxcore", _ => 60),
        };

        var drop = FrameDrops.Explain(T0.AddSeconds(120), T0.AddSeconds(124), Game(), tracks, null, "Game", null, Clock);

        drop.LowestFps.ShouldBe(50);
        drop.UsualFps.ShouldBe(120);
        drop.Findings.Select(f => f.Kind).ShouldBe(["gpu", "thermal"], ignoreOrder: true);
        drop.Findings.ShouldContain(f => f.Kind == "thermal" && f.Text.Contains("2600 to 1200 MHz") && f.Text.Contains("104 °C"));
        drop.Findings.ShouldNotContain(f => f.Kind == "cpu");
    }

    [Fact]
    public void A_drop_with_one_processor_full_while_the_gpu_waits_and_another_program_busy()
    {
        var tracks = new List<MetricTrack>
        {
            Track("gpu.1.util", "gpu.util", s => InDrop(s) ? 55 : 90),
            Track("cpu.maxcore", "cpu.maxcore", s => InDrop(s) ? 100 : 70),
            Track("latency.dpc", "latency.dpc", s => InDrop(s) ? 55 : 21),
        };
        var processes = new List<ProcessActivity>
        {
            new("Game", 20, 35, 90, 55, 0, 0, 0, 0),             // the game itself is left out
            new("MsMpEng", 0.5, 14, 0, 0, 1e6, 120 * 1_048_576, 0, 0), // a virus scan
            new("Discord", 1, 1.5, 2, 2, 0, 0, 1e4, 1e4),        // unchanged
        };
        var measurements = new List<DriverLatencyMeasurement>
        {
            new(T0.AddSeconds(135), T0.AddSeconds(125), 10, "stutter", null, [new DriverLatency("Netwtw14.sys", 900, 2100, 40_000, 10, 30, 200, 6, 2)]),
        };

        var drop = FrameDrops.Explain(T0.AddSeconds(120), T0.AddSeconds(124), Game(), tracks, processes, "Game", measurements, Clock);

        drop.Findings.ShouldContain(f => f.Kind == "cpu" && f.Text.StartsWith("One processor was 100 % busy while the GPU was only 55 % busy", StringComparison.Ordinal));
        drop.Findings.ShouldContain(f => f.Kind == "driver" && f.Severity == 3 && f.Text.Contains("Netwtw14.sys running up to 2100 µs"));
        drop.Findings.ShouldContain(f => f.Kind == "program" && f.Text.StartsWith("MsMpEng used 14 % CPU (0.5 % before), 120 MB/s of disk", StringComparison.Ordinal));
        drop.Findings.ShouldNotContain(f => f.Text.StartsWith("Game ", StringComparison.Ordinal) || f.Text.StartsWith("Discord", StringComparison.Ordinal));
    }

    [Fact]
    public void PresentMons_per_frame_times_say_which_side_held_the_frames_and_frames_that_stopped_reaching_the_screen_are_named()
    {
        // The GPU at 99 % overall (another program shares it) but working on the game's frames only 50 % of their time,
        // while the game's CPU work takes 92 % of it: the CPU side, not the GPU.
        var tracks = new List<MetricTrack>
        {
            Track("gpu.1.util", "gpu.util", _ => 99),
            Track("game.gpubusy", "game.gpubusy", s => InDrop(s) ? 50 : 80),
            Track("game.cpubusy", "game.cpubusy", s => InDrop(s) ? 92 : 60),
            Track("game.fps", "game.fps", s => InDrop(s) ? 50 : 120),
            Track("game.shown", "game.shown", s => InDrop(s) ? 30 : 118),
        };

        var drop = FrameDrops.Explain(T0.AddSeconds(120), T0.AddSeconds(124), Game(), tracks, null, "Game", null, Clock);

        drop.Findings.ShouldContain(f => f.Kind == "cpu" && f.Text.StartsWith("The game's CPU work took 92 % of each frame's time during the drop while the GPU worked on it 50 %", StringComparison.Ordinal));
        drop.Findings.ShouldNotContain(f => f.Kind == "gpu"); // the overall 99 % is not repeated
        drop.Findings.ShouldContain(f => f.Kind == "display" && f.Text.StartsWith("Only 30 of the 50 frames a second", StringComparison.Ordinal));

        var gpuBound = FrameDrops.Explain(T0.AddSeconds(120), T0.AddSeconds(124), Game(), [Track("game.gpubusy", "game.gpubusy", s => InDrop(s) ? 97 : 70)], null, "Game", null, Clock);
        gpuBound.Findings.ShouldHaveSingleItem().Text.ShouldStartWith("The GPU worked on each frame 97 % of its time during the drop");
    }

    [Fact]
    public void A_session_says_how_its_frames_reached_the_screen_and_which_side_was_its_limit()
    {
        var session = new GameSession(1, "IndieGame", T0, T0.AddHours(1), 3600, 140, 90, 85, 40, 12, 60, DisplayedFps: 120, Dropped: 108_000, DisplayLatencyMs: 62,
            GpuBusyPercent: 96, CpuBusyPercent: 40, PresentMode: "Composed: Flip", WindowedPercent: 100, Recognized: "library");

        var findings = SessionDiagnosis.Analyze(session);

        findings.ShouldContain(f => f.Text.StartsWith("Its frames went through Windows' desktop compositor (Composed: Flip)", StringComparison.Ordinal));
        findings.ShouldContain(f => f.Kind == "gpu" && f.Text.Contains("96 %"));
        findings.ShouldContain(f => f.Text.StartsWith("About 30 frames a second never reached the screen (120 a second were shown)", StringComparison.Ordinal));
        findings.ShouldContain(f => f.Text.StartsWith("Frames took 62 ms on average from their start to the screen", StringComparison.Ordinal));

        var direct = SessionDiagnosis.Analyze(session with { PresentMode = "Hardware: Independent Flip", GpuBusyPercent = 55, CpuBusyPercent = 90, Dropped = 0, DisplayLatencyMs = 12 });
        direct.ShouldContain(f => f.Severity == 0 && f.Text == "Its frames went straight to the screen (Hardware: Independent Flip).");
        direct.ShouldContain(f => f.Kind == "cpu");
        SessionDiagnosis.Analyze(session with { PresentMode = null, GpuBusyPercent = null, CpuBusyPercent = null, DisplayedFps = null, Dropped = null, DisplayLatencyMs = null })
            .ShouldBeEmpty();

        foreach (var finding in findings.Concat(direct))
        {
            var level = finding.Text.Contains(" may ", StringComparison.Ordinal) || finding.Text.Contains(" could ", StringComparison.Ordinal)
                ? EvidenceLevel.PossibleExplanation
                : EvidenceLevel.Observation;
            LanguageGuard.Violation(new Finding(level, finding.Text, [])).ShouldBeNull(finding.Text);
        }
    }

    [Fact]
    public void A_drop_with_nothing_else_changing_points_at_the_game()
    {
        var tracks = new List<MetricTrack> { Track("gpu.1.util", "gpu.util", _ => 70), Track("cpu.maxcore", "cpu.maxcore", _ => 60) };

        var drop = FrameDrops.Explain(T0.AddSeconds(120), T0.AddSeconds(124), Game(), tracks, [], "Game", [], Clock);

        drop.Findings.ShouldHaveSingleItem().Kind.ShouldBe("game");
    }

    private static QualityMinute Minute(int i, double? routerLoss, double? providerLoss, double? internetLoss, double internetMs = 20, double? netBps = 1000, double routerMs = 1,
        int dnsFailed = 0) =>
        new(T0.AddMinutes(i), 60, null, 0, null, null, null, 0, null, 0, 2, 1, 0.5, new HopMinute(routerMs, routerMs * 2, routerLoss), new HopMinute(8, 12, providerLoss),
            new HopMinute(internetMs, internetMs * 2, internetLoss), 2, 15, 6, dnsFailed, null, netBps);

    [Fact]
    public void Losses_at_the_router_are_the_home_network()
    {
        var minutes = Enumerable.Range(0, 30).Select(i => Minute(i, i % 3 == 0 ? 9 : 0, i % 3 == 0 ? 9 : 0, i % 3 == 0 ? 10 : 0)).ToList();

        var report = ConnectionDiagnosis.Analyze(minutes, null, Clock);

        report.Router.Loss.ShouldBe(3);
        report.Internet.Loss!.Value.ShouldBe(3.33, 0.01);
        report.Findings[0].Kind.ShouldBe("home-network");
    }

    [Fact]
    public void Losses_only_after_the_router_are_the_providers()
    {
        var minutes = Enumerable.Range(0, 30).Select(i => Minute(i, 0, i % 2 == 0 ? 6 : 0, i % 2 == 0 ? 6 : 0)).ToList();

        var report = ConnectionDiagnosis.Analyze(minutes, null, Clock);

        report.Findings[0].Kind.ShouldBe("provider");
        report.Findings[0].Severity.ShouldBe(2);
    }

    [Fact]
    public void Round_trips_rising_with_traffic_are_bufferbloat_and_a_minute_mostly_lost_is_an_outage()
    {
        var minutes = Enumerable.Range(0, 40)
            .Select(i => i >= 30 ? Minute(i, 0, 0, 0, internetMs: 180, netBps: 12_000_000) : Minute(i, 0, 0, 0, internetMs: 20, netBps: 20_000))
            .ToList();
        minutes[5] = Minute(5, 0, 80, 90);
        var programs = new List<NetworkProgram> { new("steam", 11_000_000, 100_000, 13_000_000, 0.2) };

        var report = ConnectionDiagnosis.Analyze(minutes, programs, Clock);

        report.Findings.ShouldContain(f => f.Kind == "bufferbloat" && f.Text.Contains("steam moved the most data"));
        report.Outages.ShouldHaveSingleItem().From.ShouldBe(T0.AddMinutes(5));
    }

    [Fact]
    public void A_quiet_connection_says_so_and_failing_dns_is_named()
    {
        var quiet = ConnectionDiagnosis.Analyze(Enumerable.Range(0, 10).Select(i => Minute(i, 0, 0, 0)).ToList(), null, Clock);
        quiet.Findings.ShouldHaveSingleItem().Severity.ShouldBe(0);

        var dns = ConnectionDiagnosis.Analyze(Enumerable.Range(0, 10).Select(i => Minute(i, 0, 0, 0, dnsFailed: 1)).ToList(), null, Clock);
        dns.Findings.ShouldContain(f => f.Kind == "dns" && f.Text.StartsWith("10 of 60 DNS lookups failed", StringComparison.Ordinal));
    }

    [Fact]
    public void Driver_latency_names_the_slow_driver_with_what_it_is()
    {
        var measurements = new List<DriverLatencyMeasurement>
        {
            new(T0, T0.AddSeconds(-10), 10, "measure", null, [new DriverLatency("nvlddmkm.sys", 5000, 1400, 90_000, 0, 0, 0, 12, 3), new DriverLatency("ntoskrnl.exe", 900, 80, 2000, 0, 0, 0, 0, 0)]),
        };
        var minutes = new List<QualityMinute> { Minute(0, 0, 0, 0) with { DpcCoreMax = 52 }, Minute(1, 0, 0, 0) with { DpcCoreMax = 24 } };

        var findings = LatencyDiagnosis.Analyze(minutes, measurements, Clock);

        findings.ShouldContain(f => f.Severity == 3 && f.Text.Contains("nvlddmkm.sys (the NVIDIA graphics driver) running up to 1400 µs"));
        findings.ShouldContain(f => f.Severity == 3 && f.Text.StartsWith("In 1 minute drivers held one processor 30 % of the time or more", StringComparison.Ordinal) && f.Text.Contains("52 % in DPCs"));
        LatencyDiagnosis.Describe("Netwtw14.sys")!.What.ShouldBe("the Intel Wi-Fi driver");
        LatencyDiagnosis.Describe("mystery.sys").ShouldBeNull();
    }

    [Fact]
    public void Every_finding_is_worded_like_an_incident_finding()
    {
        var tracks = new List<MetricTrack>
        {
            Track("gpu.1.util", "gpu.util", s => InDrop(s) ? 99 : 60),
            Track("gpu.1.core", "gpu.core", s => InDrop(s) ? 900 : 2600),
            Track("vram", "vram", _ => 98),
            Track("cpu", "cpu", _ => 95),
            Track("cpu.limit", "cpu.limit", _ => 70),
            Track("cpu.temp", "cpu.temp", _ => 99),
            Track("memory.hardfaults", "memory.hardfaults", s => InDrop(s) ? 900 : 10),
            Track("memory.available", "memory.available", _ => 5e8),
            Track("disk.1.readlat", "disk.readlat", s => InDrop(s) ? 300 : 2, "SSD"),
            Track("latency.dpc", "latency.dpc", s => InDrop(s) ? 45 : 22),
        };
        var drop = FrameDrops.Explain(T0.AddSeconds(120), T0.AddSeconds(124), Game(), tracks, [new("Updater", 0, 30, 0, 40, 0, 1e8, 0, 0)], "Game", [], Clock);
        var connection = ConnectionDiagnosis.Analyze(Enumerable.Range(0, 40).Select(i => Minute(i, 5, 6, 7, internetMs: i > 30 ? 200 : 20, netBps: i > 30 ? 9e6 : 1e3, routerMs: 25, dnsFailed: 2))
            .ToList(), [new("steam", 9e6, 0, 9e6, 3)], Clock);
        var latency = LatencyDiagnosis.Analyze([Minute(0, 0, 0, 0) with { IsrCoreMax = 40 }],
            [new(T0, T0, 10, "dpc", null, [new DriverLatency("acpi.sys", 10, 3000, 3000, 0, 0, 0, 1, 1)])], Clock);

        var all = drop.Findings.Concat(connection.Findings).Concat(latency).ToList();
        all.Count.ShouldBeGreaterThan(12);
        foreach (var finding in all)
        {
            var level = finding.Text.Contains(" may ", StringComparison.Ordinal) || finding.Text.Contains(" could ", StringComparison.Ordinal)
                ? EvidenceLevel.PossibleExplanation
                : EvidenceLevel.Observation;
            LanguageGuard.Violation(new Finding(level, finding.Text, [])).ShouldBeNull(finding.Text);
        }
    }

    [Fact]
    public void An_incident_with_a_frame_drop_is_explained_by_the_frame_rate_analyzer()
    {
        var center = T0.AddSeconds(122);
        var tracks = new List<MetricTrack>
        {
            Track("game.fps", "game.fps", s => InDrop(s) ? 50 : 120),
            Track("game.usual", "game.usual", _ => 120),
            Track("game.worst", "game.worst", s => InDrop(s) ? 40 : 9),
            Track("game.stutters", "game.stutters", _ => 0),
            Track("gpu.1.util", "gpu.util", s => InDrop(s) ? 99 : 70),
        };
        var anomaly = new Anomaly(1, "game-fps-drop", "game.drop", center, null, 58, Severity.Medium, new AnomalyReasoning("Frame rate drop", "40 % below its usual frame rate for 5 s", null, null, null, "58 %", TimeSpan.FromSeconds(5)), Device: "ArkAscended");
        var evidence = new IncidentEvidence(Core.Incidents.IncidentKey.Create(new DateOnly(2026, 11, 26), 1), center, T0, T0.AddSeconds(179), Core.Incidents.IncidentSource.Rule, null, false, tracks, [], [anomaly], []);

        var findings = IncidentDiagnostics.Analyze(evidence, [], TimeZoneInfo.Utc);

        findings.ShouldContain(f => f.Text.StartsWith("ArkAscended ran at 50 fps against its usual 120 fps from 20:02:00 to 20:02:04", StringComparison.Ordinal));
        findings.ShouldContain(f => f.Level == EvidenceLevel.PossibleExplanation && f.Text.StartsWith("The GPU was 99 % busy during the drop", StringComparison.Ordinal));
    }

    [Fact]
    public void Lost_pings_are_watched_over_the_last_twenty()
    {
        var extractor = new MetricExtractor();
        var output = new List<MetricValue>();
        for (var i = 0; i < 25; i++)
        {
            output.Clear();
            var lost = i is 20 or 21 or 22;
            extractor.Extract(new SampleFrame(i, T0.AddSeconds(i), Connection: new ConnectionSample(T0.AddSeconds(i), new PingResult(true, 1), PingResult.None,
                new PingResult(true, lost ? null : 20), null, null, null)), output);
        }

        output.Single(v => v.Metric == AnomalyMetrics.ConnectionLoss).Value.ShouldBe(15); // 3 of the last 20
        output.Single(v => v.Metric == AnomalyMetrics.RouterLoss).Value.ShouldBe(0);
        output.Single(v => v.Metric == AnomalyMetrics.ConnectionPing).Value.ShouldBe(20);
        output.ShouldNotContain(v => v.Metric == "conn.provider");
    }
}
