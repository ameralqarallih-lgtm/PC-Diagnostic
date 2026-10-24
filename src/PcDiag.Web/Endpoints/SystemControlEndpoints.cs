using PcDiag.Core.Control;
using PcDiag.Core.Ipc;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Processes, services and the tune-up, carried out by the elevated recorder. Changes are accepted only on this PC
/// (paired devices can look, see <see cref="Security.LanGuardMiddleware"/>), and the recorder checks each one.
/// </summary>
internal static class SystemControlEndpoints
{
    public static IEndpointRouteBuilder MapSystemControlEndpoints(this IEndpointRouteBuilder app)
    {
        var control = app.MapGroup("/api/v1/control");

        control.MapGet("/processes", async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.ControlProcesses, null, ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.Processes ?? []);
        });

        control.MapGet("/services", async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.ControlServices, null, ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.Services ?? []);
        });

        control.MapPost("/processes/{pid:int}/end", (int pid, EndProcessRequest request, IAgentClient agent, CancellationToken ct) =>
            ActionAsync(agent, AgentCommands.ProcessAction, new AgentRequestArgs(Pid: pid, ProcessStart: request.Start, ControlAction: request.Tree == true ? "end-tree" : "end", Confirmed: request.Confirmed, Safe: request.Safe), ct));

        control.MapPost("/processes/{pid:int}/priority", (int pid, PriorityRequest request, IAgentClient agent, CancellationToken ct) =>
            ActionAsync(agent, AgentCommands.ProcessAction, new AgentRequestArgs(Pid: pid, ProcessStart: request.Start, ControlAction: "priority", Priority: request.Priority), ct));

        control.MapPost("/services/{name}/{action}", (string name, string action, ServiceActionRequest? request, IAgentClient agent, CancellationToken ct) =>
            ActionAsync(agent, AgentCommands.ServiceAction, new AgentRequestArgs(ServiceName: name, ControlAction: action, StartType: request?.StartType, Confirmed: request?.Confirmed, Safe: request?.Safe), ct));

        var tuneUp = app.MapGroup("/api/v1/tuneup");
        tuneUp.MapGet(string.Empty, async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.TuneUpScan, null, ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.TuneUp);
        });

        tuneUp.MapGet("/cleanup/{id}/files", async (string id, int? root, string? path, IAgentClient agent, CancellationToken ct) =>
        {
            if (id.Length > 64 || (path?.Length ?? 0) > 1024)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Too long");
            }

            var response = await agent.SendAsync(AgentCommands.CleanupFiles,
                new AgentRequestArgs(Value: id, Items: [(root ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture), path ?? ""]), ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.CleanupFiles);
        });

        tuneUp.MapGet("/plan", async (IAgentClient agent, CancellationToken ct) =>
        {
            var response = await agent.SendAsync(AgentCommands.TuneUpPlan, null, ct).ConfigureAwait(false);
            return Failure(response, agent) ?? Results.Ok(response!.Plan);
        });

        tuneUp.MapPost("/{action}", (string action, TuneUpRequest? request, IAgentClient agent, CancellationToken ct) =>
            !TuneUpActions.All.Contains(action)
                ? Task.FromResult(Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown tune-up action"))
                : ActionAsync(agent, AgentCommands.TuneUpApply, new AgentRequestArgs(ControlAction: action, Items: request?.Items, Value: request?.Value, Confirmed: request?.Confirmed, Safe: request?.Safe), ct));

        return app;
    }

    /// <summary>An action's outcome, done or not (the result says what happened); 400 for a request the recorder refused to read.</summary>
    private static async Task<IResult> ActionAsync(IAgentClient agent, string command, AgentRequestArgs args, CancellationToken ct)
    {
        var response = await agent.SendAsync(command, args, ct).ConfigureAwait(false);
        if (response is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason);
        }

        return response.Result is { } result
            ? Results.Ok(result)
            : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The recorder refused the request", detail: response.Error);
    }

    private static IResult? Failure(AgentResponse? response, IAgentClient agent) => response switch
    {
        null => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Recorder not reachable", detail: agent.UnavailableReason),
        { Ok: false } => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "The recorder refused the command", detail: response.Error),
        _ => null,
    };
}

/// <summary>Body of <c>POST /api/v1/control/processes/{pid}/end</c>.</summary>
/// <param name="Start">The process's start time (UTC ticks, from the list).</param>
/// <param name="Tree">Also end the processes it started.</param>
/// <param name="Confirmed">The user confirmed ending a part of Windows.</param>
/// <param name="Safe">Safe mode: refuse when the process is in front, has a window or is working.</param>
public sealed record EndProcessRequest(long Start, bool? Tree, bool? Confirmed, bool? Safe = null);

/// <summary>Body of <c>POST /api/v1/control/processes/{pid}/priority</c>.</summary>
public sealed record PriorityRequest(long Start, string Priority);

/// <summary>Body of <c>POST /api/v1/control/services/{name}/{action}</c>.</summary>
/// <param name="StartType">For start-type: Automatic, AutomaticDelayed, Manual or Disabled.</param>
/// <param name="Confirmed">The user confirmed changing a Windows service.</param>
/// <param name="Safe">Safe mode: refuse to stop a service whose process is working.</param>
public sealed record ServiceActionRequest(string? StartType, bool? Confirmed, bool? Safe = null);

/// <summary>Body of <c>POST /api/v1/tuneup/{action}</c>.</summary>
/// <param name="Safe">Safe mode: leave anything other programs are using now.</param>
public sealed record TuneUpRequest(IReadOnlyList<string>? Items, string? Value, bool? Confirmed, bool? Safe = null);
