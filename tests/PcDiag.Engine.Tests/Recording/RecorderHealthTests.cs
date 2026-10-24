using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Tests.Recording;

public class RingBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private static RingBuffer Ring(int preMinutes = 1, int maxMegabytes = 64) => new(
        new StaticOptionsMonitor<RecorderOptions>(new RecorderOptions { PreIncidentMinutes = preMinutes, RingBufferMaxMegabytes = maxMegabytes }),
        new StaticOptionsMonitor<SamplingOptions>(new SamplingOptions()),
        NullLogger<RingBuffer>.Instance);

    private static SampleFrame Frame(int second, int processes = 0)
    {
        var at = T0.AddSeconds(second);
        var d = Reading<double>.Unavailable(Availability.Unsupported);
        var l = Reading<long>.Unavailable(Availability.Unsupported);
        var n = Reading<int>.Unavailable(Availability.Unsupported);
        var metrics = Enumerable.Range(1, processes)
            .Select(i => new ProcessMetrics(new ProcessKey(i, i), "p.exe", d, l, l, l, l, d, d, n, n, n, ProcessState.Running))
            .ToList();
        return new SampleFrame(second, at, Processes: processes == 0 ? null : new ProcessSample(at, metrics, [], []));
    }

    [Fact]
    public void Frames_older_than_the_pre_window_plus_a_minute_are_evicted()
    {
        var ring = Ring(preMinutes: 1);
        for (var s = 0; s <= 180; s++)
        {
            ring.Append(Frame(s));
        }

        var stats = ring.Stats;
        stats.Newest.ShouldBe(T0.AddSeconds(180));
        stats.Oldest.ShouldBe(T0.AddSeconds(60)); // 2-minute window: pre-window (1) + 1
        stats.Frames.ShouldBe(121);
        stats.Evicted.ShouldBe(60);
    }

    [Fact]
    public void The_size_cap_wins_over_the_window()
    {
        var ring = Ring(preMinutes: 30, maxMegabytes: 16);
        var perFrame = SampleFrameSize.Estimate(Frame(0, processes: 4000)); // ~1 MB of process metrics

        for (var s = 0; s < 60; s++)
        {
            ring.Append(Frame(s, processes: 4000));
        }

        ring.Stats.EstimatedBytes.ShouldBeLessThanOrEqualTo(16L * 1024 * 1024);
        ring.Stats.Frames.ShouldBe((int)(16L * 1024 * 1024 / perFrame));
        ring.Stats.Newest.ShouldBe(T0.AddSeconds(59));
    }

    [Fact]
    public void Rows_and_readings_shared_with_the_previous_frame_are_charged_once()
    {
        var sharing = new FrameSharing();
        var first = Frame(0, processes: 1000);
        var sensors = new SensorSample(T0, Enumerable.Range(0, 200)
            .Select(i => new SensorReading(new SensorDescriptor("lhm", "hw", SensorKind.Temperature, $"s{i}", "°C", $"lhm/{i}"), Reading<double>.Of(i))).ToList());
        first = first with { Sensors = sensors };

        var full = SampleFrameSize.Estimate(first, sharing);
        full.ShouldBe(SampleFrameSize.Estimate(first));

        // The next frames hold the same objects (collectors hand back what reads as before) in new lists.
        var same = Frame(1) with { Processes = first.Processes! with { Processes = [.. first.Processes!.Processes] }, Sensors = new SensorSample(T0, [.. sensors.Readings]) };
        var shared = SampleFrameSize.Estimate(same, sharing);
        shared.ShouldBeLessThan(full / 10);

        // A frame without processes or sensors does not make the next one pay for them again.
        SampleFrameSize.Estimate(Frame(2), sharing);
        SampleFrameSize.Estimate(same with { Sequence = 3 }, sharing).ShouldBe(shared);

        // New objects with equal values are charged in full (this frame has no sensors).
        SampleFrameSize.Estimate(Frame(4, processes: 1000), sharing).ShouldBe(full - (200 * 160));
    }

    [Fact]
    public void Frames_sharing_their_rows_keep_the_whole_window_under_the_cap()
    {
        var ring = Ring(preMinutes: 30, maxMegabytes: 16);
        var rows = Frame(0, processes: 1000).Processes!; // 240 KB charged once, then 8 KB of references per frame
        for (var s = 0; s < 600; s++)
        {
            ring.Append(Frame(s) with { Processes = rows with { Timestamp = T0.AddSeconds(s) } });
        }

        ring.Stats.Frames.ShouldBe(600); // unshared, 600 such frames would be charged 144 MB
        ring.Stats.EstimatedBytes.ShouldBeLessThan(6L * 1024 * 1024);
    }

    [Fact]
    public void Snapshot_returns_the_requested_range_in_order()
    {
        var ring = Ring(preMinutes: 5);
        for (var s = 0; s < 100; s++)
        {
            ring.Append(Frame(s));
        }

        var frames = ring.Snapshot(T0.AddSeconds(10), T0.AddSeconds(19));

        frames.Select(f => f.Sequence).ShouldBe(Enumerable.Range(10, 10).Select(i => (long)i));
    }

    [Fact]
    public void Slots_are_allocated_once_for_the_longest_window()
    {
        Ring().Capacity.ShouldBe((RingBuffer.MaxWindowMinutes * 60) + 16);
    }
}

public class BootRecoveryClassifierTests
{
    private static readonly DateTimeOffset LastBeat = new(2026, 10, 5, 20, 6, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 21, 30, 0, TimeSpan.Zero);

    private static SystemEvent E(int minutesAfterBeat, EventCategory category, string ruleId, Dictionary<string, string>? data = null, string provider = "p", int id = 1) =>
        new(LastBeat.AddMinutes(minutesAfterBeat), "System", 1, provider, id, EventLevel.Error, category, Severity.High, ruleId, string.Empty, RuleId: ruleId, Data: data);

    private static AgentHeartbeatState Unclean(bool recording = true) => new(LastBeat, "boot", CleanShutdown: false, Recording: recording);

    [Fact]
    public void A_clean_stop_or_a_first_run_finds_nothing()
    {
        BootRecoveryClassifier.Classify(null, Now, [], Now).ShouldBeNull();
        BootRecoveryClassifier.Classify(Unclean() with { CleanShutdown = true }, Now, [], Now).ShouldBeNull();
    }

    [Fact]
    public void Without_a_restart_only_the_recorder_stopped()
    {
        var recovery = BootRecoveryClassifier.Classify(Unclean(), LastBeat.AddDays(-2), [], Now).ShouldNotBeNull();

        recovery.Kind.ShouldBe(UncleanStopKind.RecorderStopped);
        recovery.WasRecording.ShouldBeTrue();
        recovery.Summary.ShouldContain("Windows kept running");
    }

    [Fact]
    public void A_bug_check_after_the_heartbeat_is_reported_with_its_code_and_dump()
    {
        var events = new[]
        {
            E(1, EventCategory.Power, "power.os-started"),
            E(1, EventCategory.UnexpectedShutdown, "shutdown.unexpected.bugcheck", new() { ["bugcheck"] = "0x50" }),
            E(1, EventCategory.BugCheck, "bugcheck", new() { ["bugcheck"] = "0x00000050 (0xffffdf04f7f88000, 0x0, 0xfffff8058700e00e, 0x2)", ["dumpFile"] = @"C:\WINDOWS\Minidump\100626-23250-01.dmp" }),
        };

        var recovery = BootRecoveryClassifier.Classify(Unclean(), LastBeat.AddMinutes(1), events, Now).ShouldNotBeNull();

        recovery.Kind.ShouldBe(UncleanStopKind.BugCheck);
        recovery.Details["bugcheck"].ShouldStartWith("0x00000050");
        recovery.Details["dumpFile"].ShouldEndWith("100626-23250-01.dmp");
        recovery.Summary.ShouldStartWith("Windows restarted after a bug check (0x00000050");
    }

    [Fact]
    public void Kernel_power_41_without_a_bug_check_is_an_unexpected_shutdown_worded_as_possibilities()
    {
        var recovery = BootRecoveryClassifier.Classify(Unclean(), LastBeat.AddMinutes(3),
            [E(3, EventCategory.UnexpectedShutdown, "shutdown.unexpected"), E(3, EventCategory.UnexpectedShutdown, "shutdown.unexpected.previous")], Now).ShouldNotBeNull();

        recovery.Kind.ShouldBe(UncleanStopKind.UnexpectedShutdown);
        recovery.Summary.ShouldContain("Possible explanations include");
    }

    [Fact]
    public void A_normal_restart_without_shutdown_errors_is_reported_as_such()
    {
        var recovery = BootRecoveryClassifier.Classify(Unclean(recording: false), LastBeat.AddMinutes(2),
            [E(0, EventCategory.Power, "power.shutdown-requested"), E(1, EventCategory.Power, "power.os-shutdown")], Now).ShouldNotBeNull();

        recovery.Kind.ShouldBe(UncleanStopKind.WindowsRestarted);
        recovery.WasRecording.ShouldBeFalse();
    }

    [Fact]
    public void Fast_startup_restarts_are_recognised_from_the_windows_started_event()
    {
        // Uptime-based boot time is before the heartbeat (the kernel hibernated), but Windows logged a start after it.
        var recovery = BootRecoveryClassifier.Classify(Unclean(), LastBeat.AddDays(-3), [E(5, EventCategory.Power, BootRecoveryClassifier.OsStartedRule)], Now).ShouldNotBeNull();

        recovery.Kind.ShouldBe(UncleanStopKind.WindowsRestarted);
        recovery.BootTime.ShouldBe(LastBeat.AddMinutes(5));
    }

    [Fact]
    public void Events_from_before_the_heartbeat_do_not_count()
    {
        var old = E(-60, EventCategory.BugCheck, "bugcheck", new() { ["bugcheck"] = "0x7e" });

        BootRecoveryClassifier.Classify(Unclean(), LastBeat.AddMinutes(1), [old], Now)!.Kind.ShouldBe(UncleanStopKind.WindowsRestarted);
    }
}

public sealed class HeartbeatServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 5, 21, 30, 0, TimeSpan.Zero));
    private readonly InMemoryHeartbeatStore _store = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly FakePowerEvents _power = new();
    private readonly StaticOptionsMonitor<RecorderOptions> _options = new(new RecorderOptions { Mode = RecordingMode.Always });
    private readonly RecordingController _recorder;

    public HeartbeatServiceTests()
    {
        var bus = EngineFactory.Bus();
        _recorder = new RecordingController(EngineFactory.Scheduler(_time, bus), bus, _sessions, _options, _time, new ElevationInfo(false), _power, NullLogger<RecordingController>.Instance);
    }

    public void Dispose() => _recorder.Dispose();

    private HeartbeatService Service(IBootEvidenceSource evidence) =>
        new(_store, evidence, _recorder, _power, _time, NullLogger<HeartbeatService>.Instance);

    [Fact]
    public async Task An_unclean_previous_run_is_classified_and_stored()
    {
        var lastBeat = _time.GetUtcNow().AddHours(-1);
        _store.State = new AgentHeartbeatState(lastBeat, "b", CleanShutdown: false, Recording: true);
        var bsod = new SystemEvent(lastBeat.AddMinutes(2), "System", 7, "Microsoft-Windows-WER-SystemErrorReporting", 1001, EventLevel.Error,
            EventCategory.BugCheck, Severity.Critical, "bug check", string.Empty, RuleId: "bugcheck", Data: new Dictionary<string, string> { ["bugcheck"] = "0x50" });
        using var service = Service(new FakeBootEvidence(lastBeat.AddMinutes(1), bsod));

        await service.CheckPreviousRunAsync(CancellationToken.None);

        _store.Recoveries.ShouldHaveSingleItem().Kind.ShouldBe(UncleanStopKind.BugCheck);
        service.LastRecovery!.LastHeartbeat.ShouldBe(lastBeat);
    }

    [Fact]
    public async Task Without_readable_events_the_previous_run_is_still_classified()
    {
        _store.State = new AgentHeartbeatState(_time.GetUtcNow().AddHours(-1), "b", CleanShutdown: false, Recording: true);
        using var service = Service(new FakeBootEvidence(_time.GetUtcNow().AddMinutes(-30)) { Fail = true });

        await service.CheckPreviousRunAsync(CancellationToken.None);

        _store.Recoveries.ShouldHaveSingleItem().Kind.ShouldBe(UncleanStopKind.WindowsRestarted);
    }

    [Fact]
    public async Task A_clean_previous_run_records_nothing_and_stopping_marks_a_clean_stop()
    {
        _store.State = new AgentHeartbeatState(_time.GetUtcNow().AddHours(-1), "b", CleanShutdown: true, Recording: false);
        using var service = Service(new FakeBootEvidence(_time.GetUtcNow().AddDays(-1)));

        await service.StartAsync(CancellationToken.None);
        await service.RecoveryChecked.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => _store.Beats > 0);
        _store.State!.CleanShutdown.ShouldBeFalse(); // alive

        await service.StopAsync(CancellationToken.None);

        _store.Recoveries.ShouldBeEmpty();
        _store.State!.CleanShutdown.ShouldBeTrue();
    }

    [Fact]
    public async Task When_windows_ends_the_session_recording_stops_and_the_stop_is_clean()
    {
        using var service = Service(new FakeBootEvidence(_time.GetUtcNow().AddDays(-1)));
        await _recorder.EvaluateAsync(CancellationToken.None);
        _recorder.State.ShouldBe(RecorderState.Recording);

        _power.RaiseSessionEnding();

        _recorder.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.Single().End.ShouldBe(RecordingEndReason.SessionEnd);
        _store.State!.CleanShutdown.ShouldBeTrue();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }
}
