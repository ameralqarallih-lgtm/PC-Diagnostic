using PcDiag.Core.Devices;
using PcDiag.Platform.Windows.Devices;

namespace PcDiag.Platform.Windows.Inventory;

/// <summary>The device register read from the device tree, and the board's LPC/eSPI bridge found in it.</summary>
/// <param name="Devices">I/O devices (connected now or before) and devices of any kind that have a problem.</param>
/// <param name="BridgeName">The chipset's LPC/eSPI (ISA) bridge, e.g. "Intel(R) Z790 Chipset LPC/eSPI Controller - 7A04".</param>
/// <param name="BridgeVendorId">Its PCI vendor ID.</param>
/// <param name="BridgeDeviceId">Its PCI device ID.</param>
public sealed record DeviceRegister(IReadOnlyList<IoDevice> Devices, string? BridgeName = null, string? BridgeVendorId = null, string? BridgeDeviceId = null);

/// <summary>
/// I/O devices from Windows' device tree (fast: no WMI). It includes the devices Windows remembers from earlier
/// connections, with when they were first installed, last connected and last removed.
/// </summary>
public static class DeviceInventory
{
    /// <summary>Reads the register.</summary>
    public static DeviceRegister Read()
    {
        var bridges = new List<DeviceNodeFacts>();
        var nodes = DeviceTree.Nodes(node =>
        {
            if (node.Present && string.Equals(node.Enumerator, "PCI", StringComparison.OrdinalIgnoreCase) && IsBridge(node))
            {
                bridges.Add(node);
            }

            return IoDevices.Wanted(node);
        });

        // A PC can have two (one on the processor package): the named one, else the one whose ID says the chipset family.
        var bridge = bridges.FirstOrDefault(b => IsBridgeName(b.Description))
                     ?? bridges.FirstOrDefault(b => IoDevices.Ids(b) is var (v, d) && BoardFacts.Chipset(null, v, d, null, null) is not null)
                     ?? bridges.FirstOrDefault();
        var (vendor, device) = bridge is null ? (null, null) : IoDevices.Ids(bridge);
        return new DeviceRegister(IoDevices.Group(nodes), bridge?.Description, vendor, device);
    }

    // The LPC/eSPI bridge: named so by Intel's chipset driver, else Windows' "PCI standard ISA bridge" (msisadrv).
    private static bool IsBridge(DeviceNodeFacts node) =>
        IsBridgeName(node.Description) || string.Equals(node.Service, "msisadrv", StringComparison.OrdinalIgnoreCase);

    private static bool IsBridgeName(string? name) =>
        name is not null && (name.Contains("LPC", StringComparison.OrdinalIgnoreCase) || name.Contains("eSPI", StringComparison.OrdinalIgnoreCase));
}
