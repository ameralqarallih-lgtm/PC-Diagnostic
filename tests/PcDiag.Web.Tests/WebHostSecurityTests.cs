using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Endpoints;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

public sealed class WebHostSecurityTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public WebHostSecurityTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);

            // Never talk to a real Agent that may be running on the test machine.
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
            // Log files may still be flushing; best effort.
        }
    }

    [Fact]
    public async Task Api_requires_a_session()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/api/v1/status");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Pages_without_a_session_explain_how_to_open_the_dashboard()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/incidents");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).ShouldContain("one-time link");
    }

    [Fact]
    public async Task Launch_token_is_exchanged_for_a_session_cookie()
    {
        using var client = CreateClient();

        var exchange = await client.GetAsync("/auth/exchange?t=" + IssueToken());

        exchange.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        exchange.Headers.Location!.OriginalString.ShouldBe("/");
        var cookie = exchange.Headers.GetValues("Set-Cookie").Single();
        cookie.ShouldStartWith(SessionStore.CookieName + "=");
        cookie.ShouldContain("httponly", Case.Insensitive);
        cookie.ShouldContain("samesite=strict", Case.Insensitive);

        var status = await client.GetFromJsonAsync<StatusResponse>("/api/v1/status", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        status!.Product.ShouldBe("PC Diagnostic & Incident Recorder");
        status.DataDirectory.ShouldBe(Path.GetFullPath(_dataDirectory));
        status.Agent.Connected.ShouldBeFalse();
    }

    [Fact]
    public async Task Launch_token_works_only_once()
    {
        var token = IssueToken();
        using var first = CreateClient();
        using var second = CreateClient();

        (await first.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await second.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-token")]
    public async Task Invalid_tokens_are_rejected(string token)
    {
        using var client = CreateClient();

        (await client.GetAsync("/auth/exchange?t=" + token)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Forged_cookie_is_rejected()
    {
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionStore.CookieName}=forged");

        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected()
    {
        using var client = CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/status");
        request.Headers.Host = "attacker.example";

        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task State_changing_requests_require_matching_origin()
    {
        using var client = await SignedInClientAsync();

        using var noOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/logout");
        (await client.SendAsync(noOrigin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var foreignOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/logout");
        foreignOrigin.Headers.Add("Origin", "http://evil.example");
        (await client.SendAsync(foreignOrigin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var devOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/logout");
        devOrigin.Headers.Add("Origin", "http://localhost:5173"); // only allowed in Development
        (await client.SendAsync(devOrigin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_with_same_origin_ends_the_session()
    {
        using var client = await SignedInClientAsync();
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/session/logout");
        logout.Headers.Add("Origin", "http://localhost");

        (await client.SendAsync(logout)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/v1/status")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        using var client = await SignedInClientAsync();

        var response = await client.GetAsync("/api/v1/status");

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldContain("script-src 'self'");
        csp.ShouldContain("frame-ancestors 'none'");
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("Referrer-Policy").Single().ShouldBe("no-referrer");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    [Fact]
    public async Task Health_probe_needs_no_session_and_reveals_nothing()
    {
        using var client = CreateClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("ok");
    }

    [Fact]
    public async Task The_app_manifest_needs_no_session()
    {
        // Browsers fetch it without the session cookie; it holds only the app's name, icon and colors.
        using var client = CreateClient();

        var response = await client.GetAsync("/manifest.webmanifest");

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Unknown_api_routes_return_404_not_the_spa()
    {
        using var client = await SignedInClientAsync();

        (await client.GetAsync("/api/v1/does-not-exist")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    private string IssueToken() => _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();

    private async Task<HttpClient> SignedInClientAsync()
    {
        var client = CreateClient();
        (await client.GetAsync("/auth/exchange?t=" + IssueToken())).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
