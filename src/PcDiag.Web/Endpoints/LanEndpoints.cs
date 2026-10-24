using System.Net;
using PcDiag.Web.Hosting;
using PcDiag.Web.Security;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Endpoints;

/// <summary>
/// Access from other devices on the local network: the status and switch (this PC only, enforced by
/// <see cref="LanGuardMiddleware"/>), pairing codes, paired devices, and the pairing page other devices open.
/// </summary>
internal static class LanEndpoints
{
    public static IEndpointRouteBuilder MapLanEndpoints(this IEndpointRouteBuilder app)
    {
        var lan = app.MapGroup("/api/v1/lan");

        lan.MapGet(string.Empty, (LanState state, PairedDevices devices, PairingCodes codes) =>
            state.Status(devices.List(), codes.Current, devices.InstallId, CompanionApp.Exists ? Paths.CompanionApk.Value : null));

        lan.MapPut(string.Empty, (HttpContext context, LanChange? change, LanState state, SettingsStore settings, WebHostRestart restart, IHostApplicationLifetime lifetime) =>
        {
            if (change is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Nothing to change");
            }

            if (!state.Supported && change.Enabled)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not available here", detail: state.Unsupported);
            }

            if (change.Port is < 1024 or > 65535 || change.Port == state.LoopbackPort)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["port"] = ["Choose a port between 1024 and 65535 that the dashboard does not already use."] },
                    title: "Invalid port");
            }

            settings.SaveLan(change.Enabled, change.Port);

            // Kestrel's endpoints are fixed while it runs: restart the host in this process once the answer is sent.
            context.Response.OnCompleted(() =>
            {
                restart.Request();
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted(value: new { restarting = true });
        });

        // The dashboard's address on this PC: the same port every time, chosen here (this PC only, as all of /lan).
        lan.MapGet("/address", (AddressState address) => new AddressStatus(address.Wanted, address.Listening, address.Problem));

        lan.MapPut("/address", (HttpContext context, AddressChange? change, AddressState address, LanState state, SettingsStore settings, WebHostRestart restart, IHostApplicationLifetime lifetime) =>
        {
            if (change is null || change.Port is < 1024 or > 65535 || change.Port == state.Port || change.Port == state.DiscoveryPort)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["port"] = ["Choose a port between 1024 and 65535 that network access does not use."] }, title: "Invalid port");
            }

            settings.SavePort(change.Port);
            address.Wanted = change.Port;

            // The new address is listened on once the host starts again (in this process; sessions carry over).
            context.Response.OnCompleted(() =>
            {
                restart.NewAddress = true;
                restart.Request();
                lifetime.StopApplication();
                return Task.CompletedTask;
            });
            return Results.Accepted(value: new { restarting = true, url = $"http://127.0.0.1:{change.Port}/" });
        });

        lan.MapPost("/pairing-code", (PairingCodes codes) =>
        {
            var (code, expiresAt) = codes.Create();
            return new PairingCode(code, expiresAt);
        });

        lan.MapDelete("/pairing-code", (PairingCodes codes) =>
        {
            codes.Cancel();
            return Results.NoContent();
        });

        lan.MapDelete("/devices/{id}", (string id, PairedDevices devices) =>
            devices.Revoke(id) ? Results.NoContent() : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such device"));

        // The companion app: "is this PC Diagnostic, which install, and am I paired?" (no sign-in, like a ping).
        app.MapGet(Paths.DeviceHello.Value!, (HttpContext context, PairedDevices devices) => new DeviceHello(
            "pcdiagnostic",
            devices.InstallId,
            Environment.MachineName,
            !LanNetwork.IsLocal(context) && devices.Validate(PairedDevices.TokenOf(context.Request), context.Connection.RemoteIpAddress?.ToString()) is not null,
            typeof(LanEndpoints).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")).ExcludeFromDescription();

        // The companion app's pairing: the code shown on this PC is the credential; the token comes back in the body
        // (and as the browser cookie, so the app's web view is signed in too).
        app.MapPost(Paths.DevicePair.Value!, (HttpContext context, DevicePairRequest? request, PairingCodes codes, PairedDevices devices, IConfiguration configuration) =>
        {
            if (LanNetwork.IsLocal(context))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Pair from the other device", detail: "This PC is signed in already.");
            }

            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            switch (codes.TryRedeem(request?.Code, address))
            {
                case PairingCodes.Result.Throttled:
                    return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many attempts", detail: "Wait a minute, then try again.");
                case PairingCodes.Result.Rejected:
                    return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "That code is wrong or has expired",
                        detail: "Show a new pairing code on the PC (Settings → Access from other devices) and try again.");
            }

            var (device, token) = devices.Pair(string.IsNullOrWhiteSpace(request?.Name) ? Describe(context.Request.Headers.UserAgent.ToString()) : request.Name, address);
            SetDeviceCookie(context, token, configuration);
            return Results.Ok(new DevicePairResponse(token, device, devices.InstallId));
        }).ExcludeFromDescription();

        // The companion app download (shown as a QR code in Settings).
        app.MapGet(Paths.CompanionApk.Value!, () => CompanionApp.Exists
            ? Results.File(CompanionApp.Path, "application/vnd.android.package-archive", "pcdiagnostic.apk")
            : Results.NotFound()).ExcludeFromDescription();

        // The pairing page (plain HTML form, no script) for other devices.
        app.MapGet(Paths.Pair.Value!, (HttpContext context) => LanNetwork.IsLocal(context) ? Results.Redirect("/") : PairPage(null, StatusCodes.Status200OK))
            .ExcludeFromDescription();

        app.MapPost(Paths.Pair.Value!, async (HttpContext context, PairingCodes codes, PairedDevices devices, IConfiguration configuration) =>
        {
            if (LanNetwork.IsLocal(context))
            {
                return Results.Redirect("/");
            }

            var form = context.Request.HasFormContentType ? await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false) : null;
            var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            switch (codes.TryRedeem(form?["code"], address))
            {
                case PairingCodes.Result.Throttled:
                    return PairPage("Too many attempts. Wait a minute, then try again with the code shown on the PC.", StatusCodes.Status429TooManyRequests);
                case PairingCodes.Result.Rejected:
                    return PairPage("That code is not right or has expired. Show a new code on the PC and try again.", StatusCodes.Status400BadRequest);
            }

            var name = form?["name"].ToString();
            var (_, token) = devices.Pair(string.IsNullOrWhiteSpace(name) ? Describe(context.Request.Headers.UserAgent.ToString()) : name, address);
            SetDeviceCookie(context, token, configuration);
            return Results.Redirect("/");
        }).ExcludeFromDescription();

        return app;
    }

    private static void SetDeviceCookie(HttpContext context, string token, IConfiguration configuration) =>
        context.Response.Cookies.Append(PairedDevices.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            IsEssential = true,
            MaxAge = TimeSpan.FromDays(configuration.GetValue("Web:Lan:DeviceIdleDays", 90)),
        });

    /// <summary>A short description of a browser, e.g. "Edge on Windows", for an unnamed device.</summary>
    internal static string Describe(string userAgent)
    {
        var browser = userAgent.Contains("Edg/", StringComparison.Ordinal) ? "Edge"
            : userAgent.Contains("Firefox/", StringComparison.Ordinal) ? "Firefox"
            : userAgent.Contains("Chrome/", StringComparison.Ordinal) ? "Chrome"
            : userAgent.Contains("Safari/", StringComparison.Ordinal) ? "Safari"
            : "Browser";
        var system = userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : userAgent.Contains("iPhone", StringComparison.Ordinal) || userAgent.Contains("iPad", StringComparison.Ordinal) ? "iOS"
            : userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Mac OS", StringComparison.Ordinal) ? "macOS"
            : userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux"
            : "a device";
        return $"{browser} on {system}";
    }

    private static IResult PairPage(string? problem, int status)
    {
        var message = problem is null ? "" : $"<p class=\"problem\" role=\"alert\">{WebUtility.HtmlEncode(problem)}</p>";
        var html =
            $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Pair with PC Diagnostic</title>
            <style>
            body{font-family:system-ui,sans-serif;background:#0b0f14;color:#e6edf3;display:grid;place-items:center;min-height:100vh;margin:0;padding:16px}
            main{max-width:26rem;width:100%}h1{font-size:1.25rem}p{color:#9aa7b4;line-height:1.5}
            label{display:block;margin:14px 0 4px;font-size:.9rem}input{width:100%;box-sizing:border-box;padding:10px;border-radius:8px;border:1px solid #253041;background:#121821;color:#e6edf3;font-size:1.1rem}
            #code{letter-spacing:.2em;text-align:center;font-size:1.5rem}button{margin-top:18px;width:100%;padding:12px;border:0;border-radius:8px;background:#38bdf8;color:#0b0f14;font-weight:600;font-size:1rem}
            .problem{color:#f87171}
            </style></head>
            <body><main>
            <h1>Pair this device with PC Diagnostic</h1>
            <p>On the PC, open the dashboard, go to <strong>Settings → Access from other devices</strong> and press <strong>Show pairing code</strong>.
            Enter the code here. It works once, for 5 minutes.</p>
            {{message}}
            <form method="post" action="/pair" autocomplete="off">
            <label for="code">Pairing code</label>
            <input id="code" name="code" inputmode="numeric" pattern="[0-9 -]{8,11}" maxlength="11" required autofocus placeholder="1234-5678">
            <label for="name">Name for this device (optional)</label>
            <input id="name" name="name" maxlength="60" placeholder="e.g. Phone">
            <button type="submit">Pair</button>
            </form>
            </main></body></html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", statusCode: status);
    }
}

/// <summary>The companion app's view of this PC.</summary>
/// <param name="App">Always "pcdiagnostic".</param>
/// <param name="Id">This install's ID (stable across address changes).</param>
/// <param name="Name">The computer name.</param>
/// <param name="Paired">Whether the caller's token is valid.</param>
/// <param name="Version">The PC Diagnostic version.</param>
public sealed record DeviceHello(string App, string Id, string Name, bool Paired, string Version);

/// <summary>The companion app's pairing request.</summary>
/// <param name="Code">The code shown on the PC.</param>
/// <param name="Name">A name for the device, e.g. "Samsung SM-S918B".</param>
public sealed record DevicePairRequest(string? Code, string? Name);

/// <summary>The companion app's pairing result.</summary>
/// <param name="Token">The device token (send it as the X-PCDiag-Device header; keep it secret).</param>
/// <param name="Device">The paired device.</param>
/// <param name="Id">This install's ID.</param>
public sealed record DevicePairResponse(string Token, PairedDevice Device, string Id);

/// <summary>Where the companion app's APK is (copied next to the web host by the build).</summary>
internal static class CompanionApp
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "app", "pcdiagnostic.apk");

    public static bool Exists => File.Exists(Path);
}

/// <summary>The dashboard's address on this PC.</summary>
/// <param name="Port">The port chosen.</param>
/// <param name="Listening">The port listened on now.</param>
/// <param name="Problem">Why they differ (another program had it), or null.</param>
public sealed record AddressStatus(int Port, int Listening, string? Problem);

/// <summary>Request to change the dashboard's port on this PC.</summary>
/// <param name="Port">The port, 1024-65535.</param>
public sealed record AddressChange(int Port);

/// <summary>Request to change network access.</summary>
/// <param name="Enabled">Listen on the local network.</param>
/// <param name="Port">The network port.</param>
public sealed record LanChange(bool Enabled, int Port);

/// <summary>A pairing code.</summary>
/// <param name="Code">The code, e.g. "1234-5678".</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="Link">pcdiag://pair?… for the companion app (address, port, code, install ID and certificate fingerprint), when listening.</param>
public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt, string? Link = null);

/// <summary>Network access as the Settings page shows it.</summary>
/// <param name="Supported">Whether network access can be used here (Windows, not in a container).</param>
/// <param name="Unsupported">Why not, when not supported.</param>
/// <param name="Enabled">Whether it is turned on in the settings.</param>
/// <param name="Listening">Whether the host is listening on the network now.</param>
/// <param name="Port">The network port.</param>
/// <param name="Urls">Addresses other devices can open.</param>
/// <param name="Fingerprint">The certificate's SHA-256 fingerprint, to compare with what the browser shows.</param>
/// <param name="Problem">Why it is not listening although turned on.</param>
/// <param name="Devices">Paired devices.</param>
/// <param name="Pairing">The current pairing code, if one is shown, with the companion app's pairing link.</param>
/// <param name="InstallId">This install's ID.</param>
/// <param name="ApkUrl">Path of the companion app download, when it is included.</param>
/// <param name="DiscoveryPort">UDP port the companion app's "Find my PC" asks on.</param>
public sealed record LanStatus(
    bool Supported,
    string? Unsupported,
    bool Enabled,
    bool Listening,
    int Port,
    IReadOnlyList<string> Urls,
    string? Fingerprint,
    string? Problem,
    IReadOnlyList<PairedDevice> Devices,
    PairingCode? Pairing,
    string InstallId,
    string? ApkUrl,
    int DiscoveryPort);
