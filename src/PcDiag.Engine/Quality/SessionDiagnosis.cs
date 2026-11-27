using System.Globalization;
using PcDiag.Core.Quality;

namespace PcDiag.Engine.Quality;

/// <summary>
/// What a whole game session shows (from its summary, kept also while the recorder only watches): how Windows put its
/// frames on the screen (composed by the desktop when windowed, or flipped straight to the screen), which side was its
/// limit most of the time (PresentMon's per-frame GPU and CPU shares), frames drawn and never shown, and display latency.
/// </summary>
public static class SessionDiagnosis
{
    /// <summary>The GPU working on each frame this share (%) of its time, on average, is its limit.</summary>
    public const double GpuLimit = 90;

    /// <summary>Display latency (ms) worth a finding.</summary>
    public const double SlowDisplayMs = 50;

    /// <summary>The findings for a session, most serious first.</summary>
    public static List<QualityFinding> Analyze(GameSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var findings = new List<QualityFinding>();
        if (session.PresentMode is { } mode)
        {
            findings.Add(mode.StartsWith("Composed", StringComparison.Ordinal)
                ? new QualityFinding("display", 1,
                    $"Its frames went through Windows' desktop compositor ({mode}) most of the time, as a game in a window does: they wait for the desktop's next refresh, which adds latency and can drop frames; full screen, or \"Optimizations for windowed games\" in Windows' graphics settings, could send them straight to the screen.")
                : new QualityFinding("display", 0, $"Its frames went straight to the screen ({mode})."));
        }

        if (session.GpuBusyPercent is >= GpuLimit)
        {
            findings.Add(new QualityFinding("gpu", 1,
                $"The GPU worked on each frame {N(session.GpuBusyPercent.Value)} % of its time on average: the graphics card was the limit most of the session; lower graphics settings or resolution would raise the frame rate."));
        }
        else if (session.CpuBusyPercent is >= 85 && session.GpuBusyPercent is null or <= 70)
        {
            findings.Add(new QualityFinding("cpu", 1,
                $"The game's CPU work took {N(session.CpuBusyPercent.Value)} % of each frame's time on average while the GPU worked on it {N(session.GpuBusyPercent ?? 0)} %: the CPU side was the limit most of the session."));
        }
        else if (session.GpuBusyPercent is { } gpu)
        {
            findings.Add(new QualityFinding("gpu", 0,
                $"The GPU worked on each frame {N(gpu)} % of its time on average: neither the graphics card nor the game's CPU work was at its limit most of the time (a frame limit or sync may hold the frame rate)."));
        }

        if (session.DisplayedFps is { } shown && session.Dropped is { } dropped && session.Seconds > 0)
        {
            var droppedPerSecond = (double)dropped / session.Seconds;
            if (droppedPerSecond >= Math.Max(5, 0.2 * (shown + droppedPerSecond)))
            {
                findings.Add(new QualityFinding("display", 0,
                    $"About {N(droppedPerSecond)} frames a second never reached the screen ({N(shown)} a second were shown): the game drew more than the screen showed; a frame limit at the screen's refresh rate would save power and heat without losing a shown frame."));
            }
        }

        if (session.DisplayLatencyMs is >= SlowDisplayMs)
        {
            findings.Add(new QualityFinding("display", 1,
                $"Frames took {N(session.DisplayLatencyMs.Value)} ms on average from their start to the screen: the game may feel slow to respond; a frame limit a little below the refresh rate, or the GPU maker's low-latency mode, could shorten it."));
        }

        return findings.OrderByDescending(f => f.Severity).ToList();
    }

    private static string N(double value) => value.ToString(Math.Abs(value) < 10 && value != Math.Round(value) ? "0.#" : "0", CultureInfo.InvariantCulture);
}
