using System.Globalization;
using PcDiag.Core.Configuration;
using PcDiag.Core.History;
using PcDiag.Core.Incidents;
using PcDiag.Core.Ipc;
using PcDiag.Core.Quality;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Quality;
using PcDiag.Storage.Sqlite.Incidents;
using PcDiag.Storage.Sqlite.Reading;
using PcDiag.Web.Agent;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// The quality pages: game sessions with their frame drops explained (Gaming), the connection hop by hop (Network), and
/// driver latency with its measurements (System › Latency). All read the quality history, kept also while the
/// recorder only watches, and the recorded seconds where there are some.
/// </summary>
internal static class QualityEndpoints
{
    /// <summary>Most seconds of a session sent for its chart (longer sessions are charted by minute).</summary>
    public const int MaxChartSeconds = 14_400;

    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/gaming/sessions", SessionsAsync);
        api.MapGet("/gaming/sessions/{id:long}", SessionAsync);
        api.MapPost("/gaming/classify", Classify);
        api.MapGet("/connection", ConnectionAsync);
        api.MapGet("/latency", LatencyAsync);
        api.MapPost("/latency/measure", MeasureAsync);
        return app;
    }

    private static async Task<IResult> SessionsAsync(int? days, QualityReader quality, DataPaths paths, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var from = now - TimeSpan.FromDays(Math.Clamp(days ?? 30, 1, 400));
        var sessions = await quality.SessionsAsync(paths.TelemetryDatabase, from.ToUnixTimeMilliseconds(), now.ToUnixTimeMilliseconds(), ct).ConfigureAwait(false);
        return Results.Ok(new GameSessionsResponse(sessions, now));
    }

    private static async Task<IResult> SessionAsync(long id, QualityReader quality, IncidentIndexReader incidents, DataPaths paths, CancellationToken ct)
    {
        var database = paths.TelemetryDatabase;
        if (await quality.SessionAsync(database, id, ct).ConfigureAwait(false) is not { } session)
        {
            return Results.NotFound();
        }

        var from = session.Start.ToUnixTimeMilliseconds();
        var to = session.End.ToUnixTimeMilliseconds();
        var seconds = await quality.FrameSecondsAsync(database, from, to, ct).ConfigureAwait(false);
        var minutes = (await quality.MinutesAsync(database, from, to, ct).ConfigureAwait(false))
            .Where(m => m.Game is null || string.Equals(m.Game, session.Game, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Drops can be explained only where the seconds were recorded (the other measurements are stored with them).
        var drops = new List<FrameDrop>();
        if (seconds.Count > 0)
        {
            var measurements = await quality.DriverLatencyAsync(database, from - 300_000, to + 300_000, ct).ConfigureAwait(false);
            foreach (var (dropFrom, dropTo) in FrameDrops.Find(seconds))
            {
                var start = dropFrom.ToUnixTimeMilliseconds();
                var end = dropTo.ToUnixTimeMilliseconds();
                var tracks = await quality.TracksAsync(database, start - 65_000, end + 5_000, ct).ConfigureAwait(false);
                var processes = await quality.ProcessActivityAsync(database, start - 60_000, start - 1_000, end + 1_000, ct).ConfigureAwait(false);
                drops.Add(FrameDrops.Explain(dropFrom, dropTo, seconds, tracks, processes, session.Game, measurements, Clock));
            }
        }

        var during = await incidents.ListAsync(new IncidentQuery(50, 0, From: from - 60_000, To: to + 60_000), ct).ConfigureAwait(false);
        var patterns = drops.SelectMany(d => d.Findings.Where(f => f.Severity >= 1).Select(f => f.Kind).Distinct())
            .GroupBy(k => k).Select(g => new DropPattern(g.Key, g.Count())).OrderByDescending(p => p.Drops).ToList();
        var chart = seconds.Count <= MaxChartSeconds ? seconds : [];
        return Results.Ok(new GameSessionResponse(session, chart, minutes, drops, patterns, during.Items.Select(i => new SessionIncident(i.Key, i.Title, i.CenterAt)).ToList(),
            SessionDiagnosis.Analyze(session)));
    }

    /// <summary>
    /// The user says a program is a game (counted whenever it is in front, also windowed), is not (never counted), or
    /// leaves it to the recognizer again (<see cref="ClassifyRequest.IsGame"/> null).
    /// </summary>
    private static IResult Classify(ClassifyRequest request, Settings.SettingsStore store)
    {
        var game = request.Game?.Trim();
        if (string.IsNullOrEmpty(game) || game.Length > 128 || game.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "A program's name is required");
        }

        var settings = store.Load();
        var recorder = settings.Recorder;
        recorder.Games.RemoveAll(g => string.Equals(g, game, StringComparison.OrdinalIgnoreCase));
        recorder.NotGames.RemoveAll(g => string.Equals(g, game, StringComparison.OrdinalIgnoreCase));
        if (request.IsGame is { } isGame)
        {
            (isGame ? recorder.Games : recorder.NotGames).Add(game);
        }

        var problems = store.Save(settings);
        return problems.Count == 0
            ? Results.Ok(new ClassifyResponse(recorder.Games, recorder.NotGames))
            : Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = [.. problems] }, title: "The setting could not be saved");
    }

    private static async Task<IResult> ConnectionAsync(long? from, long? to, QualityReader quality, DataPaths paths, TimeProvider time, CancellationToken ct)
    {
        var (start, end) = Range(from, to, time);
        var minutes = await quality.MinutesAsync(paths.TelemetryDatabase, start, end, ct).ConfigureAwait(false);
        var programs = await quality.NetworkProgramsAsync(paths.TelemetryDatabase, start, end, 10, ct).ConfigureAwait(false);
        var report = ConnectionDiagnosis.Analyze(minutes, programs, Clock);
        var chart = minutes.Where(m => m.Internet.Loss is not null || m.Router.Loss is not null)
            .Select(m => new ConnectionMinute(m.At, m.Router, m.Provider, m.Internet, m.InternetJitter, m.DnsMs, m.DnsFailed, m.WifiMin, m.NetBps))
            .ToList();
        return Results.Ok(new ConnectionResponse(start, end, report, chart, programs));
    }

    private static async Task<IResult> LatencyAsync(long? from, long? to, QualityReader quality, DataPaths paths, IAgentClient agent, TimeProvider time, CancellationToken ct)
    {
        var (start, end) = Range(from, to, time);
        var minutes = await quality.MinutesAsync(paths.TelemetryDatabase, start, end, ct).ConfigureAwait(false);
        var measurements = await quality.DriverLatencyAsync(paths.TelemetryDatabase, start, end, ct).ConfigureAwait(false);
        var findings = LatencyDiagnosis.Analyze(minutes, measurements, Clock);
        var status = await StatusAsync(agent, AgentCommands.DriverLatency, ct).ConfigureAwait(false);
        return Results.Ok(new LatencyResponse(
            start,
            end,
            minutes.Where(m => m.DpcCoreMax is not null || m.IsrCoreMax is not null).Select(m => new LatencyMinute(m.At, m.DpcCoreMax, m.IsrCoreMax, m.DpcAvg)).ToList(),
            measurements.Select(Describe).Reverse().ToList(),
            findings,
            status.Status,
            status.Problem));
    }

    private static async Task<IResult> MeasureAsync(IAgentClient agent, CancellationToken ct)
    {
        var status = await StatusAsync(agent, AgentCommands.DriverLatencyMeasure, ct).ConfigureAwait(false);
        return Results.Ok(new MeasureResponse(status.Status, status.Problem));
    }

    private static async Task<(DriverLatencyStatus? Status, string? Problem)> StatusAsync(IAgentClient agent, string command, CancellationToken ct)
    {
        var response = await agent.SendAsync(command, null, ct).ConfigureAwait(false);
        return response switch
        {
            null => (null, agent.UnavailableReason ?? "The recorder is not running."),
            { Ok: true, DriverLatency: { } status } => (status, status.Problem),
            _ => (null, response.Error ?? "The recorder could not answer."),
        };
    }

    /// <summary>A measurement with what each driver is.</summary>
    internal static MeasurementView Describe(DriverLatencyMeasurement m) => new(m.At, m.From, m.Seconds, m.Reason, m.Problem,
        m.Drivers.Select(d => LatencyDiagnosis.Describe(d.Driver) is { } kind ? new DriverView(d, kind.What, kind.Try) : new DriverView(d, null, null)).ToList());

    private static (long From, long To) Range(long? from, long? to, TimeProvider time)
    {
        var end = to ?? time.GetUtcNow().ToUnixTimeMilliseconds();
        var start = from ?? end - (long)TimeSpan.FromDays(1).TotalMilliseconds;
        return (Math.Min(start, end - 60_000), end);
    }

    private static string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, TimeZoneInfo.Local).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>The game sessions of a period, newest first.</summary>
public sealed record GameSessionsResponse(IReadOnlyList<GameSession> Sessions, DateTimeOffset Now);

/// <summary>A game session: its seconds (when recorded) or minutes for the chart, its drops explained, its incidents and what the whole session shows.</summary>
public sealed record GameSessionResponse(GameSession Session, IReadOnlyList<FrameSecond> Seconds, IReadOnlyList<QualityMinute> Minutes, IReadOnlyList<FrameDrop> Drops,
    IReadOnlyList<DropPattern> Patterns, IReadOnlyList<SessionIncident> Incidents, IReadOnlyList<QualityFinding> Findings);

/// <summary>Body of <c>POST /api/v1/gaming/classify</c>: a program's name and whether it is a game (null: let the recognizer decide again).</summary>
public sealed record ClassifyRequest(string? Game, bool? IsGame);

/// <summary>The programs the user counts as games and as not games.</summary>
public sealed record ClassifyResponse(IReadOnlyList<string> Games, IReadOnlyList<string> NotGames);

/// <summary>How many of a session's drops had a finding of a kind ("gpu", "cpu", "driver"...).</summary>
public sealed record DropPattern(string Kind, int Drops);

/// <summary>An incident during a session.</summary>
public sealed record SessionIncident(string Key, string Title, DateTimeOffset At);

/// <summary>The connection over a period: the figures and findings, the minutes for the chart, the programs that used the network most.</summary>
public sealed record ConnectionResponse(long From, long To, ConnectionReport Report, IReadOnlyList<ConnectionMinute> Minutes, IReadOnlyList<NetworkProgram> Programs);

/// <summary>A minute of the connection.</summary>
public sealed record ConnectionMinute(DateTimeOffset At, HopMinute Router, HopMinute Provider, HopMinute Internet, double? JitterMs, double? DnsMs, int DnsFailed, double? WifiMin,
    double? NetBps);

/// <summary>Driver latency over a period: the busiest processor's driver time each minute, the measurements (newest first) and findings.</summary>
public sealed record LatencyResponse(long From, long To, IReadOnlyList<LatencyMinute> Minutes, IReadOnlyList<MeasurementView> Measurements, IReadOnlyList<QualityFinding> Findings,
    DriverLatencyStatus? Status, string? Problem);

/// <summary>A minute of driver time.</summary>
public sealed record LatencyMinute(DateTimeOffset At, double? DpcCoreMax, double? IsrCoreMax, double? DpcAvg);

/// <summary>A measurement as the page shows it.</summary>
public sealed record MeasurementView(DateTimeOffset At, DateTimeOffset From, double Seconds, string Reason, string? Problem, IReadOnlyList<DriverView> Drivers);

/// <summary>A driver of a measurement, with what it is and what could help.</summary>
public sealed record DriverView(DriverLatency Latency, string? What, string? Try);

/// <summary>Answer of <c>POST /api/v1/latency/measure</c>.</summary>
public sealed record MeasureResponse(DriverLatencyStatus? Status, string? Problem);
