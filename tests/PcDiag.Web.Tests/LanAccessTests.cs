using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Time.Testing;
using PcDiag.Storage.Sqlite;
using PcDiag.Web.Agent;
using PcDiag.Web.Endpoints;
using PcDiag.Web.Security;

namespace PcDiag.Web.Tests;

/// <summary>
/// Access from other devices: requests are sent through the in-memory server with a chosen remote address, the way
/// Kestrel reports a device on the network.
/// </summary>
public sealed class LanAccessTests : IDisposable
{
    private static readonly IPAddress Phone = IPAddress.Parse("192.168.1.42");

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly WebApplicationFactory<Program> _factory;

    public LanAccessTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Storage:DataDirectory", _dataDirectory);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IAgentClient>(new FakeAgentClient { Connected = true });
                services.AddSingleton<TimeProvider>(_time);
                services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, RemoteAddressFilter>();
            });
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

    private Task<HttpContext> SendAsync(string method, string path, IPAddress? from, string? cookie = null, string? origin = null, Dictionary<string, string>? form = null, string? json = null,
        Dictionary<string, string>? headers = null) =>
        _factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = from;
            context.Request.Method = method;
            context.Request.Scheme = from is null ? "http" : "https";
            context.Request.Host = from is null ? new HostString("localhost") : new HostString("localhost");
            var query = path.IndexOf('?', StringComparison.Ordinal);
            context.Request.Path = query < 0 ? path : path[..query];
            context.Request.QueryString = query < 0 ? QueryString.Empty : new QueryString(path[query..]);
            if (cookie is not null)
            {
                context.Request.Headers.Cookie = cookie;
            }

            if (origin is not null)
            {
                context.Request.Headers.Origin = origin;
            }

            foreach (var (name, value) in headers ?? [])
            {
                context.Request.Headers[name] = value;
            }

            if (json is not null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
                context.Request.Body = new MemoryStream(bytes);

                // Let the request read the body above (the in-memory server's own body pipe is empty).
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IRequestBodyPipeFeature>(null);
            }

            if (form is not null)
            {
                var body = string.Join('&', form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
                context.Request.ContentType = "application/x-www-form-urlencoded";
                context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(body));
            }
        });

    /// <summary>Test only: the request comes from the address in <see cref="RemoteAddressFilter.Header"/>, as Kestrel would report it.</summary>
    private sealed class RemoteAddressFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public const string Header = "X-Test-Remote-Address";

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(nextMiddleware => context =>
            {
                if (context.Request.Headers.TryGetValue(Header, out var address))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                    context.Request.Headers.Remove(Header);
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>A real request (with a JSON body) from a device on the network.</summary>
    private async Task<HttpResponseMessage> PostJsonFromAsync(IPAddress from, string path, string json, Dictionary<string, string>? headers = null)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(RemoteAddressFilter.Header, from.ToString());
        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request);
    }

    /// <summary>A session for this PC, through a launch token.</summary>
    private async Task<string> LocalSessionAsync()
    {
        var token = _factory.Services.GetRequiredService<LaunchTokenService>().IssueToken();
        var exchange = await SendAsync("GET", $"/auth/exchange?t={token}", null);
        return CookieFrom(exchange, SessionStore.CookieName)!;
    }

    private static string? CookieFrom(HttpContext context, string name)
    {
        foreach (var header in context.Response.Headers.SetCookie)
        {
            if (header is not null && header.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return header.Split(';')[0];
            }
        }

        return null;
    }

    private static async Task<string> BodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }

    private async Task<string> NewCodeAsync(string session)
    {
        var created = await SendAsync("POST", "/api/v1/lan/pairing-code", null, session, origin: "http://localhost");
        created.Response.StatusCode.ShouldBe(200);
        return JsonNode.Parse(await BodyAsync(created))!["code"]!.GetValue<string>();
    }

    private Task<HttpContext> PairAsync(string code, IPAddress? from = null, string name = "Phone") =>
        SendAsync("POST", "/pair", from ?? Phone, origin: "https://localhost", form: new() { ["code"] = code, ["name"] = name });

    [Fact]
    public async Task The_dashboard_keeps_one_port_chosen_on_this_pc()
    {
        var session = await LocalSessionAsync();
        var address = JsonNode.Parse(await BodyAsync(await SendAsync("GET", "/api/v1/lan/address", null, session)))!;
        address["port"]!.GetValue<int>().ShouldBe(47440, "the same port every time, not any free one");
        address["problem"].ShouldBeNull();

        // Not a usable port, or network access's: refused; and only on this PC.
        (await SendAsync("PUT", "/api/v1/lan/address", null, session, origin: "http://localhost", json: """{"port":80}""")).Response.StatusCode.ShouldBe(400);
        (await SendAsync("PUT", "/api/v1/lan/address", null, session, origin: "http://localhost", json: """{"port":47443}""")).Response.StatusCode.ShouldBe(400);
        (await SendAsync("GET", "/api/v1/lan/address", Phone)).Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Network_access_is_off_by_default_and_managed_only_on_this_pc()
    {
        var session = await LocalSessionAsync();

        var status = JsonNode.Parse(await BodyAsync(await SendAsync("GET", "/api/v1/lan", null, session)))!;
        status["enabled"]!.GetValue<bool>().ShouldBeFalse();
        status["listening"]!.GetValue<bool>().ShouldBeFalse();
        status["port"]!.GetValue<int>().ShouldBe(47443);

        (await SendAsync("GET", "/api/v1/lan", Phone)).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/lan/pairing-code", Phone, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("GET", "/auth/exchange?t=whatever", Phone)).Response.StatusCode.ShouldBe(404, "launch links belong to this PC");
    }

    [Fact]
    public async Task A_device_pairs_once_with_the_code_shown_on_this_pc_and_is_signed_in()
    {
        var session = await LocalSessionAsync();

        // Unpaired: API requests are refused and pages lead to pairing.
        (await SendAsync("GET", "/api/v1/status", Phone)).Response.StatusCode.ShouldBe(401);
        var page = await SendAsync("GET", "/", Phone);
        page.Response.StatusCode.ShouldBe(302);
        page.Response.Headers.Location.ToString().ShouldBe("/pair");
        (await BodyAsync(await SendAsync("GET", "/pair", Phone))).ShouldContain("Pairing code");

        var code = await NewCodeAsync(session);
        code.ShouldMatch(@"^\d{4}-\d{4}$");
        var paired = await PairAsync(code.Replace("-", " ", StringComparison.Ordinal));
        paired.Response.StatusCode.ShouldBe(302);
        var device = CookieFrom(paired, PairedDevices.CookieName).ShouldNotBeNull();
        var setCookie = paired.Response.Headers.SetCookie.ToString();
        setCookie.ShouldContain("secure", Case.Insensitive);
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("samesite=strict", Case.Insensitive);

        (await SendAsync("GET", "/api/v1/status", Phone, device)).Response.StatusCode.ShouldBe(200);
        (await PairAsync(code)).Response.StatusCode.ShouldBe(400, "a code works once");

        // Listed on this PC with the name given, and revocable.
        var devices = JsonNode.Parse(await BodyAsync(await SendAsync("GET", "/api/v1/lan", null, session)))!["devices"]!.AsArray();
        var listed = devices.ShouldHaveSingleItem()!;
        listed["name"]!.GetValue<string>().ShouldBe("Phone");
        listed["lastAddress"]!.GetValue<string>().ShouldBe("192.168.1.42");
        (await SendAsync("DELETE", $"/api/v1/lan/devices/{listed["id"]!.GetValue<string>()}", null, session, origin: "http://localhost")).Response.StatusCode.ShouldBe(204);
        (await SendAsync("GET", "/api/v1/status", Phone, device)).Response.StatusCode.ShouldBe(401);
    }

    [Fact]
    public async Task Codes_expire_and_stop_working_after_five_wrong_attempts_and_guessing_is_throttled()
    {
        var session = await LocalSessionAsync();
        var code = await NewCodeAsync(session);
        for (var i = 0; i < PairingCodes.MaxWrongAttempts; i++)
        {
            (await PairAsync("0000-0000")).Response.StatusCode.ShouldBe(400);
        }

        (await PairAsync(code)).Response.StatusCode.ShouldBe(400, "five wrong attempts discard the code");

        var expiring = await NewCodeAsync(session);
        _time.Advance(PairingCodes.Lifetime + TimeSpan.FromSeconds(1));
        (await PairAsync(expiring, IPAddress.Parse("192.168.1.43"))).Response.StatusCode.ShouldBe(400);

        var other = IPAddress.Parse("10.0.0.7");
        for (var i = 0; i < PairingCodes.MaxAttemptsPerMinute; i++)
        {
            await PairAsync("1111-1111", other);
        }

        (await PairAsync("1111-1111", other)).Response.StatusCode.ShouldBe(429);
    }

    [Fact]
    public async Task Only_private_network_addresses_are_accepted_and_remote_devices_cannot_change_settings()
    {
        var session = await LocalSessionAsync();
        (await SendAsync("GET", "/pair", IPAddress.Parse("8.8.8.8"))).Response.StatusCode.ShouldBe(403);
        (await SendAsync("GET", "/pair", IPAddress.Parse("fd12:3456::1"))).Response.StatusCode.ShouldBe(200);

        var paired = await PairAsync(await NewCodeAsync(session));
        var device = CookieFrom(paired, PairedDevices.CookieName)!;
        (await SendAsync("GET", "/api/v1/settings", Phone, device)).Response.StatusCode.ShouldBe(200);
        (await SendAsync("PUT", "/api/v1/settings", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("PUT", "/api/v1/lan", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);

        // Ending processes, stopping services and the tune-up change this PC: never from another device.
        (await SendAsync("POST", "/api/v1/control/processes/1234/end", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/control/services/Spooler/stop", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/tuneup/clean", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/reports", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);

        // Clean-ups and deleting incidents in bulk too; looking at the storage is fine.
        (await SendAsync("POST", "/api/v1/storage/clean", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/storage/incidents/delete", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("GET", "/api/v1/storage", Phone, device)).Response.StatusCode.ShouldBe(200);
        (await SendAsync("PUT", "/api/v1/hardware/expected", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/overlay/arrange", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/overlay/images", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/overlay/screen-drivers/approve", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
        (await SendAsync("POST", "/api/v1/hardware/check", Phone, device, origin: "https://localhost")).Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task The_companion_app_checks_the_pc_pairs_with_json_and_signs_requests_with_its_header()
    {
        var session = await LocalSessionAsync();
        var hello = JsonNode.Parse(await BodyAsync(await SendAsync("GET", "/api/v1/devices/hello", Phone)))!;
        hello["app"]!.GetValue<string>().ShouldBe("pcdiagnostic");
        hello["paired"]!.GetValue<bool>().ShouldBeFalse();
        var installId = hello["id"]!.GetValue<string>();
        installId.ShouldMatch("^[0-9a-f]{16}$");

        var client = new Dictionary<string, string> { ["X-PCDiag-Client"] = "android/1.0" };
        var wrong = await PostJsonFromAsync(Phone, "/api/v1/devices/pair", """{"code":"00000000","name":"Pixel 9"}""", client);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var code = await NewCodeAsync(session);
        var paired = await PostJsonFromAsync(Phone, "/api/v1/devices/pair", $$"""{"code":"{{code}}","name":"Pixel 9"}""", client);
        paired.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await paired.Content.ReadAsStringAsync())!;
        var token = body["token"]!.GetValue<string>();
        body["id"]!.GetValue<string>().ShouldBe(installId);
        body["device"]!["name"]!.GetValue<string>().ShouldBe("Pixel 9");
        paired.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith($"{PairedDevices.CookieName}={token}", StringComparison.Ordinal), "the app's web view is signed in too");

        var device = new Dictionary<string, string> { [PairedDevices.HeaderName] = token };
        JsonNode.Parse(await BodyAsync(await SendAsync("GET", "/api/v1/devices/hello", Phone, headers: device)))!["paired"]!.GetValue<bool>().ShouldBeTrue();
        (await SendAsync("GET", "/api/v1/status", Phone, headers: device)).Response.StatusCode.ShouldBe(200);

        // A state-changing call with the header needs no Origin (no page on another site can send that header).
        (await SendAsync("POST", "/api/v1/recorder/start", Phone, headers: device)).Response.StatusCode.ShouldNotBe(403);
        (await SendAsync("POST", "/api/v1/recorder/start", Phone, cookie: $"{PairedDevices.CookieName}={token}")).Response.StatusCode.ShouldBe(403, "a cookie alone still needs a matching Origin");
        (await SendAsync("GET", "/api/v1/status", Phone, headers: new() { [PairedDevices.HeaderName] = "forged" })).Response.StatusCode.ShouldBe(401);

        // The install ID survives a restart (it is in devices.json).
        File.ReadAllText(Path.Combine(_dataDirectory, "devices.json")).ShouldContain(installId);
        // The companion app download needs no pairing (it is how a phone gets the app).
        var apk = await SendAsync("GET", "/app/pcdiagnostic.apk", Phone);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "app", "pcdiagnostic.apk")))
        {
            apk.Response.StatusCode.ShouldBe(200);
            apk.Response.ContentType.ShouldBe("application/vnd.android.package-archive");
        }
        else
        {
            apk.Response.StatusCode.ShouldBe(404);
        }
    }

    [Fact]
    public void Find_my_pc_answers_only_its_question_from_private_networks()
    {
        var state = new PcDiag.Web.Hosting.LanState { Supported = true, Enabled = true, Listening = true, Port = 47443, Fingerprint = "AB:CD", PrimaryHost = "192.168.1.151" };
        var question = System.Text.Encoding.ASCII.GetBytes("PCDIAG?");

        var answer = PcDiag.Web.Hosting.LanDiscoveryService.Answer(question, Phone, IPAddress.Parse("192.168.1.151"), state, "0123456789abcdef").ShouldNotBeNull();
        var json = JsonNode.Parse(answer)!;
        json["app"]!.GetValue<string>().ShouldBe("pcdiagnostic");
        json["id"]!.GetValue<string>().ShouldBe("0123456789abcdef");
        json["host"]!.GetValue<string>().ShouldBe("192.168.1.151");
        json["port"]!.GetValue<int>().ShouldBe(47443);
        json["fp"]!.GetValue<string>().ShouldBe("ABCD");

        PcDiag.Web.Hosting.LanDiscoveryService.Answer(question, IPAddress.Parse("8.8.8.8"), null, state, "x").ShouldBeNull();
        PcDiag.Web.Hosting.LanDiscoveryService.Answer("HELLO"u8, Phone, null, state, "x").ShouldBeNull();
        PcDiag.Web.Hosting.LanDiscoveryService.Answer(question, Phone, null, new PcDiag.Web.Hosting.LanState(), "x").ShouldBeNull("not listening");
        state.PairingLink("1234-5678", "0123456789abcdef").ShouldBe("pcdiag://pair?host=192.168.1.151&port=47443&code=12345678&id=0123456789abcdef&fp=ABCD");
    }

    [Fact]
    public void Private_address_ranges_and_device_names()
    {
        foreach (var address in new[] { "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.0.1", "169.254.1.1", "100.100.1.1", "fd00::1", "fe80::1", "::ffff:192.168.1.5" })
        {
            LanNetwork.IsPrivate(IPAddress.Parse(address)).ShouldBeTrue(address);
        }

        foreach (var address in new[] { "8.8.8.8", "172.32.0.1", "192.169.0.1", "100.128.0.1", "2001:db8::1" })
        {
            LanNetwork.IsPrivate(IPAddress.Parse(address)).ShouldBeFalse(address);
        }

        LanEndpoints.Describe("Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 Chrome/129.0 Mobile Safari/537.36").ShouldBe("Chrome on Android");
        LanEndpoints.Describe("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1").ShouldBe("Safari on iOS");
    }

    [Fact]
    public void The_certificate_is_kept_across_address_changes_until_it_nears_expiry_and_is_stored_encrypted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI; access from other devices is offered on Windows only.
        }

        var paths = new PcDiag.Core.Configuration.DataPaths(_dataDirectory);
        var now = DateTimeOffset.UtcNow;
        string[] names = ["gaming-pc"];
        IPAddress[] addresses = [IPAddress.Parse("192.168.1.151")];

        using var first = LanCertificate.GetOrCreate(paths, names, addresses, now);
        using var again = LanCertificate.GetOrCreate(paths, names, addresses, now.AddDays(10));
        again.Thumbprint.ShouldBe(first.Thumbprint);
        first.HasPrivateKey.ShouldBeTrue();
        LanCertificate.Covers(first, names, addresses).ShouldBeTrue();
        LanCertificate.Fingerprint(first).ShouldMatch("^([0-9A-F]{2}:){31}[0-9A-F]{2}$");
        File.ReadAllBytes(paths.LanCertificateFile).AsSpan().IndexOf(first.RawData.AsSpan(0, 16)).ShouldBe(-1, "stored encrypted");

        // A new address keeps the certificate, so paired apps that pinned its fingerprint keep working.
        using var moved = LanCertificate.GetOrCreate(paths, names, [IPAddress.Parse("192.168.1.200")], now);
        moved.Thumbprint.ShouldBe(first.Thumbprint);
        using var renewed = LanCertificate.GetOrCreate(paths, names, [IPAddress.Parse("192.168.1.200")], now + LanCertificate.Validity - TimeSpan.FromDays(10));
        renewed.Thumbprint.ShouldNotBe(moved.Thumbprint);
    }

    /// <summary>A real TLS handshake with the stored certificate (on loopback only, so nothing is exposed).</summary>
    [Fact]
    public async Task Kestrel_serves_https_with_the_certificate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var paths = new PcDiag.Core.Configuration.DataPaths(_dataDirectory);
        using var certificate = LanCertificate.GetOrCreate(paths, ["localhost"], [IPAddress.Loopback], DateTimeOffset.UtcNow);
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions.MapGet(app, "/", () => "ok");
        await app.StartAsync();
        var address = app.Urls.Single();

        string? seen = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            {
                seen = cert?.Thumbprint;
                return true;
            },
        };
        using var client = new HttpClient(handler);
        (await client.GetStringAsync(address)).ShouldBe("ok");
        seen.ShouldBe(certificate.Thumbprint);
        await app.StopAsync();
    }

    [Fact]
    public async Task Turning_network_access_on_is_validated_and_saved()
    {
        var session = await LocalSessionAsync();
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/lan") { Content = new StringContent("""{"enabled":true,"port":80}""", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", session);
        request.Headers.Add("Origin", "http://localhost");
        var bad = await client.SendAsync(request);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await bad.Content.ReadAsStringAsync()).ShouldContain("between 1024 and 65535");

        // Saving restarts the host in a real run; the in-memory server only checks what is written.
        _factory.Services.GetRequiredService<PcDiag.Web.Settings.SettingsStore>().SaveLan(enabled: true, port: 48000);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dataDirectory, "settings.json")))!;
        file["Web"]!["Lan"]!["Enabled"]!.GetValue<bool>().ShouldBeTrue();
        file["Web"]!["Lan"]!["Port"]!.GetValue<int>().ShouldBe(48000);
        _ = JsonSerializer.Serialize(file);
    }
}
