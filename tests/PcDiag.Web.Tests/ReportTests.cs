using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PcDiag.Core.Reporting;
using PcDiag.ReportService;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Reporting;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

/// <summary>
/// Problem reports from the dashboard to the makers' report service, end to end: the service runs in memory and
/// files problems with a recording sink instead of Azure DevOps.
/// </summary>
public sealed class ReportTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly string _serviceDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _sink = new();
    private WebApplication _service = null!;
    private WebApplicationFactory<Program> _factory = null!;

    public async Task InitializeAsync()
    {
        _service = ReportServiceApp.Build([$"--Storage:Directory={_serviceDirectory}"], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<IWorkItemSink>(_sink);
        });
        await _service.StartAsync();
        var handler = _service.GetTestServer().CreateHandler();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);
            builder.UseSetting("Reporting:Endpoint", "http://localhost/");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentClient>(new FakeAgentClient());
                services.AddHttpClient(ProblemReportService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _service.DisposeAsync();
        new SqliteConnectionFactory().ReleasePooledConnectionsUnder(_dataDirectory);
        new SqliteConnectionFactory().ReleasePooledConnectionsUnder(_serviceDirectory);
        foreach (var folder in new[] { _dataDirectory, _serviceDirectory })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Logs may still be flushing.
            }
        }
    }

    [Fact]
    public async Task A_report_the_user_writes_is_previewed_then_sent_and_filed()
    {
        using var client = await SignedInClientAsync();
        var request = new { summary = "The History chart stays empty", description = $@"Logs in C:\Users\{Environment.UserName}\AppData", includeLog = true, includeSystem = true, page = "/history" };

        var preview = await (await PostAsync(client, "/api/v1/reports/preview", request)).Content.ReadFromJsonAsync<ProblemReport>(Json);
        preview!.Kind.ShouldBe("manual");
        preview.Summary.ShouldBe("The History chart stays empty");
        preview.Context["page"].ShouldBe("/history");
        preview.Product.App.ShouldBe("web");
        (await client.GetFromJsonAsync<ReportsOverview>("/api/v1/reports", Json))!.Items.ShouldBeEmpty(); // a preview is not kept

        var sent = await PostAsync(client, "/api/v1/reports", request);
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("outcome").GetProperty("sent").GetBoolean().ShouldBeTrue();
        body.RootElement.GetProperty("outcome").GetProperty("reference").GetString().ShouldBe("Bug 1");

        var overview = await client.GetFromJsonAsync<ReportsOverview>("/api/v1/reports", Json);
        overview!.ServiceConfigured.ShouldBeTrue();
        overview.Items.ShouldHaveSingleItem().Status.ShouldBe("sent");
        _sink.Created.ShouldHaveSingleItem().Summary.ShouldBe("The History chart stays empty");
    }

    [Fact]
    public async Task Errors_are_kept_on_the_PC_until_the_user_agrees_to_send_them()
    {
        using var client = await SignedInClientAsync();
        (await PostAsync(client, "/api/v1/reports/client-error", new { message = "Cannot read properties of undefined (reading 'value')", stack = "at Gauge (gauge.js:10)", page = "/", component = "GaugeGrid" }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var reports = _factory.Services.GetRequiredService<ProblemReportService>();
        (await reports.SendDueAsync(CancellationToken.None)).ShouldBe(0); // no consent yet
        var local = (await client.GetFromJsonAsync<ReportsOverview>("/api/v1/reports", Json))!.Items.ShouldHaveSingleItem();
        local.Status.ShouldBe("local");
        local.Component.ShouldBe("ui/GaugeGrid");

        var consent = new HttpRequestMessage(HttpMethod.Put, "/api/v1/reports/consent") { Content = JsonContent.Create(new { automatic = true }) };
        consent.Headers.Add("Origin", "http://localhost");
        (await client.SendAsync(consent)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await reports.SendDueAsync(CancellationToken.None)).ShouldBe(1);
        var overview = await client.GetFromJsonAsync<ReportsOverview>("/api/v1/reports", Json);
        overview!.Automatic.ShouldBeTrue();
        overview.AskedAt.ShouldNotBeNull();
        overview.Items.ShouldHaveSingleItem().Status.ShouldBe("sent");
    }

    [Fact]
    public async Task Errors_logged_by_the_dashboard_become_reports_with_their_log_lines()
    {
        using var client = await SignedInClientAsync();
        var logger = _factory.Services.GetRequiredService<ILogger<ReportTests>>();
        logger.LogInformation("Opened the History page");
        logger.LogError(new InvalidOperationException("The chart buffer overflowed"), "Rendering failed");

        // (Other errors this host logs, such as a port another test host holds, are reports too.)
        var items = (await client.GetFromJsonAsync<ReportsOverview>("/api/v1/reports", Json))!.Items;
        items.ShouldContain(i => i.Summary.Contains("The chart buffer overflowed", StringComparison.Ordinal), string.Join(" | ", items.Select(i => $"{i.Summary} x{i.Occurrences}")));
        var item = items.Single(i => i.Summary.Contains("The chart buffer overflowed", StringComparison.Ordinal));
        item.Summary.ShouldContain("InvalidOperationException: The chart buffer overflowed");
        var report = await client.GetFromJsonAsync<ProblemReport>($"/api/v1/reports/{item.Id}", Json);
        report!.Cause!.Type.ShouldBe("System.InvalidOperationException");
        report.Log.ShouldContain(l => l.Message == "Opened the History page");
        report.Product.App.ShouldBe("web");
    }

    [Fact]
    public async Task The_same_problem_reported_again_adds_to_its_work_item()
    {
        var http = _service.GetTestServer().CreateClient();
        var report = new ProblemReport("00aa11bb22cc33dd", "automatic", DateTimeOffset.UtcNow, "agent/GpuCollector", "agent/GpuCollector: IOException: it broke", null, null,
            new ProblemCause("System.IO.IOException", "it broke", null, null, null), new ProductFacts("PC Diagnostic", "1.2", "agent", true, "0123456789abcdef0123456789abcdef"),
            ProblemFacts.Environment(), [], new Dictionary<string, string>(), "00aa11bb22cc33dd", 3, DateTimeOffset.UtcNow);

        (await http.PostAsJsonAsync("/v1/reports", report)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PostAsJsonAsync("/v1/reports", report with { Product = report.Product with { Version = "1.3" } })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.PostAsJsonAsync("/v1/reports", new { id = "../../x", summary = "no" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        _sink.Created.ShouldHaveSingleItem();
        var repeat = _sink.Repeats.ShouldHaveSingleItem();
        (repeat.Reports, repeat.Occurrences).ShouldBe((2, 6));
        repeat.Versions.ShouldBe(["1.2", "1.3"]);
        ReportHtml.Render(report).ShouldContain("System.IO.IOException");
    }

    [Fact]
    public async Task Writing_a_report_needs_a_line_about_the_problem()
    {
        using var client = await SignedInClientAsync();
        (await PostAsync(client, "/api/v1/reports", new { summary = "x", includeLog = false, includeSystem = false })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/v1/reports/0123456789abcdef")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/reports/..%2F..%2Fsettings")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();
        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost");
        return client.SendAsync(request);
    }

    private sealed class RecordingSink : IWorkItemSink
    {
        public List<ProblemReport> Created { get; } = [];

        public List<TrackedProblem> Repeats { get; } = [];

        public Task<string?> CreateAsync(ProblemReport report, CancellationToken cancellationToken)
        {
            Created.Add(report);
            return Task.FromResult<string?>($"Bug {Created.Count}");
        }

        public Task AddRepeatAsync(TrackedProblem problem, ProblemReport report, CancellationToken cancellationToken)
        {
            Repeats.Add(problem);
            return Task.CompletedTask;
        }
    }
}
