using PcDiag.Core.Overlay;
using PcDiag.Core.Snapshots;
using PcDiag.Engine.Display;

namespace PcDiag.Engine.Overlay;

/// <summary>The monitors for the on-screen display: Windows' desktop areas named and keyed by the display configuration.</summary>
public static class OverlayDisplays
{
    /// <summary>
    /// Each monitor with its stable key (the display configuration's target key, so a layout follows the monitor) and
    /// its number, left to right then top to bottom. A monitor the configuration does not list keeps its device name.
    /// </summary>
    public static List<OverlayDisplay> Build(IReadOnlyList<MonitorArea> areas, DisplaySnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(areas);
        var displays = new List<OverlayDisplay>();
        var number = 0;
        foreach (var area in areas.OrderBy(a => a.X).ThenBy(a => a.Y))
        {
            number++;
            var targets = snapshot?.Displays.Where(d => string.Equals(d.GdiDeviceName, area.DeviceName, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
            if (targets.Count == 0)
            {
                displays.Add(new OverlayDisplay("screen:" + area.DeviceName, $"Display {number}", number, area.X, area.Y, area.Width, area.Height, area.Scale, area.Primary, null));
                continue;
            }

            // A duplicated desktop shows the same area on several monitors; each keeps its own layout.
            foreach (var target in targets)
            {
                // Its turn as set in Windows (its shape, portrait or landscape, comes from its area: a monitor can be upright untouched).
                displays.Add(new OverlayDisplay(target.TargetKey, DisplayDiff.Name(target), number, area.X, area.Y, area.Width, area.Height, area.Scale, area.Primary,
                    target.RefreshRateHz > 0 ? Math.Round(target.RefreshRateHz, 2) : null, Rotation: target.OrientationDegrees is 90 or 180 or 270 ? target.OrientationDegrees : 0));
            }
        }

        return displays;
    }
}
