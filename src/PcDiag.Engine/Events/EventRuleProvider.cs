using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;

namespace PcDiag.Engine.Events;

/// <summary>
/// Loads the shipped rules plus the user's %LOCALAPPDATA%\PcDiagnostic\event-rules.json (if any). Called when
/// recording starts, so edits apply on the next start. A broken user file never stops collection: its problems
/// are logged and the valid rules are used.
/// </summary>
public sealed partial class EventRuleProvider
{
    /// <summary>Largest accepted user rules file.</summary>
    public const long MaxUserFileBytes = 1024 * 1024;

    private readonly DataPaths _paths;
    private readonly ILogger<EventRuleProvider> _logger;
    private readonly IOptionsMonitor<TriggerOptions>? _triggers;
    private readonly Lazy<string> _defaultJson = new(EventRuleSet.LoadDefaultJson);
    private int _version;

    /// <summary>Creates the provider.</summary>
    /// <param name="paths">The data folder (the user's rules file).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="triggers">The anomaly rules: Windows events the user's own rules describe become rules too.</param>
    public EventRuleProvider(DataPaths paths, ILogger<EventRuleProvider> logger, IOptionsMonitor<TriggerOptions>? triggers = null)
    {
        _paths = paths;
        _logger = logger;
        _triggers = triggers;
        triggers?.OnChange(_ => Interlocked.Increment(ref _version));
    }

    /// <summary>Changes when the rules may have changed (a collector then loads them again).</summary>
    public int Version => Volatile.Read(ref _version);

    /// <summary>Loads the current rules.</summary>
    public EventRuleSet Load()
    {
        string? userJson = null;
        var file = new FileInfo(_paths.EventRulesFile);
        if (file.Exists)
        {
            if (file.Length > MaxUserFileBytes)
            {
                LogUserFileProblem(_logger, file.FullName, $"larger than {MaxUserFileBytes / 1024} KB; ignored");
            }
            else
            {
                try
                {
                    userJson = File.ReadAllText(file.FullName);
                }
                catch (IOException ex)
                {
                    LogUserFileProblem(_logger, file.FullName, ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogUserFileProblem(_logger, file.FullName, ex.Message);
                }
            }
        }

        var rules = EventRuleSet.Load(_defaultJson.Value, WithTriggerEvents(userJson));
        foreach (var problem in rules.Problems)
        {
            LogUserFileProblem(_logger, file.FullName, problem);
        }

        return rules;
    }

    /// <summary>
    /// The user's rules file with a rule added for each Windows event the user's own anomaly rules describe (log,
    /// source, IDs, levels), so those events are collected and classified like the shipped ones.
    /// </summary>
    private string? WithTriggerEvents(string? userJson)
    {
        var made = new System.Text.Json.Nodes.JsonArray();
        foreach (var custom in _triggers?.CurrentValue.Custom ?? [])
        {
            if (custom is not { Kind: Core.Anomalies.AnomalyRuleKind.Event, Enabled: true })
            {
                continue;
            }

            foreach (var (match, index) in custom.WindowsEvents.Select((m, i) => (m, i)))
            {
                if (match is null || string.IsNullOrWhiteSpace(match.Channel))
                {
                    continue;
                }

                made.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = Core.Anomalies.AnomalyRuleCatalog.WindowsEventRuleId(custom.Id, index),
                    ["channel"] = match.Channel.Trim(),
                    ["providers"] = new System.Text.Json.Nodes.JsonArray([.. string.IsNullOrWhiteSpace(match.Provider) ? [] : new System.Text.Json.Nodes.JsonNode?[] { match.Provider.Trim() }]),
                    ["eventIds"] = new System.Text.Json.Nodes.JsonArray([.. match.EventIds.Select(id => (System.Text.Json.Nodes.JsonNode?)id)]),
                    ["levels"] = new System.Text.Json.Nodes.JsonArray([.. match.Levels.Select(l => (System.Text.Json.Nodes.JsonNode?)l)]),
                    ["category"] = "Other",
                    ["severity"] = custom.Severity.ToString(),
                    ["title"] = $"{custom.Name.Trim()} ({{provider}} {{eventId}})",
                });
            }
        }

        if (made.Count == 0)
        {
            return userJson;
        }

        System.Text.Json.Nodes.JsonObject document;
        try
        {
            document = userJson is null
                ? new System.Text.Json.Nodes.JsonObject { ["version"] = 1, ["rules"] = new System.Text.Json.Nodes.JsonArray() }
                : System.Text.Json.Nodes.JsonNode.Parse(userJson, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })?.AsObject()
                    ?? new System.Text.Json.Nodes.JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return userJson; // the file's own problem is reported when it is loaded
        }

        if (document["rules"] is not System.Text.Json.Nodes.JsonArray rules)
        {
            document["rules"] = rules = new System.Text.Json.Nodes.JsonArray();
        }

        foreach (var rule in made.ToList())
        {
            made.Remove(rule);
            rules.Add(rule);
        }

        return document.ToJsonString();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event rules file {Path}: {Problem}")]
    private static partial void LogUserFileProblem(ILogger logger, string path, string problem);
}
