using System.Net.NetworkInformation;
using PcDiag.Platform.Windows.Inventory;

namespace PcDiag.Collectors.IntegrationTests.Inventory;

public sealed class HardwareInventoryTests
{
    [Theory]
    [InlineData("Ethernet 3", "Intel(R) Ethernet Controller I226-V", true)]
    [InlineData("Wi-Fi", "MediaTek Wi-Fi 7 MT7925 Wireless LAN Card", true)]
    [InlineData("Ethernet 3-WFP Native MAC Layer LightWeight Filter-0000", "Intel(R) Ethernet Controller I226-V-WFP Native MAC Layer LightWeight Filter-0000", false)]
    [InlineData("Ethernet 3-QoS Packet Scheduler-0000", "Intel(R) Ethernet Controller I226-V-QoS Packet Scheduler-0000", false)]
    [InlineData("Ethernet (Kernel Debugger)", "Microsoft Kernel Debug Network Adapter", false)]
    [InlineData("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", false)]
    public void Only_real_adapters_count_as_network_hardware(string name, string description, bool hardware)
    {
        HardwareInventory.Physical(NetworkInterfaceType.Ethernet, name, description).ShouldBe(hardware);
    }

    [Fact]
    public void This_PC_lists_its_processor_memory_drives_and_real_adapters()
    {
        var items = HardwareInventory.Read();

        items.ShouldContain(i => i.Kind == "cpu");
        items.ShouldContain(i => i.Kind == "memory" && i.Amount > 0);
        items.ShouldContain(i => i.Kind == "volume");
        items.Where(i => i.Kind == "network").ShouldAllBe(i => !i.Name.EndsWith("-0000", StringComparison.Ordinal));
        items.Select(i => i.Key).ShouldBeUnique();
    }

    [Fact]
    public void The_connected_devices_signature_changes_with_an_arrival_a_removal_or_a_problem_and_not_with_order_or_case()
    {
        var present = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [@"USB\VID_046D&PID_C52B\5&1A2B3C&0&2"] = 0,
            [@"PCI\VEN_10DE&DEV_2B85&SUBSYS_00000000&REV_A1\4&2283F625&0&0008"] = 0,
            [@"HID\VID_046D&PID_C52B&MI_00\7&3A2F&0&0000"] = 0,
        };
        var signature = HardwareInventory.PresentSignature(present);

        // The same devices listed in another order or case: the same signature.
        var reordered = present.Reverse().ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value);
        HardwareInventory.PresentSignature(reordered).ShouldBe(signature);

        // A device arriving, leaving or having a problem: another.
        HardwareInventory.PresentSignature(new Dictionary<string, int>(present) { [@"USB\VID_0BDA&PID_8153\000001"] = 0 }).ShouldNotBe(signature);
        HardwareInventory.PresentSignature(present.Skip(1).ToDictionary()).ShouldNotBe(signature);
        HardwareInventory.PresentSignature(new Dictionary<string, int>(present) { [@"USB\VID_046D&PID_C52B\5&1A2B3C&0&2"] = 43 }).ShouldNotBe(signature);
    }
}
