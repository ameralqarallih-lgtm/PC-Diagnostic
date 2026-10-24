using PcDiag.Core.Incidents;

namespace PcDiag.Core.Tests;

public class IncidentKeyTests
{
    [Fact]
    public void Formats_as_documented()
    {
        var key = IncidentKey.Create(new DateOnly(2026, 9, 28), 27);

        key.ToString().ShouldBe("INC-20260928-00027");
        key.DisplayNumber.ShouldBe("#00027");
    }

    [Fact]
    public void Round_trips_through_parse()
    {
        var key = IncidentKey.Create(new DateOnly(2027, 1, 5), 99_999);

        IncidentKey.Parse(key.ToString(), null).ShouldBe(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("INC-20260928-0027")]    // too short
    [InlineData("INC-20260928-000027")]  // too long
    [InlineData("inc-20260928-00027")]   // wrong case
    [InlineData("INC-20261328-00027")]   // invalid month
    [InlineData("INC-20260928-00000")]   // sequence starts at 1
    [InlineData("INC-20260928-0002x")]
    [InlineData("INC-20260928_00027")]
    [InlineData("INC-2026092800027-")]
    [InlineData("..\\..\\windows\\sys")] // must never be accepted as a folder name
    public void Rejects_malformed_keys(string? value)
    {
        IncidentKey.TryParse(value, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000)]
    public void Create_rejects_out_of_range_sequences(int sequence)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => IncidentKey.Create(new DateOnly(2026, 9, 28), sequence));
    }

    [Fact]
    public void Parse_throws_format_exception_for_invalid_input()
    {
        Should.Throw<FormatException>(() => IncidentKey.Parse("nope", null));
    }

    [Fact]
    public void All_user_incident_types_have_display_names()
    {
        foreach (var type in Enum.GetValues<UserIncidentType>())
        {
            type.ToDisplayName().ShouldNotBeNullOrWhiteSpace();
        }

        UserIncidentType.ScreenFlicker.ToDisplayName().ShouldBe("Screen Flicker");
        UserIncidentType.HighRam.ToDisplayName().ShouldBe("High RAM");
    }
}
