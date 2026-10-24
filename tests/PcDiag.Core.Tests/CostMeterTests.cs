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
}
