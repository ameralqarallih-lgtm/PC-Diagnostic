using PcDiag.Core.Devices;

namespace PcDiag.Core.Tests;

public sealed class ExpectedHardwareTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly HardwareItem Monitor = new("monitor:AUS2721", "monitor", "VG279QM", "1920 × 1080");
    private static readonly HardwareItem Drive = new("volume:D:", "volume", "D: Data", "2 TB");
    private static readonly HardwareItem Headset = new("device:usb:1532:0527", "device", "Razer Kraken", "Audio");
    private static readonly HardwareItem Memory = new("memory", "memory", "Memory", "32 GB installed", 32);

    [Fact]
    public void Missing_items_keep_the_time_they_were_first_missed_and_new_ones_are_added()
    {
        var expected = new ExpectedHardware(1, T0, [Monitor, Drive, Headset, Memory]);

        var (missing, added) = HardwareCheck.Compare(expected, [Monitor, Memory, Headset with { Name = "Razer Kraken V3" }], [], T0);
        missing.ShouldHaveSingleItem().Item.ShouldBe(Drive);
        missing[0].Since.ShouldBe(T0);
        added.ShouldBeEmpty();

        // Still missing later: since stays; a memory module gone counts too; a new device is added.
        var phone = new HardwareItem("device:usb:05AC:12A8", "device", "iPhone", "Portable");
        var (later, more) = HardwareCheck.Compare(expected, [Monitor, Memory with { Amount = 16 }, Headset, phone], missing, T0.AddMinutes(5));
        later.Select(m => m.Item.Key).ShouldBe(["volume:D:", "memory"]);
        later[0].Since.ShouldBe(T0);
        later[1].Now.ShouldBe(16);
        more.ShouldHaveSingleItem().ShouldBe(phone);
        HardwareCheck.Describe(later).ShouldBe("D: Data (drive), Memory (16 of 32 GB)");

        // Memory rounding (31.9 of 32) is not a shortfall; no setup, nothing missing.
        HardwareCheck.Compare(expected, [Monitor, Drive, Headset, Memory with { Amount = 31.9 }], [], T0).Missing.ShouldBeEmpty();
        HardwareCheck.Compare(null, [Monitor], [], T0).Missing.ShouldBeEmpty();
    }

    [Fact]
    public void A_setup_is_checked_and_survives_its_file()
    {
        HardwareCheck.Validate(new ExpectedHardware(1, T0, [Monitor, Drive])).ShouldBeNull();
        HardwareCheck.Validate(new ExpectedHardware(1, T0, [Monitor, Monitor]))!.ShouldContain("once");
        HardwareCheck.Validate(new ExpectedHardware(1, T0, [Monitor with { Kind = "toaster" }]))!.ShouldContain("kind");
        HardwareCheck.Validate(new ExpectedHardware(1, T0, [Monitor with { Key = "" }]))!.ShouldContain("key");
        HardwareCheck.Validate(null)!.ShouldContain("required");

        var file = Path.Combine(Path.GetTempPath(), "pcdiag-tests", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            HardwareCheck.Write(file, new ExpectedHardware(1, T0, [Monitor, Memory], IncidentOnMissing: true));
            var read = HardwareCheck.Read(file).ShouldNotBeNull();
            read.Items.ShouldBe([Monitor, Memory]);
            read.IncidentOnMissing.ShouldBeTrue();
            File.WriteAllText(file, "{ broken");
            HardwareCheck.Read(file).ShouldBeNull();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
