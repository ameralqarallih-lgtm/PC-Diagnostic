using PcDiag.Core.Diagnostics;
using PcDiag.Core.Overlay;

namespace PcDiag.Agent.Overlay;

/// <summary>
/// Gliding values: at each refresh a number that changed is not shown at its new value at once but counted there,
/// with its bar, ring or dial sweeping along, over a little under half a second (easing out), drawn 30 times a second
/// (a USB screen at its own rate). Only numbers glide; text, limits and units change at once. Values jump instead when
/// smooth values are off, the panels are being arranged or dragged, they refresh faster than every half second, or
/// panels keep still (battery saver, Remote Desktop…, see <see cref="Lively"/>).
/// </summary>
internal sealed partial class OverlayHost
{
    private static readonly double GlideSeconds = 0.45;

    private IReadOnlyDictionary<string, OverlaySource>? _glideFrom;
    private IReadOnlyDictionary<string, OverlaySource>? _glideTo;
    private long _glideStart;
    private System.Windows.Forms.Timer? _glideTimer;

    /// <summary>
    /// The values to draw at a refresh that brought <paramref name="target"/>: those shown now (with the new text,
    /// limits and units), gliding to the new ones from here; or the new ones, when they do not glide.
    /// </summary>
    private Dictionary<string, OverlaySource> StartGlide(Dictionary<string, OverlaySource> target)
    {
        var from = _lastSources;
        if (!_config.SmoothValues || _config.IntervalMs < 500 || from.Count == 0 || !Lively() || !target.Any(t => Glides(from.GetValueOrDefault(t.Key), t.Value)))
        {
            StopGlide();
            return target;
        }

        _glideFrom = from;
        _glideTo = target;
        _glideStart = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_glideTimer is null)
        {
            _glideTimer = new System.Windows.Forms.Timer { Interval = 33 };
            _glideTimer.Tick += (_, _) => GlideTick();
            _glideTimer.Start();
        }

        return Between(from, target, 0);
    }

    private void StopGlide()
    {
        _glideTimer?.Dispose();
        _glideTimer = null;
        _glideFrom = null;
        _glideTo = null;
    }

    /// <summary>A step of the glide: the panels drawn at the values on the way (the last step at the new ones).</summary>
    private void GlideTick()
    {
        if (_glideFrom is not { } from || _glideTo is not { } to)
        {
            StopGlide();
            return;
        }

        using var cost = CostMeter.Measure("overlay.glide");
        var t = System.Diagnostics.Stopwatch.GetElapsedTime(_glideStart).TotalSeconds / GlideSeconds;
        var done = t >= 1 || _arranging || _drag is not null;
        _lastSources = done ? new Dictionary<string, OverlaySource>(to, StringComparer.Ordinal) : Between(from, to, Ease(t));
        try
        {
            var now = DateTime.UtcNow;
            // The last step reaches every screen; the others, the USB screens whose next picture is due.
            DrawDevices(now, glide: !done);
            foreach (var key in _windows.Keys.ToList())
            {
                Draw(key, now);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            LogDrawFailed(_logger, ex);
            done = true;
        }

        if (done)
        {
            StopGlide();
        }
    }

    /// <summary>Eases out (fast first, settling into the value): 1 - (1 - t)³.</summary>
    internal static double Ease(double t)
    {
        var rest = 1 - Math.Clamp(t, 0, 1);
        return 1 - (rest * rest * rest);
    }

    /// <summary>Whether a value glides from what was shown: both numbers (finite), in the same unit, and different.</summary>
    internal static bool Glides(OverlaySource? from, OverlaySource to) =>
        from is { Value: { } a, Text: null, Loading: false } && to is { Value: { } b, Text: null, Loading: false }
        && double.IsFinite(a) && double.IsFinite(b) && a != b && from.Unit == to.Unit;

    /// <summary>The new values <paramref name="t"/> of the way from those shown (0: as shown, 1: the new ones).</summary>
    internal static Dictionary<string, OverlaySource> Between(IReadOnlyDictionary<string, OverlaySource> from, IReadOnlyDictionary<string, OverlaySource> to, double t)
    {
        var between = new Dictionary<string, OverlaySource>(to.Count, StringComparer.Ordinal);
        foreach (var (id, target) in to)
        {
            between[id] = Glides(from.GetValueOrDefault(id), target)
                ? target with { Value = from[id].Value!.Value + ((target.Value!.Value - from[id].Value!.Value) * t) }
                : target;
        }

        return between;
    }
}
