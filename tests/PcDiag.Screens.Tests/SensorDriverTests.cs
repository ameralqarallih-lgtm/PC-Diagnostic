using System.Text.Json;
using PcDiag.Screens;

namespace PcDiag.Screens.Tests;

public sealed class SensorDriverTests
{
    private static ScreenDriverPackage? Parse(string json, out string? problem) => ScreenDriverPackage.Parse(json, out problem);

    [Fact]
    public void A_sensor_package_needs_no_screens_and_its_settings_are_checked()
    {
        var package = Parse("""
            { "id": "aquarium", "name": "Aquarium probe", "version": "1.0", "publisher": "Me",
              "sensors": { "family": "http-json", "url": "http://192.168.1.40/data", "every": 5,
                "sensors": [ { "id": "water", "name": "Water", "kind": "temperature", "unit": "°C", "path": "probes[0].temp" } ] } }
            """, out var problem);
        problem.ShouldBeNull();
        package!.Capabilities.ShouldBe(["sensors"]);
        package.Models.ShouldBeEmpty();

        // Each family's own settings.
        Parse("""{ "id": "x", "name": "x", "version": "1", "publisher": "x", "sensors": { "family": "http-json", "url": "ftp://a/b", "sensors": [ { "id": "a", "name": "A", "path": "a" } ] } }""", out problem);
        problem.ShouldNotBeNull().ShouldContain("url");
        Parse("""{ "id": "x", "name": "x", "version": "1", "publisher": "x", "sensors": { "family": "serial-lines", "port": "/dev/tty0", "sensors": [ { "id": "a", "name": "A", "key": "T" } ] } }""", out problem);
        problem.ShouldNotBeNull().ShouldContain("port");
        Parse("""{ "id": "x", "name": "x", "version": "1", "publisher": "x", "sensors": { "family": "hid-report", "vid": "16C0", "pid": "05DF", "sensors": [ { "id": "a", "name": "A", "type": "u64" } ] } }""", out problem);
        problem.ShouldNotBeNull().ShouldContain("type");
        Parse("""{ "id": "x", "name": "x", "version": "1", "publisher": "x", "sensors": { "type": "My.Driver", "every": 2 } }""", out problem);
        problem.ShouldNotBeNull().ShouldContain("assembly");
        Parse("""{ "id": "x", "name": "x", "version": "1", "publisher": "x" }""", out problem);
        problem.ShouldNotBeNull().ShouldContain("screens");
    }

    [Fact]
    public void Values_are_found_at_their_path_in_lines_and_in_report_bytes()
    {
        using var document = JsonDocument.Parse("""{ "probes": [ { "temp": 24.5 }, { "temp": "25.1" } ], "on": true, "name": "x" }""");
        SensorDrivers.AtPath(document.RootElement, "probes[0].temp").ShouldBe(24.5);
        SensorDrivers.AtPath(document.RootElement, "probes[1].temp").ShouldBe(25.1);
        SensorDrivers.AtPath(document.RootElement, "on").ShouldBe(1);
        SensorDrivers.AtPath(document.RootElement, "name").ShouldBeNull();
        SensorDrivers.AtPath(document.RootElement, "probes[5].temp").ShouldBeNull();

        SensorDrivers.FromLine("TEMP=21.5, HUM: 40; bad=x\tCO2=612").ShouldBe([("TEMP", 21.5), ("HUM", 40.0), ("CO2", 612.0)]);

        byte[] report = [0x01, 0x34, 0x12, 0xFF, 0xFE];
        SensorDrivers.FromReport(report, 1, "u16le").ShouldBe(0x1234);
        SensorDrivers.FromReport(report, 3, "i16le").ShouldBe(-257);
        SensorDrivers.FromReport(report, 1, "u16be").ShouldBe(0x3412);
        SensorDrivers.FromReport(report, 4, "u16le").ShouldBeNull();
    }

    [Fact]
    public void An_http_json_driver_reads_each_sensor_scaled_through_the_hosts_io()
    {
        var package = Parse("""
            { "id": "meter", "name": "Meter", "version": "1", "publisher": "x",
              "sensors": { "family": "http-json", "url": "http://127.0.0.1:8080/", "device": "desk",
                "sensors": [ { "id": "power", "name": "Power", "kind": "power", "unit": "W", "path": "mw", "scale": 0.001 },
                             { "id": "gone", "name": "Gone", "path": "missing" } ] } }
            """, out _)!;
        var io = new FakeIo("""{ "mw": 45200 }""");
        using var driver = SensorDrivers.Create(package, io);
        var values = driver.Read(CancellationToken.None);
        values.ShouldBe([new SensorValue("desk", "power", "Power", "power", "W", 45.2), new SensorValue("desk", "gone", "Gone", "other", string.Empty, null)]);
        io.Asked.ShouldBe([new Uri("http://127.0.0.1:8080/")]);
    }

    [Fact]
    public void A_serial_driver_keeps_the_last_value_of_each_key()
    {
        var package = Parse("""
            { "id": "station", "name": "Station", "version": "1", "publisher": "x",
              "sensors": { "family": "serial-lines", "port": "COM4", "baud": 115200,
                "sensors": [ { "id": "t", "name": "Temperature", "kind": "temperature", "unit": "°C", "key": "TEMP" } ] } }
            """, out _)!;
        var io = new FakeIo("{}") { Lines = ["TEMP=20.0", "TEMP=20.5"] };
        using var driver = SensorDrivers.Create(package, io);
        driver.Read(CancellationToken.None).Single().Value.ShouldBe(20.5);
        // Nothing new: the last value stays.
        driver.Read(CancellationToken.None).Single().Value.ShouldBe(20.5);
        io.Opened.ShouldBe(("COM4", 115200));
    }

    [Fact]
    public void A_package_hash_covers_every_file_in_it()
    {
        var folder = Directory.CreateTempSubdirectory("pkg").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "driver.json"), "{}");
            Directory.CreateDirectory(Path.Combine(folder, "lib"));
            File.WriteAllText(Path.Combine(folder, "lib", "helper.dll"), "one");
            var first = ScreenDriverPackage.HashFolder(folder, out _);
            File.WriteAllText(Path.Combine(folder, "lib", "helper.dll"), "two");
            ScreenDriverPackage.HashFolder(folder, out _).ShouldNotBe(first);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FakeIo(string json) : ISensorIo
    {
        public List<Uri> Asked { get; } = [];

        public List<string> Lines { get; init; } = [];

        public (string, int)? Opened { get; private set; }

        public byte[] Fetch(Uri url, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked.Add(url);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public ISerialLines OpenSerial(string port, int baud)
        {
            Opened = (port, baud);
            return new Serial(Lines);
        }

        public IHidReports? OpenHid(int vendorId, int productId) => null;

        private sealed class Serial(List<string> lines) : ISerialLines
        {
            public IReadOnlyList<string> Take()
            {
                var taken = lines.ToList();
                lines.Clear();
                return taken;
            }

            public void Dispose()
            {
            }
        }
    }
}
