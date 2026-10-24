using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Tests.Live;

/// <summary>The dashboard's live view: sampling in memory while the recorder is idle, never recorded.</summary>
public sealed class LiveViewTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _sessions = new();
    private readonly RecordingConsumer _frames = new();
    private readonly SampleBus _bus;
    private readonly RecordingController _recorder;

    public LiveViewTests()
    {
        _bus = EngineFactory.Bus(_frames);
        _bus.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        var scheduler = EngineFactory.Scheduler(_time, _bus, new FakeCollector("cpu", SamplingTier.System));
        _recorder = new RecordingController(scheduler, _bus, _sessions, new StaticOptionsMonitor<RecorderOptions>(new RecorderOptions()), _time,
            new ElevationInfo(false), new FakePowerEvents(), NullLogger<RecordingController>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _recorder.StopAsync(CancellationToken.None);
        _recorder.Dispose();
        await _bus.StopAsync(CancellationToken.None);
        await _bus.DisposeAsync();
    }

    private List<SampleFrame> Frames
    {
        get
        {
            lock (_frames.Frames)
            {
                return [.. _frames.Frames];
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task An_idle_recorder_samples_for_the_dashboard_without_recording()
    {
        await _recorder.KeepLiveViewAsync(CancellationToken.None);

        _recorder.LiveViewActive.ShouldBeTrue();
        await WaitUntilAsync(() => Frames.Count > 0);
        Frames.ShouldAllBe(f => !f.Recorded);
        _recorder.State.ShouldBe(RecorderState.Idle);
        _sessions.Sessions.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_live_view_stops_when_no_dashboard_asks_for_a_while()
    {
        await _recorder.KeepLiveViewAsync(CancellationToken.None);
        await WaitUntilAsync(() => Frames.Count > 0);

        _time.Advance(RecordingController.LiveViewLinger - TimeSpan.FromSeconds(1));
        await _recorder.KeepLiveViewAsync(CancellationToken.None); // a dashboard is still watching
        _time.Advance(TimeSpan.FromSeconds(2));
        _recorder.LiveViewActive.ShouldBeTrue();

        _time.Advance(RecordingController.LiveViewLinger);
        await WaitUntilAsync(() => !_recorder.LiveViewActive);
    }

    [Fact]
    public async Task Starting_to_record_replaces_the_live_view()
    {
        await _recorder.KeepLiveViewAsync(CancellationToken.None);
        await WaitUntilAsync(() => Frames.Count > 0);

        await _recorder.StartRecordingByUserAsync(CancellationToken.None);

        _recorder.LiveViewActive.ShouldBeFalse();
        _recorder.State.ShouldBe(RecorderState.Recording);
        await WaitUntilAsync(() => Frames.Any(f => f.Recorded));
        await _recorder.KeepLiveViewAsync(CancellationToken.None);
        _recorder.LiveViewActive.ShouldBeFalse(); // recording frames already reach the dashboard
    }

    [Fact]
    public async Task A_pause_is_respected()
    {
        await _recorder.KeepLiveViewAsync(CancellationToken.None);
        await WaitUntilAsync(() => Frames.Count > 0);

        await _recorder.PauseAsync(null, CancellationToken.None);
        _recorder.LiveViewActive.ShouldBeFalse();

        await _recorder.KeepLiveViewAsync(CancellationToken.None);
        _recorder.LiveViewActive.ShouldBeFalse();
    }
}
