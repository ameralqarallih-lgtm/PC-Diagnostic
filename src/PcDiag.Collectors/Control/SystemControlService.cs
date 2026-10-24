using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Control;
using PcDiag.Core.Events;
using PcDiag.Core.Snapshots;
using PcDiag.Core.Telemetry;
using PcDiag.Engine.Control;
using PcDiag.Platform.Windows.Kernel;
using PcDiag.Platform.Windows.Power;
using PcDiag.Platform.Windows.Processes;
using PcDiag.Platform.Windows.Services;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Collectors.Control;

/// <summary>
/// The process and service controls and the tune-up, run by the elevated recorder. Every action names its target
/// precisely (a process by PID and start time, a service by name, a clean-up target or setting by a fixed id), is
/// checked against <see cref="ControlPolicy"/>, and is logged and recorded as an event (so incidents show what the
/// user changed). Nothing here takes a path or a command from a request.
/// </summary>
public sealed partial class SystemControlService(ProcessDirectory directory, ISystemEventSink events, TimeProvider time, ILogger<SystemControlService> logger) : ISystemControl
{
    /// <summary>Channel of the recorder's control events.</summary>
    public const string Channel = "pcdiag.control";

    private static readonly TimeSpan ServiceWait = TimeSpan.FromSeconds(20);
    private static readonly string WindowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    private readonly ConcurrentDictionary<string, string?> _companies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ProcessKey, string?> _paths = new();
    private readonly NtProcesses _table = new();
    private readonly Lock _tableGate = new();
    private readonly ConcurrentDictionary<string, (ServiceDetails? Details, DateTimeOffset At)> _serviceDetails = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlyList<ControlProcess> Processes()
    {
        var services = ServiceControlManager.Enumerate().Where(s => s.ProcessId > 0).GroupBy(s => s.ProcessId).ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToList());
        var visible = VisibleWindows.ProcessIds();
        var self = Environment.ProcessId;
        // What runs now comes from Windows; the recorder's samples add CPU use, signatures and responsiveness.
        var sampled = directory.Snapshot().ToDictionary(p => p.Metrics.Key);
        var result = new List<ControlProcess>();
        foreach (var raw in Table())
        {
            var pid = raw.Pid;
            var key = new ProcessKey(pid, raw.StartTicks);
            sampled.TryGetValue(key, out var entry);
            var (metrics, identity) = entry;
            // Paths arrive with the collector's enrichment (64 processes per tick); a missing one is asked for directly.
            var path = identity?.Path ?? _paths.GetOrAdd(key, k => k.Pid is 0 or 4 ? null : ProcessDetails.TryGetImagePath(k.Pid));
            var windows = ControlPolicy.IsWindowsPath(path, WindowsFolder);
            var hosted = services.GetValueOrDefault(pid) ?? [];
            var name = identity?.Name ?? raw.Name;
            var critical = windows && ProcessControl.IsCritical(pid);
            var own = pid == self || name.StartsWith("PcDiag.", StringComparison.OrdinalIgnoreCase);
            var (protection, reason) = ControlPolicy.ForProcess(name, pid, windows, critical, own);
            // Windows' own processes without a program file (Memory Compression, Registry, System) are Windows too.
            var kind = hosted.Count > 0 ? "service" : visible.Contains(pid) ? "app" : windows || pid is 0 or 4 || (protection != ControlProtection.None && !own && path is null) ? "windows" : "background";
            result.Add(new ControlProcess(
                pid,
                raw.StartTicks,
                raw.ParentPid,
                name,
                path,
                string.IsNullOrWhiteSpace(identity?.Signer) ? Company(path) : identity.Signer,
                kind,
                protection,
                reason,
                metrics?.CpuPercent.Value,
                raw.WorkingSet,
                raw.PrivateBytes,
                raw.Threads,
                raw.Handles,
                pid is 0 or 4 ? null : ProcessControl.GetPriority(pid),
                metrics?.State == ProcessState.NotResponding,
                raw.SessionId,
                hosted,
                new DateTimeOffset(raw.StartTicks > 0 ? raw.StartTicks : DateTime.UnixEpoch.Ticks, TimeSpan.Zero),
                directory.Gpu(pid) is { } gpu ? Math.Round(gpu.Percent, 1) : null,
                directory.Gpu(pid) is { MemoryBytes: > 0 } vram ? vram.MemoryBytes : null,
                metrics?.TrueIo is { } io ? io.NetReceiveBytesPerSec + io.NetSendBytesPerSec : null,
                metrics?.TrueIo is { } disk ? disk.DiskReadBytesPerSec + disk.DiskWriteBytesPerSec
                    : metrics is { } m && (m.IoReadBytesPerSec.HasValue || m.IoWriteBytesPerSec.HasValue) ? m.IoReadBytesPerSec.GetValueOrDefault(0) + m.IoWriteBytesPerSec.GetValueOrDefault(0) : null));
        }

        if (_paths.Count > 4096)
        {
            _paths.Clear();
        }

        return result;
    }

    private readonly record struct RawEntry(int Pid, int ParentPid, long StartTicks, string Name, long WorkingSet, long PrivateBytes, int Threads, int Handles, int SessionId);

    /// <summary>Every process running now (one NtQuerySystemInformation call).</summary>
    private List<RawEntry> Table()
    {
        var entries = new List<RawEntry>(600);
        lock (_tableGate)
        {
            _ = _table.Visit(ref entries, static (ref List<RawEntry> list, in RawProcess p) => list.Add(new RawEntry(
                p.Pid,
                p.ParentPid,
                p.CreateTime > 0 ? DateTime.FromFileTimeUtc(p.CreateTime).Ticks : 0,
                p.Pid == 0 ? "System Idle Process" : p.Name.ToString(),
                p.WorkingSet,
                p.PrivateWorkingSet,
                p.Threads,
                p.Handles,
                p.SessionId)));
        }

        return entries;
    }

    /// <inheritdoc />
    public IReadOnlyList<ControlService> Services()
    {
        var all = ServiceControlManager.Enumerate();
        var configs = ServiceControlManager.QueryStartConfigs(all.Select(s => s.Name));
        var now = time.GetUtcNow();
        return all.Select(s =>
        {
            var details = Details(s.Name, now);
            var program = ServiceControl.ProgramOf(details?.BinaryPath);
            var windows = ControlPolicy.IsWindowsPath(program, WindowsFolder);
            var (protection, reason) = ControlPolicy.ForService(s.Name, windows);
            var config = configs.GetValueOrDefault(s.Name);
            return new ControlService(
                s.Name,
                s.DisplayName,
                details?.Description,
                StatusName(s.State),
                StartTypeName(config),
                s.ProcessId > 0 ? s.ProcessId : null,
                program,
                Company(program),
                windows,
                (s.ControlsAccepted & 1) != 0,
                protection,
                reason);
        }).ToList();
    }

    /// <inheritdoc />
    public ControlResult EndProcess(int pid, long startTicks, bool tree, bool confirmed, bool safe = false)
    {
        var processes = Processes();
        var target = processes.FirstOrDefault(p => p.Pid == pid && Math.Abs(p.StartTicks - startTicks) <= TimeSpan.TicksPerMillisecond);
        if (target is null)
        {
            return new ControlResult(false, "The process has already ended.");
        }

        if (Refused(target.Protection, target.ProtectionReason, confirmed) is { } refused)
        {
            return refused;
        }

        var guard = safe ? new ResourceGuard(processes, VisibleWindows.ForegroundProcessId()) : null;
        if (guard?.EndBlocked(target) is { } inUse)
        {
            return SafeLeft($"{target.Name} ({target.Pid})", inUse);
        }

        // Children first (only those started after their parent: a PID can be reused by an unrelated program).
        var order = new List<ControlProcess>();
        if (tree)
        {
            var queue = new Queue<ControlProcess>([target]);
            while (queue.TryDequeue(out var parent))
            {
                foreach (var child in processes.Where(p => p.ParentPid == parent.Pid && p.StartTicks > parent.StartTicks && p.Pid != parent.Pid))
                {
                    order.Add(child);
                    queue.Enqueue(child);
                }
            }

            order.Reverse();
        }

        order.Add(target);
        var problems = new List<string>();
        var ended = 0;
        foreach (var process in order)
        {
            if (process != target && process.Protection == ControlProtection.Protected)
            {
                problems.Add($"{process.Name} ({process.Pid}) was left: {process.ProtectionReason}");
                continue;
            }

            if (process != target && guard?.EndBlocked(process) is { } childInUse)
            {
                problems.Add($"Safe mode left {process.Name} ({process.Pid}): {childInUse}");
                continue;
            }

            var outcome = ProcessControl.Terminate(process.Pid, process.StartTicks);
            if (outcome.Ok)
            {
                ended++;
                Audit($"You ended {process.Name} (PID {process.Pid})", $"{process.Name} (PID {process.Pid}) was ended from PC Diagnostic.", EventCategory.Other, "process-end", process.Pid, process.Name);
            }
            else
            {
                problems.Add($"{process.Name} ({process.Pid}): {outcome.Error}");
            }
        }

        return new ControlResult(ended > 0, ended > 0 ? (tree && order.Count > 1 ? $"Ended {target.Name} and {ended - 1} process(es) it started." : $"Ended {target.Name}.") : problems.FirstOrDefault() ?? "Nothing was ended.",
            Changed: ended, Problems: problems.Count > 0 ? problems : null);
    }

    /// <inheritdoc />
    public ControlResult SetPriority(int pid, long startTicks, string priority)
    {
        if (priority == "Realtime")
        {
            return new ControlResult(false, "Realtime priority can freeze the whole PC (even the mouse); choose High at most.");
        }

        var target = Processes().FirstOrDefault(p => p.Pid == pid && Math.Abs(p.StartTicks - startTicks) <= TimeSpan.TicksPerMillisecond);
        if (target is null)
        {
            return new ControlResult(false, "The process has already ended.");
        }

        if (target.Protection == ControlProtection.Protected)
        {
            return new ControlResult(false, $"{target.Name} is protected: {target.ProtectionReason}");
        }

        var outcome = ProcessControl.SetPriority(pid, startTicks, priority);
        if (!outcome.Ok)
        {
            return new ControlResult(false, outcome.Error!);
        }

        Audit($"You set {target.Name} (PID {pid}) to {priority} priority", $"{target.Name} (PID {pid}) priority set to {priority} from PC Diagnostic.", EventCategory.Other, "process-priority", pid, target.Name);
        return new ControlResult(true, $"{target.Name} now runs at {Words(priority)} priority (until it ends).", Changed: 1);
    }

    /// <inheritdoc />
    public ControlResult ControlService(string name, string action, string? startType, bool confirmed, bool safe = false)
    {
        var service = Services().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (service is null)
        {
            return new ControlResult(false, "There is no such service.");
        }

        // Safe mode: a service whose process is working is not stopped (or turned off) under the programs using it.
        if (safe && action is "stop" or "restart" || safe && action == "start-type" && startType == "Disabled")
        {
            var host = service.ProcessId is int hostPid and > 0 ? Processes().FirstOrDefault(p => p.Pid == hostPid) : null;
            if (host is not null && ResourceGuard.Working(host) is { } inUse)
            {
                return SafeLeft(service.DisplayName, inUse.Replace("it is", "its process is", StringComparison.Ordinal));
            }
        }

        if (Refused(service.Protection, service.ProtectionReason, confirmed) is { } refused && action != "start")
        {
            return refused;
        }

        ControlOutcome outcome;
        string done;
        switch (action)
        {
            case "start":
                outcome = ServiceControl.Start(service.Name, ServiceWait);
                done = $"{service.DisplayName} started.";
                break;
            case "stop":
                outcome = ServiceControl.Stop(service.Name, ServiceWait);
                done = $"{service.DisplayName} stopped.";
                break;
            case "restart":
                outcome = ServiceControl.Stop(service.Name, ServiceWait);
                if (outcome.Ok)
                {
                    outcome = ServiceControl.Start(service.Name, ServiceWait);
                }

                done = $"{service.DisplayName} restarted.";
                break;
            case "start-type" when startType is not null && StartTypes.TryGetValue(startType, out var type):
                outcome = ServiceControl.SetStartType(service.Name, type.Code, type.Delayed);
                done = $"{service.DisplayName} now starts {Words(startType)}.";
                break;
            default:
                return new ControlResult(false, "Unknown service action.");
        }

        if (!outcome.Ok)
        {
            return new ControlResult(false, outcome.Error!);
        }

        Audit(done.Replace(service.DisplayName, $"You changed {service.DisplayName}:", StringComparison.Ordinal).TrimEnd('.'), $"{service.DisplayName} ({service.Name}): {action}{(startType is null ? string.Empty : $" {startType}")} from PC Diagnostic.",
            EventCategory.Service, $"service-{action}", service.ProcessId, null, service.Name);
        return new ControlResult(true, done, Changed: 1);
    }

    /// <inheritdoc />
    public async Task<TuneUpReport> ScanAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cleanup = Task.Run(() => Cleanup.Scan(now), cancellationToken);
        var system = Task.Run(Tuning, cancellationToken);
        var startup = Task.Run(() => StartupItems.List().Select(i => new StartupEntry(i.Id, i.Name, i.Command, i.Location, Company(i.Program), i.Enabled)).ToList(), cancellationToken);
        await Task.WhenAll(cleanup, system, startup).ConfigureAwait(false);
        return new TuneUpReport(now, await cleanup.ConfigureAwait(false), await system.ConfigureAwait(false), await startup.ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<ControlResult> ApplyAsync(string action, IReadOnlyList<string> items, string? value, bool confirmed, CancellationToken cancellationToken, bool safe = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        switch (action)
        {
            case TuneUpActions.Clean:
            {
                // Safe mode: a cache whose program is running (a browser's, a game's shaders) is left for now.
                var guard = safe ? Guard() : null;
                var skipped = new List<string>();
                var chosen = items.Where(id =>
                {
                    if (guard?.CleanBlocked(id) is { } inUse)
                    {
                        skipped.Add($"{CleanName(id)}: {inUse}");
                        return false;
                    }

                    return true;
                }).ToList();
                if (chosen.Count == 0 && skipped.Count > 0)
                {
                    return new ControlResult(false, $"Safe mode left everything chosen: {skipped[0]}.", Skipped: skipped);
                }

                var cleaned = await Task.Run(() => Clean(chosen), cancellationToken).ConfigureAwait(false);
                return skipped.Count == 0 ? cleaned : cleaned with { Skipped = skipped };
            }

            case TuneUpActions.EndProcesses:
                return EndProcesses(items, confirmed, safe);
            case TuneUpActions.StopServices:
                return await Task.Run(() => StopServices(items, value, confirmed, safe), cancellationToken).ConfigureAwait(false);
            case TuneUpActions.FreeVram:
                return FreeVram(items, confirmed, safe);
            case TuneUpActions.RestartGpu:
                return await RestartGpuAsync(value, confirmed, safe, cancellationToken).ConfigureAwait(false);
            case TuneUpActions.TrimMemory:
                return TrimMemory(items, safe);
            case TuneUpActions.LowerPriority:
                return LowerPriority(items, safe);
            case TuneUpActions.RetrimDrives:
                return await RetrimAsync(items, safe, cancellationToken).ConfigureAwait(false);
            case TuneUpActions.PowerPlan:
            {
                var plan = PowerTuning.Schemes().FirstOrDefault(p => string.Equals(p.Id.ToString(), value, StringComparison.OrdinalIgnoreCase));
                if (plan is null || !PowerTuning.Activate(plan.Id))
                {
                    return new ControlResult(false, "That power plan could not be switched to.");
                }

                Audit($"You switched to the {plan.Name} power plan", $"Power plan set to {plan.Name} from PC Diagnostic.", EventCategory.Power, "power-plan");
                return new ControlResult(true, $"The {plan.Name} power plan is active.", Changed: 1);
            }

            case TuneUpActions.Hibernation:
            {
                var on = value == "on";
                var (ok, output) = await RunAsync(Path.Combine(System32, "powercfg.exe"), ["/hibernate", on ? "on" : "off"], cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    return new ControlResult(false, $"Windows refused: {output}");
                }

                Audit($"You turned hibernation {(on ? "on" : "off")}", $"Hibernation turned {(on ? "on" : "off")} from PC Diagnostic.", EventCategory.Power, "hibernation");
                return new ControlResult(true, on ? "Hibernation (and Fast Startup) is on." : "Hibernation is off: its file is removed, sleep still works, and Windows starts fully each time (no Fast Startup).", Changed: 1);
            }

            case TuneUpActions.GameMode:
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar", writable: true);
                key.SetValue("AutoGameModeEnabled", value == "on" ? 1 : 0, RegistryValueKind.DWord);
                key.SetValue("AllowAutoGameMode", value == "on" ? 1 : 0, RegistryValueKind.DWord);
                Audit($"You turned Game Mode {value}", $"Game Mode turned {value} from PC Diagnostic.", EventCategory.Other, "game-mode");
                return new ControlResult(true, value == "on" ? "Game Mode is on." : "Game Mode is off.", Changed: 1);
            }

            case TuneUpActions.GpuScheduling:
            {
                using var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", writable: true);
                key.SetValue("HwSchMode", value == "on" ? 2 : 1, RegistryValueKind.DWord);
                Audit($"You turned hardware-accelerated GPU scheduling {value}", $"Hardware-accelerated GPU scheduling turned {value} from PC Diagnostic (after a restart).", EventCategory.Gpu, "gpu-scheduling");
                return new ControlResult(true, $"Hardware-accelerated GPU scheduling turns {value} when Windows restarts.", Changed: 1, RestartNeeded: true);
            }

            case TuneUpActions.FreeStandby:
            {
                if (safe && Guard().StandbyBlocked(BusiestDrive()) is { } standbyInUse)
                {
                    return SafeLeft("The standby memory", standbyInUse);
                }

                var before = MemoryLists.Query().Standby;
                if (MemoryLists.PurgeStandby() is { } problem)
                {
                    return new ControlResult(false, problem);
                }

                var after = MemoryLists.Query().Standby;
                var freed = before is { } b && after is { } a ? Math.Max(0, b - a) : (long?)null;
                Audit("You emptied the standby memory", "Standby memory emptied from PC Diagnostic.", EventCategory.Other, "free-standby");
                return new ControlResult(true, "The standby memory was emptied; Windows fills it again with what programs read.", FreedBytes: freed, Changed: 1);
            }

            case TuneUpActions.FlushDns:
                if (!ShellCleanup.FlushDns())
                {
                    return new ControlResult(false, "Windows did not clear the DNS cache.");
                }

                Audit("You cleared the DNS cache", "DNS cache cleared from PC Diagnostic.", EventCategory.Network, "flush-dns");
                return new ControlResult(true, "The DNS cache is cleared: names are looked up afresh.", Changed: 1);
            case TuneUpActions.ShutdownWsl:
            {
                var wsl = Path.Combine(System32, "wsl.exe");
                if (!File.Exists(wsl))
                {
                    return new ControlResult(false, "WSL is not installed.");
                }

                if (safe && Guard().WslBlocked() is { } wslInUse)
                {
                    return SafeLeft("WSL", wslInUse);
                }

                var before = VirtualMachineMemory();
                var (ok, output) = await RunAsync(wsl, ["--shutdown"], cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    return new ControlResult(false, $"WSL did not shut down: {output}");
                }

                Audit("You shut down WSL", "WSL was shut down from PC Diagnostic.", EventCategory.Other, "shutdown-wsl");
                return new ControlResult(true, "WSL is shut down; it starts again the next time a Linux program or Docker needs it.", FreedBytes: before, Changed: 1);
            }

            case TuneUpActions.Startup:
            {
                var id = items.Count > 0 ? items[0] : null;
                var entry = id is null ? null : StartupItems.List().FirstOrDefault(i => i.Id == id);
                if (entry is null || !StartupItems.SetEnabled(entry.Id, value == "on", time.GetUtcNow()))
                {
                    return new ControlResult(false, "There is no such startup entry.");
                }

                Audit($"You {(value == "on" ? "enabled" : "disabled")} {entry.Name} at startup", $"Startup entry {entry.Name} ({entry.Location}) {(value == "on" ? "enabled" : "disabled")} from PC Diagnostic.", EventCategory.Other, "startup");
                return new ControlResult(true, value == "on" ? $"{entry.Name} starts when you sign in." : $"{entry.Name} no longer starts when you sign in.", Changed: 1);
            }

            default:
                return new ControlResult(false, "Unknown tune-up action.");
        }
    }

    private ControlResult Clean(IReadOnlyList<string> ids)
    {
        long freed = 0;
        int removed = 0, left = 0;
        var known = Cleanup.Targets.Select(t => t.Id).Append(Cleanup.RecycleBin).ToHashSet(StringComparer.Ordinal);
        foreach (var id in ids.Where(known.Contains).Distinct())
        {
            var (f, r, l) = Cleanup.Clean(id, time.GetUtcNow());
            freed += f;
            removed += r;
            left += l;
        }

        Audit("You cleaned up files", $"Clean-up from PC Diagnostic ({string.Join(", ", ids)}): {removed} files, {freed} bytes.", EventCategory.FileSystem, "clean");
        LogCleaned(logger, string.Join(", ", ids), removed, freed, left);
        return new ControlResult(true, left > 0 ? $"{removed} files removed; {left} in use were left." : $"{removed} files removed.", FreedBytes: freed, Changed: removed);
    }

    private ControlResult EndProcesses(IReadOnlyList<string> items, bool confirmed, bool safe)
    {
        int ended = 0;
        var problems = new List<string>();
        var skipped = new List<string>();
        foreach (var item in items.Distinct())
        {
            var colon = item.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || !int.TryParse(item[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                || !long.TryParse(item[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            {
                continue;
            }

            var result = EndProcess(pid, ticks, tree: false, confirmed, safe);
            if (result.Ok)
            {
                ended += result.Changed;
            }
            else if (result.Skipped is { } left)
            {
                skipped.AddRange(left);
            }
            else
            {
                problems.Add(result.Message);
            }
        }

        return new ControlResult(ended > 0, ended > 0 ? $"Ended {ended} process(es)." : skipped.Count > 0 ? $"Safe mode left {skipped[0]}." : problems.FirstOrDefault() ?? "Nothing was ended.",
            Changed: ended, Problems: problems.Count > 0 ? problems : null, Skipped: skipped.Count > 0 ? skipped : null);
    }

    private ControlResult StopServices(IReadOnlyList<string> names, string? then, bool confirmed, bool safe)
    {
        int stopped = 0;
        var problems = new List<string>();
        var skipped = new List<string>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var result = ControlService(name, "stop", null, confirmed, safe);
            if (result.Skipped is { } left)
            {
                skipped.AddRange(left);
                continue;
            }

            if (result.Ok && then is "Manual" or "Disabled")
            {
                result = ControlService(name, "start-type", then, confirmed);
            }

            if (result.Ok)
            {
                stopped++;
            }
            else
            {
                problems.Add($"{name}: {result.Message}");
            }
        }

        return new ControlResult(stopped > 0, stopped > 0 ? $"Stopped {stopped} service(s)." : skipped.Count > 0 ? $"Safe mode left {skipped[0]}." : problems.FirstOrDefault() ?? "Nothing was stopped.",
            Changed: stopped, Problems: problems.Count > 0 ? problems : null, Skipped: skipped.Count > 0 ? skipped : null);
    }

    private SystemTuning Tuning()
    {
        bool? hibernation = null;
        using (var power = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
        {
            if ((power?.GetValue("HibernateEnabled") ?? power?.GetValue("HibernateEnabledDefault")) is int h)
            {
                hibernation = h != 0;
            }
        }

        var hiberfil = Path.Combine(Path.GetPathRoot(WindowsFolder) ?? @"C:\", "hiberfil.sys");
        long? hiberfilBytes = null;
        try
        {
            var file = new FileInfo(hiberfil);
            hiberfilBytes = file.Exists ? file.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Size unknown.
        }

        // Neither value set: hibernation is on exactly when Windows keeps its file.
        hibernation ??= hiberfilBytes is not null;

        bool? gameMode = true; // on unless turned off (Windows 10 1903 and later)
        using (var bar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar"))
        {
            if (bar?.GetValue("AutoGameModeEnabled") is int g)
            {
                gameMode = g != 0;
            }
        }

        bool? scheduling = null;
        using (var graphics = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
        {
            if (graphics?.GetValue("HwSchMode") is int mode)
            {
                scheduling = mode == 2;
            }
        }

        var (standby, free) = MemoryLists.Query();
        var vm = VirtualMachineMemory();
        var volumes = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => new VolumeSpace(
            d.Name.TrimEnd('\\'),
            string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
            d.TotalSize,
            d.AvailableFreeSpace,
            string.Equals(d.Name, Path.GetPathRoot(WindowsFolder), StringComparison.OrdinalIgnoreCase))).ToList();
        return new SystemTuning(
            PowerTuning.Schemes().Select(s => new PowerPlan(s.Id.ToString(), s.Name, s.Active)).ToList(),
            PowerTuning.HasBattery(),
            hibernation,
            hiberfilBytes,
            gameMode,
            scheduling,
            standby,
            free,
            vm is not null,
            vm,
            volumes);
    }

    /// <summary>Memory held by WSL's or Hyper-V's virtual machine (vmmem processes), or null when none runs.</summary>
    private long? VirtualMachineMemory()
    {
        var vm = Table().Where(p => p.Name.StartsWith("vmmem", StringComparison.OrdinalIgnoreCase)).ToList();
        return vm.Count == 0 ? null : vm.Sum(p => p.WorkingSet);
    }

    /// <summary>Safe mode left something alone because other programs were using it.</summary>
    private static ControlResult SafeLeft(string what, string why) => new(false, $"Safe mode left {what}: {why}.", Skipped: [$"{what}: {why}"]);

    private ResourceGuard Guard() => new(Processes(), VisibleWindows.ForegroundProcessId());

    private double? BusiestDrive() => directory.Disks().Select(d => d.ActivePercent).Where(a => a.HasValue).Select(a => a.Value!.Value).DefaultIfEmpty().Max() is var max and > 0 ? max : null;

    private static string CleanName(string id) => Cleanup.Targets.FirstOrDefault(t => t.Id == id)?.Name ?? id;

    private static bool TryProcessItem(string item, out int pid, out long ticks)
    {
        pid = 0;
        ticks = 0;
        var colon = item.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && int.TryParse(item[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out pid)
            && long.TryParse(item[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
    }

    /// <summary>The processes named by "pid:startTicks" items (or, with none, <paramref name="otherwise"/>).</summary>
    private static List<ControlProcess> Chosen(IReadOnlyList<ControlProcess> processes, IReadOnlyList<string> items, Func<IEnumerable<ControlProcess>> otherwise) =>
        items.Count == 0
            ? [.. otherwise()]
            : [.. items.Select(i => TryProcessItem(i, out var pid, out var ticks) ? processes.FirstOrDefault(p => p.Pid == pid && Math.Abs(p.StartTicks - ticks) <= TimeSpan.TicksPerMillisecond) : null).OfType<ControlProcess>()];

    private static bool Untouchable(ControlProcess p) =>
        p.Protection == ControlProtection.Protected || p.Pid is 0 or 4 || p.Name.StartsWith("PcDiag.", StringComparison.OrdinalIgnoreCase)
        || p.Name is "dwm.exe" or "csrss.exe" or "Memory Compression" or "Registry" or "System" or "Idle" or "MsMpEng.exe";

    private const long VramWorthFreeing = 64L * 1024 * 1024;
    private const long MemoryWorthTrimming = 200L * 1024 * 1024;
    private const double BusyBackground = 5;

    /// <summary>Background programs holding GPU memory, most first.</summary>
    private static IEnumerable<ControlProcess> VramHolders(IReadOnlyList<ControlProcess> processes) =>
        processes.Where(p => !Untouchable(p) && p.GpuMemoryBytes >= VramWorthFreeing).OrderByDescending(p => p.GpuMemoryBytes);

    /// <summary>Programs holding the most memory.</summary>
    private static IEnumerable<ControlProcess> MemoryHolders(IReadOnlyList<ControlProcess> processes) =>
        processes.Where(p => !Untouchable(p) && p.WorkingSetBytes >= MemoryWorthTrimming).OrderByDescending(p => p.WorkingSetBytes);

    /// <summary>Busy background programs (no window).</summary>
    private static IEnumerable<ControlProcess> BusyBackgroundPrograms(IReadOnlyList<ControlProcess> processes, int foreground) =>
        processes.Where(p => !Untouchable(p) && p.Protection == ControlProtection.None && p.Kind != "app" && p.Pid != foreground && p.CpuPercent >= BusyBackground
            && p.Priority is not ("BelowNormal" or "Idle")).OrderByDescending(p => p.CpuPercent);

    /// <summary>GPU memory back: ends the chosen background programs holding it.</summary>
    private ControlResult FreeVram(IReadOnlyList<string> items, bool confirmed, bool safe)
    {
        var processes = Processes();
        var guard = new ResourceGuard(processes, VisibleWindows.ForegroundProcessId());
        var chosen = Chosen(processes, items, () => VramHolders(processes).Where(p => guard.EndBlocked(p) is null));
        long freed = 0;
        int ended = 0;
        var problems = new List<string>();
        var skipped = new List<string>();
        foreach (var process in chosen)
        {
            if (Untouchable(process))
            {
                problems.Add($"{process.Name} is never ended by PC Diagnostic.");
                continue;
            }

            if (safe && guard.EndBlocked(process) is { } inUse)
            {
                skipped.Add($"{process.Name} ({process.Pid}): {inUse}");
                continue;
            }

            var result = EndProcess(process.Pid, process.StartTicks, tree: false, confirmed);
            if (result.Ok)
            {
                ended++;
                freed += process.GpuMemoryBytes ?? 0;
            }
            else
            {
                problems.Add($"{process.Name}: {result.Message}");
            }
        }

        return new ControlResult(ended > 0, ended > 0 ? $"Ended {ended} program(s) holding GPU memory." : skipped.Count > 0 ? $"Safe mode left {skipped[0]}." : problems.FirstOrDefault() ?? "No background program holds GPU memory worth freeing.",
            FreedBytes: freed > 0 ? freed : null, Changed: ended, Problems: problems.Count > 0 ? problems : null, Skipped: skipped.Count > 0 ? skipped : null);
    }

    /// <summary>Trims working sets; what was freed is measured from the process table before and after.</summary>
    private ControlResult TrimMemory(IReadOnlyList<string> items, bool safe)
    {
        var processes = Processes();
        var guard = new ResourceGuard(processes, VisibleWindows.ForegroundProcessId());
        var chosen = Chosen(processes, items, () => MemoryHolders(processes));
        var before = Table().ToDictionary(r => (r.Pid, r.StartTicks), r => r.WorkingSet);
        int trimmed = 0;
        var problems = new List<string>();
        var skipped = new List<string>();
        foreach (var process in chosen)
        {
            if (Untouchable(process))
            {
                continue;
            }

            if (safe && guard.TrimBlocked(process) is { } inUse)
            {
                skipped.Add($"{process.Name} ({process.Pid}): {inUse}");
                continue;
            }

            var outcome = ProcessControl.TrimWorkingSet(process.Pid, process.StartTicks);
            if (outcome.Ok)
            {
                trimmed++;
            }
            else
            {
                problems.Add($"{process.Name}: {outcome.Error}");
            }
        }

        var after = Table().ToDictionary(r => (r.Pid, r.StartTicks), r => r.WorkingSet);
        var freed = chosen.Sum(p => before.TryGetValue((p.Pid, p.StartTicks), out var b) && after.TryGetValue((p.Pid, p.StartTicks), out var a) ? Math.Max(0, b - a) : 0);
        if (trimmed > 0)
        {
            Audit($"You trimmed the memory of {trimmed} program(s)", $"Working sets of {trimmed} program(s) trimmed from PC Diagnostic ({freed} bytes).", EventCategory.Other, "trim-memory");
        }

        return new ControlResult(trimmed > 0, trimmed > 0 ? $"Trimmed {trimmed} program(s): their idle memory is on the standby list, given back at once if they need it." : skipped.Count > 0 ? $"Safe mode left {skipped[0]}." : problems.FirstOrDefault() ?? "No program holds memory worth trimming.",
            FreedBytes: freed > 0 ? freed : null, Changed: trimmed, Problems: problems.Count > 0 ? problems : null, Skipped: skipped.Count > 0 ? skipped : null);
    }

    /// <summary>Lowers busy background programs to below-normal priority (until they end), so what you use gets the CPU first.</summary>
    private ControlResult LowerPriority(IReadOnlyList<string> items, bool safe)
    {
        var processes = Processes();
        var guard = new ResourceGuard(processes, VisibleWindows.ForegroundProcessId());
        var chosen = Chosen(processes, items, () => BusyBackgroundPrograms(processes, guard.ForegroundPid));
        int lowered = 0;
        var problems = new List<string>();
        var skipped = new List<string>();
        foreach (var process in chosen)
        {
            if (Untouchable(process) || process.Protection != ControlProtection.None)
            {
                problems.Add($"{process.Name} is part of Windows or protected; its priority is left.");
                continue;
            }

            if (safe && guard.PriorityBlocked(process) is { } inUse)
            {
                skipped.Add($"{process.Name} ({process.Pid}): {inUse}");
                continue;
            }

            var result = SetPriority(process.Pid, process.StartTicks, "BelowNormal");
            if (result.Ok)
            {
                lowered++;
            }
            else
            {
                problems.Add($"{process.Name}: {result.Message}");
            }
        }

        return new ControlResult(lowered > 0, lowered > 0 ? $"Lowered {lowered} busy background program(s) to below-normal priority (until they end)." : skipped.Count > 0 ? $"Safe mode left {skipped[0]}." : problems.FirstOrDefault() ?? "No busy background program to lower.",
            Changed: lowered, Problems: problems.Count > 0 ? problems : null, Skipped: skipped.Count > 0 ? skipped : null);
    }

    /// <summary>Retrims solid-state drives (defrag /L): tells them which blocks are free, which keeps writes fast.</summary>
    private async Task<ControlResult> RetrimAsync(IReadOnlyList<string> items, bool safe, CancellationToken cancellationToken)
    {
        var drives = Drives();
        var chosen = items.Count == 0 ? drives.Where(d => d.Ssd).ToList() : drives.Where(d => items.Contains(d.Mount, StringComparer.OrdinalIgnoreCase)).ToList();
        int done = 0;
        var problems = new List<string>();
        var skipped = new List<string>();
        foreach (var drive in chosen)
        {
            if (!drive.Ssd)
            {
                problems.Add($"{drive.Mount} is a hard disk: Windows' Optimize Drives defragments it on its schedule.");
                continue;
            }

            if (safe && drive.InUse is { } inUse)
            {
                skipped.Add($"{drive.Mount}: {inUse}");
                continue;
            }

            var (ok, output) = await RunAsync(Path.Combine(System32, "defrag.exe"), [drive.Mount, "/L"], cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                done++;
                Audit($"You retrimmed {drive.Mount}", $"{drive.Mount} ({drive.Model}) retrimmed from PC Diagnostic.", EventCategory.Disk, "retrim");
            }
            else
            {
                problems.Add($"{drive.Mount}: {output}");
            }
        }

        return new ControlResult(done > 0, done > 0 ? $"Retrimmed {done} solid-state drive(s)." : skipped.Count > 0 ? $"Safe mode left {skipped[0]}." : problems.FirstOrDefault() ?? "No solid-state drive to retrim.",
            Changed: done, Problems: problems.Count > 0 ? problems : null, Skipped: skipped.Count > 0 ? skipped : null);
    }

    /// <summary>Restarts the graphics driver (pnputil /restart-device): memory a driver kept is given back; the screens go dark for a moment.</summary>
    private async Task<ControlResult> RestartGpuAsync(string? instanceId, bool confirmed, bool safe, CancellationToken cancellationToken)
    {
        if (!confirmed)
        {
            return new ControlResult(false, "Confirm first: the screens go dark for a moment, and programs drawing with the GPU may stop.");
        }

        if (safe && Guard().GpuRestartBlocked() is { } inUse)
        {
            return SafeLeft("The graphics driver", inUse);
        }

        var gpus = await GpusAsync(cancellationToken).ConfigureAwait(false);
        var chosen = string.IsNullOrWhiteSpace(instanceId) ? gpus : gpus.Where(g => string.Equals(g.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (chosen.Count == 0)
        {
            return new ControlResult(false, "There is no such graphics adapter.");
        }

        int done = 0;
        var problems = new List<string>();
        foreach (var gpu in chosen)
        {
            var (ok, output) = await RunAsync(Path.Combine(System32, "pnputil.exe"), ["/restart-device", gpu.InstanceId], cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                done++;
                Audit($"You restarted the {gpu.Name} driver", $"Graphics driver of {gpu.Name} restarted from PC Diagnostic.", EventCategory.Gpu, "restart-gpu");
            }
            else
            {
                problems.Add($"{gpu.Name}: {output}");
            }
        }

        return new ControlResult(done > 0, done > 0 ? $"Restarted the driver of {string.Join(", ", chosen.Select(g => g.Name))}." : problems.FirstOrDefault() ?? "The driver was not restarted.",
            Changed: done, Problems: problems.Count > 0 ? problems : null);
    }

    private (DateTimeOffset At, IReadOnlyList<OptimizeGpu> Gpus)? _gpus;

    /// <summary>The display adapters (pnputil, kept for 5 minutes).</summary>
    private async Task<IReadOnlyList<OptimizeGpu>> GpusAsync(CancellationToken cancellationToken)
    {
        if (_gpus is { } cached && time.GetUtcNow() - cached.At < TimeSpan.FromMinutes(5))
        {
            return cached.Gpus;
        }

        var (ok, output) = await RunAsync(Path.Combine(System32, "pnputil.exe"), ["/enum-devices", "/class", "Display", "/connected"], cancellationToken).ConfigureAwait(false);
        var gpus = new List<OptimizeGpu>();
        string? instance = null;
        foreach (var line in ok ? output.Split('\n') : [])
        {
            var at = line.IndexOf(':', StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            var (key, text) = (line[..at].Trim(), line[(at + 1)..].Trim());
            if (key.Equals("Instance ID", StringComparison.OrdinalIgnoreCase))
            {
                instance = text;
            }
            else if (key.Equals("Device Description", StringComparison.OrdinalIgnoreCase) && instance is not null)
            {
                gpus.Add(new OptimizeGpu(instance, text));
                instance = null;
            }
        }

        _gpus = (time.GetUtcNow(), gpus);
        return gpus;
    }

    /// <summary>Fixed drives with their media and their disk's active time now.</summary>
    private List<OptimizeDrive> Drives()
    {
        var disks = directory.Disks();
        var drives = new List<OptimizeDrive>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var mount = drive.Name.TrimEnd('\\');
            var disk = disks.FirstOrDefault(d => d.Volumes.Any(v => v.TrimEnd('\\').Equals(mount, StringComparison.OrdinalIgnoreCase)));
            var active = disk?.ActivePercent is { HasValue: true } a ? a.Value : null;
            drives.Add(new OptimizeDrive(mount, disk?.Disk.Model ?? "", disk?.Disk.Media == StorageMediaKind.Ssd || disk?.Disk.BusType == "NVMe", active,
                active is >= ResourceGuard.BusyDrivePercent ? $"it is busy ({active:0} % active)" : null));
        }

        return drives;
    }

    /// <inheritdoc />
    public CleanupListing CleanupFiles(string target, int root, string path) => Cleanup.Browse(target, root, path, time.GetUtcNow());

    /// <inheritdoc />
    public async Task<OptimizePlan> PlanAsync(CancellationToken cancellationToken)
    {
        var processes = Processes();
        var guard = new ResourceGuard(processes, VisibleWindows.ForegroundProcessId());
        OptimizeCandidate Candidate(ControlProcess p, string? inUse) =>
            new(p.Pid, p.StartTicks, p.Name, p.Kind, p.CpuPercent, p.WorkingSetBytes, p.GpuPercent, p.GpuMemoryBytes, p.NetBytesPerSec, p.DiskBytesPerSec, inUse,
                p.Protection == ControlProtection.Protected ? p.ProtectionReason : null);
        var gpus = await GpusAsync(cancellationToken).ConfigureAwait(false);
        var (standby, _) = MemoryLists.Query();
        return new OptimizePlan(
            time.GetUtcNow(),
            [.. VramHolders(processes).Take(20).Select(p => Candidate(p, guard.EndBlocked(p)))],
            [.. MemoryHolders(processes).Take(20).Select(p => Candidate(p, guard.TrimBlocked(p)))],
            [.. BusyBackgroundPrograms(processes, guard.ForegroundPid).Take(20).Select(p => Candidate(p, guard.PriorityBlocked(p)))],
            Drives(),
            gpus,
            standby,
            guard.StandbyBlocked(BusiestDrive()),
            guard.GpuRestartBlocked(),
            VirtualMachineMemory(),
            guard.WslBlocked(),
            guard.Find(guard.ForegroundPid)?.Name);
    }

    private static ControlResult? Refused(ControlProtection protection, string? reason, bool confirmed) => protection switch
    {
        ControlProtection.Protected => new ControlResult(false, $"Protected: {reason}"),
        ControlProtection.Caution when !confirmed => new ControlResult(false, $"Confirm first: {reason}"),
        _ => null,
    };

    private ServiceDetails? Details(string name, DateTimeOffset now)
    {
        if (_serviceDetails.TryGetValue(name, out var cached) && now - cached.At < TimeSpan.FromMinutes(10))
        {
            return cached.Details;
        }

        var details = ServiceControl.QueryDetails(name);
        _serviceDetails[name] = (details, now);
        return details;
    }

    private string? Company(string? path) => path is null ? null : _companies.GetOrAdd(path, StartupItems.CompanyOf);

    private static readonly Dictionary<string, (int Code, bool Delayed)> StartTypes = new(StringComparer.Ordinal)
    {
        ["Automatic"] = (2, false),
        ["AutomaticDelayed"] = (2, true),
        ["Manual"] = (3, false),
        ["Disabled"] = (4, false),
    };

    private static string StatusName(int state) => state switch
    {
        1 => "Stopped",
        2 => "StartPending",
        3 => "StopPending",
        4 => "Running",
        5 => "ContinuePending",
        6 => "PausePending",
        7 => "Paused",
        _ => "Unknown",
    };

    private static string StartTypeName(ServiceStartConfig config) => config.StartType switch
    {
        0 => "Boot",
        1 => "System",
        2 => config.DelayedAutoStart ? "AutomaticDelayed" : "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => "Unknown",
    };

    private static string Words(string value) => value switch
    {
        "BelowNormal" => "below normal",
        "AboveNormal" => "above normal",
        "AutomaticDelayed" => "automatically (delayed)",
        "Automatic" => "automatically",
        "Manual" => "only when needed (manual)",
        "Disabled" => "never (disabled)",
        _ => value.ToLowerInvariant(),
    };

    private static async Task<(bool Ok, string Output)> RunAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start);
        if (process is null)
        {
            return (false, "It did not start.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false) + await process.StandardError.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        return (process.ExitCode == 0, output.Trim().Replace('\0', ' '));
    }

    private void Audit(string title, string message, EventCategory category, string rule, int? pid = null, string? processName = null, string? service = null)
    {
        LogAction(logger, title);
        events.Publish(new SystemEvent(time.GetUtcNow(), Channel, null, "PcDiagnostic", 1, EventLevel.Information, category, Severity.Low, title, message,
            ProcessId: pid, ProcessName: processName, RuleId: $"pcdiag.control.{rule}", ServiceName: service));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Control: {Action}")]
    private static partial void LogAction(ILogger logger, string action);

    [LoggerMessage(Level = LogLevel.Information, Message = "Clean-up of {Targets}: {Removed} files, {Freed} bytes freed, {Left} in use left")]
    private static partial void LogCleaned(ILogger logger, string targets, int removed, long freed, int left);
}
