using PcDiag.Core.Configuration;
using PcDiag.Core.Ipc;
using PcDiag.Hosting;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>Host and Agent status.</summary>
internal static class StatusEndpoints
{
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/status", async (DataPaths paths, TimeProvider time, IAgentClient agent, CancellationToken cancellationToken) =>
        {
            var response = await agent.SendAsync(AgentCommands.Status, null, cancellationToken).ConfigureAwait(false);
            return new StatusResponse(
                ProductInfo.Name,
                ProductInfo.Version,
                time.GetUtcNow(),
                paths.Root,
                new AgentConnection(response is not null, response is null ? agent.UnavailableReason : response.Error, response?.Status));
        });

        return app;
    }
}

/// <summary>Response of <c>GET /api/v1/status</c>.</summary>
/// <param name="Product">Product name.</param>
/// <param name="Version">Build version.</param>
/// <param name="ServerTime">Current server time (UTC).</param>
/// <param name="DataDirectory">Where diagnostic data is stored.</param>
/// <param name="Agent">Recorder Agent connection and status.</param>
public sealed record StatusResponse(string Product, string Version, DateTimeOffset ServerTime, string DataDirectory, AgentConnection Agent);

/// <summary>Recorder Agent connection.</summary>
/// <param name="Connected">Whether the Agent answered.</param>
/// <param name="Detail">Why it is not connected (or why the request failed).</param>
/// <param name="Status">The Agent's status when connected.</param>
public sealed record AgentConnection(bool Connected, string? Detail, AgentStatus? Status);
