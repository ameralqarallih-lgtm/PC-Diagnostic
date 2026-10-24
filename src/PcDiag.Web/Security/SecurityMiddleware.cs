using Microsoft.Extensions.Options;

namespace PcDiag.Web.Security;

/// <summary>Adds security headers to every response.</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    // No inline scripts, no external origins. 'unsafe-inline' for styles only, because UI primitives
    // may inject <style> elements; script execution stays restricted to our own bundled files.
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "font-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=(), serial=()";

            if (context.Request.Path.StartsWithSegments(Paths.Api, StringComparison.OrdinalIgnoreCase))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}

/// <summary>
/// Rejects state-changing requests (and SignalR handshakes) whose Origin is not this host.
/// Protects against other local web pages or processes driving the API through the user's browser.
/// </summary>
internal sealed class OriginCheckMiddleware(RequestDelegate next, IOptions<WebOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var needsCheck = !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
                         || request.Path.StartsWithSegments(Paths.Hubs, StringComparison.OrdinalIgnoreCase);

        // The companion app authenticates with a custom header, which a page on another site can never send (that would
        // need a CORS preflight, and this host allows none), so such requests need no Origin.
        if (!needsCheck || IsAllowedOrigin(request) || request.Headers.ContainsKey(PairedDevices.HeaderName) || request.Headers.ContainsKey(Paths.ClientHeader))
        {
            return next(context);
        }

        return Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Origin not allowed",
            detail: "State-changing requests must come from the PC Diagnostic dashboard itself.").ExecuteAsync(context);
    }

    private bool IsAllowedOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            return false;
        }

        var self = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, self, StringComparison.OrdinalIgnoreCase)
               || options.Value.AllowedDevOrigins.Any(o => string.Equals(origin, o.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Requests from other devices (only possible when access from the local network is on): only private network
/// addresses, never the one-time launch links (they belong to this PC's tray), and never the pages that manage
/// network access (pairing codes, paired devices, turning it on or off), which work only on this PC.
/// </summary>
internal sealed class LanGuardMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (LanNetwork.IsLocal(context))
        {
            return next(context);
        }

        var path = context.Request.Path;
        if (!LanNetwork.IsPrivate(context.Connection.RemoteIpAddress!))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not on the local network",
                detail: "PC Diagnostic accepts only devices on the same private network.").ExecuteAsync(context);
        }

        if (path.StartsWithSegments(Paths.AuthExchange, StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound().ExecuteAsync(context);
        }

        // Ending processes, stopping services and the tune-up change this PC: only from the PC itself.
        if ((path.StartsWithSegments(Paths.ControlApi, StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments(Paths.TuneUpApi, StringComparison.OrdinalIgnoreCase))
            && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "Processes, services and the tune-up are changed on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        // What is drawn on this PC's screens is arranged on the PC itself.
        if (path.StartsWithSegments(Paths.OverlayApi, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "The readings on this PC's screens are arranged on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        // Driver packages run on this PC: imported, approved and removed on the PC itself.
        if (path.StartsWithSegments(Paths.DriversApi, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "Driver packages are imported, approved and removed on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        // The expected hardware is remembered, forgotten and checked on the PC itself.
        if (path.StartsWithSegments(Paths.HardwareApi, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "The expected hardware is set on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        // Clean-ups, deleting incidents in bulk and where files are kept are done on the PC itself.
        if (path.StartsWithSegments(Paths.StorageApi, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "Clean-ups and where files are kept are changed on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        // Problem reports are written, sent and consented to on the PC itself.
        if (path.StartsWithSegments(Paths.ReportsApi, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "Problem reports are written and sent on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        if (path.StartsWithSegments(Paths.LanApi, StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments(Paths.SettingsApi, StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsGet(context.Request.Method))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only on the PC itself",
                detail: "Network access, paired devices and settings are changed on the PC that runs PC Diagnostic.").ExecuteAsync(context);
        }

        return next(context);
    }
}

/// <summary>
/// Requires a valid session cookie (this PC, from a launch link) or device cookie (a paired device on the local
/// network) for everything except the launch-token exchange and the pairing page. API requests get a 401 problem
/// response; page requests get a short explanation page, or the pairing page on another device.
/// </summary>
internal sealed class SessionAuthMiddleware(RequestDelegate next, SessionStore sessions, PairedDevices devices)
{
    private const string UnauthorizedPage =
        """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>PC Diagnostic</title>
        <style>body{font-family:system-ui,sans-serif;background:#0b0f14;color:#e6edf3;display:grid;place-items:center;min-height:100vh;margin:0;padding:16px}
        main{max-width:32rem}h1{font-size:1.25rem}p{color:#9aa7b4;line-height:1.5}</style></head>
        <body><main><h1>Open the dashboard from PC Diagnostic</h1>
        <p>For your security, the dashboard only opens through a one-time link. Use <strong>Open dashboard</strong>
        in the PC Diagnostic tray menu, or use the link printed when the web host starts.</p></main></body></html>
        """;

    public Task InvokeAsync(HttpContext context)
    {
        var local = LanNetwork.IsLocal(context);
        if (context.Request.Path.StartsWithSegments(Paths.AuthExchange, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments(Paths.Pair, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals(Paths.DeviceHello, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals(Paths.DevicePair, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals(Paths.CompanionApk, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals(Paths.Health, StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals(Paths.Manifest, StringComparison.OrdinalIgnoreCase)
            || sessions.Validate(context.Request.Cookies[SessionStore.CookieName])
            || (!local && devices.Validate(PairedDevices.TokenOf(context.Request), context.Connection.RemoteIpAddress?.ToString()) is not null))
        {
            return next(context);
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments(Paths.Api, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(Paths.Hubs, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Not signed in",
                detail: "Open the dashboard through a PC Diagnostic launch link.").ExecuteAsync(context);
        }

        return local
            ? Results.Content(UnauthorizedPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context)
            : Results.Redirect(Paths.Pair).ExecuteAsync(context);
    }
}

/// <summary>Well-known request paths.</summary>
internal static class Paths
{
    public static readonly PathString Api = "/api";
    public static readonly PathString Hubs = "/hubs";
    public static readonly PathString AuthExchange = "/auth/exchange";

    /// <summary>Pairing page for other devices on the local network.</summary>
    public static readonly PathString Pair = "/pair";

    /// <summary>The companion app's "is this the PC, and am I paired?" check (no sign-in).</summary>
    public static readonly PathString DeviceHello = "/api/v1/devices/hello";

    /// <summary>The companion app's pairing call (no sign-in; the code is the credential).</summary>
    public static readonly PathString DevicePair = "/api/v1/devices/pair";

    /// <summary>The companion app download.</summary>
    public static readonly PathString CompanionApk = "/app/pcdiagnostic.apk";

    /// <summary>Header the companion app sends on calls made before it has a token.</summary>
    public const string ClientHeader = "X-PCDiag-Client";

    /// <summary>Network access management (this PC only).</summary>
    public static readonly PathString LanApi = "/api/v1/lan";

    /// <summary>Settings (changed on this PC only).</summary>
    public static readonly PathString SettingsApi = "/api/v1/settings";

    /// <summary>Process and service control.</summary>
    public static readonly PathString ControlApi = "/api/v1/control";

    /// <summary>The tune-up.</summary>
    public static readonly PathString TuneUpApi = "/api/v1/tuneup";

    /// <summary>Problem reports for the makers.</summary>
    public static readonly PathString ReportsApi = "/api/v1/reports";

    /// <summary>The on-screen readings layout.</summary>
    public static readonly PathString OverlayApi = "/api/v1/overlay";

    /// <summary>The storage manager (clean-ups and deletions).</summary>
    public static readonly PathString StorageApi = "/api/v1/storage";

    /// <summary>Expected hardware (remembered on this PC only).</summary>
    public static readonly PathString HardwareApi = "/api/v1/hardware";

    /// <summary>Driver packages: imported, approved and removed on the PC itself.</summary>
    public static readonly PathString DriversApi = "/api/v1/drivers";

    /// <summary>Unauthenticated liveness probe; returns only "ok".</summary>
    public static readonly PathString Health = "/healthz";

    /// <summary>The web app manifest (name, icons, colors; nothing private), which browsers fetch without the session cookie.</summary>
    public static readonly PathString Manifest = "/manifest.webmanifest";
}
