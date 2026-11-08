using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Stability;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>The Stability tab: crashes from Windows' log (each with PC Diagnostic's incident, when there is one), dumps and memory tests.</summary>
internal static class StabilityEndpoints
{
    /// <summary>How far an incident's moment may be from a crash and still be its incident.</summary>
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan After = TimeSpan.FromMinutes(30);

    public static IEndpointRouteBuilder MapStabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var stability = app.MapGroup("/api/v1/stability");
        stability.MapGet(string.Empty, (int? days, IAgentClient agent, IncidentIndexReader incidents, CancellationToken ct) =>
            AskAsync(AgentCommands.Stability, new AgentRequestArgs(Value: Math.Clamp(days ?? 30, 1, 365).ToString(System.Globalization.CultureInfo.InvariantCulture)), agent, incidents, ct));
        stability.MapPost("/dumps", (StabilityDumpsRequest request, IAgentClient agent, IncidentIndexReader incidents, CancellationToken ct) =>
            AskAsync(AgentCommands.StabilityDumps, new AgentRequestArgs(Value: request.Keep.ToString(System.Globalization.CultureInfo.InvariantCulture)), agent, incidents, ct));
        stability.MapPost("/memory-test", (IAgentClient agent, IncidentIndexReader incidents, CancellationToken ct) =>
            AskAsync(AgentCommands.StabilityMemoryTest, null, agent, incidents, ct));
        return app;
    }

    private static async Task<IResult> AskAsync(string command, AgentRequestArgs? args, IAgentClient agent, IncidentIndexReader incidents, CancellationToken ct)
    {
        var response = await agent.SendAsync(command, args, ct).ConfigureAwait(false);
        if (response is not { Ok: true, Stability: { } report })
        {
            return Results.Ok(new StabilityResponse(null, response is null ? agent.UnavailableReason ?? "The recorder is not running." : response.Error ?? "The recorder could not read it."));
        }

        var page = await incidents.ListAsync(new IncidentQuery(IncidentIndexReader.MaxPageSize, 0, Source: IncidentSource.BootRecovery, From: report.Since.ToUnixTimeMilliseconds()), ct).ConfigureAwait(false);
        return Results.Ok(new StabilityResponse(report with { Crashes = Link(report.Crashes, page.Items) }, null));
    }

    /// <summary>Each crash with the crash incident closest to it (the recorder dates one at its last heartbeat, or when Windows came back). Exposed for tests.</summary>
    internal static List<CrashRecord> Link(IReadOnlyList<CrashRecord> crashes, IReadOnlyList<IncidentSummary> incidents)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var linked = new List<CrashRecord>();
        foreach (var crash in crashes)
        {
            var from = (crash.WentDown ?? crash.Restarted) - Before;
            var to = crash.Restarted + After;
            var incident = incidents.Where(i => !used.Contains(i.Key) && i.CenterAt >= from && i.CenterAt <= to)
                .OrderBy(i => (i.CenterAt - (crash.WentDown ?? crash.Restarted)).Duration()).FirstOrDefault();
            if (incident is not null)
            {
                used.Add(incident.Key);
            }

            linked.Add(crash with { IncidentKey = incident?.Key });
        }

        return linked;
    }
}

/// <summary>Body of <c>POST /api/v1/stability/dumps</c>: how many small crash dumps Windows should keep.</summary>
public sealed record StabilityDumpsRequest(int Keep);

/// <summary>Answer of the stability endpoints: the report, or why there is none.</summary>
public sealed record StabilityResponse(StabilityReport? Report, string? Problem);
