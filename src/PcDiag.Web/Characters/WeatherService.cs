using System.Globalization;
using System.Text.Json;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;

namespace PcDiag.Web.Characters;

/// <summary>
/// The characters' weather. While the layout names a place (<see cref="OverlayConfig.Weather"/>, off by default), MET
/// Norway's forecast for it (api.met.no locationforecast, free with attribution, commercial use allowed) is fetched at
/// most every <see cref="Every"/> and never before its own Expires, and written to weather.json, where the overlay
/// and the editor read it; without one nothing is fetched and weather.json is removed. Places are searched by name
/// with OpenStreetMap's Nominatim, only when the user asks, at most once a second (its usage policy).
/// </summary>
public sealed partial class WeatherService(DataPaths paths, IHttpClientFactory http, TimeProvider time, ILogger<WeatherService> logger) : BackgroundService
{
    /// <summary>The HTTP client's name (a short timeout, the app named in its User-Agent).</summary>
    public const string HttpClientName = "weather";

    /// <summary>The least time between two fetches for the same place.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    /// <summary>After a failed fetch, the next try waits this long.</summary>
    public static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _search = new(1, 1);
    private DateTimeOffset _lastSearch = DateTimeOffset.MinValue;
    private (DateTimeOffset At, OverlayWeather? Place) _failed = (DateTimeOffset.MinValue, null);

    private OverlayWeather? _trying;

    /// <summary>The weather now (null: none fetched, or none wanted).</summary>
    public WeatherReport? Now() => WeatherReport.Read(paths.Root);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException || (ex is TaskCanceledException && !stoppingToken.IsCancellationRequested))
            {
                _failed = (time.GetUtcNow(), _trying);
                LogFetchFailed(logger, ex.Message);
            }

            try
            {
                // The layout is looked at every 15 s: a place chosen and saved is fetched soon after.
                await Task.Delay(TimeSpan.FromSeconds(15), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Fetches the weather when the place wants it and it is due (a new place at once); removes it with no place.</summary>
    internal async Task RefreshAsync(CancellationToken ct)
    {
        var place = OverlayStyles.Read(paths.OverlayFile, out _).Weather;
        var file = Path.Combine(paths.Root, WeatherReport.FileName);
        if (place is null)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            return;
        }

        var now = time.GetUtcNow();
        var report = WeatherReport.Read(paths.Root);
        var same = report is not null && report.Place == place.Place && place.SameSpot(report.Latitude, report.Longitude);
        if (same && (now - report!.At < Every || (report.Expires is { } expires && now < expires)))
        {
            return;
        }

        // A place that just failed is tried again a while later (another one at once).
        if (now - _failed.At < AfterFailure && _failed.Place == place)
        {
            return;
        }

        _trying = place;
        var url = string.Create(CultureInfo.InvariantCulture, $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={Math.Round(place.Latitude, 4)}&lon={Math.Round(place.Longitude, 4)}");
        using var response = await http.CreateClient(HttpClientName).GetAsync(new Uri(url), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (WeatherReport.FromForecast(json, place, now, response.Content.Headers.Expires) is not { } fresh)
        {
            _failed = (now, place);
            LogFetchFailed(logger, "the forecast had no weather for now");
            return;
        }

        fresh.Write(paths.Root);
        LogFetched(logger, fresh.Place, fresh.Symbol);
    }

    /// <summary>Places by name (at most 6), named in <paramref name="language"/> when it is given (the browser's).</summary>
    public async Task<IReadOnlyList<OverlayWeather>> SearchAsync(string name, string? language, CancellationToken ct)
    {
        await _search.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // At most one search a second (Nominatim's usage policy).
            var wait = _lastSearch + TimeSpan.FromSeconds(1) - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, time, ct).ConfigureAwait(false);
            }

            _lastSearch = time.GetUtcNow();
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://nominatim.openstreetmap.org/search?format=jsonv2&limit=6&q=" + Uri.EscapeDataString(name)));
            if (!string.IsNullOrWhiteSpace(language))
            {
                request.Headers.TryAddWithoutValidation("Accept-Language", language);
            }

            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return Places(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            _search.Release();
        }
    }

    /// <summary>The places of Nominatim's answer (its display names, cut to 120 characters).</summary>
    internal static IReadOnlyList<OverlayWeather> Places(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var places = new List<OverlayWeather>();
        foreach (var p in document.RootElement.EnumerateArray())
        {
            if (p.TryGetProperty("display_name", out var n) && n.GetString() is { Length: > 0 } shown
                && p.TryGetProperty("lat", out var lat) && double.TryParse(lat.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                && p.TryGetProperty("lon", out var lon) && double.TryParse(lon.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
            {
                var place = new OverlayWeather(shown.Length > 120 ? shown[..119].TrimEnd() + "…" : shown, Math.Round(latitude, 4), Math.Round(longitude, 4));
                if (place.Check() is null)
                {
                    places.Add(place);
                }
            }
        }

        return places;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Weather fetched for {Place}: {Symbol}")]
    private static partial void LogFetched(ILogger logger, string place, string symbol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The weather could not be fetched: {Reason}")]
    private static partial void LogFetchFailed(ILogger logger, string reason);
}
