using System.Text;
using System.Text.Json;
using PcDiag.Core.Reporting;
using PcDiag.Web.Reporting;

namespace PcDiag.Web.Endpoints;

/// <summary>Problem reports for the makers of PC Diagnostic: manual ones, the automatic ones, and the user's consent.</summary>
internal static class ReportEndpoints
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var reports = app.MapGroup("/api/v1/reports");

        reports.MapGet(string.Empty, (ProblemReportService service) => Results.Ok(service.Overview()));

        reports.MapGet("/{id}", (string id, ProblemReportService service) =>
            service.Get(id) is { } report ? Results.Ok(report) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such report"));

        // The report as a file, to e-mail when this build cannot send it.
        reports.MapGet("/{id}/download", (string id, ProblemReportService service) =>
            service.Get(id) is { } report
                ? Results.File(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, Indented)), "application/json", $"pc-diagnostic-report-{report.At:yyyyMMdd-HHmmss}.json")
                : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such report"));

        // Exactly what would be sent, before anything is saved or sent.
        reports.MapPost("/preview", async (ManualReportRequest request, ProblemReportService service, CancellationToken ct) =>
            Validate(request) ?? Results.Ok(await service.ComposeAsync(request, ct).ConfigureAwait(false)));

        reports.MapPost(string.Empty, async (ManualReportRequest request, ProblemReportService service, CancellationToken ct) =>
        {
            if (Validate(request) is { } invalid)
            {
                return invalid;
            }

            var (entry, outcome) = await service.SubmitAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(new { report = entry, outcome });
        });

        reports.MapPost("/{id}/send", async (string id, ProblemReportService service, CancellationToken ct) =>
            ProblemSpool.IsId(id) ? Results.Ok(await service.SendAsync(id, ct).ConfigureAwait(false)) : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such report"));

        reports.MapDelete("/{id}", (string id, ProblemReportService service) =>
            service.Delete(id) ? Results.NoContent() : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such report"));

        reports.MapPut("/consent", (ConsentRequest request, ProblemReportService service) =>
            request.Contact is { Length: > 200 }
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Contact too long")
                : Results.Ok(service.SetConsent(request.Automatic, request.Contact)));

        // Errors the dashboard caught in the browser (kept like the others; sent only with consent).
        reports.MapPost("/client-error", (ClientErrorRequest request, ProblemReportService service) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 2000 || request.Stack is { Length: > 20_000 } || request.Page is { Length: > 300 } || request.Component is { Length: > 80 })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid error report");
            }

            service.CaptureClientError(request);
            return Results.NoContent();
        });

        return app;
    }

    private static IResult? Validate(ManualReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Summary) || request.Summary.Trim().Length is < 3 or > 200)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Say in a line what went wrong (3–200 characters).");
        }

        if (request.Description is { Length: > 5000 } || request.Contact is { Length: > 200 } || request.Page is { Length: > 300 })
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The description can be at most 5000 characters and the contact 200.");
        }

        return null;
    }
}

/// <summary>Body of <c>PUT /api/v1/reports/consent</c>.</summary>
public sealed record ConsentRequest(bool Automatic, string? Contact);
