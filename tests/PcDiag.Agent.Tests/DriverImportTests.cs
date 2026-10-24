using System.IO.Compression;
using System.Net;
using PcDiag.Agent.Screens;
using PcDiag.Core.Telemetry;
using PcDiag.Screens;

namespace PcDiag.Agent.Tests;

public sealed class DriverImportTests : IDisposable
{
    private const string Package = """
        { "id": "aquarium", "name": "Aquarium probe", "version": "1.0", "publisher": "Me",
          "sensors": { "family": "http-json", "url": "http://192.168.1.40/", "sensors": [ { "id": "water", "name": "Water", "kind": "temperature", "unit": "°C", "path": "t" } ] } }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("pcdiag-import").FullName;

    private string Uploads => Path.Combine(_root, "uploads");

    private string Staging => Path.Combine(_root, "staging");

    private string Drivers => Path.Combine(_root, "drivers");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_valid_package_is_checked_from_a_copy_and_put_in_the_drivers_folder()
    {
        var zip = Zip(("aquarium/driver.json", Package), ("aquarium/readme.txt", "hello"));
        var (id, problem) = Import(zip);
        problem.ShouldBeNull();
        id.ShouldBe("aquarium");
        File.Exists(Path.Combine(Drivers, "aquarium", "driver.json")).ShouldBeTrue();
        File.Exists(Path.Combine(Drivers, "aquarium", "readme.txt")).ShouldBeTrue();
        ScreenDriverPackage.Read(Path.Combine(Drivers, "aquarium"), out _)!.Capabilities.ShouldBe(["sensors"]);
        Directory.GetFileSystemEntries(Staging).ShouldBeEmpty();
    }

    [Fact]
    public void Unsafe_names_zip_bombs_wrong_places_and_built_in_ids_are_refused()
    {
        Import(Zip(("../driver.json", Package))).Problem.ShouldNotBeNull().ShouldContain("unsafe");
        Import(Zip(("driver.json", Package), ("C:/evil.dll", "x"))).Problem.ShouldNotBeNull().ShouldContain("unsafe");
        Import(Zip(("driver.json", Package), ("CON.txt", "x"))).Problem.ShouldNotBeNull().ShouldContain("unsafe");
        Import(Zip(("readme.txt", "x"))).Problem.ShouldNotBeNull().ShouldContain("driver.json");
        Import(Zip(("driver.json", Package), ("zeros.bin", new string('0', 5_000_000)))).Problem.ShouldNotBeNull().ShouldContain("zip bomb");
        Import(Zip(("driver.json", Package.Replace("\"aquarium\"", "\"lianli-turzx\"", StringComparison.Ordinal)))).Problem.ShouldNotBeNull().ShouldContain("comes with PC Diagnostic");
        Import(Zip(("driver.json", "{ not json"))).Problem.ShouldNotBeNull().ShouldContain("not a valid driver package");

        // Only from the uploads folder.
        var elsewhere = Path.Combine(_root, "elsewhere.zip");
        File.Copy(Zip(("driver.json", Package)), elsewhere);
        DriverImport.Import(elsewhere, Uploads, Staging, Drivers, new HashSet<string>()).Problem.ShouldNotBeNull().ShouldContain("uploads");
        Directory.Exists(Path.Combine(Drivers, "aquarium")).ShouldBeFalse();
    }

    [Fact]
    public void Too_many_files_are_refused()
    {
        var files = Enumerable.Range(0, ScreenDriverPackage.MaxFiles + 1).Select(i => ($"f{i}.txt", "x")).Prepend(("driver.json", Package)).ToArray();
        Import(Zip(files)).Problem.ShouldNotBeNull().ShouldContain("at most");
    }

    [Fact]
    public void Only_this_pc_and_the_local_network_are_reached()
    {
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Loopback).ShouldBeTrue();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("192.168.1.40")).ShouldBeTrue();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("10.0.0.5")).ShouldBeTrue();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("172.20.0.1")).ShouldBeTrue();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("fe80::1")).ShouldBeTrue();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("172.32.0.1")).ShouldBeFalse();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("8.8.8.8")).ShouldBeFalse();
        PcDiag.Drivers.Io.SensorIo.IsLocal(IPAddress.Parse("2001:4860:4860::8888")).ShouldBeFalse();
    }

    [Fact]
    public void Readings_keep_to_loaded_drivers_and_their_own_keys_and_go_stale()
    {
        var now = DateTimeOffset.UtcNow;
        ExternalReadings.Put(
            [
                new ScreenHostProtocol.DriverReadings("aquarium", [new SensorValue("Tank 1", "water/../cpu", "Water", "temperature", "°C", 24.5)], null),
                new ScreenHostProtocol.DriverReadings("not-loaded", [new SensorValue("d", "s", "S", "other", string.Empty, 1)], null),
            ],
            new HashSet<string>(["aquarium"]),
            now);
        var readings = ExternalReadings.Readings(now, TimeSpan.FromMinutes(2));
        var water = readings.ShouldHaveSingleItem();
        water.Sensor.LogicalKey.ShouldBe("ext/aquarium/tank-1/water----cpu");
        water.Sensor.Kind.ShouldBe(SensorKind.Temperature);
        water.Value.Value.ShouldBe(24.5);

        // A driver that stopped answering reads as unavailable.
        ExternalReadings.Readings(now.AddMinutes(5), TimeSpan.FromMinutes(2)).Single().Value.HasValue.ShouldBeFalse();
        ExternalReadings.Put([], new HashSet<string>(), now);
    }

    private (string? Id, string? Problem) Import(string zip) => DriverImport.Import(zip, Uploads, Staging, Drivers, new HashSet<string>(["lianli-turzx"]));

    private string Zip(params (string Name, string Text)[] files)
    {
        Directory.CreateDirectory(Uploads);
        var path = Path.Combine(Uploads, Guid.NewGuid().ToString("n") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.SmallestSize).Open());
            writer.Write(text);
        }

        return path;
    }
}

public sealed class WindowsDriverPackageTests
{
    [Fact]
    public void A_windows_driver_zip_has_one_inf_and_everything_beside_it()
    {
        PcDiag.Agent.Devices.WindowsDriverService.InfTop(["ch340.inf", "ch340.sys", "ch340.cat"]).ShouldBe(string.Empty);
        PcDiag.Agent.Devices.WindowsDriverService.InfTop(["CH340/ch340.inf", "CH340/amd64/ch340.sys"]).ShouldBe("CH340/");
        PcDiag.Agent.Devices.WindowsDriverService.InfTop(["a.inf", "b.inf"]).ShouldBeNull();
        PcDiag.Agent.Devices.WindowsDriverService.InfTop(["CH340/ch340.inf", "other/x.sys"]).ShouldBeNull();
        PcDiag.Agent.Devices.WindowsDriverService.InfTop(["readme.txt"]).ShouldBeNull();
    }
}
