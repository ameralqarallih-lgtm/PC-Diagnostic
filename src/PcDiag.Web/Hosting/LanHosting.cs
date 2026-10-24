using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.HostFiltering;
using PcDiag.Core.Configuration;
using PcDiag.Web.Endpoints;
using PcDiag.Web.Security;

namespace PcDiag.Web.Hosting;

/// <summary>Asks Program to build and start the host again in the same process (sessions and the loopback port are kept).</summary>
public sealed class WebHostRestart
{
    private volatile bool _requested;

    /// <summary>Whether a restart was asked for.</summary>
    public bool Requested => _requested;

    /// <summary>Asks for a restart (the caller then stops the application).</summary>
    public void Request() => _requested = true;

    /// <summary>Clears the request before a new start.</summary>
    public void Clear()
    {
        _requested = false;
        NewAddress = false;
    }

    /// <summary>The restart is for a new address on this PC: the port chosen in the settings, not the one in use.</summary>
    public bool NewAddress { get; set; }
}

/// <summary>The dashboard's address on this PC: the port chosen, the one listened on, and why they differ.</summary>
public sealed class AddressState
{
    /// <summary>The port chosen (Web:Port; 0: any free one).</summary>
    public int Wanted { get; set; }

    /// <summary>The port listened on.</summary>
    public int Listening { get; set; }

    /// <summary>Why it is not the one chosen (another program had it), or null.</summary>
    public string? Problem { get; set; }
}

/// <summary>What the host decided about network access when it started.</summary>
public sealed class LanState
{
    /// <summary>Whether network access can be used here.</summary>
    public bool Supported { get; init; }

    /// <summary>Why not.</summary>
    public string? Unsupported { get; init; }

    /// <summary>Turned on in the settings.</summary>
    public bool Enabled { get; init; }

    /// <summary>Listening on the network now.</summary>
    public bool Listening { get; init; }

    /// <summary>The network port.</summary>
    public int Port { get; init; } = LanOptions.DefaultPort;

    /// <summary>Addresses other devices can open.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>The certificate fingerprint.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>Why it is not listening although turned on.</summary>
    public string? Problem { get; init; }

    /// <summary>The loopback port (set once Kestrel has started).</summary>
    public int LoopbackPort { get; set; }

    /// <summary>UDP port of "Find my PC".</summary>
    public int DiscoveryPort { get; init; } = LanOptions.DefaultDiscoveryPort;

    /// <summary>The address the pairing link points the app at (the first private IPv4 address).</summary>
    public string? PrimaryHost { get; init; }

    /// <summary>The status for the Settings page.</summary>
    public LanStatus Status(IReadOnlyList<PairedDevice> devices, (string Code, DateTimeOffset ExpiresAt)? pairing, string installId, string? apkUrl) =>
        new(Supported, Unsupported, Enabled, Listening, Port, Urls, Fingerprint, Problem, devices,
            pairing is { } p ? new PairingCode(p.Code, p.ExpiresAt, PairingLink(p.Code, installId)) : null, installId, apkUrl, DiscoveryPort);

    /// <summary>
    /// The companion app's pairing link: pcdiag://pair?host=&amp;port=&amp;code=&amp;id=&amp;fp=. The fingerprint lets the
    /// app trust this PC's self-signed certificate (and only it) from the first connection.
    /// </summary>
    public string? PairingLink(string code, string installId) =>
        Listening && PrimaryHost is { } host && Fingerprint is { } fp
            ? "pcdiag://pair?" + string.Join('&',
                $"host={Uri.EscapeDataString(host)}",
                $"port={Port}",
                $"code={code.Replace("-", "", StringComparison.Ordinal)}",
                $"id={installId}",
                $"fp={fp.Replace(":", "", StringComparison.Ordinal)}")
            : null;
}

/// <summary>Sets up the network listener when access from other devices is turned on.</summary>
internal static partial class LanHosting
{
    /// <summary>
    /// Decides whether to listen on the network: only on Windows outside a container, and only when turned on. Creates
    /// or loads the certificate; any failure leaves the dashboard working on this PC and reports the problem.
    /// </summary>
    public static (LanState State, System.Security.Cryptography.X509Certificates.X509Certificate2? Certificate) Prepare(IConfiguration configuration, DataPaths paths, TimeProvider time)
    {
        var options = configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();
        var lan = options.Lan;
        if (!OperatingSystem.IsWindows())
        {
            return (new LanState { Supported = false, Unsupported = "Access from other devices is available on Windows only.", Enabled = lan.Enabled, Port = lan.Port }, null);
        }

        if (ListenAddress.Resolve(Environment.GetEnvironmentVariable).Equals(IPAddress.Any))
        {
            return (new LanState { Supported = false, Unsupported = "In a container, publish the port with Docker instead.", Enabled = lan.Enabled, Port = lan.Port }, null);
        }

        if (!lan.Enabled)
        {
            return (new LanState { Supported = true, Enabled = false, Port = lan.Port }, null);
        }

        var names = LanNetwork.HostNames();
        var addresses = LanNetwork.LocalAddresses();
        try
        {
            var certificate = LanCertificate.GetOrCreate(paths, names, addresses, time.GetUtcNow());
            var urls = addresses.Select(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"https://[{a}]:{lan.Port}/" : $"https://{a}:{lan.Port}/")
                .Concat(names.Take(1).Select(n => $"https://{n}:{lan.Port}/"))
                .ToList();
            var primary = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString();
            return (new LanState
            {
                Supported = true, Enabled = true, Listening = true, Port = lan.Port, Urls = urls, Fingerprint = LanCertificate.Fingerprint(certificate),
                DiscoveryPort = lan.DiscoveryPort, PrimaryHost = primary,
            }, certificate);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return (new LanState { Supported = true, Enabled = true, Port = lan.Port, Problem = $"The HTTPS certificate could not be prepared: {ex.Message}" }, null);
        }
    }

    /// <summary>Accepts this PC's network names and addresses in the Host header while listening on the network.</summary>
    public static void AllowNetworkHosts(IServiceCollection services, LanState state)
    {
        if (!state.Listening)
        {
            return;
        }

        services.PostConfigure<HostFilteringOptions>(options =>
        {
            var hosts = LanNetwork.HostNames()
                .Concat(LanNetwork.LocalAddresses().Select(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString()));
            foreach (var host in hosts)
            {
                if (!options.AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    options.AllowedHosts.Add(host);
                }
            }
        });
    }
}
