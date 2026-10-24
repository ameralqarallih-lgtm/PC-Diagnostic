using System.Diagnostics;
using PcDiag.Collectors.Control;
using PcDiag.Platform.Windows.Kernel;
using PcDiag.Platform.Windows.Power;
using PcDiag.Platform.Windows.Processes;
using PcDiag.Platform.Windows.Services;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Collectors.IntegrationTests;

/// <summary>The clean-up never leaves its folders, keeps recent files when asked, and keeps the folders themselves.</summary>
public sealed class CleanupTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "pcdiag-outside-" + Guid.NewGuid().ToString("N"));

    public CleanupTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cache", "deep"));
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _root, _outside })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }
        }
    }

    private string File(string relative, TimeSpan age, int bytes = 100)
    {
        var path = Path.Combine(_root, relative);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        System.IO.File.SetLastWriteTimeUtc(path, Now.UtcDateTime - age);
        return path;
    }

    [Fact]
    public void Old_files_go_recent_ones_and_folders_stay()
    {
        var old = File(@"cache\old.tmp", TimeSpan.FromDays(3), 1000);
        var deep = File(@"cache\deep\older.tmp", TimeSpan.FromDays(9), 500);
        var recent = File(@"cache\new.tmp", TimeSpan.FromMinutes(5));
        var target = new CleanupDefinition("test", "Test", "Test", true, null, TimeSpan.FromDays(1), () => [new CleanupRoot(Path.Combine(_root, "cache"))]);

        Cleanup.Measure(target, Now).ShouldBe((1500L, 2));
        var (freed, removed, left) = Cleanup.Clean(target, Now);

        (freed, removed, left).ShouldBe((1500L, 2, 0));
        System.IO.File.Exists(old).ShouldBeFalse();
        System.IO.File.Exists(deep).ShouldBeFalse();
        System.IO.File.Exists(recent).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "cache")).ShouldBeTrue();
        Directory.Exists(Path.Combine(_root, "cache", "deep")).ShouldBeFalse(); // emptied sub-folders are tidied away
    }

    [Fact]
    public void A_junction_inside_a_cache_is_never_followed()
    {
        var precious = Path.Combine(_outside, "document.docx");
        System.IO.File.WriteAllText(precious, "keep me");
        var junction = Path.Combine(_root, "cache", "link");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", junction, _outside]) { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
        }

        Directory.Exists(junction).ShouldBeTrue();
        var target = new CleanupDefinition("test", "Test", "Test", true, null, TimeSpan.Zero, () => [new CleanupRoot(Path.Combine(_root, "cache"))]);
        Cleanup.Clean(target, Now);

        System.IO.File.Exists(precious).ShouldBeTrue();
        Directory.Delete(junction); // the link only
    }

    [Fact]
    public void Drives_profiles_and_system_folders_are_never_roots()
    {
        Cleanup.IsSafeRoot(new CleanupRoot(@"C:\")).ShouldBeFalse();
        Cleanup.IsSafeRoot(new CleanupRoot(@"C:\Windows")).ShouldBeFalse();
        Cleanup.IsSafeRoot(new CleanupRoot(@"C:\Windows\System32")).ShouldBeFalse();
        Cleanup.IsSafeRoot(new CleanupRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))).ShouldBeFalse();
        Cleanup.IsSafeRoot(new CleanupRoot(@"C:\Windows", "MEMORY.DMP", Recursive: false)).ShouldBeTrue();
        Cleanup.IsSafeRoot(new CleanupRoot(@"C:\Windows\Temp")).ShouldBeTrue();
    }

    [Fact]
    public void Every_target_measures_without_failing()
    {
        var targets = Cleanup.Scan(Now);

        targets.Select(t => t.Id).ShouldBe(["user-temp", "windows-temp", "update-cache", "delivery-optimization", "thumbnails", "shader-cache", "browser-cache", "crash-dumps", "recycle-bin"]);
        targets.ShouldAllBe(t => t.Bytes >= 0 && t.Files >= 0);
    }
}

/// <summary>Reading commands, startup state and the PC's settings.</summary>
public sealed class ControlPlatformTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\tool.exe /start", @"C:\Tools\tool.exe")]
    [InlineData(@"C:\Program Files\No Quotes\app.exe -x", @"C:\Program Files\No Quotes\app.exe")]
    public void The_program_is_found_in_a_command_line(string command, string program)
    {
        StartupItems.ProgramOf(command).ShouldBe(program);
        ServiceControl.ProgramOf(command).ShouldBe(program);
    }

    [Fact]
    public void Service_paths_relative_to_Windows_are_resolved()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        ServiceControl.ProgramOf(@"\SystemRoot\System32\drivers\foo.sys").ShouldBe(Path.Combine(windows, @"System32\drivers\foo.sys"));
        ServiceControl.ProgramOf(@"%SystemRoot%\system32\svchost.exe -k netsvcs -p").ShouldBe(Path.Combine(windows, @"system32\svchost.exe"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void Startup_entries_are_enabled_unless_Task_Manager_turned_them_off(byte[]? approved, bool enabled) => StartupItems.IsEnabled(approved).ShouldBe(enabled);

    [Fact]
    public void This_PC_s_power_plans_memory_and_services_are_read()
    {
        PowerTuning.Schemes().ShouldContain(s => s.Active);
        var (standby, free) = MemoryLists.Query();
        standby.ShouldNotBeNull();
        free.ShouldNotBeNull();
        ServiceControl.QueryDetails("EventLog").ShouldNotBeNull().Description.ShouldNotBeNullOrEmpty();
        ServiceControlManager.Enumerate().ShouldContain(s => s.Name == "EventLog" && (s.ControlsAccepted & 1) != 0);
        StartupItems.List().ShouldAllBe(i => i.Id.Contains(':'));
    }

    [Fact]
    public void A_process_is_ended_only_with_its_own_start_time()
    {
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        var started = child.StartTime.ToUniversalTime().Ticks;

        ProcessControl.Terminate(child.Id, started - TimeSpan.TicksPerSecond).Ok.ShouldBeFalse(); // another program's start time: refused
        child.HasExited.ShouldBeFalse();
        ProcessControl.SetPriority(child.Id, started, "BelowNormal").Ok.ShouldBeTrue();
        ProcessControl.GetPriority(child.Id).ShouldBe("BelowNormal");
        ProcessControl.Terminate(child.Id, started).Ok.ShouldBeTrue();
        child.WaitForExit(5000).ShouldBeTrue();
        ProcessControl.IsCritical(Environment.ProcessId).ShouldBeFalse();
    }
}
