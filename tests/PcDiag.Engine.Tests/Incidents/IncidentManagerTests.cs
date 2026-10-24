using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Diagnostics;
using PcDiag.Engine.Incidents;
using PcDiag.Engine.Ipc;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Tests.Incidents;

internal sealed class RecordingIncidentStore : IIncidentStore
{
    private long _next;

    public List<Incident> Created { get; } = [];

    public List<IncidentDraft> Drafts { get; } = [];

    public List<(long Incident, IncidentTrigger Trigger)> Triggers { get; } = [];

    public List<(IncidentKey Key, DateTimeOffset From, DateTimeOffset To, int Frames)> Windows { get; } = [];

    public List<(IncidentKey Key, IncidentState State, BootRecovery? Recovery)> Finalized { get; } = [];

    public int Snapshots { get; private set; }

    public List<Incident> Unfinished { get; } = [];

    public List<PendingBootRecovery> Pending { get; } = [];

    public List<(long Recovery, long? Incident)> Marked { get; } = [];

    public Task<Incident> CreateAsync(IncidentDraft draft, CancellationToken cancellationToken)
    {
        var id = ++_next;
        var incident = new Incident(id, IncidentKey.Create(DateOnly.FromDateTime(draft.CreatedAt.UtcDateTime), (int)id), draft.CreatedAt, draft.CenterAt, draft.PreWindow,
            draft.PostWindow, IncidentState.Recording, draft.Source, draft.UserType, draft.Title, draft.Notes, draft.Severity);
        Created.Add(incident);
        Drafts.Add(draft);
        Triggers.Add((id, draft.Trigger));
        return Task.FromResult(incident);
    }

    public Task AddTriggerAsync(long incidentId, IncidentTrigger trigger, CancellationToken cancellationToken)
    {
        Triggers.Add((incidentId, trigger));
        return Task.CompletedTask;
    }

    public Task CaptureWindowAsync(Incident incident, DateTimeOffset windowStart, DateTimeOffset windowEnd, IReadOnlyList<SampleFrame> frames, CancellationToken cancellationToken)
    {
        Windows.Add((incident.Key, windowStart, windowEnd, frames.Count));
        return Task.CompletedTask;
    }

    public Task SaveSnapshotsAsync(Incident incident, DisplaySnapshot? display, ServiceSnapshot? services, CancellationToken cancellationToken)
    {
        Snapshots++;
        return Task.CompletedTask;
    }

    public Task FinalizeAsync(Incident incident, IncidentState state, BootRecovery? recovery, CancellationToken cancellationToken)
    {
        Finalized.Add((incident.Key, state, recovery));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Incident>> ListUnfinishedAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Incident>>(Unfinished);

    public Task<IReadOnlyList<PendingBootRecovery>> PendingBootRecoveriesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PendingBootRecovery>>(Pending);

    public Task MarkBootRecoveryAsync(long recoveryId, long? incidentId, CancellationToken cancellationToken)
    {
        Marked.Add((recoveryId, incidentId));
        return Task.CompletedTask;
    }

    public List<(IncidentKey Key, IncidentUpdate Update)> Updates { get; } = [];

    public List<IncidentKey> Deleted { get; } = [];

    public Task<bool> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken)
    {
        var exists = Created.Any(i => i.Key == key);
        if (exists)
        {
            Updates.Add((key, update));
        }

        return Task.FromResult(exists);
    }

    public Task<bool> DeleteAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        var exists = Created.Any(i => i.Key == key) && !Deleted.Contains(key);
        if (exists)
        {
            Deleted.Add(key);
        }

        return Task.FromResult(exists);
    }

    public List<(IncidentKey Key, IReadOnlyList<Finding> Findings, int Version)> Findings { get; } = [];

    public List<IncidentKey> Unanalyzed { get; } = [];

    public Task<bool> SaveFindingsAsync(IncidentKey key, IReadOnlyList<Finding> findings, int analyzerVersion, CancellationToken cancellationToken)
    {
        Findings.Add((key, findings, analyzerVersion));
        Unanalyzed.Remove(key);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<IncidentKey>> ListUnanalyzedAsync(int analyzerVersion, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<IncidentKey>>(Unanalyzed.Take(limit).ToList());
}

/// <summary>Evidence for any incident the store created (an empty window around its center), or for listed keys.</summary>
internal sealed class FakeEvidenceSource(RecordingIncidentStore store) : IIncidentEvidenceSource
{
    public Task<IncidentEvidence?> LoadAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        var center = store.Created.FirstOrDefault(i => i.Key == key)?.CenterAt ?? DateTimeOffset.UnixEpoch;
        return Task.FromResult<IncidentEvidence?>(new IncidentEvidence(key, center, center.AddMinutes(-10), center.AddMinutes(2), IncidentSource.User, null, false, [], [], [], []));
    }

    public static IncidentDiagnostics Diagnostics(RecordingIncidentStore store, TimeProvider time) =>
        new(new FakeEvidenceSource(store), store, new StaticOptionsMonitor<TriggerOptions>(new TriggerOptions()), time, NullLogger<IncidentDiagnostics>.Instance);
}

public sealed class IncidentManagerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingIncidentStore _store = new();
    private readonly InMemorySessionStore _sessions = new();
    private readonly StaticOptionsMonitor<RecorderOptions> _options = new(new RecorderOptions { PreIncidentMinutes = 10, PostIncidentSeconds = 120 });
    private readonly RecordingController _recorder;
    private readonly HeartbeatService _heartbeat;
    private readonly RingBuffer _ring;
    private readonly IncidentManager _manager;

    public IncidentManagerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var bus = EngineFactory.Bus();
        var power = new FakePowerEvents();
        _recorder = new RecordingController(EngineFactory.Scheduler(_time, bus), bus, _sessions, _options, _time, new ElevationInfo(false), power, NullLogger<RecordingController>.Instance);
        _heartbeat = new HeartbeatService(new InMemoryHeartbeatStore(), new FakeBootEvidence(_time.GetUtcNow().AddHours(-1)), _recorder, power, _time, NullLogger<HeartbeatService>.Instance);
        _ring = new RingBuffer(_options, new StaticOptionsMonitor<SamplingOptions>(new SamplingOptions()), NullLogger<RingBuffer>.Instance);
        _manager = new IncidentManager(_store, _ring, bus, _recorder, _heartbeat, FakeEvidenceSource.Diagnostics(_store, _time), [], [], _options, _time, NullLogger<IncidentManager>.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _heartbeat.Dispose();
        _recorder.Dispose();
    }

    private static IncidentTrigger Hotkey(UserIncidentType? type = null) => new(DateTimeOffset.MinValue, IncidentSource.Hotkey, type);

    private void FillRing(TimeSpan span)
    {
        for (var s = (int)span.TotalSeconds; s >= 0; s--)
        {
            var at = _time.GetUtcNow().AddSeconds(-s);
            _ring.Append(new SampleFrame(1000 - s, at));
        }
    }

    [Fact]
    public async Task A_capture_while_recording_saves_the_pre_window_before_returning()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        FillRing(TimeSpan.FromMinutes(11));

        var outcome = await _manager.CaptureAsync(Hotkey(UserIncidentType.ScreenFlicker), CancellationToken.None);

        outcome.Merged.ShouldBeFalse();
        outcome.SnapshotOnly.ShouldBeFalse();
        outcome.Incident.Title.ShouldBe("Screen Flicker");
        outcome.Incident.CenterAt.ShouldBe(_time.GetUtcNow());
        outcome.RecordingUntil.ShouldBe(_time.GetUtcNow().AddMinutes(2));
        var window = _store.Windows.ShouldHaveSingleItem();
        (window.From, window.To).ShouldBe((_time.GetUtcNow().AddMinutes(-10), _time.GetUtcNow()));
        window.Frames.ShouldBe(601); // 10 minutes of 1 s frames from the ring, both ends included
        _store.Snapshots.ShouldBe(1);
        _manager.Active.ShouldBe(outcome.Incident);
    }

    [Fact]
    public async Task An_incident_of_a_custom_recording_keeps_its_selection_and_a_capture_while_idle_records_everything()
    {
        var custom = RecordingSelection.Of([RecordingItems.CpuUsage, RecordingItems.Gpu("gpu-a", "usage")]);
        await _recorder.StartRecordingByUserAsync(custom, CancellationToken.None);
        FillRing(TimeSpan.FromMinutes(1));

        await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        _store.Drafts.Single().Selection.ShouldBeSameAs(custom);

        _time.Advance(TimeSpan.FromMinutes(5));
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);
        await _recorder.StopRecordingByUserAsync(CancellationToken.None);
        await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        _store.Drafts[^1].Selection.ShouldBeNull("a capture that starts the recorder records everything");
    }

    [Fact]
    public async Task Captures_during_the_post_window_merge_into_the_same_incident()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        var first = await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(90));

        var second = await _manager.CaptureAsync(new IncidentTrigger(DateTimeOffset.MinValue, IncidentSource.Tray, UserIncidentType.SystemFreeze), CancellationToken.None);

        second.Merged.ShouldBeTrue();
        second.Incident.Key.ShouldBe(first.Incident.Key);
        _store.Created.Count.ShouldBe(1);
        _store.Triggers.Select(t => t.Trigger.Source).ShouldBe([IncidentSource.Hotkey, IncidentSource.Tray]);
        _store.Triggers[1].Trigger.Timestamp.ShouldBe(_time.GetUtcNow()); // stamped by the Agent, not the caller
    }

    [Fact]
    public async Task The_incident_is_finished_after_the_post_window_and_a_settle_delay()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        var captured = await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        IncidentCapturedEventArgs? finished = null;
        _manager.Finished += (_, e) => finished = e;

        _time.Advance(TimeSpan.FromSeconds(120) + IncidentManager.FinalizeDelay - TimeSpan.FromSeconds(1));
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);
        _store.Finalized.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromSeconds(1));
        FillRing(TimeSpan.FromSeconds(130));
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);

        _store.Windows[^1].ShouldBe((captured.Incident.Key, captured.Incident.CenterAt, captured.Incident.CenterAt.AddMinutes(2), 121));
        _store.Finalized.ShouldHaveSingleItem().State.ShouldBe(IncidentState.Complete);
        finished!.Outcome.Incident.State.ShouldBe(IncidentState.Complete);
        _manager.Active.ShouldBeNull();
        _recorder.State.ShouldBe(RecorderState.Recording); // the user's recording continues

        // The diagnostic summary is written once the incident is finished, ending with what is not proven.
        var (key, findings, version) = _store.Findings.ShouldHaveSingleItem();
        key.ShouldBe(captured.Incident.Key);
        version.ShouldBe(IncidentDiagnostics.AnalyzerVersion);
        findings[^1].Level.ShouldBe(EvidenceLevel.NotProven);
    }

    [Fact]
    public async Task The_post_window_is_saved_while_it_records_so_a_restart_keeps_every_process()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        var captured = await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        var center = captured.Incident.CenterAt;

        _time.Advance(IncidentManager.ProgressInterval - TimeSpan.FromSeconds(1));
        await _manager.SaveProgressAsync(CancellationToken.None);
        _store.Windows.Count.ShouldBe(1); // only the pre-window so far

        _time.Advance(TimeSpan.FromSeconds(1));
        FillRing(IncidentManager.ProgressInterval);
        await _manager.SaveProgressAsync(CancellationToken.None);
        _store.Windows[^1].ShouldBe((captured.Incident.Key, center, center + IncidentManager.ProgressInterval, 11));

        // The next save continues where this one ended.
        _time.Advance(IncidentManager.ProgressInterval);
        FillRing(IncidentManager.ProgressInterval);
        await _manager.SaveProgressAsync(CancellationToken.None);
        (_store.Windows[^1].From, _store.Windows[^1].To).ShouldBe((center + IncidentManager.ProgressInterval, center + (2 * IncidentManager.ProgressInterval)));

        // Past the post-window, finishing the incident takes over.
        _time.Advance(TimeSpan.FromMinutes(2));
        var saves = _store.Windows.Count;
        await _manager.SaveProgressAsync(CancellationToken.None);
        _store.Windows.Count.ShouldBe(saves);
    }

    [Fact]
    public async Task At_startup_incidents_analyzed_by_an_older_version_are_analyzed_again()
    {
        var old = IncidentKey.Create(new DateOnly(2026, 10, 1), 3);
        _store.Unanalyzed.Add(old);

        await _manager.ReanalyzeAsync(CancellationToken.None);

        _store.Findings.ShouldHaveSingleItem().Key.ShouldBe(old);
        _store.Unanalyzed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_problem_found_while_watching_keeps_the_watched_minutes_and_starts_recording()
    {
        // Watched while not recording (in memory only), as the recorder does when stopped.
        for (var sec = 11 * 60; sec >= 0; sec--)
        {
            _ring.Append(new SampleFrame(1000 - sec, _time.GetUtcNow().AddSeconds(-sec), Recorded: false, Watched: true));
        }

        var outcome = await _manager.CaptureAsync(Hotkey(), CancellationToken.None);

        outcome.SnapshotOnly.ShouldBeFalse();
        outcome.Incident.PreWindow.ShouldBe(TimeSpan.FromMinutes(10));
        _store.Windows.ShouldHaveSingleItem().Frames.ShouldBe(601);
        _recorder.State.ShouldBe(RecorderState.Recording);
        _sessions.Sessions.Single().Start.ShouldBe(RecordingStartReason.Capture);
    }

    [Fact]
    public async Task A_capture_while_idle_records_temporarily_and_is_snapshot_only()
    {
        var outcome = await _manager.CaptureAsync(Hotkey(), CancellationToken.None);

        outcome.SnapshotOnly.ShouldBeTrue();
        outcome.Incident.PreWindow.ShouldBe(TimeSpan.Zero);
        _store.Windows.ShouldBeEmpty(); // nothing from before to save
        _recorder.State.ShouldBe(RecorderState.Recording);
        _sessions.Sessions.Single().Start.ShouldBe(RecordingStartReason.Capture);

        _time.Advance(TimeSpan.FromSeconds(120) + IncidentManager.FinalizeDelay);
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);

        _recorder.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.Single().End.ShouldBe(RecordingEndReason.CaptureComplete);
    }

    [Fact]
    public async Task While_the_drive_is_almost_full_no_new_incident_is_written()
    {
        var guard = new PcDiag.Core.Storage.StorageGuard();
        using var manager = new IncidentManager(_store, _ring, EngineFactory.Bus(), _recorder, _heartbeat, FakeEvidenceSource.Diagnostics(_store, _time), [], [], _options, _time,
            NullLogger<IncidentManager>.Instance, guard: guard);
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        guard.Set(true);

        var refused = await Should.ThrowAsync<PcDiag.Core.Storage.StorageFullException>(() => manager.CaptureAsync(Hotkey(), CancellationToken.None));

        refused.Message.ShouldContain("less than 1 GB free");
        _store.Windows.ShouldBeEmpty();
        guard.Set(false);
        (await manager.CaptureAsync(Hotkey(), CancellationToken.None)).Merged.ShouldBeFalse();
    }

    [Fact]
    public async Task A_stop_during_a_capture_takes_effect_once_the_post_window_is_recorded()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(20));

        await _recorder.StopRecordingByUserAsync(CancellationToken.None);

        _recorder.State.ShouldBe(RecorderState.Recording); // the incident still needs 100 s
        _time.Advance(TimeSpan.FromSeconds(100) + IncidentManager.FinalizeDelay);
        FillRing(TimeSpan.FromSeconds(130));
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);

        _store.Finalized.ShouldHaveSingleItem().State.ShouldBe(IncidentState.Complete);
        _recorder.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.Single().End.ShouldBe(RecordingEndReason.User);
    }

    [Fact]
    public async Task A_pause_during_a_capture_starts_once_the_post_window_is_recorded()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        await _manager.CaptureAsync(Hotkey(), CancellationToken.None);

        await _recorder.PauseAsync(TimeSpan.FromMinutes(30), CancellationToken.None);

        _recorder.State.ShouldBe(RecorderState.Recording);
        _time.Advance(TimeSpan.FromSeconds(120) + IncidentManager.FinalizeDelay);
        FillRing(TimeSpan.FromSeconds(130));
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);

        _recorder.State.ShouldBe(RecorderState.Paused);
        _recorder.PausedUntil.ShouldNotBeNull();
        _sessions.Sessions.Single().End.ShouldBe(RecordingEndReason.Pause);
    }

    [Fact]
    public async Task Resuming_before_the_capture_ends_keeps_the_user_recording()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        await _recorder.PauseAsync(null, CancellationToken.None);

        await _recorder.ResumeAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(120) + IncidentManager.FinalizeDelay);
        FillRing(TimeSpan.FromSeconds(130));
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);

        _recorder.State.ShouldBe(RecorderState.Recording);
        _sessions.Sessions.Single().End.ShouldBeNull();
    }

    [Fact]
    public async Task A_post_window_the_recorder_did_not_record_is_partial()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));
        FillRing(TimeSpan.FromSeconds(30)); // then the recorder stopped (e.g. the computer went to sleep)

        _time.Advance(TimeSpan.FromSeconds(90) + IncidentManager.FinalizeDelay);
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);

        _store.Finalized.ShouldHaveSingleItem().State.ShouldBe(IncidentState.Partial);
    }

    [Fact]
    public async Task Stopping_the_agent_mid_window_finishes_the_incident_as_partial()
    {
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        var captured = await _manager.CaptureAsync(Hotkey(), CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));

        await _manager.FinalizeDueAsync(force: true, CancellationToken.None);

        _store.Finalized.ShouldHaveSingleItem().State.ShouldBe(IncidentState.Partial);
        _store.Windows[^1].To.ShouldBe(captured.Incident.CenterAt.AddSeconds(30));
    }

    [Fact]
    public async Task At_startup_unfinished_incidents_are_finished_and_crashes_become_incidents()
    {
        var lastBeat = _time.GetUtcNow().AddMinutes(-20);
        var unfinished = new Incident(9, IncidentKey.Create(new DateOnly(2026, 10, 5), 9), lastBeat.AddMinutes(-5), lastBeat.AddMinutes(-5), TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(2), IncidentState.Recording, IncidentSource.Hotkey, null, "Captured with the hotkey", null, Severity.Medium);
        _store.Unfinished.Add(unfinished);
        var bsod = new BootRecovery(_time.GetUtcNow(), lastBeat, lastBeat.AddMinutes(1), UncleanStopKind.BugCheck, true, "Windows restarted after a bug check (0x00000050 (…)).",
            new Dictionary<string, string> { ["bugcheck"] = "0x00000050 (0xffff, 0x0)" });
        var restart = bsod with { Kind = UncleanStopKind.WindowsRestarted };
        var idleCrash = bsod with { WasRecording = false };
        _store.Pending.AddRange([new PendingBootRecovery(1, bsod), new PendingBootRecovery(2, restart), new PendingBootRecovery(3, idleCrash)]);
        await _heartbeat.CheckPreviousRunAsync(CancellationToken.None);

        await _manager.RecoverAsync(CancellationToken.None);

        _store.Finalized[0].ShouldBe((unfinished.Key, IncidentState.Partial, (BootRecovery?)null));
        var created = _store.Created.ShouldHaveSingleItem();
        created.Title.ShouldBe("Blue screen (bug check 0x00000050)");
        created.Source.ShouldBe(IncidentSource.BootRecovery);
        created.Severity.ShouldBe(Severity.Critical);
        created.CenterAt.ShouldBe(lastBeat);
        _store.Windows[^1].From.ShouldBe(lastBeat.AddMinutes(-10));
        _store.Finalized[1].Recovery.ShouldBe(bsod);
        _store.Marked.ShouldBe([(1L, (long?)created.Id), (2L, null), (3L, null)]);
    }

    [Theory]
    [InlineData(null, IncidentSource.Hotkey, "Captured with the hotkey")]
    [InlineData(null, IncidentSource.Tray, "Captured from the tray")]
    [InlineData(UserIncidentType.GameCrash, IncidentSource.Tray, "Game Crash")]
    public void Titles_follow_the_type_or_source(UserIncidentType? type, IncidentSource source, string title) =>
        IncidentManager.TitleFor(new IncidentTrigger(DateTimeOffset.MinValue, source, type)).ShouldBe(title);
}

public sealed class AgentCommandHandlerTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly RecordingIncidentStore _store = new();
    private readonly StaticOptionsMonitor<RecorderOptions> _options = new(new RecorderOptions());
    private readonly RecordingController _recorder;
    private readonly HeartbeatService _heartbeat;
    private readonly IncidentManager _manager;
    private readonly AgentCommandHandler _handler;

    public AgentCommandHandlerTests()
    {
        var bus = EngineFactory.Bus();
        var power = new FakePowerEvents();
        _recorder = new RecordingController(EngineFactory.Scheduler(_time, bus), bus, new InMemorySessionStore(), _options, _time, new ElevationInfo(true), power, NullLogger<RecordingController>.Instance);
        _heartbeat = new HeartbeatService(new InMemoryHeartbeatStore(), new FakeBootEvidence(_time.GetUtcNow()), _recorder, power, _time, NullLogger<HeartbeatService>.Instance);
        var ring = new RingBuffer(_options, new StaticOptionsMonitor<SamplingOptions>(new SamplingOptions()), NullLogger<RingBuffer>.Instance);
        _manager = new IncidentManager(_store, ring, bus, _recorder, _heartbeat, FakeEvidenceSource.Diagnostics(_store, _time), [], [], _options, _time, NullLogger<IncidentManager>.Instance);
        _handler = new AgentCommandHandler(_recorder, _manager, [], [], _options, new ElevationInfo(true), _time, NullLogger<AgentCommandHandler>.Instance);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _heartbeat.Dispose();
        _recorder.Dispose();
    }

    private Task<AgentResponse> Send(string command, AgentRequestArgs? args = null, int version = AgentIpc.ProtocolVersion, string id = "1") =>
        _handler.HandleAsync(new AgentRequest(version, id, command, args), CancellationToken.None);

    [Fact]
    public async Task Commands_control_the_recorder_and_report_status()
    {
        (await Send(AgentCommands.Start)).Status!.State.ShouldBe(RecorderState.Recording);
        var paused = await Send(AgentCommands.Pause, new AgentRequestArgs(PauseMinutes: 15));
        paused.Status!.State.ShouldBe(RecorderState.Paused);
        paused.Status.PausedUntil.ShouldBe(_time.GetUtcNow().AddMinutes(15));
        (await Send(AgentCommands.Resume)).Status!.State.ShouldBe(RecorderState.Recording);
        var stopped = await Send(AgentCommands.Stop);
        stopped.Ok.ShouldBeTrue();
        stopped.Status!.State.ShouldBe(RecorderState.Idle);
        stopped.Status.Elevated.ShouldBeTrue();
    }

    [Fact]
    public async Task A_custom_recording_starts_with_its_selection_and_the_catalog_answers()
    {
        var started = await Send(AgentCommands.Start, new AgentRequestArgs(Selection: [RecordingItems.CpuUsage, RecordingItems.ProcessMetrics]));
        started.Status!.RecordingItems.ShouldBe(2);
        _recorder.Selection!.Items.ShouldBe([RecordingItems.CpuUsage, RecordingItems.ProcessMetrics]);

        (await Send(AgentCommands.Start, new AgentRequestArgs(Selection: []))).Error!.ShouldContain("1–2000 items");
        (await Send(AgentCommands.Start, new AgentRequestArgs(Selection: [new string('x', AgentCommandHandler.MaxSelectionItemLength + 1)]))).Ok.ShouldBeFalse();
        (await Send(AgentCommands.Start)).Status!.RecordingItems.ShouldBeNull("no selection records everything");

        var catalog = (await Send(AgentCommands.Catalog)).Catalog!;
        catalog.Sources.Select(s => s.Id).ShouldContain("cpu");
        catalog.Sources.Select(s => s.Id).ShouldContain("events");
    }

    [Fact]
    public async Task Capture_returns_the_incident_key()
    {
        var response = await Send(AgentCommands.Capture, new AgentRequestArgs(IncidentType: UserIncidentType.HighGpu, Note: "fans loud"));

        response.Ok.ShouldBeTrue();
        response.Capture!.IncidentKey.ShouldBe(_store.Created.Single().Key.ToString());
        response.Capture.SnapshotOnly.ShouldBeTrue();
        response.Status!.ActiveIncident.ShouldBe(response.Capture.IncidentKey);
        _store.Triggers.Single().Trigger.Note.ShouldBe("fans loud");
    }

    [Theory]
    [InlineData(2, "1", AgentCommands.Status, "Unsupported protocol version")]
    [InlineData(1, "", AgentCommands.Status, "request ID")]
    [InlineData(1, "1", "format-disk", "Unknown command")]
    public async Task Invalid_requests_are_refused_before_doing_anything(int version, string id, string command, string error)
    {
        var response = await Send(command, null, version, id);

        response.Ok.ShouldBeFalse();
        response.Error!.ShouldContain(error);
        _recorder.State.ShouldBe(RecorderState.Idle);
    }

    [Fact]
    public async Task Argument_ranges_are_enforced()
    {
        (await Send(AgentCommands.Pause, new AgentRequestArgs(PauseMinutes: 0))).Error!.ShouldContain("1–1440");
        (await Send(AgentCommands.Capture, new AgentRequestArgs(Note: new string('x', AgentCommandHandler.MaxNoteLength + 1)))).Ok.ShouldBeFalse();
        (await Send(AgentCommands.Capture, new AgentRequestArgs(IncidentType: (UserIncidentType)99))).Error!.ShouldContain("incident type");
        (await Send(AgentCommands.Trend, new AgentRequestArgs(TrendMinutes: 11))).Error!.ShouldContain("1–10");
        _store.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Incidents_can_be_edited_and_deleted_but_not_while_recording()
    {
        var key = (await Send(AgentCommands.Capture)).Capture!.IncidentKey;

        var edited = await Send(AgentCommands.UpdateIncident, new AgentRequestArgs(IncidentKey: key, Title: "  Flicker on the left monitor ", Note: "after driver update", Pinned: true));
        edited.Ok.ShouldBeTrue();
        _store.Updates.Single().Update.ShouldBe(new IncidentUpdate("Flicker on the left monitor", "after driver update", null, true));
        _manager.Active!.Title.ShouldBe("Flicker on the left monitor");

        var refused = await Send(AgentCommands.DeleteIncident, new AgentRequestArgs(IncidentKey: key));
        refused.Ok.ShouldBeFalse();
        refused.Error!.ShouldContain("still recording");

        _time.Advance(TimeSpan.FromSeconds(120) + IncidentManager.FinalizeDelay);
        await _manager.FinalizeDueAsync(force: false, CancellationToken.None);
        (await Send(AgentCommands.DeleteIncident, new AgentRequestArgs(IncidentKey: key))).Ok.ShouldBeTrue();
        (await Send(AgentCommands.DeleteIncident, new AgentRequestArgs(IncidentKey: key))).Error!.ShouldContain("No such incident");
    }

    [Theory]
    [InlineData(null, "key is missing")]
    [InlineData("../../Windows", "Not an incident key")]
    public async Task Incident_edits_need_a_valid_key(string? key, string error)
    {
        var response = await Send(AgentCommands.DeleteIncident, new AgentRequestArgs(IncidentKey: key));

        response.Ok.ShouldBeFalse();
        response.Error!.ShouldContain(error);
    }

    [Fact]
    public async Task Live_requests_report_the_live_view_in_the_status()
    {
        var live = await Send(AgentCommands.Live);

        live.Ok.ShouldBeTrue();
        live.Status!.LiveView.ShouldBeTrue(); // idle recorder: the dashboard gets an in-memory live view
        live.Status.State.ShouldBe(RecorderState.Idle);
        await _recorder.StopAsync(CancellationToken.None);
    }
}
