using PcDiag.Core.Configuration;
using PcDiag.Core.Devices;
using PcDiag.Core.Ipc;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Expected hardware: the setup the user remembers (expected-hardware.json, written here) and the recorder's check
/// of the hardware against it (what is missing, what was added).
/// </summary>
internal static class HardwareEndpoints
{
    private static readonly Lock WriteLock = new();

    public static IEndpointRouteBuilder MapHardwareEndpoints(this IEndpointRouteBuilder app)
    {
        var hardware = app.MapGroup("/api/v1/hardware");

        // What the recorder cost in CPU over the last minute, by part (null until it has run a minute).
        app.MapGet("/api/v1/recorder/cost", async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.RecorderCost, null, ct).ConfigureAwait(false);
            return Results.Ok(new RecorderCostResponse(response is { Ok: true } ? response.Cost : null, response is null ? agent.UnavailableReason : response.Ok ? null : response.Error));
        });

        hardware.MapGet(string.Empty, async (DataPaths paths, IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.HardwareState, null, ct).ConfigureAwait(false);
            return Results.Ok(new HardwareResponse(
                response is { Ok: true } ? response.Hardware : null,
                HardwareCheck.Read(paths.ExpectedHardwareFile),
                response is null ? agent.UnavailableReason : response.Ok ? null : response.Error));
        });

        // Remember a setup: the items the user chose from what is there now.
        hardware.MapPut("/expected", async (ExpectedRequest? request, DataPaths paths, TimeProvider time, IAgentClient agent, CancellationToken ct) =>
        {
            if (request?.Items is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a setup", detail: "The items are required.");
            }

            var expected = new ExpectedHardware(1, time.GetUtcNow(), request.Items, request.IncidentOnMissing ?? false);
            if (HardwareCheck.Validate(expected) is { } problem)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a setup", detail: problem);
            }

            lock (WriteLock)
            {
                HardwareCheck.Write(paths.ExpectedHardwareFile, expected);
            }

            var response = await agent.SendAsync(AgentCommands.HardwareCheck, null, ct).ConfigureAwait(false);
            return Results.Ok(new HardwareResponse(response?.Hardware, expected, response is null ? agent.UnavailableReason : response.Ok ? null : response.Error));
        });

        hardware.MapDelete("/expected", async (DataPaths paths, IAgentClient agent, CancellationToken ct) =>
        {
            lock (WriteLock)
            {
                if (File.Exists(paths.ExpectedHardwareFile))
                {
                    File.Delete(paths.ExpectedHardwareFile);
                }
            }

            await agent.SendAsync(AgentCommands.HardwareCheck, null, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        hardware.MapPost("/check", async (DataPaths paths, IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.HardwareCheck, null, ct).ConfigureAwait(false);
            return response switch
            {
                null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: $"{agent.UnavailableReason} The recorder checks the hardware."),
                { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Not checked", detail: response.Error),
                _ => Results.Ok(new HardwareResponse(response.Hardware, HardwareCheck.Read(paths.ExpectedHardwareFile), null)),
            };
        });

        return app;
    }
}

/// <summary>The hardware check and the expected setup.</summary>
/// <param name="Status">The recorder's newest check (null when it is not reachable).</param>
/// <param name="Expected">The remembered setup (null: none).</param>
/// <param name="RecorderProblem">Why the recorder did not answer.</param>
public sealed record HardwareResponse(HardwareStatus? Status, ExpectedHardware? Expected, string? RecorderProblem);

/// <summary>Body of <c>PUT /api/v1/hardware/expected</c>.</summary>
public sealed record ExpectedRequest(IReadOnlyList<HardwareItem>? Items, bool? IncidentOnMissing);

/// <summary>The recorder's CPU over the last minute, by part, or why it is not known.</summary>
public sealed record RecorderCostResponse(PcDiag.Core.Diagnostics.RecorderCost? Cost, string? Problem);
