using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using PcDiag.Core.Devices;
using PcDiag.Core.Telemetry;

namespace PcDiag.Platform.Windows.Inventory;

/// <summary>Result of an inventory read.</summary>
public sealed record InventoryResult(IReadOnlyDictionary<string, string> System, string HardwareJson, IReadOnlyList<DriverInfo> Drivers, IReadOnlyList<IoDevice> IoDevices);

/// <summary>
/// Hardware, OS and driver inventory through WMI (standard user). Slow (seconds for drivers), so it is read in the
/// background at startup and every few hours. Serial numbers and asset tags are deliberately not read.
/// </summary>
public static partial class SystemInventory
{
    /// <summary>Device classes whose drivers matter for diagnosis (display, storage, network, USB, audio, system...).</summary>
    public static readonly IReadOnlySet<string> DriverClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Monitor", "Net", "Bluetooth", "SCSIAdapter", "HDC", "DiskDrive", "Volume", "USB", "System",
        "MEDIA", "AudioEndpoint", "HIDClass", "Processor", "Firmware", "Battery", "SecurityDevices", "Camera", "Image",
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Reads everything. Individual WMI failures leave the corresponding section empty.</summary>
    /// <param name="includeComputerName">Include the computer name (identifying; only with user-name capture enabled).</param>
    public static InventoryResult Read(bool includeComputerName)
    {
        var system = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var hardware = new Dictionary<string, object?>();

        var os = Query("SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, InstallDate FROM Win32_OperatingSystem").FirstOrDefault();
        if (os is not null)
        {
            Put(system, "os.name", os["Caption"]);
            Put(system, "os.version", os["Version"]);
            Put(system, "os.build", os["BuildNumber"]);
            Put(system, "os.architecture", os["OSArchitecture"]);
            Put(system, "os.last_boot", WmiDate(os["LastBootUpTime"]));
            Put(system, "os.install_date", WmiDate(os["InstallDate"]));
        }

        Put(system, "os.display_version", Registry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion"));
        Put(system, "os.ubr", Registry(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR"));

        var cs = Query("SELECT Manufacturer, Model, SystemType, TotalPhysicalMemory, Name, HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        if (cs is not null)
        {
            Put(system, "system.manufacturer", cs["Manufacturer"]);
            Put(system, "system.model", cs["Model"]);
            Put(system, "memory.total_bytes", cs["TotalPhysicalMemory"]);
            if (includeComputerName)
            {
                Put(system, "system.computer_name", cs["Name"]);
            }
        }

        var bios = Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS").FirstOrDefault();
        if (bios is not null)
        {
            Put(system, "bios.vendor", bios["Manufacturer"]);
            Put(system, "bios.version", bios["SMBIOSBIOSVersion"]);
            Put(system, "bios.date", WmiDate(bios["ReleaseDate"]));
            if (bios["SMBIOSMajorVersion"] is not null)
            {
                Put(system, "bios.smbios", $"{bios["SMBIOSMajorVersion"]}.{bios["SMBIOSMinorVersion"] ?? 0}");
            }
        }

        Firmware(system);

        var board = Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault();
        if (board is not null)
        {
            Put(system, "board.manufacturer", board["Manufacturer"]);
            Put(system, "board.product", board["Product"]);
            Put(system, "board.version", board["Version"]);
        }

        hardware["cpus"] = Query("SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor")
            .Select(Properties).ToList();
        foreach (var (cpu, i) in ((List<Dictionary<string, object?>>)hardware["cpus"]!).Select((c, i) => (c, i)))
        {
            Put(system, $"cpu.{i}.name", cpu.GetValueOrDefault("Name"));
            Put(system, $"cpu.{i}.cores", cpu.GetValueOrDefault("NumberOfCores"));
            Put(system, $"cpu.{i}.logical_processors", cpu.GetValueOrDefault("NumberOfLogicalProcessors"));
            Put(system, $"cpu.{i}.socket", cpu.GetValueOrDefault("SocketDesignation"));
        }

        // With a hypervisor running (Hyper-V, VBS), Windows itself runs on it and the processor reports virtualization
        // as off to it: the hypervisor is the proof that it is on.
        var hypervisor = cs?["HypervisorPresent"] is true;
        var firmwareVirtualization = ((List<Dictionary<string, object?>>)hardware["cpus"]!).Select(c => c.GetValueOrDefault("VirtualizationFirmwareEnabled")).OfType<bool>().FirstOrDefault();
        Put(system, "cpu.virtualization", hypervisor ? "on (a hypervisor is running)" : firmwareVirtualization ? "on" : "off in the firmware settings");

        hardware["memory_modules"] = Query("SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory")
            .Select(Properties).ToList();
        var array = Query("SELECT MemoryDevices, MaxCapacityEx, MaxCapacity, Use FROM Win32_PhysicalMemoryArray")
            .FirstOrDefault(a => a["Use"] is null || Convert.ToInt32(a["Use"], CultureInfo.InvariantCulture) == 3); // system memory
        if (array is not null)
        {
            Put(system, "memory.slots", array["MemoryDevices"]);
            Put(system, "memory.slots_used", ((List<Dictionary<string, object?>>)hardware["memory_modules"]!).Count);
            var maxKilobytes = array["MaxCapacityEx"] ?? array["MaxCapacity"];
            if (maxKilobytes is not null && Convert.ToUInt64(maxKilobytes, CultureInfo.InvariantCulture) is > 0 and var kilobytes)
            {
                Put(system, "memory.max_bytes", kilobytes * 1024);
            }
        }

        hardware["expansion_slots"] = Query("SELECT SlotDesignation, CurrentUsage, MaxDataWidth, Description FROM Win32_SystemSlot").Select(Properties).ToList();
        var slots = (List<Dictionary<string, object?>>)hardware["expansion_slots"]!;
        if (slots.Count > 0)
        {
            Put(system, "board.slots", slots.Count);
            Put(system, "board.slots_used", slots.Count(s => s.GetValueOrDefault("CurrentUsage") is { } usage && Convert.ToInt32(usage, CultureInfo.InvariantCulture) == 4));
        }
        hardware["video_controllers"] = Query("SELECT Name, DriverVersion, DriverDate, VideoProcessor, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, PNPDeviceID FROM Win32_VideoController")
            .Select(Properties).ToList();
        hardware["disk_drives"] = Query("SELECT Model, InterfaceType, Size, FirmwareRevision, Index, MediaType FROM Win32_DiskDrive")
            .Select(Properties).ToList();
        var tpm = Query("SELECT IsEnabled_InitialValue, IsActivated_InitialValue, ManufacturerIdTxt, ManufacturerVersion, SpecVersion FROM Win32_Tpm", @"root\CIMV2\Security\MicrosoftTpm").FirstOrDefault();
        if (tpm is not null)
        {
            Put(system, "tpm.version", (tpm["SpecVersion"] as string)?.Split(',')[0]);
            Put(system, "tpm.maker", tpm["ManufacturerIdTxt"]);
            Put(system, "tpm.firmware", tpm["ManufacturerVersion"]);
            Put(system, "tpm.state", tpm["IsEnabled_InitialValue"] is true ? tpm["IsActivated_InitialValue"] is false ? "enabled, not activated" : "ready" : "disabled");
        }

        var register = ReadDevices();
        PutDeviceFacts(system, register);

        hardware["system"] = system;

        var drivers = Query("SELECT DeviceClass, DeviceName, HardWareID, DriverProviderName, DriverVersion, DriverDate, InfName, Signer FROM Win32_PnPSignedDriver")
            .Where(d => d["DeviceClass"] is string cls && DriverClasses.Contains(cls) && d["DeviceName"] is string)
            .Select(d => new DriverInfo(
                (string)d["DeviceClass"]!,
                (string)d["DeviceName"]!,
                d["HardWareID"] as string,
                d["DriverProviderName"] as string,
                d["DriverVersion"] as string,
                WmiDate(d["DriverDate"]),
                d["InfName"] as string,
                d["Signer"] as string))
            .OrderBy(d => d.DeviceClass, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InventoryResult(system, JsonSerializer.Serialize(hardware, Json), drivers, register.Devices);
    }

    /// <summary>The device register alone (fast), for a refresh after a device was connected or removed.</summary>
    public static DeviceRegister ReadDevices()
    {
        try
        {
            return DeviceInventory.Read();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidOperationException)
        {
            return new DeviceRegister([], null);
        }
    }

    /// <summary>The facts the device register gives: the chipset and how many devices are registered, connected and failing.</summary>
    public static void PutDeviceFacts(IDictionary<string, string> system, DeviceRegister register)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(register);
        if (register.BridgeName is not null)
        {
            system["board.chipset_bridge"] = register.BridgeVendorId is null ? register.BridgeName : $"{register.BridgeName} ({register.BridgeVendorId}:{register.BridgeDeviceId})";
        }

        if (BoardFacts.Chipset(register.BridgeName, register.BridgeVendorId, register.BridgeDeviceId, system.TryGetValue("board.product", out var board) ? board : null,
                system.TryGetValue("cpu.0.name", out var cpu) ? cpu : null) is { } chipset)
        {
            system["board.chipset"] = chipset;
        }

        system["devices.registered"] = register.Devices.Count(d => !d.Technical).ToString(CultureInfo.InvariantCulture);
        system["devices.connected"] = register.Devices.Count(d => d.Present && !d.Technical).ToString(CultureInfo.InvariantCulture);
        system["devices.with_problems"] = register.Devices.Count(d => d.Present && d.ProblemCode != 0).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>UEFI or legacy BIOS, and Secure Boot.</summary>
    private static void Firmware(SortedDictionary<string, string> system)
    {
        var known = GetFirmwareType(out var type);
        if (known)
        {
            Put(system, "firmware.type", type switch { 1 => "BIOS (legacy)", 2 => "UEFI", _ => null });
        }

        var secureBoot = Registry(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        Put(system, "firmware.secure_boot", secureBoot switch { int on => on != 0 ? "on" : "off", _ => known && type == 1 ? "not supported" : null });
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out int firmwareType);

    private static List<ManagementBaseObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            searcher.Options.Timeout = TimeSpan.FromSeconds(30);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            return [];
        }
    }

    private static Dictionary<string, object?> Properties(ManagementBaseObject o) =>
        o.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)(p.Value is string s ? s.Trim() : p.Value));

    private static void Put(SortedDictionary<string, string> target, string key, object? value)
    {
        var text = value switch
        {
            null => null,
            string s => s.Trim(),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        if (!string.IsNullOrWhiteSpace(text))
        {
            target[key] = text;
        }
    }

    private static string? WmiDate(object? value) =>
        value is string s && s.Length >= 8 ? $"{s[..4]}-{s[4..6]}-{s[6..8]}" : null;

    private static object? Registry(string path, string name)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue(name);
    }
}
