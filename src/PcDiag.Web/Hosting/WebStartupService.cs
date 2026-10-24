using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Web.Security;

namespace PcDiag.Web.Hosting;

/// <summary>
/// After Kestrel starts: records the bound port in web.json (so the Agent's tray can build launch links),
/// prints a one-time launch link to the console, and optionally opens the browser. Deletes web.json on shutdown.
/// </summary>
internal sealed partial class WebStartupService(
    IHostApplicationLifetime lifetime,
    IServer server,
    DataPaths paths,
    LaunchTokenService tokens,
    IOptions<WebOptions> options,
    TimeProvider timeProvider,
    ILogger<WebStartupService> logger) : IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(OnStarted);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        TryDelete(paths.WebStateFile);
        return Task.CompletedTask;
    }

    /// <summary>The loopback (or, in a container, the first) address Kestrel bound, or null on an in-memory server.</summary>
    public static int? LoopbackPort(IServiceProvider services) => LoopbackAddress(services.GetRequiredService<IServer>()) is { } uri ? uri.Port : null;

    private static Uri? LoopbackAddress(IServer server)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var uri) ? uri : null)
            .OfType<Uri>()
            .ToList() ?? [];

        // The network listener (when on) is HTTPS on all interfaces; this PC's dashboard is the plain loopback one.
        return addresses.FirstOrDefault(a => a.Scheme == Uri.UriSchemeHttp) ?? addresses.FirstOrDefault();
    }

    private void OnStarted()
    {
        // TestServer and other in-memory servers expose no addresses; nothing to publish then.
        if (LoopbackAddress(server) is not { } baseUri)
        {
            return;
        }

        if (!string.IsNullOrEmpty(options.Value.PublicBaseUrl))
        {
            baseUri = new Uri(options.Value.PublicBaseUrl);
        }

        WriteStateFile(baseUri);

        var launchUrl = new Uri(baseUri, "/auth/exchange?t=" + tokens.IssueToken());
        LogListening(logger, baseUri, options.Value.LaunchTokenLifetimeSeconds);

        // The launch link is a credential: print it to the console only, never to the log file.
        Console.WriteLine();
        Console.WriteLine($"  Open the dashboard (single use, valid {options.Value.LaunchTokenLifetimeSeconds} s):");
        Console.WriteLine($"  {launchUrl}");
        Console.WriteLine();

        if (options.Value.OpenBrowserOnStart)
        {
            OpenBrowser(launchUrl);
        }
    }

    private void WriteStateFile(Uri baseUri)
    {
        var state = new WebState(baseUri.Port, Environment.ProcessId, timeProvider.GetUtcNow(), baseUri.ToString());
        var temp = paths.WebStateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temp, paths.WebStateFile, overwrite: true);
    }

    private void OpenBrowser(Uri url)
    {
        // Only ever our own loopback URL, opened with the user's default browser.
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LogBrowserFailed(logger, ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a stale web.json is detected by its process ID.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dashboard available at {Address}; launch links are valid for {LifetimeSeconds} s")]
    private static partial void LogListening(ILogger logger, Uri address, int lifetimeSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not open the default browser")]
    private static partial void LogBrowserFailed(ILogger logger, Exception exception);
}

/// <summary>Contents of web.json.</summary>
internal sealed record WebState(int Port, int ProcessId, DateTimeOffset StartedAt, string BaseUrl);
