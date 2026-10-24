using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Web.Reports;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Reports and exports of an incident, downloaded through the browser (PROJECT §18): the quick or technical report as
/// HTML or PDF, CSV and JSON files, and a ZIP with the layout incident.json, summary.html, metrics.csv,
/// processes.csv, services.csv, events.csv, hardware.json, display.json, drivers.json, system.json.
/// </summary>
internal static class ExportEndpoints
{
    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/incidents/{key}/export", async (
            HttpContext context,
            string key,
            string? format,
            string? report,
            string? file,
            bool? keep,
            PcDiag.Storage.Files.FileStore store,
            IncidentReader incidents,
            HistoryReader history,
            SystemReader system,
            AnomalyReader anomalies,
            ExportReader exports,
            PdfPrinter printer,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var kind = string.Equals(report, "technical", StringComparison.OrdinalIgnoreCase) ? ReportKind.Technical : ReportKind.Quick;
            var model = await IncidentReport.LoadAsync(key, incidents, history, system, anomalies, time, ct).ConfigureAwait(false);
            if (model is null || incidents.DatabasePath(key) is not { } database)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Incident not found", detail: "There is no recorded incident with that key.");
            }

            var name = model.Detail.Summary.Key;
            var suffix = kind == ReportKind.Technical ? "technical" : "quick";

            // Asked to keep it: a copy in the storage locations that keep exports (the download is the same).
            async Task<byte[]> Kept(byte[] content, string fileName)
            {
                if (keep == true && PcDiag.Storage.Files.FileKinds.IsName(fileName))
                {
                    using var copy = new MemoryStream(content);
                    await store.PutAsync(PcDiag.Storage.Files.FileKinds.Exports, fileName, copy, ct).ConfigureAwait(false);
                }

                return content;
            }
            switch ((format ?? "html").ToLowerInvariant())
            {
                case "html":
                    return Results.File(await Kept(Encoding.UTF8.GetBytes(HtmlReport.Render(model, kind)), $"{name}-{suffix}.html").ConfigureAwait(false), "text/html; charset=utf-8", $"{name}-{suffix}.html");

                case "pdf":
                    var pdf = await printer.PrintAsync(HtmlReport.Render(model, kind), ct).ConfigureAwait(false);
                    return pdf is null
                        ? Results.Problem(statusCode: StatusCodes.Status501NotImplemented, title: "PDF is not available",
                            detail: "PDF needs Microsoft Edge. Download the HTML report and print it to PDF from the browser instead.")
                        : Results.File(await Kept(pdf, $"{name}-{suffix}.pdf").ConfigureAwait(false), "application/pdf", $"{name}-{suffix}.pdf");

                case "json":
                    return Results.File(await Kept(JsonSerializer.SerializeToUtf8Bytes(model.ToJson(), IncidentReport.Json), $"{name}.json").ConfigureAwait(false), "application/json", $"{name}.json");

                case "csv":
                    var which = (file ?? "metrics").ToLowerInvariant();
                    if (which is not ("metrics" or "processes" or "services" or "events"))
                    {
                        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown file", detail: "file must be metrics, processes, services or events.");
                    }

                    SetAttachment(context, $"{name}-{which}.csv", "text/csv; charset=utf-8");
                    await using (var writer = new StreamWriter(context.Response.Body, Utf8Bom, 64 * 1024, leaveOpen: true))
                    {
                        await WriteCsvAsync(which, exports, database, model, writer, ct).ConfigureAwait(false);
                    }

                    return Results.Empty;

                case "zip":
                    // Built in a temporary file (deleted when the download ends): the archive writes parts of itself synchronously.
                    var temp = new FileStream(Path.Combine(Path.GetTempPath(), $"pcdiag-{Guid.NewGuid():N}.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                        64 * 1024, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                    try
                    {
                        await WriteZipAsync(temp, model, database, exports, ct).ConfigureAwait(false);
                        if (keep == true && PcDiag.Storage.Files.FileKinds.IsName($"{name}.zip"))
                        {
                            temp.Position = 0;
                            await store.PutAsync(PcDiag.Storage.Files.FileKinds.Exports, $"{name}.zip", temp, ct).ConfigureAwait(false);
                        }

                        temp.Position = 0;
                        return Results.File(temp, "application/zip", $"{name}.zip");
                    }
                    catch
                    {
                        await temp.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }

                default:
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown format", detail: "format must be html, pdf, json, csv or zip.");
            }
        });

        return app;
    }

    private static void SetAttachment(HttpContext context, string fileName, string contentType)
    {
        context.Response.ContentType = contentType;
        context.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = fileName, DispositionType = "attachment" }.ToString();
    }

    private static Task WriteCsvAsync(string which, ExportReader exports, string database, IncidentReport model, TextWriter writer, CancellationToken ct) => which switch
    {
        "metrics" => exports.WriteMetricsAsync(database, model.Detail.From, model.Detail.To, writer, ct),
        "processes" => exports.WriteProcessesAsync(database, writer, ct),
        "services" => exports.WriteServicesAsync(database, writer, ct),
        _ => exports.WriteEventsAsync(database, writer, ct),
    };

    /// <summary>The ZIP, written straight to the response. summary.html is the technical report (it ends with the diagnostic summary).</summary>
    internal static async Task WriteZipAsync(Stream output, IncidentReport model, string database, ExportReader exports, CancellationToken ct)
    {
        await using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        async Task TextAsync(string entryName, Func<TextWriter, Task> write)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = model.GeneratedAt;
            await using var stream = await entry.OpenAsync(ct).ConfigureAwait(false);
            await using var writer = new StreamWriter(stream, entryName.EndsWith(".csv", StringComparison.Ordinal) ? Utf8Bom : new UTF8Encoding(false), 64 * 1024);
            await write(writer).ConfigureAwait(false);
        }

        Task JsonAsync(string entryName, object? value) => TextAsync(entryName, w => w.WriteAsync(JsonSerializer.Serialize(value, IncidentReport.Json)));

        await JsonAsync("incident.json", model.ToJson()).ConfigureAwait(false);
        await TextAsync("summary.html", w => w.WriteAsync(HtmlReport.Render(model, ReportKind.Technical))).ConfigureAwait(false);
        await TextAsync("metrics.csv", w => exports.WriteMetricsAsync(database, model.Detail.From, model.Detail.To, w, ct)).ConfigureAwait(false);
        await TextAsync("processes.csv", w => exports.WriteProcessesAsync(database, w, ct)).ConfigureAwait(false);
        await TextAsync("services.csv", w => exports.WriteServicesAsync(database, w, ct)).ConfigureAwait(false);
        await TextAsync("events.csv", w => exports.WriteEventsAsync(database, w, ct)).ConfigureAwait(false);
        await JsonAsync("hardware.json", new { capturedUtc = Utc(model.System.HardwareTs), hardware = model.System.Hardware }).ConfigureAwait(false);
        await JsonAsync("display.json", new
        {
            capturedUtc = Utc(model.System.DisplayTs),
            configuration = model.System.Display,
            changes = await exports.DisplayChangesAsync(database, ct).ConfigureAwait(false),
        }).ConfigureAwait(false);
        await JsonAsync("drivers.json", new { capturedUtc = Utc(model.System.DriversTs), drivers = model.System.Drivers }).ConfigureAwait(false);
        await JsonAsync("system.json", new
        {
            information = model.System.Information,
            devices = model.System.Devices,
            sensors = model.System.Sensors,
            availability = model.System.Availability,
        }).ConfigureAwait(false);

        // Enhanced mode's flight recorder trace (for Windows Performance Analyzer), when one was saved.
        var trace = TracePath(database);
        if (File.Exists(trace))
        {
            var entry = zip.CreateEntry("traces/incident.etl", CompressionLevel.Fastest);
            entry.LastWriteTime = File.GetLastWriteTime(trace);
            await using var source = new FileStream(trace, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var target = await entry.OpenAsync(ct).ConfigureAwait(false);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Where Enhanced mode saves an incident's flight recorder trace (beside its incident.db).</summary>
    internal static string TracePath(string database) => Path.Combine(Path.GetDirectoryName(database)!, "traces", "incident.etl");

    private static DateTime? Utc(long? unixMs) => unixMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
}
