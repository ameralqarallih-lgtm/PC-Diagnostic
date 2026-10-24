using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;
using PcDiag.Core.Recording;
using PcDiag.Engine.Recording;
using PcDiag.Engine.Tests.Anomalies;
using Shouldly;

namespace PcDiag.Engine.Tests.Recording;

public sealed class EnhancedModeServiceTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly StaticOptionsMonitor<EnhancedOptions> _options = new(new EnhancedOptions { FlightRecorder = true, ProcessIo = true });
    private readonly FakeRecorder _recorder = new();
    private readonly FakeFlightRecorder _flight = new();
    private readonly FakeIoMonitor _io = new();
    private readonly FakeCapture _capture;
    private readonly EnhancedModeService _service;

    public EnhancedModeServiceTests()
    {
        _capture = new FakeCapture(_time);
        _service = new EnhancedModeService(_options, _recorder, _capture, new DataPaths(_directory), [_flight], [_io], _time, NullLogger<EnhancedModeService>.Instance);
    }

    [Fact]
    public async Task Runs_only_while_recording()
    {
        await _service.ApplyAsync(CancellationToken.None);
        _flight.Running.ShouldBeFalse();
        _io.Running.ShouldBeFalse();

        _recorder.State = RecorderState.Recording;
        await _service.ApplyAsync(CancellationToken.None);
        _flight.Running.ShouldBeTrue();
        _io.Running.ShouldBeTrue();
        _service.Status.ShouldBe("flight recorder on; per-process I/O on");

        _recorder.State = RecorderState.Idle;
        await _service.ApplyAsync(CancellationToken.None);
        _flight.Running.ShouldBeFalse();
        _io.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task Enhanced_mode_runs_while_recording()
    {
        using var service = new EnhancedModeService(_options, _recorder, _capture, new DataPaths(_directory), [_flight], [_io], _time, NullLogger<EnhancedModeService>.Instance);
        _recorder.State = RecorderState.Recording;
        await service.ApplyAsync(CancellationToken.None);
        (_flight.Running, _io.Running).ShouldBe((true, true));
    }

    [Fact]
    public async Task Each_part_follows_its_setting()
    {
        _recorder.State = RecorderState.Recording;
        _options.Set(new EnhancedOptions { ProcessIo = true });
        await _service.ApplyAsync(CancellationToken.None);
        _flight.Running.ShouldBeFalse();
        _io.Running.ShouldBeTrue();

        _options.Set(new EnhancedOptions { ProcessIo = false });
        await _service.ApplyAsync(CancellationToken.None);
        _io.Running.ShouldBeFalse();
        _service.Status.ShouldBeNull();
    }

    [Fact]
    public async Task Per_process_io_is_on_by_default_and_runs_whenever_the_recorder_samples()
    {
        new EnhancedOptions().ProcessIo.ShouldBeTrue();
        _options.Set(new EnhancedOptions());

        // Not recording but watching for problems (or showing the dashboard): the processes lists still get network figures.
        _recorder.State = RecorderState.Idle;
        _recorder.Watching = true;
        await _service.ApplyAsync(CancellationToken.None);
        _io.Running.ShouldBeTrue();
        _flight.Running.ShouldBeFalse();

        _recorder.Watching = false;
        await _service.ApplyAsync(CancellationToken.None);
        _io.Running.ShouldBeFalse();
    }

    [Fact]
    public async Task A_failed_start_is_retried_only_when_wanted_again()
    {
        _recorder.State = RecorderState.Recording;
        _flight.FailWith = "Needs the recorder to run as administrator.";
        await _service.ApplyAsync(CancellationToken.None);
        await _service.ApplyAsync(CancellationToken.None);
        _flight.Starts.ShouldBe(1);
        _service.Status.ShouldStartWith("flight recorder: Needs the recorder to run as administrator.");

        // Turned off and on again: one more try.
        _options.Set(new EnhancedOptions());
        await _service.ApplyAsync(CancellationToken.None);
        _flight.FailWith = null;
        _options.Set(new EnhancedOptions { FlightRecorder = true });
        await _service.ApplyAsync(CancellationToken.None);
        _flight.Starts.ShouldBe(2);
        _flight.Running.ShouldBeTrue();
    }

    [Fact]
    public async Task Saves_the_trace_into_the_new_incidents_folder()
    {
        _recorder.State = RecorderState.Recording;
        await _service.ApplyAsync(CancellationToken.None);
        var outcome = await _capture.CaptureAsync(new IncidentTrigger(_time.GetUtcNow(), IncidentSource.User), CancellationToken.None);

        (await _service.SaveTraceAsync(outcome.Incident.Key, CancellationToken.None)).ShouldBeTrue();

        var expected = Path.Combine(new DataPaths(_directory).GetIncidentDirectory(outcome.Incident.Key), "traces", "incident.etl");
        _flight.Saved.ShouldBe([expected]);
        File.Exists(expected).ShouldBeTrue();
    }

    [Fact]
    public async Task Nothing_is_saved_while_the_flight_recorder_is_off()
    {
        (await _service.SaveTraceAsync(IncidentKey.Create(new DateOnly(2026, 10, 6), 1), CancellationToken.None)).ShouldBeFalse();
        _flight.Saved.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_capture_saves_in_the_background_and_merges_do_not_save_again()
    {
        _recorder.State = RecorderState.Recording;
        await _service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => _flight.Running);
            var first = await _capture.CaptureAsync(new IncidentTrigger(_time.GetUtcNow(), IncidentSource.User), CancellationToken.None);
            await WaitUntilAsync(() => _flight.Saved.Count == 1);

            // Merged into the incident that is still recording.
            _capture.Active = first.Incident;
            _capture.ActiveUntil = _time.GetUtcNow().AddMinutes(2);
            var merged = await _capture.CaptureAsync(new IncidentTrigger(_time.GetUtcNow(), IncidentSource.User), CancellationToken.None);
            merged.Merged.ShouldBeTrue();
            await Task.Delay(100, CancellationToken.None);
            _flight.Saved.Count.ShouldBe(1);
        }
        finally
        {
            await _service.StopAsync(CancellationToken.None);
        }

        _flight.Running.ShouldBeFalse();
        _io.Running.ShouldBeFalse();
    }

    public void Dispose()
    {
        _service.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        condition().ShouldBeTrue();
    }

    private sealed class FakeRecorder : IRecordingControl
    {
        public RecorderState State { get; set; } = RecorderState.Idle;

        public DateTimeOffset? PausedUntil => null;

        public bool LiveViewActive => false;

        public bool Watching { get; set; }

        public RecordingSelection? Selection => null;

        public Task StartRecordingByUserAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartRecordingByUserAsync(RecordingSelection? selection, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopRecordingByUserAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PauseAsync(TimeSpan? duration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task KeepLiveViewAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeFlightRecorder : IFlightRecorder
    {
        public bool Running { get; private set; }

        public string? Problem { get; private set; }

        public string? FailWith { get; set; }

        public int Starts { get; private set; }

        public List<string> Saved { get; } = [];

        public Task<bool> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            Problem = FailWith;
            Running = FailWith is null;
            return Task.FromResult(Running);
        }

        public Task<bool> SaveAsync(string etlPath, string description, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(etlPath)!);
            File.WriteAllBytes(etlPath, new byte[16]);
            lock (Saved)
            {
                Saved.Add(etlPath);
            }

            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Running = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeIoMonitor : IProcessIoMonitor
    {
        public bool Running { get; private set; }

        public string? Problem => null;

        public bool StartCollecting() => Running = true;

        public void StopCollecting() => Running = false;

        public IReadOnlyDictionary<int, ProcessIoTotals> Take() => new Dictionary<int, ProcessIoTotals>();
    }
}
