using System.Text;
using PcDiag.Web.Dashboard;

namespace PcDiag.Web.Endpoints;

/// <summary>The user's dashboard layout and widget settings.</summary>
internal static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var dashboard = app.MapGroup("/api/v1/dashboard");

        // 204 when the user has not customized the dashboard: the UI shows its default.
        dashboard.MapGet(string.Empty, (DashboardStore store) =>
            store.Load() is { } json ? Results.Text(json, "application/json", Encoding.UTF8) : Results.NoContent());

        dashboard.MapPut(string.Empty, async (HttpRequest request, DashboardStore store, CancellationToken cancellationToken) =>
        {
            if (request.ContentLength is > DashboardStore.MaxBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Dashboard too large", detail: $"At most {DashboardStore.MaxBytes} bytes.");
            }

            using var reader = new StreamReader(request.Body, Encoding.UTF8);
            var buffer = new char[DashboardStore.MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await reader.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false)) > 0)
            {
                read += n;
            }

            if (read > DashboardStore.MaxBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Dashboard too large", detail: $"At most {DashboardStore.MaxBytes} bytes.");
            }

            return store.Save(new string(buffer, 0, read)) is { } problem
                ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid dashboard", detail: problem)
                : Results.NoContent();
        });

        dashboard.MapDelete(string.Empty, (DashboardStore store) =>
        {
            store.Reset();
            return Results.NoContent();
        });

        return app;
    }
}
