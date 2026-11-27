using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PcDiag.Collectors.Gaming;
using PcDiag.Collectors.Latency;
using PcDiag.Collectors.Network;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Tracing;

namespace PcDiag.Collectors.IntegrationTests.Tracing;

public sealed class QualityCollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 26, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Dpcs_and_isrs_are_timed_and_put_in_the_driver_whose_code_holds_their_routine()
    {
        // A clock of 10 MHz: 1 tick is 0.1 µs.
        var counter = new DriverLatencyCounter(10_000_000);
        counter.Add(isr: false, routine: 0x1000_0100, started: 0, ended: 1_500);   // 150 µs in nvlddmkm
        counter.Add(isr: false, routine: 0x1000_0200, started: 10, ended: 12_010); // 1.2 ms in nvlddmkm (another routine)
        counter.Add(isr: true, routine: 0x2000_0050, started: 0, ended: 6_000);    // 600 µs ISR in ndis
        counter.Add(isr: false, routine: 0x9000_0000, started: 0, ended: 100);     // in no known module
        counter.Add(isr: false, routine: 0x1000_0100, started: 50, ended: 40);     // ends before it starts: ignored

        var drivers = counter.ByDriver([new KernelModule(0x1000_0000, 0x10_0000, "nvlddmkm.sys"), new KernelModule(0x2000_0000, 0x1000, "ndis.sys")]);

        counter.Events.ShouldBe(4);
        drivers.Select(d => d.Driver).ShouldBe(["nvlddmkm.sys", "ndis.sys", "unknown"]);
        var nvidia = drivers[0];
        nvidia.Dpcs.ShouldBe(2);
        nvidia.DpcMaxUs.ShouldBe(1200);
        nvidia.DpcTotalUs.ShouldBe(1350);
        nvidia.Over500Us.ShouldBe(1);
        nvidia.Over1Ms.ShouldBe(1);
        drivers[1].Isrs.ShouldBe(1);
        drivers[1].IsrMaxUs.ShouldBe(600);
        drivers[1].LongestUs.ShouldBe(600);
    }

    [Fact]
    public void A_routine_is_in_a_module_only_within_its_size_or_before_the_next_module()
    {
        var modules = new List<KernelModule> { new(0x1000, 0x100, "a.sys"), new(0x2000, 0, "b.sys"), new(0x3000, 0x10, "c.sys") };

        DriverLatencyCounter.ModuleOf(modules, 0x1050).ShouldBe("a.sys");
        DriverLatencyCounter.ModuleOf(modules, 0x1100).ShouldBeNull(); // past a.sys's end
        DriverLatencyCounter.ModuleOf(modules, 0x2FFF).ShouldBe("b.sys"); // no size: up to the next module
        DriverLatencyCounter.ModuleOf(modules, 0x0FFF).ShouldBeNull();
        DriverLatencyCounter.ModuleOf(modules, 0x3020).ShouldBeNull();
    }

    [Fact]
    public void The_kernel_module_list_is_read_with_its_x64_layout()
    {
        // RTL_PROCESS_MODULES: the count (padded to 8), then 296-byte entries.
        var data = new byte[8 + (2 * 296)];
        BitConverter.TryWriteBytes(data.AsSpan(0), 2u);
        void Entry(int index, ulong imageBase, uint size, string path)
        {
            var entry = data.AsSpan(8 + (index * 296), 296);
            BitConverter.TryWriteBytes(entry[16..], imageBase);
            BitConverter.TryWriteBytes(entry[24..], size);
            BitConverter.TryWriteBytes(entry[38..], (ushort)(path.LastIndexOf('\\') + 1));
            Encoding.ASCII.GetBytes(path).CopyTo(entry[40..]);
        }

        Entry(0, 0xFFFFF800_00000000, 0x0100_0000, @"\SystemRoot\system32\ntoskrnl.exe");
        Entry(1, 0xFFFFF800_10000000, 0x0200_0000, @"\SystemRoot\System32\DriverStore\FileRepository\nv_dispi.inf_amd64\nvlddmkm.sys");

        var modules = EtwDriverLatency.ParseModules(data);

        modules.ShouldBe([new KernelModule(0xFFFFF800_00000000, 0x0100_0000, "ntoskrnl.exe"), new KernelModule(0xFFFFF800_10000000, 0x0200_0000, "nvlddmkm.sys")]);
        EtwDriverLatency.ParseModules(data.AsSpan(0, 8 + 296)).Count.ShouldBe(1); // a short buffer stops at the last whole entry
    }

    [Fact]
    public void The_kernel_events_that_are_dpcs_and_isrs()
    {
        EtwDriverLatency.Kind(68).ShouldBe((true, false)); // DPC
        EtwDriverLatency.Kind(66).ShouldBe((true, false)); // threaded DPC
        EtwDriverLatency.Kind(69).ShouldBe((true, false)); // timer DPC
        EtwDriverLatency.Kind(67).ShouldBe((true, true));  // ISR
        EtwDriverLatency.Kind(50).ShouldBe((true, true));  // message-signaled ISR
        EtwDriverLatency.Kind(46).ShouldBe((false, false)); // a profile sample
    }

    [Fact]
    public void A_tcp_retransmit_counts_one_segment_for_its_process()
    {
        var payload = new byte[24];
        BitConverter.TryWriteBytes(payload.AsSpan(0), 4242u);
        BitConverter.TryWriteBytes(payload.AsSpan(4), 1460u);

        EtwProcessIo.Network(14, payload).ShouldBe(new EtwProcessIo.Transfer(4242, EtwProcessIo.Kind.Retransmit, 1));
        EtwProcessIo.Network(30, payload).ShouldBe(new EtwProcessIo.Transfer(4242, EtwProcessIo.Kind.Retransmit, 1));
        EtwProcessIo.Network(14, payload.AsSpan(0, 4)).ShouldBeNull();
    }

    [Fact]
    public void Frames_count_only_for_games_drawing_enough_frames()
    {
        var collector = new FrameCollector([], new Options<RecorderOptions>(new RecorderOptions()));
        FrameStats Stats(string name, double fps, bool foreground = true) => new(10, name, fps, 1000 / fps, null, foreground, 1000 / fps, 0);

        collector.Sample(Stats("ArkAscended", 90), T0)!.Game.ShouldBe("ArkAscended");
        collector.Sample(Stats("msedge", 60), T0).ShouldBeNull(); // a browser in full screen
        collector.Sample(Stats("LockApp", 60), T0).ShouldBeNull();
        collector.Sample(Stats("ArkAscended", 90, foreground: false), T0).ShouldBeNull(); // not the app in front

        // A maximized editor redrawing its cursor twice a second is no game.
        var editor = new FrameCollector([], new Options<RecorderOptions>(new RecorderOptions()));
        editor.Sample(Stats("Code", 2), T0).ShouldBeNull();
    }

    [Fact]
    public void A_game_has_a_usual_frame_rate_after_30_s_and_a_drop_below_it()
    {
        var collector = new FrameCollector([], new Options<RecorderOptions>(new RecorderOptions()));
        FrameSample? last = null;
        for (var s = 0; s < 40; s++)
        {
            last = collector.Sample(new FrameStats(10, "Game", 120, 8.3, null, true, 9, 0), T0.AddSeconds(s));
        }

        last!.NormalFps.ShouldBe(120);
        var drop = collector.Sample(new FrameStats(10, "Game", 48, 20.8, null, true, 40, 2), T0.AddSeconds(40))!;
        drop.NormalFps.ShouldBe(120);
        drop.DropPercent.ShouldBe(60);
        drop.Stutters.ShouldBe(2);

        // Below the game minimum during the drop: still recorded, its usual rate is a game's.
        collector.Sample(new FrameStats(10, "Game", 5, 200, null, true, 400, 1), T0.AddSeconds(41))!.Fps.ShouldBe(5);
    }

    [Fact]
    public void Windowed_games_are_recognized_and_the_users_choice_comes_first()
    {
        var options = new RecorderOptions { Games = ["MyIndieGame"], NotGames = ["ArkAscended"] };
        var recognizer = new GameRecognizer(new Options<RecorderOptions>(options), new PcDiag.Collectors.Gpu.ProcessGpuActivity());

        recognizer.Recognize(Environment.ProcessId, "MyIndieGame", fullScreen: false).ShouldBe("user");
        recognizer.Recognize(Environment.ProcessId, "ArkAscended", fullScreen: true).ShouldBeNull(); // "Not a game" wins over full screen
        recognizer.Recognize(Environment.ProcessId, "msedge", fullScreen: true).ShouldBeNull();
        recognizer.Recognize(Environment.ProcessId, "SomeGame", fullScreen: true).ShouldBe("full screen");
        recognizer.Recognize(Environment.ProcessId, null, fullScreen: true).ShouldBeNull();

        var windows = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Games\Indie\Indie.exe" };
        GameRecognizer.Classify(@"d:\games\indie\INDIE.EXE", windows, 0).ShouldBe("windows");
        GameRecognizer.Classify(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", windows, 0).ShouldBe("library");
        GameRecognizer.Classify(@"E:\SteamLibrary\steamapps\common\ARK Survival Ascended\ShooterGame\Binaries\Win64\ArkAscended.exe", windows, 0).ShouldBe("library");
        GameRecognizer.Classify(@"C:\Emulators\emu.exe", windows, 60).ShouldBe("graphics"); // keeps the 3D engine busy
        GameRecognizer.Classify(@"C:\Program Files\Microsoft VS Code\Code.exe", windows, 3).ShouldBeNull();
        GameRecognizer.Classify(null, windows, 10).ShouldBeNull();
    }

    [Fact]
    public void PresentMon_runs_for_one_process_with_its_output_read_and_its_session_named()
    {
        var arguments = PresentMonRunner.Arguments(4242);

        arguments.ShouldContain("--process_id 4242");
        arguments.ShouldContain("--output_stdout");
        arguments.ShouldContain("--qpc_time");
        arguments.ShouldContain($"--session_name {PresentMonRunner.SessionName} --stop_existing_session");
        arguments.ShouldContain("--terminate_on_proc_exit");
        arguments.ShouldContain("--no_track_input"); // no input events: less work, nothing about keys
    }

    [Fact]
    public void A_windowed_games_second_carries_the_frames_that_reached_the_screen()
    {
        var collector = new FrameCollector([], new Options<RecorderOptions>(new RecorderOptions()));
        var shown = new DisplayedStats(144, 120, 24, 33.4, 18.5, 97, 40, "Composed: Flip");

        var sample = collector.Sample(new FrameStats(10, "IndieGame", 144, 6.9, null, true, 9, 0), T0, "library", fullScreen: false, shown)!;

        sample.FullScreen.ShouldBeFalse();
        sample.Recognized.ShouldBe("library");
        sample.DisplayedFps.ShouldBe(120);
        sample.Dropped.ShouldBe(24);
        sample.WorstDisplayedMs.ShouldBe(33.4);
        sample.DisplayLatencyMs.ShouldBe(18.5);
        sample.GpuBusyPercent.ShouldBe(97);
        sample.CpuBusyPercent.ShouldBe(40);
        sample.PresentMode.ShouldBe("Composed: Flip");

        // A program the user counts as a game is counted even when it is on the list of programs that are no games.
        collector.Sample(new FrameStats(11, "chrome", 60, 16.7, null, true, 17, 0), T0, "user")!.Game.ShouldBe("chrome");
    }

    [Fact]
    public void Home_addresses_are_told_from_the_providers()
    {
        ConnectionCollector.IsPrivate(IPAddress.Parse("192.168.1.1")).ShouldBeTrue();
        ConnectionCollector.IsPrivate(IPAddress.Parse("10.20.0.1")).ShouldBeTrue();
        ConnectionCollector.IsPrivate(IPAddress.Parse("172.20.1.1")).ShouldBeTrue();
        ConnectionCollector.IsPrivate(IPAddress.Parse("172.32.1.1")).ShouldBeFalse();
        ConnectionCollector.IsPrivate(IPAddress.Parse("100.64.0.1")).ShouldBeFalse(); // carrier NAT: the provider's
        ConnectionCollector.IsPrivate(IPAddress.Parse("81.23.4.5")).ShouldBeFalse();
    }

    [Fact]
    public void A_measurement_starts_by_itself_when_drivers_hold_a_processor_or_the_game_stutters()
    {
        var collector = new DriverLatencyCollector([], new Options<RecorderOptions>(new RecorderOptions()), NullLogger<DriverLatencyCollector>.Instance);
        SampleFrame Frame(int second, double dpc, int stutters = 0) => new(second, T0.AddSeconds(second), Cpu: Cpu(dpc),
            Frames: stutters > 0 ? new FrameSample(T0.AddSeconds(second), 1, "Game", 60, 16, 80, stutters, null, 60, true) : null);

        collector.Wanted(Frame(0, 24)).ShouldBeNull(); // a game at a high frame rate: normal
        collector.Wanted(Frame(1, 32)).ShouldBeNull();
        collector.Wanted(Frame(2, 35)).ShouldBeNull();
        collector.Wanted(Frame(3, 31)).ShouldBe("dpc"); // three seconds in a row at 30 % or more
        collector.Wanted(Frame(4, 60)).ShouldBeNull(); // counted again from there
        collector.Wanted(Frame(5, 2)).ShouldBeNull();

        collector.Wanted(Frame(10, 1, stutters: 3)).ShouldBeNull();
        collector.Wanted(Frame(14, 1, stutters: 2)).ShouldBe("stutter"); // 5 within 10 s
        collector.Wanted(Frame(30, 1, stutters: 4)).ShouldBeNull();

        // Without a tracer nothing can be measured, on request or otherwise.
        collector.MeasureNow().ShouldBeFalse();
        collector.Problem.ShouldNotBeNull();
    }

    private static CpuSample Cpu(double busiestDpc)
    {
        var none = Reading<double>.Unavailable(Availability.Unsupported);
        var cores = new List<CpuCoreSample>
        {
            new(0, Reading<double>.Of(20), none, Reading<double>.Of(busiestDpc), Reading<double>.Of(1)),
            new(1, Reading<double>.Of(20), none, Reading<double>.Of(0.5), Reading<double>.Of(0.2)),
        };
        return new CpuSample(T0, none, none, none, none, none, none, none, Reading<long>.Unavailable(Availability.Unsupported), none, none, none, none, none, cores);
    }

    private sealed class Options<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
