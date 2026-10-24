using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;

namespace PcDiag.Engine.Telemetry;

/// <summary>
/// Maps sampling tiers onto the base tick. Every tier runs on a whole multiple of the base (system) interval,
/// so all collectors due in a tick share one timestamp and one frame.
/// </summary>
public sealed class TierPlan
{
    /// <summary>Largest share of the base interval a collector may take before it is abandoned for this tick.</summary>
    public const double MaxShareOfBaseInterval = 0.8;

    private readonly Dictionary<SamplingTier, int> _divisors;

    /// <summary>Builds the plan from sampling options.</summary>
    public TierPlan(SamplingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        BaseInterval = TimeSpan.FromMilliseconds(options.SystemIntervalMs);
        _divisors = new Dictionary<SamplingTier, int>
        {
            [SamplingTier.System] = 1,
            [SamplingTier.Sensors] = DivisorFor(options.SensorIntervalMs),
            [SamplingTier.Process] = DivisorFor(options.ProcessIntervalMs),
            [SamplingTier.Inventory] = DivisorFor(options.InventoryIntervalSeconds * 1000.0),
        };
        CollectorTimeout = TimeSpan.FromMilliseconds(options.SystemIntervalMs * MaxShareOfBaseInterval);
    }

    /// <summary>The base tick interval (the system tier).</summary>
    public TimeSpan BaseInterval { get; }

    /// <summary>How long a collector may run within a tick.</summary>
    public TimeSpan CollectorTimeout { get; }

    /// <summary>Number of base ticks between runs of a tier.</summary>
    public int Divisor(SamplingTier tier) => _divisors[tier];

    /// <summary>Actual interval of a tier after rounding to whole base ticks.</summary>
    public TimeSpan Interval(SamplingTier tier) => BaseInterval * Divisor(tier);

    /// <summary>Whether a tier is due on tick <paramref name="sequence"/> (1-based). Every tier runs on the first tick.</summary>
    public bool IsDue(SamplingTier tier, long sequence) => (sequence - 1) % Divisor(tier) == 0;

    private int DivisorFor(double intervalMs) =>
        Math.Max(1, (int)Math.Round(intervalMs / BaseInterval.TotalMilliseconds, MidpointRounding.AwayFromZero));
}
