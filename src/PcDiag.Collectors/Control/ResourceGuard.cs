using System.Globalization;
using PcDiag.Core.Control;

namespace PcDiag.Collectors.Control;

/// <summary>
/// Safe mode for the tune-up: whether a process, a drive, the GPU or the memory is in use by other programs right
/// now (from the recorder's newest samples), so an action leaves it alone. Each check says why, in words.
/// </summary>
public sealed class ResourceGuard
{
    /// <summary>CPU use from which a process counts as busy (% of the whole PC).</summary>
    public const double BusyCpuPercent = 2;

    /// <summary>GPU use from which a process counts as using the GPU (%).</summary>
    public const double BusyGpuPercent = 2;

    /// <summary>Network transfer from which a process counts as transferring (bytes per second).</summary>
    public const double BusyNetBytesPerSec = 10 * 1024;

    /// <summary>Disk transfer from which a process counts as reading or writing (bytes per second).</summary>
    public const double BusyDiskBytesPerSec = 256 * 1024;

    /// <summary>A drive's active time from which it counts as busy (%).</summary>
    public const double BusyDrivePercent = 20;

    /// <summary>GPU use (any program but Windows' own) from which a game or a 3D program counts as running (%).</summary>
    public const double GameGpuPercent = 5;

    private static readonly HashSet<string> WindowsGraphics = new(StringComparer.OrdinalIgnoreCase) { "dwm.exe", "csrss.exe", "System", "Idle", "Registry" };

    private readonly Dictionary<int, ControlProcess> _byPid;

    /// <summary>Creates a guard over the processes now.</summary>
    /// <param name="processes">The running processes with their figures.</param>
    /// <param name="foregroundPid">The program in front (0 when none).</param>
    public ResourceGuard(IReadOnlyList<ControlProcess> processes, int foregroundPid)
    {
        ArgumentNullException.ThrowIfNull(processes);
        Processes = processes;
        ForegroundPid = foregroundPid;
        _byPid = processes.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>The running processes.</summary>
    public IReadOnlyList<ControlProcess> Processes { get; }

    /// <summary>The program in front.</summary>
    public int ForegroundPid { get; }

    /// <summary>A process by PID.</summary>
    public ControlProcess? Find(int pid) => _byPid.GetValueOrDefault(pid);

    /// <summary>Why a process should not be ended now (null: nothing in the way).</summary>
    public string? EndBlocked(ControlProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Pid == ForegroundPid)
        {
            return "it is the program in front";
        }

        if (process.Kind == "app")
        {
            return "it has a window open (unsaved work could be lost)";
        }

        return Working(process);
    }

    /// <summary>Why a process's memory should not be trimmed now: it is in front or working (it would read it straight back).</summary>
    public string? TrimBlocked(ControlProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Pid == ForegroundPid)
        {
            return "it is the program in front";
        }

        if (process.CpuPercent is >= BusyCpuPercent)
        {
            return $"it is busy ({Percent(process.CpuPercent.Value)} CPU)";
        }

        return process.GpuPercent is >= BusyGpuPercent ? $"it is using the GPU ({Percent(process.GpuPercent.Value)})" : null;
    }

    /// <summary>Why a busy background process should not be lowered now: it is transferring for someone.</summary>
    public string? PriorityBlocked(ControlProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Pid == ForegroundPid)
        {
            return "it is the program in front";
        }

        if (process.Name.Equals("audiodg.exe", StringComparison.OrdinalIgnoreCase))
        {
            return "it plays the sound";
        }

        if (process.NetBytesPerSec is >= BusyNetBytesPerSec)
        {
            return "it is transferring over the network";
        }

        return process.DiskBytesPerSec is >= BusyDiskBytesPerSec ? "it is reading or writing the disk" : null;
    }

    /// <summary>Whether the process is doing work: CPU, GPU, network or disk.</summary>
    public static string? Working(ControlProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.GpuPercent is >= BusyGpuPercent)
        {
            return $"it is using the GPU ({Percent(process.GpuPercent.Value)})";
        }

        if (process.CpuPercent is >= BusyCpuPercent)
        {
            return $"it is busy ({Percent(process.CpuPercent.Value)} CPU)";
        }

        if (process.NetBytesPerSec is >= BusyNetBytesPerSec)
        {
            return "it is transferring over the network";
        }

        return process.DiskBytesPerSec is >= BusyDiskBytesPerSec ? "it is reading or writing the disk" : null;
    }

    /// <summary>A game or 3D program drawing now (the busiest such program), or null.</summary>
    public ControlProcess? GpuUser() =>
        Processes.Where(p => !WindowsGraphics.Contains(p.Name) && !p.Name.StartsWith("PcDiag.", StringComparison.OrdinalIgnoreCase) && p.GpuPercent is >= GameGpuPercent)
            .OrderByDescending(p => p.GpuPercent).FirstOrDefault();

    /// <summary>Why the graphics driver should not be restarted now.</summary>
    public string? GpuRestartBlocked() =>
        Processes.Where(p => !WindowsGraphics.Contains(p.Name) && !p.Name.StartsWith("PcDiag.", StringComparison.OrdinalIgnoreCase) && p.GpuPercent is >= BusyGpuPercent)
            .OrderByDescending(p => p.GpuPercent).FirstOrDefault() is { } user
            ? $"{user.Name} is using the GPU ({Percent(user.GpuPercent!.Value)}); restarting the driver could crash it"
            : null;

    /// <summary>Why the standby memory should not be emptied now: a game would read its files again (stutter), or a drive is busy.</summary>
    public string? StandbyBlocked(double? busiestDrivePercent) =>
        GpuUser() is { } game
            ? $"{game.Name} is drawing ({Percent(game.GpuPercent!.Value)} GPU): it would read its files again and stutter"
            : busiestDrivePercent is >= 50 ? $"a drive is busy ({Percent(busiestDrivePercent.Value)} active)" : null;

    /// <summary>Why WSL should not be shut down now.</summary>
    public string? WslBlocked()
    {
        if (Processes.FirstOrDefault(p => p.Name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase) && Working(p) is not null) is { } vm)
        {
            return $"its virtual machine is working ({Working(vm)})";
        }

        return Processes.FirstOrDefault(p => p.Kind == "app" && (p.Name.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("Docker Desktop.exe", StringComparison.OrdinalIgnoreCase))) is { } open
            ? $"{open.Name} is open"
            : null;
    }

    /// <summary>Why a clean-up target should be left now (the programs that use it are running).</summary>
    public string? CleanBlocked(string target)
    {
        string[] browsers = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "vivaldi.exe", "opera.exe"];
        return target switch
        {
            "browser-cache" when Processes.FirstOrDefault(p => browsers.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) is { } browser => $"{browser.Name} is running",
            "shader-cache" when GpuUser() is { } game => $"{game.Name} is drawing with the GPU",
            "update-cache" when Processes.FirstOrDefault(p => p.Name is "TiWorker.exe" or "MoUsoCoreWorker.exe" or "TrustedInstaller.exe" && Working(p) is not null) is { } update => $"Windows Update is working ({update.Name})",
            "crash-dumps" when Processes.FirstOrDefault(p => p.Name.Equals("WerFault.exe", StringComparison.OrdinalIgnoreCase)) is not null => "Windows is writing an error report",
            _ => null,
        };
    }

    private static string Percent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " %";
}
