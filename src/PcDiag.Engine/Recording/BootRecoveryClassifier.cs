using System.Globalization;
using PcDiag.Core.Events;
using PcDiag.Core.Recording;

namespace PcDiag.Engine.Recording;

/// <summary>
/// Decides what happened to the previous Agent run when it did not stop cleanly, from its last heartbeat, the
/// boot time and the classified Windows events logged since (Phase 7 rules): bug check, unexpected shutdown,
/// a normal Windows restart, or the recorder alone stopping. Wording is observational: Kernel-Power 41 says Windows
/// did not shut down cleanly, not why.
/// </summary>
public static class BootRecoveryClassifier
{
    /// <summary>Events this long before the last heartbeat still count (clock and write-behind slack).</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);

    /// <summary>The shipped rule for Kernel-General 12 ("The operating system started").</summary>
    public const string OsStartedRule = "power.os-started";

    /// <summary>Key in <see cref="BootRecovery.Details"/> set when the recorder had stopped cleanly before Windows went down.</summary>
    public const string StoppedFirstKey = "recorderStoppedCleanlyAt";

    /// <summary>
    /// The finding, or null when there is nothing to report: no previous run, or a clean stop with no crash after it. A
    /// clean stop followed by a crash is reported too (the recorder was not running when Windows went down, as on the dev PC
    /// on 7 Nov 2026), with <see cref="StoppedFirstKey"/> in the details.
    /// </summary>
    public static BootRecovery? Classify(AgentHeartbeatState? previous, DateTimeOffset bootTime, IReadOnlyList<SystemEvent> evidence, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (previous is null)
        {
            return null;
        }

        if (previous.CleanShutdown)
        {
            return CrashWhileStopped(previous.LastBeat, bootTime, evidence, now);
        }

        var lastBeat = previous.LastBeat;
        var details = new Dictionary<string, string>
        {
            ["lastHeartbeat"] = lastBeat.ToString("O", CultureInfo.InvariantCulture),
            ["bootTime"] = bootTime.ToString("O", CultureInfo.InvariantCulture),
        };

        var relevant = evidence.Where(e => e.Timestamp >= lastBeat - Slack).OrderBy(e => e.Timestamp).ToList();
        if (relevant.Count > 0)
        {
            details["events"] = string.Join("; ", relevant.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Provider} {e.EventId} at {e.Timestamp:O}")));
        }

        var when = lastBeat.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        // With Fast Startup a shutdown hibernates the kernel and the uptime keeps counting, so the boot time from
        // uptime can predate the heartbeat after a real restart. "Windows started" or crash events after the
        // heartbeat show the restart.
        var started = relevant.Where(e => e.RuleId == OsStartedRule && e.Timestamp > lastBeat).Select(e => (DateTimeOffset?)e.Timestamp).Max();
        if (started is { } osStart && osStart > bootTime)
        {
            bootTime = osStart;
            details["bootTime"] = bootTime.ToString("O", CultureInfo.InvariantCulture);
        }

        var restarted = bootTime > lastBeat || relevant.Any(e => e.Timestamp > lastBeat && e.Category is EventCategory.UnexpectedShutdown or EventCategory.BugCheck);

        // Windows has not restarted since the last heartbeat: only the recorder stopped.
        if (!restarted)
        {
            return new BootRecovery(now, lastBeat, bootTime, UncleanStopKind.RecorderStopped, previous.Recording,
                $"The recorder stopped unexpectedly after {when}; Windows kept running.", details);
        }

        var bugCheck = relevant.FirstOrDefault(e => e.Category == EventCategory.BugCheck)
            ?? relevant.FirstOrDefault(e => e.Category == EventCategory.UnexpectedShutdown && e.Data?.ContainsKey("bugcheck") == true);
        if (bugCheck is not null)
        {
            var code = bugCheck.Data?.GetValueOrDefault("bugcheck") ?? "unknown";
            details["bugcheck"] = code;
            if (relevant.Select(e => e.Data?.GetValueOrDefault("dumpFile")).FirstOrDefault(f => f is not null) is { } dump)
            {
                details["dumpFile"] = dump;
            }

            return new BootRecovery(now, lastBeat, bootTime, UncleanStopKind.BugCheck, previous.Recording,
                $"Windows restarted after a bug check ({code}). The recorder's last heartbeat was at {when}.", details);
        }

        if (relevant.Any(e => e.Category == EventCategory.UnexpectedShutdown))
        {
            return new BootRecovery(now, lastBeat, bootTime, UncleanStopKind.UnexpectedShutdown, previous.Recording,
                $"Windows restarted without a clean shutdown (last heartbeat {when}). Possible explanations include a hang, a crash without a dump, or a power loss.",
                details);
        }

        return new BootRecovery(now, lastBeat, bootTime, UncleanStopKind.WindowsRestarted, previous.Recording,
            $"Windows restarted after {when}, but the recorder was not stopped cleanly first. No unexpected-shutdown event was found.", details);
    }

    /// <summary>A crash Windows logged after the recorder had stopped cleanly; null when there was none.</summary>
    private static BootRecovery? CrashWhileStopped(DateTimeOffset stoppedAt, DateTimeOffset bootTime, IReadOnlyList<SystemEvent> evidence, DateTimeOffset now)
    {
        var crash = evidence.Where(e => e.Timestamp > stoppedAt && e.Category is EventCategory.UnexpectedShutdown or EventCategory.BugCheck).OrderBy(e => e.Timestamp).ToList();
        if (crash.Count == 0)
        {
            return null;
        }

        var stopped = stoppedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        if (bootTime <= stoppedAt)
        {
            bootTime = crash[0].Timestamp;
        }

        var details = new Dictionary<string, string>
        {
            ["lastHeartbeat"] = stoppedAt.ToString("O", CultureInfo.InvariantCulture),
            ["bootTime"] = bootTime.ToString("O", CultureInfo.InvariantCulture),
            [StoppedFirstKey] = stoppedAt.ToString("O", CultureInfo.InvariantCulture),
            ["events"] = string.Join("; ", crash.Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Provider} {e.EventId} at {e.Timestamp:O}"))),
        };
        var bugCheck = crash.FirstOrDefault(e => e.Category == EventCategory.BugCheck) ?? crash.FirstOrDefault(e => e.Data?.ContainsKey("bugcheck") == true);
        if (bugCheck is not null)
        {
            var code = bugCheck.Data?.GetValueOrDefault("bugcheck") ?? "unknown";
            details["bugcheck"] = code;
            if (crash.Select(e => e.Data?.GetValueOrDefault("dumpFile")).FirstOrDefault(f => f is not null) is { } dump)
            {
                details["dumpFile"] = dump;
            }

            return new BootRecovery(now, stoppedAt, bootTime, UncleanStopKind.BugCheck, false,
                $"Windows restarted after a bug check ({code}) while the recorder was not running (it had stopped cleanly at {stopped}).", details);
        }

        return new BootRecovery(now, stoppedAt, bootTime, UncleanStopKind.UnexpectedShutdown, false,
            $"Windows restarted without a clean shutdown while the recorder was not running (it had stopped cleanly at {stopped}). Possible explanations include a hang, a crash without a dump, or a power loss.",
            details);
    }

    /// <summary>A stable identifier for the current boot: the boot time to the minute.</summary>
    public static string BootId(DateTimeOffset bootTime) =>
        bootTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);
}
