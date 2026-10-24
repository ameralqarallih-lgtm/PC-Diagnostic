using System.Text.Json;

namespace PcDiag.Core.Devices;

/// <summary>A piece of hardware the PC has: a device, a drive, a monitor, a network adapter, a graphics card, its memory or processor.</summary>
/// <param name="Key">Its stable identity (the same device on any port keeps it).</param>
/// <param name="Kind">device, volume, monitor, network, gpu, memory or cpu.</param>
/// <param name="Name">Its name, e.g. "VG279QM", "D: Data", "Logitech G Pro".</param>
/// <param name="Detail">What it is or its size, e.g. "Monitor", "2 TB", "Headset".</param>
/// <param name="Amount">For memory (GB) and the processor (threads): how much, compared by amount (a failed module or disabled cores count as missing).</param>
public sealed record HardwareItem(string Key, string Kind, string Name, string? Detail = null, double? Amount = null);

/// <summary>The hardware the user expects the PC to have (expected-hardware.json, written by the Web host).</summary>
/// <param name="Version">File format version.</param>
/// <param name="Saved">When the setup was remembered.</param>
/// <param name="Items">What is expected.</param>
/// <param name="IncidentOnMissing">Start an incident when expected hardware goes missing.</param>
public sealed record ExpectedHardware(int Version, DateTimeOffset Saved, IReadOnlyList<HardwareItem> Items, bool IncidentOnMissing = false);

/// <summary>Expected hardware that is not there, and since when.</summary>
/// <param name="Item">What is missing.</param>
/// <param name="Since">When it was first found missing.</param>
/// <param name="Now">What is there instead, for amounts (e.g. 16 GB of the expected 32 GB).</param>
public sealed record MissingHardware(HardwareItem Item, DateTimeOffset Since, double? Now = null);

/// <summary>The newest check of the hardware against what is expected.</summary>
/// <param name="CheckedAt">When.</param>
/// <param name="Expected">The expected setup (null: none remembered yet).</param>
/// <param name="Current">The hardware there now.</param>
/// <param name="Missing">Expected and not there.</param>
/// <param name="Added">There now and not expected.</param>
public sealed record HardwareStatus(DateTimeOffset CheckedAt, ExpectedHardware? Expected, IReadOnlyList<HardwareItem> Current, IReadOnlyList<MissingHardware> Missing, IReadOnlyList<HardwareItem> Added);

/// <summary>Expected hardware missing, in every status (null when everything expected is there).</summary>
public sealed record HardwareNotice(int Missing, string Message);

/// <summary>Checks the hardware against what is expected; the recorder implements it.</summary>
public interface IHardwareWatch
{
    /// <summary>The newest check (null before the first).</summary>
    HardwareStatus? Status { get; }

    /// <summary>Checks now (after the expected setup changed, or when asked).</summary>
    Task<HardwareStatus> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>Compares, reads and writes expected hardware.</summary>
public static class HardwareCheck
{
    /// <summary>The kinds, in the order they are listed.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["device", "monitor", "volume", "network", "gpu", "memory", "cpu"];

    /// <summary>Most items remembered.</summary>
    public const int MaxItems = 500;

    /// <summary>An amount this much lower than expected counts as missing (rounding of memory sizes).</summary>
    public const double Tolerance = 0.97;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// What is missing and what was added. Items found missing before keep their first time; amounts (memory,
    /// threads) are missing when there is clearly less than expected.
    /// </summary>
    public static (IReadOnlyList<MissingHardware> Missing, IReadOnlyList<HardwareItem> Added) Compare(
        ExpectedHardware? expected, IReadOnlyList<HardwareItem> current, IReadOnlyList<MissingHardware> before, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(before);
        if (expected is null)
        {
            return ([], []);
        }

        var here = current.GroupBy(i => i.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var since = before.ToDictionary(m => m.Item.Key, m => m.Since, StringComparer.Ordinal);
        var missing = new List<MissingHardware>();
        foreach (var item in expected.Items)
        {
            here.TryGetValue(item.Key, out var now_);
            var lacking = now_ is null || (item.Amount is { } want && (now_.Amount ?? 0) < want * Tolerance);
            if (lacking)
            {
                missing.Add(new MissingHardware(item, since.GetValueOrDefault(item.Key, now), now_?.Amount));
            }
        }

        var known = expected.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var added = current.Where(i => !known.Contains(i.Key)).ToList();
        return (missing, added);
    }

    /// <summary>The problem with an expected setup, or null when it is fine.</summary>
    public static string? Validate(ExpectedHardware? expected)
    {
        if (expected is null)
        {
            return "an expected setup is required.";
        }

        if (expected.Items is null || expected.Items.Count > MaxItems)
        {
            return $"at most {MaxItems} items.";
        }

        foreach (var item in expected.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 400 || item.Key.Any(char.IsControl))
            {
                return "each item has a key of 1-400 printable characters.";
            }

            if (!Kinds.Contains(item.Kind))
            {
                return "kind is one of " + string.Join(", ", Kinds) + ".";
            }

            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 200 || (item.Detail?.Length ?? 0) > 200 || item.Name.Any(char.IsControl))
            {
                return "each item has a name (at most 200 characters).";
            }

            if (item.Amount is { } amount && (!double.IsFinite(amount) || amount < 0))
            {
                return "amounts are numbers of 0 or more.";
            }
        }

        return expected.Items.Select(i => i.Key).Distinct(StringComparer.Ordinal).Count() == expected.Items.Count ? null : "each item is listed once.";
    }

    /// <summary>Reads the expected setup; a missing or broken file is no setup.</summary>
    public static ExpectedHardware? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var expected = JsonSerializer.Deserialize<ExpectedHardware>(File.ReadAllText(path), Json);
            return Validate(expected) is null ? expected : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes the expected setup atomically.</summary>
    public static void Write(string path, ExpectedHardware expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(expected, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A short description of what is missing, e.g. "VG279QM (monitor), D: Data (drive)".</summary>
    public static string Describe(IReadOnlyList<MissingHardware> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        string KindName(string kind) => kind switch
        {
            "volume" => "drive",
            "network" => "network adapter",
            "gpu" => "graphics card",
            "cpu" => "processor",
            _ => kind,
        };
        var names = missing.Take(4).Select(m => m.Item.Amount is { } want && m.Now is { } have
            ? $"{m.Item.Name} ({have:0.#} of {want:0.#}{(m.Item.Kind == "memory" ? " GB" : " threads")})"
            : $"{m.Item.Name} ({KindName(m.Item.Kind)})");
        return string.Join(", ", names) + (missing.Count > 4 ? $" and {missing.Count - 4} more" : string.Empty);
    }
}
