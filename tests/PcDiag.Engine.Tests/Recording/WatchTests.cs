using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Telemetry;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Tests.Incidents;

namespace PcDiag.Engine.Tests.Recording;

/// <summary>Watching for problems while not recording (a stopped Manual recorder still finds them).</summary>
public sealed class WatchTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
    private readonly StaticOptionsMonitor<RecorderOptions> _options = new(new RecorderOptions { Mode = RecordingMode.Manual });
    private readonly RecordingController _recorder;

    public WatchTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var bus = EngineFactory.Bus();
        _recorder = new RecordingController(EngineFactory.Scheduler(_time, bus), bus, new InMemorySessionStore(), _options, _time, new ElevationInfo(true), new FakePowerEvents(),
            NullLogger<RecordingController>.Instance);
    }

    public void Dispose() => _recorder.Dispose();

    [Fact]
    public async Task A_recorder_that_is_not_recording_watches_until_paused_or_recording()
    {
        await _recorder.EvaluateAsync(CancellationToken.None);
        _recorder.State.ShouldBe(RecorderState.Idle);
        _recorder.Watching.ShouldBeTrue();

        // Recording replaces watching; stopping brings it back.
        await _recorder.StartRecordingByUserAsync(CancellationToken.None);
        _recorder.Watching.ShouldBeFalse();
        await _recorder.StopRecordingByUserAsync(CancellationToken.None);
        await _recorder.EvaluateAsync(CancellationToken.None);
        _recorder.Watching.ShouldBeTrue();

        // A pause stops everything, watching too.
        await _recorder.PauseAsync(TimeSpan.FromMinutes(5), CancellationToken.None);
        await _recorder.EvaluateAsync(CancellationToken.None);
        _recorder.Watching.ShouldBeFalse();
    }

    [Fact]
    public void A_watched_frame_says_so_and_a_recorded_one_never_does()
    {
        var diagnostics = new TickDiagnostics(TimeSpan.Zero, 0, 0, default, 0);
        var builder = new SampleFrameBuilder();

        builder.Build(1, _time.GetUtcNow(), diagnostics, recorded: false, watched: true).ShouldSatisfyAllConditions(f => f.Recorded.ShouldBeFalse(), f => f.Watched.ShouldBeTrue());
        builder.Build(2, _time.GetUtcNow(), diagnostics, recorded: false).Watched.ShouldBeFalse();
    }

    [Fact]
    public async Task Watching_can_be_turned_off()
    {
        _options.CurrentValue.WatchWhenNotRecording = false;
        await _recorder.EvaluateAsync(CancellationToken.None);
        _recorder.Watching.ShouldBeFalse();
    }
}
