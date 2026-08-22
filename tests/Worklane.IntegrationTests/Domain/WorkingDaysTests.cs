using FluentAssertions;
using Worklane.Domain.Common;

namespace Worklane.IntegrationTests.Domain;

/// <summary>
/// <see cref="WorkingDays"/> is the basis of every man-day figure in the reports, so the
/// weekend handling is pinned down here rather than inferred from the API responses.
/// </summary>
public class WorkingDaysTests
{
    private static DateTimeOffset At(int year, int month, int day, int hour = 0) =>
        new(new DateTime(year, month, day, hour, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Between_SameInstant_IsZero()
    {
        // Monday
        var monday = At(2026, 8, 17);
        WorkingDays.Between(monday, monday).Should().Be(0);
    }

    [Fact]
    public void Between_BackwardsRange_IsZero()
    {
        WorkingDays.Between(At(2026, 8, 18), At(2026, 8, 17)).Should().Be(0);
    }

    [Fact]
    public void Between_OneWeekday_IsOneDay()
    {
        // Monday 00:00 -> Tuesday 00:00
        WorkingDays.Between(At(2026, 8, 17), At(2026, 8, 18)).Should().Be(1);
    }

    [Fact]
    public void Between_HalfAWeekday_IsHalfADay()
    {
        // Monday 00:00 -> Monday 12:00
        WorkingDays.Between(At(2026, 8, 17), At(2026, 8, 17, 12)).Should().Be(0.5);
    }

    [Fact]
    public void Between_SpanningAWeekend_SkipsSaturdayAndSunday()
    {
        // Friday 00:00 -> Monday 00:00 is three calendar days but one working day.
        WorkingDays.Between(At(2026, 8, 21), At(2026, 8, 24)).Should().Be(1);
    }

    [Fact]
    public void Between_EntirelyInsideAWeekend_IsZero()
    {
        // Saturday -> Sunday
        WorkingDays.Between(At(2026, 8, 22), At(2026, 8, 23)).Should().Be(0);
    }

    [Fact]
    public void Between_FullWeek_CountsFiveDays()
    {
        // Monday -> next Monday
        WorkingDays.Between(At(2026, 8, 17), At(2026, 8, 24)).Should().Be(5);
    }

    [Fact]
    public void WholeDaysBetween_SpanningAWeekend_CountsWeekdaysOnly()
    {
        // Due Friday, delivered Monday — one working day late, not three.
        WorkingDays.WholeDaysBetween(At(2026, 8, 21), At(2026, 8, 24)).Should().Be(1);
    }

    [Fact]
    public void WholeDaysBetween_SameDay_IsZero()
    {
        WorkingDays.WholeDaysBetween(At(2026, 8, 17), At(2026, 8, 17, 23)).Should().Be(0);
    }
}
