using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PcDiag.Collectors.Gpu;
using PcDiag.Collectors.Processes;
using PcDiag.Collectors.Services;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Processes;
using PcDiag.Platform.Windows.Services;
using Xunit.Abstractions;

namespace PcDiag.Collectors.IntegrationTests;

public class CommandLineRedactorTests
{
    [Theory]
    [InlineData("app.exe --password hunter2 --verbose", "app.exe --password [redacted] --verbose")]
    [InlineData("app.exe --password=hunter2", "app.exe --password=[redacted]")]
    [InlineData("tool /pwd:secret123 /x", "tool /pwd:[redacted] /x")]
    [InlineData("svc.exe token=abc.def.ghi", "svc.exe token=[redacted]")]
    [InlineData("cli --api-key \"k 1 2\"", "cli --api-key [redacted]")]
    [InlineData("db.exe \"Server=x;User Id=sa;Password=P@ss;\"", "db.exe \"Server=x;User Id=sa;Password=[redacted];\"")]
    [InlineData("git clone https://user:ghp_abc@github.com/x.git", "git clone https://[redacted]@github.com/x.git")]
    [InlineData("notepad.exe C:\\notes\\passport.txt", "notepad.exe C:\\notes\\passport.txt")]
    public void Known_secret_patterns_are_redacted(string input, string expected)
    {
        CommandLineRedactor.Redact(input).ShouldBe(expected);
    }
}

public class ServiceDiffTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    private static ServiceInfo Svc(string name, ServiceStatus status, ServiceStartType start = ServiceStartType.Automatic) =>
        new(name, name + " display", status, start, status == ServiceStatus.Running ? 100 : null, 16, null);

    private static Dictionary<string, ServiceInfo> Map(params ServiceInfo[] services) =>
        services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    private readonly ServiceCollector _collector = new(TimeProvider.System, NullLogger<ServiceCollector>.Instance);
    private readonly Dictionary<string, ServiceStartConfig> _known = new() { ["a"] = new(2, false), ["b"] = new(2, false) };

    [Fact]
    public void Start_stop_install_and_remove_are_detected()
    {
        var before = Map(Svc("a", ServiceStatus.Running), Svc("b", ServiceStatus.Stopped), Svc("old", ServiceStatus.Stopped));
        var after = Map(Svc("a", ServiceStatus.Stopped), Svc("b", ServiceStatus.Running), Svc("new", ServiceStatus.Running));

        var changes = _collector.Diff(before, after, _known, startTypesRefreshed: false, T0);

        changes.Select(c => (c.Name, c.Kind)).ShouldBe(
            [("a", ServiceEventKind.Stopped), ("b", ServiceEventKind.Started), ("new", ServiceEventKind.Installed), ("old", ServiceEventKind.Removed)],
            ignoreOrder: true);
    }

    [Fact]
    public void A_start_carries_the_process_and_a_new_process_between_polls_is_a_restart()
    {
        var before = Map(Svc("a", ServiceStatus.Stopped), Svc("b", ServiceStatus.Running));
        var after = Map(Svc("a", ServiceStatus.Running) with { ProcessId = 4242 }, Svc("b", ServiceStatus.Running) with { ProcessId = 777 });

        var changes = _collector.Diff(before, after, _known, startTypesRefreshed: false, T0);

        changes.Select(c => (c.Name, c.Kind, c.ProcessId)).ShouldBe(
            [("a", ServiceEventKind.Started, (int?)4242), ("b", ServiceEventKind.Restarted, (int?)777)], ignoreOrder: true);
    }

    [Fact]
    public void Stop_then_start_within_a_minute_is_a_restart()
    {
        var running = Map(Svc("a", ServiceStatus.Running));
        var stopped = Map(Svc("a", ServiceStatus.Stopped));

        _collector.Diff(running, stopped, _known, false, T0).Single().Kind.ShouldBe(ServiceEventKind.Stopped);
        _collector.Diff(stopped, running, _known, false, T0.AddSeconds(20)).Single().Kind.ShouldBe(ServiceEventKind.Restarted);

        _collector.Diff(running, stopped, _known, false, T0.AddMinutes(5)).Single().Kind.ShouldBe(ServiceEventKind.Stopped);
        _collector.Diff(stopped, running, _known, false, T0.AddMinutes(7)).Single().Kind.ShouldBe(ServiceEventKind.Started);
    }

    [Fact]
    public void Pending_states_are_not_reported_until_settled()
    {
        var changes = _collector.Diff(Map(Svc("a", ServiceStatus.Stopped)), Map(Svc("a", ServiceStatus.StartPending)), _known, false, T0);

        changes.ShouldBeEmpty();
    }

    [Fact]
    public void Start_type_changes_are_reported_only_after_a_refresh()
    {
        var before = Map(Svc("a", ServiceStatus.Running, ServiceStartType.Automatic));
        var after = Map(Svc("a", ServiceStatus.Running, ServiceStartType.Disabled));

        _collector.Diff(before, after, _known, startTypesRefreshed: false, T0).ShouldBeEmpty();
        var change = _collector.Diff(before, after, _known, startTypesRefreshed: true, T0).Single();
        change.Kind.ShouldBe(ServiceEventKind.StartTypeChanged);
        (change.OldValue, change.NewValue).ShouldBe(("Automatic", "Disabled"));
    }

    [Theory]
    [InlineData(2, false, ServiceStartType.Automatic)]
    [InlineData(2, true, ServiceStartType.AutomaticDelayed)]
    [InlineData(3, false, ServiceStartType.Manual)]
    [InlineData(4, false, ServiceStartType.Disabled)]
    public void Start_types_are_mapped(int startType, bool delayed, ServiceStartType expected)
    {
        ServiceCollector.MapStartType(new ServiceStartConfig(startType, delayed)).ShouldBe(expected);
    }

    [Fact]
    public void Real_service_list_includes_the_event_log_service()
    {
        var services = ServiceControlManager.Enumerate();

        services.Count.ShouldBeGreaterThan(50);
        var eventLog = services.Single(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase));
        eventLog.State.ShouldBe(4); // running
        eventLog.ProcessId.ShouldBeGreaterThan(0);
        ServiceControlManager.QueryStartConfigs(["EventLog"])["EventLog"].StartType.ShouldBe(2); // automatic
    }
}

public class ProcessCollectorTests(ITestOutputHelper output)
{
    private static SampleContext Context(long sequence) => new(sequence, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), false);

    [Fact]
    public async Task First_tick_is_a_baseline_and_later_ticks_detect_start_and_stop()
    {
        var collector = new ProcessCollector(new Privacy(new PrivacyOptions()), TimeProvider.System, NullLogger<ProcessCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        var baseline = await collector.CollectAsync(Context(1), CancellationToken.None);
        baseline.Events.ShouldBeEmpty(); // no flood of "Started" for everything already running
        baseline.Processes.Count.ShouldBeGreaterThan(20);

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
        await Task.Delay(300);
        var started = await collector.CollectAsync(Context(3), CancellationToken.None);
        started.Events.ShouldContain(e => e.Key.Pid == child.Id && e.Kind == ProcessEventKind.Started);

        await child.WaitForExitAsync();
        var stopped = await collector.CollectAsync(Context(5), CancellationToken.None);
        stopped.Events.ShouldContain(e => e.Key.Pid == child.Id && e.Kind == ProcessEventKind.Stopped);
    }

    [Fact]
    public async Task Metrics_and_identity_for_the_current_process_are_plausible()
    {
        var collector = new ProcessCollector(new Privacy(new PrivacyOptions { CaptureCommandLines = true, CaptureUserNames = true }), TimeProvider.System, NullLogger<ProcessCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);
        var first = await collector.CollectAsync(Context(1), CancellationToken.None);
        await Task.Delay(500);
        var second = await collector.CollectAsync(Context(3), CancellationToken.None);

        var self = second.Processes.Single(p => p.Key.Pid == Environment.ProcessId);
        self.CpuPercent.HasValue.ShouldBeTrue();
        self.CpuPercent.Value!.Value.ShouldBeInRange(0, 100);
        self.WorkingSetBytes.Value.GetValueOrDefault().ShouldBeGreaterThan(1 << 20);
        self.ThreadCount.Value.GetValueOrDefault().ShouldBeGreaterThan(1);

        // Enrichment is capped per tick (about 480 processes at startup), so keep collecting until this process is reached.
        var identities = first.Identities.Concat(second.Identities).ToList();
        for (var tick = 5; tick < 40 && !identities.Any(i => i.Key.Pid == Environment.ProcessId && i.Path is not null); tick += 2)
        {
            identities.AddRange((await collector.CollectAsync(Context(tick), CancellationToken.None)).Identities);
        }

        var identity = identities.Last(i => i.Key.Pid == Environment.ProcessId && i.Path is not null);
        identity.Path.ShouldBe(Environment.ProcessPath, StringCompareShould.IgnoreCase);
        identity.CommandLine.ShouldNotBeNullOrEmpty();
        identity.User.ShouldBe(Environment.UserDomainName + "\\" + Environment.UserName, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public async Task A_process_that_reads_as_before_hands_back_the_same_metrics()
    {
        var collector = new ProcessCollector(new Privacy(new PrivacyOptions()), TimeProvider.System, NullLogger<ProcessCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);
        await collector.CollectAsync(Context(1), CancellationToken.None); // rates are pending on the first tick
        var second = (await collector.CollectAsync(Context(3), CancellationToken.None)).Processes.ToDictionary(p => p.Key);
        var third = await collector.CollectAsync(Context(5), CancellationToken.None);

        var reused = third.Processes.Where(p => second.TryGetValue(p.Key, out var before) && ReferenceEquals(p, before)).ToList();
        output.WriteLine($"{reused.Count} of {third.Processes.Count} processes unchanged");
        reused.ShouldNotBeEmpty("an idle machine still has hundreds of idle processes");
        third.Processes.Where(p => second.TryGetValue(p.Key, out var before) && !ReferenceEquals(p, before)).ShouldAllBe(p => !p.Equals(second[p.Key]));
    }

    [Fact]
    public async Task Command_lines_and_users_are_not_captured_by_default()
    {
        var collector = new ProcessCollector(new Privacy(new PrivacyOptions()), TimeProvider.System, NullLogger<ProcessCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        var sample = await collector.CollectAsync(Context(1), CancellationToken.None);
        var more = await collector.CollectAsync(Context(3), CancellationToken.None);

        sample.Identities.Concat(more.Identities).ShouldAllBe(i => i.CommandLine == null && i.User == null);
    }

    [Fact]
    public async Task Enhanced_mode_fills_true_io_rates_per_process()
    {
        var io = new FakeIo();
        var collector = new ProcessCollector(new Privacy(new PrivacyOptions()), [io], TimeProvider.System, NullLogger<ProcessCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        // Not running: no true I/O at all.
        await collector.CollectAsync(Context(1), CancellationToken.None);
        await Task.Delay(200);
        (await collector.CollectAsync(Context(3), CancellationToken.None)).Processes.ShouldAllBe(p => p.TrueIo == null);

        // The first read after it starts covers part of a tick only, so it is discarded.
        io.Running = true;
        io.Next = new Dictionary<int, ProcessIoTotals> { [Environment.ProcessId] = new(1_000, 2_000, 3_000, 4_000) };
        await Task.Delay(200);
        (await collector.CollectAsync(Context(5), CancellationToken.None)).Processes.ShouldAllBe(p => p.TrueIo == null);

        io.Next = new Dictionary<int, ProcessIoTotals> { [Environment.ProcessId] = new(1_000, 2_000, 3_000, 4_000) };
        await Task.Delay(500);
        var sample = await collector.CollectAsync(Context(7), CancellationToken.None);
        var self = sample.Processes.Single(p => p.Key.Pid == Environment.ProcessId).TrueIo.ShouldNotBeNull();
        self.DiskReadBytesPerSec.ShouldBeInRange(100, 2_000); // 1,000 bytes over roughly half a second
        (self.NetSendBytesPerSec / self.DiskReadBytesPerSec).ShouldBe(4, 0.001);
        sample.Processes.Where(p => p.Key.Pid != Environment.ProcessId).ShouldAllBe(p => p.TrueIo == ProcessTrueIo.None);
    }

    [Fact]
    public async Task Each_program_file_signature_is_checked_once_and_sent_with_its_process()
    {
        var verifier = new FakeVerifier();
        using var signatures = new ProcessSignatures(verifier, NullLogger<ProcessSignatures>.Instance, TimeSpan.Zero);
        var collector = new ProcessCollector(new Privacy(new PrivacyOptions()), [], signatures, TimeProvider.System, NullLogger<ProcessCollector>.Instance);
        await collector.InitializeAsync(CancellationToken.None);

        // Enrichment is capped per tick; keep collecting until this process comes back with its signature.
        ProcessIdentityDetails? self = null;
        for (var tick = 1; tick < 200 && self is null; tick += 2)
        {
            var sample = await collector.CollectAsync(Context(tick), CancellationToken.None);
            self = sample.Identities.FirstOrDefault(i => i.Key.Pid == Environment.ProcessId && i.Signature is not null);
            await Task.Delay(20);
        }

        self.ShouldNotBeNull();
        self.Signature.ShouldBe(SignatureStatus.Embedded);
        self.Signer.ShouldBe("Test Signer");
        self.Path.ShouldBe(Environment.ProcessPath, StringCompareShould.IgnoreCase);
        verifier.Checked.Count(p => string.Equals(p, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)).ShouldBe(1);

        // Known now: answered from memory, without another check.
        signatures.TryGet(Environment.ProcessPath!, out var cached).ShouldBeTrue();
        cached.Signer.ShouldBe("Test Signer");
        signatures.Check(Environment.ProcessPath!).ShouldBe(cached);
        verifier.Checked.Count(p => string.Equals(p, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public void A_missing_program_file_is_unknown_without_a_check()
    {
        var verifier = new FakeVerifier();
        using var signatures = new ProcessSignatures(verifier, NullLogger<ProcessSignatures>.Instance, TimeSpan.Zero);

        signatures.Check(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe")).Status.ShouldBe(SignatureStatus.Unknown);
        verifier.Checked.ShouldBeEmpty();
    }

    [Fact]
    public void Hung_window_scan_runs_without_error()
    {
        Should.NotThrow(() => HungWindows.FindHungProcesses());
    }

    [Theory]
    [InlineData(944L << 20, 24L << 30, true)]
    [InlineData(33L << 30, 24L << 30, false)] // observed: dwm.exe 33 GB on a 24 GB card
    [InlineData(1L << 30, 0, true)]           // unknown adapter size: cannot judge
    public void Per_process_gpu_memory_larger_than_the_adapter_is_rejected(long bytes, long adapter, bool plausible)
    {
        GpuCollector.IsPlausibleProcessMemory(bytes, adapter).ShouldBe(plausible);
    }

    private sealed class FakeVerifier : IFileSignatureVerifier
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Checked { get; } = [];

        public PcDiag.Platform.Windows.Processes.FileSignature Verify(string path)
        {
            Checked.Add(path);
            return new PcDiag.Platform.Windows.Processes.FileSignature(SignatureStatus.Embedded, "Test Signer");
        }
    }

    private sealed class FakeIo : IProcessIoMonitor
    {
        public bool Running { get; set; }

        public string? Problem => null;

        public IReadOnlyDictionary<int, ProcessIoTotals> Next { get; set; } = new Dictionary<int, ProcessIoTotals>();

        public bool StartCollecting() => Running = true;

        public void StopCollecting() => Running = false;

        public IReadOnlyDictionary<int, ProcessIoTotals> Take()
        {
            var taken = Next;
            Next = new Dictionary<int, ProcessIoTotals>();
            return taken;
        }
    }

    private sealed class Privacy(PrivacyOptions value) : IOptionsMonitor<PrivacyOptions>
    {
        public PrivacyOptions CurrentValue => value;

        public PrivacyOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<PrivacyOptions, string?> listener) => null;
    }
}
