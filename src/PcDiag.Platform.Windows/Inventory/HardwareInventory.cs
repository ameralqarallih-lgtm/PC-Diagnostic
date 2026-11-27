using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PcDiag.Core.Devices;
using PcDiag.Platform.Windows.Devices;
using PcDiag.Platform.Windows.Gpu;
using PcDiag.Platform.Windows.Network;

namespace PcDiag.Platform.Windows.Inventory;

/// <summary>
/// The hardware there now, for the expected-hardware check, read quickly (no WMI): the connected devices from the
/// device tree (not the plumbing), drives by letter, physical network adapters, graphics cards, installed memory and
/// the processor's threads. Monitors come from the display configuration (the recorder adds them).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class HardwareInventory
{
    /// <summary>The connected devices are read in full again at least this often, even when none came or went.</summary>
    public static readonly TimeSpan DevicesRefresh = TimeSpan.FromMinutes(10);

    private static readonly Lock DevicesLock = new();
    private static (ulong Signature, long At, List<HardwareItem> Items)? _devices;

    /// <summary>What is there now (monitors excepted).</summary>
    public static List<HardwareItem> Read()
    {
        var items = new List<HardwareItem>();
        try
        {
            items.AddRange(Devices());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidOperationException)
        {
            // Without the device tree the other kinds are still checked.
        }

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? letter : $"{letter} {drive.VolumeLabel}";
                items.Add(new HardwareItem("volume:" + letter.ToUpperInvariant(), "volume", label, Size(drive.TotalSize)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not readable now (an empty card reader): not there.
            }
        }

        try
        {
            // Windows' own flags tell hardware from filters and software adapters; the names are the fallback. Adapters
            // that are not present (one left behind each time a card was reinstalled, or a disabled one) are not here.
            var flags = InterfaceFlagsReader.Read();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var known = Guid.TryParse(nic.Id, out var id) && flags.TryGetValue(id, out var flag) ? flag : (InterfaceFlags?)null;
                if (nic.OperationalStatus == OperationalStatus.NotPresent || known is { IsHardware: false } or { IsFilter: true }
                    || !Physical(nic.NetworkInterfaceType, nic.Name, nic.Description))
                {
                    continue;
                }

                items.Add(new HardwareItem("network:" + nic.Id, "network", nic.Name, nic.Description));
            }
        }
        catch (NetworkInformationException)
        {
            // The adapters could not be listed now.
        }

        var gpus = Dxgi.EnumerateAdapters().Where(a => a.VendorId != 0x1414).GroupBy(a => (a.VendorId, a.DeviceId));
        foreach (var group in gpus)
        {
            var index = 0;
            foreach (var adapter in group)
            {
                items.Add(new HardwareItem(string.Create(CultureInfo.InvariantCulture, $"gpu:{adapter.VendorId:X4}:{adapter.DeviceId:X4}:{index++}"), "gpu", adapter.Description, "Graphics card"));
            }
        }

        if (GetPhysicallyInstalledSystemMemory(out var kilobytes) && kilobytes > 0)
        {
            var gb = Math.Round(kilobytes / 1024.0 / 1024.0, 1);
            items.Add(new HardwareItem("memory", "memory", "Memory", string.Create(CultureInfo.InvariantCulture, $"{gb:0.#} GB installed"), gb));
        }

        items.Add(new HardwareItem("cpu", "cpu", ProcessorName() ?? "Processor", string.Create(CultureInfo.InvariantCulture, $"{Environment.ProcessorCount} threads"), Environment.ProcessorCount));
        return items;
    }

    /// <summary>
    /// A real adapter (Ethernet or Wi-Fi): not a virtual switch, VPN, loopback or the kernel debugger's, and not one of
    /// the filters Windows binds to an adapter ("Ethernet 3-WFP Native MAC Layer LightWeight Filter-0000", "…-QoS
    /// Packet Scheduler-0000"), which are listed as adapters of their own.
    /// </summary>
    internal static bool Physical(NetworkInterfaceType type, string name, string description)
    {
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT))
        {
            return false;
        }

        if (FilterBinding().IsMatch(name) || FilterBinding().IsMatch(description))
        {
            return false;
        }

        var text = description + " " + name;
        return !NotHardware.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] NotHardware = ["Virtual", "Hyper-V", "VPN", "Bluetooth", "Loopback", "WAN Miniport", "vEthernet", "Kernel Debug", "Filter", "Packet Scheduler"];

    /// <summary>A filter's binding to an adapter ends in a four-digit instance number.</summary>
    [GeneratedRegex(@"-\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex FilterBinding();

    /// <summary>
    /// The connected devices (not the plumbing). Reading the whole device register costs seconds of CPU (every node
    /// Windows has known, connected or not, with up to twenty properties each: about 8 s a read on the dev PC, which the
    /// check every 20 s made 0.4 of a processor). So the list is kept while the connected devices and their problem
    /// codes stay the same (one cheap call for the IDs and one status call each) and read in full when they change, and
    /// every <see cref="DevicesRefresh"/> anyway (a driver update can rename one).
    /// </summary>
    private static List<HardwareItem> Devices()
    {
        var signature = PresentSignature(DeviceTree.PresentProblems());
        lock (DevicesLock)
        {
            if (_devices is { } kept && kept.Signature == signature && System.Diagnostics.Stopwatch.GetElapsedTime(kept.At) < DevicesRefresh)
            {
                return kept.Items;
            }
        }

        var items = DeviceInventory.Read().Devices
            .Where(d => d.Present && !d.Technical && d.Kind is not ("system" or "display"))
            .Select(d => new HardwareItem("device:" + d.Key, "device", d.Name, Title(d.Kind)))
            .ToList();
        lock (DevicesLock)
        {
            _devices = (signature, System.Diagnostics.Stopwatch.GetTimestamp(), items);
        }

        return items;
    }

    /// <summary>The connected device nodes and their problem codes as one number (any arrival, removal or new problem changes it).</summary>
    internal static ulong PresentSignature(IReadOnlyDictionary<string, int> present)
    {
        var hash = 14695981039346656037UL;
        foreach (var (id, problem) in present.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var c in id)
            {
                hash = (hash ^ char.ToUpperInvariant(c)) * 1099511628211UL;
            }

            hash = (hash ^ (uint)problem ^ 0x1F) * 1099511628211UL;
        }

        return hash ^ (ulong)present.Count;
    }

    private static string Title(string kind) => kind switch
    {
        "game-controller" => "Game controller",
        "usb" => "USB device",
        "ports" => "Port",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    private static string Size(long bytes) => bytes >= 1L << 40
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 40):0.#} TB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0} GB");

    private static string? ProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);
}
