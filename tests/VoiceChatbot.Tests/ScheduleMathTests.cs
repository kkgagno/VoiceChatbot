using VoiceChatbot;
using Xunit;

public class ScheduleMathTests
{
    private static readonly DateTime Jan31 = new(2026, 1, 31, 9, 0, 0);

    [Fact]
    public void MonthlyOn31stUsesLastDayOfShortMonthsAndReturnsTo31st()
    {
        var slot = Jan31;
        var days = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            // Each run advances from the slot it ran for, as the scheduler does.
            slot = ScheduleMath.NextOccurrenceAfter(ScheduledTaskRecurrence.Monthly, Jan31, slot);
            days.Add(slot.Day);
            Assert.Equal(new TimeSpan(9, 0, 0), slot.TimeOfDay);
        }

        // Feb 2026 .. Jan 2027
        Assert.Equal(new[] { 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31, 31 }, days);
    }

    [Fact]
    public void MonthlyOn31stUsesFeb29InLeapYears()
    {
        var anchor = new DateTime(2028, 1, 31, 7, 30, 0);
        var feb = ScheduleMath.NextOccurrenceAfter(ScheduledTaskRecurrence.Monthly, anchor, anchor);
        var mar = ScheduleMath.NextOccurrenceAfter(ScheduledTaskRecurrence.Monthly, anchor, feb);

        Assert.Equal(new DateTime(2028, 2, 29, 7, 30, 0), feb);
        Assert.Equal(new DateTime(2028, 3, 31, 7, 30, 0), mar);
    }

    [Fact]
    public void MonthlySkipsAheadPastLongGaps()
    {
        var now = new DateTime(2026, 6, 15, 12, 0, 0);
        Assert.Equal(new DateTime(2026, 6, 30, 9, 0, 0),
            ScheduleMath.NextOccurrenceAfter(ScheduledTaskRecurrence.Monthly, Jan31, now));
    }

    [Theory]
    [InlineData(ScheduledTaskRecurrence.Hourly, "2026-03-04 10:15", "2026-03-04 11:00")]
    [InlineData(ScheduledTaskRecurrence.Daily, "2026-03-04 10:15", "2026-03-05 09:00")]
    [InlineData(ScheduledTaskRecurrence.Weekly, "2026-03-04 10:15", "2026-03-07 09:00")]
    [InlineData(ScheduledTaskRecurrence.Daily, "2026-03-04 09:00", "2026-03-05 09:00")]
    [InlineData(ScheduledTaskRecurrence.Daily, "2026-03-04 08:59", "2026-03-04 09:00")]
    public void NextOccurrenceIsStrictlyAfter(ScheduledTaskRecurrence recurrence, string after, string expected)
    {
        var anchor = new DateTime(2026, 1, 3, 9, 0, 0); // a Saturday
        Assert.Equal(DateTime.Parse(expected), ScheduleMath.NextOccurrenceAfter(recurrence, anchor, DateTime.Parse(after)));
    }

    [Fact]
    public void FutureAnchorIsTheNextOccurrence()
    {
        var anchor = new DateTime(2026, 5, 1, 9, 0, 0);
        Assert.Equal(anchor, ScheduleMath.NextOccurrenceAfter(ScheduledTaskRecurrence.Daily, anchor, new DateTime(2026, 4, 1)));
        Assert.Null(ScheduleMath.LatestOccurrenceAtOrBefore(ScheduledTaskRecurrence.Daily, anchor, new DateTime(2026, 4, 1)));
    }

    [Fact]
    public void LatestOccurrenceAtOrBefore()
    {
        var anchor = new DateTime(2026, 1, 1, 7, 0, 0);
        Assert.Equal(new DateTime(2026, 3, 5, 7, 0, 0),
            ScheduleMath.LatestOccurrenceAtOrBefore(ScheduledTaskRecurrence.Daily, anchor, new DateTime(2026, 3, 5, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 3, 5, 7, 0, 0),
            ScheduleMath.LatestOccurrenceAtOrBefore(ScheduledTaskRecurrence.Daily, anchor, new DateTime(2026, 3, 5, 7, 0, 0)));
        Assert.Equal(new DateTime(2026, 2, 28, 9, 0, 0),
            ScheduleMath.LatestOccurrenceAtOrBefore(ScheduledTaskRecurrence.Monthly, Jan31, new DateTime(2026, 3, 30)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(500, 60)]
    [InlineData(0, 1)]
    public void RetryDelayBacksOffToAnHour(int failures, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), ScheduleMath.RetryDelay(failures));

    [Fact]
    public void RetryBackOffDelaysADueTask()
    {
        var now = new DateTime(2026, 3, 5, 7, 2, 0);
        var slot = new DateTime(2026, 3, 5, 7, 0, 0);

        Assert.True(ScheduleMath.IsDue(slot, null, now));
        Assert.False(ScheduleMath.IsDue(slot, now.AddMinutes(1), now));
        Assert.True(ScheduleMath.IsDue(slot, now, now));
        Assert.False(ScheduleMath.IsDue(now.AddMinutes(1), null, now));
    }

    [Fact]
    public void CatchUpRunsOnceForTheLatestMissedSlotWithinTheWindow()
    {
        var anchor = new DateTime(2026, 1, 1, 7, 0, 0);
        var nextRunAt = new DateTime(2026, 3, 2, 7, 0, 0); // app closed for three days
        var now = new DateTime(2026, 3, 5, 9, 0, 0);

        var plan = ScheduleMath.PlanStartupCatchUp(ScheduledTaskRecurrence.Daily, anchor, nextRunAt, now, ScheduleMath.CatchUpWindow);

        Assert.Equal(MissedRunAction.RunNow, plan.Action);
        Assert.Equal(new DateTime(2026, 3, 5, 7, 0, 0), plan.MissedAt);
        Assert.Equal(plan.MissedAt, plan.NextRunAt);
    }

    [Fact]
    public void CatchUpSkipsToTheNextSlotWhenMoreThan12HoursLate()
    {
        var anchor = new DateTime(2026, 1, 1, 7, 0, 0);
        var now = new DateTime(2026, 3, 5, 19, 30, 0);

        var plan = ScheduleMath.PlanStartupCatchUp(ScheduledTaskRecurrence.Daily, anchor, new DateTime(2026, 3, 5, 7, 0, 0), now, ScheduleMath.CatchUpWindow);

        Assert.Equal(MissedRunAction.Skip, plan.Action);
        Assert.Equal(new DateTime(2026, 3, 5, 7, 0, 0), plan.MissedAt);
        Assert.Equal(new DateTime(2026, 3, 6, 7, 0, 0), plan.NextRunAt);
    }

    [Fact]
    public void CatchUpForMonthlyKeepsTheAnchorDay()
    {
        var now = new DateTime(2026, 3, 2, 12, 0, 0);
        var plan = ScheduleMath.PlanStartupCatchUp(ScheduledTaskRecurrence.Monthly, Jan31, new DateTime(2026, 2, 28, 9, 0, 0), now, ScheduleMath.CatchUpWindow);

        Assert.Equal(MissedRunAction.Skip, plan.Action);
        Assert.Equal(new DateTime(2026, 3, 31, 9, 0, 0), plan.NextRunAt);
    }

    [Theory]
    [InlineData(11, MissedRunAction.RunNow)]
    [InlineData(12, MissedRunAction.RunNow)]
    [InlineData(13, MissedRunAction.Skip)]
    public void CatchUpForOnceTasks(int hoursLate, MissedRunAction expected)
    {
        var due = new DateTime(2026, 3, 5, 7, 0, 0);
        var plan = ScheduleMath.PlanStartupCatchUp(ScheduledTaskRecurrence.Once, due, due, due.AddHours(hoursLate), ScheduleMath.CatchUpWindow);

        Assert.Equal(expected, plan.Action);
        Assert.Equal(due, plan.MissedAt);
    }

    [Fact]
    public void NothingToCatchUpWhenTheSlotIsInTheFuture()
    {
        var now = new DateTime(2026, 3, 5, 6, 0, 0);
        var plan = ScheduleMath.PlanStartupCatchUp(ScheduledTaskRecurrence.Daily, now.AddHours(1), now.AddHours(1), now, ScheduleMath.CatchUpWindow);
        Assert.Equal(MissedRunAction.None, plan.Action);
    }

    [Fact]
    public void ChangingOnlyTheTimeKeepsTheAnchorDate()
    {
        // Monthly task anchored on Jan 31, currently waiting on Feb 28: moving it to 10:00 must not pin it to the 28th.
        var anchor = ScheduleMath.UpdateAnchor(Jan31, new DateTime(2026, 2, 28, 9, 0, 0), ScheduledTaskRecurrence.Monthly,
            new DateTime(2026, 2, 28, 10, 0, 0), ScheduledTaskRecurrence.Monthly);

        Assert.Equal(new DateTime(2026, 1, 31, 10, 0, 0), anchor);
        Assert.Equal(new DateTime(2026, 3, 31, 10, 0, 0),
            ScheduleMath.NextOccurrenceAfter(ScheduledTaskRecurrence.Monthly, anchor, new DateTime(2026, 2, 28, 10, 0, 0)));
    }

    [Fact]
    public void ChangingTheDateOrRecurrenceStartsANewSeries()
    {
        var current = new DateTime(2026, 2, 28, 9, 0, 0);
        var newDate = new DateTime(2026, 3, 15, 9, 0, 0);

        Assert.Equal(newDate, ScheduleMath.UpdateAnchor(Jan31, current, ScheduledTaskRecurrence.Monthly, newDate, ScheduledTaskRecurrence.Monthly));
        Assert.Equal(current, ScheduleMath.UpdateAnchor(Jan31, current, ScheduledTaskRecurrence.Monthly, current, ScheduledTaskRecurrence.Daily));
        Assert.Equal(current, ScheduleMath.UpdateAnchor(null, current, ScheduledTaskRecurrence.Monthly, current, ScheduledTaskRecurrence.Monthly));
    }
}
