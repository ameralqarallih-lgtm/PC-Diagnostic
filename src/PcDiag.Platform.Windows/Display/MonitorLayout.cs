using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PcDiag.Core.Overlay;

namespace PcDiag.Platform.Windows.Display;

/// <summary>
/// The monitors on the desktop in physical pixels with their display scaling (EnumDisplayMonitors,
/// GetMonitorInfo, GetDpiForMonitor), read per-monitor DPI aware whatever the calling thread is.
/// </summary>
public static unsafe partial class MonitorLayout
{
    private const uint MonitorInfoPrimary = 1;
    private const int EffectiveDpi = 0;
    private static readonly nint PerMonitorAwareV2 = -4;

    [ThreadStatic]
    private static List<MonitorArea>? _found;

    /// <summary>The monitors now.</summary>
    public static List<MonitorArea> Read()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            _found = [];
            EnumDisplayMonitors(nint.Zero, null, &Found, nint.Zero);
            return _found;
        }
        finally
        {
            _found = null;
            if (previous != nint.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    /// <summary>Makes the calling thread per-monitor DPI aware (v2), so its windows are placed in physical pixels.</summary>
    public static void UsePhysicalPixelsOnThisThread() => SetThreadDpiAwarenessContext(PerMonitorAwareV2);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Found(nint monitor, nint hdc, Rect* clip, nint data)
    {
        try
        {
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            if (GetMonitorInfoW(monitor, &info) != 0)
            {
                var scale = GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
                var r = info.Monitor;
                _found?.Add(new MonitorArea(new string(info.Device), r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.Flags & MonitorInfoPrimary) != 0, scale));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never let an exception cross into the native caller.
        }

        return 1; // continue
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint hdc, Rect* clip, delegate* unmanaged[Stdcall]<nint, nint, Rect*, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    private static partial int GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>RECT.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>MONITORINFOEXW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }
}
