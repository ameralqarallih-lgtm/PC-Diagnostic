using System.Globalization;
using PcDiag.Core.Events;

namespace PcDiag.Core.Devices;

/// <summary>What happened to one device node between two scans of the device tree.</summary>
public enum NodeChangeKind
{
    /// <summary>Connected.</summary>
    Connected,

    /// <summary>Removed.</summary>
    Disconnected,

    /// <summary>Removed and connected again between two scans (a short drop-out).</summary>
    Reconnected,

    /// <summary>Was working, now has a problem.</summary>
    Failed,

    /// <summary>Was working, now disabled.</summary>
    Disabled,

    /// <summary>Had a problem, now works.</summary>
    Recovered,
}

/// <summary>A notification that a device node arrived or was removed, with when it was received.</summary>
public readonly record struct DeviceChangeNote(DateTimeOffset At, string InstanceId, bool Arrived);

/// <summary>One device node's change.</summary>
public sealed record NodeChange(string InstanceId, NodeChangeKind Kind, DateTimeOffset At, int ProblemCode);

/// <summary>
/// Turns changes in the device tree into the recorder's own events (channel <see cref="Channel"/>): devices connected,
/// disconnected or dropping out, devices that keep reconnecting, devices of the PC itself that disappear, and devices
/// that stop working. The nodes of one physical device (a headset's audio, HID and Bluetooth nodes) become one event.
/// </summary>
public sealed class DeviceEvents
{
    /// <summary>Channel of device events.</summary>
    public const string Channel = "pcdiag.devices";

    /// <summary>Provider name of the recorder's own events.</summary>
    public const string Provider = "PcDiagnostic";

    /// <summary>A device that disconnects this often within <see cref="FlapWindow"/> keeps reconnecting.</summary>
    public const int FlapCount = 3;

    /// <summary>Most device events per minute (a failing hub can remove dozens of nodes at once, repeatedly).</summary>
    public const int MaxEventsPerMinute = 30;

    /// <summary>The window in which disconnections are counted.</summary>
    public static readonly TimeSpan FlapWindow = TimeSpan.FromMinutes(10);

    // Buses of real hardware: a node of the PC itself on one of these that disappears is worth a warning.
    private static readonly HashSet<string> HardwareEnumerators = new(StringComparer.OrdinalIgnoreCase) { "PCI", "USB", "USBSTOR", "HDAUDIO", "SCSI", "NVME", "UASPSTOR" };

    private readonly Dictionary<string, Queue<DateTimeOffset>> _drops = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _warned = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<DateTimeOffset> _emitted = new();

    /// <summary>Event IDs.</summary>
    public enum Id
    {
        /// <summary>A device was connected.</summary>
        Connected = 1,

        /// <summary>A device was disconnected.</summary>
        Disconnected = 2,

        /// <summary>A device dropped out and came back.</summary>
        Reconnected = 3,

        /// <summary>A device keeps disconnecting.</summary>
        KeepsReconnecting = 4,

        /// <summary>A device of the PC itself disappeared.</summary>
        InternalRemoved = 5,

        /// <summary>A device stopped working.</summary>
        Failed = 6,

        /// <summary>A device was disabled.</summary>
        Disabled = 7,

        /// <summary>A device works again.</summary>
        Recovered = 8,
    }

    /// <summary>
    /// The changes between two scans of the connected nodes (instance ID → problem code). The notifications received in
    /// between give the times, and catch nodes that were removed and came back before the second scan.
    /// </summary>
    public static List<NodeChange> Compare(IReadOnlyDictionary<string, int> before, IReadOnlyDictionary<string, int> after, IReadOnlyList<DeviceChangeNote> notes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(notes);
        var arrived = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var removed = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var note in notes)
        {
            (note.Arrived ? arrived : removed).TryAdd(note.InstanceId, note.At);
        }

        var changes = new List<NodeChange>();
        foreach (var (id, problem) in after)
        {
            if (!before.TryGetValue(id, out var previous))
            {
                changes.Add(new NodeChange(id, NodeChangeKind.Connected, arrived.GetValueOrDefault(id, now), problem));
            }
            else if (previous == 0 && problem != 0)
            {
                changes.Add(new NodeChange(id, problem == IoDevices.ProblemDisabled ? NodeChangeKind.Disabled : NodeChangeKind.Failed, now, problem));
            }
            else if (previous != 0 && problem == 0)
            {
                changes.Add(new NodeChange(id, NodeChangeKind.Recovered, now, 0));
            }
            else if (removed.TryGetValue(id, out var at) && arrived.ContainsKey(id))
            {
                changes.Add(new NodeChange(id, NodeChangeKind.Reconnected, at, problem));
            }
        }

        foreach (var id in before.Keys.Where(id => !after.ContainsKey(id)))
        {
            changes.Add(new NodeChange(id, NodeChangeKind.Disconnected, removed.GetValueOrDefault(id, now), 0));
        }

        // Connected and gone again before the scan.
        foreach (var (id, at) in arrived.Where(a => !before.ContainsKey(a.Key) && !after.ContainsKey(a.Key) && removed.ContainsKey(a.Key)))
        {
            changes.Add(new NodeChange(id, NodeChangeKind.Reconnected, at, 0));
        }

        return changes;
    }

    /// <summary>The events for a set of changes; <paramref name="facts"/> describes a node (connected or not).</summary>
    public IReadOnlyList<SystemEvent> Describe(IReadOnlyList<NodeChange> changes, Func<string, DeviceNodeFacts?> facts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(facts);
        var events = new List<SystemEvent>();
        var nodes = changes.Select(c => (Change: c, Node: facts(c.InstanceId))).Where(n => n.Node is not null).Select(n => (n.Change, Node: n.Node!)).ToList();

        // Problems are about one node.
        foreach (var (change, node) in nodes.Where(n => n.Change.Kind is NodeChangeKind.Failed or NodeChangeKind.Disabled or NodeChangeKind.Recovered))
        {
            if (Reportable(node) && Problem(change, node) is { } problem)
            {
                events.Add(problem);
            }
        }

        // Connections are about physical devices: group the nodes of each one.
        var connections = nodes.Where(n => n.Change.Kind is NodeChangeKind.Connected or NodeChangeKind.Disconnected or NodeChangeKind.Reconnected).ToList();
        foreach (var group in connections.GroupBy(n => IoDevices.KeyOf(n.Node, facts).Key, StringComparer.OrdinalIgnoreCase))
        {
            events.AddRange(Connection(group.Key, group.ToList(), facts, now));
        }

        events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return Limit(events, now);
    }

    // Failing plumbing of the PC's own buses matters (a USB controller); failing software devices are noise.
    private static bool Reportable(DeviceNodeFacts node) =>
        IoDevices.Part(node with { Present = true }) is { } part && IoDevices.BusOf(node) != "Virtual"
        && (!part.Technical || HardwareEnumerators.Contains(node.Enumerator ?? IoDevices.EnumeratorOf(node.InstanceId)) || string.Equals(node.Enumerator, "ACPI", StringComparison.OrdinalIgnoreCase));

    private static SystemEvent? Problem(NodeChange change, DeviceNodeFacts node)
    {
        var name = IoDevices.NameOf(node);
        var code = change.ProblemCode.ToString(CultureInfo.InvariantCulture);
        var driver = Driver(node);
        var data = Data(node);
        data["problemCode"] = code;
        return change.Kind switch
        {
            NodeChangeKind.Failed => Event(change.At, Id.Failed, EventLevel.Warning, Severity.Medium, $"Device stopped working: {name} (code {code})",
                $"{name} ({Describe(node)}) stopped working: {IoDevices.ProblemText(change.ProblemCode)} (Device Manager code {code}).{driver} Disconnecting and reconnecting it, updating or reinstalling its driver, or restarting the PC usually clears it.",
                name, "failed", data),
            NodeChangeKind.Disabled => Event(change.At, Id.Disabled, EventLevel.Information, Severity.Low, $"Device disabled: {name}",
                $"{name} ({Describe(node)}) was disabled (Device Manager code 22), by a person or a program. It stays off until it is enabled again in Device Manager.",
                name, "disabled", data),
            NodeChangeKind.Recovered => Event(change.At, Id.Recovered, EventLevel.Information, Severity.Info, $"Device works again: {name}",
                $"{name} ({Describe(node)}) works again.", name, "recovered", data),
            _ => null,
        };
    }

    private IEnumerable<SystemEvent> Connection(string key, List<(NodeChange Change, DeviceNodeFacts Node)> group, Func<string, DeviceNodeFacts?> facts, DateTimeOffset now)
    {
        if (group.All(n => IoDevices.IsEndpoint(n.Node)))
        {
            yield break; // the device's own nodes tell its story; its speakers and microphones come and go with it
        }

        var devices = group.Select(n => (n.Change, n.Node, Part: IoDevices.Part(n.Node with { Present = true }))).ToList();
        var useful = devices.Where(d => d.Part is { Technical: false } && d.Part.Kind != "system" && IoDevices.BusOf(d.Node) != "Virtual").ToList();
        var own = IoDevices.KeyOf(group[0].Node, facts).Device ?? group.Select(n => n.Node).FirstOrDefault(IoDevices.IsDeviceNode);
        if (!group.Any(n => IoDevices.IsExternal(n.Node)))
        {
            // The PC's own devices: only real hardware disappearing (or coming back) is news.
            var hardware = group.Where(n => HardwareEnumerators.Contains(n.Node.Enumerator ?? IoDevices.EnumeratorOf(n.Node.InstanceId)) && !string.Equals(n.Node.Class, "Volume", StringComparison.OrdinalIgnoreCase)
                                            && (IoDevices.Part(n.Node with { Present = true }) is { Technical: false } || string.Equals(n.Node.Enumerator, "PCI", StringComparison.OrdinalIgnoreCase))).ToList();
            if (hardware.Count > 0)
            {
                yield return Internal(hardware[0].Change, own is not null && !IoDevices.IsTechnical(own) ? own : hardware[0].Node);
            }

            yield break;
        }

        var lead = own ?? useful.OrderBy(d => IoDevices.Rank(d.Part!.Kind)).Select(d => d.Node).FirstOrDefault();
        if (lead is null || IoDevices.BusOf(lead) == "Virtual" || (useful.Count == 0 && IoDevices.IsTechnical(lead) && !IoDevices.IsDeviceNode(lead)))
        {
            yield break; // only plumbing changed (a Bluetooth service, a program's virtual device)
        }

        var name = IoDevices.NameOf(lead);
        var kinds = useful.Select(d => d.Part!.Kind).Where(k => k is not ("usb" or "input" or "bluetooth" or "other")).Distinct().OrderBy(IoDevices.Rank).ToList();
        var what = kinds.Count > 0 ? $" ({string.Join(", ", kinds.Select(Label))})" : string.Empty;
        var data = Data(lead);
        data["nodes"] = group.Count.ToString(CultureInfo.InvariantCulture);
        if (kinds.Count > 0)
        {
            data["kinds"] = string.Join(",", kinds);
        }

        var connected = group.Any(n => n.Change.Kind == NodeChangeKind.Connected);
        var disconnected = group.Any(n => n.Change.Kind == NodeChangeKind.Disconnected);
        var at = group.Min(n => n.Change.At);
        var bus = IoDevices.BusOf(lead);
        if (connected && !disconnected && group.All(n => n.Change.Kind == NodeChangeKind.Connected))
        {
            yield return Event(at, Id.Connected, EventLevel.Information, Severity.Info, $"Device connected: {name}{what}", $"{name}{what} was connected ({bus}).", name, "connected", data);
            foreach (var failing in devices.Where(d => d.Change.ProblemCode is not (0 or IoDevices.ProblemNotConnected) && d.Part is { Technical: false }))
            {
                yield return Event(at, Id.Failed, EventLevel.Warning, Severity.Medium, $"Device connected but not working: {IoDevices.NameOf(failing.Node)} (code {failing.Change.ProblemCode.ToString(CultureInfo.InvariantCulture)})",
                    $"{IoDevices.NameOf(failing.Node)} ({Describe(failing.Node)}) was connected but does not work: {IoDevices.ProblemText(failing.Change.ProblemCode)} (Device Manager code {failing.Change.ProblemCode.ToString(CultureInfo.InvariantCulture)}).{Driver(failing.Node)}",
                    IoDevices.NameOf(failing.Node), "failed", Data(failing.Node));
            }

            yield break;
        }

        var reconnected = connected || group.Any(n => n.Change.Kind == NodeChangeKind.Reconnected);
        yield return reconnected
            ? Event(at, Id.Reconnected, EventLevel.Information, Severity.Low, $"Device dropped out and came back: {name}{what}",
                $"{name}{what} disconnected and connected again within seconds ({bus}). Once can be a bumped cable; often points to a loose connector, the cable, the port's power or USB power saving.", name, "reconnected", data)
            : Event(at, Id.Disconnected, EventLevel.Information, Severity.Low, $"Device disconnected: {name}{what}", $"{name}{what} was disconnected ({bus}).", name, "disconnected", data);

        if (Flapping(key, at, now) is { } count)
        {
            data = new Dictionary<string, string>(data) { ["count"] = count.ToString(CultureInfo.InvariantCulture) };
            yield return Event(at, Id.KeepsReconnecting, EventLevel.Warning, Severity.Medium, $"Device keeps disconnecting: {name} ({count.ToString(CultureInfo.InvariantCulture)} times in 10 minutes)",
                $"{name}{what} disconnected {count.ToString(CultureInfo.InvariantCulture)} times in the last 10 minutes ({bus}). Usual causes: a loose cable or connector, a worn cable, a hub or front-panel port that cannot give enough power, " +
                "or USB selective suspend. Try a port on the back of the PC, another cable or a powered hub; for a wireless device, its battery or the distance to its receiver.",
                name, "keeps-reconnecting", data);
        }
    }

    private static SystemEvent Internal(NodeChange change, DeviceNodeFacts node)
    {
        var name = IoDevices.NameOf(node);
        var data = Data(node);
        return change.Kind == NodeChangeKind.Connected
            ? Event(change.At, Id.Connected, EventLevel.Information, Severity.Low, $"Device of this PC appeared: {name}", $"{name} ({Describe(node)}) appeared while Windows was running (after a rescan, a driver reset or a device enabled in the firmware).", name, "internal-added", data)
            : Event(change.At, Id.InternalRemoved, EventLevel.Warning, Severity.High, $"Device of this PC disappeared: {name}",
                $"{name} ({Describe(node)}) is part of this PC and disappeared while Windows was running{(change.Kind == NodeChangeKind.Reconnected ? ", then came back" : string.Empty)}. " +
                "That follows a driver crash or reset, power saving (PCI Express link state power management, USB selective suspend), overheating, or a loose or failing card, cable or drive.",
                name, "internal-removed", data);
    }

    /// <summary>The number of drops in the window when it reaches <see cref="FlapCount"/> (once per window per device).</summary>
    private int? Flapping(string key, DateTimeOffset at, DateTimeOffset now)
    {
        if (!_drops.TryGetValue(key, out var drops))
        {
            _drops[key] = drops = new Queue<DateTimeOffset>();
        }

        drops.Enqueue(at);
        while (drops.Count > 0 && now - drops.Peek() > FlapWindow)
        {
            drops.Dequeue();
        }

        if (drops.Count < FlapCount || (_warned.TryGetValue(key, out var warned) && now - warned < FlapWindow))
        {
            return null;
        }

        _warned[key] = now;
        return drops.Count;
    }

    private List<SystemEvent> Limit(List<SystemEvent> events, DateTimeOffset now)
    {
        while (_emitted.Count > 0 && now - _emitted.Peek() > TimeSpan.FromMinutes(1))
        {
            _emitted.Dequeue();
        }

        var room = Math.Max(0, MaxEventsPerMinute - _emitted.Count);
        var kept = events.OrderByDescending(e => e.Severity).Take(room).OrderBy(e => e.Timestamp).ToList();
        foreach (var _ in kept)
        {
            _emitted.Enqueue(now);
        }

        return kept;
    }

    private static string Label(string kind) => kind switch
    {
        "game-controller" => "game controller",
        "portable" => "phone or portable device",
        "ports" => "serial port",
        "security" => "security device",
        _ => kind,
    };

    private static string Describe(DeviceNodeFacts node) =>
        string.Join(", ", new[] { node.Class, IoDevices.BusOf(node) }.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string Driver(DeviceNodeFacts node) =>
        node.DriverVersion is null ? string.Empty : $" Driver: {node.DriverProvider ?? "unknown provider"} {node.DriverVersion}{(node.DriverDate is { } date ? $" ({date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})" : string.Empty)}.";

    private static Dictionary<string, string> Data(DeviceNodeFacts node)
    {
        var data = new Dictionary<string, string> { ["instanceId"] = node.InstanceId, ["bus"] = IoDevices.BusOf(node) };
        if (node.Class is { } cls)
        {
            data["class"] = cls;
        }

        var (vendor, product) = IoDevices.Ids(node);
        if (vendor is not null)
        {
            data["vendorId"] = vendor;
            data["productId"] = product!;
        }

        if (node.ContainerId is { } container)
        {
            data["containerId"] = container;
        }

        return data;
    }

    private static SystemEvent Event(DateTimeOffset at, Id id, EventLevel level, Severity severity, string title, string message, string device, string rule, Dictionary<string, string> data) =>
        new(at, Channel, null, Provider, (int)id, level, EventCategory.Device, severity, title, message, RelatedDevice: device, RuleId: "pcdiag.devices." + rule, Data: data);
}
