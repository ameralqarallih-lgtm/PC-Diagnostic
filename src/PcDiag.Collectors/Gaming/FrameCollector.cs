using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Core.Overlay;
using PcDiag.Core.Telemetry;
using PcDiag.Platform.Windows.Shell;

namespace PcDiag.Collectors.Gaming;

/// <summary>
/// The game's frame rate each second: while the app in front is a game (<see cref="GameRecognizer"/>: full screen,
/// borderless or windowed), the frame monitor counts its presents and PresentMon the frames that reached the screen;
/// this reads the last second of both (frame rate, longest frame, stutters; frames shown and dropped, how long an image
/// stayed, display latency, the GPU's and CPU's share of each frame, how Windows shows them) and the game's usual frame
/// rate. Apps drawing fewer than <see cref="MinGameFps"/> frames a second (a maximized editor redrawing its cursor) are
/// left out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FrameCollector(IEnumerable<IFrameMonitor> monitors, IOptionsMonitor<RecorderOptions> options, GameRecognizer? recognizer = null,
    IEnumerable<IDisplayedFrames>? displayed = null) : IPolledCollector<FrameSample?>
{
    /// <summary>A game draws at least this many frames a second (usually; a drop below it is still recorded).</summary>
    public const double MinGameFps = 15;

    /// <summary>Apps that cover the screen and are not games, by process name.</summary>
    internal static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave", "opera", "opera_gx", "vivaldi", "iexplore", "msedgewebview2",
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "PotPlayerMini", "PotPlayerMini64", "wmplayer", "Microsoft.Media.Player", "Video.UI", "mpv",
        "ApplicationFrameHost", "explorer", "LockApp", "LogonUI", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
        "ScreenClippingHost", "SnippingTool", "dwm", "mstsc", "msrdc", "vmconnect", "POWERPNT", "Teams", "ms-teams", "Zoom",
        "PcDiag.Agent", "PcDiag.Web",
    };

    private readonly IFrameMonitor? _frames = monitors.FirstOrDefault();
    private readonly IDisplayedFrames? _displayed = displayed?.FirstOrDefault();
    private readonly Dictionary<int, string?> _names = [];
    private readonly UsualFrameRate _usual = new();

    /// <inheritdoc />
    public string Id => "frames";

    /// <inheritdoc />
    public SamplingTier Tier => SamplingTier.System;

    /// <inheritdoc />
    public CollectorInfo Describe() => new(Id, "Game frame rate", RequiredPrivilege.Elevated, CostClass.Cheap);

    /// <inheritdoc />
    public ValueTask<CollectorStatus> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_frames is null
        ? new CollectorStatus(Availability.Unsupported, "no frame monitor in this process")
        : new CollectorStatus(Availability.Available, _frames.Problem));

    /// <inheritdoc />
    public ValueTask<FrameSample?> CollectAsync(SampleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_frames is null || !options.CurrentValue.FrameRates || ForegroundWindow.Current() is not { } front)
        {
            return ValueTask.FromResult<FrameSample?>(null);
        }

        var name = NameOf(front.ProcessId);
        var recognized = recognizer is not null ? recognizer.Recognize(front.ProcessId, name, front.FullScreen)
            : front.FullScreen && name is not null && !NotGames.Contains(name) ? "full screen" : null;
        if (recognized is null)
        {
            return ValueTask.FromResult<FrameSample?>(null);
        }

        // Keeps both counters running while a game is in front (they stop a minute after the last call).
        _frames.Want();
        _displayed?.Want(front.ProcessId);
        return ValueTask.FromResult(Sample(_frames.Current(front.ProcessId), context.Timestamp, recognized, front.FullScreen, _displayed?.Current(front.ProcessId)));
    }

    /// <summary>The sample for the counters' readings, or null when it is no game's (exposed for tests).</summary>
    internal FrameSample? Sample(FrameStats? stats, DateTimeOffset at, string recognized = "full screen", bool fullScreen = true, DisplayedStats? shown = null)
    {
        if (stats is not { Foreground: true } || stats.ProcessName is null || (recognized != "user" && NotGames.Contains(stats.ProcessName)))
        {
            return null;
        }

        var normal = _usual.Add(stats.ProcessId, at, stats.Fps);
        if ((normal ?? stats.Fps) < MinGameFps)
        {
            return null;
        }

        return new FrameSample(at, stats.ProcessId, stats.ProcessName, Math.Round(stats.Fps, 1), Math.Round(stats.FrameTimeMs, 2), Math.Round(stats.WorstFrameTimeMs, 1),
            stats.Stutters, stats.Low1Fps is { } low ? Math.Round(low, 1) : null, normal is { } n ? Math.Round(n, 1) : null, fullScreen,
            shown?.DisplayedFps, shown?.Dropped, shown?.WorstDisplayedMs is { } worst ? Math.Round(worst, 1) : null, shown?.DisplayLatencyMs, shown?.GpuBusyPercent,
            shown?.CpuBusyPercent, shown?.PresentMode, recognized);
    }

    /// <summary>A process's name, looked up once per process.</summary>
    private string? NameOf(int processId)
    {
        if (_names.TryGetValue(processId, out var known))
        {
            return known;
        }

        string? name = null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // It just ended.
        }

        if (_names.Count > 512)
        {
            _names.Clear();
        }

        _names[processId] = name;
        return name;
    }
}
