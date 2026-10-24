using PcDiag.Core.Devices;
using Shouldly;
using Xunit;

namespace PcDiag.Core.Tests;

public class InfFileTests
{
    private const string Inf = """
        ; A USB serial adapter's driver (shortened)
        [Version]
        Signature   = "$WINDOWS NT$"
        Class       = Ports
        ClassGuid   = {4D36E978-E325-11CE-BFC1-08002BE10318}
        Provider    = %Vendor%
        DriverVer   = 06/21/2024,3.9.2024.0621
        CatalogFile = adapter.cat
        CatalogFile.NTamd64 = adapter64.cat

        [Manufacturer]
        %Vendor% = Adapter, NTamd64.10.0...19041, NTarm64

        [Adapter]   ; 32-bit x86 only
        %Desc% = Install, USB\VID_1A86&PID_7523

        [Adapter.NTamd64.10.0...19041]
        %Desc% = Install, USB\VID_1A86&PID_7523, \
                 USB\VID_1A86&PID_5523
        "%Desc% (other)" = Install, "USB\VID_1A86&PID_E523"

        [Adapter.NTarm64]
        %Desc% = Install, USB\VID_1A86&PID_FFFF

        [Strings]
        Vendor = "WCH; Ltd"
        Desc   = "USB-SERIAL CH340"
        """;

    [Fact]
    public void Reads_the_version_and_the_ids_for_an_architecture_only()
    {
        var inf = InfFile.Parse(Inf);
        inf.Provider.ShouldBe("WCH; Ltd");
        inf.Class.ShouldBe("Ports");
        inf.DriverVersion.ShouldBe("3.9.2024.0621");
        inf.DriverDate.ShouldBe("06/21/2024");
        inf.CatalogFor("NTamd64").ShouldBe("adapter64.cat");
        inf.CatalogFor("NTarm64").ShouldBe("adapter.cat");
        inf.HardwareIdsFor("NTamd64").ShouldBe([@"USB\VID_1A86&PID_7523", @"USB\VID_1A86&PID_5523", @"USB\VID_1A86&PID_E523"]);
        inf.HardwareIdsFor("NTarm64").ShouldBe([@"USB\VID_1A86&PID_FFFF"]);
        // The undecorated section is x86's: never read for a 64-bit PC.
        inf.HardwareIdsFor("NTx86").ShouldBeEmpty();
    }

    [Fact]
    public void A_device_matches_by_its_hardware_ids_then_its_compatible_ids()
    {
        var ids = InfFile.Parse(Inf).HardwareIdsFor("NTamd64");
        var device = new DeviceWithoutDriver(@"USB\VID_1A86&PID_5523\5&1", "USB Serial", null, 28, "Windows has no driver for it",
            [@"USB\VID_1A86&PID_5523&REV_0264", @"USB\VID_1A86&PID_5523"], [@"USB\Class_FF"]);
        InfFile.Matching(ids, device).ShouldBe([@"USB\VID_1A86&PID_5523"]);
        InfFile.Matching(ids, device with { HardwareIds = [@"USB\VID_9999&PID_0001"] }).ShouldBeEmpty();
    }
}
