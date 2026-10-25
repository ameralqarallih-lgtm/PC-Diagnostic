using System.Globalization;
using PcDiag.Core.Configuration;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Storage;
using PcDiag.Storage.Sqlite;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Storage.Sqlite.Telemetry;
using PcDiag.Web.Agent;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// The storage manager: what the data folder uses (measured here, read-only), each incident's size, the limits, and
/// the recorder's view (the drive's free space, its condition, recent clean-ups). Clean-ups and deletions are done by
/// the recorder, which owns the data.
/// </summary>
internal static class StorageEndpoints
{
    /// <summary>Most incidents deleted in one request.</summary>
    public const int MaxDelete = 500;

    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder app)
    {
        var storage = app.MapGroup("/api/v1/storage");

        storage.MapGet(string.Empty, async (DataPaths paths, IncidentIndexReader index, SqliteConnectionFactory connections, SettingsStore settings, IAgentClient agent, CancellationToken ct) =>
        {
            var usage = StorageMeter.Measure(paths);
            var drive = new DriveDiskSpace().Read(paths.Root);
            var limits = settings.Load().Storage;

            // Every incident with its folder's size (pages of the index's largest page size).
            var incidents = new List<StorageIncident>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var offset = 0; ; offset += IncidentIndexReader.MaxPageSize)
            {
                var page = await index.ListAsync(new IncidentQuery(IncidentIndexReader.MaxPageSize, offset), ct).ConfigureAwait(false);
                foreach (var i in page.Items)
                {
                    known.Add(i.Key);
                    incidents.Add(new StorageIncident(i.Key, i.Title, i.CenterAt, i.State, i.Source, i.UserType, i.Severity, i.Pinned, i.SnapshotOnly,
                        usage.IncidentBytes.GetValueOrDefault(i.Key)));
                }

                if (page.Items.Count < IncidentIndexReader.MaxPageSize || offset + page.Items.Count >= page.Total)
                {
                    break;
                }
            }

            // Folders whose incident is gone (the recorder removes them once nothing holds their files).
            var leftovers = usage.IncidentBytes.Where(f => !known.Contains(f.Key)).Sum(f => f.Value);

            TelemetryCoverage? coverage = null;
            if (File.Exists(paths.TelemetryDatabase))
            {
                try
                {
                    await using var connection = await connections.OpenReadOnlyAsync(paths.TelemetryDatabase, ct).ConfigureAwait(false);
                    coverage = await TelemetryMaintenance.ReadCoverageAsync(connection, paths.TelemetryDatabase, ct).ConfigureAwait(false);
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Busy or being replaced: the page shows the sizes without the span.
                }
            }

            var response = await agent.SendAsync(AgentCommands.StorageState, null, ct).ConfigureAwait(false);
            var recorder = response is { Ok: true } ? response.Storage : null;
            return Results.Ok(new StorageResponse(
                paths.Root,
                drive,
                usage.TotalBytes,
                usage.LimitedBytes,
                usage.Kinds,
                incidents,
                leftovers,
                coverage,
                new StorageLimitsView(limits.DataLimitGb, limits.KeepFreeGb, limits.MaxIncidents, limits.TelemetryRetentionHours, limits.RollupRetentionDays,
                    StorageLimits.FullDetailFloor.TotalHours, StorageLimits.SummariesFloor.TotalDays, StorageLimits.PauseBelowBytes, StorageLimits.ResumeAboveBytes),
                recorder,
                recorder?.Recent ?? StorageLog.Read(paths.StorageLogFile),
                response is null ? agent.UnavailableReason : response.Ok ? null : response.Error));
        });

        // Apply the limits now and give the recording database's free space back to the drive.
        storage.MapPost("/clean", async (StorageCleanRequest? request, IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.StorageClean, new AgentRequestArgs(Compact: request?.Compact ?? true), ct).ConfigureAwait(false);
            return response switch
            {
                null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: $"{agent.UnavailableReason} The recorder does the clean-up."),
                { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not cleaned up", detail: response.Error),
                _ => Results.Ok(response.Cleaned),
            };
        });

        // Delete several incidents (each one by the recorder, as on the incident's own page).
        storage.MapPost("/incidents/delete", async (StorageDeleteRequest? request, IncidentReader reader, SqliteConnectionFactory connections, IAgentClient agent, CancellationToken ct) =>
        {
            if (request?.Keys is not { Count: > 0 } keys || keys.Count > MaxDelete)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Which incidents?", detail: $"Name 1–{MaxDelete} incidents.");
            }

            if (keys.FirstOrDefault(k => !IncidentKey.TryParse(k, CultureInfo.InvariantCulture, out _)) is { } bad)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not an incident", detail: $"{(bad.Length > 30 ? bad[..30] : bad)} is not an incident key.");
            }

            var deleted = new List<string>();
            var problems = new List<StorageDeleteProblem>();
            foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                // This host's idle connections to the incident's file would keep its folder from being deleted.
                if (reader.DatabasePath(key) is { } database)
                {
                    connections.ReleasePooledConnections(database);
                }

                var response = await agent.SendAsync(AgentCommands.DeleteIncident, new AgentRequestArgs(IncidentKey: key), ct).ConfigureAwait(false);
                if (response is null)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable",
                        detail: $"{agent.UnavailableReason} Incidents are deleted by the recorder.{(deleted.Count > 0 ? $" {deleted.Count} were deleted before it stopped answering." : "")}");
                }

                if (response.Ok)
                {
                    deleted.Add(key);
                }
                else
                {
                    problems.Add(new StorageDeleteProblem(key, response.Error ?? "Not deleted."));
                }
            }

            return Results.Ok(new StorageDeleteResult(deleted, problems));
        });

        return app;
    }
}

/// <summary>An incident and the space it uses.</summary>
public sealed record StorageIncident(
    string Key,
    string Title,
    DateTimeOffset CenterAt,
    IncidentState State,
    IncidentSource Source,
    UserIncidentType? UserType,
    Core.Events.Severity Severity,
    bool Pinned,
    bool SnapshotOnly,
    long Bytes);

/// <summary>The limits as they apply now, and the fixed floors.</summary>
public sealed record StorageLimitsView(
    int DataLimitGb,
    int KeepFreeGb,
    int MaxIncidents,
    int TelemetryRetentionHours,
    int RollupRetentionDays,
    double FullDetailFloorHours,
    double SummariesFloorDays,
    long PauseBelowBytes,
    long ResumeAboveBytes);

/// <summary>Everything the Storage page shows.</summary>
/// <param name="Folder">The data folder.</param>
/// <param name="Drive">Its drive's size and free space.</param>
/// <param name="TotalBytes">What the data folder uses.</param>
/// <param name="LimitedBytes">What the space limit counts: the data folder without the character generator's folder.</param>
/// <param name="Kinds">By kind, largest first.</param>
/// <param name="Incidents">Every incident with its size, newest first.</param>
/// <param name="LeftoverBytes">Files of deleted incidents still waiting to be removed.</param>
/// <param name="Telemetry">How far back the recording database goes and its free space.</param>
/// <param name="Limits">The limits.</param>
/// <param name="Recorder">The recorder's view (null when it is not reachable).</param>
/// <param name="Recent">Recent clean-ups, newest first.</param>
/// <param name="RecorderProblem">Why the recorder did not answer.</param>
public sealed record StorageResponse(
    string Folder,
    DiskSpace? Drive,
    long TotalBytes,
    long LimitedBytes,
    IReadOnlyList<StorageKindUsage> Kinds,
    IReadOnlyList<StorageIncident> Incidents,
    long LeftoverBytes,
    TelemetryCoverage? Telemetry,
    StorageLimitsView Limits,
    StorageState? Recorder,
    IReadOnlyList<StorageAction> Recent,
    string? RecorderProblem);

/// <summary>Body of <c>POST /api/v1/storage/clean</c>.</summary>
public sealed record StorageCleanRequest(bool? Compact);

/// <summary>Body of <c>POST /api/v1/storage/incidents/delete</c>.</summary>
public sealed record StorageDeleteRequest(IReadOnlyList<string>? Keys);

/// <summary>An incident that was not deleted, and why.</summary>
public sealed record StorageDeleteProblem(string Key, string Problem);

/// <summary>What a deletion did.</summary>
public sealed record StorageDeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<StorageDeleteProblem> Problems);
