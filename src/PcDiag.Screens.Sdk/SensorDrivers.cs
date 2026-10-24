using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PcDiag.Screens;

/// <summary>
/// How a package reads its sensors: a known family (no code) or a type in the package's assembly, how often, and the
/// family's settings and sensors.
/// </summary>
/// <param name="Family">http-json, serial-lines or hid-report (no code); null with <paramref name="Type"/>.</param>
/// <param name="Type">An <see cref="ISensorDriver"/> type in the package's assembly.</param>
/// <param name="Every">Seconds between reads, 1–60.</param>
/// <param name="Url">http-json: the address to GET (this PC or the local network only).</param>
/// <param name="Port">serial-lines: the port, e.g. "COM3".</param>
/// <param name="Baud">serial-lines: its speed.</param>
/// <param name="Vid">hid-report: the USB vendor id in hexadecimal.</param>
/// <param name="Pid">hid-report: the USB product id in hexadecimal.</param>
/// <param name="Device">The device's name in the readings (the package's id when absent).</param>
/// <param name="Sensors">The sensors (1–64): which value each is, and its name, kind and unit.</param>
/// <param name="Settings">A code driver's own settings (passed to it as they are).</param>
public sealed record SensorSpec(
    string? Family = null,
    string? Type = null,
    int Every = 2,
    string? Url = null,
    string? Port = null,
    int Baud = 9600,
    string? Vid = null,
    string? Pid = null,
    string? Device = null,
    IReadOnlyList<SensorEntry>? Sensors = null,
    JsonElement? Settings = null);

/// <summary>
/// A sensor of a declarative family: where its value is, and how it reads.
/// </summary>
/// <param name="Id">Its id (letters, digits, dashes).</param>
/// <param name="Name">How people read it.</param>
/// <param name="Kind">temperature, clock, power, fan, voltage, load or other.</param>
/// <param name="Unit">Its unit.</param>
/// <param name="Path">http-json: where the value is in the answer, e.g. "sensors[0].temp" or "data.humidity".</param>
/// <param name="Key">serial-lines: the key before '=' or ':' on a line, e.g. "TEMP" in "TEMP=21.5".</param>
/// <param name="Offset">hid-report: the value's first byte in the report (the report id is byte 0).</param>
/// <param name="Type">hid-report: u8, i8, u16le, i16le, u16be, i16be, u32le, i32le or f32le.</param>
/// <param name="Scale">The value is multiplied by this…</param>
/// <param name="Add">…then this is added.</param>
/// <param name="ReportId">hid-report: only reports with this id carry it.</param>
public sealed record SensorEntry(string Id, string Name, string Kind = "other", string Unit = "", string? Path = null, string? Key = null, int Offset = 0, string Type = "u8", double Scale = 1, double Add = 0, int? ReportId = null);

/// <summary>Checks sensor settings and turns them into drivers.</summary>
public static class SensorDrivers
{
    /// <summary>The most values a driver gives (the rest are dropped).</summary>
    public const int MaxValues = 64;

    /// <summary>The families a package can name without code.</summary>
    public static readonly IReadOnlySet<string> Families = new HashSet<string>(StringComparer.Ordinal) { "http-json", "serial-lines", "hid-report" };

    /// <summary>The kinds a sensor can be.</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "temperature", "clock", "power", "fan", "voltage", "load", "other" };

    private static readonly HashSet<string> HidTypes = new(StringComparer.Ordinal) { "u8", "i8", "u16le", "i16le", "u16be", "i16be", "u32le", "i32le", "f32le" };

    /// <summary>The problem with sensor settings, or null when they are valid.</summary>
    public static string? Check(SensorSpec spec, bool hasAssembly)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if ((spec.Family is null) == (spec.Type is null))
        {
            return "they name either a family or a type.";
        }

        if (spec.Type is not null && !hasAssembly)
        {
            return "a type needs the package's assembly.";
        }

        if (spec.Family is not null && !Families.Contains(spec.Family))
        {
            return $"family is one of: {string.Join(", ", Families)}.";
        }

        if (spec.Every is < 1 or > 60)
        {
            return "every is 1-60 seconds.";
        }

        if (spec.Device is { } device && !IsName(device))
        {
            return "device is 1-40 letters, digits or dashes.";
        }

        switch (spec.Family)
        {
            case "http-json" when !Uri.TryCreate(spec.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"):
                return "url is an http or https address.";
            case "serial-lines" when spec.Port is null || !System.Text.RegularExpressions.Regex.IsMatch(spec.Port, "^COM[1-9][0-9]{0,2}$") || spec.Baud is < 300 or > 4_000_000:
                return "port is COM1-COM999 and baud 300-4000000.";
            case "hid-report" when !Hex(spec.Vid) || !Hex(spec.Pid):
                return "vid and pid are USB ids in hexadecimal.";
        }

        if (spec.Family is null)
        {
            return null;
        }

        if (spec.Sensors is not { Count: > 0 } || spec.Sensors.Count > MaxValues)
        {
            return $"sensors lists 1-{MaxValues}.";
        }

        if (spec.Sensors.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != spec.Sensors.Count)
        {
            return "each sensor has its own id.";
        }

        foreach (var s in spec.Sensors)
        {
            if (!IsName(s.Id) || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 80 || !Kinds.Contains(s.Kind) || s.Unit.Length > 12 || !double.IsFinite(s.Scale) || !double.IsFinite(s.Add))
            {
                return $"{s.Id}: an id (letters, digits, dashes), a name, a kind ({string.Join(", ", Kinds)}) and a unit.";
            }

            switch (spec.Family)
            {
                case "http-json" when string.IsNullOrWhiteSpace(s.Path) || s.Path.Length > 200:
                    return $"{s.Id}: path says where its value is.";
                case "serial-lines" when string.IsNullOrWhiteSpace(s.Key) || s.Key.Length > 40:
                    return $"{s.Id}: key names its value on a line.";
                case "hid-report" when s.Offset is < 0 or > 1023 || !HidTypes.Contains(s.Type) || s.ReportId is < 0 or > 255:
                    return $"{s.Id}: offset 0-1023 and type one of {string.Join(", ", HidTypes)}.";
            }
        }

        return null;
    }

    /// <summary>
    /// The driver of a package's sensors: a family's is this app's; one with code is loaded from the package's assembly
    /// into its own load context (with the libraries beside it), only ever in the host.
    /// </summary>
    public static ISensorDriver Create(ScreenDriverPackage package, ISensorIo io)
    {
        ArgumentNullException.ThrowIfNull(package);
        var spec = package.Sensors ?? throw new InvalidOperationException("The package reads no sensors.");
        switch (spec.Family)
        {
            case "http-json":
                return new HttpJsonDriver(package.Id, spec, io);
            case "serial-lines":
                return new SerialLinesDriver(package.Id, spec, io);
            case "hid-report":
                return new HidReportDriver(package.Id, spec, io);
        }

        var path = Path.GetFullPath(Path.Combine(package.Folder ?? throw new InvalidOperationException("A package with code has a folder."), package.Assembly!));
        var resolver = new System.Runtime.Loader.AssemblyDependencyResolver(path);
        var context = new System.Runtime.Loader.AssemblyLoadContext("sensor-driver:" + package.Id, isCollectible: true);
        context.Resolving += (c, name) =>
            name.Name == typeof(ISensorDriver).Assembly.GetName().Name ? typeof(ISensorDriver).Assembly
            : resolver.ResolveAssemblyToPath(name) is { } file ? c.LoadFromAssemblyPath(file) : null;
        context.ResolvingUnmanagedDll += (_, name) => resolver.ResolveUnmanagedDllToPath(name) is { } file ? System.Runtime.InteropServices.NativeLibrary.Load(file) : 0;
        var assembly = context.LoadFromAssemblyPath(path);
        var type = assembly.GetType(spec.Type!, throwOnError: true)!;
        if (!typeof(ISensorDriver).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"{spec.Type} is not an ISensorDriver.");
        }

        return (ISensorDriver)Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.Instance, null, [package.Id, spec, io], CultureInfo.InvariantCulture)!;
    }

    /// <summary>A number read from a value's text, or null.</summary>
    internal static double? Number(string? text) =>
        double.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

    /// <summary>The value at a path in a JSON document ("a.b[2].c"), as a number; null when it is not there or not a number.</summary>
    internal static double? AtPath(JsonElement root, string path)
    {
        var node = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;
            var indexes = new List<int>();
            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket >= 0)
            {
                name = part[..bracket];
                foreach (var index in part[bracket..].Split('[', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!int.TryParse(index.TrimEnd(']'), NumberStyles.None, CultureInfo.InvariantCulture, out var i))
                    {
                        return null;
                    }

                    indexes.Add(i);
                }
            }

            if (name.Length > 0 && (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node)))
            {
                return null;
            }

            foreach (var i in indexes)
            {
                if (node.ValueKind != JsonValueKind.Array || i >= node.GetArrayLength())
                {
                    return null;
                }

                node = node[i];
            }
        }

        return node.ValueKind switch
        {
            JsonValueKind.Number => node.GetDouble(),
            JsonValueKind.String => Number(node.GetString()),
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            _ => null,
        };
    }

    /// <summary>A value in a HID report at its offset, as its type; null when the report is too short.</summary>
    internal static double? FromReport(ReadOnlySpan<byte> report, int offset, string type)
    {
        var size = type switch { "u8" or "i8" => 1, "u16le" or "i16le" or "u16be" or "i16be" => 2, _ => 4 };
        if (offset < 0 || offset + size > report.Length)
        {
            return null;
        }

        var b = report.Slice(offset, size);
        return type switch
        {
            "u8" => b[0],
            "i8" => (sbyte)b[0],
            "u16le" => BinaryPrimitives.ReadUInt16LittleEndian(b),
            "i16le" => BinaryPrimitives.ReadInt16LittleEndian(b),
            "u16be" => BinaryPrimitives.ReadUInt16BigEndian(b),
            "i16be" => BinaryPrimitives.ReadInt16BigEndian(b),
            "u32le" => BinaryPrimitives.ReadUInt32LittleEndian(b),
            "i32le" => BinaryPrimitives.ReadInt32LittleEndian(b),
            _ => BinaryPrimitives.ReadSingleLittleEndian(b) is var f && float.IsFinite(f) ? f : null,
        };
    }

    /// <summary>The values a line of a serial device carries: "key=value" or "key: value", several split by ',', ';' or tabs.</summary>
    internal static IEnumerable<(string Key, double Value)> FromLine(string line)
    {
        foreach (var pair in line.Split([',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = pair.IndexOfAny(['=', ':']);
            if (at > 0 && Number(pair[(at + 1)..]) is { } value)
            {
                yield return (pair[..at].Trim(), value);
            }
        }
    }

    private static bool IsName(string text) => text.Length is > 0 and <= 40 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static bool Hex(string? text) => int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 0xFFFF;

    private static SensorValue Value(SensorSpec spec, string packageId, SensorEntry s, double? raw) =>
        new(spec.Device ?? packageId, s.Id, s.Name, s.Kind, s.Unit, raw is { } v ? (v * s.Scale) + s.Add : null);

    /// <summary>http-json: GETs the address and reads each sensor at its path.</summary>
    private sealed class HttpJsonDriver(string id, SensorSpec spec, ISensorIo io) : ISensorDriver
    {
        private readonly Uri _url = new(spec.Url!);

        public string Id => id;

        public IReadOnlyList<SensorValue> Read(CancellationToken cancellationToken)
        {
            var body = io.Fetch(_url, 64 * 1024, TimeSpan.FromSeconds(2), cancellationToken);
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
            return [.. spec.Sensors!.Select(s => Value(spec, id, s, AtPath(document.RootElement, s.Path!)))];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>serial-lines: keeps the last value of each key from the lines the port sends.</summary>
    private sealed class SerialLinesDriver(string id, SensorSpec spec, ISensorIo io) : ISensorDriver
    {
        private readonly Dictionary<string, double> _last = new(StringComparer.OrdinalIgnoreCase);
        private ISerialLines? _port;

        public string Id => id;

        public IReadOnlyList<SensorValue> Read(CancellationToken cancellationToken)
        {
            _port ??= io.OpenSerial(spec.Port!, spec.Baud);
            foreach (var line in _port.Take())
            {
                foreach (var (key, value) in FromLine(line))
                {
                    _last[key] = value;
                }
            }

            return [.. spec.Sensors!.Select(s => Value(spec, id, s, _last.TryGetValue(s.Key!, out var v) ? v : null))];
        }

        public void Dispose() => _port?.Dispose();
    }

    /// <summary>hid-report: reads input reports and each sensor from its bytes (the last report of each id kept).</summary>
    private sealed class HidReportDriver(string id, SensorSpec spec, ISensorIo io) : ISensorDriver
    {
        private readonly Dictionary<int, byte[]> _reports = [];
        private IHidReports? _device;

        public string Id => id;

        public IReadOnlyList<SensorValue> Read(CancellationToken cancellationToken)
        {
            _device ??= io.OpenHid(int.Parse(spec.Vid!, NumberStyles.HexNumber, CultureInfo.InvariantCulture), int.Parse(spec.Pid!, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                ?? throw new IOException("The device is not connected.");

            // What came since the last read (a few reports at most; the device keeps sending).
            for (var i = 0; i < 8 && !cancellationToken.IsCancellationRequested; i++)
            {
                if (_device.Read(TimeSpan.FromMilliseconds(i == 0 ? 500 : 20)) is not { Length: > 0 } report)
                {
                    break;
                }

                _reports[report[0]] = report;
            }

            return [.. spec.Sensors!.Select(s =>
            {
                var report = s.ReportId is { } rid ? _reports.GetValueOrDefault(rid) : _reports.Values.LastOrDefault();
                return Value(spec, id, s, report is null ? null : FromReport(report, s.Offset, s.Type));
            })];
        }

        public void Dispose() => _device?.Dispose();
    }
}
