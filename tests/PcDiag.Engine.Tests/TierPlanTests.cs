using PcDiag.Core.Abstractions;
using PcDiag.Core.Configuration;
using PcDiag.Engine.Telemetry;

namespace PcDiag.Engine.Tests;

public class TierPlanTests
{
    [Fact]
    public void Default_tiers_map_to_whole_base_ticks()
    {
        var plan = new TierPlan(new SamplingOptions());

        plan.BaseInterval.ShouldBe(TimeSpan.FromSeconds(1));
        plan.Divisor(SamplingTier.System).ShouldBe(1);
        plan.Divisor(SamplingTier.Sensors).ShouldBe(1);
        plan.Divisor(SamplingTier.Process).ShouldBe(2);
        plan.Divisor(SamplingTier.Inventory).ShouldBe(10);
        plan.CollectorTimeout.ShouldBe(TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public void Every_tier_runs_on_the_first_tick_then_on_its_interval()
    {
        var plan = new TierPlan(new SamplingOptions());

        Enumerable.Range(1, 6).Where(t => plan.IsDue(SamplingTier.Process, t)).ShouldBe([1, 3, 5]);
        Enumerable.Range(1, 21).Where(t => plan.IsDue(SamplingTier.Inventory, t)).ShouldBe([1, 11, 21]);
    }

    [Fact]
    public void Intervals_are_rounded_to_the_base_tick()
    {
        var plan = new TierPlan(new SamplingOptions { SystemIntervalMs = 1000, ProcessIntervalMs = 1600, SensorIntervalMs = 250 });

        plan.Divisor(SamplingTier.Process).ShouldBe(2);
        plan.Divisor(SamplingTier.Sensors).ShouldBe(1); // never faster than the base tick
        plan.Interval(SamplingTier.Process).ShouldBe(TimeSpan.FromSeconds(2));
    }
}
