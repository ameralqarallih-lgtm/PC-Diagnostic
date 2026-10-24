using System.Runtime.InteropServices;

namespace PcDiag.Platform.Windows.Processes;

/// <summary>
/// Finds processes with a visible top-level window that Windows considers hung (IsHungAppWindow: the window has not
/// processed messages for about 5 seconds). This is the same signal as Task Manager's "Not responding".
/// DWM "ghost" windows (class "Ghost", owned by dwm.exe, shown in place of a hung window) are skipped; otherwise
/// dwm.exe would be reported as hung whenever any application hangs.
/// </summary>
public static unsafe partial class HungWindows
{
    /// <summary>Process IDs that own at least one hung, visible top-level window on this desktop.</summary>
    public static HashSet<int> FindHungProcesses()
    {
        var result = new HashSet<int>();
        var handle = GCHandle.Alloc(result);
        try
        {
            EnumWindows(&Callback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static int Callback(nint window, nint state)
    {
        if (IsWindowVisible(window) && IsHungAppWindow(window) && !IsGhost(window))
        {
            _ = GetWindowThreadProcessId(window, out var pid);
            if (pid != 0 && GCHandle.FromIntPtr(state).Target is HashSet<int> set)
            {
                set.Add((int)pid);
            }
        }

        return 1; // continue enumeration
    }

    private static bool IsGhost(nint window)
    {
        var name = stackalloc char[16];
        var length = GetClassName(window, name, 16);
        return new ReadOnlySpan<char>(name, length).SequenceEqual("Ghost");
    }

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassName(nint window, char* className, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsHungAppWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
