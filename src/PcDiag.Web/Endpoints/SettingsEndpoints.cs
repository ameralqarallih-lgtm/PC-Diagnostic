using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Endpoints;

/// <summary>The Settings page.</summary>
internal static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/settings", (SettingsStore store, DataPaths paths) => new SettingsResponse(store.Load(), paths.SettingsFile));

        // The anomaly rules: shipped values, and the rule in plain words with the user's changes applied.
        app.MapGet("/api/v1/triggers", (SettingsStore store) => TriggerRuleInfo.List(store.Load().Triggers ?? new TriggerOptions()));
        app.MapGet("/api/v1/triggers/choices", () => TriggerChoices.Value);
        app.MapGet("/api/v1/triggers/readings", (DataPaths paths) => TriggerReadings.List(paths));
        app.MapGet("/api/v1/triggers/suggestions", async (PcDiag.Storage.Sqlite.Reading.SystemReader system, DataPaths paths, CancellationToken ct) =>
            await TriggerSuggestions.ListAsync(system, paths, ct).ConfigureAwait(false));

        app.MapPut("/api/v1/settings", (UserSettings? settings, SettingsStore store, DataPaths paths) =>
        {
            if (settings is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No settings sent");
            }

            var problems = store.Save(settings);
            return problems.Count == 0
                ? Results.Ok(new SettingsResponse(store.Load(), paths.SettingsFile))
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [.. problems] }, title: "Some settings are not valid");
        });

        return app;
    }
}

/// <summary>An anomaly rule as the Settings page shows it.</summary>
/// <param name="Id">Rule ID.</param>
/// <param name="Name">Name.</param>
/// <param name="Classification">Classification of its anomalies.</param>
/// <param name="Kind">How it decides.</param>
/// <param name="Metric">The watched metric's label (null for event rules).</param>
/// <param name="PerDevice">Evaluated per GPU, disk or network adapter.</param>
/// <param name="Description">The rule in plain words, with the user's changes applied.</param>
/// <param name="Note">Why the default is what it is.</param>
/// <param name="Severity">Severity.</param>
/// <param name="Unit">Unit of the threshold.</param>
/// <param name="ThresholdMin">Smallest threshold.</param>
/// <param name="ThresholdMax">Largest threshold.</param>
/// <param name="UsesThreshold">Whether the threshold can be changed.</param>
/// <param name="UsesDuration">Whether the duration can be changed.</param>
/// <param name="UsesWindow">Whether the window can be changed.</param>
/// <param name="Defaults">The shipped values.</param>
public sealed record TriggerRuleInfo(
    string Id,
    string Name,
    string Classification,
    AnomalyRuleKind Kind,
    string? Metric,
    bool PerDevice,
    string Description,
    string? Note,
    Core.Events.Severity Severity,
    string Unit,
    double ThresholdMin,
    double ThresholdMax,
    bool UsesThreshold,
    bool UsesDuration,
    bool UsesWindow,
    TriggerDefaults Defaults,
    bool Custom = false,
    bool Removed = false,
    string DefaultCategory = "")
{
    /// <summary>All rules with the given settings applied to their descriptions.</summary>
    public static IReadOnlyList<TriggerRuleInfo> List(TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var catalog = AnomalyRuleCatalog.Default;
        var removed = options.Removed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        TriggerRuleInfo Info(AnomalyRule rule, bool custom)
        {
            var effective = options.Rules.TryGetValue(rule.Id, out var change) && change is not null && !custom ? AnomalyRuleCatalog.Apply(rule, change) : rule;
            var (min, max) = rule.ThresholdRange;
            return new TriggerRuleInfo(rule.Id, rule.Name, rule.Classification, rule.Kind, rule.MetricInfo?.Label, rule.MetricInfo?.PerDevice ?? false,
                effective.Describe(), rule.Note, rule.Severity, rule.ThresholdUnit, min, max, rule.Kind != AnomalyRuleKind.Event, rule.UsesDuration,
                rule.UsesWindow,
                new TriggerDefaults(rule.Enabled, rule.CreateIncident, rule.Threshold, rule.DurationSeconds, rule.WindowSeconds, rule.CooldownMinutes),
                custom, removed.Contains(rule.Id), AnomalyCategories.DefaultFor(rule));
        }

        return [.. catalog.Rules.Select(r => Info(r, false)), .. AnomalyRuleCatalog.CustomRules(options).Select(r => Info(r, true))];
    }
}

/// <summary>What a rule of the user's own can watch: the metrics, and for event rules the events.</summary>
/// <param name="Metrics">Measurements (with unit and allowed range).</param>
/// <param name="ProcessEvents">Process transitions.</param>
/// <param name="DisplayChanges">Display changes.</param>
/// <param name="WindowsEvents">Classified Windows event rules (ID and title).</param>
/// <param name="Categories">The built-in categories.</param>
public sealed record TriggerChoices(IReadOnlyList<AnomalyMetric> Metrics, IReadOnlyList<string> ProcessEvents, IReadOnlyList<string> DisplayChanges, IReadOnlyList<TriggerEventChoice> WindowsEvents,
    IReadOnlyList<TriggerCategory> Categories)
{
    /// <summary>The choices (fixed for a build).</summary>
    public static TriggerChoices Value { get; } = new(
        AnomalyMetrics.All,
        Enum.GetNames<Core.Events.ProcessEventKind>(),
        Enum.GetNames<Core.Events.DisplayEventKind>(),
        [.. Engine.Events.EventRuleSet.Load(Engine.Events.EventRuleSet.LoadDefaultJson(), null).Rules.Where(r => !r.Rule.Ignore).Select(r => new TriggerEventChoice(r.Rule.Id, r.Rule.Title ?? r.Rule.Id))],
        AnomalyCategories.BuiltIn);
}

/// <summary>One of the system's readings a rule can watch.</summary>
/// <param name="Id">The reading (the rule's metric is "reading:" + this).</param>
/// <param name="Label">Its name.</param>
/// <param name="Group">The device or part it belongs to.</param>
/// <param name="Unit">Its unit.</param>
public sealed record TriggerReading(string Id, string Label, string Group, string Unit);

/// <summary>The system's readings (those the recorder has seen, as the on-screen panels list them).</summary>
public static class TriggerReadings
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The numeric readings, by group (texts, the clock and the game's own values are left out).</summary>
    public static IReadOnlyList<TriggerReading> List(DataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var file = Path.Combine(paths.Root, "overlay-sources.json");
        try
        {
            var sources = File.Exists(file) ? System.Text.Json.JsonSerializer.Deserialize<List<Core.Overlay.OverlaySource>>(File.ReadAllText(file), Json) ?? [] : [];
            return [.. sources
                .Where(s => s.Text is null && s.Room is null && s.Group is not ("This PC" or "Gaming"))
                .Select(s => new TriggerReading(s.Id, s.Label, s.Group, s.Unit))
                .DistinctBy(s => s.Id)
                .OrderBy(s => s.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Names a rule can be narrowed to: programs running now, displays, event logs and sources.</summary>
public sealed record TriggerSuggestions(IReadOnlyList<string> Processes, IReadOnlyList<string> Displays, IReadOnlyList<string> Channels, IReadOnlyList<string> Providers)
{
    private static readonly Lazy<(IReadOnlyList<string> Channels, IReadOnlyList<string> Providers)> EventSources = new(() =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return (["System", "Application"], []);
        }

        try
        {
            using var session = new System.Diagnostics.Eventing.Reader.EventLogSession();
            return ([.. session.GetLogNames().Order(StringComparer.OrdinalIgnoreCase)], [.. session.GetProviderNames().Order(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            return (["System", "Application"], []);
        }
    });

    /// <summary>The names now.</summary>
    public static async Task<TriggerSuggestions> ListAsync(PcDiag.Storage.Sqlite.Reading.SystemReader system, DataPaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(paths);
        var processes = System.Diagnostics.Process.GetProcesses();
        List<string> names;
        try
        {
            names = [.. processes.Select(p => p.ProcessName + ".exe").Where(n => n is not ("Idle.exe" or "System.exe")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        var displays = new List<string>();
        try
        {
            if ((await system.OverviewAsync(paths.TelemetryDatabase, cancellationToken).ConfigureAwait(false)).Display is { } display
                && display.TryGetProperty(nameof(Displays), out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var d in list.EnumerateArray())
                {
                    if (d.TryGetProperty("FriendlyName", out var name) && name.GetString() is { Length: > 0 } text)
                    {
                        displays.Add(text);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            // No display recorded yet: none suggested.
        }

        var (channels, providers) = EventSources.Value;
        return new(names, [.. displays.Distinct(StringComparer.OrdinalIgnoreCase)], channels, providers);
    }
}

/// <summary>A classified Windows event rule a user rule can watch.</summary>
public sealed record TriggerEventChoice(string Id, string Title);

/// <summary>A rule's shipped values.</summary>
public sealed record TriggerDefaults(bool Enabled, bool CreateIncident, double Threshold, double DurationSeconds, double WindowSeconds, int CooldownMinutes);

/// <summary>Response of the settings endpoints.</summary>
/// <param name="Settings">The effective settings.</param>
/// <param name="File">Where they are stored.</param>
public sealed record SettingsResponse(UserSettings Settings, string File);
