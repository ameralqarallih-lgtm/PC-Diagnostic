using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Storage.Files;

namespace PcDiag.Storage.Tests;

public sealed class FileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pcdiag-tests", "store-" + Guid.NewGuid().ToString("N"));

    public FileStoreTests() => Directory.CreateDirectory(_root);

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

    private FileStore Store() => new(Path.Combine(_root, "data"), Path.Combine(_root, "data", "overlay-images"), NullLogger.Instance);

    private static MemoryStream Text(string text) => new(Encoding.UTF8.GetBytes(text));

    private static async Task<string> Read(Stream? stream)
    {
        stream.ShouldNotBeNull();
        await using (stream)
        {
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        }
    }

    [Fact]
    public async Task This_pc_keeps_every_kind_and_a_folder_added_gets_what_it_keeps()
    {
        using var store = Store();
        store.Locations.ShouldHaveSingleItem().Id.ShouldBe("pc");

        // Pictures go where the recorder reads them; other kinds in the data folder's files.
        await store.PutAsync(FileKinds.Pictures, "abc.png", Text("picture"), CancellationToken.None);
        File.Exists(Path.Combine(_root, "data", "overlay-images", "abc.png")).ShouldBeTrue();
        var kept = await store.PutAsync(FileKinds.Backups, "overlay-1.json", Text("{\"version\":1}"), CancellationToken.None);
        kept.Sha256!.Length.ShouldBe(64);
        File.Exists(Path.Combine(_root, "data", "files", "backups", "overlay-1.json")).ShouldBeTrue();

        // A folder that keeps backups: it gets the one already kept, and the next ones as they are made.
        var folder = Path.Combine(_root, "D-drive");
        var (location, problem) = await store.AddAsync(new StorageLocation("x", "Backup drive", LocationKinds.Folder, [FileKinds.Backups], Path: folder), null, CancellationToken.None);
        problem.ShouldBeNull();
        // The job runs from the moment it is asked for (one small file: it may be done already).
        (store.Job is { Running: true } or { Finished: not null }).ShouldBeTrue("the job runs from the moment it is asked for");
        for (var i = 0; i < 100 && store.Job.Running; i++)
        {
            await Task.Delay(50);
        }

        store.Job.ShouldBe(store.Job with { Running = false, Copied = 1, Problem = null });
        (await store.SyncAsync(CancellationToken.None)).ShouldBeNull();
        File.Exists(Path.Combine(folder, "PcDiagnostic", "backups", "overlay-1.json")).ShouldBeTrue();
        await store.PutAsync(FileKinds.Backups, "overlay-2.json", Text("{\"version\":1,\"enabled\":true}"), CancellationToken.None);
        File.Exists(Path.Combine(folder, "PcDiagnostic", "backups", "overlay-2.json")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "PcDiagnostic", "pictures", "abc.png")).ShouldBeFalse();

        // Read from the first that has it, even when This PC lost it.
        File.Delete(Path.Combine(_root, "data", "files", "backups", "overlay-2.json"));
        (await Read(await store.OpenAsync(FileKinds.Backups, "overlay-2.json", CancellationToken.None))).ShouldContain("enabled");
        (await store.ListAsync(FileKinds.Backups, CancellationToken.None)).Select(f => f.Name).Order().ShouldBe(["overlay-1.json", "overlay-2.json"]);
        store.Job.Running.ShouldBeFalse();

        // Saved and read again by the next run, This PC first.
        using var again = Store();
        again.Locations.Select(l => l.Id).ShouldBe(["pc", location!.Id]);
    }

    [Fact]
    public async Task Every_kind_is_kept_somewhere_and_what_the_recorder_reads_stays_on_this_pc()
    {
        using var store = Store();
        var folder = Path.Combine(_root, "share");
        var (location, _) = await store.AddAsync(new StorageLocation("x", "Share", LocationKinds.Folder, [FileKinds.Backups, FileKinds.Exports], Path: folder), null, CancellationToken.None);

        // The default keeps every kind, whatever it is asked to keep.
        store.Update("pc", null, [FileKinds.Exports], null).ShouldBeNull();
        store.Locations[0].Keeps.ShouldBe(FileKinds.All);

        // With the share the default, This PC can stop keeping backups but never what the recorder reads.
        store.SetDefault(location!.Id, moveFromPrevious: false).ShouldBeNull();
        store.Update("pc", null, [FileKinds.Exports], null).ShouldBeNull();
        store.Locations[0].Keeps.ShouldBe([FileKinds.Pictures, FileKinds.Characters, FileKinds.Drivers, FileKinds.Exports]);
        store.Update(location.Id, null, [FileKinds.Exports], null).ShouldBeNull();
        store.Locations[1].Keeps.ShouldBe(FileKinds.All);

        (await store.RemoveAsync("pc", CancellationToken.None))!.ShouldContain("cannot be removed");

        // This PC the default again: the share goes, its backups copied here first.
        await store.PutAsync(FileKinds.Backups, "overlay-9.json", Text("{}"), CancellationToken.None);
        File.Exists(Path.Combine(_root, "data", "files", "backups", "overlay-9.json")).ShouldBeFalse();
        store.SetDefault("pc", moveFromPrevious: false).ShouldBeNull();
        (await store.RemoveAsync(location.Id, CancellationToken.None)).ShouldBeNull();
        File.Exists(Path.Combine(_root, "data", "files", "backups", "overlay-9.json")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "PcDiagnostic", "backups", "overlay-9.json")).ShouldBeTrue("its own copies stay there");
        store.Locations.ShouldHaveSingleItem();

        // Bad locations are refused before anything is kept.
        (await store.TestAsync(new StorageLocation("t", "Relative", LocationKinds.Folder, [FileKinds.Backups], Path: "backups"), null, CancellationToken.None))!.ShouldContain("full path");
        (await store.TestAsync(new StorageLocation("t", "No keys", LocationKinds.S3, [FileKinds.Backups], Bucket: "b"), null, CancellationToken.None))!.ShouldContain("key");
        (await store.AddAsync(new StorageLocation("t", "Nothing", LocationKinds.Folder, [], Path: folder), null, CancellationToken.None)).Problem!.ShouldContain("Choose what it keeps");
        await Should.ThrowAsync<ArgumentException>(() => store.PutAsync(FileKinds.Backups, "..\\escape.json", Text("{}"), CancellationToken.None));
    }

    [Fact]
    public async Task An_s3_service_keeps_files_with_their_hash_and_its_keys_are_never_saved_in_plain_text()
    {
        using var s3 = new FakeS3();
        using var store = Store();
        var service = new StorageLocation("x", "MinIO", LocationKinds.S3, [FileKinds.Backups, FileKinds.Pictures], Endpoint: s3.Address, Bucket: "pcdiag", Region: "us-east-1", Prefix: "home/", PathStyle: true);
        var keys = new StorageKeys("AKIAEXAMPLE1234", "very-secret-key-value");
        (await store.TestAsync(service, keys, CancellationToken.None)).ShouldBeNull();
        s3.Objects.Keys.ShouldBeEmpty("the probe is deleted after the test");

        var (location, problem) = await store.AddAsync(service, keys, CancellationToken.None);
        problem.ShouldBeNull();
        var file = await store.PutAsync(FileKinds.Backups, "overlay-1.json", Text("{\"version\":1}"), CancellationToken.None);
        s3.Objects.ShouldContainKey("pcdiag/home/backups/overlay-1.json");
        s3.Metadata["pcdiag/home/backups/overlay-1.json"].ShouldBe(file.Sha256);

        // A picture already on This PC is copied there; reading works from the service when This PC lost it.
        var picture = Path.Combine(_root, "data", "overlay-images", "0011aabb.png");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        await File.WriteAllTextAsync(picture, "png bytes");
        await store.MirrorAsync(FileKinds.Pictures, "0011aabb.png", picture, CancellationToken.None);
        s3.Objects.ShouldContainKey("pcdiag/home/pictures/0011aabb.png");
        File.Delete(Path.Combine(_root, "data", "files", "backups", "overlay-1.json"));
        (await Read(await store.OpenAsync(FileKinds.Backups, "overlay-1.json", CancellationToken.None))).ShouldBe("{\"version\":1}");
        (await store.ListAsync(FileKinds.Backups, CancellationToken.None)).ShouldHaveSingleItem().Size.ShouldBe(13);

        // The browser sees the keys as set, with their last four characters; the file on disk does not hold them.
        var view = store.Views().Single(v => v.Location.Id == location!.Id);
        (view.HasKeys, view.AccessKeyEnd).ShouldBe((true, "1234"));
        var saved = await File.ReadAllBytesAsync(Path.Combine(_root, "data", "storage", "keys.dat"));
        Encoding.UTF8.GetString(saved).ShouldNotContain("very-secret-key-value");
        using var again = Store();
        (await again.OpenAsync(FileKinds.Backups, "overlay-1.json", CancellationToken.None)).ShouldNotBeNull();

        // A service that refuses: said plainly.
        s3.Refuse = true;
        (await store.TestAsync(service with { Id = "y" }, keys, CancellationToken.None))!.ShouldContain("not allowed");
    }

    /// <summary>
    /// A minimal S3-compatible service on this PC (path-style; PUT, GET, HEAD, DELETE and ListObjectsV2), enough for the
    /// SDK as the store uses it. Signatures are not checked.
    /// </summary>
    private sealed class FakeS3 : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public FakeS3()
        {
            var port = 40000 + Random.Shared.Next(20000);
            Address = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Address + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public string Address { get; }

        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

        public ConcurrentDictionary<string, string?> Metadata { get; } = new();

        public bool Refuse { get; set; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    await HandleAsync(context);
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (Refuse)
            {
                await Xml(response, 403, "<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>");
                return;
            }

            var path = Uri.UnescapeDataString(request.Url!.AbsolutePath.TrimStart('/'));
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            var bucket = slash < 0 ? path : path[..slash];
            var key = slash < 0 ? string.Empty : path[(slash + 1)..];
            var id = bucket + "/" + key;
            switch (request.HttpMethod)
            {
                case "PUT":
                    using (var body = new MemoryStream())
                    {
                        await request.InputStream.CopyToAsync(body);
                        Objects[id] = body.ToArray();
                    }

                    Metadata[id] = request.Headers["x-amz-meta-sha256"];
                    response.StatusCode = 200;
                    response.AddHeader("ETag", ETag(Objects[id]));
                    return;
                case "GET" when key.Length == 0:
                    var prefix = request.QueryString["prefix"] ?? string.Empty;
                    var contents = new StringBuilder();
                    var found = Objects.Where(o => o.Key.StartsWith(bucket + "/" + prefix, StringComparison.Ordinal)).ToList();
                    foreach (var o in found)
                    {
                        contents.Append(System.Globalization.CultureInfo.InvariantCulture, $"<Contents><Key>{o.Key[(bucket.Length + 1)..]}</Key><LastModified>2026-10-09T10:00:00.000Z</LastModified><ETag>\"etag\"</ETag><Size>{o.Value.Length}</Size><StorageClass>STANDARD</StorageClass></Contents>");
                    }

                    await Xml(response, 200, $"<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>{bucket}</Name><Prefix>{prefix}</Prefix><KeyCount>{found.Count}</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>{contents}</ListBucketResult>");
                    return;
                case "GET" or "HEAD":
                    if (!Objects.TryGetValue(id, out var bytes))
                    {
                        await Xml(response, 404, "<Error><Code>NoSuchKey</Code><Message>Not found</Message></Error>");
                        return;
                    }

                    response.StatusCode = 200;
                    response.AddHeader("ETag", ETag(bytes));
                    response.AddHeader("Last-Modified", "Fri, 09 Oct 2026 10:00:00 GMT");
                    response.ContentLength64 = bytes.Length;
                    if (request.HttpMethod == "GET")
                    {
                        await response.OutputStream.WriteAsync(bytes);
                    }

                    return;
                case "DELETE":
                    Objects.TryRemove(id, out _);
                    Metadata.TryRemove(id, out _);
                    response.StatusCode = 204;
                    return;
                default:
                    response.StatusCode = 405;
                    return;
            }
        }

        // Single-part objects' ETag is their MD5, which the SDK checks on reading.
#pragma warning disable CA5351 // As S3 does.
        private static string ETag(byte[] bytes) => "\"" + Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes)) + "\"";
#pragma warning restore CA5351

        private static async Task Xml(HttpListenerResponse response, int status, string xml)
        {
            response.StatusCode = status;
            response.ContentType = "application/xml";
            var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
        }
    }
}
