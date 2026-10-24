using PcDiag.Core.Devices;
using PcDiag.Core.Events;

namespace PcDiag.Core.Tests.Devices;

/// <summary>Device nodes as Windows' device tree describes a wireless receiver, a keyboard, Bluetooth earbuds and hubs.</summary>
internal static class Nodes
{
    public const string RootHub = @"USB\ROOT_HUB30\4&1141AC06&0&0";
    public const string Hub = @"USB\VID_05E3&PID_0610\5&230B5917&0&3";
    public const string Receiver = @"USB\VID_046D&PID_C54D\377235823234";
    public const string ReceiverKeyboardInterface = @"USB\VID_046D&PID_C54D&MI_01\7&2F3B&0&0001";
    public const string ReceiverKeyboard = @"HID\VID_046D&PID_C54D&MI_01&COL01\8&629C800&0&0000";
    public const string ReceiverMouseInterface = @"USB\VID_046D&PID_C54D&MI_00\7&2F3B&0&0000";
    public const string ReceiverMouse = @"HID\VID_046D&PID_C54D&MI_00\8&2F063B5E&0&0000";
    public const string Keyboard = @"USB\VID_1532&PID_02A7\5&230B5917&0&5";
    public const string KeyboardInterface = @"USB\VID_1532&PID_02A7&MI_01\6&72718DA&0&0001";
    public const string KeyboardGamepad = @"USB\VID_045E&PID_028E&REV_0110\01";
    public const string Earbuds = @"BTHENUM\DEV_E8C913314F45\8&1A2B&0&BLUETOOTHDEVICE_E8C913314F45";
    public const string EarbudsAudio = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&00010075_PID&A013\7&3C&0&E8C913314F45_C00000000";
    public const string Wifi = @"PCI\VEN_14C3&DEV_7925&SUBSYS_60001A3B&REV_00\4&BE8BA64&0&00E2";
    public const string Monitor = @"DISPLAY\AUS32F4\5&12862C70&0&UID4353";
    public const string GhubKeyboard = @"LGHUBDEVICE\VID_046D&PID_C232\1&79F5D87&3&01";

    public static readonly DateTimeOffset Installed = new(2025, 6, 18, 9, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Arrived = new(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);

    public static DeviceNodeFacts Node(string id, string? cls, string? description, string? service = null, string? parent = null, bool present = true, string? friendly = null,
        string? reported = null, bool? local = false, int problem = 0, string? container = null) =>
        new(id, friendly, description, reported, cls, "(Standard system devices)", IoDevices.EnumeratorOf(id), service, present ? parent : null, container, local, present, problem, null, [],
            "Microsoft", "10.0.26100.1", Installed, null, Installed, present ? Arrived : Arrived.AddDays(-3), present ? null : Arrived.AddDays(-1));

    /// <summary>A receiver for a keyboard and a mouse behind a hub, a keyboard with an emulated game controller, earbuds, a monitor, a disabled Wi-Fi card.</summary>
    public static List<DeviceNodeFacts> Tree(bool receiverPresent = true) =>
    [
        Node(RootHub, "USB", "USB Root Hub (USB 3.0)", "USBHUB3", local: true),
        Node(Hub, "USB", "Generic USB Hub", "USBHUB3", RootHub, reported: "USB2.0 Hub"),
        Node(Receiver, "USB", "LIGHTSPEED Receiver", "usbccgp", Hub, receiverPresent, friendly: "LIGHTSPEED Receiver", reported: "USB Receiver"),
        Node(ReceiverKeyboardInterface, "HIDClass", "USB Input Device", "HidUsb", Receiver, receiverPresent),
        Node(ReceiverKeyboard, "Keyboard", "HID Keyboard Device", "kbdhid", ReceiverKeyboardInterface, receiverPresent),
        Node(ReceiverMouseInterface, "HIDClass", "USB Input Device", "HidUsb", Receiver, receiverPresent),
        Node(ReceiverMouse, "Mouse", "HID-compliant mouse", "mouhid", ReceiverMouseInterface, receiverPresent),
        Node(Keyboard, "USB", "USB Composite Device", "usbccgp", RootHub, reported: "Razer Huntsman V3 Pro TKL"),
        Node(KeyboardInterface, "USB", "USB Composite Device", "usbccgp", Keyboard),
        Node(KeyboardGamepad, "XnaComposite", "Xbox 360 Controller for Windows", "xusb22", KeyboardInterface, reported: "Controller"),
        Node(Earbuds, "Bluetooth", "Bluetooth Device", null, @"BTH\MS_BTHBRB\7&1", present: false, friendly: "Galaxy Buds3 Pro"),
        Node(EarbudsAudio, "MEDIA", "Microsoft Bluetooth A2dp Source", "BthA2dp", @"BTH\MS_BTHBRB\7&1", present: false, friendly: "Galaxy Buds3 Pro"),
        Node(Wifi, "Net", null, "mtkwecx", @"PCI\VEN_8086&DEV_AE4D", friendly: "MediaTek Wi-Fi 7 MT7925 Wireless LAN Card", local: true, problem: IoDevices.ProblemDisabled),
        Node(Monitor, "Monitor", "Generic PnP Monitor", "monitor", @"PCI\VEN_10DE&DEV_2684", friendly: "Generic Monitor (PG32UCDP)") with { HardwareIds = [@"MONITOR\AUS32F4"] },
        Node(GhubKeyboard, "Keyboard", "HID Keyboard Device", "kbdhid", @"ROOT\LGHUB\0000", friendly: "Logitech G HUB Virtual Keyboard"),
    ];
}

public sealed class IoDeviceTests
{
    [Fact]
    public void The_nodes_of_one_physical_device_become_one_device_named_by_its_own_node()
    {
        var devices = IoDevices.Group(Nodes.Tree());

        var receiver = devices.Single(d => d.Key == "id:046D:C54D");
        receiver.Name.ShouldBe("LIGHTSPEED Receiver");
        receiver.Kinds.ShouldBe(["keyboard", "mouse"]);
        receiver.Kind.ShouldBe("keyboard");
        receiver.Manufacturer.ShouldBe("Logitech");
        (receiver.Present, receiver.Connected, receiver.External, receiver.Technical).ShouldBe((true, 1, true, false));
        receiver.Bus.ShouldBe("USB");
        receiver.Parts.Count.ShouldBe(5);
        receiver.FirstInstalled.ShouldBe(Nodes.Installed);

        // The keyboard's emulated game controller is a part of the keyboard, which names the device.
        var keyboard = devices.Single(d => d.Key == "id:1532:02A7");
        keyboard.Name.ShouldBe("Razer Huntsman V3 Pro TKL");
        keyboard.Kinds.ShouldContain("game-controller");
        keyboard.Manufacturer.ShouldBe("Razer");
        keyboard.Connected.ShouldBe(1);
    }

    [Fact]
    public void Bluetooth_services_join_their_device_and_disconnected_devices_stay_registered()
    {
        var earbuds = IoDevices.Group(Nodes.Tree()).Single(d => d.Key == "bt:E8C913314F45");

        earbuds.Name.ShouldBe("Galaxy Buds3 Pro");
        earbuds.Kind.ShouldBe("audio");
        earbuds.Present.ShouldBeFalse();
        earbuds.Connected.ShouldBe(0);
        earbuds.LastRemoval.ShouldBe(Nodes.Arrived.AddDays(-1));
    }

    [Fact]
    public void Hubs_and_programs_virtual_devices_are_plumbing_and_the_PCs_own_devices_only_count_with_a_problem()
    {
        var devices = IoDevices.Group(Nodes.Tree());

        devices.Single(d => d.Key == "id:05E3:0610").Technical.ShouldBeTrue();
        devices.Single(d => d.Name == "Logitech G HUB Virtual Keyboard").Technical.ShouldBeTrue();
        devices.Single(d => d.Name == "Logitech G HUB Virtual Keyboard").Bus.ShouldBe("Virtual");

        var wifi = devices.Single(d => d.Key == "id:14C3:7925");
        (wifi.Kind, wifi.ProblemCode, wifi.Problem, wifi.External, wifi.Technical).ShouldBe(("system", 22, "This device is disabled.", false, false));

        var monitor = devices.Single(d => d.Key == "display:AUS32F4");
        (monitor.Name, monitor.Manufacturer, monitor.Kind).ShouldBe(("PG32UCDP", "ASUS", "display"));
    }

    [Fact]
    public void Audio_endpoints_join_the_device_they_name_and_a_programs_endpoints_stay_plumbing()
    {
        const string Codec = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_1168&SUBSYS_104388EF&REV_1000\5&1A&0&0001";
        var tree = Nodes.Tree();
        tree.Add(Nodes.Node(Codec, "MEDIA", "Realtek High Definition Audio", "IntcAzAudAddService", @"PCI\VEN_8086&DEV_7F50", friendly: "Realtek(R) Audio", local: true));
        tree.Add(Nodes.Node(@"SWD\MMDEVAPI\{0.0.0.00000000}.{AAA}", "AudioEndpoint", "Audio Endpoint", null, @"SWD\MMDEVAPI\MMDEVAPI", friendly: "Speakers (2- Realtek(R) Audio)", local: true));
        tree.Add(Nodes.Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{BBB}", "AudioEndpoint", "Audio Endpoint", null, @"SWD\MMDEVAPI\MMDEVAPI", friendly: "Remote Audio", local: true));

        // The previous motherboard's codec, with the same name, is remembered but not connected.
        tree.Insert(0, Nodes.Node(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_1849A11B&REV_1000\4&9&0&0001", "MEDIA", "Realtek High Definition Audio", "IntcAzAudAddService", null, present: false, friendly: "Realtek(R) Audio", local: true));

        // A program's virtual microphone with its endpoint.
        tree.Add(Nodes.Node(@"ROOT\SteamStreamingMicrophone\0000", "MEDIA", "Steam Streaming Microphone", "SteamStreamingMicrophone", @"HTREE\ROOT\0", friendly: "Steam Streaming Microphone", local: true));
        tree.Add(Nodes.Node(@"SWD\MMDEVAPI\{0.0.1.00000000}.{CCC}", "AudioEndpoint", "Audio Endpoint", null, @"SWD\MMDEVAPI\MMDEVAPI", friendly: "Microphone (Steam Streaming Microphone)", local: true));

        var devices = IoDevices.Group(tree);

        var realtek = devices.Single(d => d.Name == "Realtek(R) Audio" && d.Present);
        realtek.Key.ShouldBe("id:10EC:1168");
        realtek.Parts.Select(p => p.Name).ShouldBe(["Realtek(R) Audio", "Speakers (2- Realtek(R) Audio)"], ignoreOrder: true);
        (realtek.Bus, realtek.Manufacturer, realtek.External).ShouldBe(("HD Audio", "Realtek", false));
        devices.Single(d => d.Key == "id:10EC:0897").Present.ShouldBeFalse();

        var steam = devices.Single(d => d.Key == @"ROOT\SteamStreamingMicrophone\0000");
        (steam.Name, steam.Technical, steam.Parts.Count).ShouldBe(("Steam Streaming Microphone", true, 2));
        devices.Single(d => d.Name == "Remote Audio").Technical.ShouldBeTrue();
        devices.ShouldNotContain(d => d.Name.StartsWith("Speakers", StringComparison.Ordinal) || d.Name.StartsWith("Microphone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disconnected_receiver_is_keyed_by_its_IDs_so_it_matches_when_it_comes_back()
    {
        var devices = IoDevices.Group(Nodes.Tree(receiverPresent: false));

        var receiver = devices.Single(d => d.Key == "id:046D:C54D");
        receiver.Present.ShouldBeFalse();
        receiver.Name.ShouldBe("LIGHTSPEED Receiver");
        receiver.Kinds.ShouldBe(["keyboard", "mouse"]);
    }

    [Theory]
    [InlineData(@"\\?\USB#VID_046D&PID_C52B#5&2f3b&0&3#{a5dcbf10-6530-11d2-901f-00c04fb951ed}", @"USB\VID_046D&PID_C52B\5&2F3B&0&3")]
    [InlineData(@"\\?\HID#VID_046D&PID_C52B&MI_00#7&1a&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", @"HID\VID_046D&PID_C52B&MI_00\7&1A&0&0000")]
    [InlineData("not a path", null)]
    public void Interface_paths_give_the_instance_ID(string path, string? expected) =>
        IoDevices.InstanceIdFromInterfacePath(path).ShouldBe(expected);

    [Theory]
    [InlineData("PCI standard ISA bridge", "8086", "7F04", "ROG MAXIMUS Z890 HERO", "Intel(R) Core(TM) Ultra 9 285K", "Intel Z890")]
    [InlineData("Intel(R) Z790 Chipset LPC/eSPI Controller - 7A04", "8086", "7A04", "PRIME B760M-A", null, "Intel Z790")]
    [InlineData(null, null, null, "ROG STRIX X870E-E GAMING WIFI", "AMD Ryzen 9 9950X3D", "AMD X870E")]
    [InlineData(null, null, null, "MAG B650 TOMAHAWK WIFI (MS-7D75)", "AMD Ryzen 7 7800X3D", "AMD B650")]
    [InlineData("PCI standard ISA bridge", "8086", "7F04", "System Product Name", null, "Intel 800 Series")]
    [InlineData(null, null, null, "0X8DXD", null, null)]
    public void The_chipset_comes_from_the_bridge_the_board_name_or_the_bridge_ID(string? bridge, string? vendor, string? device, string? board, string? cpu, string? expected) =>
        BoardFacts.Chipset(bridge, vendor, device, board, cpu).ShouldBe(expected);
}

public sealed class DeviceEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, int> Present(IEnumerable<DeviceNodeFacts> nodes) => nodes.Where(n => n.Present).ToDictionary(n => n.InstanceId, n => n.ProblemCode, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Plugging_in_a_receiver_is_one_event_for_the_device_not_one_per_node()
    {
        var before = Nodes.Tree(receiverPresent: false);
        var after = Nodes.Tree();
        var lookup = after.ToDictionary(n => n.InstanceId, StringComparer.OrdinalIgnoreCase);
        var notes = new List<DeviceChangeNote> { new(Now.AddSeconds(-2), Nodes.Receiver, Arrived: true) };

        var changes = DeviceEvents.Compare(Present(before), Present(after), notes, Now);
        changes.Count.ShouldBe(5);
        changes.Single(c => c.InstanceId == Nodes.Receiver).At.ShouldBe(Now.AddSeconds(-2));

        var events = new DeviceEvents().Describe(changes, id => lookup.GetValueOrDefault(id), Now);

        var connected = events.ShouldHaveSingleItem();
        connected.Channel.ShouldBe(DeviceEvents.Channel);
        connected.Title.ShouldBe("Device connected: LIGHTSPEED Receiver (keyboard, mouse)");
        connected.Category.ShouldBe(EventCategory.Device);
        connected.Data!["vendorId"].ShouldBe("046D");
        connected.Timestamp.ShouldBe(Now.AddSeconds(-2));
    }

    [Fact]
    public void A_device_that_keeps_dropping_out_is_a_warning_once()
    {
        var describer = new DeviceEvents();
        var connected = Present(Nodes.Tree());
        var gone = Present(Nodes.Tree(receiverPresent: false));
        var unplugged = Nodes.Tree(receiverPresent: false).ToDictionary(n => n.InstanceId, StringComparer.OrdinalIgnoreCase);
        var events = new List<SystemEvent>();
        for (var i = 0; i < 4; i++)
        {
            var at = Now.AddMinutes(i);
            events.AddRange(describer.Describe(DeviceEvents.Compare(connected, gone, [], at), id => unplugged.GetValueOrDefault(id), at));
        }

        events.Count(e => e.EventId == (int)DeviceEvents.Id.Disconnected).ShouldBe(4);
        var warning = events.Single(e => e.EventId == (int)DeviceEvents.Id.KeepsReconnecting);
        warning.Level.ShouldBe(EventLevel.Warning);
        warning.Title.ShouldStartWith("Device keeps disconnecting: LIGHTSPEED Receiver (3 times");
    }

    [Fact]
    public void A_short_drop_out_between_scans_is_still_seen()
    {
        var present = Present(Nodes.Tree());
        var lookup = Nodes.Tree().ToDictionary(n => n.InstanceId, StringComparer.OrdinalIgnoreCase);
        var notes = new List<DeviceChangeNote> { new(Now.AddSeconds(-3), Nodes.Receiver, Arrived: false), new(Now.AddSeconds(-2), Nodes.Receiver, Arrived: true) };

        var changes = DeviceEvents.Compare(present, present, notes, Now);

        changes.ShouldHaveSingleItem().Kind.ShouldBe(NodeChangeKind.Reconnected);
        new DeviceEvents().Describe(changes, id => lookup.GetValueOrDefault(id), Now).ShouldHaveSingleItem().Title.ShouldStartWith("Device dropped out and came back: LIGHTSPEED Receiver");
    }

    [Fact]
    public void A_device_of_the_PC_that_disappears_or_fails_is_a_warning()
    {
        var tree = Nodes.Tree();
        var lookup = tree.ToDictionary(n => n.InstanceId, StringComparer.OrdinalIgnoreCase);
        var before = Present(tree);
        before[Nodes.Wifi] = 0;

        // Working → disabled is information; a device that fails (code 43) is a warning.
        var disabled = new DeviceEvents().Describe(DeviceEvents.Compare(before, Present(tree), [], Now), id => lookup.GetValueOrDefault(id), Now).ShouldHaveSingleItem();
        (disabled.Title, disabled.Level).ShouldBe(("Device disabled: MediaTek Wi-Fi 7 MT7925 Wireless LAN Card", EventLevel.Information));

        var failing = Present(tree);
        failing[Nodes.Wifi] = 43;
        var failed = new DeviceEvents().Describe(DeviceEvents.Compare(before, failing, [], Now), id => lookup.GetValueOrDefault(id), Now).ShouldHaveSingleItem();
        failed.Level.ShouldBe(EventLevel.Warning);
        failed.Message.ShouldContain("Windows stopped this device because it reported problems");

        var removed = Present(tree);
        removed.Remove(Nodes.Wifi);
        var gone = new DeviceEvents().Describe(DeviceEvents.Compare(before, removed, [], Now), id => lookup.GetValueOrDefault(id) is { } n && n.InstanceId == Nodes.Wifi ? n with { Present = false } : lookup.GetValueOrDefault(id), Now)
            .ShouldHaveSingleItem();
        (gone.EventId, gone.Severity).ShouldBe(((int)DeviceEvents.Id.InternalRemoved, Severity.High));
    }

    [Fact]
    public void Programs_virtual_devices_coming_and_going_are_not_news()
    {
        var tree = Nodes.Tree();
        var lookup = tree.ToDictionary(n => n.InstanceId, StringComparer.OrdinalIgnoreCase);
        var before = Present(tree);
        before.Remove(Nodes.GhubKeyboard);

        new DeviceEvents().Describe(DeviceEvents.Compare(before, Present(tree), [], Now), id => lookup.GetValueOrDefault(id), Now).ShouldBeEmpty();
    }
}
