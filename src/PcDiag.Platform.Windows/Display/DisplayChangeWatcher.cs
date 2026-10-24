using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Display;

/// <summary>What the watcher was told by Windows.</summary>
public enum DisplayNotificationKind
{
    /// <summary>WM_DISPLAYCHANGE: a display mode or the desktop layout changed.</summary>
    DisplayChange,

    /// <summary>A monitor device interface arrived (connected or powered on).</summary>
    MonitorArrived,

    /// <summary>A monitor device interface was removed (disconnected or powered off).</summary>
    MonitorRemoved,

    /// <summary>WM_SETTINGCHANGE (may include color or scaling changes).</summary>
    SettingChange,

    /// <summary>The session's display power state (Value: 0 off, 1 on, 2 dimmed).</summary>
    DisplayPower,

    /// <summary>WM_WTSSESSION_CHANGE (Value: WTS_CONSOLE_CONNECT 1, _DISCONNECT 2, WTS_REMOTE_CONNECT 3, _DISCONNECT 4).</summary>
    SessionChange,
}

/// <summary>One notification. <paramref name="DevicePath"/> is the monitor device interface path for arrival/removal.</summary>
public readonly record struct DisplayNotification(DisplayNotificationKind Kind, string? DevicePath = null, int Value = 0);

/// <summary>
/// Receives display-related notifications through a hidden top-level window on its own thread (message-only
/// windows do not receive broadcasts such as WM_DISPLAYCHANGE): display changes, monitor arrival/removal
/// (GUID_DEVINTERFACE_MONITOR), the session display power state (GUID_SESSION_DISPLAY_STATUS) and console/remote
/// session switches. Constants and structures follow WinUser.h, Dbt.h, WinNT.h and WtsApi32.h.
/// The callback runs on the watcher thread and must return quickly.
/// </summary>
public sealed unsafe partial class DisplayChangeWatcher : IDisposable
{
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmSettingChange = 0x001A;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmDeviceChange = 0x0219;
    private const uint WmWtsSessionChange = 0x02B1;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const uint DbtDevtypDeviceInterface = 5;
    private const uint DeviceNotifyWindowHandle = 0;
    private const uint NotifyForThisSession = 0;

    private static readonly Guid MonitorInterface = new(0xe6f07b5f, 0xee97, 0x4a90, 0xb0, 0x76, 0x33, 0xf5, 0x7b, 0xf4, 0xea, 0xa7);
    private static readonly Guid SessionDisplayStatus = new(0x2b84c20e, 0xad23, 0x4ddf, 0x93, 0xdb, 0x05, 0xff, 0xbd, 0x7e, 0xfc, 0xa5);
    private static readonly ConcurrentDictionary<nint, DisplayChangeWatcher> Windows = new();

    private readonly Action<DisplayNotification> _callback;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly string _className = "PcDiagDisplayWatcher-" + Guid.NewGuid().ToString("N");
    private nint _hwnd;
    private nint _deviceNotification;
    private nint _powerNotification;
    private bool _sessionNotification;
    private int _startError;

    private DisplayChangeWatcher(Action<DisplayNotification> callback)
    {
        _callback = callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "pcdiag-display-watcher" };
    }

    /// <summary>Which notifications were registered successfully.</summary>
    public string Registrations { get; private set; } = string.Empty;

    /// <summary>Starts the watcher. Returns null and the Win32 error when the window could not be created.</summary>
    public static DisplayChangeWatcher? Start(Action<DisplayNotification> callback, out int error)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var watcher = new DisplayChangeWatcher(callback);
        watcher._thread.Start();
        if (!watcher._ready.Wait(TimeSpan.FromSeconds(10)) || watcher._hwnd == nint.Zero)
        {
            error = watcher._startError;
            watcher.Dispose();
            return null;
        }

        error = 0;
        return watcher;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_hwnd != nint.Zero)
        {
            PostMessage(_hwnd, WmClose, nint.Zero, nint.Zero);
        }

        if (_thread.IsAlive && Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        if (!_thread.IsAlive)
        {
            _ready.Dispose();
        }
    }

    private void Run()
    {
        var instance = GetModuleHandle(null);
        fixed (char* className = _className)
        {
            var windowClass = new WndClassEx
            {
                Size = (uint)sizeof(WndClassEx),
                WndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
                Instance = instance,
                ClassName = (nint)className,
            };

            if (RegisterClassEx(&windowClass) == 0)
            {
                _startError = Marshal.GetLastPInvokeError();
                _ready.Set();
                return;
            }

            // A never-shown top-level window: it receives broadcasts but has no UI.
            _hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0, nint.Zero, nint.Zero, instance, nint.Zero);
            if (_hwnd == nint.Zero)
            {
                _startError = Marshal.GetLastPInvokeError();
                UnregisterClass(className, instance);
                _ready.Set();
                return;
            }

            Windows[_hwnd] = this;
            Register();
            _ready.Set();

            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                TranslateMessage(in message);
                DispatchMessage(in message);
            }

            UnregisterClass(className, instance);
        }
    }

    private void Register()
    {
        var registered = new List<string> { "display changes" };

        var filter = new DevBroadcastDeviceInterface
        {
            Size = (uint)sizeof(DevBroadcastDeviceInterface),
            DeviceType = DbtDevtypDeviceInterface,
            ClassGuid = MonitorInterface,
        };
        _deviceNotification = RegisterDeviceNotification(_hwnd, &filter, DeviceNotifyWindowHandle);
        if (_deviceNotification != nint.Zero)
        {
            registered.Add("monitor arrival/removal");
        }

        // Windows sends the current state right after registering.
        var guid = SessionDisplayStatus;
        _powerNotification = RegisterPowerSettingNotification(_hwnd, &guid, DeviceNotifyWindowHandle);
        if (_powerNotification != nint.Zero)
        {
            registered.Add("display power");
        }

        _sessionNotification = WTSRegisterSessionNotification(_hwnd, NotifyForThisSession);
        if (_sessionNotification)
        {
            registered.Add("session switches");
        }

        Registrations = string.Join(", ", registered);
    }

    private void Unregister()
    {
        if (_deviceNotification != nint.Zero)
        {
            UnregisterDeviceNotification(_deviceNotification);
        }

        if (_powerNotification != nint.Zero)
        {
            UnregisterPowerSettingNotification(_powerNotification);
        }

        if (_sessionNotification)
        {
            WTSUnRegisterSessionNotification(_hwnd);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var watcher))
        {
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        try
        {
            switch (message)
            {
                case WmDisplayChange:
                    watcher._callback(new DisplayNotification(DisplayNotificationKind.DisplayChange));
                    break;
                case WmSettingChange:
                    watcher._callback(new DisplayNotification(DisplayNotificationKind.SettingChange));
                    break;
                case WmDeviceChange when (int)wParam is DbtDeviceArrival or DbtDeviceRemoveComplete && lParam != nint.Zero:
                    var header = (DevBroadcastDeviceInterface*)lParam;
                    if (header->DeviceType == DbtDevtypDeviceInterface && header->ClassGuid == MonitorInterface)
                    {
                        var path = new string(&header->Name);
                        var kind = (int)wParam == DbtDeviceArrival ? DisplayNotificationKind.MonitorArrived : DisplayNotificationKind.MonitorRemoved;
                        watcher._callback(new DisplayNotification(kind, path));
                    }

                    return 1; // TRUE
                case WmPowerBroadcast when (int)wParam == PbtPowerSettingChange && lParam != nint.Zero:
                    var setting = (PowerBroadcastSetting*)lParam;
                    if (setting->PowerSetting == SessionDisplayStatus && setting->DataLength >= 4)
                    {
                        watcher._callback(new DisplayNotification(DisplayNotificationKind.DisplayPower, Value: *(int*)&setting->Data));
                    }

                    return 1; // TRUE
                case WmWtsSessionChange:
                    watcher._callback(new DisplayNotification(DisplayNotificationKind.SessionChange, Value: (int)wParam));
                    break;
                case WmClose:
                    DestroyWindow(hwnd);
                    return 0;
                case WmDestroy:
                    watcher.Unregister();
                    Windows.TryRemove(hwnd, out _);
                    PostQuitMessage(0);
                    return 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let an exception cross into the window procedure's native caller.
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static partial ushort RegisterClassEx(WndClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterClass(char* className, nint instance);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    private static partial int GetMessage(out Msg message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in Msg message);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW")]
    private static partial nint RegisterDeviceNotification(nint recipient, void* filter, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterDeviceNotification(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint RegisterPowerSettingNotification(nint recipient, Guid* powerSettingGuid, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterPowerSettingNotification(nint handle);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSRegisterSessionNotification(nint hwnd, uint flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSUnRegisterSessionNotification(nint hwnd);

    /// <summary>WNDCLASSEXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint SmallIcon;
    }

    /// <summary>MSG.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    /// <summary>DEV_BROADCAST_DEVICEINTERFACE_W (the name continues past the struct).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DevBroadcastDeviceInterface
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
        public Guid ClassGuid;
        public char Name;
    }

    /// <summary>POWERBROADCAST_SETTING (data continues past the struct).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }
}

/// <summary>Session facts that decide which displays are visible.</summary>
public static partial class DisplaySession
{
    private const int SmRemoteSession = 0x1000;

    /// <summary>
    /// True in a Remote Desktop session. Display configuration is per session, so a remote session sees the
    /// remote display adapter instead of the physical monitors.
    /// </summary>
    public static bool IsRemote => GetSystemMetrics(SmRemoteSession) != 0;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}
