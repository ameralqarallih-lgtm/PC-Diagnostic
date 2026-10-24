using System.Net;

namespace PcDiag.Web.Hosting;

/// <summary>Chooses the Kestrel listen address.</summary>
internal static class ListenAddress
{
    /// <summary>Set to "true" by the official .NET container images.</summary>
    public const string ContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    /// <summary>
    /// Loopback on a normal install. Inside a container, loopback cannot be reached through a published port,
    /// so every container interface is used; the host side of the port mapping must be bound to 127.0.0.1.
    /// </summary>
    public static IPAddress Resolve(Func<string, string?> getEnvironmentVariable) =>
        string.Equals(getEnvironmentVariable(ContainerVariable), "true", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Any
            : IPAddress.Loopback;
}

/// <summary>
/// Docker HEALTHCHECK probe (<c>PcDiag.Web --healthcheck</c>). The runtime image has no curl, so the
/// executable probes its own /healthz over loopback.
/// </summary>
internal static class ContainerHealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("Web__Port");
        if (string.IsNullOrEmpty(port) || !int.TryParse(port, out var portNumber) || portNumber is <= 0 or > 65535)
        {
            portNumber = 8080;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{portNumber}/healthz"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
