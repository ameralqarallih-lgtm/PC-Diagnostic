using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Storage.Files;

namespace PcDiag.Web.Tests;

public sealed class StoreKeeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "keeper-" + Guid.NewGuid().ToString("N"));

    public StoreKeeperTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void The_apps_files_are_kept_by_kind_and_secrets_never()
    {
        var paths = new DataPaths(_root);
        void Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
        }

        Write(paths.SettingsFile);
        Write(paths.OverlayFile);
        Write(Path.Combine(_root, "devices.json"));
        Write(Path.Combine(_root, "lan-certificate.bin"));
        Write(Path.Combine(paths.OverlayImagesDirectory, "0123456789abcdef.char"));
        Write(Path.Combine(paths.OverlayImagesDirectory, "picture.png"));
        Write(Path.Combine(paths.DiagnosticsDirectory, "d1.json"));
        Write(Path.Combine(paths.ReportsDirectory, "outbox", "r1.json"));
        Write(Path.Combine(_root, "characters", "generations", "job1", "still-2.png"));

        using var store = new FileStore(_root, paths.OverlayImagesDirectory, NullLogger.Instance);
        var keeper = new StoreKeeper(store, paths, NullLogger<StoreKeeper>.Instance);

        keeper.Wanted().Select(w => $"{w.Kind}/{w.Name}").Order(StringComparer.Ordinal).ShouldBe(
        [
            "characters/0123456789abcdef.char",
            "diagnostics/d1.json",
            "drafts/job1-still-2.png",
            "reports/r1.json",
            "settings/overlay.json",
            "settings/settings.json",
        ]);
    }
}
