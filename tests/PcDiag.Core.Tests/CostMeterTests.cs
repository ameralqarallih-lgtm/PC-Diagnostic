using PcDiag.Core.Diagnostics;
using Shouldly;
using Xunit;

namespace PcDiag.Core.Tests;

public class CostMeterTests
{
    [Fact]
    public void Work_measured_is_counted_by_part_and_handed_over_once()
    {
        _ = CostMeter.Take();
        using (CostMeter.Measure("test.busy"))
        {
            var x = 0.0;
            for (var i = 0; i < 2_000_000; i++)
            {
                x += Math.Sqrt(i);
            }

            x.ShouldBeGreaterThan(0);
        }

        var taken = CostMeter.Take();
        if (OperatingSystem.IsWindows())
        {
            taken["test.busy"].ShouldBeGreaterThan(0);
            CostMeter.ProcessCycles().ShouldBeGreaterThan(taken["test.busy"]);
        }

        CostMeter.Take().ContainsKey("test.busy").ShouldBeFalse();
    }

    [Fact]
    public void A_part_measured_inside_another_is_counted_once()
    {
        static double Spin(int n)
        {
            var x = 0.0;
            for (var i = 0; i < n; i++)
            {
                x += Math.Sqrt(i);
            }

            return x;
        }

        long whole;
        _ = CostMeter.Take();
        var start = CostMeter.ProcessCycles();
        using (CostMeter.Measure("test.outer"))
        {
            Spin(200_000).ShouldBeGreaterThan(0);
            using (CostMeter.Measure("test.inner"))
            {
                Spin(4_000_000).ShouldBeGreaterThan(0);
            }
        }

        whole = CostMeter.ProcessCycles() - start;
        var taken = CostMeter.Take();
        if (OperatingSystem.IsWindows())
        {
            // The outer part counts only its own (small) work; together they are no more than the thread spent.
            taken["test.inner"].ShouldBeGreaterThan(taken["test.outer"] * 5);
            (taken["test.inner"] + taken["test.outer"]).ShouldBeLessThanOrEqualTo(whole);
        }
    }
}
