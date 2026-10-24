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
}
