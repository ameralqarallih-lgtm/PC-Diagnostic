using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PcDiag.Hosting;
using PcDiag.Web.Settings;

namespace PcDiag.Web.Updates;

/// <summary>Where newer versions are announced ("Updates" section of the Web host's appsettings).</summary>
public sealed class UpdateOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Updates";

    /// <summary>
    /// The latest-release address (GitHub's API, e.g. https://api.github.com/repos/owner/repo/releases/latest). Empty: this build
    /// never checks.
    /// </summary>
    public string? Releases { get; set; }
}

/// <summary>What is known about a newer version (<c>GET /api/v1/updates</c>).</summary>
/// <param name="Available">This build has somewhere to ask (the open-source build).</param>
/// <param name="Enabled">The user lets it ask (Settings › Privacy).</param>
/// <param name="Current">The version running.</param>
/// <param name="Latest">The latest release's version, once asked.</param>
/// <param name="Newer">The latest release is newer than the version running.</param>
/// <param name="Page">The release's page (what changed).</param>
/// <param name="Download">The installer's address.</param>
/// <param name="Sha256">The installer's SHA-256 as the release notes give it, to check the download against.</param>
/// <param name="CheckedAt">When it last asked.</param>
/// <param name="Problem">Why the last check failed, if it did.</param>
public sealed record UpdateStatus(
    bool Available,
    bool Enabled,
    string Current,
    string? Latest = null,
    bool Newer = false,
    string? Page = null,
    string? Download = null,
    string? Sha256 = null,
    DateTimeOffset? PublishedAt = null,
    DateTimeOffset? CheckedAt = null,
    string? Problem = null);

/// <summary>
/// Tells the user when a newer version is out. Nothing told users so far: on GitHub (Nov 2026) the first release had the most
/// downloads and the latest two none, so fixes reached nobody who had installed it. Asks the release address a minute after
/// the start and then once a day, only while Settings › Privacy allows it; it only shows the release's page and installer,
/// both on github.com, and installs nothing itself (the installer is not code-signed yet).
/// </summary>
public sealed partial class UpdateCheck(IOptions<UpdateOptions> options, SettingsStore settings, IHttpClientFactory http, TimeProvider time, ILogger<UpdateCheck> logger)
    : BackgroundService
{
    /// <summary>The HTTP client's name (a short timeout, the app named in its User-Agent).</summary>
    public const string HttpClientName = "updates";

    /// <summary>How often it asks.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>Asking again by hand waits at least this long after the last time.</summary>
    public static readonly TimeSpan AtMostEvery = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan FirstAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan AfterFailure = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateStatus? _last;

    /// <summary>What is known now (without asking).</summary>
    public UpdateStatus Status => Shape(_last);

    /// <summary>Asks now, unless it asked within <see cref="AtMostEvery"/>; returns what is known.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Configured || !Allowed)
        {
            return Status;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_last?.CheckedAt is { } at && time.GetUtcNow() - at < AtMostEvery)
            {
                return Status;
            }

            _last = await AskAsync(cancellationToken).ConfigureAwait(false);
            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The version part of a tag or version ("v1.0.10", "1.0.10+abc"); null when it is not one. Exposed for tests.</summary>
    internal static Version? VersionOf(string? text)
    {
        var match = text is null ? null : VersionPattern().Match(text);
        return match is { Success: true } && Version.TryParse(match.Value, out var version) ? version : null;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Configured)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstAfter, time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                var status = await CheckAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(status.Problem is null ? Every : AfterFailure, time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private bool Configured => Uri.TryCreate(options.Value.Releases, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private bool Allowed => settings.Load().Privacy.CheckForUpdates;

    private UpdateStatus Shape(UpdateStatus? last) =>
        (last ?? new UpdateStatus(Configured, Allowed, ProductInfo.Version)) with { Available = Configured, Enabled = Allowed, Current = ProductInfo.Version };

    private async Task<UpdateStatus> AskAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        try
        {
            using var client = http.CreateClient(HttpClientName);
            var release = await client.GetFromJsonAsync(options.Value.Releases, ReleaseJson.Default.Release, cancellationToken).ConfigureAwait(false);
            if (release is null || VersionOf(release.TagName) is not { } latest)
            {
                return new UpdateStatus(true, true, ProductInfo.Version, CheckedAt: now, Problem: "the answer named no version");
            }

            var newer = VersionOf(ProductInfo.Version) is { } current && latest > current;
            var installer = release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && OnGitHub(a.Download));
            var sha = release.Body is { } body && ShaPattern().Match(body) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;
            if (newer)
            {
                LogNewer(logger, latest.ToString(), ProductInfo.Version);
            }

            return new UpdateStatus(true, true, ProductInfo.Version, latest.ToString(), newer, OnGitHub(release.Page) ? release.Page : null, installer?.Download, sha,
                release.PublishedAt, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFailed(logger, ex.Message);
            return (_last ?? new UpdateStatus(true, true, ProductInfo.Version)) with { CheckedAt = now, Problem = ex is TaskCanceledException ? "no answer in time" : ex.Message };
        }
    }

    /// <summary>Only addresses on github.com are shown: the page links to them.</summary>
    private static bool OnGitHub(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\d+\.\d+(\.\d+){0,2}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"SHA-256:\s*`?([0-9A-Fa-f]{64})`?", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();

    [LoggerMessage(Level = LogLevel.Information, Message = "PC Diagnostic {Latest} is available (this is {Current})")]
    private static partial void LogNewer(ILogger logger, string latest, string current);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not check for a newer version: {Problem}")]
    private static partial void LogFailed(ILogger logger, string problem);
}

/// <summary>The parts of a GitHub release this reads.</summary>
internal sealed record Release(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("html_url")] string? Page,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] IReadOnlyList<ReleaseAsset>? Assets);

/// <summary>A file of a GitHub release.</summary>
internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string? Download);

[JsonSerializable(typeof(Release))]
internal sealed partial class ReleaseJson : JsonSerializerContext;

/// <summary>The update endpoints.</summary>
internal static class UpdateEndpoints
{
    public static IEndpointRouteBuilder MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/updates", (UpdateCheck updates) => updates.Status);
        app.MapPost("/api/v1/updates/check", (UpdateCheck updates, CancellationToken ct) => updates.CheckAsync(ct));
        return app;
    }
}
