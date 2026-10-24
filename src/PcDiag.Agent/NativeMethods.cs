using System.Runtime.InteropServices;

namespace PcDiag.Agent;

/// <summary>Win32 calls used by the Agent's tray and startup (WinUser.h, consoleapi.h).</summary>
internal static partial class NativeMethods
{
    /// <summary>WM_HOTKEY.</summary>
    public const int WmHotkey = 0x0312;

    /// <summary>MOD_NOREPEAT: holding the keys down does not repeat the hotkey.</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>ATTACH_PARENT_PROCESS ((DWORD)-1).</summary>
    public const uint AttachParentProcess = 0xFFFFFFFF;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    /// <summary>
    /// The Agent is a windowed (tray) application; when started from a terminal it attaches to that console so
    /// its log output is still visible. Started any other way, there is no console and nothing happens.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(uint processId);
}
