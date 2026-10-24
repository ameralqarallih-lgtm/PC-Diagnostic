using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PcDiag.Core.Devices;

namespace PcDiag.Platform.Windows.Devices;

/// <summary>What happened to a device node.</summary>
public enum DeviceNotificationKind
{
    /// <summary>The device was connected (enumerated), or one of its interfaces arrived.</summary>
    Arrived,

    /// <summary>Its driver started.</summary>
    Started,

    /// <summary>The device was removed, or one of its interfaces went away.</summary>
    Removed,
}

/// <summary>One notification about a device node.</summary>
public readonly record struct DeviceNotification(DeviceNotificationKind Kind, string InstanceId);

/// <summary>
/// Device arrival and removal from the Plug and Play manager (<c>CM_Register_Notification</c>, Windows 8 and later):
/// for every device instance when Windows allows it, else for every device interface (the instance ID is taken from
/// the interface path). No window is needed. The callback runs on a thread-pool thread and must return quickly.
/// Constants and structures follow cfgmgr32.h.
/// </summary>
public sealed unsafe partial class DeviceNotifications : IDisposable
{
    private const int CrSuccess = 0;
    private const uint FlagAllInterfaceClasses = 0x1;
    private const uint FlagAllDeviceInstances = 0x2;
    private const uint FilterDeviceInterface = 0;
    private const uint FilterDeviceInstance = 2;
    private const uint ActionInterfaceArrival = 0;
    private const uint ActionInterfaceRemoval = 1;
    private const uint ActionInstanceEnumerated = 7;
    private const uint ActionInstanceStarted = 8;
    private const uint ActionInstanceRemoved = 9;
    private const int MaxDeviceIdLength = 200;

    private readonly Action<DeviceNotification> _callback;
    private GCHandle _self;
    private nint _handle;

    private DeviceNotifications(Action<DeviceNotification> callback)
    {
        _callback = callback;
    }

    /// <summary>"device instances" or "device interfaces": what the registration covers.</summary>
    public string Mode { get; private set; } = string.Empty;

    /// <summary>Starts receiving notifications. Returns null and the CONFIGRET error when neither registration works.</summary>
    public static DeviceNotifications? Start(Action<DeviceNotification> callback, out int error)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var watcher = new DeviceNotifications(callback);
        watcher._self = GCHandle.Alloc(watcher);
        error = watcher.Register(FilterDeviceInstance, FlagAllDeviceInstances);
        if (error == CrSuccess)
        {
            watcher.Mode = "device instances";
            return watcher;
        }

        error = watcher.Register(FilterDeviceInterface, FlagAllInterfaceClasses);
        if (error == CrSuccess)
        {
            watcher.Mode = "device interfaces";
            return watcher;
        }

        watcher._self.Free();
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Waits for callbacks in progress, so the handle can be freed afterwards.
        if (_handle != nint.Zero)
        {
            _ = CM_Unregister_Notification(_handle);
            _handle = nint.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private int Register(uint filterType, uint flags)
    {
        var filter = new NotifyFilter { Size = (uint)sizeof(NotifyFilter), Flags = flags, FilterType = filterType };
        nint handle;
        var result = CM_Register_Notification(&filter, GCHandle.ToIntPtr(_self), &Callback, &handle);
        if (result == CrSuccess)
        {
            _handle = handle;
        }

        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Callback(nint notification, nint context, uint action, NotifyEventData* data, uint size)
    {
        try
        {
            if (context == nint.Zero || data is null || GCHandle.FromIntPtr(context).Target is not DeviceNotifications watcher)
            {
                return 0;
            }

            var kind = action switch
            {
                ActionInstanceEnumerated or ActionInterfaceArrival => DeviceNotificationKind.Arrived,
                ActionInstanceStarted => DeviceNotificationKind.Started,
                ActionInstanceRemoved or ActionInterfaceRemoval => DeviceNotificationKind.Removed,
                _ => (DeviceNotificationKind?)null,
            };
            if (kind is null)
            {
                return 0;
            }

            var instanceId = data->FilterType == FilterDeviceInstance
                ? new string((char*)((byte*)data + 8))
                : IoDevices.InstanceIdFromInterfacePath(new string((char*)((byte*)data + 24)));
            if (!string.IsNullOrEmpty(instanceId))
            {
                watcher._callback(new DeviceNotification(kind.Value, instanceId.ToUpperInvariant()));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let an exception cross into the native caller.
        }

        return 0; // ERROR_SUCCESS
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Register_Notification(NotifyFilter* filter, nint context, delegate* unmanaged[Stdcall]<nint, nint, uint, NotifyEventData*, uint, uint> callback, nint* notification);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Unregister_Notification(nint notification);

    /// <summary>CM_NOTIFY_FILTER (the union is the 200-character instance ID, its largest member).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyFilter
    {
        public uint Size;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public fixed char InstanceId[MaxDeviceIdLength];
    }

    /// <summary>CM_NOTIFY_EVENT_DATA header (the union follows at offset 8).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyEventData
    {
        public uint FilterType;
        public uint Reserved;
    }
}
