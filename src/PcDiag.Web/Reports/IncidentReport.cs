using System.Text.Json;
using PcDiag.Core.Anomalies;
using PcDiag.Core.History;
using PcDiag.Storage.Sqlite.Reading;

namespace PcDiag.Web.Reports;

/// <summary>Which report.</summary>
public enum ReportKind
{
    /// <summary>A human-readable summary: what happened, the key moments and charts, the diagnostic summary.</summary>
    Quick = 0,

    /// <summary>Everything: all charts, the full timeline, processes, events, services, display, drivers, system.</summary>
    Technical = 1,
}

/// <summary>A chart group: the series it plots (those the incident has).</summary>
/// <param name="Title">Heading.</param>
/// <param name="Unit">Unit of every series in the group.</param>
/// <param name="Series">Series and their buckets.</param>
/// <param name="Fixed">Percent charts run 0–100.</param>
public sealed record ReportChart(string Title, string Unit, IReadOnlyList<(SeriesInfo Info, SeriesData Data)> Series, bool Fixed);

/// <summary>Everything a report shows, read from the incident's own database.</summary>
public sealed record IncidentReport(
    IncidentDetail Detail,
    IReadOnlyList<TimelineEntry> Timeline,
    IReadOnlyList<FindingEntry> Findings,
    IReadOnlyList<AnomalyInfo> Anomalies,
    SystemOverview System,
    ProcessesAtTime Processes,
    IReadOnlyList<ReportChart> Charts,
    DateTimeOffset GeneratedAt,
    TimeZoneInfo Zone)
{
    /// <summary>The incident moment, Unix ms.</summary>
    public long Center => Detail.Summary.CenterAt.ToUnixTimeMilliseconds();

    /// <summary>Reads an incident for a report; null when it does not exist or has no data.</summary>
    public static async Task<IncidentReport?> LoadAsync(
        string key,
        IncidentReader incidents,
        HistoryReader history,
        SystemReader system,
        AnomalyReader anomalies,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (await incidents.DetailAsync(key, cancellationToken).ConfigureAwait(false) is not { HasData: true } detail || incidents.DatabasePath(key) is not { } database)
        {
            return null;
        }

        var timeline = await incidents.TimelineAsync(key, cancellationToken).ConfigureAwait(false);
        var findings = await incidents.FindingsAsync(key, cancellationToken).ConfigureAwait(false);
        var anomalyList = await anomalies.ListAsync(database, 0, long.MaxValue, AnomalyReader.MaxAnomalies, cancellationToken).ConfigureAwait(false);
        var overview = await system.OverviewAsync(database, cancellationToken).ConfigureAwait(false);
        var processes = await incidents.ProcessesAsync(key, detail.Summary.CenterAt.ToUnixTimeMilliseconds(), cancellationToken).ConfigureAwait(false);
        var catalog = await history.CatalogAsync(database, cancellationToken).ConfigureAwait(false);
        var charts = new List<ReportChart>();
        foreach (var (title, unit, ids, isFixed) in ChartGroups(catalog))
        {
            var result = await history.SeriesAsync(database, ids, detail.From, detail.To, 600, cancellationToken).ConfigureAwait(false);
            var series = result.Series.Where(s => s.Points.Count > 0).Select(s => (catalog.First(c => c.Id == s.Id), s)).ToList();
            if (series.Count > 0)
            {
                charts.Add(new ReportChart(title, unit, series, isFixed));
            }
        }

        return new IncidentReport(detail, timeline, findings, anomalyList, overview, processes, charts, time.GetUtcNow(), time.LocalTimeZone);
    }

    /// <summary>The charts, in report order (the first two are the quick report's).</summary>
    private static IEnumerable<(string Title, string Unit, string[] Ids, bool Fixed)> ChartGroups(IReadOnlyList<SeriesInfo> catalog)
    {
        string[] Device(string kind, params string[] suffixes) =>
            catalog.Where(c => c.Id.StartsWith(kind + ".", StringComparison.Ordinal) && c.Id.Count(ch => ch == '.') == 2 && suffixes.Any(s => c.Id.EndsWith("." + s, StringComparison.Ordinal)))
                .Select(c => c.Id).Take(HistoryReader.MaxSeries).ToArray();

        yield return ("Utilization", "%", ["cpu", "memory", "gpu", "vram", "disk"], true);
        yield return ("Temperatures", "°C", ["cpu.temp", .. Device("gpu", "temp", "hotspot"), .. Device("disk", "temp")], false);
        yield return ("Available memory", "B", ["memory.available"], false);
        yield return ("Memory commit", "%", ["memory.commit"], true);
        yield return ("GPU clocks", "MHz", Device("gpu", "core", "mem"), false);
        yield return ("GPU power", "W", Device("gpu", "power"), false);
        yield return ("CPU frequency", "MHz", ["cpu.freq"], false);
        yield return ("Disk latency", "ms", Device("disk", "readlat", "writelat"), false);
        yield return ("Disk throughput", "B/s", Device("disk", "read", "write"), false);
        yield return ("Network", "B/s", ["net", .. Device("net", "rx", "tx")], false);
        yield return ("Network errors", "", Device("net", "errors"), false);
    }

    /// <summary>The incident as JSON for incident.json.</summary>
    public object ToJson() => new
    {
        format = "pcdiagnostic-incident/1",
        generatedUtc = GeneratedAt.UtcDateTime,
        incident = Detail.Summary,
        window = new { fromUtc = DateTimeOffset.FromUnixTimeMilliseconds(Detail.From).UtcDateTime, toUtc = DateTimeOffset.FromUnixTimeMilliseconds(Detail.To).UtcDateTime },
        triggers = Detail.Triggers,
        info = Detail.Info,
        findings = Findings,
        anomalies = Anomalies,
        timeline = Timeline,
    };

    /// <summary>JSON options for the export files.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
