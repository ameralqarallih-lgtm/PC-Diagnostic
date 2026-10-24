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

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
