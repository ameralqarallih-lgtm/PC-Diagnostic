using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace PcDiag.Web.Hosting;

/// <summary>Serves the built React UI with SPA fallback so client-side routes (e.g. /incidents/INC-…) load index.html.</summary>
internal static class StaticUi
{
    private const string NotBuiltPage =
        """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><title>PC Diagnostic</title></head>
        <body style="font-family:system-ui,sans-serif;padding:16px">
        <h1>The dashboard UI has not been built</h1>
        <p>Run <code>npm run build</code> in the <code>web</code> folder, or build without <code>-p:SkipWebBuild=true</code>.</p>
        </body></html>
        """;

    public static WebApplication UseStaticUi(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;
        var root = string.IsNullOrEmpty(options.StaticRoot) ? Path.Combine(AppContext.BaseDirectory, "wwwroot") : options.StaticRoot;
        IFileProvider files = Directory.Exists(root) ? new PhysicalFileProvider(root) : new NullFileProvider();

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = ctx =>
            {
                // Vite fingerprints everything under /assets, so it can be cached forever; everything else is revalidated.
                ctx.Context.Response.Headers[HeaderNames.CacheControl] =
                    ctx.Context.Request.Path.StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase)
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        });

        app.MapFallback(async context =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
                || Path.HasExtension(path.Value))
            {
                await Results.NotFound().ExecuteAsync(context);
                return;
            }

            var index = files.GetFileInfo("index.html");
            if (!index.Exists)
            {
                await Results.Content(NotBuiltPage, "text/html; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(context);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            await context.Response.SendFileAsync(index, context.RequestAborted);
        });

        return app;
    }
}
