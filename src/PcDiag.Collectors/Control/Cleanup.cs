using System.Diagnostics;
using PcDiag.Core.Control;
using PcDiag.Platform.Windows.Kernel;

namespace PcDiag.Collectors.Control;

/// <summary>A folder (or files in a folder) a clean-up target removes from.</summary>
/// <param name="Path">The folder (computed here, never taken from a request).</param>
/// <param name="Pattern">File name pattern.</param>
/// <param name="Recursive">Include sub-folders.</param>
internal sealed record CleanupRoot(string Path, string Pattern = "*", bool Recursive = true);

/// <summary>A clean-up target: what it is, where, and how old a file must be to go.</summary>
internal sealed record CleanupDefinition(string Id, string Name, string Description, bool Recommended, string? Caution, TimeSpan MinAge, Func<IEnumerable<CleanupRoot>> Roots);

/// <summary>
/// What the tune-up cleans: caches and leftovers that Windows and programs rebuild by themselves. The folders are
/// fixed here (a request only names a target), junctions and symbolic links are never followed, files still in use
/// are skipped, and folders themselves are kept.
/// </summary>
internal static class Cleanup
{
    /// <summary>Most files looked at per target (a runaway folder cannot stall the scan).</summary>
    private const int MaxFiles = 400_000;

    private static readonly TimeSpan MaxScanTime = TimeSpan.FromSeconds(20);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string LocalLow => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    /// <summary>The recycle bin is emptied through Windows, not by deleting files.</summary>
    public const string RecycleBin = "recycle-bin";

    public static readonly IReadOnlyList<CleanupDefinition> Targets =
    [
        // Older than a week, like Windows' Disk Cleanup: a program may still be using a temporary file for days.
        new("user-temp", "Your temporary files", "Files programs left in your Temp folder, older than a week.", true, null, TimeSpan.FromDays(7),
            () => [new(System.IO.Path.GetTempPath())]),
        new("windows-temp", "Windows temporary files", "Files left in the Windows Temp folder, older than a week.", true, null, TimeSpan.FromDays(7),
            () => [new(System.IO.Path.Combine(Windows, "Temp"))]),
        new("update-cache", "Windows Update downloads", "Update files Windows already downloaded (and usually installed).", true,
            "If an update is still waiting to install, Windows downloads it again.", TimeSpan.FromDays(2),
            () => [new(System.IO.Path.Combine(Windows, "SoftwareDistribution", "Download"))]),
        new("delivery-optimization", "Delivery Optimization cache", "Update pieces Windows keeps to share with other PCs.", true, null, TimeSpan.Zero,
            () => [new(System.IO.Path.Combine(Windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"))]),
        new("thumbnails", "Thumbnail cache", "Picture and video previews File Explorer shows; it makes them again.", true,
            "Files File Explorer is using are left.", TimeSpan.Zero,
            () => [new(System.IO.Path.Combine(Local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false)]),
        new("shader-cache", "Graphics shader caches", "Compiled shaders of DirectX, NVIDIA, AMD and Intel drivers.", false,
            "Games compile their shaders again, so their first starts and scenes may stutter. Useful after a driver update or when a game shows glitches.",
            TimeSpan.Zero,
            () =>
            [
                new(System.IO.Path.Combine(Local, "D3DSCache")),
                new(System.IO.Path.Combine(Local, "NVIDIA", "DXCache")),
                new(System.IO.Path.Combine(Local, "NVIDIA", "GLCache")),
                new(System.IO.Path.Combine(LocalLow, "NVIDIA", "PerDriverVersion", "DXCache")),
                new(System.IO.Path.Combine(ProgramData, "NVIDIA Corporation", "NV_Cache")),
                new(System.IO.Path.Combine(Local, "AMD", "DxCache")),
                new(System.IO.Path.Combine(Local, "AMD", "DxcCache")),
                new(System.IO.Path.Combine(Local, "AMD", "GLCache")),
                new(System.IO.Path.Combine(Local, "AMD", "VkCache")),
                new(System.IO.Path.Combine(LocalLow, "Intel", "ShaderCache")),
            ]),
        new("browser-cache", "Browser caches", "Cached web pages and images of Edge, Chrome, Brave, Vivaldi, Opera and Firefox. Passwords, history, cookies and sign-ins stay.", false,
            "Close the browsers first (what they are using is left). Sites load a little slower the first time afterwards.",
            TimeSpan.Zero, BrowserCaches),
        new("crash-dumps", "Crash dumps and error reports", "Memory dumps and reports Windows saved when programs or Windows crashed.", false,
            "They explain crashes and blue screens: keep them while you are investigating one (an incident lists them).", TimeSpan.Zero,
            () =>
            [
                new(System.IO.Path.Combine(Local, "CrashDumps")),
                new(System.IO.Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportArchive")),
                new(System.IO.Path.Combine(Local, "Microsoft", "Windows", "WER", "ReportQueue")),
                new(System.IO.Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive")),
                new(System.IO.Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue")),
                new(System.IO.Path.Combine(Windows, "Minidump")),
                new(System.IO.Path.Combine(Windows, "LiveKernelReports"), "*.dmp"),
                new(Windows, "MEMORY.DMP", Recursive: false),
            ]),
    ];

    private static IEnumerable<CleanupRoot> BrowserCaches()
    {
        string[] chromium =
        [
            System.IO.Path.Combine(Local, "Google", "Chrome", "User Data"),
            System.IO.Path.Combine(Local, "Microsoft", "Edge", "User Data"),
            System.IO.Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"),
            System.IO.Path.Combine(Local, "Vivaldi", "User Data"),
            System.IO.Path.Combine(Local, "Chromium", "User Data"),
        ];
        foreach (var userData in chromium.Where(Directory.Exists))
        {
            foreach (var profile in SafeDirectories(userData))
            {
                yield return new(System.IO.Path.Combine(profile, "Cache"));
                yield return new(System.IO.Path.Combine(profile, "Code Cache"));
                yield return new(System.IO.Path.Combine(profile, "GPUCache"));
            }
        }

        yield return new(System.IO.Path.Combine(Local, "Opera Software", "Opera Stable", "Cache"));
        var firefox = System.IO.Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            foreach (var profile in SafeDirectories(firefox))
            {
                yield return new(System.IO.Path.Combine(profile, "cache2"));
            }
        }
    }

    private static List<string> SafeDirectories(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                .Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Most entries listed per folder (the largest).</summary>
    private const int MaxEntries = 300;

    /// <summary>Time spent measuring one folder's entries.</summary>
    private static readonly TimeSpan BrowseTime = TimeSpan.FromSeconds(4);

    /// <summary>
    /// What a target's folder holds, largest first: <paramref name="root"/> -1 lists the target's folders themselves;
    /// otherwise the entries of <paramref name="path"/> inside folder <paramref name="root"/>. The path must stay inside
    /// that folder (no "..", no rooted path, no junction or symbolic link on the way); anything else lists nothing.
    /// </summary>
    public static CleanupListing Browse(string id, int root, string path, DateTimeOffset now)
    {
        if (id == RecycleBin)
        {
            return new CleanupListing(id, null, [], 0, "The Recycle Bin is browsed in File Explorer (its files keep their original names there).");
        }

        var target = Targets.FirstOrDefault(t => t.Id == id);
        if (target is null)
        {
            return new CleanupListing(id, null, [], 0, "There is no such clean-up target.");
        }

        var roots = target.Roots().Where(r => IsSafeRoot(r)).ToList();
        var clock = Stopwatch.StartNew();
        if (root < 0)
        {
            var tops = roots.Select((r, i) => (r, i)).Where(x => Directory.Exists(x.r.Path) || File.Exists(System.IO.Path.Combine(x.r.Path, x.r.Pattern)))
                .Select(x => FolderEntry(target, x.r, x.i, x.r.Path, "", x.r.Path, now, clock)).ToList();
            return new CleanupListing(id, null, [.. tops.OrderByDescending(e => e.Bytes)], 0, tops.Count == 0 ? "None of its folders exist on this PC." : null);
        }

        if (root >= roots.Count || Inside(roots[root], path) is not { } folder || !Directory.Exists(folder))
        {
            return new CleanupListing(id, null, [], 0, "That folder is not part of this clean-up target.");
        }

        var cleanupRoot = roots[root];
        var entries = new List<CleanupEntry>();
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System, IgnoreInaccessible = true };
        var info = new DirectoryInfo(folder);
        try
        {
            if (cleanupRoot.Recursive)
            {
                foreach (var sub in info.EnumerateDirectories("*", options))
                {
                    entries.Add(FolderEntry(target, cleanupRoot, root, sub.FullName, Relative(cleanupRoot, sub.FullName), sub.Name, now, clock));
                }
            }

            foreach (var file in info.EnumerateFiles(path.Length == 0 || cleanupRoot.Recursive ? cleanupRoot.Pattern : "*", options))
            {
                var removable = Removable(target, file, now);
                entries.Add(new CleanupEntry(root, Relative(cleanupRoot, file.FullName), file.Name, false, file.Length, 1, removable ? file.Length : 0, removable ? 1 : 0,
                    new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // What was read so far is listed.
        }

        var ordered = entries.OrderByDescending(e => e.Folder).ThenByDescending(e => e.Bytes).ToList();
        return new CleanupListing(id, folder, [.. ordered.Take(MaxEntries)], Math.Max(0, ordered.Count - MaxEntries), null);
    }

    /// <summary>The full path of <paramref name="path"/> inside a root, or null when it would leave it or pass a link.</summary>
    internal static string? Inside(CleanupRoot root, string path)
    {
        if (path.Length > 1024 || path.Any(char.IsControl) || System.IO.Path.IsPathRooted(path) || path.Split('\\', '/').Any(p => p is ".." or "."))
        {
            return null;
        }

        var baseFull = System.IO.Path.GetFullPath(root.Path).TrimEnd('\\');
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseFull, path)).TrimEnd('\\');
        if (!full.Equals(baseFull, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(baseFull + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // No junction or symbolic link anywhere between the root and the folder.
        for (var at = full; at.Length > baseFull.Length; at = System.IO.Path.GetDirectoryName(at) ?? baseFull)
        {
            try
            {
                if ((File.GetAttributes(at) & FileAttributes.ReparsePoint) != 0)
                {
                    return null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return full;
    }

    private static string Relative(CleanupRoot root, string full) =>
        System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(root.Path), full) is var relative && relative == "." ? "" : relative;

    private static bool Removable(CleanupDefinition target, FileInfo file, DateTimeOffset now) =>
        (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) == 0 && (target.MinAge <= TimeSpan.Zero || now.UtcDateTime - file.LastWriteTimeUtc >= target.MinAge);

    /// <summary>A folder's figures (everything in it, and what the clean-up would remove), measured within the time left.</summary>
    private static CleanupEntry FolderEntry(CleanupDefinition target, CleanupRoot root, int index, string folder, string relative, string name, DateTimeOffset now, Stopwatch clock)
    {
        long bytes = 0, removableBytes = 0;
        int files = 0, removableFiles = 0;
        var partial = false;
        DateTimeOffset? modified = null;
        try
        {
            var info = new DirectoryInfo(folder);
            modified = info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
            var all = info.EnumerateFiles(relative.Length == 0 ? root.Pattern : "*", new EnumerationOptions
            {
                RecurseSubdirectories = root.Recursive,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                IgnoreInaccessible = true,
            });
            foreach (var file in all)
            {
                if (clock.Elapsed > BrowseTime || files >= MaxFiles)
                {
                    partial = true;
                    break;
                }

                files++;
                bytes += file.Length;
                if (Removable(target, file, now))
                {
                    removableFiles++;
                    removableBytes += file.Length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            partial = true;
        }

        return new CleanupEntry(index, relative, name, true, bytes, files, removableBytes, removableFiles, modified, partial);
    }

    /// <summary>Measures every target (what could be removed now).</summary>
    public static IReadOnlyList<CleanupTarget> Scan(DateTimeOffset now)
    {
        var results = Targets.AsParallel().AsOrdered().Select(t =>
        {
            var (bytes, files) = Measure(t, now);
            return new CleanupTarget(t.Id, t.Name, t.Description, bytes, files, t.Recommended, t.Caution);
        }).ToList();
        var (binBytes, binItems) = ShellCleanup.RecycleBinSize();
        results.Add(new CleanupTarget(RecycleBin, "Recycle Bin", "Files you deleted, on every drive.", binBytes, (int)Math.Min(int.MaxValue, binItems), false,
            "They cannot be restored afterwards."));
        return results;
    }

    /// <summary>Removes what a target holds. Returns the space freed, files removed and files that were left (in use).</summary>
    public static (long Freed, int Removed, int Left) Clean(string id, DateTimeOffset now)
    {
        if (id == RecycleBin)
        {
            var (bytes, items) = ShellCleanup.RecycleBinSize();
            return ShellCleanup.EmptyRecycleBin() is null ? (bytes, (int)Math.Min(int.MaxValue, items), 0) : (0, 0, (int)Math.Min(int.MaxValue, items));
        }

        var target = Targets.FirstOrDefault(t => t.Id == id) ?? throw new ArgumentException("Unknown clean-up target.", nameof(id));
        return Clean(target, now);
    }

    /// <summary>Removes what a target holds (exposed for tests with their own folders).</summary>
    internal static (long Freed, int Removed, int Left) Clean(CleanupDefinition target, DateTimeOffset now)
    {
        long freed = 0;
        int removed = 0, left = 0;
        foreach (var root in target.Roots().Where(r => IsSafeRoot(r) && Directory.Exists(r.Path)))
        {
            foreach (var file in Files(root, target.MinAge, now, Stopwatch.StartNew()))
            {
                try
                {
                    var length = file.Length;
                    if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        file.Attributes &= ~FileAttributes.ReadOnly;
                    }

                    file.Delete();
                    freed += length;
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    left++; // in use, or protected
                }
            }

            if (root.Recursive)
            {
                RemoveEmptyFolders(root.Path);
            }
        }

        return (freed, removed, left);
    }

    internal static (long Bytes, int Files) Measure(CleanupDefinition target, DateTimeOffset now)
    {
        long bytes = 0;
        var files = 0;
        var clock = Stopwatch.StartNew();
        foreach (var root in target.Roots().Where(r => IsSafeRoot(r) && Directory.Exists(r.Path)))
        {
            foreach (var file in Files(root, target.MinAge, now, clock))
            {
                bytes += file.Length;
                files++;
            }
        }

        return (bytes, files);
    }

    private static IEnumerable<FileInfo> Files(CleanupRoot root, TimeSpan minAge, DateTimeOffset now, Stopwatch clock)
    {
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(root.Path).EnumerateFiles(root.Pattern, new EnumerationOptions
            {
                RecurseSubdirectories = root.Recursive,
                // Never into junctions or symbolic links (they could lead anywhere, even to the user's documents).
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                IgnoreInaccessible = true,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var count = 0;
        using var enumerator = files.GetEnumerator();
        while (count < MaxFiles && clock.Elapsed < MaxScanTime)
        {
            FileInfo file;
            try
            {
                if (!enumerator.MoveNext())
                {
                    yield break;
                }

                file = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            count++;
            if (minAge > TimeSpan.Zero && now.UtcDateTime - file.LastWriteTimeUtc < minAge)
            {
                continue;
            }

            yield return file;
        }
    }

    private static void RemoveEmptyFolders(string root)
    {
        try
        {
            var folders = new DirectoryInfo(root).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true })
                .OrderByDescending(d => d.FullName.Length).ToList();
            foreach (var folder in folders)
            {
                try
                {
                    if (!folder.EnumerateFileSystemInfos().Any())
                    {
                        folder.Delete();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use; kept.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to tidy.
        }
    }

    /// <summary>
    /// A root is never a drive, the Windows folder's system parts, or a user's profile itself; a single named file
    /// (MEMORY.DMP in the Windows folder) may be one level deep.
    /// </summary>
    internal static bool IsSafeRoot(CleanupRoot root)
    {
        var full = System.IO.Path.GetFullPath(root.Path).TrimEnd('\\');
        var depth = full.Count(c => c == '\\');
        if (!root.Recursive && !root.Pattern.Contains('*', StringComparison.Ordinal))
        {
            return depth >= 1;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
        return depth >= 2 && !string.Equals(full, profile, StringComparison.OrdinalIgnoreCase)
            && !full.EndsWith(@"\System32", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(@"\SysWOW64", StringComparison.OrdinalIgnoreCase);
    }
}
