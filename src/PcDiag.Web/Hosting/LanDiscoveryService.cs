using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PcDiag.Web.Security;

namespace PcDiag.Web.Hosting;

/// <summary>
/// "Find my PC" for the companion app: while access from other devices is on, answers the UDP broadcast
/// <see cref="Question"/> from private-network senders with this PC's install ID, name, address, HTTPS port and
/// certificate fingerprint. The answer reveals nothing a paired device could not see; it only saves typing an
/// address. Runs only while listening on the network.
/// </summary>
internal sealed partial class LanDiscoveryService(LanState state, PairedDevices devices, ILogger<LanDiscoveryService> logger) : BackgroundService
{
    /// <summary>What the app broadcasts.</summary>
    public const string Question = "PCDIAG?";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The answer to a datagram, or null when it is not the question or the sender is not on a private network.</summary>
    public static byte[]? Answer(ReadOnlySpan<byte> request, IPAddress sender, IPAddress? local, LanState state, string installId)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(state);
        if (!state.Listening || !LanNetwork.IsPrivate(sender) || request.Length > 64 || !Encoding.ASCII.GetString(request).Trim().Equals(Question, StringComparison.Ordinal))
        {
            return null;
        }

        var host = local is { } l && !IPAddress.Any.Equals(l) ? l.ToString() : state.PrimaryHost;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            app = "pcdiagnostic",
            id = installId,
            name = Environment.MachineName,
            host,
            port = state.Port,
            fp = state.Fingerprint?.Replace(":", "", StringComparison.Ordinal),
        }, Json);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!state.Listening)
        {
            return;
        }

        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, state.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            LogUnavailable(logger, ex, state.DiscoveryPort);
            return;
        }

        using (udp)
        {
            LogListening(logger, state.DiscoveryPort);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var received = await udp.ReceiveAsync(stoppingToken).ConfigureAwait(false);
                    var answer = Answer(received.Buffer, received.RemoteEndPoint.Address, LocalAddressFor(received.RemoteEndPoint.Address), state, devices.InstallId);
                    if (answer is not null)
                    {
                        await udp.SendAsync(answer, received.RemoteEndPoint, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    LogReceiveFailed(logger, ex);
                }
            }
        }
    }

    /// <summary>The address of this PC on the network the sender is on (a connected UDP socket sends nothing).</summary>
    private static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(remote, 9));
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Answering \"Find my PC\" on UDP port {Port}")]
    private static partial void LogListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "\"Find my PC\" is not available: UDP port {Port} could not be opened")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, int port);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A \"Find my PC\" request could not be answered")]
    private static partial void LogReceiveFailed(ILogger logger, Exception exception);
}
