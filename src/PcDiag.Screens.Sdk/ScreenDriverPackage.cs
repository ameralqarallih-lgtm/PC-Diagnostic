using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcDiag.Screens;

/// <summary>
/// A driver package: a folder with driver.json, for screens (the screens it drives and either a known protocol family,
/// no code: <see cref="Family"/>, or a .NET assembly with an <see cref="IScreenDriver"/>: <see cref="Assembly"/> and
/// <see cref="Type"/>), for sensors (<see cref="Sensors"/>: a known family, no code, or an <see cref="ISensorDriver"/>
/// in the assembly), or both. Packages other than the built-in ones are used only once approved, by their
/// <see cref="Sha256"/> (every file in the folder), so a changed package waits for approval again.
/// </summary>
/// <param name="Id">Its id: lower-case letters, digits and dashes.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Publisher">Who made it.</param>
/// <param name="Family">A protocol family this app knows (e.g. "turzx-winusb"), for a package without code.</param>
/// <param name="Assembly">The assembly (in the package folder) of a package with code.</param>
/// <param name="Type">The <see cref="IScreenDriver"/> type in it.</param>
/// <param name="Models">The screens it drives.</param>
/// <param name="Holders">Other programs that hold its screens (paused while one is used).</param>
/// <param name="Sensors">The sensors it reads, and how.</param>
public sealed record ScreenDriverPackage(
    string Id,
    string Name,
    string Version,
    string Publisher,
    string? Family,
    string? Assembly,
    string? Type,
    IReadOnlyList<ScreenDriverPackage.ModelEntry> Models,
    IReadOnlyList<ScreenHolder>? Holders = null,
    SensorSpec? Sensors = null)
{
    /// <summary>The most files a package may have.</summary>
    public const int MaxFiles = 256;

    /// <summary>The most bytes a package may have, all files together.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>What it is for: "screen" (it drives screens) and "sensors" (it reads sensors).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Capabilities => [.. (Models is { Count: > 0 } ? new[] { "screen" } : []), .. (Sensors is not null ? new[] { "sensors" } : [])];

    /// <summary>Where the package was read from (null: built in).</summary>
    [JsonIgnore]
    public string? Folder { get; init; }

    /// <summary>The SHA-256 of driver.json and the assembly (what an approval names).</summary>
    [JsonIgnore]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Whether it ships with the app (used without approval).</summary>
    [JsonIgnore]
    public bool BuiltIn => Folder is null;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>A screen in a package (ids in hexadecimal, as Windows shows them: "1CBE").</summary>
    public sealed record ModelEntry(string Id, string Name, string Vid, string Pid, int Width, int Height, FrameFormat Format, int MaxBytes = 0, int MaxFps = 30, int Rotation = 0, bool Tested = false)
    {
        /// <summary>As a model.</summary>
        public ScreenModel ToModel() => new(
            Id,
            Name,
            int.Parse(Vid, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            int.Parse(Pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Width,
            Height,
            Format,
            MaxBytes,
            MaxFps,
            Rotation,
            Tested);
    }

    /// <summary>The packages that ship with the app.</summary>
    public static IReadOnlyList<ScreenDriverPackage> BuiltInPackages()
    {
        var assembly = typeof(ScreenDriverPackage).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("PcDiag.Screens.Sdk.BuiltIn.", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();
                return Parse(text, out _) ?? throw new InvalidOperationException($"Built-in screen driver {n} is invalid.");
            })];
    }

    /// <summary>A package from its folder (driver.json); null with the problem when it is not a valid package.</summary>
    public static ScreenDriverPackage? Read(string folder, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var manifest = Path.Combine(folder, "driver.json");
        if (!File.Exists(manifest))
        {
            problem = "driver.json is missing.";
            return null;
        }

        try
        {
            var package = Parse(File.ReadAllText(manifest, System.Text.Encoding.UTF8), out problem);
            if (package is null)
            {
                return null;
            }

            if (package.Assembly is { } file)
            {
                var path = Path.GetFullPath(Path.Combine(folder, file));
                if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    problem = "assembly must be a file in the package's folder.";
                    return null;
                }
            }

            if (HashFolder(folder, out problem) is not { } sha256)
            {
                return null;
            }

            return package with { Folder = folder, Sha256 = sha256 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The SHA-256 of a package's folder: every file in it (its path relative to the folder, lower case with '/', then
    /// its bytes), in path order; null with the problem when it has too many files or bytes, or a link.
    /// </summary>
    public static string? HashFolder(string folder, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var root = Path.GetFullPath(folder);
        var files = new List<(string Relative, string Path)>();
        long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                problem = "a package may not hold links.";
                return null;
            }

            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                continue;
            }

            bytes += info.Length;
            files.Add((Path.GetRelativePath(root, path).Replace('\\', '/').ToLowerInvariant(), path));
            if (files.Count > MaxFiles || bytes > MaxBytes)
            {
                problem = $"a package has at most {MaxFiles} files and {MaxBytes / (1024 * 1024)} MB.";
                return null;
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, path) in files.OrderBy(f => f.Relative, StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(relative + "\n"));
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        problem = null;
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>A package from driver.json's text, checked; null with the problem when it is not valid.</summary>
    public static ScreenDriverPackage? Parse(string json, out string? problem)
    {
        ScreenDriverPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<ScreenDriverPackage>(json, Json);
        }
        catch (JsonException ex)
        {
            problem = "driver.json is not valid: " + ex.Message;
            return null;
        }

        package = package is null ? null : package with { Models = package.Models ?? [] };
        problem = package is null ? "driver.json is empty." : Check(package);
        return problem is null ? package : null;
    }

    private static string? Check(ScreenDriverPackage p)
    {
        if (string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 64 || !p.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return "id is 1-64 lower-case letters, digits or dashes.";
        }

        if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Version) || string.IsNullOrWhiteSpace(p.Publisher))
        {
            return "name, version and publisher are required.";
        }

        if (p.Models.Count == 0 && p.Sensors is null && p.Family is null && p.Type is null)
        {
            return "a package drives screens (models), reads sensors (sensors), or both.";
        }

        if (p.Sensors is { } sensors && SensorDrivers.Check(sensors, p.Assembly is not null) is { } sensorProblem)
        {
            return "sensors: " + sensorProblem;
        }

        // Screens: when it names any (or a screen family or type), they are checked as a screen package's.
        if (p.Models.Count == 0 && p.Family is null && p.Type is null)
        {
            return null;
        }

        if ((p.Family is null) == (p.Assembly is null || p.Type is null) || (p.Family is not null && p.Type is not null))
        {
            return "a package's screens use either a protocol family, or an assembly and its type.";
        }

        if (p.Family is not null && !ScreenDrivers.Families.Contains(p.Family))
        {
            return $"family is one of: {string.Join(", ", ScreenDrivers.Families)}.";
        }

        if (p.Models.Count is 0 or > 64)
        {
            return "models lists 1-64 screens.";
        }

        foreach (var m in p.Models)
        {
            if (string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Name)
                || !int.TryParse(m.Vid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var vid) || vid is < 0 or > 0xFFFF
                || !int.TryParse(m.Pid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pid) || pid is < 0 or > 0xFFFF)
            {
                return "each model has an id, a name and USB vid/pid in hexadecimal.";
            }

            if (m.Width is < 16 or > 8192 || m.Height is < 16 or > 8192 || m.MaxFps is < 1 or > 120 || m.MaxBytes < 0 || m.Rotation is not (0 or 90 or 180 or 270))
            {
                return $"{m.Id}: width and height 16-8192, maxFps 1-120, maxBytes 0 or more, rotation 0/90/180/270.";
            }
        }

        if (p.Holders?.Any(h => string.IsNullOrWhiteSpace(h.Service) || string.IsNullOrWhiteSpace(h.Name)) == true)
        {
            return "each holder names a service and the program.";
        }

        return null;
    }
}

/// <summary>Turns packages into drivers.</summary>
public static class ScreenDrivers
{
    /// <summary>The protocol families a package can name without code.</summary>
    public static readonly IReadOnlySet<string> Families = new HashSet<string>(StringComparer.Ordinal) { Protocols.TurzxWinUsb.Family };

    /// <summary>
    /// The driver of a package. A family's driver is this app's; a package with code is loaded from its assembly into its
    /// own load context (only ever in the screen host, never the recorder). <paramref name="clearPng"/> and
    /// <paramref name="blackJpeg"/> encode the blank pictures a session starts with (the host has the codecs).
    /// </summary>
    public static IScreenDriver Create(ScreenDriverPackage package, Func<int, int, byte[]> clearPng, Func<int, int, byte[]> blackJpeg)
    {
        ArgumentNullException.ThrowIfNull(package);
        var models = package.Models.Select(m => m.ToModel()).ToList();
        if (package.Family == Protocols.TurzxWinUsb.Family)
        {
            return new Protocols.TurzxWinUsb.Driver(package.Id, models, clearPng, blackJpeg);
        }

        var path = Path.GetFullPath(Path.Combine(package.Folder ?? throw new InvalidOperationException("A package with code has a folder."), package.Assembly!));
        var context = new System.Runtime.Loader.AssemblyLoadContext("screen-driver:" + package.Id, isCollectible: true);

        // The contract is shared: the package sees this assembly's IScreenDriver, not a copy of its own.
        context.Resolving += (c, name) => name.Name == typeof(IScreenDriver).Assembly.GetName().Name ? typeof(IScreenDriver).Assembly : null;
        var assembly = context.LoadFromAssemblyPath(path);
        var type = assembly.GetType(package.Type!, throwOnError: true)!;
        if (!typeof(IScreenDriver).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"{package.Type} is not an IScreenDriver.");
        }

        return (IScreenDriver)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null, [package.Id, models], CultureInfo.InvariantCulture)!;
    }
}
