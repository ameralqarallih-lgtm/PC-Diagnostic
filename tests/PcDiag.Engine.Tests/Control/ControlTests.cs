using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Control;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Control;
using PcDiag.Engine.Ipc;
using PcDiag.Engine.Recording;

namespace PcDiag.Engine.Tests.Control;

public sealed class ControlPolicyTests
{
    [Theory]
    [InlineData("csrss.exe", 600, true, false, false, ControlProtection.Protected)]
    [InlineData("svchost.exe", 1200, true, false, false, ControlProtection.Protected)]
    [InlineData("anything.exe", 4, false, false, false, ControlProtection.Protected)]
    [InlineData("helper.exe", 1300, true, true, false, ControlProtection.Protected)] // Windows marked it critical
    [InlineData("PcDiag.Agent.exe", 1400, false, false, true, ControlProtection.Protected)]
    [InlineData("explorer.exe", 1500, true, false, false, ControlProtection.Caution)]
    [InlineData("notepad.exe", 1600, true, false, false, ControlProtection.Caution)] // any other part of Windows
    [InlineData("chrome.exe", 1700, false, false, false, ControlProtection.None)]
    public void Processes_are_refused_confirmed_or_free(string name, int pid, bool windows, bool critical, bool self, ControlProtection expected)
    {
        var (protection, reason) = ControlPolicy.ForProcess(name, pid, windows, critical, self);

        protection.ShouldBe(expected);
        (reason is null).ShouldBe(expected == ControlProtection.None);
    }

    [Theory]
    [InlineData("RpcSs", true, ControlProtection.Protected)]
    [InlineData("WinDefend", true, ControlProtection.Protected)]
    [InlineData("Spooler", true, ControlProtection.Caution)]
    [InlineData("AdobeARMservice", false, ControlProtection.None)]
    public void Services_are_refused_confirmed_or_free(string name, bool windows, ControlProtection expected) =>
        ControlPolicy.ForService(name, windows).Protection.ShouldBe(expected);

    [Fact]
    public void A_program_is_part_of_Windows_only_under_the_Windows_folder()
    {
        ControlPolicy.IsWindowsPath(@"C:\Windows\System32\svchost.exe", @"C:\Windows").ShouldBeTrue();
        ControlPolicy.IsWindowsPath(@"c:\windows\explorer.exe", @"C:\Windows\").ShouldBeTrue();
        ControlPolicy.IsWindowsPath(@"C:\WindowsApps\fake.exe", @"C:\Windows").ShouldBeFalse();
        ControlPolicy.IsWindowsPath(null, @"C:\Windows").ShouldBeFalse();
    }
}

public sealed class ProcessDirectoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static ProcessMetrics Metrics(int pid, string name) =>
        new(new ProcessKey(pid, T0.UtcTicks + pid), name, Reading<double>.Of(1), Reading<long>.Of(1), Reading<long>.Of(1000), Reading<long>.Of(500), Reading<long>.Of(500),
            Reading<double>.Of(0), Reading<double>.Of(0), Reading<int>.Of(4), Reading<int>.Of(40), Reading<int>.Of(8), ProcessState.Running);

    private static ProcessIdentityDetails Identity(int pid, string name, string? signer = null) =>
        new(new ProcessKey(pid, T0.UtcTicks + pid), 4, name, T0, 1, $@"C:\Apps\{name}", null, null, signer);

    private static SampleFrame Frame(IReadOnlyList<ProcessMetrics> metrics, IReadOnlyList<ProcessIdentityDetails> identities) =>
        new(1, T0, Processes: new ProcessSample(T0, metrics, identities, []));

    [Fact]
    public async Task Identities_sent_once_are_kept_while_the_process_runs()
    {
        var directory = new ProcessDirectory();
        await directory.ConsumeAsync(Frame([Metrics(10, "a.exe"), Metrics(11, "b.exe")], [Identity(10, "a.exe"), Identity(11, "b.exe")]), CancellationToken.None);
        // Later samples carry no identities (only new processes and finished signature checks do).
        await directory.ConsumeAsync(Frame([Metrics(10, "a.exe"), Metrics(11, "b.exe")], [Identity(11, "b.exe", "B Corp")]), CancellationToken.None);

        var snapshot = directory.Snapshot();
        snapshot.Count.ShouldBe(2);
        snapshot.Single(p => p.Metrics.Key.Pid == 10).Identity!.Path.ShouldBe(@"C:\Apps\a.exe");
        snapshot.Single(p => p.Metrics.Key.Pid == 11).Identity!.Signer.ShouldBe("B Corp");
        directory.SampledAt.ShouldBe(T0);
    }

    [Fact]
    public async Task Ended_processes_are_forgotten()
    {
        var directory = new ProcessDirectory();
        var many = Enumerable.Range(100, 80).ToList();
        await directory.ConsumeAsync(Frame([.. many.Select(p => Metrics(p, $"p{p}.exe"))], [.. many.Select(p => Identity(p, $"p{p}.exe"))]), CancellationToken.None);
        await directory.ConsumeAsync(Frame([Metrics(100, "p100.exe")], []), CancellationToken.None);

        directory.Snapshot().ShouldHaveSingleItem().Identity.ShouldNotBeNull();
    }
}

public sealed class ControlCommandTests
{
    private readonly FakeControl _control = new();
    private readonly AgentCommandHandler _handler;

    public ControlCommandTests()
    {
        _handler = new AgentCommandHandler(new IdleRecorder(), new NoIncidents(), [], [], new StaticOptionsMonitor<RecorderOptions>(new RecorderOptions()), new ElevationInfo(true),
            new FakeTimeProvider(), NullLogger<AgentCommandHandler>.Instance, control: _control);
    }

    private Task<AgentResponse> SendAsync(string command, AgentRequestArgs? args) =>
        _handler.HandleAsync(new AgentRequest(AgentIpc.ProtocolVersion, "1", command, args), CancellationToken.None);

    [Fact]
    public async Task Processes_and_services_are_listed_and_acted_on()
    {
        (await SendAsync(AgentCommands.ControlProcesses, null)).Processes.ShouldNotBeNull().ShouldHaveSingleItem().Name.ShouldBe("app.exe");
        (await SendAsync(AgentCommands.ControlServices, null)).Services.ShouldNotBeNull().ShouldHaveSingleItem();

        var ended = await SendAsync(AgentCommands.ProcessAction, new AgentRequestArgs(Pid: 42, ProcessStart: 7, ControlAction: "end-tree", Confirmed: true));
        ended.Ok.ShouldBeTrue();
        _control.Calls.ShouldContain("end 42 7 tree=True confirmed=True");

        var stop = await SendAsync(AgentCommands.ServiceAction, new AgentRequestArgs(ServiceName: "AppUpdater", ControlAction: "start-type", StartType: "Manual"));
        stop.Result!.Ok.ShouldBeTrue();
        _control.Calls.ShouldContain("service AppUpdater start-type Manual confirmed=False");

        var clean = await SendAsync(AgentCommands.TuneUpApply, new AgentRequestArgs(ControlAction: TuneUpActions.Clean, Items: ["user-temp"]));
        clean.Result!.FreedBytes.ShouldBe(1024);
    }

    [Theory]
    [InlineData(AgentCommands.ProcessAction, 0, 7L, "end", null, null, null)]
    [InlineData(AgentCommands.ProcessAction, 42, 7L, "kill", null, null, null)]
    [InlineData(AgentCommands.ProcessAction, 42, 7L, "priority", "Turbo", null, null)]
    [InlineData(AgentCommands.ServiceAction, null, null, "stop", null, @"..\evil", null)]
    [InlineData(AgentCommands.ServiceAction, null, null, "delete", null, "AppUpdater", null)]
    [InlineData(AgentCommands.ServiceAction, null, null, "start-type", null, "AppUpdater", "Boot")]
    [InlineData(AgentCommands.TuneUpApply, null, null, "format-c", null, null, null)]
    public async Task Malformed_control_requests_are_refused_before_anything_happens(string command, int? pid, long? start, string action, string? priority, string? service, string? startType)
    {
        var response = await SendAsync(command, new AgentRequestArgs(Pid: pid, ProcessStart: start, ControlAction: action, Priority: priority, ServiceName: service, StartType: startType));

        response.Ok.ShouldBeFalse();
        _control.Calls.ShouldBeEmpty();
    }

    private sealed class FakeControl : ISystemControl
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<ControlProcess> Processes() =>
            [new(1, 2, 0, "app.exe", null, null, "app", ControlProtection.None, null, 1, 1, 1, 1, 1, "Normal", false, 1, [], DateTimeOffset.UnixEpoch)];

        public IReadOnlyList<ControlService> Services() =>
            [new("AppUpdater", "App Updater", null, "Running", "Automatic", 3, null, null, false, true, ControlProtection.None, null)];

        public ControlResult EndProcess(int pid, long startTicks, bool tree, bool confirmed, bool safe = false)
        {
            Calls.Add($"end {pid} {startTicks} tree={tree} confirmed={confirmed}");
            return new ControlResult(true, "Ended.", Changed: 1);
        }

        public ControlResult SetPriority(int pid, long startTicks, string priority)
        {
            Calls.Add($"priority {pid} {priority}");
            return new ControlResult(true, "Set.", Changed: 1);
        }

        public ControlResult ControlService(string name, string action, string? startType, bool confirmed, bool safe = false)
        {
            Calls.Add($"service {name} {action} {startType} confirmed={confirmed}");
            return new ControlResult(true, "Done.", Changed: 1);
        }

        public Task<TuneUpReport> ScanAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ControlResult> ApplyAsync(string action, IReadOnlyList<string> items, string? value, bool confirmed, CancellationToken cancellationToken, bool safe = false)
        {
            Calls.Add($"apply {action}");
            return Task.FromResult(new ControlResult(true, "Cleaned.", FreedBytes: 1024, Changed: 3));
        }
    }

    private sealed class IdleRecorder : IRecordingControl
    {
        public RecorderState State => RecorderState.Idle;

        public DateTimeOffset? PausedUntil => null;

        public RecordingSelection? Selection => null;

        public bool LiveViewActive => false;

        public Task StartRecordingByUserAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartRecordingByUserAsync(RecordingSelection? selection, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopRecordingByUserAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PauseAsync(TimeSpan? duration, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task KeepLiveViewAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoIncidents : IIncidentCapture
    {
        public Incident? Active => null;

        public DateTimeOffset? ActiveUntil => null;

        public event EventHandler<IncidentCapturedEventArgs>? Captured
        {
            add { }
            remove { }
        }

        public event EventHandler<IncidentCapturedEventArgs>? Finished
        {
            add { }
            remove { }
        }

        public Task<CaptureOutcome> CaptureAsync(IncidentTrigger trigger, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IncidentEditResult> UpdateAsync(IncidentKey key, IncidentUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IncidentEditResult> DeleteAsync(IncidentKey key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
