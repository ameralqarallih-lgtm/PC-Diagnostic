namespace PcDiag.Core.Control;

/// <summary>How far a process or service may be touched.</summary>
public enum ControlProtection
{
    /// <summary>A program of its own: can be ended or stopped.</summary>
    None = 0,

    /// <summary>Part of Windows: allowed, but the user confirms that Windows may misbehave until restarted.</summary>
    Caution = 1,

    /// <summary>Windows, security or the recorder itself needs it: refused.</summary>
    Protected = 2,
}

/// <summary>A running process with what can be done with it.</summary>
/// <param name="Pid">Process ID.</param>
/// <param name="StartTicks">Creation time (UTC ticks); actions name the process by PID and this, so a reused PID is never hit.</param>
/// <param name="ParentPid">Parent process ID.</param>
/// <param name="Name">Image name.</param>
/// <param name="Path">Executable path, when known.</param>
/// <param name="Publisher">Who signed or made the program.</param>
/// <param name="Kind">"app" (has a window in this session), "background" (a program without a window), "windows" (part of Windows) or "service" (hosts services).</param>
/// <param name="Protection">Whether it may be ended.</param>
/// <param name="ProtectionReason">Why it is protected or needs care.</param>
/// <param name="CpuPercent">Share of the whole PC's CPU.</param>
/// <param name="WorkingSetBytes">Memory in use (working set).</param>
/// <param name="PrivateBytes">Private memory.</param>
/// <param name="Threads">Thread count.</param>
/// <param name="Handles">Handle count.</param>
/// <param name="Priority">Priority class: "Idle", "BelowNormal", "Normal", "AboveNormal", "High", "Realtime", or null.</param>
/// <param name="NotResponding">A window of it stopped responding.</param>
/// <param name="SessionId">Windows session (0 is services).</param>
/// <param name="Services">Services it hosts.</param>
/// <param name="StartedAt">When it started.</param>
public sealed record ControlProcess(
    int Pid,
    long StartTicks,
    int ParentPid,
    string Name,
    string? Path,
    string? Publisher,
    string Kind,
    ControlProtection Protection,
    string? ProtectionReason,
    double? CpuPercent,
    long? WorkingSetBytes,
    long? PrivateBytes,
    int? Threads,
    int? Handles,
    string? Priority,
    bool NotResponding,
    int SessionId,
    IReadOnlyList<string> Services,
    DateTimeOffset StartedAt,
    double? GpuPercent = null,
    long? GpuMemoryBytes = null,
    double? NetBytesPerSec = null,
    double? DiskBytesPerSec = null);

/// <summary>A Windows service with what can be done with it.</summary>
/// <param name="Name">Service (key) name.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Description">What it does, as the service describes itself.</param>
/// <param name="Status">"Running", "Stopped", "Paused", "StartPending", "StopPending"…</param>
/// <param name="StartType">"Automatic", "AutomaticDelayed", "Manual", "Disabled", "Boot", "System".</param>
/// <param name="ProcessId">Hosting process when running.</param>
/// <param name="Path">The service program.</param>
/// <param name="Publisher">Who made it.</param>
/// <param name="Windows">Part of Windows.</param>
/// <param name="CanStop">The service accepts a stop request now.</param>
/// <param name="Protection">Whether it may be stopped or reconfigured.</param>
/// <param name="ProtectionReason">Why it is protected or needs care.</param>
public sealed record ControlService(
    string Name,
    string DisplayName,
    string? Description,
    string Status,
    string StartType,
    int? ProcessId,
    string? Path,
    string? Publisher,
    bool Windows,
    bool CanStop,
    ControlProtection Protection,
    string? ProtectionReason);

/// <summary>A program that starts when the user signs in.</summary>
/// <param name="Id">Stable id: location and entry name, e.g. "hkcu-run:Discord".</param>
/// <param name="Name">Entry name.</param>
/// <param name="Command">What it runs.</param>
/// <param name="Location">"Your sign-in (registry)", "Everyone's sign-in (registry)", "Startup folder"…</param>
/// <param name="Publisher">Who made the program.</param>
/// <param name="Enabled">Whether it starts.</param>
public sealed record StartupEntry(string Id, string Name, string Command, string Location, string? Publisher, bool Enabled);

/// <summary>Something the clean-up can free.</summary>
/// <param name="Id">One of the fixed clean-up ids.</param>
/// <param name="Name">What it is.</param>
/// <param name="Description">What is removed and what it means.</param>
/// <param name="Bytes">Space it would free (what could be measured).</param>
/// <param name="Files">Files in it.</param>
/// <param name="Recommended">Safe to clean without thinking twice (selected by default).</param>
/// <param name="Caution">What to know first, if anything.</param>
public sealed record CleanupTarget(string Id, string Name, string Description, long Bytes, int Files, bool Recommended, string? Caution);

/// <summary>A power plan.</summary>
public sealed record PowerPlan(string Id, string Name, bool Active);

/// <summary>A drive's space.</summary>
public sealed record VolumeSpace(string Name, string? Label, long TotalBytes, long FreeBytes, bool System);

/// <summary>The PC's performance-related settings as they are now.</summary>
/// <param name="PowerPlans">Power plans, one active.</param>
/// <param name="HasBattery">A battery is present (a laptop): some advice differs.</param>
/// <param name="HibernationEnabled">Hibernation (and Fast Startup) is on.</param>
/// <param name="HibernationFileBytes">Size of hiberfil.sys.</param>
/// <param name="GameMode">Windows Game Mode.</param>
/// <param name="GpuScheduling">Hardware-accelerated GPU scheduling (null when the GPU or driver does not offer it).</param>
/// <param name="StandbyBytes">Memory held as standby cache (freed by Windows whenever needed).</param>
/// <param name="FreeBytes">Memory free (zeroed and free lists).</param>
/// <param name="WslRunning">A WSL or Hyper-V virtual machine holds memory (vmmem).</param>
/// <param name="WslBytes">Memory it holds.</param>
/// <param name="Volumes">Fixed drives and their space.</param>
public sealed record SystemTuning(
    IReadOnlyList<PowerPlan> PowerPlans,
    bool HasBattery,
    bool? HibernationEnabled,
    long? HibernationFileBytes,
    bool? GameMode,
    bool? GpuScheduling,
    long? StandbyBytes,
    long? FreeBytes,
    bool WslRunning,
    long? WslBytes,
    IReadOnlyList<VolumeSpace> Volumes);

/// <summary>What the tune-up page shows: what can be cleaned and the settings that matter.</summary>
public sealed record TuneUpReport(DateTimeOffset At, IReadOnlyList<CleanupTarget> Cleanup, SystemTuning System, IReadOnlyList<StartupEntry> Startup);

/// <summary>The outcome of an action.</summary>
/// <param name="Ok">It did what was asked (fully or partly).</param>
/// <param name="Message">What happened, in words.</param>
/// <param name="FreedBytes">Space freed by a clean-up.</param>
/// <param name="Changed">How many items changed (processes ended, files removed, services stopped…).</param>
/// <param name="Problems">What could not be done, item by item.</param>
/// <param name="RestartNeeded">The change takes effect after Windows restarts.</param>
/// <param name="Skipped">What safe mode left alone because other programs were using it, and why.</param>
public sealed record ControlResult(bool Ok, string Message, long? FreedBytes = null, int Changed = 0, IReadOnlyList<string>? Problems = null, bool RestartNeeded = false,
    IReadOnlyList<string>? Skipped = null);

/// <summary>A process an optimization step would act on, with what it uses and, in safe mode, why it is left.</summary>
/// <param name="InUse">Why safe mode leaves it (null: free to act on).</param>
/// <param name="Protected">Why it is never acted on (Windows' critical processes, security software, the recorder).</param>
public sealed record OptimizeCandidate(int Pid, long StartTicks, string Name, string Kind, double? CpuPercent, long? WorkingSetBytes, double? GpuPercent,
    long? GpuMemoryBytes, double? NetBytesPerSec, double? DiskBytesPerSec, string? InUse, string? Protected);

/// <summary>A file or folder in a clean-up target's folders (GET /api/v1/tuneup/cleanup/{id}/files).</summary>
/// <param name="Root">Which of the target's folders it is in.</param>
/// <param name="Path">Its path inside that folder ("" for the folder itself), to open it.</param>
/// <param name="Folder">A folder (it can be opened).</param>
/// <param name="Bytes">Its size (a folder: of everything in it, as far as measured).</param>
/// <param name="Files">Files (a folder: in it).</param>
/// <param name="RemovableBytes">What the clean-up would remove of it (old enough, of the target's kind).</param>
/// <param name="RemovableFiles">Files the clean-up would remove.</param>
/// <param name="Modified">Last changed.</param>
/// <param name="Partial">The folder is too big to measure entirely; the figures are a lower bound.</param>
public sealed record CleanupEntry(int Root, string Path, string Name, bool Folder, long Bytes, int Files, long RemovableBytes, int RemovableFiles, DateTimeOffset? Modified, bool Partial = false);

/// <summary>What a clean-up target's folder holds: its entries, largest first.</summary>
/// <param name="Location">The folder on this PC (shown, never taken back from a request).</param>
/// <param name="More">Entries left out (only the largest are listed).</param>
/// <param name="Note">Why there is nothing to browse (the recycle bin, a missing folder).</param>
public sealed record CleanupListing(string Target, string? Location, IReadOnlyList<CleanupEntry> Entries, int More, string? Note);

/// <summary>A drive the disk step would optimize.</summary>
/// <param name="Mount">"C:".</param>
/// <param name="Ssd">A solid-state drive (retrimmed); hard disks are left to Windows' scheduled defragmentation.</param>
/// <param name="ActivePercent">Its disk's active time now.</param>
public sealed record OptimizeDrive(string Mount, string Model, bool Ssd, double? ActivePercent, string? InUse);

/// <summary>A graphics adapter (its driver can be restarted).</summary>
public sealed record OptimizeGpu(string InstanceId, string Name);

/// <summary>What each optimization step would do now (GET /api/v1/tuneup/plan).</summary>
/// <param name="GpuMemory">Background programs holding GPU memory (ending them gives it back).</param>
/// <param name="Memory">Programs holding the most memory (trimming moves idle pages out).</param>
/// <param name="Cpu">Busy background programs (lowered to below-normal priority).</param>
/// <param name="StandbyInUse">Why safe mode would not empty the standby memory now.</param>
/// <param name="GpuRestartInUse">Why safe mode would not restart the graphics driver now.</param>
/// <param name="WslInUse">Why safe mode would not shut WSL down now.</param>
public sealed record OptimizePlan(DateTimeOffset At, IReadOnlyList<OptimizeCandidate> GpuMemory, IReadOnlyList<OptimizeCandidate> Memory, IReadOnlyList<OptimizeCandidate> Cpu,
    IReadOnlyList<OptimizeDrive> Drives, IReadOnlyList<OptimizeGpu> Gpus, long? StandbyBytes, string? StandbyInUse, string? GpuRestartInUse, long? WslBytes, string? WslInUse,
    string? ForegroundName);

/// <summary>The fixed actions the tune-up offers (nothing else can be asked for).</summary>
public static class TuneUpActions
{
    /// <summary>Remove the chosen clean-up targets' files (Items: clean-up ids).</summary>
    public const string Clean = "clean";

    /// <summary>End processes (Items: "pid:startTicks").</summary>
    public const string EndProcesses = "end-processes";

    /// <summary>Stop services (Items: service names; Value "manual" or "disabled" also changes how they start).</summary>
    public const string StopServices = "stop-services";

    /// <summary>Switch power plan (Value: the plan's GUID).</summary>
    public const string PowerPlan = "power-plan";

    /// <summary>Turn hibernation on or off (Value "on" / "off").</summary>
    public const string Hibernation = "hibernation";

    /// <summary>Turn Game Mode on or off (Value "on" / "off").</summary>
    public const string GameMode = "game-mode";

    /// <summary>Turn hardware-accelerated GPU scheduling on or off (Value "on" / "off"; restart needed).</summary>
    public const string GpuScheduling = "gpu-scheduling";

    /// <summary>Empty the standby memory list.</summary>
    public const string FreeStandby = "free-standby";

    /// <summary>Clear the DNS cache.</summary>
    public const string FlushDns = "flush-dns";

    /// <summary>Shut down WSL (its virtual machine gives back its memory).</summary>
    public const string ShutdownWsl = "shutdown-wsl";

    /// <summary>Enable or disable a startup entry (Items: its id; Value "on" / "off").</summary>
    public const string Startup = "startup";

    /// <summary>Give back GPU memory: end the background programs holding it (Items: "pid:startTicks").</summary>
    public const string FreeVram = "free-vram";

    /// <summary>Restart the graphics driver (Value: the adapter's instance ID; empty: every adapter). Frees memory a driver kept.</summary>
    public const string RestartGpu = "restart-gpu";

    /// <summary>Trim programs' working sets (Items: "pid:startTicks"; none: every idle background program).</summary>
    public const string TrimMemory = "trim-memory";

    /// <summary>Lower busy background programs to below-normal priority (Items: "pid:startTicks"; none: those found).</summary>
    public const string LowerPriority = "lower-priority";

    /// <summary>Retrim solid-state drives (Items: "C:"…; none: every SSD).</summary>
    public const string RetrimDrives = "retrim-drives";

    /// <summary>Every action.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Clean, EndProcesses, StopServices, PowerPlan, Hibernation, GameMode, GpuScheduling, FreeStandby, FlushDns, ShutdownWsl, Startup,
        FreeVram, RestartGpu, TrimMemory, LowerPriority, RetrimDrives,
    };
}

/// <summary>What the process and service controls do.</summary>
public interface ISystemControl
{
    /// <summary>Running processes.</summary>
    IReadOnlyList<ControlProcess> Processes();

    /// <summary>Windows services.</summary>
    IReadOnlyList<ControlService> Services();

    /// <summary>Ends a process (and its children with <paramref name="tree"/>).</summary>
    ControlResult EndProcess(int pid, long startTicks, bool tree, bool confirmed, bool safe = false);

    /// <summary>Sets a process's priority class.</summary>
    ControlResult SetPriority(int pid, long startTicks, string priority);

    /// <summary>Starts, stops or restarts a service, or changes how it starts.</summary>
    ControlResult ControlService(string name, string action, string? startType, bool confirmed, bool safe = false);

    /// <summary>What the tune-up can do now.</summary>
    Task<TuneUpReport> ScanAsync(CancellationToken cancellationToken);

    /// <summary>Runs one of <see cref="TuneUpActions"/>.</summary>
    /// <param name="safe">Safe mode: leave anything other programs are using now, and say so.</param>
    Task<ControlResult> ApplyAsync(string action, IReadOnlyList<string> items, string? value, bool confirmed, CancellationToken cancellationToken, bool safe = false);

    /// <summary>What a clean-up target's folder holds (root -1: the target's folders themselves).</summary>
    CleanupListing CleanupFiles(string target, int root, string path) => new(target, null, [], 0, "Not available.");

    /// <summary>What each optimization step would do now, with what safe mode would leave.</summary>
    Task<OptimizePlan> PlanAsync(CancellationToken cancellationToken) => Task.FromResult(new OptimizePlan(DateTimeOffset.UtcNow, [], [], [], [], [], null, null, null, null, null, null));
}
