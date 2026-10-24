using PcDiag.Core.Recording;

namespace PcDiag.Core.Tests;

public class RecordingScheduleTests
{
    private static readonly RecordingWindow WorkingHours = new()
    {
        Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
        Start = new TimeOnly(9, 0),
        End = new TimeOnly(18, 0),
    };

    // Monday 22:00 → Tuesday 02:00
    private static readonly RecordingWindow MondayNight = new()
    {
        Days = [DayOfWeek.Monday],
        Start = new TimeOnly(22, 0),
        End = new TimeOnly(2, 0),
    };

    [Theory]
    [InlineData("2026-09-28T09:00:00", true)]   // Monday, start is inclusive
    [InlineData("2026-09-28T17:59:59", true)]
    [InlineData("2026-09-28T18:00:00", false)]  // end is exclusive
    [InlineData("2026-09-28T08:59:59", false)]
    [InlineData("2026-10-03T12:00:00", false)]  // Saturday
    public void Daytime_window(string localTime, bool expected)
    {
        WorkingHours.Contains(DateTime.Parse(localTime, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("2026-09-28T21:59:59", false)] // Monday before start
    [InlineData("2026-09-28T22:00:00", true)]  // Monday night
    [InlineData("2026-09-28T23:59:59", true)]
    [InlineData("2026-09-29T00:00:00", true)]  // Tuesday early morning belongs to Monday's window
    [InlineData("2026-09-29T01:59:59", true)]
    [InlineData("2026-09-29T02:00:00", false)]
    [InlineData("2026-09-29T22:30:00", false)] // Tuesday night is not scheduled
    [InlineData("2026-09-28T01:00:00", false)] // Monday early morning belongs to Sunday (not scheduled)
    public void Overnight_window(string localTime, bool expected)
    {
        MondayNight.Contains(DateTime.Parse(localTime, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(expected);
    }

    [Fact]
    public void Sunday_overnight_wraps_into_monday()
    {
        var sundayNight = new RecordingWindow { Days = [DayOfWeek.Sunday], Start = new TimeOnly(23, 0), End = new TimeOnly(1, 0) };

        sundayNight.Contains(new DateTime(2026, 9, 28, 0, 30, 0, DateTimeKind.Local)).ShouldBeTrue(); // Monday 00:30
    }

    [Fact]
    public void Equal_start_and_end_means_whole_day()
    {
        var allDay = new RecordingWindow { Days = [DayOfWeek.Wednesday], Start = new TimeOnly(0, 0), End = new TimeOnly(0, 0) };

        allDay.Contains(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Local)).ShouldBeTrue();
        allDay.Contains(new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Local)).ShouldBeTrue();
        allDay.Contains(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Local)).ShouldBeFalse();
    }

    [Fact]
    public void Schedule_is_active_when_any_window_matches()
    {
        RecordingWindow[] windows = [WorkingHours, MondayNight];

        RecordingSchedule.IsActive(windows, new DateTime(2026, 9, 28, 23, 0, 0, DateTimeKind.Local)).ShouldBeTrue();
        RecordingSchedule.IsActive(windows, new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Local)).ShouldBeFalse();
        RecordingSchedule.IsActive([], new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Local)).ShouldBeFalse();
    }
}
