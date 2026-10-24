using System.Globalization;
using System.Text.Json;

namespace PcDiag.Core.Overlay;

/// <summary>
/// Where the characters' weather is taken from (off without one): a place the user chose, searched by name. The Web
/// host fetches its weather (<see cref="WeatherReport"/>) and the characters show it now and then.
/// </summary>
/// <param name="Place">Its name as shown ("Amman, Jordan").</param>
/// <param name="Latitude">-90 to 90.</param>
/// <param name="Longitude">-180 to 180.</param>
public sealed record OverlayWeather(string Place, double Latitude, double Longitude)
{
    /// <summary>What is wrong with it; null when nothing is.</summary>
    public string? Check() =>
        string.IsNullOrWhiteSpace(Place) || Place.Length > 120 || Place.Any(char.IsControl) ? "weather.place is 1-120 characters."
        : !double.IsFinite(Latitude) || Latitude is < -90 or > 90 || !double.IsFinite(Longitude) || Longitude is < -180 or > 180 ? "weather's latitude is -90 to 90 and its longitude -180 to 180."
        : null;

    /// <summary>Whether it is the same spot as another (to 4 decimals, as the forecast takes it).</summary>
    public bool SameSpot(double latitude, double longitude) => Math.Round(Latitude, 4) == Math.Round(latitude, 4) && Math.Round(Longitude, 4) == Math.Round(longitude, 4);
}

/// <summary>
/// The weather now at the chosen place (weather.json in the data folder, written by the Web host from MET Norway's
/// forecast, read by the overlay as animations.json is).
/// </summary>
/// <param name="At">When it was fetched.</param>
/// <param name="Place">The place's name.</param>
/// <param name="Symbol">MET Norway's symbol for the next hour ("rain", "clearsky_day", "lightsnowshowers_night").</param>
/// <param name="Temperature">Air temperature, °C.</param>
/// <param name="Wind">Wind speed, m/s.</param>
/// <param name="Expires">Not fetched again before this (the forecast's own Expires).</param>
public sealed record WeatherReport(DateTimeOffset At, string Place, double Latitude, double Longitude, string Symbol, double Temperature, double Wind, DateTimeOffset? Expires = null)
{
    /// <summary>The file in the data folder.</summary>
    public const string FileName = "weather.json";

    /// <summary>Older than this, characters no longer show it.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromHours(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>clear, clouds, fog, rain, snow or storm.</summary>
    public string Kind => KindOf(Symbol);

    /// <summary>Daytime there (its symbol is not a night one).</summary>
    public bool Day => !Symbol.EndsWith("_night", StringComparison.Ordinal);

    /// <summary>The kind of weather for a MET Norway symbol: thunder a storm, snow snow, rain or sleet rain, fog fog, clear sky or fair clear, others clouds.</summary>
    public static string KindOf(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.Contains("thunder", StringComparison.Ordinal) ? "storm"
            : symbol.Contains("snow", StringComparison.Ordinal) ? "snow"
            : symbol.Contains("rain", StringComparison.Ordinal) || symbol.Contains("sleet", StringComparison.Ordinal) ? "rain"
            : symbol.StartsWith("fog", StringComparison.Ordinal) ? "fog"
            : symbol.StartsWith("clearsky", StringComparison.Ordinal) || symbol.StartsWith("fair", StringComparison.Ordinal) ? "clear"
            : "clouds";
    }

    /// <summary>The weather for the characters at <paramref name="now"/> (null when it is older than <see cref="ShownFor"/>).</summary>
    public CharacterWeather? ForCharacters(DateTimeOffset now) =>
        now - At > ShownFor ? null : new CharacterWeather(At.ToUnixTimeSeconds(), Kind, Temperature, Wind, Day);

    /// <summary>The report in a data folder (null: none, or not readable).</summary>
    public static WeatherReport? Read(string root)
    {
        try
        {
            return JsonSerializer.Deserialize<WeatherReport>(File.ReadAllText(Path.Combine(root, FileName)), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Writes it to a data folder (whole, through a file beside it).</summary>
    public void Write(string root)
    {
        var file = Path.Combine(root, FileName);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    /// <summary>
    /// The weather now from MET Norway's compact location forecast (locationforecast/2.0/compact): the last hour that has
    /// begun (else the first), its temperature and wind, and the symbol of the hour after it (else of the next 6 or 12
    /// hours). Null when the answer has none.
    /// </summary>
    public static WeatherReport? FromForecast(string json, OverlayWeather place, DateTimeOffset now, DateTimeOffset? expires)
    {
        ArgumentNullException.ThrowIfNull(place);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("properties", out var properties) || !properties.TryGetProperty("timeseries", out var series) || series.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            JsonElement? chosen = null;
            foreach (var entry in series.EnumerateArray())
            {
                if (!entry.TryGetProperty("time", out var time) || !DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                {
                    continue;
                }

                if (chosen is null || at <= now)
                {
                    chosen = entry;
                }

                if (at > now)
                {
                    break;
                }
            }

            if (chosen is not { } e || !e.TryGetProperty("data", out var data)
                || !data.TryGetProperty("instant", out var instant) || !instant.TryGetProperty("details", out var details)
                || !details.TryGetProperty("air_temperature", out var temperature) || temperature.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            string? symbol = null;
            foreach (var next in (string[])["next_1_hours", "next_6_hours", "next_12_hours"])
            {
                if (symbol is null && data.TryGetProperty(next, out var period) && period.TryGetProperty("summary", out var summary)
                    && summary.TryGetProperty("symbol_code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    symbol = code.GetString();
                }
            }

            var wind = details.TryGetProperty("wind_speed", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : 0;
            return symbol is null ? null : new WeatherReport(now, place.Place, place.Latitude, place.Longitude, symbol, temperature.GetDouble(), wind, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
