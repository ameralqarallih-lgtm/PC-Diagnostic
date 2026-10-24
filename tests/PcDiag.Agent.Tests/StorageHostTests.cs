using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Agent.Storage;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Agent.Tests;

/// <summary>The storage host the recorder starts with it: connected by a one-time token, set up, ended with the recorder.</summary>
public sealed partial class StorageHostTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "storage-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // The host's log may still be closing.
        }
    }

    [Fact]
    public async Task The_recorder_starts_the_storage_host_which_sets_the_storage_up_and_ends_with_the_recorder()
    {
        var launcher = new InProcessLauncher();
        var link = new StorageHostLink(launcher, @"C:\PC Diagnostic\agent\PcDiag.Agent.exe", _data + "\\", NullLogger.Instance);
        link.Start();
        await Until(() => link.Connected, "the host connects with its token");

        // Started as the storage host, for this data folder, without administrator rights (through the launcher).
        var (file, arguments) = launcher.Calls.ShouldHaveSingleItem();
        file.ShouldEndWith("PcDiag.Agent.exe");
        arguments.ShouldStartWith(StorageHostMode.Verb + " --pipe PcDiag.Storage.");
        arguments.ShouldEndWith($"--data \"{_data}\"");

        // Set up on its first start; one host at a time.
        await Until(() => File.Exists(Path.Combine(_data, "storage", "locations.json")), "the storage is set up");
        (await StorageHostMode.RunAsync(["storage-host", "--pipe", "PcDiag.Storage.none", "--token", "x", "--data", _data])).ShouldBe(1, "no recorder offers that pipe");

        // The recorder stops: the host ends.
        await link.DisposeAsync();
        (await launcher.Hosts.Single().WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(0);
        link.Started.ShouldBe(1);
    }

    [Fact]
    public async Task Something_without_the_token_is_turned_away_and_bad_arguments_are_refused()
    {
        (await StorageHostMode.RunAsync(["storage-host"])).ShouldBe(64);
        (await StorageHostMode.RunAsync(["storage-host", "--pipe", "p", "--token", "t", "--data", "relative\\folder"])).ShouldBe(64);

        var launcher = new InProcessLauncher { Token = "not-the-token" };
        var link = new StorageHostLink(launcher, @"C:\PC Diagnostic\agent\PcDiag.Agent.exe", _data, NullLogger.Instance);
        link.Start();
        await Until(() => link.Problem is not null, "the impostor is noticed");
        link.Problem!.ShouldContain("Something other than the storage host connected");
        link.Connected.ShouldBeFalse();
        await link.DisposeAsync();
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().ShouldBeTrue(because);
    }

    /// <summary>Runs the storage host in this process instead of asking Explorer to start it.</summary>
    private sealed partial class InProcessLauncher : IUnelevatedLauncher
    {
        public List<(string File, string Arguments)> Calls { get; } = [];

        public List<Task<int>> Hosts { get; } = [];

        /// <summary>A token to send instead of the one given (an impostor).</summary>
        public string? Token { get; init; }

        public bool TryStart(string file, string? arguments, string? workingDirectory, bool hidden)
        {
            Calls.Add((file, arguments ?? string.Empty));
            var args = Words().Matches(arguments ?? string.Empty).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value).ToArray();
            if (Token is not null)
            {
                args[Array.IndexOf(args, "--token") + 1] = Token;
            }

            Hosts.Add(Task.Run(() => StorageHostMode.RunAsync(args)));
            return true;
        }

        [GeneratedRegex("\"([^\"]*)\"|\\S+")]
        private static partial Regex Words();
    }
}
