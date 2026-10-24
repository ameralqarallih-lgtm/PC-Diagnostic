using PcDiag.Core.Telemetry;

namespace PcDiag.Core.Tests;

public class ReadingTests
{
    [Fact]
    public void Of_creates_available_reading()
    {
        var reading = Reading<double>.Of(42.5);

        reading.HasValue.ShouldBeTrue();
        reading.Availability.ShouldBe(Availability.Available);
        reading.GetValueOrDefault(0).ShouldBe(42.5);
    }

    [Theory]
    [InlineData(Availability.Unsupported)]
    [InlineData(Availability.RequiresElevation)]
    [InlineData(Availability.NotPresent)]
    [InlineData(Availability.Error)]
    [InlineData(Availability.Disabled)]
    public void Unavailable_keeps_reason_and_has_no_value(Availability reason)
    {
        var reading = Reading<double>.Unavailable(reason);

        reading.HasValue.ShouldBeFalse();
        reading.Availability.ShouldBe(reason);
        reading.GetValueOrDefault(-1).ShouldBe(-1);
        reading.ToString().ShouldBe(reason.ToString());
    }

    [Fact]
    public void Unavailable_rejects_available_as_reason()
    {
        Should.Throw<ArgumentException>(() => Reading<int>.Unavailable(Availability.Available));
    }

    [Fact]
    public void Map_transforms_available_values_and_preserves_unavailable_reasons()
    {
        Reading<long>.Of(2048).Map(b => b / 1024.0).ShouldBe(Reading<double>.Of(2.0));
        Reading<long>.Unavailable(Availability.RequiresElevation).Map(b => b / 1024.0).Availability.ShouldBe(Availability.RequiresElevation);
    }

    [Fact]
    public void Default_reading_is_not_treated_as_a_value()
    {
        // default(Reading<T>) has Availability.Available but no value; it must not look like a real zero.
        default(Reading<double>).HasValue.ShouldBeFalse();
    }
}
