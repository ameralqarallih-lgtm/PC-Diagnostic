using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Storage.Files;

namespace PcDiag.Storage.Tests;

public sealed class LocalObjectStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "local-" + Guid.NewGuid().ToString("N"));

    public LocalObjectStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string Data => Path.Combine(_root, "data");

    private FileStore Store() => new(Data, Path.Combine(Data, "overlay-images"), NullLogger.Instance);

    [Fact]
    public void It_runs_on_this_pc_only_on_free_ports_with_keys_made_up_for_it()
    {
        var server = LocalObjectStore.PickPorts(Path.Combine(_root, "store"));
        new[] { server.S3Port, server.MasterPort, server.VolumePort, server.FilerPort, server.AdminPort }.ShouldBe(Enumerable.Range(server.S3Port, 5));
        server.S3Port.ShouldBeGreaterThanOrEqualTo(LocalObjectStore.FirstPort);
        LocalObjectStore.Free(server.S3Port).ShouldBeTrue();

        // A port in use is passed over (its whole block).
        using (var busy = new System.Net.Sockets.TcpListener(IPAddress.Loopback, server.S3Port))
        {
            busy.Start();
            LocalObjectStore.PickPorts(_root).S3Port.ShouldBe(server.S3Port + 100);
        }

        var keys = LocalObjectStore.NewKeys();
        keys.AccessKey.Length.ShouldBe(20);
        keys.SecretKey.Length.ShouldBe(40);
        LocalObjectStore.NewKeys().ShouldNotBe(keys);

        // Bound to 127.0.0.1, every part on its own port, no admin page, no WebDAV, its one bucket and identity.
        var arguments = LocalObjectStore.Arguments(server, "C:\\s3.json");
        arguments[0].ShouldBe("mini");
        arguments.ShouldContain("-ip=127.0.0.1");
        arguments.ShouldContain("-ip.bind=127.0.0.1");
        arguments.ShouldContain($"-s3.port={server.S3Port}");
        arguments.ShouldContain($"-filer.port={server.FilerPort}");
        arguments.ShouldContain("-admin.ui=false");
        arguments.ShouldContain("-webdav=false");
        arguments.ShouldContain("-bucket=pcdiagnostic");
        arguments.ShouldContain("-s3.config=C:\\s3.json");
        LocalObjectStore.ConfigJson(keys).ShouldContain(keys.SecretKey);

        var location = LocalObjectStore.LocationFor("local", server, FileKinds.All, isDefault: true);
        (location.Endpoint, location.Bucket, location.PathStyle, location.Kind).ShouldBe(($"http://127.0.0.1:{server.S3Port}", "pcdiagnostic", true, LocationKinds.S3));
        LocalObjectStore.CheckFolder("relative").ShouldNotBeNull();
        LocalObjectStore.CheckFolder(Path.Combine(_root, "elsewhere")).ShouldBeNull();
    }

    [Fact]
    public async Task Its_program_is_downloaded_once_and_used_only_when_both_checksums_match()
    {
        // A release archive with a weed.exe in it.
        var exe = Encoding.UTF8.GetBytes("pretend weed");
        using var zipped = new MemoryStream();
        using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("weed.exe").Open();
            await entry.WriteAsync(exe);
        }

        var archive = zipped.ToArray();
        var zipSha = Convert.ToHexStringLower(SHA256.HashData(archive));
        var exeSha = Convert.ToHexStringLower(SHA256.HashData(exe));
        var asked = 0;
        using var http = new HttpClient(new Answer(() =>
        {
            asked++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        }));

        var reported = new List<double>();
        var path = await LocalObjectStore.DownloadAsync(Data, http, new SyncProgress(reported.Add), zipSha, exeSha, CancellationToken.None);
        path.ShouldBe(LocalObjectStore.DownloadedExe(Data));
        (await File.ReadAllBytesAsync(path)).ShouldBe(exe);
        reported.ShouldNotBeEmpty();
        reported[^1].ShouldBe(1);

        // There already: not downloaded again.
        await LocalObjectStore.DownloadAsync(Data, http, null, zipSha, exeSha, CancellationToken.None);
        asked.ShouldBe(1);

        // Not what was pinned (the archive, or the program in it): refused, nothing left behind.
        File.Delete(path);
        (await Should.ThrowAsync<InvalidDataException>(() => LocalObjectStore.DownloadAsync(Data, http, null, new string('0', 64), exeSha, CancellationToken.None))).Message.ShouldContain("checksum");
        (await Should.ThrowAsync<InvalidDataException>(() => LocalObjectStore.DownloadAsync(Data, http, null, zipSha, new string('0', 64), CancellationToken.None))).Message.ShouldContain("checksum");
        File.Exists(path).ShouldBeFalse();
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldBeEmpty();
        LocalObjectStore.Exe(Data, _root).ShouldBeNull();
    }

    [Fact]
    public void It_is_where_files_are_kept_by_default_and_the_default_can_be_changed_and_given_back()
    {
        using var store = Store();
        store.Initialize();
        store.Default.Id.ShouldBe("pc");

        var server = LocalObjectStore.PickPorts(Path.Combine(_root, "store"));
        var local = store.AddLocal(server, LocalObjectStore.NewKeys(), FileKinds.All);
        store.AddLocal(server, LocalObjectStore.NewKeys(), FileKinds.All).Id.ShouldBe(local.Id, "only one");
        store.KeysOf(local.Id).ShouldNotBeNull();

        // The default: it keeps everything; This PC only what the recorder reads.
        store.SetDefault(local.Id, moveFromPrevious: true).ShouldBeNull();
        store.Default.Id.ShouldBe(local.Id);
        store.Locations.Single(l => l.Id == "pc").Keeps.ShouldBe([FileKinds.Pictures, FileKinds.Characters, FileKinds.Drivers]);
        store.Locations.Single(l => l.Id == local.Id).Keeps.ShouldBe(FileKinds.All);
        store.Locations.Count(l => l.Default).ShouldBe(1);

        // Another store reading the same files sees the same default.
        using (var other = Store())
        {
            other.Default.Id.ShouldBe(local.Id);
        }

        // Stopped and started (the process looking after it does it).
        store.SetLocalStopped(true).ShouldBeNull();
        store.Locations.Single(l => l.Id == local.Id).Local!.Stopped.ShouldBeTrue();
        store.SetLocalStopped(false).ShouldBeNull();

        // The default given back to This PC, the other keeping copies (not moved).
        store.SetDefault("pc", moveFromPrevious: false).ShouldBeNull();
        store.Locations.Single(l => l.Id == "pc").Keeps.ShouldBe(FileKinds.All);
        store.Locations.Single(l => l.Id == local.Id).Keeps.ShouldBe(FileKinds.All);

        // A set-up that did not finish: taken out, keys too.
        store.Forget(local.Id);
        store.Locations.ShouldHaveSingleItem().Id.ShouldBe("pc");
        store.KeysOf(local.Id).ShouldBeNull();
        store.SetLocalStopped(true).ShouldNotBeNull();
    }

    [Fact]
    public async Task Removing_the_default_gives_it_back_to_this_pc_and_a_removed_local_store_is_not_set_up_again_by_itself()
    {
        using var store = Store();
        store.Initialize();
        var folder = Path.Combine(_root, "drive");
        var (drive, problem) = await store.AddAsync(new StorageLocation("x", "Drive", LocationKinds.Folder, [FileKinds.Backups], Path: folder), null, CancellationToken.None);
        problem.ShouldBeNull();
        store.SetDefault(drive!.Id, moveFromPrevious: true).ShouldBeNull();
        store.Locations.Single(l => l.Id == "pc").Keeps.ShouldBe([FileKinds.Pictures, FileKinds.Characters, FileKinds.Drivers]);

        (await store.RemoveAsync(drive.Id, CancellationToken.None)).ShouldBeNull();
        store.Default.Id.ShouldBe("pc");
        store.Locations.Single().Keeps.ShouldBe(FileKinds.All);

        // A previous default may keep nothing until told to.
        var (second, _) = await store.AddAsync(new StorageLocation("y", "Second", LocationKinds.Folder, [FileKinds.Exports], Path: Path.Combine(_root, "second")), null, CancellationToken.None);
        store.SetDefault(second!.Id, moveFromPrevious: true).ShouldBeNull();
        store.SetDefault("pc", moveFromPrevious: true).ShouldBeNull();
        store.Locations.Single(l => l.Id == second.Id).Keeps.ShouldBeEmpty();
        store.Update("pc", null, [FileKinds.Pictures, FileKinds.Drivers], null).ShouldBeNull();
        store.Default.Keeps.ShouldBe(FileKinds.All, "the default keeps everything");

        // Set up on a first start only when shipped, never again once removed.
        var setup = new LocalStoreSetup(store, Data, () => new HttpClient(), NullLogger.Instance, baseDirectory: _root);
        (await setup.SetUpOnFirstStartAsync(CancellationToken.None)).ShouldBeFalse("not shipped");
        store.AddLocal(LocalObjectStore.PickPorts(_root), LocalObjectStore.NewKeys(), [FileKinds.Exports]);
        (await store.RemoveAsync("local", CancellationToken.None)).ShouldNotBeNull("not running: its files cannot be copied out");
        (await store.RemoveAsync("local", CancellationToken.None, force: true)).ShouldBeNull();
        File.Exists(Path.Combine(Data, "storage", "local-store.auto")).ShouldBeTrue();
    }

    /// <summary>
    /// The real thing: SeaweedFS set up, started, keeping files, started again when it stops, stopped when asked. Runs when
    /// PCDIAG_TEST_SEAWEEDFS names a folder with tools\seaweedfs\weed.exe (4.48) in it; passes trivially otherwise.
    /// </summary>
    [Fact]
    public async Task SeaweedFS_is_set_up_keeps_files_comes_back_when_it_stops_and_stops_when_asked()
    {
        if (Environment.GetEnvironmentVariable("PCDIAG_TEST_SEAWEEDFS") is not { Length: > 0 } shipped || LocalObjectStore.BundledExe(Path.Combine(shipped, "x")) is null)
        {
            return;
        }

        using var store = Store();
        store.Initialize();
        using var host = new LocalObjectStoreHost(store, Data, NullLogger.Instance, Path.Combine(shipped, "x")) { Interval = TimeSpan.FromMilliseconds(200) };
        using var stop = new CancellationTokenSource();
        var looking = host.RunAsync(stop.Token);
        try
        {
            var setup = new LocalStoreSetup(store, Data, () => new HttpClient(), NullLogger.Instance, Path.Combine(shipped, "x"));
            (await setup.SetUpOnFirstStartAsync(CancellationToken.None)).ShouldBeTrue(setup.State.Problem);
            setup.State.Problem.ShouldBeNull();
            store.Default.Local.ShouldNotBeNull();
            LocalObjectStoreHost.Read(Data)!.Running.ShouldBeTrue();
            (await setup.SetUpOnFirstStartAsync(CancellationToken.None)).ShouldBeFalse("once");

            // Files kept in it, read back from it.
            await store.PutAsync(FileKinds.Exports, "report.html", new MemoryStream("<p>kept</p>"u8.ToArray()), CancellationToken.None);
            File.Exists(Path.Combine(Data, "files", "exports", "report.html")).ShouldBeFalse("This PC no longer keeps exports");
            await using (var read = await store.OpenAsync(FileKinds.Exports, "report.html", CancellationToken.None))
            {
                new StreamReader(read!).ReadToEnd().ShouldBe("<p>kept</p>");
            }

            // It stops by itself: started again.
            var first = host.Process!.Id;
            host.Process.Kill();
            await Until(() => host.Process is { HasExited: false } p && p.Id != first && LocalObjectStoreHost.Read(Data)!.Running);
            await using (var again = await store.OpenAsync(FileKinds.Exports, "report.html", CancellationToken.None))
            {
                again.ShouldNotBeNull();
            }

            // Stopped when asked.
            store.SetLocalStopped(true);
            await Until(() => host.Process is null && LocalObjectStoreHost.Read(Data)!.Running == false);
        }
        finally
        {
            await stop.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(() => looking);
        }

        host.Process.ShouldBeNull();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        condition().ShouldBeTrue();
    }

    private sealed class Answer(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(answer());
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
