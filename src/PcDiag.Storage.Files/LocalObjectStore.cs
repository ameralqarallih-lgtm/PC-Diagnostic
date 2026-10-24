using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace PcDiag.Storage.Files;

/// <summary>
/// "PC Diagnostic Storage": SeaweedFS run on this PC as an S3-compatible object store, the storage location files are
/// kept in by default (as Video Clipper's built-in storage, set up the same way). One process ("weed mini": master,
/// volume, filer and S3 in one) bound to this PC only (127.0.0.1, so no firewall rule and nothing reachable from the
/// network), its data in a folder of the user's, one bucket, and keys made up for it and kept protected like any
/// service's keys. The installer ships the program (tools\seaweedfs\weed.exe); without it, it is downloaded once from
/// SeaweedFS's releases and both the archive and the program are checked against pinned SHA-256 hashes.
/// <see cref="LocalObjectStoreHost"/> keeps it running; <see cref="LocalStoreSetup"/> sets it up.
/// </summary>
public static class LocalObjectStore
{
    /// <summary>The SeaweedFS release used (Apache-2.0).</summary>
    public const string Version = "4.48";

    /// <summary>Its name as a storage location.</summary>
    public const string Name = "PC Diagnostic Storage";

    /// <summary>The one bucket it keeps files in.</summary>
    public const string Bucket = "pcdiagnostic";

    /// <summary>The release archive (Windows, 64-bit), and about its size.</summary>
    public static readonly Uri DownloadUrl = new($"https://github.com/seaweedfs/seaweedfs/releases/download/{Version}/windows_amd64.zip");

    /// <summary>About the download's size in bytes (for the page).</summary>
    public const long DownloadBytes = 47_000_874;

    /// <summary>SHA-256 of the release archive and of weed.exe in it (checked here on 9 Oct 2026, with its MD5 from the release).</summary>
    internal const string ZipSha256 = "fe90c04c0620ad1a1c756f86cd5e1443773f56a446688077a4bb5ec04c3cc874";

    internal const string ExeSha256 = "394a0154424f3d96f7969c044b77ee4cfb649299f8b42ecc7b703971872ce61d";

    /// <summary>The first port block tried: S3, master, volume, filer and admin (Video Clipper's start at 18300).</summary>
    internal const int FirstPort = 21300;

    /// <summary>The program shipped with PC Diagnostic (tools\seaweedfs beside the program's folder, or in it).</summary>
    public static string? BundledExe(string? baseDirectory = null)
    {
        var from = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var folder in new[] { Path.Combine(from, ".."), from })
        {
            var exe = Path.GetFullPath(Path.Combine(folder, "tools", "seaweedfs", "weed.exe"));
            if (File.Exists(exe))
            {
                return exe;
            }
        }

        return null;
    }

    /// <summary>Where a downloaded program is kept (in the data folder, by version).</summary>
    public static string DownloadedExe(string root) => Path.Combine(root, "tools", "seaweedfs", Version, "weed.exe");

    /// <summary>The program to run: the shipped one, else one downloaded before; null when neither.</summary>
    public static string? Exe(string root, string? baseDirectory = null) => BundledExe(baseDirectory) ?? (File.Exists(DownloadedExe(root)) ? DownloadedExe(root) : null);

    /// <summary>Its folder unless another is chosen.</summary>
    public static string DefaultFolder(string root) => Path.Combine(root, "object-store");

    /// <summary>
    /// Downloads the program once (an archive of about 47 MB), checking the archive's and the program's SHA-256; the
    /// program's path. <paramref name="progress"/> goes from 0 to 1.
    /// </summary>
    public static Task<string> DownloadAsync(string root, HttpClient http, IProgress<double>? progress, CancellationToken cancellationToken) =>
        DownloadAsync(root, http, progress, ZipSha256, ExeSha256, cancellationToken);

    internal static async Task<string> DownloadAsync(string root, HttpClient http, IProgress<double>? progress, string zipSha256, string exeSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var target = DownloadedExe(root);
        if (File.Exists(target) && await Sha256Async(target, cancellationToken).ConfigureAwait(false) == exeSha256)
        {
            return target;
        }

        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var part = Path.Combine(folder, "download.zip.part");
        try
        {
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? DownloadBytes;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long got = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    got += read;
                    progress?.Report(Math.Min(1, (double)got / Math.Max(1, total)));
                }

                if (Convert.ToHexStringLower(hash.GetHashAndReset()) != zipSha256)
                {
                    throw new InvalidDataException("The SeaweedFS download did not match its checksum: try again.");
                }
            }

            // weed.exe from the archive, checked too, then put in place whole.
            var temp = Path.Combine(folder, "weed.tmp");
            using (var zip = ZipFile.OpenRead(part))
            {
                var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "weed.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("The SeaweedFS download has no weed.exe in it.");
                await using var from = entry.Open();
                await using var to = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                await from.CopyToAsync(to, cancellationToken).ConfigureAwait(false);
            }

            if (await Sha256Async(temp, cancellationToken).ConfigureAwait(false) != exeSha256)
            {
                File.Delete(temp);
                throw new InvalidDataException("SeaweedFS's program did not match its checksum: try again.");
            }

            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(part);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Ports for it: the first block of five (S3, master, volume, filer, admin) free on this PC with their gRPC ports
    /// (10000 above each), from <see cref="FirstPort"/> in steps of 100.
    /// </summary>
    public static LocalServer PickPorts(string folder)
    {
        for (var first = FirstPort; first < FirstPort + 1600; first += 100)
        {
            var ports = Enumerable.Range(first, 5).ToArray();
            if (ports.All(p => Free(p) && Free(p + 10000)))
            {
                return new LocalServer(folder, ports[0], ports[1], ports[2], ports[3], ports[4]);
            }
        }

        throw new InvalidOperationException("No free ports for PC Diagnostic Storage on this PC (21300-22899 are all in use).");
    }

    /// <summary>Whether a port is free on this PC's loopback.</summary>
    internal static bool Free(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Keys made up for it: an access key of 20 characters, a secret of 40.</summary>
    public static StorageKeys NewKeys() =>
        new("PD" + Convert.ToHexString(RandomNumberGenerator.GetBytes(9)), Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_'));

    /// <summary>The storage location that is it.</summary>
    public static StorageLocation LocationFor(string id, LocalServer server, IReadOnlyList<string> keeps, bool isDefault) =>
        new(id, Name, LocationKinds.S3, keeps, Endpoint: $"http://127.0.0.1:{server.S3Port}", Bucket: Bucket, Region: "us-east-1", PathStyle: true, Default: isDefault, Local: server);

    private static readonly string[] Actions = ["Admin", "Read", "Write", "List", "Tagging"];

    /// <summary>Its S3 identities: one, with these keys, allowed everything.</summary>
    internal static string ConfigJson(StorageKeys keys) => JsonSerializer.Serialize(new
    {
        identities = new[]
        {
            new
            {
                name = "pcdiagnostic",
                credentials = new[] { new { accessKey = keys.AccessKey, secretKey = keys.SecretKey } },
                actions = Actions,
            },
        },
    });

    /// <summary>The command line: "weed mini" on 127.0.0.1 only, its ports, its data folder, its identities and its bucket.</summary>
    internal static IReadOnlyList<string> Arguments(LocalServer server, string configFile) =>
    [
        "mini",
        $"-dir={Path.Combine(server.Folder, "data")}",
        "-ip=127.0.0.1",
        "-ip.bind=127.0.0.1",
        $"-s3.port={server.S3Port}",
        $"-master.port={server.MasterPort}",
        $"-volume.port={server.VolumePort}",
        $"-filer.port={server.FilerPort}",
        $"-admin.port={server.AdminPort}",
        "-admin.ui=false",
        "-webdav=false",
        "-s3.port.iceberg=0",
        "-s3.port.lance=0",
        $"-s3.config={configFile}",
        $"-bucket={Bucket}",
    ];

    /// <summary>Whether a folder can hold it: a full path, writable; null when it can, otherwise why not.</summary>
    public static string? CheckFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return "Give the folder's full path, e.g. D:\\PC Diagnostic Storage.";
        }

        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".pcdiag-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "PC Diagnostic");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "The folder cannot be written: " + ex.Message;
        }
    }
}
