using System.Diagnostics;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Collectors.IntegrationTests;

public sealed class DesktopShellTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));

    public DesktopShellTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Explorer_starts_the_program_as_its_own_child_without_administrator_rights()
    {
        Process.GetProcessesByName("explorer").ShouldNotBeEmpty("the desktop shell must be running for this test");
        var output = Path.Combine(_directory, "whoami.txt");

        // whoami /groups lists "Mandatory Label\Medium Mandatory Level" for a process without administrator rights.
        var started = new DesktopShell().TryStart(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/c whoami.exe /groups > \"{output}\"", // a /c line starting with a quote would lose its outer quotes
            _directory,
            hidden: true);

        started.ShouldBeTrue();

        // The integrity label is near the end of whoami's output: wait until it has been written.
        var groups = string.Empty;
        for (var i = 0; i < 100 && !groups.Contains("Mandatory Level", StringComparison.Ordinal); i++)
        {
            await Task.Delay(100);
            try
            {
                using var reader = new StreamReader(new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                groups = await reader.ReadToEndAsync();
            }
            catch (IOException)
            {
                // Not created yet.
            }
        }

        groups.ShouldContain("Medium Mandatory Level");
        groups.ShouldNotContain("High Mandatory Level");
    }
}
