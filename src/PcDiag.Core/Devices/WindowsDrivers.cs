using System.Text.RegularExpressions;

namespace PcDiag.Core.Devices;

/// <summary>A connected device Windows has no working driver for (Device Manager's yellow mark).</summary>
/// <param name="InstanceId">Its device instance id.</param>
/// <param name="Name">Its name (friendly name, description or the bus's).</param>
/// <param name="Class">Its device class, when Windows knows it.</param>
/// <param name="ProblemCode">Windows' problem code: 28 (no driver), 1 (not configured) or 18 (reinstall).</param>
/// <param name="Problem">What the code means.</param>
/// <param name="HardwareIds">Its hardware ids, most specific first.</param>
/// <param name="CompatibleIds">Its compatible ids.</param>
public sealed record DeviceWithoutDriver(string InstanceId, string Name, string? Class, int ProblemCode, string Problem, IReadOnlyList<string> HardwareIds, IReadOnlyList<string> CompatibleIds);

/// <summary>A Windows driver package (an INF with its files) imported to be installed for such a device.</summary>
/// <param name="Id">Its id here (from its INF's name and content).</param>
/// <param name="Inf">The INF's file name.</param>
/// <param name="Provider">Who made it ([Version] Provider).</param>
/// <param name="Version">Its version (DriverVer).</param>
/// <param name="Date">Its date (DriverVer).</param>
/// <param name="Class">The device class it installs.</param>
/// <param name="Signature">Its catalog's signature: Signed, NotSigned, Untrusted or Unknown.</param>
/// <param name="Signer">Who signed the catalog.</param>
/// <param name="HardwareIds">The ids it installs for on this PC's architecture.</param>
/// <param name="Matches">The devices without a driver it fits (instance ids).</param>
/// <param name="Installed">The name Windows gave it in the driver store (oemNN.inf) once installed here.</param>
/// <param name="Problem">Why it cannot be installed (no device it fits, not signed for this PC…).</param>
public sealed record WindowsDriverInfo(string Id, string Inf, string? Provider, string? Version, string? Date, string? Class, string Signature, string? Signer, IReadOnlyList<string> HardwareIds, IReadOnlyList<string> Matches, string? Installed, string? Problem);

/// <summary>The devices without a driver and the driver packages imported for them.</summary>
public sealed record WindowsDriversState(IReadOnlyList<DeviceWithoutDriver> Devices, IReadOnlyList<WindowsDriverInfo> Packages, string? Message = null);

/// <summary>
/// Windows drivers for devices that have none: listed, imported (checked), installed after a confirmation on the PC
/// itself (optionally after a restore point), never forced, and removed again. Implemented by the recorder.
/// </summary>
public interface IWindowsDrivers
{
    /// <summary>The devices without a driver and the imported packages.</summary>
    WindowsDriversState State();

    /// <summary>Imports a driver zip uploaded to the drivers' uploads folder: the problem, or null.</summary>
    string? Import(string upload);

    /// <summary>
    /// Installs a package for a device after the person at the PC confirms it (on its own screen), with a restore
    /// point first when asked: what happened.
    /// </summary>
    Task<string> InstallAsync(string id, string instanceId, bool restorePoint, CancellationToken cancellationToken);

    /// <summary>Removes a package: uninstalled from Windows if it was installed here (after a confirmation on the PC), then deleted.</summary>
    Task<string?> RemoveAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// An INF file's facts that matter for installing it: [Version]'s provider, version, date, class and catalog, and the
/// hardware ids its models sections name for an architecture ([Manufacturer] entries decorated NTamd64, NTarm64…).
/// Strings (%Name%) come from [Strings]; comments (;), quotes and line continuations (\) are understood.
/// </summary>
public sealed partial class InfFile
{
    private readonly Dictionary<string, List<(string? Key, string Value)>> _sections;
    private readonly Dictionary<string, string> _strings;

    private InfFile(Dictionary<string, List<(string? Key, string Value)>> sections)
    {
        _sections = sections;
        _strings = (sections.GetValueOrDefault("strings") ?? []).Where(l => l.Key is not null).GroupBy(l => l.Key!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Unquote(g.First().Value), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Reads an INF's text.</summary>
    public static InfFile Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sections = new Dictionary<string, List<(string?, string)>>(StringComparer.OrdinalIgnoreCase);
        List<(string?, string)>? current = null;
        var pending = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = pending + StripComment(raw.TrimEnd('\r'));
            pending = string.Empty;
            if (line.EndsWith('\\'))
            {
                pending = line[..^1];
                continue;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.IndexOf(']', StringComparison.Ordinal) is > 0 and var end)
            {
                var name = line[1..end].Trim();
                if (!sections.TryGetValue(name, out current))
                {
                    current = [];
                    sections[name] = current;
                }

                continue;
            }

            if (current is null)
            {
                continue;
            }

            var equals = IndexOutsideQuotes(line, '=');
            current.Add(equals < 0 ? (null, line) : (line[..equals].Trim(), line[(equals + 1)..].Trim()));
        }

        return new InfFile(sections);
    }

    /// <summary>[Version] Provider.</summary>
    public string? Provider => Version("Provider");

    /// <summary>[Version] Class.</summary>
    public string? Class => Version("Class");

    /// <summary>The driver's version (DriverVer's second part).</summary>
    public string? DriverVersion => Version("DriverVer")?.Split(',') is [_, var v, ..] ? v.Trim() : null;

    /// <summary>The driver's date (DriverVer's first part).</summary>
    public string? DriverDate => Version("DriverVer")?.Split(',')[0].Trim();

    /// <summary>The catalog for an architecture (CatalogFile.NTamd64), or the undecorated one.</summary>
    public string? CatalogFor(string architecture) => Version("CatalogFile." + architecture) ?? Version("CatalogFile");

    /// <summary>
    /// The hardware (or compatible) ids its models install for on an architecture ("NTamd64"): the models sections its
    /// [Manufacturer] entries decorate with it (NTamd64, NTamd64.10.0…). Undecorated sections are for 32-bit x86 only,
    /// as Windows reads them on 64-bit PCs.
    /// </summary>
    public IReadOnlyList<string> HardwareIdsFor(string architecture)
    {
        var ids = new List<string>();
        foreach (var (_, value) in _sections.GetValueOrDefault("manufacturer") ?? [])
        {
            var parts = SplitList(value);
            if (parts.Count < 2)
            {
                continue;
            }

            foreach (var decoration in parts.Skip(1).Where(d => d.Equals(architecture, StringComparison.OrdinalIgnoreCase) || d.StartsWith(architecture + ".", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var (key, model) in _sections.GetValueOrDefault(parts[0] + "." + decoration) ?? [])
                {
                    if (key is null)
                    {
                        continue;
                    }

                    // %Desc% = InstallSection, HardwareId[, CompatibleId…]
                    ids.AddRange(SplitList(model).Skip(1).Where(id => id.Length > 0));
                }
            }
        }

        return [.. ids.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The ids a device has (its hardware ids, then its compatible ones) that the INF installs for.</summary>
    public static IReadOnlyList<string> Matching(IReadOnlyList<string> infIds, DeviceWithoutDriver device)
    {
        ArgumentNullException.ThrowIfNull(infIds);
        ArgumentNullException.ThrowIfNull(device);
        var set = new HashSet<string>(infIds, StringComparer.OrdinalIgnoreCase);
        return [.. device.HardwareIds.Concat(device.CompatibleIds).Where(set.Contains)];
    }

    private string? Version(string key)
    {
        var line = (_sections.GetValueOrDefault("version") ?? []).FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
        return line.Value is { } value ? Unquote(Expand(value)) : null;
    }

    private string Expand(string value) => StringToken().Replace(value, m => _strings.TryGetValue(m.Groups[1].Value, out var s) ? s : m.Value);

    private List<string> SplitList(string value)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] == '"')
            {
                quoted = !quoted;
            }
            else if (i == value.Length || (value[i] == ',' && !quoted))
            {
                parts.Add(Unquote(Expand(value[start..i].Trim())));
                start = i + 1;
            }
        }

        return parts;
    }

    private static string Unquote(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : value;

    private static string StripComment(string line)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ';' && !quoted)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static int IndexOutsideQuotes(string line, char c)
    {
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == c && !quoted)
            {
                return i;
            }
        }

        return -1;
    }

    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex StringToken();
}
