using Microsoft.Extensions.Options;
using PcDiag.Core.Recording;

namespace PcDiag.Core.Configuration;

/// <summary>Validates <see cref="StorageOptions"/>.</summary>
public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (DataPaths.ValidateDirectory(options.DataDirectory) is { } pathError)
        {
            errors.Add(pathError);
        }

        Range.Check(errors, "Storage:TelemetryRetentionHours", options.TelemetryRetentionHours, 1, 72);
        Range.Check(errors, "Storage:RollupRetentionDays", options.RollupRetentionDays, 1, 365);
        Range.Check(errors, "Storage:WriteBehindSeconds", options.WriteBehindSeconds, 1, 60);
        Range.Check(errors, "Storage:MaxIncidents", options.MaxIncidents, 0, 100_000);
        Range.Check(errors, "Storage:DataLimitGb", options.DataLimitGb, 0, 10_000);
        Range.Check(errors, "Storage:KeepFreeGb", options.KeepFreeGb, 0, 1000);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Validates <see cref="RecorderOptions"/>.</summary>
public sealed class RecorderOptionsValidator : IValidateOptions<RecorderOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RecorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (!Enum.IsDefined(options.Mode))
        {
            errors.Add($"Recorder:Mode '{options.Mode}' is not a valid recording mode.");
        }

        if (!RecorderOptions.AllowedPreIncidentMinutes.Contains(options.PreIncidentMinutes))
        {
            errors.Add($"Recorder:PreIncidentMinutes must be one of {string.Join(", ", RecorderOptions.AllowedPreIncidentMinutes)} (was {options.PreIncidentMinutes}).");
        }

        Range.Check(errors, "Recorder:PostIncidentSeconds", options.PostIncidentSeconds, 15, 900);
        Range.Check(errors, "Recorder:RingBufferMaxMegabytes", options.RingBufferMaxMegabytes, 16, 512);

        if (options.Mode == RecordingMode.Scheduled && options.Schedule.Count == 0)
        {
            errors.Add("Recorder:Schedule must contain at least one window when Recorder:Mode is Scheduled.");
        }

        for (var i = 0; i < options.Schedule.Count; i++)
        {
            if (options.Schedule[i].Days.Count == 0)
            {
                errors.Add($"Recorder:Schedule[{i}] must list at least one day.");
            }

            CheckSelection(errors, $"Recorder:Schedule[{i}]:Selection", options.Schedule[i].Selection);
        }

        CheckSelection(errors, "Recorder:CustomSelection", options.CustomSelection);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    /// <summary>A custom selection records at least one item, and its IDs are plain text of a sensible length.</summary>
    private static void CheckSelection(List<string> errors, string name, RecordingSelection? selection)
    {
        if (selection is null)
        {
            return;
        }

        if (selection.Items.Count == 0)
        {
            errors.Add($"{name} must select at least one item (leave it out to record everything).");
        }
        else if (selection.Items.Count > RecordingSelection.MaxItems)
        {
            errors.Add($"{name} may select at most {RecordingSelection.MaxItems} items (was {selection.Items.Count}).");
        }
        else if (selection.Items.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 400 || item.Any(char.IsControl)))
        {
            errors.Add($"{name} contains an item ID that is empty, too long or has control characters.");
        }
    }
}

/// <summary>Validates <see cref="SamplingOptions"/>.</summary>
public sealed class SamplingOptionsValidator : IValidateOptions<SamplingOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, SamplingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        Range.Check(errors, "Sampling:SystemIntervalMs", options.SystemIntervalMs, 250, 10_000);
        Range.Check(errors, "Sampling:SensorIntervalMs", options.SensorIntervalMs, 250, 10_000);
        Range.Check(errors, "Sampling:ProcessIntervalMs", options.ProcessIntervalMs, 500, 30_000);
        Range.Check(errors, "Sampling:InventoryIntervalSeconds", options.InventoryIntervalSeconds, 5, 300);
        Range.Check(errors, "Sampling:TopProcessCount", options.TopProcessCount, 5, 200);

        if (options.CpuBudgetPercent is < 0.5 or > 10 || double.IsNaN(options.CpuBudgetPercent))
        {
            errors.Add($"Sampling:CpuBudgetPercent must be between 0.5 and 10 (was {options.CpuBudgetPercent}).");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Validates <see cref="EventOptions"/>.</summary>
public sealed class EventOptionsValidator : IValidateOptions<EventOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EventOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        Range.Check(errors, "Events:BackfillMinutes", options.BackfillMinutes, 0, 1440);
        Range.Check(errors, "Events:BootBackfillMaxHours", options.BootBackfillMaxHours, 0, 168);
        Range.Check(errors, "Events:MaxPerRulePerMinute", options.MaxPerRulePerMinute, 1, 1000);
        Range.Check(errors, "Events:MaxMessageLength", options.MaxMessageLength, 200, 32_000);

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

/// <summary>Validates <see cref="TriggerOptions"/> against the shipped anomaly rules (unknown rule IDs are ignored).</summary>
public sealed class TriggerOptionsValidator : IValidateOptions<TriggerOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TriggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = Anomalies.AnomalyRuleCatalog.Default.Validate(options, strict: false).Where(e => !e.StartsWith("Triggers:Custom:", StringComparison.Ordinal)).ToList();
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

file static class Range
{
    public static void Check(List<string> errors, string key, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{key} must be between {min} and {max} (was {value}).");
        }
    }
}
