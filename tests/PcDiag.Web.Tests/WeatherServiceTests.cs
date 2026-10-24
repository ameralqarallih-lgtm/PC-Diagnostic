using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Web.Characters;

namespace PcDiag.Web.Tests;

public sealed class WeatherServiceTests : IDisposable
{
    private const string Forecast = """
        {"properties":{"timeseries":[{"time":"2026-10-21T04:00:00Z","data":{"instant":{"details":{"air_temperature":-1.5,"wind_speed":3.0}},"next_1_hours":{"summary":{"symbol_code":"lightsnow"}}}}]}}
        """;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("weather-");
    private readonly Clock _time = new(new DateTimeOffset(2026, 10, 21, 4, 30, 0, TimeSpan.Zero));
    private readonly Answers _answers = new();

    public void Dispose() => _root.Delete(recursive: true);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers each request with the forecast (or places), and keeps what was asked.</summary>
    private sealed class Answers : HttpMessageHandler, IHttpClientFactory
    {
        public List<Uri> Asked { get; } = [];

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            var body = request.RequestUri!.Host == "api.met.no" ? Forecast : """[{"display_name":"Oslo, Norway","lat":"59.91333","lon":"10.73897"},{"display_name":"Nowhere","lat":"x","lon":"1"}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private WeatherService Service(out DataPaths paths)
    {
        paths = new DataPaths(_root.FullName);
        return new WeatherService(paths, _answers, _time, NullLogger<WeatherService>.Instance);
    }

    [Fact]
    public async Task It_fetches_the_weather_for_the_chosen_place_when_it_is_due_and_removes_it_with_none()
    {
        var weather = Service(out var paths);
        var oslo = new OverlayWeather("Oslo, Norway", 59.9139, 10.7522);
        OverlayStyles.Write(paths.OverlayFile, OverlayConfig.Empty with { Weather = oslo });

        await weather.RefreshAsync(CancellationToken.None);
        _answers.Asked.ShouldHaveSingleItem().ToString().ShouldBe("https://api.met.no/weatherapi/locationforecast/2.0/compact?lat=59.9139&lon=10.7522");
        weather.Now().ShouldNotBeNull().ShouldSatisfyAllConditions(r => r.Kind.ShouldBe("snow"), r => r.Temperature.ShouldBe(-1.5), r => r.Place.ShouldBe("Oslo, Norway"));

        // Not again before its time; a new place at once.
        _time.Now += TimeSpan.FromMinutes(10);
        await weather.RefreshAsync(CancellationToken.None);
        _answers.Asked.Count.ShouldBe(1);
        _time.Now += WeatherService.Every;
        await weather.RefreshAsync(CancellationToken.None);
        _answers.Asked.Count.ShouldBe(2);
        OverlayStyles.Write(paths.OverlayFile, OverlayConfig.Empty with { Weather = new OverlayWeather("Amman, Jordan", 31.9516, 35.924) });
        await weather.RefreshAsync(CancellationToken.None);
        _answers.Asked.Count.ShouldBe(3);
        weather.Now()!.Place.ShouldBe("Amman, Jordan");

        // No place: nothing fetched, and the weather gone.
        OverlayStyles.Write(paths.OverlayFile, OverlayConfig.Empty);
        await weather.RefreshAsync(CancellationToken.None);
        weather.Now().ShouldBeNull();
        _answers.Asked.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Places_are_searched_by_name()
    {
        var weather = Service(out _);
        var places = await weather.SearchAsync("Oslo", "en", CancellationToken.None);
        places.ShouldHaveSingleItem().ShouldBe(new OverlayWeather("Oslo, Norway", 59.9133, 10.739));
        _answers.Asked.ShouldHaveSingleItem().Host.ShouldBe("nominatim.openstreetmap.org");
        OverlayStyles.Validate(OverlayConfig.Empty with { Weather = places[0] with { Latitude = 100 } }).ShouldNotBeNull();
    }
}
