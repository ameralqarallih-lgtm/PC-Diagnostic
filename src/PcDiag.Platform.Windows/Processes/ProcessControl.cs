using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcDiag.Platform.Windows.Processes;

/// <summary>What an action did: done, or why not (in words).</summary>
public readonly record struct ControlOutcome(bool Ok, string? Error)
{
    /// <summary>Done.</summary>
    public static ControlOutcome Done => new(true, null);

    /// <summary>Not done, and why.</summary>
    public static ControlOutcome Failed(string error) => new(false, error);
}

/// <summary>
/// Ends processes and changes their priority. A process is always named by its PID and creation time: the time is
/// checked on the opened handle, so a PID that Windows reused for another program is never touched.
/// </summary>
public static partial class ProcessControl
{
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetInformation = 0x0200;
    private const uint ProcessSetQuota = 0x0100;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    /// <summary>Priority classes by name (GetPriorityClass values).</summary>
    public static readonly IReadOnlyDictionary<string, uint> Priorities = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["Idle"] = 0x40,
        ["BelowNormal"] = 0x4000,
        ["Normal"] = 0x20,
        ["AboveNormal"] = 0x8000,
        ["High"] = 0x80,
        ["Realtime"] = 0x100,
    };

    /// <summary>Ends the process and waits up to 3 s for it to go.</summary>
    public static ControlOutcome Terminate(int pid, long createTicksUtc)
    {
        using var process = Open(pid, createTicksUtc, ProcessTerminate | ProcessQueryLimitedInformation | Synchronize, out var problem);
        if (process is null)
        {
            return ControlOutcome.Failed(problem!);
        }

        if (!TerminateProcess(process, 1))
        {
            return ControlOutcome.Failed(Describe(Marshal.GetLastPInvokeError()));
        }

        _ = WaitForSingleObject(process, 3000);
        return ControlOutcome.Done;
    }

    /// <summary>Sets the priority class (one of <see cref="Priorities"/>).</summary>
    public static ControlOutcome SetPriority(int pid, long createTicksUtc, string priority)
    {
        if (!Priorities.TryGetValue(priority, out var value))
        {
            return ControlOutcome.Failed("Unknown priority.");
        }

        using var process = Open(pid, createTicksUtc, ProcessSetInformation | ProcessQueryLimitedInformation, out var problem);
        if (process is null)
        {
            return ControlOutcome.Failed(problem!);
        }

        return SetPriorityClass(process, value) ? ControlOutcome.Done : ControlOutcome.Failed(Describe(Marshal.GetLastPInvokeError()));
    }

    /// <summary>
    /// Trims the process's working set: Windows moves its pages to the standby list (still in memory, given back to it
    /// at once if it touches them, or to other programs that need memory).
    /// </summary>
    public static ControlOutcome TrimWorkingSet(int pid, long createTicksUtc)
    {
        using var process = Open(pid, createTicksUtc, ProcessSetQuota | ProcessQueryLimitedInformation, out var problem);
        if (process is null)
        {
            return ControlOutcome.Failed(problem!);
        }

        return EmptyWorkingSet(process) ? ControlOutcome.Done : ControlOutcome.Failed(Describe(Marshal.GetLastPInvokeError()));
    }

    /// <summary>The priority class name, or null when the process cannot be queried.</summary>
    public static string? GetPriority(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid)
        {
            return null;
        }

        var value = GetPriorityClass(process);
        return value == 0 ? null : Priorities.FirstOrDefault(p => p.Value == value).Key;
    }

    /// <summary>Whether Windows marked the process critical (ending it stops Windows with a blue screen).</summary>
    public static bool IsCritical(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        return !process.IsInvalid && IsProcessCritical(process, out var critical) && critical;
    }

    private static SafeProcessHandle? Open(int pid, long createTicksUtc, uint access, out string? problem)
    {
        var handle = OpenProcess(access, false, pid);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            problem = error == ErrorInvalidParameter ? "The process has already ended." : Describe(error);
            return null;
        }

        // The same program instance: creation times match (to the millisecond; Windows keeps 100 ns).
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _) || Math.Abs(DateTime.FromFileTimeUtc(creation).Ticks - createTicksUtc) > TimeSpan.TicksPerMillisecond)
        {
            handle.Dispose();
            problem = "The process has already ended (its process ID now belongs to another program).";
            return null;
        }

        problem = null;
        return handle;
    }

    private static string Describe(int error) => error switch
    {
        ErrorAccessDenied => "Windows refused: the process is protected (or runs with more rights than the recorder).",
        ErrorInvalidParameter => "The process has already ended.",
        _ => $"Windows refused (error {error}).",
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyWorkingSet(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}

/// <summary>Processes with a visible top-level window on this desktop (the programs a person would call "apps").</summary>
public static unsafe partial class VisibleWindows
{
    [ThreadStatic]
    private static HashSet<int>? t_found;

    /// <summary>The process that owns the window in front (0 when none).</summary>
    public static int ForegroundProcessId()
    {
        var window = GetForegroundWindow();
        return window == 0 || GetWindowThreadProcessId(window, out var pid) == 0 ? 0 : (int)pid;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    /// <summary>Process IDs owning a visible, unowned, titled top-level window.</summary>
    public static HashSet<int> ProcessIds()
    {
        var pids = new HashSet<int>();
        t_found = pids;
        try
        {
            _ = EnumWindows(&Visit, 0);
        }
        finally
        {
            t_found = null;
        }

        return pids;
    }

    [UnmanagedCallersOnly]
    private static int Visit(nint window, nint parameter)
    {
        if (IsWindowVisible(window) && GetWindow(window, 4 /* GW_OWNER */) == 0 && GetWindowTextLength(window) > 0)
        {
            _ = GetWindowThreadProcessId(window, out var pid);
            t_found?.Add((int)pid);
        }

        return 1;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);
}
