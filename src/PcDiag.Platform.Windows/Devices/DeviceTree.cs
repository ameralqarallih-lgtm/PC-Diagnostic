using System.Globalization;
using System.Runtime.InteropServices;
using PcDiag.Core.Devices;

namespace PcDiag.Platform.Windows.Devices;

/// <summary>
/// Windows' device tree through the Configuration Manager (cfgmgr32, standard user): every device node, including
/// the nodes Windows keeps for devices that are not connected now, with Device Manager's properties. Property keys
/// follow devpkey.h; problem and status values follow cfg.h.
/// </summary>
public static unsafe partial class DeviceTree
{
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;
    private const uint CmGetIdListFilterNone = 0;
    private const uint CmGetIdListFilterPresent = 0x100;
    private const uint CmLocateDevnodePhantom = 1;
    private const uint DnHasProblem = 0x400;
    private const uint TypeString = 0x12;
    private const uint TypeStringList = 0x2012;
    private const uint TypeGuid = 0x0D;
    private const uint TypeFiletime = 0x10;
    private const uint TypeBoolean = 0x11;

    private static readonly Guid General = new(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0);
    private static readonly Guid BusFacts = new(0x540b947e, 0x8b40, 0x45bc, 0xa8, 0xa2, 0x6a, 0x0b, 0x89, 0x4c, 0xbd, 0xa2);
    private static readonly Guid ContainerFacts = new(0x8c7ed206, 0x3f8a, 0x4827, 0xb3, 0xab, 0xae, 0x9e, 0x1f, 0xae, 0xfc, 0x6c);
    private static readonly Guid Relations = new(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x08, 0xa5, 0xa7);
    private static readonly Guid DriverFacts = new(0xa8b865dd, 0x2e3d, 0x4094, 0xad, 0x97, 0xe5, 0x93, 0xa7, 0x0c, 0x75, 0xd6);
    private static readonly Guid InstallFacts = new(0x83da6326, 0x97a6, 0x4088, 0x94, 0x53, 0xa1, 0x92, 0x3f, 0x57, 0x3b, 0x29);

    private static readonly DevPropKey KeyDeviceDesc = new(General, 2);
    private static readonly DevPropKey KeyHardwareIds = new(General, 3);
    private static readonly DevPropKey KeyCompatibleIds = new(General, 4);
    private static readonly DevPropKey KeyService = new(General, 6);
    private static readonly DevPropKey KeyClass = new(General, 9);
    private static readonly DevPropKey KeyManufacturer = new(General, 13);
    private static readonly DevPropKey KeyFriendlyName = new(General, 14);
    private static readonly DevPropKey KeyLocationInfo = new(General, 15);
    private static readonly DevPropKey KeyEnumeratorName = new(General, 24);
    private static readonly DevPropKey KeyBusReportedDeviceDesc = new(BusFacts, 4);
    private static readonly DevPropKey KeyContainerId = new(ContainerFacts, 2);
    private static readonly DevPropKey KeyInLocalMachineContainer = new(ContainerFacts, 4);
    private static readonly DevPropKey KeyParent = new(Relations, 8);
    private static readonly DevPropKey KeyDriverDate = new(DriverFacts, 2);
    private static readonly DevPropKey KeyDriverVersion = new(DriverFacts, 3);
    private static readonly DevPropKey KeyDriverInfPath = new(DriverFacts, 5);
    private static readonly DevPropKey KeyDriverProvider = new(DriverFacts, 9);
    private static readonly DevPropKey KeyFirstInstallDate = new(InstallFacts, 101);
    private static readonly DevPropKey KeyLastArrivalDate = new(InstallFacts, 102);
    private static readonly DevPropKey KeyLastRemovalDate = new(InstallFacts, 103);

    /// <summary>The instance IDs of all device nodes (or only the connected ones).</summary>
    public static IReadOnlyList<string> InstanceIds(bool presentOnly)
    {
        var flags = presentOnly ? CmGetIdListFilterPresent : CmGetIdListFilterNone;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, null, flags) != CrSuccess || length == 0)
            {
                return [];
            }

            var buffer = new char[length + 64];
            int result;
            fixed (char* p = buffer)
            {
                result = CM_Get_Device_ID_ListW(null, p, (uint)buffer.Length, flags);
            }

            if (result == CrBufferSmall)
            {
                continue; // a device arrived in between
            }

            return result == CrSuccess ? new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
        }

        return [];
    }

    /// <summary>The connected device nodes with their problem codes (0: working). Fast: one status call per node.</summary>
    public static Dictionary<string, int> PresentProblems()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in InstanceIds(presentOnly: true))
        {
            if (Locate(id) is { } node && Status(node) is { } status)
            {
                result[id] = status.Problem;
            }
        }

        return result;
    }

    /// <summary>
    /// The device nodes <paramref name="wanted"/> accepts. It sees each node's basic facts (names, class, bus, service,
    /// whether it is connected, its problem); the slower facts (driver, dates, parent, location) are read only for
    /// the nodes it accepts.
    /// </summary>
    public static List<DeviceNodeFacts> Nodes(Func<DeviceNodeFacts, bool> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        var result = new List<DeviceNodeFacts>();
        foreach (var id in InstanceIds(presentOnly: false))
        {
            if (Locate(id) is not { } node)
            {
                continue;
            }

            var basic = Basic(id, node);
            if (wanted(basic))
            {
                result.Add(Complete(basic, node));
            }
        }

        return result;
    }

    /// <summary>One device node, connected or not; null when Windows does not know it.</summary>
    public static DeviceNodeFacts? Read(string instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        return Locate(instanceId) is { } node ? Complete(Basic(instanceId, node), node) : null;
    }

    private static uint? Locate(string instanceId)
    {
        fixed (char* id = instanceId)
        {
            return CM_Locate_DevNodeW(out var node, id, CmLocateDevnodePhantom) == CrSuccess ? node : null;
        }
    }

    // A node that is not connected has no status (CR_NO_SUCH_DEVINST).
    private static (uint Flags, int Problem)? Status(uint node) =>
        CM_Get_DevNode_Status(out var status, out var problem, node, 0) == CrSuccess ? (status, (status & DnHasProblem) != 0 ? (int)problem : 0) : null;

    private static DeviceNodeFacts Basic(string id, uint node)
    {
        var status = Status(node);
        return new DeviceNodeFacts(
            id,
            ReadString(node, KeyFriendlyName),
            ReadString(node, KeyDeviceDesc),
            ReadString(node, KeyBusReportedDeviceDesc),
            ReadString(node, KeyClass),
            null,
            ReadString(node, KeyEnumeratorName) ?? IoDevices.EnumeratorOf(id),
            ReadString(node, KeyService),
            null,
            null,
            ReadBoolean(node, KeyInLocalMachineContainer),
            status is not null,
            status?.Problem ?? 0,
            null,
            []);
    }

    private static DeviceNodeFacts Complete(DeviceNodeFacts basic, uint node) => basic with
    {
        Manufacturer = ReadString(node, KeyManufacturer),
        Parent = ReadString(node, KeyParent),
        ContainerId = ReadGuid(node, KeyContainerId)?.ToString("D", CultureInfo.InvariantCulture),
        Location = ReadString(node, KeyLocationInfo),
        HardwareIds = ReadStringList(node, KeyHardwareIds),
        CompatibleIds = ReadStringList(node, KeyCompatibleIds),
        DriverProvider = ReadString(node, KeyDriverProvider),
        DriverVersion = ReadString(node, KeyDriverVersion),
        DriverDate = ReadFileTime(node, KeyDriverDate),
        DriverInf = ReadString(node, KeyDriverInfPath),
        FirstInstalled = ReadFileTime(node, KeyFirstInstallDate),
        LastArrival = ReadFileTime(node, KeyLastArrivalDate),
        LastRemoval = ReadFileTime(node, KeyLastRemovalDate),
    };

    private static byte[]? Property(uint node, DevPropKey key, uint expectedType)
    {
        Span<byte> stack = stackalloc byte[1024];
        var size = (uint)stack.Length;
        uint type;
        int result;
        fixed (byte* p = stack)
        {
            result = CM_Get_DevNode_PropertyW(node, &key, out type, p, ref size, 0);
        }

        if (result == CrSuccess)
        {
            return type == expectedType ? stack[..(int)size].ToArray() : null;
        }

        if (result != CrBufferSmall || size == 0 || size > 1024 * 1024)
        {
            return null;
        }

        var heap = new byte[size];
        fixed (byte* p = heap)
        {
            result = CM_Get_DevNode_PropertyW(node, &key, out type, p, ref size, 0);
        }

        return result == CrSuccess && type == expectedType ? heap.AsSpan(0, (int)size).ToArray() : null;
    }

    private static string? ReadString(uint node, DevPropKey key)
    {
        var bytes = Property(node, key, TypeString);
        if (bytes is null)
        {
            return null;
        }

        var text = MemoryMarshal.Cast<byte, char>(bytes).ToString().TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static string[] ReadStringList(uint node, DevPropKey key)
    {
        var bytes = Property(node, key, TypeStringList);
        return bytes is null ? [] : MemoryMarshal.Cast<byte, char>(bytes).ToString().Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static Guid? ReadGuid(uint node, DevPropKey key) =>
        Property(node, key, TypeGuid) is { Length: 16 } bytes ? new Guid(bytes) : null;

    private static bool? ReadBoolean(uint node, DevPropKey key) =>
        Property(node, key, TypeBoolean) is { Length: >= 1 } bytes ? bytes[0] != 0 : null;

    private static DateTimeOffset? ReadFileTime(uint node, DevPropKey key)
    {
        if (Property(node, key, TypeFiletime) is not { Length: 8 } bytes)
        {
            return null;
        }

        var value = BitConverter.ToInt64(bytes);
        return value <= 0 ? null : DateTimeOffset.FromFileTime(value).ToUniversalTime();
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Get_Device_ID_ListW(string? filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Locate_DevNodeW(out uint node, char* instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_PropertyW(uint node, DevPropKey* key, out uint type, byte* buffer, ref uint size, uint flags);

    /// <summary>DEVPROPKEY.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct DevPropKey(Guid category, uint id)
    {
        public readonly Guid Category = category;
        public readonly uint Id = id;
    }
}
