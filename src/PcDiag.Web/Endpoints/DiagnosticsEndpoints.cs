using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PcDiag.Core.Anomalies;
using PcDiag.Core.Configuration;
using PcDiag.Core.Diagnostics;
using PcDiag.Engine.Diagnostics;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Web.Diagnostics;
using PcDiag.Web.Reports;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// The period diagnostic (Phase 17): everything recorded between two times analyzed aspect by aspect against the
/// user's anomaly rules, as a report to read in the browser or download as HTML, PDF or JSON. The dashboard runs it as
/// a background job (<see cref="DiagnosticJobs"/>): <c>POST /diagnostics/jobs</c> starts one, <c>GET</c> follows it
/// and lists recent ones, <c>DELETE</c> cancels or forgets one. <c>GET /diagnostics/period</c> runs one and waits.
/// </summary>
internal static class DiagnosticsEndpoints
{
    /// <summary>How long a report is kept, so a download matches the report on screen.</summary>
    internal static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>Most incidents one diagnostic covers.</summary>
    internal const int MaxIncidents = 20;

    private static readonly System.Text.RegularExpressions.Regex IncidentKeys = new("^INC-[0-9]{8}-[0-9]{5}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Shortest period.</summary>
    internal static readonly TimeSpan MinPeriod = TimeSpan.FromMinutes(1);

    private const int CachedReports = 4;

    private static readonly ConcurrentDictionary<(long From, long To), PeriodReport> Cache = new();

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/diagnostics");

        // Background jobs: they run whatever the browser does, and are found again by ID.
        api.MapPost("/jobs", async (StartDiagnosticRequest? request, DiagnosticJobs jobs, IncidentReader incidents, TimeProvider time, CancellationToken ct) =>
        {
            // Incidents chosen: the period that covers their recorded windows (an incident captured at one moment, a
            // minute around it).
            long? askedFrom = request?.From, askedTo = request?.To;
            List<string>? keys = null;
            if (request?.Incidents is { Count: > 0 } chosen)
            {
                keys = [.. chosen.Distinct(StringComparer.Ordinal)];
                if (keys.Count > MaxIncidents || keys.Any(k => !IncidentKeys.IsMatch(k)))
                {
                    return Invalid($"Choose 1-{MaxIncidents} incidents.");
                }

                var windows = new List<(long From, long To)>();
                foreach (var key in keys)
                {
                    if (await incidents.DetailAsync(key, ct).ConfigureAwait(false) is not { } detail)
                    {
                        return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such incident", detail: $"{key} does not exist (it may have been deleted).");
                    }

                    var (start, end) = (detail.From, detail.To);
                    if (end - start < MinPeriod.TotalMilliseconds)
                    {
                        var middle = (start + end) / 2;
                        (start, end) = (middle - 30_000, middle + 30_000);
                    }

                    windows.Add((start, end));
                }

                (askedFrom, askedTo) = (windows.Min(w => w.From), windows.Max(w => w.To));
            }

            if (Validate(askedFrom, askedTo, time.GetUtcNow(), out var from, out var to) is { } problem)
            {
                return keys is null ? problem : Invalid("These incidents span more than a diagnostic can cover (31 days); choose incidents closer together.");
            }

            return jobs.Start(DateTimeOffset.FromUnixTimeMilliseconds(from), DateTimeOffset.FromUnixTimeMilliseconds(to), request!.Fresh ?? false, keys) is { } job
                ? Results.Accepted($"/api/v1/diagnostics/jobs/{job.Id}", job)
                : Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many diagnostics",
                    detail: $"{DiagnosticJobs.MaxRunning} diagnostics are already queued or running; wait for one to finish or cancel one.");
        });

        api.MapGet("/jobs", (DiagnosticJobs jobs) => jobs.List());

        api.MapGet("/jobs/{id}", (string id, DiagnosticJobs jobs) =>
            JobId(id) && jobs.Get(id) is { } detail ? Results.Ok(detail) : UnknownJob());

        api.MapDelete("/jobs/{id}", (string id, DiagnosticJobs jobs) =>
            JobId(id) && jobs.CancelOrRemove(id) ? Results.NoContent() : UnknownJob());

        api.MapGet("/jobs/{id}/export", async (string id, string? format, DiagnosticJobs jobs, PdfPrinter printer, TimeProvider time, CancellationToken ct) =>
        {
            if (!JobId(id) || jobs.Get(id) is not { } detail)
            {
                return UnknownJob();
            }

            return detail.Report is { } report
                ? await ExportAsync(report, format, printer, time, ct).ConfigureAwait(false)
                : Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not finished", detail: "The diagnostic has no report yet.");
        });

        api.MapGet("/period", async (long? from, long? to, bool? fresh, PeriodEvidenceReader reader, SettingsStore settings, TimeProvider time, CancellationToken ct) =>
        {
            var (report, problem) = await RunAsync(from, to, fresh ?? false, reader, settings, time, ct).ConfigureAwait(false);
            return problem ?? Results.Ok(report);
        });

        api.MapGet("/period/export", async (long? from, long? to, string? format, PeriodEvidenceReader reader, SettingsStore settings, PdfPrinter printer, TimeProvider time,
            CancellationToken ct) =>
        {
            if (!Format(format))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown format", detail: "format must be html, pdf or json.");
            }

            var (report, problem) = await RunAsync(from, to, false, reader, settings, time, ct).ConfigureAwait(false);
            return problem ?? await ExportAsync(report!, format, printer, time, ct).ConfigureAwait(false);
        });

        return app;
    }

    private static bool Format(string? format) => (format ?? "html").ToLowerInvariant() is "html" or "pdf" or "json";

    private static async Task<IResult> ExportAsync(PeriodReport report, string? format, PdfPrinter printer, TimeProvider time, CancellationToken ct)
    {
        if (!Format(format))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown format", detail: "format must be html, pdf or json.");
        }

        var zone = time.LocalTimeZone;
        var name = "period-" + TimeZoneInfo.ConvertTime(report.From, zone).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)
            + "-" + TimeZoneInfo.ConvertTime(report.To, zone).ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        switch ((format ?? "html").ToLowerInvariant())
        {
            case "json":
                return Results.File(JsonSerializer.SerializeToUtf8Bytes(report, IncidentReport.Json), "application/json", name + ".json");

            case "pdf":
                var pdf = await printer.PrintAsync(PeriodHtmlReport.Render(report, zone), ct).ConfigureAwait(false);
                return pdf is null
                    ? Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "PDF is not available",
                        detail: "PDF needs Microsoft Edge. Download the HTML report and print it to PDF from the browser instead.")
                    : Results.File(pdf, "application/pdf", name + ".pdf");

            default:
                return Results.File(Encoding.UTF8.GetBytes(PeriodHtmlReport.Render(report, zone)), "text/html; charset=utf-8", name + ".html");
        }
    }

    /// <summary>Job IDs are generated (yyyyMMdd-HHmmss-xxxxxx); anything else is refused before a lookup.</summary>
    private static bool JobId(string id) => id.Length == 22 && id.All(c => char.IsAsciiDigit(c) || c is '-' || char.IsAsciiHexDigitLower(c));

    private static IResult UnknownJob() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Diagnostic not found", detail: "There is no diagnostic with that ID.");

    /// <summary>The period (Unix ms) checked: both ends, at least a minute, at most <see cref="PeriodEvidenceReader.MaxPeriod"/>, the end not after now.</summary>
    internal static IResult? Validate(long? from, long? to, DateTimeOffset now, out long start, out long end)
    {
        start = from ?? 0;
        end = Math.Min(to ?? 0, now.ToUnixTimeMilliseconds());
        if (from is null || to is null)
        {
            return Invalid("from and to (Unix milliseconds) are required.");
        }

        if (end - start < MinPeriod.TotalMilliseconds)
        {
            return Invalid("The period must be at least a minute long and not in the future.");
        }

        return end - start > PeriodEvidenceReader.MaxPeriod.TotalMilliseconds
            ? Invalid($"The period can be at most {PeriodEvidenceReader.MaxPeriod.TotalDays:0} days long.")
            : null;
    }

    /// <summary>The report of a period (Unix ms), from the cache when it was made in the last few minutes and <paramref name="fresh"/> is not set.</summary>
    internal static async Task<(PeriodReport? Report, IResult? Problem)> RunAsync(
        long? from, long? to, bool fresh, PeriodEvidenceReader reader, SettingsStore settings, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (Validate(from, to, now, out var f, out var t) is { } invalid)
        {
            return (null, invalid);
        }

        var key = (f, t);
        if (!fresh && Cache.TryGetValue(key, out var cached) && now - cached.GeneratedAt < CacheFor)
        {
            return (cached, null);
        }

        await DiagnosticJobs.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!fresh && Cache.TryGetValue(key, out cached) && now - cached.GeneratedAt < CacheFor)
            {
                return (cached, null);
            }

            var evidence = await reader.LoadAsync(DateTimeOffset.FromUnixTimeMilliseconds(f), DateTimeOffset.FromUnixTimeMilliseconds(t), ct).ConfigureAwait(false);
            var rules = AnomalyRuleCatalog.Default.Apply(settings.Load().Triggers ?? new TriggerOptions());
            var report = PeriodDiagnostics.Analyze(evidence, rules, time.LocalTimeZone, time.GetUtcNow());
            // Expired reports go, then the oldest until there is room.
            var kept = Cache.OrderBy(x => x.Value.GeneratedAt).ToList();
            for (var i = 0; i < kept.Count; i++)
            {
                if (report.GeneratedAt - kept[i].Value.GeneratedAt >= CacheFor || kept.Count - i >= CachedReports)
                {
                    Cache.TryRemove(kept[i].Key, out _);
                }
            }

            Cache[key] = report;
            return (report, null);
        }
        finally
        {
            DiagnosticJobs.Gate.Release();
        }
    }

    private static IResult Invalid(string detail) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid period", detail: detail);
}

/// <summary>
/// <c>POST /api/v1/diagnostics/jobs</c>: the period (Unix ms), or the incidents whose recorded windows make it, and
/// whether to analyze afresh rather than reuse a report of the last few minutes.
/// </summary>
public sealed record StartDiagnosticRequest(long? From, long? To, bool? Fresh, IReadOnlyList<string>? Incidents = null);
