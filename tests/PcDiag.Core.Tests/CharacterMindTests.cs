using System.Text.Json;
using PcDiag.Core.Overlay;

namespace PcDiag.Core.Tests;

public sealed class CharacterMindTests
{
    private static string EditorFile(string name)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, "web", "src", "features", "overlay", "characters", name);
            if (File.Exists(file))
            {
                return file;
            }
        }

        throw new FileNotFoundException(name + " (the editor's character tests)");
    }

    private static string? Text(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    private static double? Number(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetDouble();

    [Fact]
    public void It_thinks_as_the_editor_thinks()
    {
        // The editor's day in a character's life (mind-parity.json, written by its tests): the same thoughts, step by step.
        using var document = JsonDocument.Parse(File.ReadAllText(EditorFile("mind-parity.json")));
        var steps = document.RootElement.GetProperty("steps").EnumerateArray().ToList();
        var thoughts = document.RootElement.GetProperty("thoughts").EnumerateArray().ToList();
        steps.Count.ShouldBeGreaterThan(50);
        var mind = new CharacterMind();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var status = Text(step, "status");
            ReadingJudged? judged = null;
            if (step.GetProperty("source") is { ValueKind: JsonValueKind.Object } s && status is not null)
            {
                var source = new OverlaySource(s.GetProperty("id").GetString()!, s.GetProperty("label").GetString()!, "", s.GetProperty("unit").GetString()!, Number(s, "value"));
                judged = new ReadingJudged(new OverlayReading(source.Id) with { Critical = Number(step, "critical") }, source, OverlayReactions.KindOf(source.Id, source.Unit), status);
            }

            IReadOnlyList<string>? plays = step.GetProperty("plays").ValueKind == JsonValueKind.Null ? null : [.. step.GetProperty("plays").EnumerateArray().Select(p => p.GetString()!)];
            CharacterEvent? happened = step.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object
                ? new CharacterEvent(e.GetProperty("id").GetInt64(), e.GetProperty("kind").GetString()!, e.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null)
                : null;
            var senses = step.TryGetProperty("senses", out var sensed) && sensed.ValueKind == JsonValueKind.Object ? SensesOf(sensed) : null;
            var thought = mind.Think(step.GetProperty("now").GetDouble(), judged, status, Number(step, "idle"), plays, happened, senses);
            var expected = thoughts[i];
            var at = $"step {i} (t = {step.GetProperty("now").GetDouble()})";
            thought.Action.ShouldBe(Text(expected, "action"), at);
            thought.Effect.ShouldBe(Text(expected, "effect"), at);
            thought.Bubble.ShouldBe(Text(expected, "bubble"), at);
            thought.Tone.ShouldBe(Text(expected, "tone"), at);
            Math.Round(thought.BubbleAge, 3).ShouldBe(expected.GetProperty("bubbleAge").GetDouble(), 1e-9, at);
            thought.BubbleLife.ShouldBe(expected.GetProperty("bubbleLife").GetDouble(), at);
            thought.Shake.ShouldBe(expected.GetProperty("shake").GetDouble(), at);
            thought.Burst.ShouldBe(Text(expected, "burst"), at);
            Math.Round(thought.BurstAge, 3).ShouldBe(expected.GetProperty("burstAge").GetDouble(), 1e-9, at);
            thought.Prop.ShouldBe(expected.TryGetProperty("prop", out var prop) ? prop.GetString() : null, at);
            if (Number(expected, "toward") is { } toward)
            {
                thought.Toward.ShouldNotBeNull(at);
                thought.Toward.Value.ShouldBe(toward, 1e-9, at);
            }
            else
            {
                thought.Toward.ShouldBeNull(at);
            }

            thought.Facing.ShouldBe(expected.GetProperty("facing").GetInt32(), at);
            thought.Weather.ShouldBe(Text(expected, "weather"), at);
            thought.WeatherProp.ShouldBe(Text(expected, "weatherProp"), at);
        }
    }

    /// <summary>What a step of the editor's scenario says the character senses.</summary>
    private static CharacterSenses SensesOf(JsonElement s)
    {
        var cursor = s.GetProperty("cursor");
        var friend = s.GetProperty("friend");
        var weather = s.GetProperty("weather");
        return new CharacterSenses(
            s.GetProperty("self").GetString()!,
            s.GetProperty("x").GetDouble(),
            s.GetProperty("y").GetDouble(),
            s.GetProperty("size").GetDouble(),
            cursor.ValueKind == JsonValueKind.Object ? cursor.GetProperty("x").GetDouble() : null,
            cursor.ValueKind == JsonValueKind.Object ? cursor.GetProperty("y").GetDouble() : null,
            s.GetProperty("touches").GetInt64(),
            friend.ValueKind == JsonValueKind.Object
                ? new CharacterFriend(friend.GetProperty("id").GetString()!, friend.GetProperty("name").GetString()!, friend.GetProperty("x").GetDouble(), friend.GetProperty("size").GetDouble(), friend.GetProperty("free").GetBoolean(), Text(friend, "meetingWith"))
                : null,
            weather.ValueKind == JsonValueKind.Object
                ? new CharacterWeather(weather.GetProperty("id").GetInt64(), weather.GetProperty("kind").GetString()!, weather.GetProperty("temperature").GetDouble(), weather.GetProperty("wind").GetDouble(), weather.GetProperty("day").GetBoolean())
                : null,
            Number(s, "hour"),
            s.GetProperty("day").GetInt32(),
            s.GetProperty("line").GetBoolean());
    }

    [Fact]
    public void A_character_meets_the_nearest_on_its_line()
    {
        var self = new CharacterSpot("p/a", "Mochi", 100, 400, 60, true, null, true);
        CharacterSpot[] others =
        [
            new("p/b", "Biscuit", 300, 405, 60, true, null, true),
            new("p/c", "Kuma", 160, 300, 60, true, null, true), // above it: another line
            new("p/d", "Ren", 140, 400, 60, true, null, false), // on a gauge
            new("p/e", "Hana", 260, 390, 60, false, "p/b", true),
        ];
        CharacterCompany.FriendOf(self, others).ShouldBe(new CharacterFriend("p/e", "Hana", 260, 60, false, "p/b"));
        CharacterCompany.FriendOf(self with { Line = false }, others).ShouldBeNull();

        var company = new CharacterCompany();
        company.Set(self);
        company.Set(others[0]);
        company.FriendOf("p/a")!.Name.ShouldBe("Biscuit");
        company.Keep(new HashSet<string>(["p/a"]));
        company.FriendOf("p/a").ShouldBeNull();
    }

    [Fact]
    public void The_weather_is_read_from_the_forecast()
    {
        // MET Norway's compact forecast (trimmed): the hour begun, its temperature and wind, the next hour's symbol.
        const string Json = """
            {"type":"Feature","properties":{"timeseries":[
              {"time":"2026-10-21T04:00:00Z","data":{"instant":{"details":{"air_temperature":6.0,"wind_speed":8.0}},"next_1_hours":{"summary":{"symbol_code":"lightrain"}}}},
              {"time":"2026-10-21T05:00:00Z","data":{"instant":{"details":{"air_temperature":6.4,"wind_speed":11.2}},"next_1_hours":{"summary":{"symbol_code":"heavyrainandthunder"}}}},
              {"time":"2026-10-21T06:00:00Z","data":{"instant":{"details":{"air_temperature":7.0,"wind_speed":9.0}},"next_6_hours":{"summary":{"symbol_code":"cloudy"}}}}
            ]}}
            """;
        var place = new OverlayWeather("Oslo, Norway", 59.9139, 10.7522);
        var now = new DateTimeOffset(2026, 10, 21, 5, 20, 0, TimeSpan.Zero);
        var report = WeatherReport.FromForecast(Json, place, now, now.AddMinutes(30))!;
        report.ShouldNotBeNull();
        (report.Symbol, report.Temperature, report.Wind, report.Kind, report.Day).ShouldBe(("heavyrainandthunder", 6.4, 11.2, "storm", true));
        report.ForCharacters(now.AddHours(1)).ShouldBe(new CharacterWeather(now.ToUnixTimeSeconds(), "storm", 6.4, 11.2, true));
        report.ForCharacters(now.AddHours(4)).ShouldBeNull();
        WeatherReport.FromForecast("""{"properties":{"timeseries":[]}}""", place, now, null).ShouldBeNull();
        WeatherReport.FromForecast("not json", place, now, null).ShouldBeNull();

        (string Symbol, string Kind)[] kinds = [("clearsky_night", "clear"), ("fair_day", "clear"), ("partlycloudy_day", "clouds"), ("fog", "fog"), ("lightsleet", "rain"), ("heavysnowshowers_polartwilight", "snow"), ("rainshowersandthunder_day", "storm")];
        foreach (var (symbol, kind) in kinds)
        {
            WeatherReport.KindOf(symbol).ShouldBe(kind, symbol);
        }

        new WeatherReport(now, "x", 0, 0, "clearsky_night", 1, 1).Day.ShouldBeFalse();
        place.Check().ShouldBeNull();
        (place with { Latitude = 91 }).Check().ShouldNotBeNull();
        (place with { Place = "" }).Check().ShouldNotBeNull();

        var folder = Directory.CreateTempSubdirectory("weather-");
        try
        {
            report.Write(folder.FullName);
            WeatherReport.Read(folder.FullName).ShouldBe(report);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Its_props_are_the_editors()
    {
        // The editor's samples (prop-parity.json, written by its tests): poses, what props give off, a track's changes.
        using var document = JsonDocument.Parse(File.ReadAllText(EditorFile("prop-parity.json")));
        var root = document.RootElement;
        var poses = root.GetProperty("poses").EnumerateArray().ToList();
        poses.Count.ShouldBeGreaterThan(50);
        foreach (var s in poses)
        {
            var prop = OverlayReactions.Props[s.GetProperty("prop").GetString()!];
            var pose = CharacterProps.PoseAt(prop, s.GetProperty("age").GetDouble(), s.GetProperty("gone").GetDouble(), s.GetProperty("now").GetDouble(), s.GetProperty("facing").GetInt32(),
                (float)s.GetProperty("size").GetDouble(), (float)s.GetProperty("headX").GetDouble(), (float)s.GetProperty("headY").GetDouble(), (float)s.GetProperty("reach").GetDouble(),
                s.GetProperty("moving").GetBoolean());
            var e = s.GetProperty("pose");
            var at = $"{prop.Id} at {s.GetProperty("age").GetDouble()}";
            pose.X.ShouldBe((float)e.GetProperty("x").GetDouble(), 1e-3f, at);
            pose.Y.ShouldBe((float)e.GetProperty("y").GetDouble(), 1e-3f, at);
            pose.Scale.ShouldBe((float)e.GetProperty("scale").GetDouble(), 1e-4f, at);
            pose.Angle.ShouldBe((float)e.GetProperty("angle").GetDouble(), 1e-4f, at);
            pose.Alpha.ShouldBe((float)e.GetProperty("alpha").GetDouble(), 1e-4f, at);
        }

        foreach (var s in root.GetProperty("emits").EnumerateArray())
        {
            var prop = OverlayReactions.Props[s.GetProperty("prop").GetString()!];
            var particles = CharacterProps.EmitAt(prop, s.GetProperty("now").GetDouble(), s.GetProperty("facing").GetInt32(), (float)s.GetProperty("size").GetDouble());
            var expected = s.GetProperty("particles").EnumerateArray().ToList();
            particles.Count.ShouldBe(expected.Count, prop.Id);
            for (var i = 0; i < particles.Count; i++)
            {
                var (p, e) = (particles[i], expected[i]);
                p.X.ShouldBe((float)e.GetProperty("x").GetDouble(), 1e-3f);
                p.Y.ShouldBe((float)e.GetProperty("y").GetDouble(), 1e-3f);
                p.R.ShouldBe((float)e.GetProperty("r").GetDouble(), 1e-3f);
                p.Color.ShouldBe(e.GetProperty("color").GetUInt32());
                p.Alpha.ShouldBe((float)e.GetProperty("alpha").GetDouble(), 1e-4f);
                p.Angle.ShouldBe((float)e.GetProperty("angle").GetDouble(), 1e-4f);
            }
        }

        var track = new PropTrack();
        foreach (var s in root.GetProperty("track").EnumerateArray())
        {
            var wanted = s.GetProperty("prop").ValueKind == JsonValueKind.Null ? null : s.GetProperty("prop").GetString();
            var (id, age, going, goingAge, gone) = track.At(s.GetProperty("now").GetDouble(), wanted);
            var e = s.GetProperty("shown");
            id.ShouldBe(Text(e, "id"));
            age.ShouldBe(e.GetProperty("age").GetDouble(), 1e-9);
            going.ShouldBe(Text(e, "going"));
            goingAge.ShouldBe(e.GetProperty("goingAge").GetDouble(), 1e-9);
            gone.ShouldBe(e.GetProperty("gone").GetDouble(), 1e-9);
        }
    }

    [Fact]
    public void Its_particles_are_the_editors()
    {
        // The editor's samples (particle-parity.json, written by its tests): confetti, dust and breathing, number for number.
        using var document = JsonDocument.Parse(File.ReadAllText(EditorFile("particle-parity.json")));
        foreach (var sample in document.RootElement.EnumerateArray())
        {
            var kind = sample.GetProperty("kind").GetString()!;
            var t = sample.GetProperty("t").GetDouble();
            var size = (float)sample.GetProperty("size").GetDouble();
            if (kind == "breath")
            {
                CharacterParticles.BreathAt(sample.GetProperty("action").GetString()!, t).ShouldBe(sample.GetProperty("value").GetDouble(), 1e-9);
                continue;
            }

            var particles = kind == "confetti" ? CharacterParticles.ConfettiAt(t, size) : CharacterParticles.DustAt(t, sample.GetProperty("facing").GetInt32(), size);
            var expected = sample.GetProperty("particles").EnumerateArray().ToList();
            particles.Count.ShouldBe(expected.Count, $"{kind} at {t}");
            for (var i = 0; i < particles.Count; i++)
            {
                var (p, e) = (particles[i], expected[i]);
                p.X.ShouldBe((float)e.GetProperty("x").GetDouble(), 1e-3f);
                p.Y.ShouldBe((float)e.GetProperty("y").GetDouble(), 1e-3f);
                p.R.ShouldBe((float)e.GetProperty("r").GetDouble(), 1e-4f);
                p.Color.ShouldBe(e.GetProperty("color").GetUInt32());
                p.Alpha.ShouldBe((float)e.GetProperty("alpha").GetDouble(), 1e-4f);
                p.Angle.ShouldBe((float)e.GetProperty("angle").GetDouble(), 1e-3f);
            }
        }
    }

    [Theory]
    [InlineData(96.4, "%", "96 %")]
    [InlineData(38_000_000_000d, "B", "35.4 GB")]
    [InlineData(1_500_000d, "B/s", "1.4 MB/s")]
    [InlineData(16.66, "ms", "16.7 ms")]
    [InlineData(-0.2, "", "0")]
    [InlineData(238.4, "fps", "238 fps")]
    public void Values_are_said_short(double value, string unit, string said) =>
        CharacterMind.Format(new OverlaySource("x", "X", "", unit, value)).ShouldBe(said);
}
