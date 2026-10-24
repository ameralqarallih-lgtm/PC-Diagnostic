using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using PcDiag.Core.Reporting;

namespace PcDiag.ReportService;

/// <summary>
/// Receives problem reports from PC Diagnostic installations. Reports are kept as JSON files by day; the same
/// problem (fingerprint) from anywhere is tracked once. With an Azure DevOps project configured, each new problem
/// becomes a bug (title, cause, stack, log, environment) and repeats add a comment with the count and versions.
/// </summary>
public static class ReportServiceApp
{
    /// <summary>Largest accepted report.</summary>
    public const int MaxReportBytes = 512 * 1024;

    public static async Task Main(string[] args)
    {
        var app = Build(args);
        await app.RunAsync().ConfigureAwait(false);
    }

    /// <summary>The service (tests host it in memory).</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(sp => new ReportStore(sp.GetRequiredService<IConfiguration>()["Storage:Directory"] ?? Path.Combine(AppContext.BaseDirectory, "reports")));
        builder.Services.AddHttpClient<IWorkItemSink, AzureDevOpsSink>();
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // At most 60 reports an hour from one address (a looping installation cannot flood the service).
            options.AddPolicy("reports", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromHours(1) }));
        });
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapPost("/v1/reports", ReceiveAsync).RequireRateLimiting("reports");
        return app;
    }

    private static async Task<IResult> ReceiveAsync(HttpRequest request, ReportStore store, IWorkItemSink sink, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaxReportBytes)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Report too large");
        }

        ProblemReport? report;
        try
        {
            using var limited = new MemoryStream();
            await request.Body.CopyToAsync(limited, cancellationToken).ConfigureAwait(false);
            if (limited.Length > MaxReportBytes)
            {
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Report too large");
            }

            report = JsonSerializer.Deserialize<ProblemReport>(limited.ToArray(), ReportStore.Json);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a problem report");
        }

        if (report is null || !ProblemSpool.IsId(report.Id) || report.Summary is not { Length: > 0 and <= 300 } || report.Kind is not ("manual" or "automatic")
            || report.Product is null || report.Environment is null || report.Log is null || report.Context is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a problem report");
        }

        var (problem, isNew) = store.Add(report);
        string? reference = problem.Reference;
        try
        {
            if (isNew || problem.Reference is null)
            {
                reference = await sink.CreateAsync(report, cancellationToken).ConfigureAwait(false);
                if (reference is not null)
                {
                    store.SetReference(problem.Fingerprint, reference);
                }
            }
            else
            {
                await sink.AddRepeatAsync(problem, report, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            // The report is kept either way; the bug can be filed later.
            loggers.CreateLogger("Reports").LogWarning(ex, "Could not file the report in Azure DevOps");
        }

        return Results.Ok(new { id = report.Id, reference });
    }
}

/// <summary>What the service knows about one problem (all reports with its fingerprint).</summary>
public sealed record TrackedProblem(string Fingerprint, string Summary, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Reports, int Occurrences, IReadOnlyList<string> Versions, string? Reference);

/// <summary>Reports on disk ({directory}/{yyyy-MM-dd}/{id}.json) and the problems they belong to (problems.json).</summary>
public sealed class ReportStore(string directory)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    /// <summary>Stores a report; returns its problem and whether the problem is new.</summary>
    public (TrackedProblem Problem, bool IsNew) Add(ProblemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_gate)
        {
            var day = Path.Combine(directory, report.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(day);
            File.WriteAllText(Path.Combine(day, $"{report.Id}-{DateTime.UtcNow.Ticks}.json"), JsonSerializer.Serialize(report, Json));

            var problems = Load();
            var key = report.Kind == "manual" ? report.Id : report.Fingerprint;
            var isNew = !problems.TryGetValue(key, out var known);
            var problem = isNew
                ? new TrackedProblem(key, report.Summary, report.FirstSeen, report.At, 1, report.Occurrences, [report.Product.Version], null)
                : known! with
                {
                    LastSeen = report.At,
                    Reports = known.Reports + 1,
                    Occurrences = known.Occurrences + report.Occurrences,
                    Versions = known.Versions.Contains(report.Product.Version) ? known.Versions : [.. known.Versions, report.Product.Version],
                };
            problems[key] = problem;
            Save(problems);
            return (problem, isNew);
        }
    }

    /// <summary>Remembers the work item a problem was filed as.</summary>
    public void SetReference(string fingerprint, string reference)
    {
        lock (_gate)
        {
            var problems = Load();
            if (problems.TryGetValue(fingerprint, out var problem))
            {
                problems[fingerprint] = problem with { Reference = reference };
                Save(problems);
            }
        }
    }

    /// <summary>Every problem.</summary>
    public IReadOnlyCollection<TrackedProblem> Problems()
    {
        lock (_gate)
        {
            return Load().Values;
        }
    }

    private Dictionary<string, TrackedProblem> Load()
    {
        var path = Path.Combine(directory, "problems.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, TrackedProblem>>(File.ReadAllText(path), Json) ?? [] : [];
    }

    private void Save(Dictionary<string, TrackedProblem> problems)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "problems.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(problems, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>Where new problems are filed.</summary>
public interface IWorkItemSink
{
    /// <summary>Files a new problem; returns its reference (e.g. "Bug 1234"), or null when not configured.</summary>
    Task<string?> CreateAsync(ProblemReport report, CancellationToken cancellationToken);

    /// <summary>Notes that a known problem happened again.</summary>
    Task AddRepeatAsync(TrackedProblem problem, ProblemReport report, CancellationToken cancellationToken);
}

/// <summary>
/// Files problems as Azure DevOps work items (AzureDevOps:Url, :Project, :Pat, optional :AreaPath and :WorkItemType,
/// default Bug). Does nothing when the URL or token is missing. The token comes from configuration (an environment
/// variable or a secret), never from a report.
/// </summary>
public sealed class AzureDevOpsSink(HttpClient http, IConfiguration configuration) : IWorkItemSink
{
    private string? Url => configuration["AzureDevOps:Url"]?.TrimEnd('/');

    private string? Project => configuration["AzureDevOps:Project"];

    private string? Pat => configuration["AzureDevOps:Pat"];

    private bool Configured => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(Project) && !string.IsNullOrEmpty(Pat);

    /// <inheritdoc />
    public async Task<string?> CreateAsync(ProblemReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Configured)
        {
            return null;
        }

        var type = configuration["AzureDevOps:WorkItemType"] ?? "Bug";
        var fields = new List<object>
        {
            new { op = "add", path = "/fields/System.Title", value = Title(report) },
            new { op = "add", path = "/fields/Microsoft.VSTS.TCM.ReproSteps", value = ReportHtml.Render(report) },
            new { op = "add", path = "/fields/System.Tags", value = $"problem-report; {report.Kind}; {report.Product.App}; v{report.Product.Version}" },
        };
        if (configuration["AzureDevOps:AreaPath"] is { Length: > 0 } area)
        {
            fields.Add(new { op = "add", path = "/fields/System.AreaPath", value = area });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/{Uri.EscapeDataString(Project!)}/_apis/wit/workitems/${Uri.EscapeDataString(type)}?api-version=7.1")
        {
            Content = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8, "application/json-patch+json"),
        };
        Authorize(request);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        return $"{type} {body.RootElement.GetProperty("id").GetInt32()}";
    }

    /// <inheritdoc />
    public async Task AddRepeatAsync(TrackedProblem problem, ProblemReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(report);
        if (!Configured || problem.Reference?.Split(' ').LastOrDefault() is not { } id || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return;
        }

        var text = $"Reported again ({problem.Reports} reports, {problem.Occurrences} occurrences). Versions: {string.Join(", ", problem.Versions)}. Latest: {WebUtility.HtmlEncode(report.Environment.Os)}, {report.Product.App} {report.Product.Version}.";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Url}/{Uri.EscapeDataString(Project!)}/_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private void Authorize(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + Pat)));

    private static string Title(ProblemReport report)
    {
        var title = $"[{(report.Kind == "manual" ? "User report" : "Auto")}] {report.Summary}";
        return title.Length <= 250 ? title : title[..247] + "...";
    }
}

/// <summary>A report as HTML for a work item (every value encoded).</summary>
public static class ReportHtml
{
    public static string Render(ProblemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"<p><b>{E(report.Summary)}</b></p>");
        if (report.Description is { } description)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>{E(description).Replace("\n", "<br/>", StringComparison.Ordinal)}</p>");
        }

        html.Append(CultureInfo.InvariantCulture, $"<p>{E(report.Kind)} report from <b>{E(report.Product.App)}</b> {E(report.Product.Version)}, component {E(report.Component)}; seen {report.Occurrences} time(s) since {report.FirstSeen:u}; fingerprint {E(report.Fingerprint)}.</p>");
        for (var cause = report.Cause; cause is not null; cause = cause.Inner)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p><b>{E(cause.Type)}</b>: {E(cause.Message)}</p>");
            if (cause.Stack is { Length: > 0 } stack)
            {
                html.Append(CultureInfo.InvariantCulture, $"<pre>{E(stack)}</pre>");
            }
        }

        html.Append(CultureInfo.InvariantCulture, $"<p><b>Environment</b>: {E(report.Environment.Os)}, {E(report.Environment.Architecture)}, {report.Environment.Processors} logical processors, {report.Environment.MemoryBytes / (1024 * 1024 * 1024)} GB, {E(report.Environment.Runtime)}, {E(report.Environment.Language)}</p>");
        if (report.Environment.Hardware.Count > 0)
        {
            html.Append("<ul>").AppendJoin(string.Empty, report.Environment.Hardware.Select(h => $"<li>{E(h)}</li>")).Append("</ul>");
        }

        if (report.Context.Count > 0)
        {
            html.Append("<p><b>Context</b></p><ul>").AppendJoin(string.Empty, report.Context.Select(kv => $"<li>{E(kv.Key)}: {E(kv.Value)}</li>")).Append("</ul>");
        }

        if (report.Log.Count > 0)
        {
            html.Append("<p><b>Log</b></p><pre>").AppendJoin('\n', report.Log.Select(l => E($"{l.At:HH:mm:ss.fff} {l.Level,-11} {l.Source}: {l.Message}"))).Append("</pre>");
        }

        if (report.Contact is { } contact)
        {
            html.Append(CultureInfo.InvariantCulture, $"<p>Contact: {E(contact)}</p>");
        }

        return html.ToString();
    }
}
