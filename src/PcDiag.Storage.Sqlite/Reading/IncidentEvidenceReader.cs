using System.Text.Json;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Diagnostics;
using PcDiag.Core.Events;
using PcDiag.Core.Incidents;
using PcDiag.Core.Telemetry;
using PcDiag.Storage.Sqlite.Telemetry;

namespace PcDiag.Storage.Sqlite.Reading;

/// <summary>
/// Reads what the diagnostic analyzers need from an incident's own database: the metric tracks they look at (about
/// one value per second), the timeline, anomalies and triggers. Used by the Agent, which owns incident databases, so
/// an incident from an older version is brought to the current schema first.
/// </summary>
public sealed class IncidentEvidenceReader(IncidentReader incidents, HistoryReader history, SqliteConnectionFactory connectionFactory, DatabaseInitializer initializer)
    : IIncidentEvidenceSource
{
    /// <summary>System-wide series the analyzers use.</summary>
    internal static readonly HashSet<string> SystemSeries = new(StringComparer.Ordinal)
    {
        "cpu", "memory", "gpu", "vram", "disk", "cpu.temp", "cpu.limit", "memory.commit", "memory.available", "memory.hardfaults",
    };

    /// <summary>Per-device series the analyzers use, by device prefix.</summary>
    internal static readonly Dictionary<string, HashSet<string>> DeviceSeries = new(StringComparer.Ordinal)
    {
        ["gpu"] = new(StringComparer.Ordinal) { "util", "temp", "hotspot", "core", "power" },
        ["disk"] = new(StringComparer.Ordinal) { "readlat", "writelat", "queue", "active" },
        ["net"] = new(StringComparer.Ordinal) { "errors", "wifi" },
    };

    private static readonly Dictionary<string, string> SourceTables = new(StringComparer.Ordinal)
    {
        ["windows-event"] = "WindowsEvents",
        ["process"] = "ProcessEvents",
        ["service"] = "ServiceEvents",
        ["display"] = "DisplayEvents",
        ["anomaly"] = "Anomalies",
        ["recorder"] = "RecorderSessions",
        ["trigger"] = "IncidentTriggers",
    };

    /// <inheritdoc />
    public async Task<IncidentEvidence?> LoadAsync(IncidentKey key, CancellationToken cancellationToken)
    {
        var keyText = key.ToString();
        if (await incidents.DetailAsync(keyText, cancellationToken).ConfigureAwait(false) is not { HasData: true } detail || incidents.DatabasePath(keyText) is not { } database)
        {
            return null;
        }

        await initializer.InitializeIncidentDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
        var summary = detail.Summary;
        var tracks = await TracksAsync(database, detail.From, detail.To, cancellationToken).ConfigureAwait(false);
        var events = (await incidents.TimelineAsync(keyText, cancellationToken).ConfigureAwait(false)).Select(ToEvent).ToList();
        var anomalies = await AnomaliesAsync(database, cancellationToken).ConfigureAwait(false);
        var triggers = detail.Triggers
            .Select(t => new IncidentTrigger(UnixTime.FromUnixMs(t.Ts), t.Source, t.UserType, t.RuleId, t.Detail, t.Note))
            .ToList();

        return new IncidentEvidence(key, summary.CenterAt, UnixTime.FromUnixMs(detail.From), UnixTime.FromUnixMs(detail.To), summary.Source, summary.UserType,
            summary.SnapshotOnly, tracks, events, anomalies, triggers, RecordedItems(detail.Info.GetValueOrDefault("recording_selection")));
    }

    /// <summary>A custom recording's items (a JSON array); null when everything was recorded or the value is unreadable.</summary>
    internal static IReadOnlySet<string>? RecordedItems(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) is { } items ? new HashSet<string>(items, StringComparer.Ordinal) : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a series ID is one the analyzers use; returns its metric kind ("gpu.util" for "gpu.3.util").</summary>
    internal static string? MetricOf(string seriesId)
    {
        if (SystemSeries.Contains(seriesId))
        {
            return seriesId;
        }

        var parts = seriesId.Split('.');
        return parts.Length == 3 && DeviceSeries.TryGetValue(parts[0], out var suffixes) && suffixes.Contains(parts[2]) ? $"{parts[0]}.{parts[2]}" : null;
    }

    private async Task<List<MetricTrack>> TracksAsync(string database, long from, long to, CancellationToken cancellationToken)
    {
        var twins = await VirtualGpuTwinsAsync(database, cancellationToken).ConfigureAwait(false);
        var catalog = (await history.CatalogAsync(database, cancellationToken).ConfigureAwait(false))
            .Select(info => (Info: info, Metric: MetricOf(info.Id)))
            .Where(x => x.Metric is not null && !twins.Any(t => x.Info.Id.StartsWith($"gpu.{t}.", StringComparison.Ordinal)))
            .ToList();
        var points = (int)Math.Clamp((to - from) / 1000, 10, HistoryReader.MaxPoints);
        var tracks = new List<MetricTrack>();
        foreach (var chunk in catalog.Chunk(HistoryReader.MaxSeries))
        {
            var result = await history.SeriesAsync(database, chunk.Select(c => c.Info.Id), from, to, points, cancellationToken).ConfigureAwait(false);
            foreach (var series in result.Series)
            {
                var (info, metric) = chunk.First(c => c.Info.Id == series.Id);
                if (series.Points.Count == 0)
                {
                    continue;
                }

                // Buckets are [time, min, average, max]; at one sample per bucket all three are the sample.
                tracks.Add(new MetricTrack(info.Id, metric!, info.Label, info.Unit, info.Device,
                    series.Points.Select(p => new MetricPoint(UnixTime.FromUnixMs((long)p[0]), p[2])).ToList()));
            }
        }

        return tracks;
    }

    /// <summary>
    /// GPU devices that were a virtual twin of a real card, recorded before the GPU collector skipped them (7 Oct 2026):
    /// same name as another GPU, a stable key that is that GPU's key plus "-N", and no NVML source where the real card
    /// has one (NVML matches cards by PCI location, which the twin lacks). Their tracks repeat the card's clocks with
    /// none of its load and would contradict the real card's findings. Two real identical NVIDIA cards both have NVML.
    /// </summary>
    internal async Task<HashSet<long>> VirtualGpuTwinsAsync(string database, CancellationToken cancellationToken)
    {
        var gpus = new List<(long Id, string Key, string Name, bool Nvml)>();
        await using (var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT device_id, stable_key, name, details_json FROM Devices WHERE kind = 'gpu';";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var details = reader.IsDBNull(3) ? "" : reader.GetString(3);
                gpus.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2), details.Contains("\"nvml\"", StringComparison.Ordinal)));
            }
        }

        return gpus
            .Where(t => !t.Nvml && gpus.Any(real => real.Nvml && real.Name == t.Name && t.Key.Length > real.Key.Length + 1
                && t.Key.StartsWith(real.Key + "-", StringComparison.Ordinal) && t.Key[(real.Key.Length + 1)..].All(char.IsAsciiDigit)))
            .Select(t => t.Id)
            .ToHashSet();
    }

    private async Task<List<Anomaly>> AnomaliesAsync(string database, CancellationToken cancellationToken)
    {
        var anomalies = new List<Anomaly>();
        await using var connection = await connectionFactory.OpenReadOnlyAsync(database, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {AnomalyStore.Columns} FROM Anomalies ORDER BY start_ts, id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            anomalies.Add(AnomalyStore.Read(reader));
        }

        return anomalies;
    }

    internal static EvidenceEvent ToEvent(Core.History.TimelineEntry entry)
    {
        EventCategory? category = null;
        string? provider = null;
        int? eventId = null;
        string? ruleId = null;
        if (entry.Kind == "windows-event" && entry.Detail is { ValueKind: JsonValueKind.Object } detail)
        {
            if (detail.TryGetProperty("category", out var c) && c.TryGetInt32(out var value) && Enum.IsDefined((EventCategory)value))
            {
                category = (EventCategory)value;
            }

            provider = detail.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            eventId = detail.TryGetProperty("eventId", out var id) && id.TryGetInt32(out var number) ? number : null;
            ruleId = detail.TryGetProperty("ruleId", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }

        return new EvidenceEvent(UnixTime.FromUnixMs(entry.Ts), entry.Kind, entry.Title, entry.Importance, SourceTables.GetValueOrDefault(entry.Kind), entry.SourceId,
            category, provider, eventId, ruleId);
    }
}
