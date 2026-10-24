using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Recording;
using PcDiag.Web.Agent;
using PcDiag.Web.Live;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Endpoints;

/// <summary>Live values, recorder control and capture.</summary>
internal static class ControlEndpoints
{
    public static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // One snapshot (for clients without EventSource, and for tests).
        api.MapGet("/live", async (IAgentClient agent, CancellationToken cancellationToken) =>
            LiveMessage.From(await agent.SendAsync(AgentCommands.Live, null, cancellationToken).ConfigureAwait(false), agent));

        // Server-Sent Events: one "live" event per second while the page is open.
        api.MapGet("/live/stream", (LiveBroadcaster live, CancellationToken cancellationToken) => live.HasCapacity
            ? Results.ServerSentEvents(live.SubscribeAsync(cancellationToken), eventType: "live")
            : Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many live views", detail: "Close some dashboard tabs and try again."));

        // The last minutes of the headline values, to fill the live charts when the page opens.
        api.MapGet("/live/trend", async (IAgentClient agent, int? minutes, CancellationToken cancellationToken) =>
        {
            var args = new AgentRequestArgs(TrendMinutes: Math.Clamp(minutes ?? 10, 1, 10));
            var response = await agent.SendAsync(AgentCommands.Trend, args, cancellationToken).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.Trend);
        });

        var recorder = api.MapGroup("/recorder");
        // Record everything (no body), or only a selection (a custom recording), which is remembered for next time.
        recorder.MapPost("/start", async (StartRecordingRequest? request, IAgentClient agent, SettingsStore settings, CancellationToken ct) =>
        {
            if (request?.Selection is { } items && (items.Count is 0 or > RecordingSelection.MaxItems))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid selection",
                    detail: $"A custom recording selects 1–{RecordingSelection.MaxItems} items.");
            }

            var response = await agent.SendAsync(AgentCommands.Start, new AgentRequestArgs(Selection: request?.Selection), ct).ConfigureAwait(false);
            if (Failure(response, agent) is { } failure)
            {
                return failure;
            }

            if (request?.Selection is { } selection)
            {
                var current = settings.Load();
                current.Recorder.CustomSelection = RecordingSelection.Of(selection);
                settings.Save(current);
            }

            return Results.Ok(response!.Status);
        });

        // What a custom recording can select on this PC (its devices and sensors).
        recorder.MapGet("/catalog", async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.Catalog, null, ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.Catalog);
        });
        recorder.MapPost("/stop", (IAgentClient agent, CancellationToken ct) => CommandAsync(agent, AgentCommands.Stop, null, ct));
        recorder.MapPost("/resume", (IAgentClient agent, CancellationToken ct) => CommandAsync(agent, AgentCommands.Resume, null, ct));
        recorder.MapPost("/pause", (PauseRequest? request, IAgentClient agent, CancellationToken ct) =>
            request?.Minutes is < 1 or > 1440
                ? Task.FromResult(Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid pause", detail: "A pause must be 1–1440 minutes, or null until resumed."))
                : CommandAsync(agent, AgentCommands.Pause, new AgentRequestArgs(PauseMinutes: request?.Minutes), ct));

        api.MapPost("/incidents/capture", async (CaptureRequest? request, IAgentClient agent, CancellationToken ct) =>
        {
            if (request?.Note is { Length: > 2000 })
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Note too long", detail: "A note can be at most 2000 characters.");
            }

            var response = await agent.SendAsync(AgentCommands.Capture, new AgentRequestArgs(IncidentType: request?.Type, Note: Clean(request?.Note)), ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(new CaptureResponse(response!.Capture!, response.Status!));
        });

        return app;
    }

    private static async Task<IResult> CommandAsync(IAgentClient agent, string command, AgentRequestArgs? args, CancellationToken cancellationToken)
    {
        var response = await agent.SendAsync(command, args, cancellationToken).ConfigureAwait(false);
        return Failure(response, agent) ?? Results.Ok(response!.Status);
    }

    private static IResult? Failure(AgentResponse? response, IAgentClient agent) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The recorder refused the command", detail: response.Error),
        _ => null,
    };

    private static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();
}

/// <summary>Body of <c>POST /api/v1/recorder/start</c>.</summary>
/// <param name="Selection">Item IDs of a custom recording; null records everything.</param>
public sealed record StartRecordingRequest(IReadOnlyList<string>? Selection);

/// <summary>Body of <c>POST /api/v1/recorder/pause</c>.</summary>
/// <param name="Minutes">Pause length (1–1440); null pauses until resumed.</param>
public sealed record PauseRequest(int? Minutes);

/// <summary>Body of <c>POST /api/v1/incidents/capture</c>.</summary>
/// <param name="Type">Quick type, if the user picked one.</param>
/// <param name="Note">Optional note (at most 2000 characters).</param>
public sealed record CaptureRequest(UserIncidentType? Type, string? Note);

/// <summary>Response of a capture.</summary>
/// <param name="Capture">The incident.</param>
/// <param name="Status">Recorder status afterwards.</param>
public sealed record CaptureResponse(AgentCaptureResult Capture, AgentStatus Status);
