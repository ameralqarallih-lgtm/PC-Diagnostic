using PcDiag.Core.Configuration;
using PcDiag.Core.Incidents;

namespace PcDiag.Core.Tests;

public class DataPathsTests
{
    [Fact]
    public void Default_is_under_local_app_data()
    {
        var paths = new DataPaths(null);

        paths.Root.ShouldBe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcDiagnostic"));
        paths.TelemetryDatabase.ShouldEndWith("telemetry.db");
        paths.IncidentIndexDatabase.ShouldEndWith("incidents.db");
    }

    [Fact]
    public void Configured_directory_is_normalized()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcdiag", "..", "pcdiag-data");

        new DataPaths(root).Root.ShouldBe(Path.GetFullPath(root));
    }

    [Theory]
    [InlineData(@"\\server\share\pcdiag")]
    [InlineData(@"\\?\C:\pcdiag")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData("relative\\folder")]
    [InlineData("C:relative")]
    public void Rejects_unsafe_directories(string directory)
    {
        DataPaths.ValidateDirectory(directory).ShouldNotBeNull();
        Should.Throw<ArgumentException>(() => new DataPaths(directory));
    }

    [Fact]
    public void Incident_folder_comes_from_the_key()
    {
        var paths = new DataPaths(Path.Combine(Path.GetTempPath(), "pcdiag-data"));
        var key = IncidentKey.Create(new DateOnly(2026, 9, 28), 27);

        paths.GetIncidentDirectory(key).ShouldBe(Path.Combine(paths.IncidentsDirectory, "INC-20260928-00027"));
    }
}
