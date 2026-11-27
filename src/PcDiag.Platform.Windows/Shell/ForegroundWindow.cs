using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PcDiag.Platform.Windows.Shell;

/// <summary>The app in front (the window the user is using: the game while playing).</summary>
[SupportedOSPlatform("windows")]
public static partial class ForegroundWindow
{
    /// <summary>Its process, or null when no window is in front (the desktop is locked, the sign-in screen is up).</summary>
    public static int? ProcessId()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    /// <summary>
    /// The app in front and whether its window covers its whole monitor (a game in full screen or a borderless window);
    /// null when no window is in front. The desktop and the taskbar are not counted as covering it.
    /// </summary>
    public static (int ProcessId, bool FullScreen)? Current()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var pid);
        if (pid == 0)
        {
            return null;
        }

        return ((int)pid, CoversMonitor(window));
    }

    private static unsafe bool CoversMonitor(nint window)
    {
        Span<char> name = stackalloc char[64];
        int length;
        fixed (char* p = name)
        {
            length = GetClassNameW(window, p, name.Length);
        }

        // The desktop (Progman, WorkerW) and the taskbar cover the screen too, and are not an app.
        var className = name[..Math.Max(0, length)];
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false;
        }

        if (!GetWindowRect(window, out var rect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
        {
            return false;
        }

        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint window, char* name, int count);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
