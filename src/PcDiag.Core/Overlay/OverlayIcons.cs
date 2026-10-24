namespace PcDiag.Core.Overlay;

/// <summary>
/// The icons a reading can carry, as SVG path data on a 24 × 24 grid drawn with a 2-unit round stroke. The recorder
/// draws them from this data and the editor's preview from the same data (web/src/features/overlay/icons.ts, which a
/// test keeps equal to this list).
/// </summary>
public static class OverlayIcons
{
    /// <summary>Icon id → path data.</summary>
    public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["cpu"] = "M8 6h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2Z M10 10h4v4h-4Z M9 2v4 M15 2v4 M9 18v4 M15 18v4 M2 9h4 M2 15h4 M18 9h4 M18 15h4",
        ["gpu"] = "M2 5v15 M2 7h18a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H2 M6 12.5a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 M13 12.5a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0 M7 18v2h8v-2",
        ["memory"] = "M3 7h18v9H3Z M6.5 10h3v3h-3Z M14.5 10h3v3h-3Z M6 16v3 M10 16v3 M14 16v3 M18 16v3",
        ["temp"] = "M14 4v10.54a4 4 0 1 1-4 0V4a2 2 0 0 1 4 0Z M12 9v7",
        ["fan"] = "M10.83 16.38a6.08 6.08 0 0 1-8.62-7l5.41 1.45a6.08 6.08 0 0 1 7-8.62l-1.45 5.41a6.08 6.08 0 0 1 8.62 7l-5.41-1.45a6.08 6.08 0 0 1-7 8.62l1.45-5.41Z M12 12v.01",
        ["power"] = "M13 2 4 14h7l-1 8 9-12h-7l1-8Z",
        ["clock"] = "M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20Z M12 6v6l4 2",
        ["calendar"] = "M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z M16 2v4 M8 2v4 M3 10h18",
        ["disk"] = "M22 12H2 M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11Z M6 16h.01 M10 16h.01",
        ["down"] = "M12 3v14 M5 11l7 7 7-7 M5 21h14",
        ["up"] = "M12 21V7 M5 13l7-7 7 7 M5 3h14",
        ["gauge"] = "M12 14l4-4 M3.34 19a10 10 0 1 1 17.32 0",
        ["activity"] = "M22 12h-4l-3 9L9 3l-3 9H2",
        ["record"] = "M12 7a5 5 0 1 0 0 10a5 5 0 1 0 0-10Z",
        ["frame"] = "M3 4h18a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1Z M8 21h8 M12 17v4",
    };

    /// <summary>The icon for what a reading measures (by its source and unit).</summary>
    public static string Auto(string sourceId, string? unit)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        if (sourceId.StartsWith("time.date", StringComparison.Ordinal))
        {
            return "calendar";
        }

        if (sourceId.StartsWith("time.", StringComparison.Ordinal))
        {
            return "clock";
        }

        if (sourceId.StartsWith("recorder.", StringComparison.Ordinal))
        {
            return "record";
        }

        if (sourceId.StartsWith("net.", StringComparison.Ordinal))
        {
            return sourceId.EndsWith(".up", StringComparison.Ordinal) ? "up" : "down";
        }

        return unit switch
        {
            "°C" => "temp",
            "RPM" => "fan",
            "W" or "V" or "A" => "power",
            "fps" => "frame",
            _ when sourceId.StartsWith("gpu.", StringComparison.Ordinal) && sourceId.Contains(".vram", StringComparison.Ordinal) => "memory",
            _ when sourceId.StartsWith("gpu.", StringComparison.Ordinal) => "gpu",
            _ when sourceId.StartsWith("cpu.", StringComparison.Ordinal) => "cpu",
            _ when sourceId.StartsWith("memory.", StringComparison.Ordinal) => "memory",
            _ when sourceId.StartsWith("disk.", StringComparison.Ordinal) => "disk",
            "MHz" or "GHz" or "%" => "gauge",
            _ => "activity",
        };
    }
}
