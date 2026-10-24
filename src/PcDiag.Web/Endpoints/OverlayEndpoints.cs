using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;
using PcDiag.Core.Overlay;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// The readings shown on the screens: the layout (overlay.json, written here and read by the recorder, which draws
/// it; the recorder saves it too when the panels are arranged on the screens), and the recorder's monitors and values
/// for the editor. Each answer carries the saved layout's revision, so the editor sees a change made on the screens.
/// </summary>
internal static class OverlayEndpoints
{
    /// <summary>Largest layout accepted.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>A backup's name starts so (then its time, UTC).</summary>
    public const string BackupPrefix = PcDiag.Storage.Files.LayoutBackups.Prefix;

    /// <summary>
    /// Keeps the layout as it is now as a backup (the rules in <see cref="PcDiag.Storage.Files.LayoutBackups"/>); a
    /// backup that cannot be made never stops a save.
    /// </summary>
    private static Task<bool> KeepBackupAsync(PcDiag.Storage.Files.FileStore store, DataPaths paths, ILogger logger, bool always, CancellationToken ct) =>
        PcDiag.Storage.Files.LayoutBackups.KeepFileAsync(store, paths.OverlayFile, always, logger, ct);

    private static readonly Lock WriteLock = new();

    /// <summary>The names a layout's JSON may have at its top (its properties, in any case).</summary>
    private static readonly HashSet<string> LayoutNames = new(typeof(OverlayConfig).GetProperties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapOverlayEndpoints(this IEndpointRouteBuilder app)
    {
        var overlay = app.MapGroup("/api/v1/overlay");

        overlay.MapGet(string.Empty, async (DataPaths paths, IAgentClient agent, CancellationToken ct) =>
        {
            var revision = OverlayStyles.Revision(paths.OverlayFile);
            if (!OverlayStyles.TryRead(paths.OverlayFile, out var config, out var problem))
            {
                // Being replaced by the recorder just now: never an empty layout the editor could then save over it.
                return Results.Problem("The layout is being saved; try again.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var response = await agent.SendAsync(AgentCommands.OverlayState, null, ct).ConfigureAwait(false);
            return Results.Ok(new OverlayResponse(config, response?.Overlay is { } state ? state with { Revision = revision } : null, problem, response is null ? agent.UnavailableReason : response.Ok ? null : response.Error, OverlayStyles.Presets, revision));
        });

        // The monitors and values only (the editor's preview refreshes them every second or two), with the saved
        // layout's revision.
        overlay.MapGet("/state", async (DataPaths paths, IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.OverlayState, null, ct).ConfigureAwait(false);
            return StateResult(agent, response, paths);
        });

        // Arranging the panels on the screens themselves: started and ended from the editor (also the recorder's
        // hotkey and tray). Changes only on the PC itself.
        overlay.MapPost("/arrange", async (ArrangeRequest? body, DataPaths paths, IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.OverlayArrange, new AgentRequestArgs(On: body?.On ?? true), ct).ConfigureAwait(false);
            return StateResult(agent, response, paths);
        });

        // The USB screens' driver packages: listed, and approved by their content (or withdrawn) on this PC only.
        overlay.MapGet("/screen-drivers", async (IAgentClient agent, CancellationToken ct) =>
            DriversResult(agent, await agent.SendAsync(AgentCommands.OverlayScreenDrivers, null, ct).ConfigureAwait(false)));
        overlay.MapPost("/screen-drivers/approve", async (DriverApproval body, IAgentClient agent, CancellationToken ct) =>
            DriversResult(agent, await agent.SendAsync(AgentCommands.OverlayScreenDriverApprove, new AgentRequestArgs(On: body.Approve, DriverId: body.Id, Sha256: body.Sha256), ct).ConfigureAwait(false)));

        // The layout's backups (kept before saves) and bringing one back.
        overlay.MapGet("/backups", async (PcDiag.Storage.Files.FileStore store, CancellationToken ct) =>
            Results.Ok(await PcDiag.Storage.Files.LayoutBackups.ListAsync(store, ct).ConfigureAwait(false)));
        overlay.MapPost("/backups/{name}/restore", async (string name, DataPaths paths, IAgentClient agent, PcDiag.Storage.Files.FileStore store, IOptions<JsonOptions> json, ILogger<OverlayResponse> logger, CancellationToken ct) =>
        {
            if (!PcDiag.Storage.Files.LayoutBackups.IsName(name))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such backup");
            }

            await using var stream = await store.OpenAsync(PcDiag.Storage.Files.FileKinds.Backups, name, ct).ConfigureAwait(false);
            if (stream is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such backup", detail: "No storage location has it (or none can be reached).");
            }

            OverlayConfig? config;
            try
            {
                config = await JsonSerializer.DeserializeAsync<OverlayConfig>(stream, json.Value.SerializerOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The backup cannot be read", detail: ex.Message);
            }

            var problem = config is null ? "It is empty." : OverlayStyles.Validate(config);
            if (config is null || problem is not null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The backup is not a layout this version takes", detail: problem);
            }

            // The layout now is kept too (whatever its age), so bringing back an older one can be undone.
            await KeepBackupAsync(store, paths, logger, always: true, ct).ConfigureAwait(false);
            lock (WriteLock)
            {
                OverlayStyles.Write(paths.OverlayFile, config);
            }

            var reloaded = await agent.SendAsync(AgentCommands.OverlayReload, null, ct).ConfigureAwait(false);
            return Results.Ok(new OverlayResponse(config, null, null, reloaded is null ? agent.UnavailableReason : reloaded.Ok ? null : reloaded.Error, OverlayStyles.Presets, OverlayStyles.Revision(paths.OverlayFile)));
        });

        overlay.MapPut(string.Empty, async (HttpRequest request, DataPaths paths, IAgentClient agent, IOptions<JsonOptions> json, PcDiag.Storage.Files.FileStore store, ILogger<OverlayResponse> logger, CancellationToken ct) =>
        {
            if (request.ContentLength is > MaxBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Layout too large", detail: $"At most {MaxBytes} bytes.");
            }

            OverlayConfig? config;
            try
            {
                using var body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct).ConfigureAwait(false);

                // Something that is not a layout (a whole answer of GET sent back, a misspelt name) is refused: read as a
                // layout it would be an empty one, saved over the real panels.
                if (body.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid layout", detail: "A layout is a JSON object.");
                }

                if (body.RootElement.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !LayoutNames.Contains(n)) is { } unknown)
                {
                    return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid layout", detail: $"\"{unknown}\" is not part of a layout.");
                }

                config = body.RootElement.Deserialize<OverlayConfig>(json.Value.SerializerOptions);
            }
            catch (JsonException ex)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid layout", detail: ex.Message);
            }

            if (config is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid layout", detail: "A layout is required.");
            }

            if (OverlayStyles.Validate(config) is { } problem)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid layout", detail: problem);
            }

            // The layout as it was, kept before it is replaced (at most every few minutes: the editor saves often).
            await KeepBackupAsync(store, paths, logger, always: false, ct).ConfigureAwait(false);
            lock (WriteLock)
            {
                OverlayStyles.Write(paths.OverlayFile, config);
                OverlayImageEndpoints.RemoveUnused(paths, config);
            }

            // The recorder also notices the file; asking makes the change show at once.
            var reloaded = await agent.SendAsync(AgentCommands.OverlayReload, null, ct).ConfigureAwait(false);
            return Results.Ok(new OverlayResponse(config, null, null, reloaded is null ? agent.UnavailableReason : reloaded.Ok ? null : reloaded.Error, OverlayStyles.Presets, OverlayStyles.Revision(paths.OverlayFile)));
        });

        overlay.MapPost("/identify", async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.OverlayIdentify, null, ct).ConfigureAwait(false);
            return response switch
            {
                null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
                { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The recorder cannot show readings on the screens", detail: response.Error),
                _ => Results.NoContent(),
            };
        });

        return app;
    }

    private static IResult DriversResult(IAgentClient agent, AgentResponse? response) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The screen driver could not be changed", detail: response.Error),
        _ => Results.Ok(response.ScreenDrivers ?? []),
    };

    private static IResult StateResult(IAgentClient agent, AgentResponse? response, DataPaths paths) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The recorder cannot show readings on the screens", detail: response.Error),
        { Overlay: { } state } => Results.Ok(state with { Revision = OverlayStyles.Revision(paths.OverlayFile) }),
        _ => Results.NoContent(),
    };
}

/// <summary>Starts (true) or ends (false) arranging the panels on the screens.</summary>
public sealed record ArrangeRequest(bool On);

/// <summary>Approves a screen driver package by its id and content (SHA-256), or withdraws it.</summary>
public sealed record DriverApproval(string Id, string Sha256, bool Approve);

/// <summary>The layout, and what the recorder says about the screens.</summary>
/// <param name="Config">The saved layout.</param>
/// <param name="State">The recorder's monitors and values (null when it is not reachable).</param>
/// <param name="FileProblem">Why the saved file was not used (a broken file).</param>
/// <param name="RecorderProblem">Why the recorder did not answer or apply it.</param>
/// <param name="Presets">The ready-made looks, by id (the editor offers these, so they are the recorder's own).</param>
/// <param name="Revision">The saved layout's revision (it changes whenever the layout does).</param>
public sealed record OverlayResponse(OverlayConfig Config, OverlayState? State, string? FileProblem, string? RecorderProblem, IReadOnlyDictionary<string, OverlayStyle>? Presets = null, string? Revision = null);
