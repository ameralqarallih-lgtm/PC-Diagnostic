using PcDiag.Core.Configuration;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Web.Agent;
using PcDiag.Web.History;

namespace PcDiag.Web.Endpoints;

/// <summary>History charts, the incident list and investigation, the System page.</summary>
internal static class HistoryEndpoints
{
    /// <summary>Longest history range (one-minute summaries are kept 30 days by default).</summary>
    private const long MaxRangeMs = 92L * 24 * 3600 * 1000;

    public static IEndpointRouteBuilder MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // History of the live database.
        // ?sensors=true adds every hardware sensor (charts and dashboard widgets can show any of them).
        api.MapGet("/history/catalog", (HistoryReader history, DataPaths paths, bool? sensors, CancellationToken ct) =>
            history.CatalogAsync(paths.TelemetryDatabase, sensors == true, ct));

        api.MapGet("/history/series", (HistoryReader history, DataPaths paths, string? ids, long? from, long? to, int? points, TimeProvider time, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to, time, TimeSpan.FromHours(1));
            return history.SeriesAsync(paths.TelemetryDatabase, SplitIds(ids), start, end, points ?? 800, ct);
        });

        api.MapGet("/history/events", (HistoryReader history, DataPaths paths, long? from, long? to, int? min, int? limit, TimeProvider time, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to, time, TimeSpan.FromHours(1));
            return history.EventsAsync(paths.TelemetryDatabase, start, end, Math.Clamp(min ?? 1, 0, 4), limit ?? 500, ct);
        });

        // Everything recorded about one moment, incident or not (values, hardware sensors, running processes).
        api.MapGet("/history/moment", (HistoryReader history, DataPaths paths, long t, CancellationToken ct) =>
            history.MomentAsync(paths.TelemetryDatabase, t, RunningProcesses.Snapshot(), ct));

        api.MapGet("/anomalies", (AnomalyReader anomalies, DataPaths paths, long? from, long? to, int? limit, TimeProvider time, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to, time, TimeSpan.FromHours(24));
            return anomalies.ListAsync(paths.TelemetryDatabase, start, end, limit ?? 200, ct);
        });

        // Incidents.
        api.MapGet("/incidents", (IncidentIndexReader incidents, int? limit, int? offset, UserIncidentType? type, IncidentSource? source, long? from, long? to, string? q, CancellationToken ct) =>
            incidents.ListAsync(new IncidentQuery(limit ?? 50, offset ?? 0, type, source, from, to, q is { Length: > 100 } ? q[..100] : q), ct));

        var incident = api.MapGroup("/incidents/{key}");
        incident.MapGet(string.Empty, async (string key, IncidentReader reader, CancellationToken ct) =>
            await reader.DetailAsync(key, ct).ConfigureAwait(false) is { } detail ? Results.Ok(detail) : NotFound(key));

        incident.MapGet("/timeline", async (string key, IncidentReader reader, CancellationToken ct) =>
            Database(reader, key) is null ? NotFound(key) : Results.Ok(await reader.TimelineAsync(key, ct).ConfigureAwait(false)));

        incident.MapGet("/catalog", async (string key, bool? sensors, IncidentReader reader, HistoryReader history, CancellationToken ct) =>
            Database(reader, key) is { } database ? Results.Ok(await history.CatalogAsync(database, sensors == true, ct).ConfigureAwait(false)) : NotFound(key));

        incident.MapGet("/series", async (string key, string? ids, long? from, long? to, int? points, IncidentReader reader, HistoryReader history, CancellationToken ct) =>
        {
            if (await reader.DetailAsync(key, ct).ConfigureAwait(false) is not { } detail)
            {
                return NotFound(key);
            }

            var start = Math.Max(from ?? detail.From, detail.From - 60_000);
            var end = Math.Min(to ?? detail.To, detail.To + 60_000);
            return Results.Ok(await history.SeriesAsync(reader.DatabasePath(key)!, SplitIds(ids), start, end, points ?? 1200, ct).ConfigureAwait(false));
        });

        incident.MapGet("/processes", async (string key, long? t, IncidentReader reader, CancellationToken ct) =>
        {
            if (await reader.DetailAsync(key, ct).ConfigureAwait(false) is not { } detail)
            {
                return NotFound(key);
            }

            return Results.Ok(await reader.ProcessesAsync(key, t ?? detail.Summary.CenterAt.ToUnixTimeMilliseconds(), ct).ConfigureAwait(false));
        });

        incident.MapGet("/findings", async (string key, IncidentReader reader, CancellationToken ct) =>
            Database(reader, key) is null ? NotFound(key) : Results.Ok(await reader.FindingsAsync(key, ct).ConfigureAwait(false)));

        incident.MapGet("/anomalies", async (string key, IncidentReader reader, AnomalyReader anomalies, CancellationToken ct) =>
            Database(reader, key) is { } database
                ? Results.Ok(await anomalies.ListAsync(database, 0, long.MaxValue, AnomalyReader.MaxAnomalies, ct).ConfigureAwait(false))
                : NotFound(key));

        incident.MapGet("/system", async (string key, IncidentReader reader, SystemReader system, CancellationToken ct) =>
            Database(reader, key) is { } database ? Results.Ok(await system.OverviewAsync(database, ct).ConfigureAwait(false)) : NotFound(key));

        // Edits go through the Agent, the only writer of incident data.
        incident.MapPatch(string.Empty, async (string key, IncidentEditRequest? request, IAgentClient agent, CancellationToken ct) =>
        {
            if (request is null || (request.Title is null && request.Notes is null && request.Type is null && request.Pinned is null))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Nothing to change");
            }

            if (request.Title is { } title && (title.Trim().Length == 0 || title.Length > 120))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid title", detail: "A title needs 1–120 characters.");
            }

            if (request.Notes is { Length: > 2000 })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Notes too long", detail: "Notes can be at most 2000 characters.");
            }

            var response = await agent.SendAsync(AgentCommands.UpdateIncident,
                new AgentRequestArgs(IncidentKey: key, Title: request.Title, Note: request.Notes, IncidentType: request.Type, Pinned: request.Pinned), ct).ConfigureAwait(false);
            return EditResult(response, agent);
        });

        incident.MapDelete(string.Empty, async (string key, IncidentReader reader, PcDiag.Storage.Sqlite.SqliteConnectionFactory connections, IAgentClient agent, CancellationToken ct) =>
        {
            // This host's idle connections to the incident's file would keep its folder from being deleted.
            if (reader.DatabasePath(key) is { } database)
            {
                connections.ReleasePooledConnections(database);
            }

            return EditResult(await agent.SendAsync(AgentCommands.DeleteIncident, new AgentRequestArgs(IncidentKey: key), ct).ConfigureAwait(false), agent);
        });

        // The recorder's own cost (CPU budget: PROJECT §20).
        api.MapGet("/recorder/overhead", (HistoryReader history, DataPaths paths, int? minutes, TimeProvider time, CancellationToken ct) =>
        {
            var end = time.GetUtcNow().ToUnixTimeMilliseconds();
            return history.OverheadAsync(paths.TelemetryDatabase, end - (Math.Clamp(minutes ?? 60, 1, 1440) * 60_000L), end, ct);
        });

        // The PC as the recorder sees it now.
        api.MapGet("/system", (SystemReader system, DataPaths paths, CancellationToken ct) => system.OverviewAsync(paths.TelemetryDatabase, ct));

        // Devices connected, disconnected, dropping out or failing, newest first.
        api.MapGet("/system/device-events", (int? limit, SystemReader system, DataPaths paths, CancellationToken ct) =>
            system.DeviceEventsAsync(paths.TelemetryDatabase, limit ?? 200, ct));

        // The newest sensor values only (the Motherboard tab refreshes them every few seconds without re-reading the inventory).
        api.MapGet("/system/sensors", async (string? prefix, SystemReader system, DataPaths paths, CancellationToken ct) =>
            prefix is { } p && (p.Length > 100 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-'))
                ? Results.BadRequest(new { error = "prefix may use letters, digits and / . _ - (at most 100)" })
                : Results.Ok(await system.SensorsAsync(paths.TelemetryDatabase, prefix, ct).ConfigureAwait(false)));

        return app;
    }

    private static (long From, long To) Range(long? from, long? to, TimeProvider time, TimeSpan fallback)
    {
        var end = to ?? time.GetUtcNow().ToUnixTimeMilliseconds();
        var start = from ?? end - (long)fallback.TotalMilliseconds;
        if (start >= end)
        {
            start = end - 1000;
        }

        return (Math.Max(start, end - MaxRangeMs), end);
    }

    /// <summary>The incident's database when the key is valid and the incident has data.</summary>
    private static string? Database(IncidentReader reader, string key) =>
        reader.DatabasePath(key) is { } database && File.Exists(database) ? database : null;

    private static string[] SplitIds(string? ids) =>
        (ids ?? "cpu").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(i => i.Length <= 40).Take(HistoryReader.MaxSeries).ToArray();

    private static IResult NotFound(string key) =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Incident not found", detail: $"There is no incident {(key.Length > 30 ? key[..30] : key)}.");

    private static IResult EditResult(AgentResponse? response, IAgentClient agent) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: $"{agent.UnavailableReason} Incidents are changed by the recorder."),
        { Ok: false, Error: { } error } when error.StartsWith("No such", StringComparison.Ordinal) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Incident not found", detail: error),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The incident could not be changed", detail: response.Error),
        _ => Results.NoContent(),
    };
}

/// <summary>Body of <c>PATCH /api/v1/incidents/{key}</c>; omitted fields stay as they are. Empty notes clear them.</summary>
public sealed record IncidentEditRequest(string? Title, string? Notes, UserIncidentType? Type, bool? Pinned);
