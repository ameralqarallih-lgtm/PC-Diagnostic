using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Engine.Anomalies;

namespace PcDiag.Engine.Tests.Anomalies;

public sealed class AnomalyEngineTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAnomalyStore _store = new();
    private readonly AnomalyTriggerChannel _triggers = new();
    private readonly StaticOptionsMonitor<TriggerOptions> _options = new(new TriggerOptions());
    private readonly AnomalyEngine _engine;

    public AnomalyEngineTests() => _engine = new AnomalyEngine(_store, _triggers, _options, _time, NullLogger<AnomalyEngine>.Instance);

    public void Dispose() => _engine.Dispose();

    private DateTimeOffset T(double seconds) => _time.GetUtcNow().AddSeconds(seconds);

    private async Task FeedAsync(int fromSecond, int toSecond, Func<DateTimeOffset, int, Core.Telemetry.SampleFrame> frame)
    {
        var start = _time.GetUtcNow();
        for (var s = fromSecond; s <= toSecond; s++)
        {
            await _engine.ConsumeAsync(frame(start.AddSeconds(s), s), CancellationToken.None);
        }
    }

    private List<AnomalyTrigger> Queued()
    {
        var list = new List<AnomalyTrigger>();
        while (_triggers.Reader.TryRead(out var trigger))
        {
            list.Add(trigger);
        }

        return list;
    }

    [Fact]
    public async Task High_cpu_must_last_the_rule_duration_and_is_dated_from_when_it_began()
    {
        // 95 %+ for 29 s is not enough…
        await FeedAsync(0, 28, (ts, _) => Frames.At(ts, cpu: 99));
        await FeedAsync(29, 31, (ts, _) => Frames.At(ts, cpu: 40));
        _store.Rows.ShouldBeEmpty();

        // …30 s is.
        await FeedAsync(40, 75, (ts, s) => Frames.At(ts, cpu: s == 60 ? 100 : 97));
        var anomaly = _store.Rows.Values.ShouldHaveSingleItem();
        anomaly.RuleId.ShouldBe("cpu-high");
        anomaly.StartedAt.ShouldBe(T(40));
        anomaly.EndedAt.ShouldBeNull();
        anomaly.Reasoning.Classification.ShouldBe("CPU saturation");
        anomaly.Reasoning.RuleDescription.ShouldBe("CPU utilization above 95 % for at least 30 s");
        anomaly.Reasoning.Duration.ShouldBe(TimeSpan.FromSeconds(30));
        Queued().ShouldBeEmpty("high CPU does not create incidents by default");

        var live = _engine.Current.ShouldHaveSingleItem();
        live.Current.ShouldBe("97 % (peak 100 %)");
        live.DurationSeconds.ShouldBe(35);
        live.Unit.ShouldBe("%");
    }

    [Fact]
    public async Task A_dip_shorter_than_the_clear_time_keeps_one_anomaly_and_a_longer_one_ends_it()
    {
        await FeedAsync(0, 35, (ts, _) => Frames.At(ts, cpu: 99));
        await FeedAsync(36, 38, (ts, _) => Frames.At(ts, cpu: 50));
        await FeedAsync(39, 45, (ts, _) => Frames.At(ts, cpu: 99));
        _store.Rows.Values.ShouldHaveSingleItem().EndedAt.ShouldBeNull();

        await FeedAsync(46, 52, (ts, _) => Frames.At(ts, cpu: 20));

        var ended = _store.Rows.Values.ShouldHaveSingleItem();
        ended.EndedAt.ShouldBe(T(46));
        ended.Reasoning.Duration.ShouldBe(TimeSpan.FromSeconds(46));
        _engine.Current.ShouldHaveSingleItem().EndedAt.ShouldBe(T(46).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_rule_on_one_of_the_systems_readings_is_evaluated_like_a_shipped_metric()
    {
        // "cpu.usage" is the reading the on-screen panels show (here from the frame's CPU sample).
        _options.CurrentValue.Custom.Add(new CustomTriggerRule
        {
            Id = "my-cpu", Name = "CPU flat out", Kind = AnomalyRuleKind.ThresholdDuration, Metric = "reading:cpu.usage", ReadingLabel = "Processor › CPU", ReadingUnit = "%",
            Threshold = 90, DurationSeconds = 5, CreateIncident = true,
        });

        await FeedAsync(0, 6, (ts, _) => Frames.At(ts, cpu: 99));

        var anomaly = _store.Rows.Values.Single(a => a.RuleId == "my-cpu");
        anomaly.Reasoning.RuleDescription.ShouldBe("Processor › CPU above 90 % for at least 5 s");
        Queued().ShouldContain(t => t.Rule.Id == "my-cpu");
    }

    [Fact]
    public async Task Low_memory_creates_an_incident_trigger()
    {
        await FeedAsync(0, 6, (ts, _) => Frames.At(ts, availableGb: 0.6));

        var trigger = Queued().ShouldHaveSingleItem();
        trigger.Rule.Id.ShouldBe("memory-low");
        trigger.Anomaly.Id.ShouldBeGreaterThan(0);
        trigger.Anomaly.Severity.ShouldBe(Severity.High);
        trigger.Anomaly.Reasoning.Current.ShouldBe("0.6 GB");
    }

    [Fact]
    public async Task A_gpu_clock_collapse_under_load_is_reported_once_but_not_when_the_load_drops_too()
    {
        await FeedAsync(0, 10, (ts, _) => Frames.At(ts, gpuUtil: 95, gpuClock: 2100));
        await FeedAsync(11, 14, (ts, _) => Frames.At(ts, gpuUtil: 96, gpuClock: 300));

        var drop = _store.Rows.Values.ShouldHaveSingleItem();
        drop.RuleId.ShouldBe("gpu-clock-drop");
        drop.Device.ShouldBe("NVIDIA GeForce RTX 4090");
        drop.StartedAt.ShouldBe(T(10));
        drop.EndedAt.ShouldBe(T(11));
        drop.Reasoning.Current.ShouldBe("2100 MHz → 300 MHz (−86 % in 1 s); GPU utilization 96 %");
        Queued().ShouldHaveSingleItem().Rule.Id.ShouldBe("gpu-clock-drop");

        // The game goes to its menu: clock and load fall together.
        await FeedAsync(20, 30, (ts, _) => Frames.At(ts, gpuUtil: 95, gpuClock: 2100));
        await FeedAsync(31, 33, (ts, _) => Frames.At(ts, gpuUtil: 5, gpuClock: 300));
        _store.Rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Baseline_deviation_waits_for_its_warmup_and_explains_the_normal_range()
    {
        static double Normal(int s) => 55 + (15 * Math.Sin(s / 7.0));

        // A jump in the first minutes is not "unusual": the baseline is still warming up.
        await FeedAsync(0, 200, (ts, s) => Frames.At(ts, gpuUtil: Normal(s)));
        await FeedAsync(201, 230, (ts, _) => Frames.At(ts, gpuUtil: 99));
        _store.Rows.Values.ShouldNotContain(a => a.RuleId == "gpu-baseline");

        // After half an hour of 40–70 % load, the same jump is.
        await FeedAsync(231, 1800, (ts, s) => Frames.At(ts, gpuUtil: Normal(s)));
        await FeedAsync(1801, 1820, (ts, _) => Frames.At(ts, gpuUtil: 99));

        var anomaly = _store.Rows.Values.Single(a => a.RuleId == "gpu-baseline");
        anomaly.StartedAt.ShouldBe(T(1801));
        anomaly.Reasoning.Classification.ShouldBe("GPU utilization anomaly");
        anomaly.Reasoning.Baseline.ShouldNotBeNull().ShouldContain("30 min");
        anomaly.Reasoning.BaselineMean!.Value.ShouldBeInRange(45, 65);
        anomaly.Reasoning.Current.ShouldStartWith("99 % (z = ");
    }

    [Fact]
    public async Task Network_errors_add_up_over_the_window()
    {
        await FeedAsync(0, 9, (ts, _) => Frames.At(ts, netErrors: 4));
        _store.Rows.ShouldBeEmpty();

        await FeedAsync(10, 14, (ts, _) => Frames.At(ts, netErrors: 20));
        var spike = _store.Rows.Values.ShouldHaveSingleItem();
        spike.RuleId.ShouldBe("network-errors");
        spike.Device.ShouldBe("Ethernet");

        await FeedAsync(15, 30, (ts, _) => Frames.At(ts, netErrors: 0));
        _store.Rows.Values.Single().EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Events_trigger_once_per_subject_and_backfilled_events_are_ignored()
    {
        var now = T(0);
        var tdr = Frames.Event(now, "gpu.tdr", "Display driver nvlddmkm stopped responding and has successfully recovered", provider: "Display");
        var old = Frames.Event(now.AddMinutes(-20), "gpu.tdr", "An old reset read back when recording started", provider: "Display2");
        await _engine.ConsumeAsync(Frames.At(now, events: [tdr, old]), CancellationToken.None);
        await _engine.ConsumeAsync(Frames.At(now.AddSeconds(2), events: [tdr with { Timestamp = now.AddSeconds(2), RecordId = 2 }]), CancellationToken.None);

        var reset = _store.Rows.Values.ShouldHaveSingleItem();
        reset.RuleId.ShouldBe("gpu-reset");
        reset.Series.ShouldBe("event:gpu.tdr");
        reset.Reasoning.Current.ShouldBe("Display driver nvlddmkm stopped responding and has successfully recovered (Event 4101, Display)");
        reset.EndedAt.ShouldBe(reset.StartedAt);
        Queued().ShouldHaveSingleItem().Rule.Id.ShouldBe("gpu-reset");
    }

    [Fact]
    public async Task A_crash_seen_as_a_process_exit_and_a_windows_event_is_one_anomaly()
    {
        var now = T(0);
        var key = new Core.Telemetry.ProcessKey(4242, now.UtcTicks);
        await _engine.ConsumeAsync(Frames.At(now, processEvents: [new(now, key, "game.exe", ProcessEventKind.Crashed, "exit code 0xC0000005")]), CancellationToken.None);
        await _engine.ConsumeAsync(Frames.At(now.AddSeconds(3), events: [Frames.Event(now.AddSeconds(3), "app.crash", "Application Error: game.exe", 1000, "Application Error", "game.exe")]),
            CancellationToken.None);

        var crash = _store.Rows.Values.ShouldHaveSingleItem();
        crash.RuleId.ShouldBe("app-crash");
        crash.Reasoning.Current.ShouldBe("game.exe crashed (exit code 0xC0000005)");
    }

    [Fact]
    public async Task Display_changes_are_recorded_without_creating_incidents_by_default()
    {
        var now = T(0);
        await _engine.ConsumeAsync(Frames.At(now, displayChanges: [new(now, DisplayEventKind.RefreshRateChanged, "DELL U2723QE", "144 Hz", "60 Hz")]), CancellationToken.None);

        _store.Rows.Values.ShouldHaveSingleItem().Reasoning.Current.ShouldBe("DELL U2723QE: refresh rate changed (144 Hz → 60 Hz)");
        Queued().ShouldBeEmpty();
    }

    [Fact]
    public async Task Frames_that_are_not_recorded_are_ignored()
    {
        await FeedAsync(0, 60, (ts, _) => Frames.At(ts, cpu: 100, availableGb: 0.2, recorded: false));

        _store.Rows.ShouldBeEmpty();
        _engine.Current.ShouldBeEmpty();
    }

    [Fact]
    public async Task When_the_data_stops_the_sweep_ends_running_anomalies_where_it_stopped()
    {
        await FeedAsync(0, 40, (ts, _) => Frames.At(ts, cpu: 99));
        _time.Advance(TimeSpan.FromSeconds(60));

        await _engine.SweepAsync(CancellationToken.None);

        _store.Rows.Values.ShouldHaveSingleItem().EndedAt.ShouldBe(T(-20));
    }

    [Fact]
    public async Task Changed_settings_apply_on_the_next_frame()
    {
        var options = new TriggerOptions();
        options.Rules["cpu-high"] = new TriggerRuleSettings { Threshold = 50, DurationSeconds = 5, CreateIncident = true };
        options.Rules["memory-low"] = new TriggerRuleSettings { Enabled = false };
        _options.Set(options);

        await FeedAsync(0, 6, (ts, _) => Frames.At(ts, cpu: 60, availableGb: 0.3));

        _store.Rows.Values.ShouldHaveSingleItem().RuleId.ShouldBe("cpu-high");
        Queued().ShouldHaveSingleItem().Rule.Threshold.ShouldBe(50);
        _engine.Rules.ShouldNotContain(r => r.Id == "memory-low");
    }

    [Fact]
    public async Task A_storage_failure_keeps_detection_running()
    {
        _store.Fail = true;

        await FeedAsync(0, 6, (ts, _) => Frames.At(ts, availableGb: 0.5));

        _engine.Current.ShouldHaveSingleItem().RuleId.ShouldBe("memory-low");
        Queued().ShouldBeEmpty("an anomaly that was not stored cannot be linked to an incident");
    }
}

public sealed class TriggerServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAnomalyStore _store = new();
    private readonly StaticOptionsMonitor<TriggerOptions> _options = new(new TriggerOptions { MaxIncidentsPerHour = 2 });
    private readonly AnomalyEngine _engine;
    private readonly FakeCapture _capture;
    private readonly TriggerService _service;

    public TriggerServiceTests()
    {
        _engine = new AnomalyEngine(_store, new AnomalyTriggerChannel(), _options, _time, NullLogger<AnomalyEngine>.Instance);
        _capture = new FakeCapture(_time);
        _service = new TriggerService(new AnomalyTriggerChannel(), _engine, _capture, _options, _time, NullLogger<TriggerService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _engine.Dispose();
    }

    private async Task<AnomalyTrigger> TriggerAsync(string ruleId, string current = "0.4 GB")
    {
        var rule = AnomalyRuleCatalog.Default.Find(ruleId)!;
        var anomaly = new Anomaly(0, rule.Id, rule.Metric ?? "event:x", _time.GetUtcNow(), null, 0.4, rule.Severity,
            new AnomalyReasoning(rule.Classification, rule.Describe(), null, null, null, current, TimeSpan.FromSeconds(5)));
        var id = await _store.AddAsync(anomaly, CancellationToken.None);
        return new AnomalyTrigger(anomaly with { Id = id }, rule);
    }

    [Fact]
    public async Task A_trigger_creates_an_incident_titled_with_what_was_observed()
    {
        var outcome = await _service.HandleAsync(await TriggerAsync("memory-low"), CancellationToken.None);

        outcome.ShouldBe(TriggerService.Outcome.Created);
        var trigger = _capture.Triggers.ShouldHaveSingleItem();
        trigger.Source.ShouldBe(IncidentSource.Rule);
        trigger.RuleId.ShouldBe("memory-low");
        trigger.Severity.ShouldBe(Severity.High);
        trigger.Detail.ShouldBe("Low available memory: 0.4 GB");
        _store.Links.ShouldHaveSingleItem().Key.ShouldBe("INC-20261006-00001");
    }

    [Fact]
    public async Task A_rule_with_a_long_duration_creates_an_incident_when_it_fires()
    {
        // VRAM above 90 % for 5 minutes: the anomaly started 5 minutes before the rule fired (seen live on 15 Oct 2026).
        var rule = AnomalyRuleCatalog.Default.Find("gpu-vram-high-sustained")!;
        var started = _time.GetUtcNow().AddMinutes(-5);
        var anomaly = new Anomaly(0, rule.Id, rule.Metric!, started, null, 96, rule.Severity,
            new AnomalyReasoning(rule.Classification, rule.Describe(), null, null, null, "96 %", TimeSpan.FromMinutes(5)));
        var id = await _store.AddAsync(anomaly, CancellationToken.None);

        var outcome = await _service.HandleAsync(new AnomalyTrigger(anomaly with { Id = id }, rule, _time.GetUtcNow()), CancellationToken.None);

        outcome.ShouldBe(TriggerService.Outcome.Created);
    }

    [Fact]
    public async Task Event_rules_create_event_incidents()
    {
        await _service.HandleAsync(await TriggerAsync("gpu-reset", "Display driver nvlddmkm stopped responding (Event 4101, Display)"), CancellationToken.None);

        _capture.Triggers.ShouldHaveSingleItem().Source.ShouldBe(IncidentSource.Event);
    }

    [Fact]
    public async Task The_cooldown_and_the_hourly_limit_hold_back_further_incidents()
    {
        (await _service.HandleAsync(await TriggerAsync("memory-low"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Created);
        _time.Advance(TimeSpan.FromMinutes(5));
        (await _service.HandleAsync(await TriggerAsync("memory-low"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Cooldown);
        (await _service.HandleAsync(await TriggerAsync("gpu-reset"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Created);
        _time.Advance(TimeSpan.FromMinutes(5));
        (await _service.HandleAsync(await TriggerAsync("app-crash"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Limit);

        _time.Advance(TimeSpan.FromHours(1));
        (await _service.HandleAsync(await TriggerAsync("app-crash"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Created);
        _capture.Triggers.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_trigger_during_a_capture_joins_it_even_at_the_hourly_limit()
    {
        _options.Set(new TriggerOptions { MaxIncidentsPerHour = 1 });
        await _service.HandleAsync(await TriggerAsync("memory-low"), CancellationToken.None);
        _capture.Active = new Incident(1, IncidentKey.Create(new DateOnly(2026, 10, 6), 1), _time.GetUtcNow(), _time.GetUtcNow(), TimeSpan.Zero, TimeSpan.FromMinutes(2),
            IncidentState.Recording, IncidentSource.Rule, null, "x", null, Severity.High);
        _capture.ActiveUntil = _time.GetUtcNow().AddMinutes(2);

        (await _service.HandleAsync(await TriggerAsync("gpu-reset"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Joined);
    }

    [Fact]
    public async Task Nothing_is_captured_when_automatic_incidents_are_off_or_the_anomaly_is_old()
    {
        _options.Set(new TriggerOptions { AutomaticIncidents = false });
        (await _service.HandleAsync(await TriggerAsync("memory-low"), CancellationToken.None)).ShouldBe(TriggerService.Outcome.Off);

        _options.Set(new TriggerOptions());
        var old = await TriggerAsync("gpu-reset");
        _time.Advance(TimeSpan.FromMinutes(3));
        (await _service.HandleAsync(old, CancellationToken.None)).ShouldBe(TriggerService.Outcome.Stale);
        _capture.Triggers.ShouldBeEmpty();
    }

    [Fact]
    public void Long_titles_are_shortened()
    {
        var rule = AnomalyRuleCatalog.Default.Find("app-crash")!;
        var anomaly = new Anomaly(1, rule.Id, "event:app.crash", _time.GetUtcNow(), null, 1000, rule.Severity,
            new AnomalyReasoning(rule.Classification, rule.Describe(), null, null, null, new string('x', 300), TimeSpan.Zero), "game.exe");

        var title = TriggerService.TitleFor(new AnomalyTrigger(anomaly, rule));

        title.Length.ShouldBe(TriggerService.MaxTitleLength);
        title.ShouldStartWith("Application crash: xxx");
        title.ShouldEndWith("…");
    }
}
