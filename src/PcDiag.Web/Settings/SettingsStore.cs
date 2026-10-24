using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;

namespace PcDiag.Web.Settings;

/// <summary>Storage settings the user can change (the data folder itself is not one of them).</summary>
/// <param name="DataLimitGb">Most space the data may use (0 = no limit).</param>
/// <param name="KeepFreeGb">Free space to keep on the drive (0 = only the 1 GB below which nothing is written).</param>
public sealed record StorageSettings(int TelemetryRetentionHours, int RollupRetentionDays, int WriteBehindSeconds, int MaxIncidents, int DataLimitGb = 10, int KeepFreeGb = 5);

/// <summary>Everything on the Settings page.</summary>
/// <param name="Triggers">Automatic incident triggers; null leaves them as they are.</param>
/// <param name="Enhanced">Enhanced mode (ETW tracing); null leaves it as it is.</param>
public sealed record UserSettings(
    RecorderOptions Recorder,
    SamplingOptions Sampling,
    StorageSettings Storage,
    EventOptions Events,
    PrivacyOptions Privacy,
    AgentOptions Agent,
    TriggerOptions? Triggers = null,
    EnhancedOptions? Enhanced = null);

/// <summary>
/// Reads and writes the user's settings.json in the data folder. The Agent watches that file, so most changes apply
/// within seconds (sampling intervals at the next recording start, the tray and hotkey when the Agent restarts).
/// Every value is checked with the same validators the Agent uses before anything is written, so a bad value can
/// never stop the recorder.
/// </summary>
public sealed class SettingsStore(DataPaths paths)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions SparseJson = new(Json) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly Lock _gate = new();

    /// <summary>The effective settings: shipped defaults overlaid with settings.json.</summary>
    public UserSettings Load()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false).Build();
        T Bind<T>(string section)
            where T : new()
        {
            var value = new T();
            configuration.GetSection(section).Bind(value);
            return value;
        }

        var storage = Bind<StorageOptions>(StorageOptions.SectionName);
        return new UserSettings(
            Bind<RecorderOptions>(RecorderOptions.SectionName),
            Bind<SamplingOptions>(SamplingOptions.SectionName),
            new StorageSettings(storage.TelemetryRetentionHours, storage.RollupRetentionDays, storage.WriteBehindSeconds, storage.MaxIncidents, storage.DataLimitGb, storage.KeepFreeGb),
            Bind<EventOptions>(EventOptions.SectionName),
            Bind<PrivacyOptions>(PrivacyOptions.SectionName),
            Bind<AgentOptions>(AgentOptions.SectionName),
            Bind<TriggerOptions>(TriggerOptions.SectionName),
            Bind<EnhancedOptions>(EnhancedOptions.SectionName));
    }

    /// <summary>Problems with the settings, or an empty list when they can be saved.</summary>
    public static IReadOnlyList<string> Validate(UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var storage = new StorageOptions
        {
            TelemetryRetentionHours = settings.Storage.TelemetryRetentionHours,
            RollupRetentionDays = settings.Storage.RollupRetentionDays,
            WriteBehindSeconds = settings.Storage.WriteBehindSeconds,
            MaxIncidents = settings.Storage.MaxIncidents,
            DataLimitGb = settings.Storage.DataLimitGb,
            KeepFreeGb = settings.Storage.KeepFreeGb,
        };
        var results = new[]
        {
            new RecorderOptionsValidator().Validate(null, settings.Recorder),
            new SamplingOptionsValidator().Validate(null, settings.Sampling),
            new StorageOptionsValidator().Validate(null, storage),
            new EventOptionsValidator().Validate(null, settings.Events),
            new AgentOptionsValidator().Validate(null, settings.Agent),
        };
        var problems = results.Where(r => r.Failed).SelectMany(r => r.Failures ?? []).ToList();
        if (settings.Triggers is { } triggers)
        {
            problems.AddRange(AnomalyRuleCatalog.Default.Validate(triggers, strict: true));
        }

        return problems;
    }

    /// <summary>
    /// Writes the settings into settings.json. Sections this page does not manage, and the data folder, are kept as
    /// they are. Returns the problems instead when the settings are not valid.
    /// </summary>
    public IReadOnlyList<string> Save(UserSettings settings)
    {
        var problems = Validate(settings);
        if (problems.Count > 0)
        {
            return problems;
        }

        lock (_gate)
        {
            var root = ReadExisting();
            root[RecorderOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Recorder, Json);
            root[SamplingOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Sampling, Json);
            root[EventOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Events, Json);
            root[PrivacyOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Privacy, Json);
            root[AgentOptions.SectionName] = JsonSerializer.SerializeToNode(settings.Agent, Json);
            if (settings.Triggers is { } triggers)
            {
                root[TriggerOptions.SectionName] = JsonSerializer.SerializeToNode(Normalize(triggers), SparseJson);
            }

            if (settings.Enhanced is { } enhanced)
            {
                root[EnhancedOptions.SectionName] = JsonSerializer.SerializeToNode(enhanced, Json);
            }

            var storage = root[StorageOptions.SectionName] as JsonObject ?? [];
            storage[nameof(StorageSettings.TelemetryRetentionHours)] = settings.Storage.TelemetryRetentionHours;
            storage[nameof(StorageSettings.RollupRetentionDays)] = settings.Storage.RollupRetentionDays;
            storage[nameof(StorageSettings.WriteBehindSeconds)] = settings.Storage.WriteBehindSeconds;
            storage[nameof(StorageSettings.MaxIncidents)] = settings.Storage.MaxIncidents;
            storage[nameof(StorageSettings.DataLimitGb)] = settings.Storage.DataLimitGb;
            storage[nameof(StorageSettings.KeepFreeGb)] = settings.Storage.KeepFreeGb;
            root[StorageOptions.SectionName] = storage;

            Directory.CreateDirectory(paths.Root);
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }

        return [];
    }

    /// <summary>The user's choice about problem reports ("Reporting"); nothing chosen yet when absent.</summary>
    public Reporting.ReportingSettings LoadReporting()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: false).Build();
        var section = configuration.GetSection("Reporting");
        return new Reporting.ReportingSettings(
            section.GetValue("Automatic", false),
            DateTimeOffset.TryParse(section["AskedAt"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var asked) ? asked : null,
            section["Contact"]);
    }

    /// <summary>Writes the problem-report choice ("Reporting") into settings.json, keeping everything else.</summary>
    public void SaveReporting(Reporting.ReportingSettings reporting)
    {
        ArgumentNullException.ThrowIfNull(reporting);
        lock (_gate)
        {
            var root = ReadExisting();
            root["Reporting"] = new JsonObject
            {
                ["Automatic"] = reporting.Automatic,
                ["AskedAt"] = reporting.AskedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["Contact"] = reporting.Contact,
            };
            Directory.CreateDirectory(paths.Root);
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
    }

    /// <summary>Writes the dashboard's port on this PC ("Web:Port") into settings.json, keeping everything else.</summary>
    public void SavePort(int port)
    {
        lock (_gate)
        {
            var root = ReadExisting();
            var web = root[WebOptions.SectionName] as JsonObject ?? [];
            web[nameof(WebOptions.Port)] = port;
            root[WebOptions.SectionName] = web;
            Directory.CreateDirectory(paths.Root);
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
    }

    /// <summary>Writes network access ("Web:Lan") into settings.json, keeping everything else.</summary>
    public void SaveLan(bool enabled, int port)
    {
        lock (_gate)
        {
            var root = ReadExisting();
            var web = root[WebOptions.SectionName] as JsonObject ?? [];
            var lan = web[nameof(WebOptions.Lan)] as JsonObject ?? [];
            lan[nameof(LanOptions.Enabled)] = enabled;
            lan[nameof(LanOptions.Port)] = port;
            web[nameof(WebOptions.Lan)] = lan;
            root[WebOptions.SectionName] = web;
            Directory.CreateDirectory(paths.Root);
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(Json));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
    }

    /// <summary>Only what differs from the shipped rules is stored, so later changes to the shipped defaults still apply.</summary>
    internal static TriggerOptions Normalize(TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new TriggerOptions
        {
            AutomaticIncidents = options.AutomaticIncidents,
            MaxIncidentsPerHour = options.MaxIncidentsPerHour,
            Custom = [.. options.Custom.Where(c => c is not null)],
            Removed = [.. options.Removed.Where(id => AnomalyRuleCatalog.Default.Find(id) is not null).Distinct(StringComparer.OrdinalIgnoreCase)],
            Categories = [.. options.Categories.Where(c => c is not null).Select(c => new TriggerCategory { Id = c.Id, Name = c.Name.Trim() })],
        };

        // Shipped rules moved to another category (a move back to where it starts is not stored).
        foreach (var (id, category) in options.RuleCategories)
        {
            if (AnomalyRuleCatalog.Default.Find(id) is { } shipped && !string.IsNullOrWhiteSpace(category) && category != AnomalyCategories.DefaultFor(shipped))
            {
                result.RuleCategories[shipped.Id] = category;
            }
        }
        foreach (var (id, change) in options.Rules)
        {
            if (change is null || AnomalyRuleCatalog.Default.Find(id) is not { } rule)
            {
                continue;
            }

            var kept = new TriggerRuleSettings
            {
                Enabled = change.Enabled == rule.Enabled ? null : change.Enabled,
                CreateIncident = change.CreateIncident == rule.CreateIncident ? null : change.CreateIncident,
                Threshold = rule.Kind == AnomalyRuleKind.Event || change.Threshold == rule.Threshold ? null : change.Threshold,
                DurationSeconds = !rule.UsesDuration || change.DurationSeconds == rule.DurationSeconds ? null : change.DurationSeconds,
                WindowSeconds = !rule.UsesWindow || change.WindowSeconds == rule.WindowSeconds ? null : change.WindowSeconds,
                CooldownMinutes = change.CooldownMinutes == rule.CooldownMinutes ? null : change.CooldownMinutes,
            };
            if (kept.Enabled is not null || kept.CreateIncident is not null || kept.Threshold is not null || kept.DurationSeconds is not null
                || kept.WindowSeconds is not null || kept.CooldownMinutes is not null)
            {
                result.Rules[rule.Id] = kept;
            }
        }

        return result;
    }

    private JsonObject ReadExisting()
    {
        if (!File.Exists(paths.SettingsFile))
        {
            return [];
        }

        try
        {
            var text = File.ReadAllText(paths.SettingsFile);
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            // Keep the unreadable file for the user to look at; start a fresh one.
            File.Copy(paths.SettingsFile, paths.SettingsFile + ".bak", overwrite: true);
            return [];
        }
    }
}
