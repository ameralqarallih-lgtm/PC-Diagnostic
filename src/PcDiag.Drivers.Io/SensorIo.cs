using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using PcDiag.Platform.Windows.Usb;
using PcDiag.Screens;

namespace PcDiag.Drivers.Io;

/// <summary>
/// What the screen host gives sensor drivers: GETs to this PC and the local network only (the address is checked when
/// the connection is made, so a name that later points elsewhere is refused too; no redirects), serial ports' lines and
/// HID devices' input reports.
/// </summary>
public sealed class SensorIo : ISensorIo, IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectLocalAsync,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <inheritdoc />
    public byte[] Fetch(Uri url, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            return Read(url, maxBytes, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"{url.Host} did not answer within {timeout.TotalSeconds:0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new IOException($"{url.Host} could not be reached: {ex.Message}", ex);
        }
    }

    private byte[] Read(Uri url, int maxBytes, CancellationToken token)
    {
        using var response = _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            throw new IOException($"The device answered {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new IOException("The device's answer is too long.");
        }

        using var stream = response.Content.ReadAsStream(token);
        var buffer = new byte[maxBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = stream.ReadAsync(buffer.AsMemory(read), token).AsTask().GetAwaiter().GetResult()) > 0)
        {
            read += n;
        }

        return read > maxBytes ? throw new IOException("The device's answer is too long.") : buffer[..read];
    }

    /// <inheritdoc />
    public ISerialLines OpenSerial(string port, int baud) => new SerialLines(port, baud);

    /// <inheritdoc />
    public IHidReports? OpenHid(int vendorId, int productId) => HidDevice.Open(vendorId, productId) is { } device ? new HidReports(device) : null;

    /// <summary>Closes its connections.</summary>
    public void Dispose() => _http.Dispose();

    /// <summary>Whether an address is this PC's or the local network's (private, link-local, unique local).</summary>
    public static bool IsLocal(IPAddress address)
    {
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
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal);
    }

    /// <summary>Connects only to a local address: every address the name has must be local.</summary>
    private static async ValueTask<Stream> ConnectLocalAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0 || !addresses.All(IsLocal))
        {
            throw new HttpRequestException($"{host} is not on this PC or the local network.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            if (socket.RemoteEndPoint is IPEndPoint connected && !IsLocal(connected.Address))
            {
                throw new HttpRequestException($"{host} is not on this PC or the local network.");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>A serial port's lines, read as they come (the last 256 kept until taken).</summary>
    private sealed class SerialLines : ISerialLines
    {
        private readonly SerialPort _port;
        private readonly Queue<string> _lines = new();

        public SerialLines(string port, int baud)
        {
            _port = new SerialPort(port, baud) { NewLine = "\n", ReadTimeout = 500, Encoding = System.Text.Encoding.ASCII };
            _port.DataReceived += (_, _) => Drain();
            _port.Open();
        }

        public IReadOnlyList<string> Take()
        {
            lock (_lines)
            {
                var taken = _lines.ToList();
                _lines.Clear();
                return taken;
            }
        }

        public void Dispose() => _port.Dispose();

        private void Drain()
        {
            try
            {
                while (_port.IsOpen && _port.BytesToRead > 0)
                {
                    var line = _port.ReadLine().TrimEnd('\r');
                    lock (_lines)
                    {
                        _lines.Enqueue(line.Length > 512 ? line[..512] : line);
                        while (_lines.Count > 256)
                        {
                            _lines.Dequeue();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
            {
                // A line not finished yet comes with the next data.
            }
        }
    }

    private sealed class HidReports(HidDevice device) : IHidReports
    {
        public byte[]? Read(TimeSpan timeout) => device.Read(timeout);

        public void Dispose() => device.Dispose();
    }
}
