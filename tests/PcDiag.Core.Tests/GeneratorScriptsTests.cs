namespace PcDiag.Core.Tests;

public sealed class GeneratorScriptsTests
{
    /// <summary>
    /// The character generator's scripts have no carriage return inside a line: Python reads one as a line break, and a
    /// string broken by it stopped the generator from starting at all ("actively refused").
    /// </summary>
    [Fact]
    public void The_generator_scripts_have_no_stray_line_breaks()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PcDiagnostic.slnx")))
        {
            root = root.Parent;
        }

        var folder = Path.Combine(root.ShouldNotBeNull().FullName, "tools", "character-gen");
        foreach (var file in Directory.GetFiles(folder, "*.py"))
        {
            var lines = File.ReadAllText(file).Split('\n');
            foreach (var (line, number) in lines.Select((l, i) => (l, i + 1)))
            {
                line.TrimEnd('\r').Contains('\r', StringComparison.Ordinal).ShouldBeFalse($"{Path.GetFileName(file)}:{number}");
            }
        }
    }
}
