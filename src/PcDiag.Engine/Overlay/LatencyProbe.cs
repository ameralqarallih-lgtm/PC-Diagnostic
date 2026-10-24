using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using PcDiag.Core.Overlay;

namespace PcDiag.Engine.Overlay;

/// <summary>
/// Pings the router (the default gateway of the adapter in use) and the internet (1.1.1.1) once a second while the
/// recorder runs, so the round trips are there wherever they are shown, at once: an echo of 16 bytes each, nothing
/// about the PC is sent.
/// </summary>
public sealed class LatencyProbe(TimeProvider time) : ILatencyProbe, IHostedService, IDisposable
{
    /// <summary>The public address pinged for the internet round trip.</summary>
    public static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");

    private readonly Lock _lock = new();
    private ITimer? _timer;
    private double? _router;
    private double? _internet;
    private int _busy;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _timer ??= time.CreateTimer(_ => _ = TickAsync(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public LatencyStats Current()
    {
        lock (_lock)
        {
            return new LatencyStats(_router, _internet);
        }
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

    private async Task TickAsync()
    {
        // One round at a time (a ping waits up to a second).
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            // Both at once: the router's answer does not wait for the internet's.
            var gateway = Gateway();
            var router = gateway is null ? Task.FromResult<double?>(null) : PingAsync(gateway);
            var internet = PingAsync(Internet);
            var routerMs = await router.ConfigureAwait(false);
            var internetMs = await internet.ConfigureAwait(false);
            lock (_lock)
            {
                _router = routerMs;
                _internet = internetMs;
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static async Task<double?> PingAsync(IPAddress address)
    {
        try
        {
            using var ping = new Ping();
            var started = Stopwatch.GetTimestamp();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(900), new byte[16]).ConfigureAwait(false);
            if (reply.Status != IPStatus.Success)
            {
                return null;
            }

            // Windows reports whole milliseconds, so a router on the LAN reads 0: below 1 ms, the time measured here
            // (which includes a little of this process's own time) says how far below.
            return reply.RoundtripTime > 0 ? reply.RoundtripTime : Math.Min(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0.9);
        }
        catch (Exception ex) when (ex is PingException or SocketException or InvalidOperationException)
        {
            return null;
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
