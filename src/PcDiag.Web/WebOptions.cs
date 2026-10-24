using Microsoft.Extensions.Options;

namespace PcDiag.Web;

/// <summary>Web host settings ("Web" section).</summary>
public sealed class WebOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Web";

    /// <summary>Loopback port; 0 picks a free port at startup (recorded in web.json).</summary>
    public int Port { get; set; }

    /// <summary>How long a launch link stays valid, in seconds (10–86400). Each link works once.</summary>
    public int LaunchTokenLifetimeSeconds { get; set; } = 120;

    /// <summary>A session ends after this many hours without requests (1–168).</summary>
    public int SessionIdleHours { get; set; } = 12;

    /// <summary>
    /// Extra origins allowed to make state-changing requests, for the Vite dev server only
    /// (e.g. "http://localhost:5173"). Leave empty in production.
    /// </summary>
    public IList<string> AllowedDevOrigins { get; init; } = [];

    /// <summary>Folder containing the built UI; defaults to <c>wwwroot</c> next to the executable.</summary>
    public string? StaticRoot { get; set; }

    /// <summary>
    /// Base URL used in printed launch links when it differs from the bound address, e.g. "http://localhost:5180"
    /// when running in Docker with the port published on the host. Must be a loopback URL.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Open the dashboard in the default browser when the host starts.</summary>
    public bool OpenBrowserOnStart { get; set; }

    /// <summary>Access from other devices on the local network (off by default).</summary>
    public LanOptions Lan { get; init; } = new();
}

/// <summary>
/// Opt-in access from other devices on the local network ("Web:Lan"): HTTPS on a fixed port with a self-signed
/// certificate, private-network clients only, and each device paired once with a code shown on this PC.
/// </summary>
public sealed class LanOptions
{
    /// <summary>Default network port.</summary>
    public const int DefaultPort = 47443;

    /// <summary>Default UDP port for the companion app's "Find my PC".</summary>
    public const int DefaultDiscoveryPort = 47444;

    /// <summary>Whether the dashboard also listens on the local network.</summary>
    public bool Enabled { get; set; }

    /// <summary>The network (HTTPS) port (1024–65535).</summary>
    public int Port { get; set; } = DefaultPort;

    /// <summary>UDP port the companion app's "Find my PC" broadcast is answered on (1024–65535).</summary>
    public int DiscoveryPort { get; set; } = DefaultDiscoveryPort;

    /// <summary>How long a paired device stays signed in without being used, in days (1–365).</summary>
    public int DeviceIdleDays { get; set; } = 90;
}

/// <summary>Validates <see cref="WebOptions"/>.</summary>
internal sealed class WebOptionsValidator : IValidateOptions<WebOptions>
{
    public ValidateOptionsResult Validate(string? name, WebOptions options)
    {
        var errors = new List<string>();

        if (options.Port is < 0 or > 65535)
        {
            errors.Add($"Web:Port must be between 0 and 65535 (was {options.Port}).");
        }

        if (options.LaunchTokenLifetimeSeconds is < 10 or > 86_400)
        {
            errors.Add($"Web:LaunchTokenLifetimeSeconds must be between 10 and 86400 (was {options.LaunchTokenLifetimeSeconds}).");
        }

        if (options.SessionIdleHours is < 1 or > 168)
        {
            errors.Add($"Web:SessionIdleHours must be between 1 and 168 (was {options.SessionIdleHours}).");
        }

        foreach (var origin in options.AllowedDevOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.AbsolutePath != "/")
            {
                errors.Add($"Web:AllowedDevOrigins entries must be loopback origins such as http://localhost:5173 (was '{origin}').");
            }
        }

        if (options.PublicBaseUrl is { Length: > 0 } publicUrl
            && (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var publicUri) || !publicUri.IsLoopback
                || (publicUri.Scheme != Uri.UriSchemeHttp && publicUri.Scheme != Uri.UriSchemeHttps)))
        {
            errors.Add($"Web:PublicBaseUrl must be an http(s) loopback URL such as http://localhost:5180 (was '{publicUrl}').");
        }

        if (options.Lan.Port is < 1024 or > 65535)
        {
            errors.Add($"Web:Lan:Port must be between 1024 and 65535 (was {options.Lan.Port}).");
        }
        else if (options.Lan.Port == options.Port)
        {
            errors.Add($"Web:Lan:Port must differ from Web:Port ({options.Port}).");
        }

        if (options.Lan.DiscoveryPort is < 1024 or > 65535)
        {
            errors.Add($"Web:Lan:DiscoveryPort must be between 1024 and 65535 (was {options.Lan.DiscoveryPort}).");
        }

        if (options.Lan.DeviceIdleDays is < 1 or > 365)
        {
            errors.Add($"Web:Lan:DeviceIdleDays must be between 1 and 365 (was {options.Lan.DeviceIdleDays}).");
        }

        if (options.StaticRoot is { Length: > 0 } root && !Path.IsPathFullyQualified(root))
        {
            errors.Add($"Web:StaticRoot must be a fully qualified path (was '{root}').");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
