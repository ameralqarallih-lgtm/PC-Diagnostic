using System.Security.Principal;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Hosting;
using PcDiag.Web;
using PcDiag.Web.Agent;
using PcDiag.Web.Dashboard;
using PcDiag.Web.Endpoints;
using PcDiag.Web.Hosting;
using PcDiag.Web.Live;
using PcDiag.Web.Security;
using PcDiag.Web.Settings;

// Docker HEALTHCHECK entry point: probe the running host and exit 0 (healthy) or 1.
if (args is ["--healthcheck"])
{
    return await ContainerHealthCheck.RunAsync();
}

// The dashboard renders data and opens links; it never needs administrator rights, so it refuses to run with them
// (an elevated browser session would be a needless risk). Override only for development.
if (OperatingSystem.IsWindows() && IsElevated() && Environment.GetEnvironmentVariable("PCDIAG_ALLOW_ELEVATED_WEB") != "1")
{
    await Console.Error.WriteLineAsync("The PC Diagnostic web host does not run as administrator. Start it from a normal (non-elevated) session.");
    return 4;
}

// Changing network access needs new Kestrel endpoints, so the host is built and started again in this process;
// sessions and the loopback port carry over, so the open dashboard keeps working.
var restart = new WebHostRestart();
var sessionTable = new SessionTable();
var loopbackPort = 0;
var address = new AddressState();
var fellBack = false;
do
{
    restart.Clear();
    var builder = WebApplication.CreateBuilder(args);
    builder.AddPcDiagHost("web");

    builder.Services.AddOptions<WebOptions>().Bind(builder.Configuration.GetSection(WebOptions.SectionName)).ValidateOnStart();
    builder.Services.AddSingleton<IValidateOptions<WebOptions>, WebOptionsValidator>();
    builder.Services.AddSingleton(restart);
    builder.Services.AddSingleton(address);
    builder.Services.AddSingleton(sessionTable);
    builder.Services.AddSingleton<LaunchTokenService>();
    builder.Services.AddSingleton<SessionStore>();
    builder.Services.AddSingleton<PairedDevices>();
    builder.Services.AddSingleton<PairingCodes>();
    builder.Services.AddHostedService<WebStartupService>();
    builder.Services.AddSingleton<IAgentClient>(sp => new AgentPipeClient(
        AgentPipeClient.CurrentUserPipeName(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<AgentPipeClient>>()));
    builder.Services.AddSingleton<LiveBroadcaster>();
    builder.Services.AddSingleton<PcDiag.Storage.Files.FileStore>();
    builder.Services.AddSingleton(sp => new PcDiag.Storage.Files.LocalStoreSetup(
        sp.GetRequiredService<PcDiag.Storage.Files.FileStore>(), sp.GetRequiredService<PcDiag.Core.Configuration.DataPaths>().Root,
        () => new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, sp.GetRequiredService<ILogger<PcDiag.Storage.Files.LocalStoreSetup>>()));
    builder.Services.AddHostedService<PcDiag.Web.Storage.LocalStorageService>();
    builder.Services.AddHostedService<StoreKeeper>();
    builder.Services.AddSingleton<DashboardStore>();
    builder.Services.AddSingleton<SettingsStore>();
    builder.Services.AddSingleton<PcDiag.Web.Reports.PdfPrinter>();
    builder.Services.AddSingleton<PcDiag.Web.Diagnostics.DiagnosticJobs>();

    // Problem reports for the makers: sent to the report service of this build (Reporting:Endpoint), automatic ones
    // only with the user's consent.
    builder.Services.AddSingleton<PcDiag.Web.Reporting.ProblemReportService>();
    builder.Services.AddHostedService<PcDiag.Web.Reporting.ProblemReportSender>();
    builder.Services.AddHttpClient(PcDiag.Web.Characters.CharacterGeneration.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
    builder.Services.AddSingleton<PcDiag.Web.Characters.CharacterGeneration>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PcDiag.Web.Characters.CharacterGeneration>());
    builder.Services.AddSingleton<PcDiag.Web.Characters.CharacterAnimationService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PcDiag.Web.Characters.CharacterAnimationService>());
    builder.Services.AddHttpClient(PcDiag.Web.Reporting.ProblemReportService.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PcDiagnostic/{PcDiag.Hosting.ProductInfo.Version}");
    });
    builder.Services.AddHttpClient(PcDiag.Web.Characters.WeatherService.HttpClientName, client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"PcDiagnostic/{PcDiag.Hosting.ProductInfo.Version}");
    });
    builder.Services.AddSingleton<PcDiag.Web.Characters.WeatherService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PcDiag.Web.Characters.WeatherService>());
    builder.Services.AddProblemDetails();
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    // Access from other devices (opt-in): HTTPS on its own port, with this PC's names allowed as hosts.
    var (lanState, lanCertificate) = LanHosting.Prepare(builder.Configuration, new DataPaths(builder.Configuration["Storage:DataDirectory"]), TimeProvider.System);
    builder.Services.AddSingleton(lanState);
    builder.Services.AddHostedService<LanDiscoveryService>();
    LanHosting.AllowNetworkHosts(builder.Services, lanState);

    // Loopback for this PC. Inside a container, loopback is unreachable from the host, so all container interfaces
    // are used and compose publishes the port on the host's 127.0.0.1 only.
    // The same port every time (Web:Port, chosen in Settings); kept across restarts in this process, unless a new one
    // was chosen; another program holding it: any free one, and the Settings page says so.
    address.Wanted = builder.Configuration.GetValue<int>($"{WebOptions.SectionName}:{nameof(WebOptions.Port)}");
    var port = loopbackPort != 0 ? loopbackPort : fellBack ? 0 : address.Wanted;
    builder.WebHost.ConfigureKestrel((context, kestrel) =>
    {
        kestrel.AddServerHeader = false;
        kestrel.Listen(ListenAddress.Resolve(Environment.GetEnvironmentVariable), port);
        if (lanCertificate is not null)
        {
            kestrel.ListenAnyIP(lanState.Port, listen => listen.UseHttps(lanCertificate));
        }
    });

    var app = builder.Build();

    // Host filtering (AllowedHosts in appsettings.json, plus network names when that is on) runs first and rejects
    // DNS-rebinding requests with 400.
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseMiddleware<LanGuardMiddleware>();
    app.UseMiddleware<OriginCheckMiddleware>();
    app.UseMiddleware<SessionAuthMiddleware>();

    app.MapGet(Paths.Health.Value!, () => Results.Text("ok")).ExcludeFromDescription();
    app.MapAuthEndpoints();
    app.MapLanEndpoints();
    app.MapStatusEndpoints();
    app.MapControlEndpoints();
    app.MapDashboardEndpoints();
    app.MapOverlayEndpoints();
    app.MapOverlayImageEndpoints();
    app.MapOverlayCharacterEndpoints();
    app.MapStorageEndpoints();
    app.MapStorageLocationEndpoints();
    app.MapHardwareEndpoints();
    app.MapDriverEndpoints();
    app.MapHistoryEndpoints();
    app.MapExportEndpoints();
    app.MapSettingsEndpoints();
    app.MapDiagnosticsEndpoints();
    app.MapSystemControlEndpoints();
    app.MapReportEndpoints();
    app.UseStaticUi();

    try
    {
        var started = false;
        try
        {
            await app.StartAsync();
            started = true;
        }
        catch (IOException ex) when (port != 0 && !fellBack)
        {
            await Console.Error.WriteLineAsync($"Port {port} is not available ({ex.Message}); using a free one.");
            fellBack = true;
            address.Problem = $"Port {port} is used by another program, so a free one is used for now.";
            restart.Request();
        }

        if (started)
        {
            loopbackPort = WebStartupService.LoopbackPort(app.Services) ?? loopbackPort;
            lanState.LoopbackPort = loopbackPort;
            address.Listening = loopbackPort;
            if (!fellBack || loopbackPort == address.Wanted)
            {
                address.Problem = null;
            }

            await app.WaitForShutdownAsync();
            if (restart.NewAddress)
            {
                // The port chosen in Settings, tried afresh.
                loopbackPort = 0;
                fellBack = false;
            }
        }
    }
    catch (OptionsValidationException ex)
    {
        await Console.Error.WriteLineAsync("PC Diagnostic web host could not start because the configuration is invalid:");
        foreach (var failure in ex.Failures)
        {
            await Console.Error.WriteLineAsync("  - " + failure);
        }

        return 3;
    }
    finally
    {
        await app.DisposeAsync();
        lanCertificate?.Dispose();
    }
}
while (restart.Requested);

return 0;

static bool IsElevated()
{
    if (!OperatingSystem.IsWindows())
    {
        return false;
    }

    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

/// <summary>Entry point (public for integration tests).</summary>
public partial class Program;
