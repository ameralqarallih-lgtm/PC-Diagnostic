using PcDiag.Collectors.Control;
using PcDiag.Core.Control;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>Safe mode leaves what other programs use; the clean-up tree never leaves a target's folders.</summary>
public sealed class SafeModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "safe-" + Guid.NewGuid().ToString("N"));

    public SafeModeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ControlProcess Process(int pid, string name, string kind = "background", double? cpu = 0, double? gpu = 0, double? net = 0, double? disk = 0) =>
        new(pid, 1, 4, name, null, null, kind, ControlProtection.None, null, cpu, 100, 100, 1, 1, null, false, 1, [], DateTimeOffset.UnixEpoch, gpu, 0, net, disk);

    [Fact]
    public void A_program_in_use_is_left_and_the_reason_said()
    {
        var game = Process(10, "game.exe", "app", cpu: 30, gpu: 80);
        var editor = Process(11, "notepad.exe", "app");
        var downloader = Process(12, "updater.exe", cpu: 9, net: 2_000_000);
        var idle = Process(13, "launcher.exe");
        var guard = new ResourceGuard([game, editor, downloader, idle], foregroundPid: 10);

        guard.EndBlocked(game).ShouldBe("it is the program in front");
        guard.EndBlocked(editor).ShouldBe("it has a window open (unsaved work could be lost)");
        guard.EndBlocked(downloader).ShouldBe("it is busy (9 % CPU)");
        guard.EndBlocked(idle).ShouldBeNull();
        guard.TrimBlocked(idle).ShouldBeNull();
        guard.PriorityBlocked(downloader).ShouldBe("it is transferring over the network");
        guard.GpuRestartBlocked().ShouldBe("game.exe is using the GPU (80 %); restarting the driver could crash it");
        guard.StandbyBlocked(5).ShouldStartWith("game.exe is drawing");
        guard.CleanBlocked("shader-cache").ShouldBe("game.exe is drawing with the GPU");

        var quiet = new ResourceGuard([idle, Process(20, "chrome.exe", "app")], 0);
        quiet.GpuRestartBlocked().ShouldBeNull();
        quiet.StandbyBlocked(80).ShouldBe("a drive is busy (80 % active)");
        quiet.CleanBlocked("browser-cache").ShouldBe("chrome.exe is running");
        quiet.CleanBlocked("user-temp").ShouldBeNull();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("sub", true)]
    [InlineData(@"sub\deeper", true)]
    [InlineData("..", false)]
    [InlineData(@"sub\..\..\elsewhere", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"\\server\share", false)]
    public void The_tree_stays_inside_the_targets_folder(string path, bool inside)
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deeper"));
        (Cleanup.Inside(new CleanupRoot(_root), path) is not null).ShouldBe(inside);
    }
}
