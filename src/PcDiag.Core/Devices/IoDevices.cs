using System.Globalization;
using System.Text.RegularExpressions;

namespace PcDiag.Core.Devices;

/// <summary>
/// One device node as Windows' device tree (Configuration Manager) describes it, present or not: Windows keeps the
/// nodes of devices that were connected before, with the dates they were first installed, last connected and last removed.
/// </summary>
/// <param name="InstanceId">Device instance ID, e.g. <c>USB\VID_046D&amp;PID_C52B\5&amp;2F3B&amp;0&amp;3</c>.</param>
/// <param name="FriendlyName">DEVPKEY_Device_FriendlyName.</param>
/// <param name="Description">DEVPKEY_Device_DeviceDesc (the driver's description, often generic).</param>
/// <param name="BusReportedName">DEVPKEY_Device_BusReportedDeviceDesc: the product name the device itself reports.</param>
/// <param name="Class">Device setup class, e.g. "Keyboard", "HIDClass", "MEDIA".</param>
/// <param name="Manufacturer">DEVPKEY_Device_Manufacturer (the driver's, e.g. "(Standard system devices)").</param>
/// <param name="Enumerator">The bus driver that found it, e.g. "USB", "HID", "BTHENUM", "PCI", "SWD".</param>
/// <param name="Service">The function driver's service.</param>
/// <param name="Parent">The parent node's instance ID (known while it is connected).</param>
/// <param name="ContainerId">Windows' container: the enclosure it is part of (everything behind a built-in hub shares one).</param>
/// <param name="InLocalMachine">Part of the PC itself, as opposed to an attached device.</param>
/// <param name="Present">Connected now.</param>
/// <param name="ProblemCode">Device Manager's problem code (0: working).</param>
/// <param name="Location">DEVPKEY_Device_LocationInfo, e.g. "Port_#0003.Hub_#0001".</param>
/// <param name="HardwareIds">Hardware IDs, most specific first.</param>
/// <param name="DriverProvider">Who made the driver.</param>
/// <param name="DriverVersion">Driver version.</param>
/// <param name="DriverDate">Driver date.</param>
/// <param name="DriverInf">The driver's INF file.</param>
/// <param name="FirstInstalled">When Windows first installed the device.</param>
/// <param name="LastArrival">When it was last connected (or found at start-up).</param>
/// <param name="LastRemoval">When it was last removed.</param>
public sealed record DeviceNodeFacts(
    string InstanceId,
    string? FriendlyName,
    string? Description,
    string? BusReportedName,
    string? Class,
    string? Manufacturer,
    string? Enumerator,
    string? Service,
    string? Parent,
    string? ContainerId,
    bool? InLocalMachine,
    bool Present,
    int ProblemCode,
    string? Location,
    IReadOnlyList<string> HardwareIds,
    string? DriverProvider = null,
    string? DriverVersion = null,
    DateTimeOffset? DriverDate = null,
    string? DriverInf = null,
    DateTimeOffset? FirstInstalled = null,
    DateTimeOffset? LastArrival = null,
    DateTimeOffset? LastRemoval = null,
    IReadOnlyList<string>? CompatibleIds = null);

/// <summary>One part (device node) of an I/O device, e.g. the keyboard function of a wireless receiver.</summary>
/// <param name="InstanceId">Device instance ID.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What it is (see <see cref="IoDevice.Kind"/>).</param>
/// <param name="Class">Device setup class.</param>
/// <param name="Present">Connected now.</param>
/// <param name="Technical">Plumbing (a HID collection, a composite parent, a Bluetooth service).</param>
/// <param name="ProblemCode">Device Manager's problem code (0: working).</param>
/// <param name="Service">Its driver's service.</param>
/// <param name="DriverProvider">Who made its driver.</param>
/// <param name="DriverVersion">Its driver's version.</param>
/// <param name="DriverDate">Its driver's date (yyyy-MM-dd).</param>
public sealed record IoDevicePart(string InstanceId, string Name, string Kind, string? Class, bool Present, bool Technical, int ProblemCode, string? Service, string? DriverProvider, string? DriverVersion, string? DriverDate);

/// <summary>
/// A physical input/output device as the inventory registers it: the node attached to a hub port (or the Bluetooth
/// device, or the monitor) with all the nodes Windows created for it. Devices of the PC itself that have a problem are
/// listed too, with the kind "system".
/// </summary>
/// <param name="Key">Stable key: "id:VID:PID" for USB, PCI and HD Audio devices, "bt:address", "display:model", else the instance ID.</param>
/// <param name="Name">The best name: the device's own product name rather than a generic driver description.</param>
/// <param name="Kind">keyboard, mouse, game-controller, input, audio, camera, imaging, display, storage, portable, network, bluetooth, printer, ports, security, sensor, usb, other, or system.</param>
/// <param name="Kinds">Every kind its parts have, e.g. keyboard and mouse for a wireless receiver.</param>
/// <param name="Type">The driver's description when it differs from the name (e.g. "USB Composite Device").</param>
/// <param name="Class">Device setup class of its main node.</param>
/// <param name="Bus">How it is attached: USB, Bluetooth, Bluetooth LE, PCI, HD Audio, Display, Built-in (I²C/SPI), ACPI, Network, Printing or Virtual.</param>
/// <param name="Manufacturer">The maker: from the vendor ID when known, else the driver's manufacturer.</param>
/// <param name="VendorId">USB/PCI/Bluetooth vendor ID, or a monitor's PnP maker code.</param>
/// <param name="ProductId">Product ID.</param>
/// <param name="Present">Connected now.</param>
/// <param name="Connected">How many of it are connected now (two identical mice: 2).</param>
/// <param name="External">Attached to the PC rather than part of it.</param>
/// <param name="Technical">Plumbing on its own (a hub, a virtual device from a program), hidden by default.</param>
/// <param name="ProblemCode">The worst problem of a connected part (0: working).</param>
/// <param name="Problem">What the problem code means.</param>
/// <param name="InstanceId">The main node's instance ID.</param>
/// <param name="Location">Where it is attached, e.g. "Port_#0003.Hub_#0001".</param>
/// <param name="Service">The main node's driver service.</param>
/// <param name="DriverProvider">Who made the main driver.</param>
/// <param name="DriverVersion">Main driver version.</param>
/// <param name="DriverDate">Main driver date (yyyy-MM-dd).</param>
/// <param name="DriverInf">The main driver's INF file.</param>
/// <param name="HardwareId">The main node's most specific hardware ID.</param>
/// <param name="FirstInstalled">When Windows first installed any part of it.</param>
/// <param name="LastArrival">When it was last connected.</param>
/// <param name="LastRemoval">When it was last removed.</param>
/// <param name="Parts">Its device nodes.</param>
public sealed record IoDevice(
    string Key,
    string Name,
    string Kind,
    IReadOnlyList<string> Kinds,
    string? Type,
    string? Class,
    string Bus,
    string? Manufacturer,
    string? VendorId,
    string? ProductId,
    bool Present,
    int Connected,
    bool External,
    bool Technical,
    int ProblemCode,
    string? Problem,
    string InstanceId,
    string? Location,
    string? Service,
    string? DriverProvider,
    string? DriverVersion,
    string? DriverDate,
    string? DriverInf,
    string? HardwareId,
    DateTimeOffset? FirstInstalled,
    DateTimeOffset? LastArrival,
    DateTimeOffset? LastRemoval,
    IReadOnlyList<IoDevicePart> Parts);

/// <summary>Decides which device nodes are I/O devices, what they are, how they are attached and what to call them.</summary>
public static partial class IoDevices
{
    /// <summary>Most devices registered in one inventory (a PC that has seen thousands of USB sticks keeps the newest).</summary>
    public const int MaxDevices = 1000;

    /// <summary>Most parts listed per device.</summary>
    public const int MaxParts = 40;

    /// <summary>Problem code 22: disabled (by the user or a program).</summary>
    public const int ProblemDisabled = 22;

    /// <summary>Problem code 45: not connected (what every disconnected device shows).</summary>
    public const int ProblemNotConnected = 45;

    private static readonly Dictionary<string, string> ClassKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Keyboard"] = "keyboard",
        ["Mouse"] = "mouse",
        ["XboxComposite"] = "game-controller",
        ["XnaComposite"] = "game-controller",
        ["HIDClass"] = "input",
        ["MEDIA"] = "audio",
        ["AudioEndpoint"] = "audio",
        ["Camera"] = "camera",
        ["Image"] = "imaging",
        ["Monitor"] = "display",
        ["WPD"] = "portable",
        ["AndroidUsbDeviceClass"] = "portable",
        ["Bluetooth"] = "bluetooth",
        ["Printer"] = "printer",
        ["WSDPrintDevice"] = "printer",
        ["Ports"] = "ports",
        ["Modem"] = "ports",
        ["MultiPortSerial"] = "ports",
        ["SmartCardReader"] = "security",
        ["SmartCard"] = "security",
        ["Biometric"] = "security",
        ["Sensor"] = "sensor",
        ["USB"] = "usb",
        ["USBDevice"] = "usb",
        ["Infrared"] = "other",
        ["Multifunction"] = "other",
    };

    // Classes that are I/O only when the device is attached (a USB stick, a USB network adapter), not the PC's own.
    private static readonly Dictionary<string, string> AttachedClassKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DiskDrive"] = "storage",
        ["CDROM"] = "storage",
        ["Net"] = "network",
    };

    private static readonly HashSet<string> TechnicalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "usbhub", "usbhub3", "usbccgp", "HidUsb", "mshidkmdf", "mshidumdf", "HidBth", "HidBthLE", "BthLEEnum", "BthEnum", "RFCOMM", "BthPan",
        "BthHFEnum", "Microsoft_Bluetooth_AvrcpTransport", "USBXHCI", "UCX01000", "iusb3xhc", "iusb3hub", "usbehci", "usbohci", "usbuhci",
        "UcmUcsiAcpiClient", "UcmUcsiCx0101", "UcmCx0101", "UcmTcpciCx0101", "Usb4HostRouter", "Usb4DeviceRouter", "hidi2c", "HidSpiCx", "umbus", "BthMini", "vhf",
    };

    private const string EndpointKey = "endpoint:";

    private static readonly HashSet<string> HubServices = new(StringComparer.OrdinalIgnoreCase) { "usbhub", "usbhub3", "iusb3hub" };

    // Buses of real hardware; any other enumerator belongs to a program (G HUB, Razer Synapse, Remote Desktop, virtual audio).
    private static readonly HashSet<string> HardwareBuses = new(StringComparer.Ordinal)
    {
        "USB", "Bluetooth", "Bluetooth LE", "PCI", "HD Audio", "Display", "ACPI", "Built-in (I²C/SPI)", "Storage", "Network", "Printing", "HID",
    };

    // Nodes a physical device's parts hang from (walking up stops at anything else: a hub, a controller, the PC).
    private static readonly HashSet<string> ChainEnumerators = new(StringComparer.OrdinalIgnoreCase) { "USB", "HID", "USBSTOR", "UASPSTOR", "USBPRINT", "BTHENUM", "BTHLE", "BTHLEDEVICE" };

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["03F0"] = "HP", ["0403"] = "FTDI", ["0409"] = "NEC", ["041E"] = "Creative", ["0424"] = "Microchip", ["0451"] = "Texas Instruments",
        ["045E"] = "Microsoft", ["046A"] = "Cherry", ["046D"] = "Logitech", ["0471"] = "Philips", ["047F"] = "Plantronics", ["0489"] = "Foxconn",
        ["04B4"] = "Cypress", ["04CA"] = "Lite-On", ["04D9"] = "Holtek", ["04E8"] = "Samsung", ["04F2"] = "Chicony", ["04F3"] = "Elan",
        ["054C"] = "Sony", ["056A"] = "Wacom", ["057E"] = "Nintendo", ["05AC"] = "Apple", ["05E3"] = "Genesys Logic", ["0781"] = "SanDisk",
        ["07CA"] = "AVerMedia", ["0951"] = "Kingston", ["0A12"] = "Cambridge Silicon Radio", ["0A5C"] = "Broadcom", ["0B05"] = "ASUS",
        ["0B95"] = "ASIX", ["0BDA"] = "Realtek", ["0C45"] = "Microdia", ["0CF3"] = "Qualcomm Atheros", ["0D8C"] = "C-Media", ["0E8D"] = "MediaTek",
        ["0FD9"] = "Elgato", ["1038"] = "SteelSeries", ["1050"] = "Yubico", ["1058"] = "Western Digital", ["1395"] = "EPOS", ["13D3"] = "IMC Networks",
        ["1532"] = "Razer", ["17EF"] = "Lenovo", ["18D1"] = "Google", ["1A40"] = "Terminus", ["1A86"] = "WCH", ["1B1C"] = "Corsair", ["1E71"] = "NZXT",
        ["1E7D"] = "ROCCAT", ["2109"] = "VIA Labs", ["2516"] = "Cooler Master", ["26CE"] = "ASRock", ["2717"] = "Xiaomi",
        ["2DC8"] = "8BitDo", ["3434"] = "Keychron", ["413C"] = "Dell", ["8087"] = "Intel", ["1462"] = "MSI", ["1044"] = "Gigabyte", ["048D"] = "ITE",
        ["174C"] = "ASMedia", ["0BB4"] = "HTC",
        // PCI and HD Audio
        ["8086"] = "Intel", ["10DE"] = "NVIDIA", ["1002"] = "AMD", ["1022"] = "AMD", ["10EC"] = "Realtek", ["14E4"] = "Broadcom", ["14C3"] = "MediaTek",
        ["168C"] = "Qualcomm Atheros", ["17CB"] = "Qualcomm", ["1B21"] = "ASMedia", ["144D"] = "Samsung", ["15B7"] = "Western Digital", ["1987"] = "Phison",
        ["1043"] = "ASUS",
        // Monitors (PnP maker codes)
        ["AUS"] = "ASUS", ["SAM"] = "Samsung", ["DEL"] = "Dell", ["GSM"] = "LG", ["ACR"] = "Acer", ["BNQ"] = "BenQ", ["AOC"] = "AOC", ["HWP"] = "HP",
        ["LEN"] = "Lenovo", ["PHL"] = "Philips", ["MSI"] = "MSI", ["GBT"] = "Gigabyte", ["VSC"] = "ViewSonic", ["SNY"] = "Sony", ["HPN"] = "HP",
        ["IVM"] = "iiyama", ["EIZ"] = "EIZO", ["NEC"] = "NEC", ["SHP"] = "Sharp", ["BOE"] = "BOE", ["AUO"] = "AU Optronics", ["CMN"] = "Innolux",
        ["LGD"] = "LG Display", ["XMI"] = "Xiaomi", ["APP"] = "Apple", ["MEI"] = "Panasonic", ["ALI"] = "Alienware",
    };

    private static readonly Dictionary<string, int> KindRank = new(StringComparer.Ordinal)
    {
        ["keyboard"] = 0, ["mouse"] = 1, ["game-controller"] = 2, ["audio"] = 3, ["camera"] = 4, ["display"] = 5, ["storage"] = 6, ["portable"] = 7,
        ["printer"] = 8, ["imaging"] = 9, ["network"] = 10, ["security"] = 11, ["ports"] = 12, ["sensor"] = 13, ["bluetooth"] = 14, ["input"] = 15,
        ["other"] = 16, ["usb"] = 17, ["system"] = 18,
    };

    /// <summary>
    /// What a node is, or null when it is neither part of an I/O device nor a device with a problem. A node that
    /// attaches a device to a hub port is always kept (it names the device).
    /// </summary>
    public static IoDevicePart? Part(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var kind = KindOf(node, IsExternal(node));
        var problem = node.Present ? node.ProblemCode : 0;
        if (kind is null)
        {
            if (node.Present && problem is not (0 or ProblemNotConnected))
            {
                kind = "system";
            }
            else if (IsDeviceNode(node))
            {
                kind = "usb";
            }
            else
            {
                return null;
            }
        }

        return new IoDevicePart(node.InstanceId, NameOf(node), kind, node.Class, node.Present, IsTechnical(node), problem, node.Service, node.DriverProvider, node.DriverVersion, Date(node.DriverDate));
    }

    /// <summary>Whether the inventory wants the node's details: part of an I/O device, a device with a problem, or a device's own node.</summary>
    public static bool Wanted(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // Audio endpoints of disconnected devices repeat the device itself.
        if (!node.Present && node.InstanceId.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Part(node) is { } part && (part.Present || !part.Technical || IsDeviceNode(node));
    }

    /// <summary>
    /// The physical devices the nodes make up: each node is grouped with the device it is part of (<see cref="KeyOf"/>).
    /// Devices are ordered with the connected ones first, then by kind and name.
    /// </summary>
    public static List<IoDevice> Group(IReadOnlyCollection<DeviceNodeFacts> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var byId = new Dictionary<string, DeviceNodeFacts>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            byId.TryAdd(node.InstanceId, node);
        }

        DeviceNodeFacts? Lookup(string id) => byId.GetValueOrDefault(id);
        var groups = nodes.Where(n => Wanted(n)).GroupBy(n => KeyOf(n, Lookup).Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // An audio endpoint ("Speakers (Realtek(R) Audio)") is a part of the device it names; one that names no device
        // is a program's (Remote Audio) and stays on its own, as plumbing.
        foreach (var (key, endpoints) in groups.Where(g => g.Key.StartsWith(EndpointKey, StringComparison.Ordinal)).ToList())
        {
            var device = key[EndpointKey.Length..];
            // A connected endpoint belongs to the connected device of that name, not to one Windows remembers from before.
            var present = endpoints.Any(n => n.Present);
            var owner = groups
                .Where(g => !g.Key.StartsWith(EndpointKey, StringComparison.Ordinal) && g.Value.Any(n => string.Equals(NameOf(n), device, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(g => g.Value.Any(n => n.Present) == present)
                .FirstOrDefault();
            if (owner.Value is not null)
            {
                owner.Value.AddRange(endpoints);
                groups.Remove(key);
            }
        }

        var devices = new List<IoDevice>();
        foreach (var (key, group) in groups)
        {
            if (Device(key, group, Lookup) is { } device)
            {
                devices.Add(device);
            }
        }

        return devices
            .OrderByDescending(d => d.Present)
            .ThenByDescending(d => d.LastArrival ?? d.FirstInstalled ?? DateTimeOffset.MinValue)
            .Take(MaxDevices)
            .OrderByDescending(d => d.Present)
            .ThenBy(d => Rank(d.Kind))
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The physical device a node is part of: walking up from the node through its parents, the node attached to a hub
    /// port (USB), the Bluetooth device (by address), the monitor (by model), else the node itself. Disconnected
    /// nodes have no parent, so USB devices are keyed by vendor and product ID, which also merges the nodes Windows
    /// keeps from earlier connections on other ports.
    /// </summary>
    public static (string Key, DeviceNodeFacts? Device) KeyOf(DeviceNodeFacts node, Func<string, DeviceNodeFacts?> lookup)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(lookup);
        if (IsEndpoint(node))
        {
            return (EndpointKey + (EndpointDevice().Match(NameOf(node)) is { Success: true } device ? device.Groups[1].Value : node.InstanceId), null);
        }

        var current = node;
        for (var depth = 0; depth < 8; depth++)
        {
            if (BluetoothAddress(current.InstanceId) is { } address)
            {
                return ("bt:" + address, current.InstanceId.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) || current.InstanceId.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase) ? current : null);
            }

            if (current.InstanceId.StartsWith(@"DISPLAY\", StringComparison.OrdinalIgnoreCase))
            {
                var model = current.InstanceId.Split('\\')[1];
                return ("display:" + model.ToUpperInvariant(), current);
            }

            var parent = current.Parent is { } parentId ? lookup(parentId) : null;
            if (UsbDeviceNode().IsMatch(current.InstanceId) && (parent is null || IsHub(parent)))
            {
                var (vendor, product) = Ids(current);
                return (vendor is null ? current.InstanceId : $"id:{vendor}:{product}", current);
            }

            if (parent is null || !ChainEnumerators.Contains(parent.Enumerator ?? EnumeratorOf(parent.InstanceId)) || IsHub(parent))
            {
                // HID collections of a built-in device (an I²C touchpad) belong to that device.
                if (string.Equals(current.Enumerator, "HID", StringComparison.OrdinalIgnoreCase) && parent is not null && !IsHub(parent))
                {
                    return (parent.InstanceId, null);
                }

                break;
            }

            current = parent;
        }

        // A network printer's or scanner's software nodes belong to the printer (Windows' container for it).
        var enumerator = node.Enumerator ?? EnumeratorOf(node.InstanceId);
        if (enumerator.Equals("SWD", StringComparison.OrdinalIgnoreCase) && node.InLocalMachine is false && node.ContainerId is { } container)
        {
            return ("container:" + container, null);
        }

        // Disconnected (no parent known) or not on a chain: by ID when the node has one.
        var ids = Ids(node);
        if (ids.VendorId is not null && (ChainEnumerators.Contains(enumerator) || enumerator.Equals("HDAUDIO", StringComparison.OrdinalIgnoreCase) || enumerator.Equals("PCI", StringComparison.OrdinalIgnoreCase)))
        {
            return ($"id:{ids.VendorId}:{ids.ProductId}", IsDeviceNode(node) ? node : null);
        }

        return (node.InstanceId, node);
    }

    /// <summary>The device kind of a node, or null when it is not an I/O node.</summary>
    internal static string? KindOf(DeviceNodeFacts node, bool external)
    {
        var cls = node.Class ?? string.Empty;
        var name = $"{node.FriendlyName} {node.Description} {node.BusReportedName}";
        if (ClassKinds.TryGetValue(cls, out var kind))
        {
            if (kind == "input" && GameController().IsMatch(name))
            {
                return "game-controller";
            }

            if (kind == "imaging" && Scanner().IsMatch(name))
            {
                return "printer";
            }

            return kind;
        }

        if (AttachedClassKinds.TryGetValue(cls, out kind) && (external || IsAttachedBus(node.Enumerator)))
        {
            return kind;
        }

        return null;
    }

    /// <summary>Attached to the PC rather than part of it (by Windows' container; else by the bus it is on).</summary>
    public static bool IsExternal(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.InLocalMachine is { } local)
        {
            return !local;
        }

        return IsAttachedBus(node.Enumerator ?? EnumeratorOf(node.InstanceId));
    }

    /// <summary>Plumbing: hubs, controllers, composite parents, HID collections, Bluetooth services, virtual devices.</summary>
    public static bool IsTechnical(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var id = node.InstanceId;
        var enumerator = node.Enumerator ?? EnumeratorOf(id);
        if (BusOf(node) == "Virtual" || string.Equals(node.Class, "PrintQueue", StringComparison.OrdinalIgnoreCase) || string.Equals(node.Class, "SoftwareDevice", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Bluetooth: the paired device is BTHENUM\DEV_… or BTHLE\DEV_…; its services are nodes of their own.
        if (enumerator.Equals("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase) || (enumerator.Equals("BTHENUM", StringComparison.OrdinalIgnoreCase) && !id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase))
            || enumerator.Equals("BTH", StringComparison.OrdinalIgnoreCase))
        {
            return node.Class is not ("MEDIA" or "AudioEndpoint" or "Keyboard" or "Mouse");
        }

        // A monitor without a model is a virtual one (Remote Desktop, a display emulator).
        if (string.Equals(node.Class, "Monitor", StringComparison.OrdinalIgnoreCase) && (node.HardwareIds.Any(h => h.Contains("Default_Monitor", StringComparison.OrdinalIgnoreCase)) || string.Equals(node.FriendlyName, "Generic Monitor", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (node.Service is { } service && TechnicalServices.Contains(service))
        {
            return true;
        }

        var description = node.Description ?? node.FriendlyName ?? string.Empty;
        return TechnicalName().IsMatch(description);
    }

    /// <summary>
    /// The name people know: the friendly name, else the product name the device reports when the driver's description is
    /// generic ("USB Composite Device" → "Razer Huntsman V3 Pro TKL"), else the description. "Generic Monitor (PG32UCDP)" → "PG32UCDP".
    /// </summary>
    public static string NameOf(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var friendly = Clean(node.FriendlyName);
        var description = Clean(node.Description);
        var reported = Clean(node.BusReportedName);
        if (friendly is not null && GenericMonitor().Match(friendly) is { Success: true } monitor)
        {
            return monitor.Groups[1].Value;
        }

        if (friendly is not null && !(reported is not null && GenericName().IsMatch(friendly)))
        {
            return friendly;
        }

        if (reported is not null && (description is null || GenericName().IsMatch(description)))
        {
            return reported;
        }

        return friendly ?? description ?? reported ?? node.InstanceId;
    }

    /// <summary>How the node is attached.</summary>
    public static string BusOf(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var enumerator = node.Enumerator ?? EnumeratorOf(node.InstanceId);
        var bus = Bus(enumerator, node.InstanceId);
        if (bus != "HID")
        {
            return bus;
        }

        // A HID collection is attached the way its parent is (USB, Bluetooth, I²C, or a program's virtual bus).
        if (node.Parent is null)
        {
            return node.InstanceId.Contains("VID_", StringComparison.OrdinalIgnoreCase) ? "USB" : "HID";
        }

        var parentBus = Bus(EnumeratorOf(node.Parent), node.Parent);
        return parentBus switch
        {
            "ACPI" => "Built-in (I²C/SPI)",
            "HID" => "HID",
            _ => parentBus,
        };
    }

    /// <summary>USB/PCI/Bluetooth vendor and product IDs, or a monitor's maker code and model, from the instance or hardware IDs.</summary>
    public static (string? VendorId, string? ProductId) Ids(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        foreach (var id in node.HardwareIds.Prepend(node.InstanceId))
        {
            if (UsbIds().Match(id) is { Success: true } usb)
            {
                return (usb.Groups[1].Value.ToUpperInvariant(), usb.Groups[2].Value.ToUpperInvariant());
            }

            if (BluetoothIds().Match(id) is { Success: true } bluetooth)
            {
                return (bluetooth.Groups[1].Value.ToUpperInvariant(), bluetooth.Groups[2].Value.ToUpperInvariant());
            }

            if (PciIds().Match(id) is { Success: true } pci)
            {
                return (pci.Groups[1].Value.ToUpperInvariant(), pci.Groups[2].Value.ToUpperInvariant());
            }

            if (MonitorIds().Match(id) is { Success: true } monitor)
            {
                return (monitor.Groups[1].Value.ToUpperInvariant(), monitor.Groups[2].Value.ToUpperInvariant());
            }
        }

        return (null, null);
    }

    /// <summary>The first part of an instance ID (the enumerator), e.g. "USB".</summary>
    public static string EnumeratorOf(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var slash = instanceId.IndexOf('\\', StringComparison.Ordinal);
        return slash > 0 ? instanceId[..slash] : instanceId;
    }

    /// <summary>
    /// The device instance ID in a device interface path (<c>\\?\USB#VID_046D&amp;PID_C52B#5&amp;2f3b#{a5dcbf10-…}</c> →
    /// <c>USB\VID_046D&amp;PID_C52B\5&amp;2F3B</c>), or null when it is not one.
    /// </summary>
    public static string? InstanceIdFromInterfacePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return null;
        }

        var body = path[4..];
        var guid = body.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0)
        {
            body = body[..guid];
        }

        return body.Length == 0 ? null : body.Replace('#', '\\').ToUpperInvariant();
    }

    /// <summary>The sort order of kinds (what people look for first).</summary>
    public static int Rank(string kind) => KindRank.GetValueOrDefault(kind, 99);

    /// <summary>The maker for a vendor ID (USB, PCI, or a monitor's PnP code), when known.</summary>
    public static string? VendorName(string? vendorId) => vendorId is not null && Vendors.TryGetValue(vendorId, out var name) ? name : null;

    /// <summary>What a Device Manager problem code means (CM_PROB_*; "Device Manager error messages").</summary>
    public static string ProblemText(int code) => code switch
    {
        1 => "This device is not configured correctly.",
        3 => "The driver may be corrupted, or Windows is running low on memory.",
        10 => "This device cannot start.",
        12 => "This device cannot find enough free resources that it can use.",
        14 => "This device cannot work properly until the PC is restarted.",
        16 => "Windows cannot identify all the resources this device uses.",
        18 => "The drivers for this device need to be reinstalled.",
        19 => "Its configuration in the registry is incomplete or damaged.",
        21 => "Windows is removing this device.",
        22 => "This device is disabled.",
        24 => "This device is not present, is not working properly, or does not have all its drivers installed.",
        28 => "The drivers for this device are not installed.",
        29 => "The device's firmware did not give it the resources it needs (disabled in the firmware).",
        31 => "Windows cannot load the drivers required for this device.",
        32 => "A driver (service) for this device has been disabled.",
        33 or 34 or 35 => "Windows cannot determine the resources or settings this device needs (a firmware update may help).",
        37 => "Windows cannot initialize the device driver.",
        38 => "A previous instance of the driver is still in memory (restart the PC).",
        39 => "Windows cannot load the driver; it may be corrupted or missing.",
        40 => "The driver's service information in the registry is missing or wrong.",
        41 => "The driver loaded but cannot find the hardware.",
        42 => "A duplicate device is already running.",
        43 => "Windows stopped this device because it reported problems.",
        44 => "An application or service shut this device down.",
        45 => "This device is not connected.",
        46 => "Windows is shutting down.",
        47 => "Prepared for safe removal but not removed yet.",
        48 => "The driver is blocked because it is known to have problems with Windows.",
        49 => "The system registry hive is too large for new devices.",
        50 => "Windows cannot apply all of the device's properties.",
        51 => "Waiting for another device to start.",
        52 => "Windows cannot verify the digital signature of the driver.",
        53 => "Reserved for the Windows kernel debugger.",
        54 => "The device failed and is being reset.",
        56 => "Windows is still setting up the class configuration for this device.",
        _ => $"Device Manager reports problem code {code.ToString(CultureInfo.InvariantCulture)}.",
    };

    /// <summary>An audio endpoint (speakers, a microphone) of an audio device.</summary>
    public static bool IsEndpoint(DeviceNodeFacts node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.InstanceId.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The node that attaches a device: on a hub port, a Bluetooth device, a monitor.</summary>
    internal static bool IsDeviceNode(DeviceNodeFacts node)
    {
        var id = node.InstanceId;
        return UsbDeviceNode().IsMatch(id)
               || id.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)
               || id.StartsWith(@"DISPLAY\", StringComparison.OrdinalIgnoreCase);
    }

    private static IoDevice? Device(string key, List<DeviceNodeFacts> nodes, Func<string, DeviceNodeFacts?> lookup)
    {
        var parts = nodes.Select(n => (Node: n, Part: Part(n)!)).Where(p => p.Part is not null).ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        // The device's own node names it; the best-ranked useful part says what it is.
        var useful = parts.Where(p => !p.Part.Technical && p.Part.Kind != "system").OrderBy(p => Rank(p.Part.Kind)).ToList();
        // The node on the hub port: for an "id:" key the one with that vendor and product (a keyboard's emulated game
        // controller is a device node of its own, below the keyboard).
        var deviceNodes = parts.Where(p => IsDeviceNode(p.Node))
            .OrderByDescending(p => key.StartsWith("id:", StringComparison.Ordinal) && Ids(p.Node) is var (v, d) && key == $"id:{v}:{d}")
            .ThenByDescending(p => p.Node.Present)
            .ThenByDescending(p => p.Node.LastArrival)
            .ToList();
        // Without a device node, the device itself names the group, not one of its audio endpoints.
        var anchors = parts.Where(p => !IsEndpoint(p.Node)).ToList();
        var own = deviceNodes.Count > 0 ? deviceNodes[0]
            : useful.Where(p => !IsEndpoint(p.Node)).Concat(anchors).Concat(parts).First();
        var main = useful.Count > 0 ? useful[0] : own;
        var kinds = useful.Select(p => p.Part.Kind).Distinct().OrderBy(Rank).ToList();
        var shown = kinds.Where(k => k is not ("usb" or "input" or "bluetooth" or "other")).ToList();
        var kind = shown.Count > 0 ? shown[0] : kinds.Count > 0 ? kinds[0] : own.Part.Kind;
        var present = parts.Where(p => p.Node.Present).ToList();
        var problem = present.Where(p => !p.Part.Technical || p.Part.Kind == "system" || p.Node.Enumerator is "PCI" or "USB")
            .Select(p => p.Part.ProblemCode).Where(c => c is not (0 or ProblemNotConnected)).DefaultIfEmpty(0).Max();

        // Plumbing on its own: a hub, a virtual device, or nothing but technical nodes without a device node of their own.
        var technical = BusOf(own.Node) == "Virtual" || parts.All(p => IsEndpoint(p.Node)) || (anchors.Count > 0 && anchors.All(p => BusOf(p.Node) == "Virtual"))
                        || (deviceNodes.Count > 0
                            ? deviceNodes.All(p => p.Node.Service is { } s && HubServices.Contains(s)) || (own.Node.Class is "Monitor" && IsTechnical(own.Node))
                            : useful.Count == 0 && parts.All(p => p.Part.Technical));
        var (vendorId, productId) = Ids(own.Node);
        if (vendorId is null)
        {
            (vendorId, productId) = Ids(main.Node);
        }

        var name = NameOf(own.Node);
        if ((PlaceholderName().IsMatch(name) || GenericName().IsMatch(name)) && useful.Select(p => p.Part.Name).FirstOrDefault(n => !GenericName().IsMatch(n) && !PlaceholderName().IsMatch(n)) is { } better)
        {
            name = better; // "Wireless_Device" → "MediaTek Bluetooth Adapter"
        }

        var type = Clean(own.Node.Description);
        var driverNode = main.Node.DriverVersion is not null ? main.Node : own.Node;
        var connected = present.Count(p => IsDeviceNode(p.Node) && (!key.StartsWith("id:", StringComparison.Ordinal) || (Ids(p.Node) is var (v, d) && key == $"id:{v}:{d}")));
        return new IoDevice(
            key,
            name,
            kind,
            kinds,
            type is not null && !string.Equals(type, name, StringComparison.OrdinalIgnoreCase) ? type : null,
            main.Node.Class ?? own.Node.Class,
            BusOf(own.Node),
            VendorName(vendorId) ?? Maker(own.Node.Manufacturer, vendorId) ?? Maker(main.Node.Manufacturer, vendorId),
            vendorId,
            productId,
            present.Count > 0,
            present.Count > 0 ? Math.Max(1, connected) : 0,
            parts.Any(p => IsExternal(p.Node)),
            technical,
            problem,
            problem == 0 ? null : ProblemText(problem),
            own.Node.InstanceId,
            Clean(own.Node.Location),
            driverNode.Service,
            driverNode.DriverProvider,
            driverNode.DriverVersion,
            Date(driverNode.DriverDate),
            driverNode.DriverInf,
            own.Node.HardwareIds.Count > 0 ? own.Node.HardwareIds[0] : null,
            parts.Select(p => p.Node.FirstInstalled).Where(d => d is not null).DefaultIfEmpty(null).Min(),
            parts.Select(p => p.Node.LastArrival).Where(d => d is not null).DefaultIfEmpty(null).Max(),
            parts.Select(p => p.Node.LastRemoval).Where(d => d is not null).DefaultIfEmpty(null).Max(),
            parts.OrderByDescending(p => p.Node.Present).ThenBy(p => p.Part.Technical).ThenBy(p => Rank(p.Part.Kind)).Take(MaxParts).Select(p => p.Part).ToList());
    }

    private static bool IsHub(DeviceNodeFacts node) =>
        node.InstanceId.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase) || (node.Service is { } service && HubServices.Contains(service));

    private static string? BluetoothAddress(string instanceId)
    {
        if (!instanceId.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = BluetoothAddressPattern().Match(instanceId);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static bool IsAttachedBus(string? enumerator) =>
        enumerator is not null && (enumerator.StartsWith("USB", StringComparison.OrdinalIgnoreCase) || enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) || enumerator.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase));

    private static string Bus(string enumerator, string instanceId) => enumerator.ToUpperInvariant() switch
    {
        "USB" or "USBSTOR" or "USBPRINT" or "USB4" or "UASPSTOR" => "USB",
        "BTHENUM" or "BTH" => "Bluetooth",
        "BTHLE" or "BTHLEDEVICE" => "Bluetooth LE",
        "HID" when instanceId.Contains("{00001124-", StringComparison.OrdinalIgnoreCase) => "Bluetooth",
        "HID" when instanceId.Contains("{00001812-", StringComparison.OrdinalIgnoreCase) => "Bluetooth LE",
        "HID" => "HID",
        "PCI" => "PCI",
        "HDAUDIO" or "INTELAUDIO" or "SOUNDWIRE" => "HD Audio",
        "DISPLAY" or "MONITOR" => "Display",
        "ACPI" or "ACPI_HAL" => "ACPI",
        "SCSI" or "STORAGE" or "NVME" => "Storage",
        "SWD" when instanceId.StartsWith(@"SWD\PRINTENUM\", StringComparison.OrdinalIgnoreCase) => "Printing",
        "SWD" when instanceId.StartsWith(@"SWD\DAFWSDProvider\", StringComparison.OrdinalIgnoreCase) || instanceId.StartsWith(@"SWD\Escl\", StringComparison.OrdinalIgnoreCase) => "Network",
        "SWD" when instanceId.StartsWith(@"SWD\MMDEVAPI\", StringComparison.OrdinalIgnoreCase) => "Audio endpoint",
        "SWD" when instanceId.StartsWith(@"SWD\WPDBUSENUM\", StringComparison.OrdinalIgnoreCase) => "Storage",
        _ => "Virtual",
    };

    /// <summary>
    /// The driver's manufacturer when it names the maker: "(Standard system devices)" does not, and neither does
    /// "Microsoft" or "WinUsb Device" for a device with another vendor's ID (Windows' own driver).
    /// </summary>
    private static string? Maker(string? manufacturer, string? vendorId)
    {
        var text = Clean(manufacturer);
        if (text is null || text.StartsWith('(') || text.StartsWith("WinUsb", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Generic", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return vendorId is not null && vendorId != "045E" && text.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static string? Date(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // INF-style references ("@input.inf,%hid_device%;USB Input Device") keep only the text after the semicolon.
        var semicolon = text.StartsWith('@') ? text.LastIndexOf(';') : -1;
        return (semicolon >= 0 ? text[(semicolon + 1)..] : text).Trim();
    }

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbIds();

    [GeneratedRegex(@"VID&[0-9A-F]{4}([0-9A-F]{4})_PID&([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothIds();

    [GeneratedRegex(@"(?:VEN|VID)_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PciIds();

    [GeneratedRegex(@"^(?:MONITOR|DISPLAY)\\([A-Z]{3})([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonitorIds();

    [GeneratedRegex(@"(?:DEV_|BLUETOOTHDEVICE_|[&_])([0-9A-F]{12})(?=$|[\\&_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BluetoothAddressPattern();

    // The node a hub port shows (USB\VID_1532&PID_02A7\serial), not an interface (&MI_), an XInput (&IG_) or a lamp array node.
    [GeneratedRegex(@"^USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}(&REV_[0-9A-F]{4})?\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbDeviceNode();

    // A name that is a placeholder rather than a product name.
    [GeneratedRegex(@"_|^(USB Device|Device|Wireless Device)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderName();

    // "Speakers (2- Realtek(R) Audio)" → "Realtek(R) Audio" (Windows numbers a second instance "2- ").
    [GeneratedRegex(@"^.+?\((?:\d+- )?(.+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointDevice();

    [GeneratedRegex(@"^Generic (?:PnP )?Monitor \((.+)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericMonitor();

    [GeneratedRegex(@"game ?controller|gamepad|joystick|controller \(xbox|dualsense|dualshock", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameController();

    [GeneratedRegex(@"scan", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Scanner();

    [GeneratedRegex(@"^(USB Composite Device|USB Input Device|USB Mass Storage Device|USB Printing Support|USB Audio Device|USB Video Device|USB Serial Device.*|HID-compliant .*|HID Keyboard Device|Generic .*|Standard .*|Bluetooth Device|WinUsb Device|Disk drive|Disk)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericName();

    [GeneratedRegex(@"^(HID-compliant (vendor-defined device|consumer control device|system controller|device|wireless radio controls)|USB Input Device|USB Composite Device|Generic (USB|SuperSpeed USB) Hub|USB Root Hub.*|.*Host Controller.*|.*\bEnumerator\b.*|Bluetooth Device \(.*\)|.*\bUCM-UCSI\b.*|.*UcmCx.*|USB4.*Router.*|USB Connector Manager|Microsoft Bluetooth .*|Portable Device Enumerator|(Converted )?Portable Device Control device)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechnicalName();
}
