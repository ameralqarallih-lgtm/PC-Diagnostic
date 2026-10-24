using System.Text.RegularExpressions;

namespace PcDiag.Storage.Files;

/// <summary>The kinds of file kept in storage locations.</summary>
public static partial class FileKinds
{
    /// <summary>Pictures behind on-screen panels (named by their content's hash; the recorder reads them on this PC).</summary>
    public const string Pictures = "pictures";

    /// <summary>Copies of the on-screen layout from before it was saved (to restore an earlier one).</summary>
    public const string Backups = "backups";

    /// <summary>Driver packages imported for devices (staged here; the recorder installs them on this PC).</summary>
    public const string Drivers = "drivers";

    /// <summary>Incident reports and exports kept when made.</summary>
    public const string Exports = "exports";

    /// <summary>Characters: those that come with PC Diagnostic, and those imported or generated (the recorder reads them on this PC).</summary>
    public const string Characters = "characters";

    /// <summary>The character generator's drafts (the pictures it drew to choose from).</summary>
    public const string Drafts = "drafts";

    /// <summary>Settings and layouts: the recorder's settings, the on-screen layout, the dashboards, the rules and the expected hardware (copies).</summary>
    public const string Settings = "settings";

    /// <summary>Period diagnostics' reports.</summary>
    public const string Diagnostics = "diagnostics";

    /// <summary>Problem reports (sent or waiting to be).</summary>
    public const string Reports = "reports";

    /// <summary>Every kind, in the order shown.</summary>
    public static readonly IReadOnlyList<string> All = [Pictures, Characters, Drafts, Settings, Backups, Drivers, Diagnostics, Reports, Exports];

    /// <summary>Kinds the recorder reads on this PC: always kept on This PC (other locations hold copies).</summary>
    public static readonly IReadOnlySet<string> OnThisPc = new HashSet<string>(StringComparer.Ordinal) { Pictures, Characters, Drivers };

    /// <summary>A file's name in a kind: 1-128 letters, digits, dots, dashes and underscores (no folders).</summary>
    public static bool IsName(string? name) => name is not null && NameRule().IsMatch(name) && name != "." && name != "..";

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex NameRule();
}

/// <summary>Location kinds.</summary>
public static class LocationKinds
{
    /// <summary>This PC's data folder (always there, cannot be removed).</summary>
    public const string ThisPc = "pc";

    /// <summary>A folder (another drive, a network share).</summary>
    public const string Folder = "folder";

    /// <summary>An S3-compatible service (AWS S3, MinIO, Cloudflare R2, Wasabi, Backblaze B2, SeaweedFS…).</summary>
    public const string S3 = "s3";
}

/// <summary>A place files are kept, and which kinds it keeps.</summary>
/// <param name="Id">Stable id ("pc" for This PC).</param>
/// <param name="Name">Its name as shown.</param>
/// <param name="Kind">pc, folder or s3.</param>
/// <param name="Keeps">The kinds of file it keeps.</param>
/// <param name="Path">A folder's path.</param>
/// <param name="Endpoint">An S3 service's address (empty: AWS for the region).</param>
/// <param name="Bucket">An S3 bucket.</param>
/// <param name="Region">An S3 region (us-east-1 when not said).</param>
/// <param name="Prefix">Where in the bucket (or folder) its files go, e.g. "pcdiag/".</param>
/// <param name="PathStyle">Addresses the bucket in the path (MinIO and most self-hosted services) rather than the host name.</param>
/// <param name="Default">Where files are kept by default: it keeps every kind (one location is; This PC when no other).</param>
/// <param name="Local">The object storage run on this PC (<see cref="LocalObjectStore"/>), when this location is it.</param>
public sealed record StorageLocation(
    string Id,
    string Name,
    string Kind,
    IReadOnlyList<string> Keeps,
    string? Path = null,
    string? Endpoint = null,
    string? Bucket = null,
    string? Region = null,
    string? Prefix = null,
    bool PathStyle = true,
    bool Default = false,
    LocalServer? Local = null);

/// <summary>The object storage run on this PC (SeaweedFS): where its files are, its ports, and whether it was stopped.</summary>
/// <param name="Folder">Its folder (its data under "data", its log beside it).</param>
/// <param name="S3Port">Its S3 port (on 127.0.0.1 only), and the ports of its parts (each with a gRPC port 10000 above).</param>
/// <param name="Stopped">Stopped by the user (it is started again only when asked).</param>
public sealed record LocalServer(string Folder, int S3Port, int MasterPort, int VolumePort, int FilerPort, int AdminPort, bool Stopped = false);

/// <summary>The object storage on this PC as it is now (written by the process running it, read by the page).</summary>
/// <param name="Running">Answering on its port.</param>
/// <param name="Pid">Its process.</param>
/// <param name="Port">Its S3 port.</param>
/// <param name="Problem">Why it is not running (it did not start, it kept stopping, its program is missing).</param>
/// <param name="At">When this was written.</param>
/// <param name="OwnerPid">The process looking after it (the storage host, or the Web host when there is none).</param>
public sealed record LocalStoreState(bool Running, int? Pid, int? Port, string? Problem, DateTimeOffset At, int OwnerPid);

/// <summary>Setting the object storage on this PC up, and how far it is.</summary>
/// <param name="Running">Under way.</param>
/// <param name="Step">What it is doing ("Downloading SeaweedFS", "Starting it", "Checking it").</param>
/// <param name="Done">How far, 0-1.</param>
/// <param name="Problem">Why it stopped (nothing was kept).</param>
/// <param name="Finished">When it last finished.</param>
public sealed record LocalStoreSetupState(bool Running, string? Step, double Done, string? Problem, DateTimeOffset? Finished);

/// <summary>An S3 service's keys (kept protected on this PC; never sent back to the browser).</summary>
public sealed record StorageKeys(string AccessKey, string SecretKey);

/// <summary>A file kept: its kind and name, size, when it changed and (when known) its SHA-256.</summary>
public sealed record StoredFile(string Kind, string Name, long Size, DateTimeOffset Updated, string? Sha256 = null);

/// <summary>A storage location as the browser sees it: its keys only as set or not, and its last four characters.</summary>
public sealed record LocationView(StorageLocation Location, bool HasKeys, string? AccessKeyEnd, bool Online, string? Problem);

/// <summary>Bringing every location up to date with the kinds it keeps (after a change), and how far it is.</summary>
/// <param name="Running">Under way.</param>
/// <param name="Done">Files looked at.</param>
/// <param name="Total">Files to look at.</param>
/// <param name="Copied">Files copied to a location that lacked them.</param>
/// <param name="Problem">The last file that could not be copied, and why.</param>
/// <param name="Finished">When it last finished.</param>
public sealed record StorageJob(bool Running, int Done, int Total, int Copied, string? Problem, DateTimeOffset? Finished);
