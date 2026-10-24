using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Storage.Files;

namespace PcDiag.Storage.Tests;

/// <summary>The storage shared by the Web host and the storage host, and the storage host's upkeep.</summary>
public sealed class StorageUpkeepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "upkeep-" + Guid.NewGuid().ToString("N"));

    public StorageUpkeepTests() => Directory.CreateDirectory(_root);

    private string Data => Path.Combine(_root, "data");

    private string LayoutFile => Path.Combine(Data, "overlay.json");

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

    private FileStore Store(TimeSpan? patience = null) => new(Data, Path.Combine(Data, "overlay-images"), NullLogger.Instance) { LockPatience = patience ?? TimeSpan.FromMinutes(10) };

    private static async Task Until(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().ShouldBeTrue(because);
    }

    [Fact]
    public async Task Two_processes_share_the_locations_the_job_and_run_one_job_at_a_time()
    {
        using var web = Store();
        using var host = Store();
        web.Initialize();
        File.Exists(Path.Combine(Data, "storage", "locations.json")).ShouldBeTrue();

        // A location the page adds is the storage host's at once.
        var folder = Path.Combine(_root, "share");
        var (added, _) = await web.AddAsync(new StorageLocation("x", "Share", LocationKinds.Folder, [FileKinds.Backups], Path: folder), null, CancellationToken.None);
        host.Locations.Select(l => l.Id).ShouldBe(["pc", added!.Id]);
        await Until(() => !web.Job.Running, "the page's job ends");

        // The storage host's job is what the page shows (the newer of the two).
        await web.PutAsync(FileKinds.Backups, "overlay-20261009-100000-000.json", new MemoryStream("{}"u8.ToArray()), CancellationToken.None);
        File.Delete(Path.Combine(folder, "PcDiagnostic", "backups", "overlay-20261009-100000-000.json"));
        (await host.SyncAsync(CancellationToken.None)).ShouldBeNull();
        web.Job.ShouldBe(host.Job);
        web.Job.Copied.ShouldBe(1);

        // While one runs a job, the other's waits for it.
        using (new FileStream(Path.Combine(Data, "storage", "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var waiting = host.SyncAsync(CancellationToken.None);
            await Task.Delay(600);
            waiting.IsCompleted.ShouldBeFalse("the other process's job runs first");
            await Task.Delay(100);
        }

        await Until(() => !host.Job.Running, "the waiting job runs once the other ends");

        // A job that cannot wait long enough goes ahead (copies can be repeated safely).
        using var hurried = Store(TimeSpan.FromMilliseconds(300));
        using (new FileStream(Path.Combine(Data, "storage", "sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            (await hurried.SyncAsync(CancellationToken.None)).ShouldBeNull();
        }

        // A removal made by the page is the storage host's too.
        web.Update("pc", null, [.. FileKinds.All], null).ShouldBeNull();
        (await web.RemoveAsync(added.Id, CancellationToken.None)).ShouldBeNull();
        host.Locations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_upkeep_sets_the_storage_up_and_keeps_the_layout_as_it_was_before_it_changes_outside_the_editor()
    {
        Directory.CreateDirectory(Data);
        await File.WriteAllTextAsync(LayoutFile, "{\"version\":1,\"enabled\":true}");
        using var store = Store();
        using var stop = new CancellationTokenSource();
        var upkeep = new StorageUpkeep(store, LayoutFile, NullLogger.Instance) { Settle = TimeSpan.FromMilliseconds(200), SyncInterval = TimeSpan.FromHours(1) };
        var run = upkeep.RunAsync(stop.Token);

        // Set up on its first start.
        await Until(() => File.Exists(Path.Combine(Data, "storage", "locations.json")), "the storage is set up");
        Directory.Exists(Path.Combine(Data, "files", "drivers")).ShouldBeTrue();
        Directory.Exists(Path.Combine(Data, "overlay-images")).ShouldBeTrue();

        // Arranged on the screens: the layout before is kept.
        await Task.Delay(300);
        await File.WriteAllTextAsync(LayoutFile, "{\"version\":1,\"enabled\":false}");
        await Until(() => Directory.Exists(Path.Combine(Data, "files", "backups")) && Directory.GetFiles(Path.Combine(Data, "files", "backups")).Length == 1, "the layout before is kept");
        var kept = Directory.GetFiles(Path.Combine(Data, "files", "backups")).Single();
        Path.GetFileName(kept).ShouldStartWith(LayoutBackups.Prefix);
        (await File.ReadAllTextAsync(kept)).ShouldContain("\"enabled\":true");

        // Changed again within 5 minutes: not kept again (the editor's saves follow the same rule).
        await File.WriteAllTextAsync(LayoutFile, "{\"version\":1,\"enabled\":true,\"intervalMs\":2000}");
        await Task.Delay(1000);
        Directory.GetFiles(Path.Combine(Data, "files", "backups")).Length.ShouldBe(1);

        await stop.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task A_backup_is_not_kept_twice_with_the_same_content_but_always_when_asked_for_a_different_one()
    {
        using var store = Store();
        var layout = Encoding.UTF8.GetBytes("{\"version\":1}");
        (await LayoutBackups.KeepAsync(store, layout, always: false, NullLogger.Instance, CancellationToken.None)).ShouldBeTrue();
        (await LayoutBackups.KeepAsync(store, layout, always: true, NullLogger.Instance, CancellationToken.None)).ShouldBeFalse("the same as the newest");
        (await LayoutBackups.KeepAsync(store, Encoding.UTF8.GetBytes("{\"version\":1,\"enabled\":true}"), always: false, NullLogger.Instance, CancellationToken.None)).ShouldBeFalse("too soon");
        await Task.Delay(5);
        (await LayoutBackups.KeepAsync(store, Encoding.UTF8.GetBytes("{\"version\":1,\"enabled\":true}"), always: true, NullLogger.Instance, CancellationToken.None)).ShouldBeTrue();
        (await LayoutBackups.ListAsync(store, CancellationToken.None)).Count.ShouldBe(2);
        LayoutBackups.IsName("overlay-20261009-100000-000.json").ShouldBeTrue();
        LayoutBackups.IsName("settings.json").ShouldBeFalse();
        LayoutBackups.IsName("overlay-..json").ShouldBeTrue("a name the store accepts; it holds no path");
    }
}
