using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PcDiag.Web.Security;

/// <summary>Which requests come from this PC, which from the local network, and this PC's network names.</summary>
public static class LanNetwork
{
    /// <summary>A request from this PC (loopback). In-memory test servers report no address; they count as local.</summary>
    public static bool IsLocal(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress is not { } address || IPAddress.IsLoopback(address);
    }

    /// <summary>
    /// Whether an address belongs to a private network: 10/8, 172.16/12, 192.168/16, link-local 169.254/16, carrier
    /// grade NAT 100.64/10 (also used by mesh VPNs), and IPv6 unique-local fc00::/7 and link-local fe80::/10.
    /// Requests from anywhere else (a forwarded port, a public address) are refused.
    /// </summary>
    public static bool IsPrivate(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || address.IsIPv6LinkLocal;
        }

        return false;
    }

    /// <summary>This PC's addresses on networks that are up (no loopback, no tunnels).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (IsPrivate(address) && !IPAddress.IsLoopback(address) && !(address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv6LinkLocal))
                    {
                        addresses.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // No address list: only the host name can be used.
        }

        return addresses.Distinct().OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ThenBy(a => a.ToString(), StringComparer.Ordinal).ToList();
    }

    /// <summary>Names this PC answers to on the network: the computer name and its DNS name.</summary>
    public static IReadOnlyList<string> HostNames()
    {
        var names = new List<string> { Environment.MachineName.ToLowerInvariant() };
        try
        {
            var dns = Dns.GetHostName().ToLowerInvariant();
            names.Add(dns);
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrEmpty(domain))
            {
                names.Add($"{dns}.{domain.ToLowerInvariant()}");
            }
        }
        catch (SocketException)
        {
            // The computer name is enough.
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The Host values (with port) this PC accepts from the network.</summary>
    public static IEnumerable<string> AllowedHosts(int port) =>
        HostNames().Concat(LocalAddresses().Select(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString()))
            .Select(h => $"{h}:{port}");
}
