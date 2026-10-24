using System.ComponentModel;
using System.Diagnostics;

namespace PcDiag.Web.History;

/// <summary>The processes running on this PC now, to tell a recorded process that is still running from one that ended unseen.</summary>
internal static class RunningProcesses
{
    /// <summary>Start times further apart than this are different processes that reused the ID.</summary>
    private const long StartToleranceMs = 2000;

    /// <summary>
    /// Takes the list now and answers whether a process (ID, start time in Unix ms) is one of them. A start time this
    /// non-elevated host may not read (a protected or elevated process) is matched on the process ID alone.
    /// </summary>
    public static Func<int, long, bool> Snapshot()
    {
        var running = new Dictionary<int, long?>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                long? start = null;
                try
                {
                    start = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Not readable from here; the ID still counts.
                }

                running[process.Id] = start;
            }
        }

        return (pid, startMs) => running.TryGetValue(pid, out var start)
            && (start is not { } known || startMs <= 0 || Math.Abs(known - startMs) <= StartToleranceMs);
    }
}
