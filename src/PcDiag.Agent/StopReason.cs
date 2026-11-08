namespace PcDiag.Agent;

/// <summary>
/// Why the Agent is stopping, for its last log line. A clean stop used to say only "stopped cleanly", so a recorder closed from
/// the tray and one closed by another program looked the same in the log (the dev PC, Nov 2026). The first reason given wins.
/// </summary>
internal sealed class StopReason
{
    /// <summary>The installer's (or the uninstaller's) signal to stop: a named event in this session, set by them.</summary>
    public const string ExitEventName = @"Local\PcDiagnostic.Agent.Exit";

    private string? _why;

    /// <summary>Why it is stopping; without a reason given, Windows or another program closed it.</summary>
    public string Why => Volatile.Read(ref _why) ?? "closed by Windows or another program, which gave no reason";

    /// <summary>Records the reason, unless one was given already.</summary>
    public void Set(string why) => Interlocked.CompareExchange(ref _why, why, null);
}
