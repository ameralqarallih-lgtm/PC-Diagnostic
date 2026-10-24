using System.Drawing;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Where a screen's readings are, by source, in its pixels (desktop pixels on a monitor, the picture's on a USB screen):
/// each reading's tile (a character goes to the one that turns bad) and its gauge as drawn (a wheel or needle character
/// plays on it). The first panel showing a reading wins.
/// </summary>
internal sealed class ReadingSpots
{
    public Dictionary<string, RectangleF> Tiles { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GaugeHit> Gauges { get; } = new(StringComparer.Ordinal);

    /// <summary>A panel's readings as drawn (<paramref name="hits"/>, in its picture) with its picture at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void Add(OverlayPanel panel, PanelHits hits, float x, float y)
    {
        for (var i = 0; i < Math.Min(panel.Readings.Count, hits.Readings.Count); i++)
        {
            var source = panel.Readings[i].Source;
            var r = hits.Readings[i];
            Tiles.TryAdd(source, new RectangleF(x + r.X, y + r.Y, r.Width, r.Height));
            if (hits.Gauges is { } gauges && i < gauges.Count && gauges[i] is { } gauge)
            {
                Gauges.TryAdd(source, gauge.Offset(x, y));
            }
        }
    }
}
