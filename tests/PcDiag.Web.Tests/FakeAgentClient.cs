using PcDiag.Core.Events;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Tests;

/// <summary>An in-memory Agent: records requests and answers like the real one (or is "not running").</summary>
internal sealed class FakeAgentClient : IAgentClient
{
    private readonly List<(string Command, AgentRequestArgs? Args)> _requests = [];

    public bool Connected { get; set; }

    /// <summary>When set, the Agent refuses every command with this error.</summary>
    public string? Refuse { get; set; }

    public AgentStatus Status { get; set; } = new("1.0.0", false, RecordingMode.Manual, RecorderState.Recording, null, null, null);

    /// <summary>The expected-hardware file it checks against (as the recorder reads it).</summary>
    public string? HardwareExpectedFile { get; set; }

    /// <summary>The storage manager's state it reports.</summary>
    public PcDiag.Core.Storage.StorageState Storage { get; set; } = new(
        PcDiag.Core.Storage.StorageCondition.Normal, null, DateTimeOffset.UnixEpoch, 400L << 30, 1L << 20, DateTimeOffset.UnixEpoch,
        [new(DateTimeOffset.UnixEpoch, PcDiag.Core.Storage.StorageActionKinds.FullDetailShortened, "Full detail kept for 20 h instead of 24 h to stay within the 10 GB space limit.", 1L << 20, true)]);

    public string UnavailableReason => "The recorder Agent is not running.";

    public IReadOnlyList<(string Command, AgentRequestArgs? Args)> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int Count(string command) => Requests.Count(r => r.Command == command);

    public Task<AgentResponse?> SendAsync(string command, AgentRequestArgs? args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_requests)
        {
            _requests.Add((command, args));
        }

        if (!Connected)
        {
            return Task.FromResult<AgentResponse?>(null);
        }

        if (Refuse is { } error)
        {
            return Task.FromResult<AgentResponse?>(new AgentResponse("1", false, error, Status));
        }

        if (command is AgentCommands.HardwareState or AgentCommands.HardwareCheck)
        {
            var expected = HardwareExpectedFile is { } file ? PcDiag.Core.Devices.HardwareCheck.Read(file) : null;
            PcDiag.Core.Devices.HardwareItem[] current = [new("monitor:AUS32F4", "monitor", "PG32UCDP", "3840 × 2160"), new("memory", "memory", "Memory", "32 GB installed", 32)];
            var (missing, added) = PcDiag.Core.Devices.HardwareCheck.Compare(expected, current, [], DateTimeOffset.UnixEpoch);
            return Task.FromResult<AgentResponse?>(new AgentResponse("1", true, Status: Status, Hardware: new PcDiag.Core.Devices.HardwareStatus(DateTimeOffset.UnixEpoch, expected, current, missing, added)));
        }

        if (command is AgentCommands.StorageState or AgentCommands.StorageClean)
        {
            return Task.FromResult<AgentResponse?>(new AgentResponse("1", true, Status: Status, Storage: Storage,
                Cleaned: command == AgentCommands.StorageClean ? new PcDiag.Core.Storage.StorageCleanResult(2L << 20, "Database compacted: 2 MB given back.", Storage) : null));
        }

        return Task.FromResult<AgentResponse?>(new AgentResponse(
            "1",
            true,
            Status: Status,
            Capture: command == AgentCommands.Capture ? new AgentCaptureResult("INC-20261006-00001", false, false, new DateTimeOffset(2026, 10, 6, 12, 2, 0, TimeSpan.Zero)) : null,
            Live: command == AgentCommands.Live ? Live() : null,
            Trend: command == AgentCommands.Trend ? Trend(args?.TrendMinutes ?? 10) : null,
            Catalog: command == AgentCommands.Catalog ? new RecordingCatalog([new("cpu", "CPU", "cpu", [new(RecordingItems.CpuUsage, "Utilization")])]) : null,
            Processes: command == AgentCommands.ControlProcesses
                ? [new PcDiag.Core.Control.ControlProcess(1234, 638_000_000_000_000_000, 4, "game.exe", @"C:\Games\game.exe", "Game Studio", "app", PcDiag.Core.Control.ControlProtection.None, null, 25, 1_000_000, 900_000, 20, 300, "Normal", false, 1, [], DateTimeOffset.UnixEpoch)]
                : null,
            Result: command is AgentCommands.ProcessAction or AgentCommands.ServiceAction or AgentCommands.TuneUpApply ? new PcDiag.Core.Control.ControlResult(true, "Done.", Changed: 1) : null,
            Overlay: command is AgentCommands.OverlayState or AgentCommands.OverlayArrange
                ? new PcDiag.Core.Overlay.OverlayState(true, true, null, [new("AUS32F4/1", "PG32UCDP", 1, 0, 0, 3840, 2160, 1.5, true, 240)], [new("cpu.usage", "CPU", "Processor", "%", 25)], Arranging: command == AgentCommands.OverlayArrange && args?.On == true)
                : null,
            ScreenDrivers: command is AgentCommands.OverlayScreenDrivers or AgentCommands.OverlayScreenDriverApprove
                ? [new("lianli-turzx", "Lian Li screens", "1.0", "PC Diagnostic", true, null, true, true, null, ["Lian Li Universal Screen 8.8″"]),
                   new("my-screen", "My screen", "1.0", "Me", false, new string('a', 64), command == AgentCommands.OverlayScreenDriverApprove && args?.On == true, false, "Waiting for your approval.", ["My 5″ screen"])]
                : null));
    }

    public static LiveTrend Trend(int minutes)
    {
        var start = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var times = Enumerable.Range(0, minutes * 60).Select(i => start + (i * 1000L)).ToList();
        List<double?> Series(double value) => times.Select(_ => (double?)value).ToList();
        return new LiveTrend(times, Series(40), Series(55), Series(80), Series(42), Series(3), Series(1000), Series(200), times.Select(_ => (double?)null).ToList(), Series(70), Series(84));
    }

    public static LiveSnapshot Live() => new(
        new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero),
        true,
        new LiveCpu(new LiveMetric(42.5), new LiveMetric(4200), new LiveMetric(null, "DriverMissing"), new LiveMetric(null, "DriverMissing"), 16),
        new LiveMemory(new LiveMetric(55), new LiveMetric(17_000_000_000), new LiveMetric(32_000_000_000), new LiveMetric(40), new LiveMetric(0)),
        [],
        [],
        [],
        new LiveProcesses(1, [new LiveProcess(1234, "game.exe", 25, 1_000_000, 900_000, 80, null, false)], [], [], []),
        [new LiveEvent(new DateTimeOffset(2026, 10, 6, 11, 59, 0, TimeSpan.Zero), "windows", "Display driver reset", "Gpu", Severity.High)],
        null,
        new LiveAgent(new LiveMetric(0.1), new LiveMetric(80_000_000), new LiveMetric(12), 600, 55));
}
