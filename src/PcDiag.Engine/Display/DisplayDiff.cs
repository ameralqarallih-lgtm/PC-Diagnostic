using System.Globalization;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;

namespace PcDiag.Engine.Display;

/// <summary>
/// Compares two display configurations and describes what changed: resolution, refresh rate, HDR, orientation,
/// and topology (a display becoming active or inactive, the primary display, positions, the driving adapter).
/// Physical connect/disconnect comes from monitor notifications instead, so displays named in
/// <c>notifiedTargets</c> are not reported again as topology changes. Nothing is compared across a switch between
/// the console and a Remote Desktop session: the two see different displays, which is not a hardware change.
/// </summary>
public static class DisplayDiff
{
    /// <summary>Refresh rates closer than this are treated as equal (59.94 vs 60 Hz is a real change).</summary>
    public const double RefreshToleranceHz = 0.05;

    /// <summary>The changes from <paramref name="before"/> to <paramref name="after"/>.</summary>
    public static IReadOnlyList<DisplayChange> Compare(DisplaySnapshot? before, DisplaySnapshot after, DateTimeOffset at, IReadOnlySet<string>? notifiedTargets = null)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null || before.IsRemoteSession != after.IsRemoteSession)
        {
            return [];
        }

        var changes = new List<DisplayChange>();
        var notified = notifiedTargets is null ? null : new HashSet<string>(notifiedTargets, StringComparer.OrdinalIgnoreCase);
        var old = Index(before);
        var now = Index(after);

        foreach (var (key, d) in old.Where(o => !now.ContainsKey(o.Key)))
        {
            if (notified?.Contains(key) != true)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.TopologyChanged, Name(d), "active, " + Mode(d), "not active", key));
            }
        }

        foreach (var (key, d) in now.Where(n => !old.ContainsKey(n.Key)))
        {
            if (notified?.Contains(key) != true)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.TopologyChanged, Name(d), "not active", "active, " + Mode(d), key));
            }
        }

        foreach (var (key, b) in old)
        {
            if (!now.TryGetValue(key, out var a))
            {
                continue;
            }

            var name = Name(a);
            if (a.WidthPixels != b.WidthPixels || a.HeightPixels != b.HeightPixels)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.ResolutionChanged, name, Resolution(b), Resolution(a), key));
            }

            if (Math.Abs(a.RefreshRateHz - b.RefreshRateHz) >= RefreshToleranceHz)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.RefreshRateChanged, name, Hz(b.RefreshRateHz), Hz(a.RefreshRateHz), key));
            }

            if (a.HdrEnabled.HasValue && b.HdrEnabled.HasValue && a.HdrEnabled.Value != b.HdrEnabled.Value)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.HdrChanged, name, Color(b), Color(a), key));
            }
            else if (a.ColorMode is not null && b.ColorMode is not null && a.ColorMode != b.ColorMode)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.HdrChanged, name, Color(b), Color(a), key));
            }

            if (a.OrientationDegrees != b.OrientationDegrees)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.OrientationChanged, name, Degrees(b.OrientationDegrees), Degrees(a.OrientationDegrees), key));
            }

            if (a.IsPrimary != b.IsPrimary || a.PositionX != b.PositionX || a.PositionY != b.PositionY || a.AdapterKey != b.AdapterKey)
            {
                changes.Add(new DisplayChange(at, DisplayEventKind.TopologyChanged, name, Placement(b), Placement(a), key));
            }
        }

        return changes;
    }

    /// <summary>
    /// The monitor's PnP instance ID from its device interface path:
    /// <c>\\?\DISPLAY#AUS32F4#5&amp;12862c70&amp;0&amp;UID4353#{e6f07b5f-…}</c> → <c>DISPLAY\AUS32F4\5&amp;12862C70&amp;0&amp;UID4353</c>.
    /// Upper case, because notifications and configuration queries do not agree on case.
    /// </summary>
    public static string? TargetKeyFromDevicePath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        var path = devicePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? devicePath[4..] : devicePath;
        var guid = path.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0)
        {
            path = path[..guid];
        }

        return path.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>The monitor's PnP ID (EDID manufacturer and product, e.g. "AUS32F4") from a device path, when present.</summary>
    public static string? MonitorIdFromDevicePath(string? devicePath)
    {
        var parts = TargetKeyFromDevicePath(devicePath)?.Split('\\');
        return parts is { Length: >= 2 } && parts[0] == "DISPLAY" ? parts[1] : null;
    }

    /// <summary>A display's name for people: friendly name, else EDID ID, else the GDI name.</summary>
    public static string Name(DisplayTarget display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return !string.IsNullOrWhiteSpace(display.FriendlyName) ? display.FriendlyName
            : display.Manufacturer is not null ? display.Manufacturer + display.ProductCode
            : display.GdiDeviceName ?? display.TargetKey;
    }

    /// <summary>"2560x1440 @ 143.97 Hz".</summary>
    public static string Mode(DisplayTarget display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return Resolution(display) + " @ " + Hz(display.RefreshRateHz);
    }

    /// <summary>"60 Hz", "143.97 Hz".</summary>
    public static string Hz(double rate) => rate.ToString("0.##", CultureInfo.InvariantCulture) + " Hz";

    private static string Resolution(DisplayTarget d) => string.Create(CultureInfo.InvariantCulture, $"{d.WidthPixels}x{d.HeightPixels}");

    private static string Degrees(int degrees) => degrees.ToString(CultureInfo.InvariantCulture) + "°";

    private static string Color(DisplayTarget d) =>
        (d.HdrEnabled.HasValue ? (d.HdrEnabled.Value == true ? "HDR on" : "HDR off") : "HDR unknown") + (d.ColorMode is null ? string.Empty : $" ({d.ColorMode})");

    private static string Placement(DisplayTarget d) =>
        string.Create(CultureInfo.InvariantCulture, $"{(d.IsPrimary ? "primary" : "secondary")} at ({d.PositionX},{d.PositionY}) on {d.AdapterName}");

    private static Dictionary<string, DisplayTarget> Index(DisplaySnapshot snapshot) =>
        snapshot.Displays.GroupBy(d => d.TargetKey, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Helpers for <see cref="Reading{T}"/> values in display snapshots.</summary>
public static class DisplayReadings
{
    /// <summary>A value, or "unsupported" when null.</summary>
    public static Reading<bool> Optional(bool? value, Availability whenMissing = Availability.Unsupported) =>
        value is { } v ? Reading<bool>.Of(v) : Reading<bool>.Unavailable(whenMissing);
}
