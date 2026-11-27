using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Quality;

/// <summary>A game session: a game in front with gaps of less than a minute.</summary>
/// <param name="Id">Its row.</param>
/// <param name="Game">The game's process name.</param>
/// <param name="Start">Its first measured second.</param>
/// <param name="End">Its last measured second.</param>
/// <param name="Seconds">Measured seconds.</param>
/// <param name="FpsAvg">Its average frame rate.</param>
/// <param name="FpsP1">The frame rate of its slowest 1 % of seconds.</param>
/// <param name="Low1Fps">The median of its 1 % lows (the slowest 1 % of frames over 30 s).</param>
/// <param name="Stutters">Its stutters.</param>
/// <param name="DropSeconds">Its seconds 40 % or more below its usual frame rate.</param>
/// <param name="WorstMs">Its longest frame.</param>
/// <param name="DisplayedFps">Frames that reached the screen a second, on average (null without PresentMon).</param>
/// <param name="Dropped">Frames presented and never shown.</param>
/// <param name="DisplayLatencyMs">From a frame's start until it was shown, on average.</param>
/// <param name="GpuBusyPercent">The GPU's work as a share of each frame's time, on average.</param>
/// <param name="CpuBusyPercent">The CPU's work before presenting as a share of each frame's time, on average.</param>
/// <param name="PresentMode">How Windows showed its frames most of the time.</param>
/// <param name="WindowedPercent">Share of its seconds in a window (not covering its screen).</param>
/// <param name="Recognized">Why it counted as a game.</param>
public sealed record GameSession(long Id, string Game, DateTimeOffset Start, DateTimeOffset End, int Seconds, double? FpsAvg, double? FpsP1, double? Low1Fps, int Stutters,
    int DropSeconds, double? WorstMs, double? DisplayedFps = null, int? Dropped = null, double? DisplayLatencyMs = null, double? GpuBusyPercent = null,
    double? CpuBusyPercent = null, string? PresentMode = null, double? WindowedPercent = null, string? Recognized = null);

/// <summary>One hop's pings in a minute.</summary>
/// <param name="Ms">The average round trip.</param>
/// <param name="Max">The longest.</param>
/// <param name="Loss">The share of pings lost (%); null when none was sent.</param>
public sealed record HopMinute(double? Ms, double? Max, double? Loss);

/// <summary>A minute of the quality history.</summary>
public sealed record QualityMinute(
    DateTimeOffset At,
    int Seconds,
    string? Game,
    int GameSeconds,
    double? FpsAvg,
    double? FpsMin,
    double? Low1Fps,
    int Stutters,
    double? WorstMs,
    int DropSeconds,
    double? DpcCoreMax,
    double? IsrCoreMax,
    double? DpcAvg,
    HopMinute Router,
    HopMinute Provider,
    HopMinute Internet,
    double? InternetJitter,
    double? DnsMs,
    int DnsChecks,
    int DnsFailed,
    double? WifiMin,
    double? NetBps,
    double? DisplayedFps = null,
    int? Dropped = null,
    double? DisplayLatencyMs = null,
    double? GpuBusyPercent = null,
    double? CpuBusyPercent = null,
    string? PresentMode = null);

/// <summary>One second of a game's frames (recorded data).</summary>
public sealed record FrameSecond(DateTimeOffset At, double Fps, double? WorstMs, int Stutters, double? UsualFps, double? DisplayedFps = null, int? Dropped = null,
    double? WorstDisplayedMs = null, double? GpuBusyPercent = null, double? CpuBusyPercent = null, string? PresentMode = null);

/// <summary>A program's average activity before a moment and during it (machine-wide shares and rates).</summary>
/// <param name="Name">Its process name.</param>
/// <param name="CpuBefore">CPU (% of the machine) before.</param>
/// <param name="CpuDuring">CPU during.</param>
/// <param name="GpuBefore">GPU (%) before.</param>
/// <param name="GpuDuring">GPU during.</param>
/// <param name="DiskBefore">Disk reads and writes (bytes/s) before.</param>
/// <param name="DiskDuring">Disk during.</param>
/// <param name="NetBefore">Network (bytes/s) before.</param>
/// <param name="NetDuring">Network during.</param>
public sealed record ProcessActivity(string Name, double CpuBefore, double CpuDuring, double GpuBefore, double GpuDuring, double DiskBefore, double DiskDuring, double NetBefore,
    double NetDuring);

/// <summary>A program's network use over a period.</summary>
/// <param name="Name">Its process name.</param>
/// <param name="ReceiveBps">Average download (bytes/s) over the seconds it was stored.</param>
/// <param name="SendBps">Average upload.</param>
/// <param name="PeakBps">Its busiest second (download and upload).</param>
/// <param name="RetransmitsPerSec">TCP segments it sent again per second, average.</param>
public sealed record NetworkProgram(string Name, double ReceiveBps, double SendBps, double PeakBps, double RetransmitsPerSec);

/// <summary>A driver latency measurement as stored.</summary>
public sealed record DriverLatencyMeasurement(DateTimeOffset At, DateTimeOffset From, double Seconds, string Reason, string? Problem, IReadOnlyList<DriverLatency> Drivers);

/// <summary>One finding of the quality pages (the frame drops, the connection, driver latency).</summary>
/// <param name="Kind">What it is about, e.g. "gpu", "cpu", "thermal", "memory", "storage", "driver", "program", "home-network", "provider", "internet", "dns", "bufferbloat", "game".</param>
/// <param name="Severity">0 (information) to 3 (serious).</param>
/// <param name="Text">What was measured and what it may mean (worded like the incident findings: no certainty that is not there).</param>
public sealed record QualityFinding(string Kind, int Severity, string Text);

/// <summary>A frame drop or hitch in a game session.</summary>
/// <param name="From">Its first second.</param>
/// <param name="To">Its last second.</param>
/// <param name="LowestFps">The lowest frame rate in it.</param>
/// <param name="UsualFps">The game's usual frame rate then.</param>
/// <param name="WorstMs">The longest frame in it.</param>
/// <param name="Stutters">Stutters in it.</param>
/// <param name="Findings">What else was measured during it.</param>
public sealed record FrameDrop(DateTimeOffset From, DateTimeOffset To, double LowestFps, double? UsualFps, double? WorstMs, int Stutters, IReadOnlyList<QualityFinding> Findings);

/// <summary>The recorder's driver latency measurements now.</summary>
/// <param name="Measuring">A measurement is running.</param>
/// <param name="Problem">Why it cannot measure, when it cannot.</param>
/// <param name="Last">The newest result since the recorder started.</param>
public sealed record DriverLatencyStatus(bool Measuring, string? Problem, DriverLatencySample? Last);
