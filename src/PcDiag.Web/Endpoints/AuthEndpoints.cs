using PcDiag.Web.Security;

namespace PcDiag.Web.Endpoints;

/// <summary>Launch-token exchange and sign-out.</summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /auth/exchange?t=<token>: swap a one-time launch token for a session cookie, then drop the token from the URL.
        app.MapGet("/auth/exchange", (HttpContext context, string? t, LaunchTokenService tokens, SessionStore sessions) =>
        {
            if (!tokens.TryRedeem(t))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "Launch link expired or already used",
                    detail: "Open the dashboard again from the PC Diagnostic tray menu to get a new link.");
            }

            context.Response.Cookies.Append(SessionStore.CookieName, sessions.Create(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false, // Loopback HTTP only; the cookie never leaves this machine.
                Path = "/",
                IsEssential = true,
            });

            return Results.Redirect("/");
        }).ExcludeFromDescription();

        // POST /api/v1/session/logout: end the current session.
        app.MapPost("/api/v1/session/logout", (HttpContext context, SessionStore sessions) =>
        {
            sessions.Revoke(context.Request.Cookies[SessionStore.CookieName]);
            context.Response.Cookies.Delete(SessionStore.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        });

        return app;
    }
}
