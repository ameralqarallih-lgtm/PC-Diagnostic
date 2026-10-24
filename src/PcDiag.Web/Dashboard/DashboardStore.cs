using System.Text.Json;
using System.Text.RegularExpressions;
using PcDiag.Core.Configuration;

namespace PcDiag.Web.Dashboard;

/// <summary>
/// The user's dashboard (widgets, their settings and the layout per screen size, and the library of widgets the
/// user built), stored as dashboard.json in the data folder so it follows the user across browsers. The content is
/// validated strictly before it is written: known widget types only, bounded sizes, simple setting values.
/// </summary>
public sealed partial class DashboardStore(DataPaths paths, ILogger<DashboardStore> logger)
{
    /// <summary>Largest accepted dashboard document.</summary>
    public const int MaxBytes = 192 * 1024;

    /// <summary>Most widgets on a dashboard.</summary>
    public const int MaxWidgets = 48;

    /// <summary>Most widgets kept in the library of widgets the user built.</summary>
    public const int MaxLibrary = 48;

    /// <summary>Largest settings of one widget (a custom widget's design and value sources).</summary>
    public const int MaxSettingsBytes = 4096;

    /// <summary>Widget types the dashboard knows (mirrors the web UI's widget registry).</summary>
    public static readonly IReadOnlySet<string> WidgetTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "capture", "gauges", "chart", "metric", "recorder", "processes", "incidents", "events", "anomalies", "sensors", "gpu", "displays", "network", "custom",
    };

    /// <summary>Screen-size breakpoints the layout may contain.</summary>
    public static readonly IReadOnlySet<string> Breakpoints = new HashSet<string>(StringComparer.Ordinal) { "lg", "md", "sm", "xs" };

    private readonly Lock _gate = new();

    /// <summary>The saved dashboard, or null when the user has not customized it (or the file is unusable).</summary>
    public string? Load()
    {
        try
        {
            var json = File.ReadAllText(paths.DashboardFile);
            return Validate(json) is null ? json : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(logger, ex);
            return null;
        }
    }

    /// <summary>Validates and saves; returns the problem instead when the document is not acceptable.</summary>
    public string? Save(string json)
    {
        if (Validate(json) is { } problem)
        {
            return problem;
        }

        lock (_gate)
        {
            Directory.CreateDirectory(paths.Root);
            var temp = paths.DashboardFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, paths.DashboardFile, overwrite: true);
        }

        return null;
    }

    /// <summary>Back to the default dashboard.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            File.Delete(paths.DashboardFile);
        }
    }

    /// <summary>Checks a dashboard document. Returns null when acceptable, otherwise what is wrong.</summary>
    public static string? Validate(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxBytes)
        {
            return $"A dashboard must be 1–{MaxBytes} bytes.";
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1)
            {
                return "Unsupported dashboard version (expected 1).";
            }

            if (!root.TryGetProperty("widgets", out var widgets) || widgets.ValueKind != JsonValueKind.Array || widgets.GetArrayLength() > MaxWidgets)
            {
                return $"A dashboard has a list of at most {MaxWidgets} widgets.";
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var widget in widgets.EnumerateArray())
            {
                if (ValidateWidget(widget, ids) is { } problem)
                {
                    return problem;
                }
            }

            // The widgets the user built, kept to add again (optional).
            if (root.TryGetProperty("library", out var library))
            {
                if (library.ValueKind != JsonValueKind.Array || library.GetArrayLength() > MaxLibrary)
                {
                    return $"The widget library holds at most {MaxLibrary} widgets.";
                }

                var libraryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var widget in library.EnumerateArray())
                {
                    if (ValidateWidget(widget, libraryIds) is { } problem)
                    {
                        return problem;
                    }
                }
            }

            if (!root.TryGetProperty("layouts", out var layouts) || layouts.ValueKind != JsonValueKind.Object)
            {
                return "A dashboard needs layouts.";
            }

            foreach (var breakpoint in layouts.EnumerateObject())
            {
                if (!Breakpoints.Contains(breakpoint.Name) || breakpoint.Value.ValueKind != JsonValueKind.Array || breakpoint.Value.GetArrayLength() > MaxWidgets)
                {
                    return $"Unknown layout '{Truncate(breakpoint.Name)}'.";
                }

                foreach (var item in breakpoint.Value.EnumerateArray())
                {
                    if (ValidateLayoutItem(item, ids) is { } problem)
                    {
                        return problem;
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "The dashboard is not valid JSON for this version.";
        }
    }

    private static string? ValidateWidget(JsonElement widget, HashSet<string> ids)
    {
        if (widget.ValueKind != JsonValueKind.Object
            || !widget.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String || !WidgetId().IsMatch(id.GetString()!)
            || !ids.Add(id.GetString()!))
        {
            return "Every widget needs a unique id (lowercase letters, digits and dashes, at most 40).";
        }

        if (!widget.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !WidgetTypes.Contains(type.GetString()!))
        {
            return $"Widget '{id.GetString()}' has an unknown type.";
        }

        if (widget.TryGetProperty("title", out var title) && title.ValueKind != JsonValueKind.Null
            && (title.ValueKind != JsonValueKind.String || title.GetString()!.Length > 60))
        {
            return $"Widget '{id.GetString()}' has a title longer than 60 characters.";
        }

        if (widget.TryGetProperty("settings", out var settings))
        {
            if (settings.ValueKind != JsonValueKind.Object || settings.GetRawText().Length > MaxSettingsBytes)
            {
                return $"Widget '{id.GetString()}' has invalid settings.";
            }

            foreach (var setting in settings.EnumerateObject())
            {
                if (setting.Name.Length > 40 || !IsSimpleValue(setting.Value))
                {
                    return $"Widget '{id.GetString()}' setting '{Truncate(setting.Name)}' must be a short text, number, true/false or list of texts.";
                }
            }
        }

        return null;
    }

    private static string? ValidateLayoutItem(JsonElement item, HashSet<string> ids)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("i", out var i) || i.ValueKind != JsonValueKind.String || !ids.Contains(i.GetString()!))
        {
            return "Every layout entry must refer to a widget.";
        }

        foreach (var name in new[] { "x", "y", "w", "h" })
        {
            if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n is < 0 or > 500)
            {
                return $"Layout entry '{i.GetString()}' has an invalid {name}.";
            }
        }

        return null;
    }

    private static bool IsSimpleValue(JsonElement value) => value.ValueKind switch
    {
        // Texts up to a short note; lists of value sources, their labels and colors (a source id is at most 120).
        JsonValueKind.String => value.GetString()!.Length <= 500,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        JsonValueKind.Array => value.GetArrayLength() <= 30 && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 120),
        _ => false,
    };

    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40] + "…";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex WidgetId();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the saved dashboard; using the default")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
