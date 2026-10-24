using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Agent.Tray;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Agent.Tests;

public sealed class DashboardLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly DataPaths _paths;

    public DashboardLauncherTests()
    {
        _paths = new DataPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private readonly FakeShell _shell = new();

    private DashboardLauncher Launcher(bool elevated = false, string? webHostPath = null) =>
        new(_paths, new AgentOptions { WebHostPath = webHostPath }, new ElevationInfo(elevated), TimeProvider.System, NullLogger.Instance, _shell);

    [Fact]
    public void No_web_json_means_the_dashboard_is_not_running() =>
        Launcher().RunningBaseUrl().ShouldBeNull();

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"ProcessId": 4, "BaseUrl": "http://example.com:5180/"}""")]
    [InlineData("""{"ProcessId": 4, "BaseUrl": "file:///C:/Windows/"}""")]
    public void Malformed_or_non_loopback_state_is_ignored(string json)
    {
        File.WriteAllText(_paths.WebStateFile, json);

        Launcher().RunningBaseUrl().ShouldBeNull();
    }

    [Fact]
    public void A_web_json_left_by_another_process_is_ignored()
    {
        // This test process is alive but is not PcDiag.Web.
        File.WriteAllText(_paths.WebStateFile, $$"""{"Port": 5180, "ProcessId": {{Environment.ProcessId}}, "BaseUrl": "http://127.0.0.1:5180/"}""");

        Launcher().RunningBaseUrl().ShouldBeNull();
    }

    [Fact]
    public async Task An_elevated_agent_starts_the_web_host_only_through_the_desktop_shell()
    {
        _shell.Works = false;

        var launch = await Launcher(elevated: true, webHostPath: Environment.ProcessPath).OpenAsync(CancellationToken.None);

        launch.Result.ShouldBe(DashboardLaunchResult.NotRunningElevated);
        _shell.Started.ShouldHaveSingleItem().File.ShouldBe(Environment.ProcessPath);
        _shell.Started[0].Arguments.ShouldBe($"\"--Storage:DataDirectory={_root}\"");
        _shell.Started[0].Hidden.ShouldBeTrue();
        Directory.Exists(_paths.LaunchDirectory).ShouldBeFalse(); // no ticket was issued either
    }

    [Fact]
    public void The_web_host_is_found_beside_the_agent_or_in_the_installed_web_folder()
    {
        Launcher(webHostPath: Path.Combine(_root, "missing.exe")).WebHostPath().ShouldBeNull();
        Launcher(webHostPath: Environment.ProcessPath).WebHostPath().ShouldBe(Environment.ProcessPath);
    }

    [Fact]
    public async Task A_missing_web_host_is_reported()
    {
        var launch = await Launcher(webHostPath: Path.Combine(_root, "PcDiag.Web.exe")).OpenAsync(CancellationToken.None);

        launch.Result.ShouldBe(DashboardLaunchResult.Failed);
        launch.Problem!.ShouldContain("PcDiag.Web.exe was not found");
    }

    private sealed class FakeShell : IUnelevatedLauncher
    {
        public bool Works { get; set; } = true;

        public List<(string File, string? Arguments, bool Hidden)> Started { get; } = [];

        public bool TryStart(string file, string? arguments, string? workingDirectory, bool hidden)
        {
            Started.Add((file, arguments, hidden));
            return Works;
        }
    }
}
