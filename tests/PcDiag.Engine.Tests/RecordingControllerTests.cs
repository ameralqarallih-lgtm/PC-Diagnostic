using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Tests;

public sealed class RecordingControllerTests : IDisposable
{
    // Monday 2026-09-28 08:00 local (UTC in these tests).
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _sessions = new();
    private readonly StaticOptionsMonitor<RecorderOptions> _options = new(new RecorderOptions());
    private readonly FakePowerEvents _power = new();
    private readonly RecordingController _controller;

    public RecordingControllerTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var bus = EngineFactory.Bus();
        var scheduler = EngineFactory.Scheduler(_time, bus);
        _controller = new RecordingController(scheduler, bus, _sessions, _options, _time, new ElevationInfo(false), _power, NullLogger<RecordingController>.Instance);
    }

    public void Dispose() => _controller.Dispose();

    [Fact]
    public async Task Manual_mode_is_idle_until_the_user_starts_recording()
    {
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Idle);

        await _controller.StartRecordingByUserAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording);

        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording); // evaluation keeps a user-started recording

        await _controller.StopRecordingByUserAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.ShouldBe([(1L, RecordingStartReason.User, (RecordingEndReason?)RecordingEndReason.User)]);
    }

    [Fact]
    public async Task Always_mode_records_until_the_user_stops_it()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording);

        await _controller.StopRecordingByUserAsync(CancellationToken.None);
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Idle); // the user's stop is respected

        _sessions.Sessions.Single().Start.ShouldBe(RecordingStartReason.Always);
    }

    [Fact]
    public async Task Scheduled_mode_follows_the_windows()
    {
        _options.CurrentValue = new RecorderOptions
        {
            Mode = RecordingMode.Scheduled,
            Schedule = [new RecordingWindow { Days = [DayOfWeek.Monday], Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0) }],
        };

        await _controller.EvaluateAsync(CancellationToken.None); // 08:00
        _controller.State.ShouldBe(RecorderState.Idle);

        _time.Advance(TimeSpan.FromHours(1)); // 09:00
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording);

        _time.Advance(TimeSpan.FromHours(1)); // 10:00
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Idle);

        _sessions.Sessions.ShouldBe([(1L, RecordingStartReason.Schedule, (RecordingEndReason?)RecordingEndReason.Schedule)]);
    }

    [Fact]
    public async Task A_custom_recording_keeps_its_selection_and_another_choice_starts_a_new_session()
    {
        var custom = RecordingSelection.Of([RecordingItems.CpuUsage, RecordingItems.ProcessMetrics]);

        await _controller.StartRecordingByUserAsync(custom, CancellationToken.None);
        _controller.Selection.ShouldBeSameAs(custom);

        await _controller.EvaluateAsync(CancellationToken.None);
        await _controller.StartRecordingByUserAsync(RecordingSelection.Of([RecordingItems.ProcessMetrics, RecordingItems.CpuUsage]), CancellationToken.None);
        _sessions.Sessions.Count.ShouldBe(1, "the same items: the recording goes on");

        await _controller.StartRecordingByUserAsync(null, CancellationToken.None); // "Record everything"
        _controller.Selection.ShouldBeNull();
        _sessions.Sessions.ShouldBe([(1L, RecordingStartReason.User, (RecordingEndReason?)RecordingEndReason.User), (2L, RecordingStartReason.User, null)]);
        _sessions.Selections.ShouldBe([custom, null]);

        await _controller.StopRecordingByUserAsync(CancellationToken.None);
        _controller.Selection.ShouldBeNull();
    }

    [Fact]
    public async Task Each_scheduled_window_records_its_own_selection()
    {
        var gpuOnly = RecordingSelection.Of([RecordingItems.Gpu("gpu-a", "usage")]);
        _options.CurrentValue = new RecorderOptions
        {
            Mode = RecordingMode.Scheduled,
            Schedule =
            [
                new RecordingWindow { Days = [DayOfWeek.Monday], Start = new TimeOnly(9, 0), End = new TimeOnly(10, 0), Selection = gpuOnly },
                new RecordingWindow { Days = [DayOfWeek.Monday], Start = new TimeOnly(10, 0), End = new TimeOnly(11, 0) },
            ],
        };

        _time.Advance(TimeSpan.FromHours(1)); // 09:00
        await _controller.EvaluateAsync(CancellationToken.None);
        RecordingSelection.SameAs(_controller.Selection, gpuOnly).ShouldBeTrue();

        _time.Advance(TimeSpan.FromHours(1)); // 10:00: a window that records everything
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording);
        _controller.Selection.ShouldBeNull();

        _time.Advance(TimeSpan.FromHours(1)); // 11:00
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.ShouldBe(
        [
            (1L, RecordingStartReason.Schedule, (RecordingEndReason?)RecordingEndReason.Schedule),
            (2L, RecordingStartReason.Schedule, (RecordingEndReason?)RecordingEndReason.Schedule),
        ]);
        RecordingSelection.SameAs(_sessions.Selections[0], gpuOnly).ShouldBeTrue();
        _sessions.Selections[1].ShouldBeNull();
    }

    [Fact]
    public async Task Always_mode_records_everything_unless_the_user_picks_a_selection()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.Selection.ShouldBeNull();

        var custom = RecordingSelection.Of([RecordingItems.MemoryUsage]);
        await _controller.StartRecordingByUserAsync(custom, CancellationToken.None);
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.Selection.ShouldBeSameAs(custom, "the user's choice holds until they stop");
        _sessions.Sessions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Changing_the_mode_ends_the_current_session()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);

        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Manual };
        await _controller.EvaluateAsync(CancellationToken.None);

        _controller.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.Single().End.ShouldBe(RecordingEndReason.ModeChanged);
    }

    [Fact]
    public async Task Host_shutdown_ends_the_session_as_shutdown()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);

        await _controller.StopAsync(CancellationToken.None);

        _controller.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.Single().End.ShouldBe(RecordingEndReason.Shutdown);
    }

    [Fact]
    public async Task A_timed_pause_stops_recording_and_resumes_when_it_ends()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);

        await _controller.PauseAsync(TimeSpan.FromMinutes(15), CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Paused);
        _controller.PausedUntil.ShouldBe(_time.GetUtcNow().AddMinutes(15));

        _time.Advance(TimeSpan.FromMinutes(14));
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Paused); // Always mode does not override a pause

        _time.Advance(TimeSpan.FromMinutes(1));
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording);
        _controller.PausedUntil.ShouldBeNull();

        _sessions.Sessions.ShouldBe(
        [
            (1L, RecordingStartReason.Always, (RecordingEndReason?)RecordingEndReason.Pause),
            (2L, RecordingStartReason.Resume, (RecordingEndReason?)null),
        ]);
    }

    [Fact]
    public async Task A_pause_until_resumed_lasts_until_resume_or_a_user_start()
    {
        await _controller.StartRecordingByUserAsync(CancellationToken.None);
        await _controller.PauseAsync(null, CancellationToken.None);

        _time.Advance(TimeSpan.FromDays(2));
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Paused);

        await _controller.ResumeAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording); // the user had started recording before the pause
        _sessions.Sessions[^1].Start.ShouldBe(RecordingStartReason.Resume);

        await _controller.PauseAsync(null, CancellationToken.None);
        await _controller.StartRecordingByUserAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Recording);
        _controller.PausedUntil.ShouldBeNull();
        _sessions.Sessions[^1].Start.ShouldBe(RecordingStartReason.User);
    }

    [Fact]
    public async Task Pausing_while_idle_keeps_always_mode_from_starting()
    {
        await _controller.PauseAsync(TimeSpan.FromHours(1), CancellationToken.None);
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };

        await _controller.EvaluateAsync(CancellationToken.None);

        _controller.State.ShouldBe(RecorderState.Paused);
        _sessions.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sleep_closes_the_session_and_waking_starts_a_new_one()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);

        _power.RaiseSuspending(); // as Windows would, on the notification thread
        _controller.State.ShouldBe(RecorderState.Idle);
        await _controller.EvaluateAsync(CancellationToken.None);
        _controller.State.ShouldBe(RecorderState.Idle); // asleep: nothing starts

        await _controller.WakeAsync(CancellationToken.None);

        _controller.State.ShouldBe(RecorderState.Recording);
        _sessions.Sessions.ShouldBe(
        [
            (1L, RecordingStartReason.Always, (RecordingEndReason?)RecordingEndReason.Suspend),
            (2L, RecordingStartReason.Resume, (RecordingEndReason?)null),
        ]);
    }

    [Fact]
    public async Task When_windows_ends_the_session_recording_stops_for_good()
    {
        _options.CurrentValue = new RecorderOptions { Mode = RecordingMode.Always };
        await _controller.EvaluateAsync(CancellationToken.None);

        await _controller.StopForSessionEndAsync(CancellationToken.None);
        await _controller.EvaluateAsync(CancellationToken.None);
        await _controller.StartRecordingByUserAsync(CancellationToken.None);

        _controller.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.ShouldHaveSingleItem().End.ShouldBe(RecordingEndReason.SessionEnd);
    }

    [Fact]
    public async Task Pauses_must_be_positive()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _controller.PauseAsync(TimeSpan.Zero, CancellationToken.None));
    }
}
