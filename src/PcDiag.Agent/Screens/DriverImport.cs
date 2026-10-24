using System.IO.Compression;
using PcDiag.Screens;

namespace PcDiag.Agent.Screens;

/// <summary>
/// Brings a driver package (a zip) into the drivers folder. The uploaded file is never used where it is: it is copied
/// once into a staging folder only administrators can write, and that copy is what is checked and unpacked: at most
/// <see cref="ScreenDriverPackage.MaxFiles"/> files and <see cref="ScreenDriverPackage.MaxBytes"/> (counted as they are
/// unpacked, whatever the zip says), no file packed more than 200 times smaller, safe relative names only, driver.json
/// at its top (or in its one folder) and valid, and not the id of a built-in package. Only then does it replace the
/// package of that id; it waits for approval like any changed package.
/// </summary>
internal static class DriverImport
{
    /// <summary>The largest zip taken.</summary>
    public const long MaxZipBytes = 64L * 1024 * 1024;

    private const int MaxRatio = 200;
    private const int MaxNameLength = 200;
    private const int MaxDepth = 8;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Imports the zip at <paramref name="upload"/> (it must be in <paramref name="uploads"/>): the package's id, or the
    /// problem. <paramref name="staging"/> and <paramref name="drivers"/> are administrators' folders.
    /// </summary>
    public static (string? Id, string? Problem) Import(string upload, string uploads, string staging, string drivers, IReadOnlySet<string> builtInIds) =>
        WithUnpacked(upload, uploads, staging, DriverJsonTop, "driver.json is not at the top of the zip (or in its one folder).", unpacked =>
        {
            if (ScreenDriverPackage.Read(unpacked, out var problem) is not { } package)
            {
                return ((string?)null, "It is not a valid driver package: " + problem);
            }

            if (builtInIds.Contains(package.Id))
            {
                return (null, $"\"{package.Id}\" is a driver that comes with PC Diagnostic; a package cannot replace it.");
            }

            Directory.CreateDirectory(drivers);
            var target = Path.Combine(drivers, package.Id);
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            // Copied, not moved: new files take the drivers folder's rights (everyone reads, administrators write);
            // moved ones would keep the staging folder's (administrators only), and the screen host could not read them.
            CopyFolder(unpacked, target);
            return (package.Id, null);
        });

    /// <summary>
    /// Copies an upload (it must be in <paramref name="uploads"/>) into <paramref name="staging"/>, unpacks it checked
    /// (its top: the folder <paramref name="topOf"/> finds in its file names, else <paramref name="noTop"/>), and
    /// hands the unpacked folder to <paramref name="use"/>; the staging copy is deleted after.
    /// </summary>
    public static (T? Result, string? Problem) WithUnpacked<T>(string upload, string uploads, string staging, Func<IReadOnlyList<string>, string?> topOf, string noTop, Func<string, (T? Result, string? Problem)> use)
    {
        var file = Path.GetFullPath(upload);
        if (!file.StartsWith(Path.GetFullPath(uploads) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)
            || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
        {
            return (default, "The upload is not where uploads are kept.");
        }

        Directory.CreateDirectory(staging);
        var work = Path.Combine(staging, Guid.NewGuid().ToString("n"));
        var zip = work + ".zip";
        var unpacked = Path.Combine(work, "package");
        try
        {
            // One copy, read once: what is checked is what is used (the upload may change after).
            using (var from = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var to = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (from.Length > MaxZipBytes || CopyAtMost(from, to, MaxZipBytes) < 0)
                {
                    return (default, $"A driver package is at most {MaxZipBytes / (1024 * 1024)} MB.");
                }
            }

            return Unpack(zip, unpacked, topOf, noTop) is { } unpackProblem ? (default, unpackProblem) : use(unpacked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return (default, "The package could not be imported: " + ex.Message);
        }
        finally
        {
            TryDelete(zip);
            if (Directory.Exists(work))
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Left for the next import's tidy-up.
                }
            }
        }
    }

    /// <summary>A driver package's top: where driver.json is (the zip's top, or its one top folder).</summary>
    internal static string? DriverJsonTop(IReadOnlyList<string> names) =>
        names.Contains("driver.json", StringComparer.OrdinalIgnoreCase) ? string.Empty
        : names.Select(n => n.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is [var top] && names.Contains($"{top}/driver.json", StringComparer.OrdinalIgnoreCase) ? top + "/"
        : null;

    /// <summary>Copies a folder's files into another (made if needed); new files take the target's rights.</summary>
    public static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Whether a name in a zip is a safe relative path (and the path to unpack it to), or why not.</summary>
    internal static string? CheckName(string name)
    {
        var path = name.Replace('\\', '/');
        if (path.Length is 0 or > MaxNameLength || path.StartsWith('/') || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
        {
            return "It has a file with an unsafe name: " + name;
        }

        var parts = path.TrimEnd('/').Split('/');
        if (parts.Length > MaxDepth)
        {
            return "Its folders are too deep: " + name;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || Reserved.Contains(Path.GetFileNameWithoutExtension(part)))
            {
                return "It has a file with an unsafe name: " + name;
            }
        }

        return null;
    }

    /// <summary>Unpacks the zip's package (its top, or its one top folder when driver.json is in it), checked as it goes.</summary>
    private static string? Unpack(string zip, string to, Func<IReadOnlyList<string>, string?> topOf, string noTop)
    {
        using var archive = ZipFile.OpenRead(zip);
        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        if (files.Count == 0)
        {
            return "The zip is empty.";
        }

        if (files.Count > ScreenDriverPackage.MaxFiles)
        {
            return $"A driver package has at most {ScreenDriverPackage.MaxFiles} files.";
        }

        foreach (var entry in archive.Entries)
        {
            if (CheckName(entry.FullName) is { } problem)
            {
                return problem;
            }

            if (entry.CompressedLength > 0 && entry.Length / entry.CompressedLength > MaxRatio)
            {
                return "It has a file packed too tightly to be a driver (a zip bomb?): " + entry.FullName;
            }
        }

        if (files.Sum(e => e.Length) > ScreenDriverPackage.MaxBytes)
        {
            return $"A driver package is at most {ScreenDriverPackage.MaxBytes / (1024 * 1024)} MB unpacked.";
        }

        var names = files.Select(e => e.FullName.Replace('\\', '/')).ToList();
        if (topOf(names) is not { } prefix)
        {
            return noTop;
        }

        Directory.CreateDirectory(to);
        var root = Path.GetFullPath(to) + Path.DirectorySeparatorChar;
        long unpackedBytes = 0;
        foreach (var entry in files)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return "Files outside the package's folder: " + entry.FullName;
            }

            var target = Path.GetFullPath(Path.Combine(to, name[prefix.Length..]));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return "It has a file with an unsafe name: " + entry.FullName;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var from = entry.Open();
            using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            // Counted as unpacked: a zip that says less than it holds stops at what it said (and the whole at the limit).
            var allowed = Math.Min(entry.Length, ScreenDriverPackage.MaxBytes - unpackedBytes);
            var written = CopyAtMost(from, file, allowed);
            if (written < 0)
            {
                return "It holds more than it says (a zip bomb?): " + entry.FullName;
            }

            unpackedBytes += written;
        }

        return null;
    }

    /// <summary>Copies at most <paramref name="max"/> bytes; -1 when there was more.</summary>
    private static long CopyAtMost(Stream from, Stream to, long max)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > max)
            {
                return -1;
            }

            to.Write(buffer, 0, read);
        }

        return total;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next import's tidy-up.
        }
    }
}
