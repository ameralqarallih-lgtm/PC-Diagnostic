namespace PcDiag.Core.Recording;

/// <summary>When the recorder records telemetry.</summary>
public enum RecordingMode
{
    /// <summary>Only when the user starts it (UI, tray or hotkey).</summary>
    Manual = 0,

    /// <summary>Whenever the Agent is running.</summary>
    Always = 1,

    /// <summary>During the configured weekly time windows.</summary>
    Scheduled = 2,
}

/// <summary>Recorder state machine states.</summary>
public enum RecorderState
{
    /// <summary>Not recording; no collectors are running.</summary>
    Idle = 0,

    /// <summary>Starting collectors.</summary>
    Starting = 1,

    /// <summary>Recording.</summary>
    Recording = 2,

    /// <summary>Temporarily paused by the user.</summary>
    Paused = 3,

    /// <summary>Stopping collectors and flushing data.</summary>
    Stopping = 4,
}

/// <summary>Why recording started (stored in RecorderSessions.start_reason).</summary>
public enum RecordingStartReason
{
    /// <summary>The user started recording.</summary>
    User = 0,

    /// <summary>A scheduled window began.</summary>
    Schedule = 1,

    /// <summary>Recording mode is Always.</summary>
    Always = 2,

    /// <summary>An incident capture started recording temporarily.</summary>
    Capture = 3,

    /// <summary>Resumed after a pause or after the computer woke from sleep.</summary>
    Resume = 4,
}

/// <summary>Why recording ended (stored in RecorderSessions.end_reason).</summary>
public enum RecordingEndReason
{
    /// <summary>The user stopped recording.</summary>
    User = 0,

    /// <summary>A scheduled window ended.</summary>
    Schedule = 1,

    /// <summary>The user paused recording.</summary>
    Pause = 2,

    /// <summary>The Agent shut down cleanly.</summary>
    Shutdown = 3,

    /// <summary>The Agent stopped without shutting down (crash, power loss, BSOD); detected at next start.</summary>
    Crash = 4,

    /// <summary>The recording mode changed.</summary>
    ModeChanged = 5,

    /// <summary>The computer went to sleep or hibernation.</summary>
    Suspend = 6,

    /// <summary>Windows is ending the session (log off, restart or shut down).</summary>
    SessionEnd = 7,

    /// <summary>The temporary recording started for a capture ended with the incident's post-window.</summary>
    CaptureComplete = 8,

    /// <summary>No longer used (licensing was removed); kept so recordings that ended this way still read.</summary>
    License = 9,
}

/// <summary>
/// A weekly recording window in local time, e.g. Mon–Fri 09:00–18:00.
/// When <see cref="End"/> is earlier than or equal to <see cref="Start"/>, the window runs past midnight
/// (e.g. 22:00–02:00 on Monday covers Monday 22:00 to Tuesday 02:00).
/// </summary>
public sealed class RecordingWindow
{
    /// <summary>Days the window starts on.</summary>
    public IList<DayOfWeek> Days { get; init; } = [];

    /// <summary>Local start time.</summary>
    public TimeOnly Start { get; init; }

    /// <summary>Local end time. Equal to <see cref="Start"/> means a full 24 hours.</summary>
    public TimeOnly End { get; init; }

    /// <summary>What the window records; null records everything.</summary>
    public RecordingSelection? Selection { get; init; }

    /// <summary>True when the window wraps past midnight.</summary>
    public bool CrossesMidnight => End <= Start;

    /// <summary>Whether <paramref name="localTime"/> falls inside this window.</summary>
    public bool Contains(DateTime localTime)
    {
        var time = TimeOnly.FromDateTime(localTime);
        var today = localTime.DayOfWeek;

        if (!CrossesMidnight)
        {
            return Days.Contains(today) && time >= Start && time < End;
        }

        // Overnight: either the part after Start on a scheduled day,
        // or the part before End on the day after a scheduled day.
        var yesterday = (DayOfWeek)(((int)today + 6) % 7);
        return (Days.Contains(today) && time >= Start) || (Days.Contains(yesterday) && time < End);
    }
}

/// <summary>Evaluates a set of <see cref="RecordingWindow"/>s.</summary>
public static class RecordingSchedule
{
    /// <summary>Whether any window contains <paramref name="localTime"/>.</summary>
    public static bool IsActive(IEnumerable<RecordingWindow> windows, DateTime localTime)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.Any(w => w.Contains(localTime));
    }
}
