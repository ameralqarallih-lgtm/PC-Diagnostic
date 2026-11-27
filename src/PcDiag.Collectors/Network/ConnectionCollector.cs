using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Network;

namespace PcDiag.Collectors.Network;

/// <summary>
/// The connection's quality, measured on three hops once a second while the recorder runs: the router (the default
/// gateway: the home network, cable or Wi-Fi), the provider (the first hop after the router with a public address,
/// found with TTL-limited pings and looked for again every minute) and the internet (1.1.1.1). Each is an ICMP echo of
/// 16 bytes; nothing about the PC is sent. Every 10 s a DNS lookup is timed through the PC's DNS servers. The rounds run
/// on their own timer (the gaming readings show the round trips at once); each tick takes the newest round.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConnectionCollector(IOptionsMonitor<RecorderOptions> options, TimeProvider time)
    : IPolledCollector<ConnectionSample?>, ILatencyProbe, IHostedService, IDisposable
{
    /// <summary>The public address pinged for the internet round trip.</summary>
    public static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");

    /// <summary>How long a ping waits for its answer.</summary>
    public static readonly TimeSpan PingTimeout = TimeSpan.FromMilliseconds(900);

    /// <summary>How often the DNS lookup is timed.</summary>
    public static readonly TimeSpan DnsEvery = TimeSpan.FromSeconds(10);

    /// <summary>A DNS lookup that has not answered after this long counts as failed.</summary>
    public static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How often the provider hop is looked for again.</summary>
    public static readonly TimeSpan HopEvery = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private ITimer? _timer;
    private int _busy;
    private (PingResult Router, PingResult Provider, PingResult Internet, long Round) _latest;
    private long _taken;
    private IPAddress? _gateway;
    private IPAddress? _provider;
    private DateTimeOffset _hopAt = DateTimeOffset.MinValue;
    private Task<double?>? _dns;
    private DateTimeOffset _dnsStarted;
    private DateTimeOffset _dnsAt = DateTimeOffset.MinValue;
    private (double? Ms, bool Failed)? _dnsResult;

    /// <inheritdoc />
    public string Id => "connection";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Connection quality", RequiredPrivilege.StandardUser, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken)
    {
        Start();
        return ValueTask.FromResult(new CollectorStatus(Availability.Available, "router, provider and internet pings, DNS every 10 s"));
    }

    /// <inheritdoc />
    public ValueTask<ConnectionSample?> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!options.CurrentValue.ConnectionChecks)
        {
            return ValueTask.FromResult<ConnectionSample?>(null);
        }

        lock (_lock)
        {
            // The newest round, once; a tick with no new round (one is still waiting for its answers) has none.
            var fresh = _latest.Round > _taken;
            _taken = _latest.Round;
            var dns = TakeDns(context.Timestamp);
            if (!fresh && dns is null)
            {
                return ValueTask.FromResult<ConnectionSample?>(null);
            }

            var (router, provider, internet, _) = fresh ? _latest : default;
            return ValueTask.FromResult<ConnectionSample?>(new ConnectionSample(context.Timestamp, router, provider, internet, dns?.Ms, dns?.Failed, _provider?.ToString()));
        }
    }

    /// <inheritdoc />
    public LatencyStats Current()
    {
        lock (_lock)
        {
            return new LatencyStats(_latest.Router.Ms, _latest.Internet.Ms);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Start()
    {
        lock (_lock)
        {
            _timer ??= time.CreateTimer(_ => _ = RoundAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>The DNS result finished since the last tick (or a lookup that timed out), once; starts the next lookup when due.</summary>
    private (double? Ms, bool Failed)? TakeDns(DateTimeOffset now)
    {
        if (_dns is { IsCompleted: true } done)
        {
            _dnsResult = done.IsCompletedSuccessfully && done.Result is { } ms && ms <= DnsTimeout.TotalMilliseconds ? (ms, false) : (null, true);
            _dns = null;
        }
        else if (_dns is not null && now - _dnsStarted > DnsTimeout)
        {
            // Still waiting: failed (its late answer is ignored).
            _dnsResult = (null, true);
            _dns = null;
        }

        if (_dns is null && now - _dnsAt >= DnsEvery)
        {
            _dnsAt = now;
            _dnsStarted = now;
            _dns = Task.Run(DnsCheck.Lookup);
        }

        var result = _dnsResult;
        _dnsResult = null;
        return result;
    }

    private async Task RoundAsync()
    {
        // One round at a time (a ping waits up to a second).
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var gateway = Gateway();
            var now = time.GetUtcNow();
            if (options.CurrentValue.ConnectionChecks && gateway is not null && (now - _hopAt >= HopEvery || !Equals(gateway, _gateway)))
            {
                _hopAt = now;
                _provider = await ProviderHopAsync(gateway).ConfigureAwait(false);
            }

            _gateway = gateway;
            var provider = options.CurrentValue.ConnectionChecks ? _provider : null;

            // All at once: one hop's answer does not wait for another's.
            var router = gateway is null ? Task.FromResult(PingResult.None) : PingAsync(gateway);
            var hop = provider is null ? Task.FromResult(PingResult.None) : PingAsync(provider);
            var internet = PingAsync(Internet);
            var results = (await router.ConfigureAwait(false), await hop.ConfigureAwait(false), await internet.ConfigureAwait(false));
            lock (_lock)
            {
                _latest = (results.Item1, results.Item2, results.Item3, _latest.Round + 1);
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>
    /// The first hop after the router toward the internet with a public address (the provider's equipment; a modem in
    /// between is passed), looking up to 4 hops away; the second hop when none of them has one; null when nothing answers.
    /// </summary>
    private static async Task<IPAddress?> ProviderHopAsync(IPAddress gateway)
    {
        IPAddress? second = null;
        for (var ttl = 2; ttl <= 4; ttl++)
        {
            if (await HopAsync(ttl).ConfigureAwait(false) is not { } address || address.Equals(gateway) || address.Equals(Internet))
            {
                continue;
            }

            second ??= address;
            if (!IsPrivate(address))
            {
                return address;
            }
        }

        return second;
    }

    private static async Task<IPAddress?> HopAsync(int ttl)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(Internet, PingTimeout, new byte[16], new PingOptions(ttl, dontFragment: true)).ConfigureAwait(false);
            return reply.Status == IPStatus.TtlExpired && reply.Address is { } address && !address.Equals(IPAddress.Any) ? address : null;
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A private address (RFC 1918, link-local): home equipment, not the provider's (100.64.0.0/10, carrier NAT, is the provider's).</summary>
    internal static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private static async Task<PingResult> PingAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var started = Stopwatch.GetTimestamp();
            var reply = await ping.SendPingAsync(address, PingTimeout, new byte[16]).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success)
            {
                return new PingResult(true, null);
            }

            // Windows reports whole milliseconds, so a router on the LAN reads 0: below 1 ms, the time measured here
            // (which includes a little of this process's own time) says how far below.
            return new PingResult(true, reply.RoundtripTime > 0 ? reply.RoundtripTime : Math.Round(Math.Min(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0.9), 2));
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            // No route at all (the adapter is down): the ping could not go, which is lost too.
            return new PingResult(true, null);
        }
    }

    /// <summary>The IPv4 default gateway of the first adapter that is up and has one.</summary>
    internal static IPAddress? Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
