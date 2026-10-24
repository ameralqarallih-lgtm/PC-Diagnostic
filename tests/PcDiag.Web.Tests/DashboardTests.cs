using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Dashboard;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

public sealed class DashboardTests : IDisposable
{
    private const string Valid = """
        {"version":1,
         "widgets":[{"id":"capture","type":"capture","settings":{}},
                    {"id":"gauges-1","type":"gauges","title":"My gauges","settings":{"metrics":["cpu","gpu"]}},
                    {"id":"chart-1","type":"chart","settings":{"view":"temperature","window":5,"markers":true}}],
         "layouts":{"lg":[{"i":"capture","x":0,"y":0,"w":12,"h":4},{"i":"gauges-1","x":0,"y":4,"w":12,"h":5},{"i":"chart-1","x":0,"y":9,"w":8,"h":9}]}}
        """;

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public DashboardTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);
            builder.ConfigureTestServices(services => services.AddSingleton<IAgentClient>(new FakeAgentClient()));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        new SqliteConnectionFactory().ReleasePooledConnectionsUnder(_dataDirectory);
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task A_new_install_has_no_saved_dashboard()
    {
        using var client = await SignedInClientAsync();

        (await client.GetAsync("/api/v1/dashboard")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_saved_dashboard_is_returned_and_can_be_reset()
    {
        using var client = await SignedInClientAsync();

        (await SendAsync(client, HttpMethod.Put, Valid)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var saved = await client.GetAsync("/api/v1/dashboard");
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        saved.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await saved.Content.ReadAsStringAsync()).ShouldBe(Valid);

        (await SendAsync(client, HttpMethod.Delete, null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/dashboard")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("""{"version":2,"widgets":[],"layouts":{}}""", "version")]
    [InlineData("""{"version":1,"widgets":[{"id":"x","type":"shell"}],"layouts":{}}""", "unknown type")]
    [InlineData("""{"version":1,"widgets":[{"id":"Bad Id","type":"gpu"}],"layouts":{}}""", "unique id")]
    [InlineData("""{"version":1,"widgets":[{"id":"a","type":"gpu"},{"id":"a","type":"gpu"}],"layouts":{}}""", "unique id")]
    [InlineData("""{"version":1,"widgets":[{"id":"a","type":"gpu","settings":{"x":{"nested":true}}}],"layouts":{}}""", "setting")]
    [InlineData("""{"version":1,"widgets":[{"id":"a","type":"gpu"}],"layouts":{"lg":[{"i":"b","x":0,"y":0,"w":1,"h":1}]}}""", "refer to a widget")]
    [InlineData("""{"version":1,"widgets":[{"id":"a","type":"gpu"}],"layouts":{"lg":[{"i":"a","x":-1,"y":0,"w":1,"h":1}]}}""", "invalid x")]
    [InlineData("""{"version":1,"widgets":[],"layouts":{"huge":[]}}""", "Unknown layout")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""{"version":1,"widgets":[],"library":{"a":1},"layouts":{}}""", "library")]
    [InlineData("""{"version":1,"widgets":[],"library":[{"id":"a","type":"shell"}],"layouts":{}}""", "unknown type")]
    public async Task Invalid_dashboards_are_refused(string json, string problem)
    {
        using var client = await SignedInClientAsync();

        var response = await SendAsync(client, HttpMethod.Put, json);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(problem);
        (await client.GetAsync("/api/v1/dashboard")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    /// <summary>A widget the user built: its design and value sources are settings, and it is kept in the library too.</summary>
    [Fact]
    public async Task Custom_widgets_and_the_widget_library_are_saved()
    {
        using var client = await SignedInClientAsync();
        const string Custom = """{"id":"custom-a1b2c","type":"custom","title":"Fans","settings":{"design":"line","sources":["sensor:lhm/gpu-nvidia/0/fan/1","gpu:gpu-10de-2684-467519da:temp"],"colors":["#38bdf8","#f87171"],"note":"Watch the fans while gaming"}}""";
        var json = """{"version":1,"widgets":[""" + Custom + """],"library":[""" + Custom + """],"layouts":{"lg":[{"i":"custom-a1b2c","x":0,"y":0,"w":4,"h":6}]}}""";

        (await SendAsync(client, HttpMethod.Put, json)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await client.GetAsync("/api/v1/dashboard")).Content.ReadAsStringAsync()).ShouldBe(json);

        var tooLong = json.Replace("Watch the fans while gaming", new string('x', 501), StringComparison.Ordinal);
        (await SendAsync(client, HttpMethod.Put, tooLong)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Oversized_dashboards_are_refused()
    {
        using var client = await SignedInClientAsync();

        var response = await SendAsync(client, HttpMethod.Put, Valid.Replace("My gauges", new string('x', DashboardStore.MaxBytes), StringComparison.Ordinal));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Changes_need_the_dashboard_origin()
    {
        using var client = await SignedInClientAsync();
        using var content = new StringContent(Valid, Encoding.UTF8, "application/json");

        (await client.PutAsync("/api/v1/dashboard", content)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string? json)
    {
        var request = new HttpRequestMessage(method, "/api/v1/dashboard");
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        request.Headers.Add("Origin", "http://localhost");
        return client.SendAsync(request);
    }

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        var token = _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();
        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
