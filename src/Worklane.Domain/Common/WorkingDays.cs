namespace Worklane.Domain.Common;

/// <summary>
/// Elapsed working time in man-days, counting Monday–Friday only.
///
/// Work that falls on Saturday or Sunday counts as 0 working days (WL-35: this
/// rule stays). A card that starts and finishes entirely over a weekend therefore
/// contributes nothing, and a range that merely crosses a weekend drops those two
/// days. This measures wall-clock elapsed time, not effort: a person holding three
/// tasks in progress at once accrues the full duration on all three. Treat the
/// numbers as a trend, not as billable days. There is no holiday calendar — a task
/// spanning a public holiday still counts that weekday as worked.
/// </summary>
public static class WorkingDays
{
    /// <summary>
    /// Working days between two instants, as a fraction (half a working day = 0.5).
    /// A day of credit is 24 elapsed hours on a weekday, not 8 office hours — the
    /// card is "in progress" overnight too.
    /// Returns 0 when <paramref name="to"/> is not after <paramref name="from"/>.
    /// </summary>
    public static double Between(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;

        var cursor = from.UtcDateTime;
        var end = to.UtcDateTime;
        double hours = 0;

        while (cursor < end)
        {
            // Advance to the end of the current day, or to `end` if that comes first.
            var nextMidnight = cursor.Date.AddDays(1);
            var segmentEnd = nextMidnight < end ? nextMidnight : end;

            if (IsWorkingDay(cursor))
                hours += (segmentEnd - cursor).TotalHours;

            cursor = segmentEnd;
        }

        // A calendar day holds 24 hours of elapsed time but only one working day of credit.
        return Math.Round(hours / 24.0, 2);
    }

    /// <summary>Working days between <paramref name="from"/> and now.</summary>
    public static double Since(DateTimeOffset from) => Between(from, DateTimeOffset.UtcNow);

    private static bool IsWorkingDay(DateTime moment) =>
        moment.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// Whole working days between two dates, used for "how many days past due".
    /// Counts the number of Mon–Fri dates strictly after <paramref name="from"/>
    /// up to and including <paramref name="to"/>.
    /// </summary>
    public static int WholeDaysBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;

        var cursor = from.UtcDateTime.Date;
        var end = to.UtcDateTime.Date;
        int days = 0;

        while (cursor < end)
        {
            cursor = cursor.AddDays(1);
            if (IsWorkingDay(cursor)) days++;
        }

        return days;
    }
}
