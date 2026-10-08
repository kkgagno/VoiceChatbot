using System;

namespace VoiceChatbot;

public enum ScheduledTaskRecurrence
{
    Once,
    Hourly,
    Daily,
    Weekly,
    Monthly
}

/// <summary>What the startup catch-up policy does with a run that was missed while the app was closed.</summary>
public enum MissedRunAction
{
    /// <summary>Nothing was missed.</summary>
    None,
    /// <summary>Run once now for the most recent missed slot.</summary>
    RunNow,
    /// <summary>Too late: do not run, move on to the next slot (a Once task is turned off).</summary>
    Skip
}

/// <param name="Action">What to do.</param>
/// <param name="MissedAt">The most recent slot that was missed (default when nothing was missed).</param>
/// <param name="NextRunAt">The slot the task should be scheduled for afterwards: the missed slot itself
/// for <see cref="MissedRunAction.RunNow"/>, the first future slot for a skipped recurring task.</param>
public readonly record struct CatchUpPlan(MissedRunAction Action, DateTime MissedAt, DateTime NextRunAt);

/// <summary>
/// Recurrence, retry and catch-up rules for scheduled tasks. Every slot of a recurring task is
/// computed from a fixed anchor (the date and time the user picked), never by stepping from the
/// previous slot, so a monthly task on the 31st runs on the last day of shorter months and goes
/// back to the 31st afterwards.
/// </summary>
public static class ScheduleMath
{
    /// <summary>Missed runs younger than this still run once at startup; older ones are skipped.</summary>
    public static readonly TimeSpan CatchUpWindow = TimeSpan.FromHours(12);

    /// <summary>Longest wait between retries of a failing scheduled run.</summary>
    public static readonly TimeSpan MaxRetryDelay = TimeSpan.FromHours(1);

    /// <summary>The k-th slot of the series that starts at <paramref name="anchor"/> (k = 0 is the anchor).</summary>
    public static DateTime Occurrence(ScheduledTaskRecurrence recurrence, DateTime anchor, int k) => recurrence switch
    {
        ScheduledTaskRecurrence.Hourly => anchor.AddHours(k),
        ScheduledTaskRecurrence.Daily => anchor.AddDays(k),
        ScheduledTaskRecurrence.Weekly => anchor.AddDays(7.0 * k),
        // AddMonths from the anchor clamps to the month's last day without losing the anchor day.
        ScheduledTaskRecurrence.Monthly => anchor.AddMonths(k),
        _ => anchor
    };

    /// <summary>First slot strictly after <paramref name="after"/>. A Once task has only its anchor.</summary>
    public static DateTime NextOccurrenceAfter(ScheduledTaskRecurrence recurrence, DateTime anchor, DateTime after)
    {
        if (recurrence == ScheduledTaskRecurrence.Once || anchor > after)
            return anchor;

        return Occurrence(recurrence, anchor, IndexAtOrBefore(recurrence, anchor, after) + 1);
    }

    /// <summary>Latest slot at or before <paramref name="atOrBefore"/>, or null when the series starts later.</summary>
    public static DateTime? LatestOccurrenceAtOrBefore(ScheduledTaskRecurrence recurrence, DateTime anchor, DateTime atOrBefore)
    {
        if (anchor > atOrBefore)
            return null;
        if (recurrence == ScheduledTaskRecurrence.Once)
            return anchor;

        return Occurrence(recurrence, anchor, IndexAtOrBefore(recurrence, anchor, atOrBefore));
    }

    /// <summary>
    /// The anchor to keep after the user edits a task. A new date or recurrence starts a new series.
    /// Changing only the time of day keeps the anchor's date, so editing the time of a monthly task
    /// while it sits on the 28th of February does not move it off the 31st.
    /// </summary>
    public static DateTime UpdateAnchor(
        DateTime? currentAnchor,
        DateTime currentNextRun,
        ScheduledTaskRecurrence currentRecurrence,
        DateTime newNextRun,
        ScheduledTaskRecurrence newRecurrence)
    {
        if (currentAnchor is not DateTime anchor
            || newRecurrence != currentRecurrence
            || newNextRun.Date != currentNextRun.Date)
            return newNextRun;

        return anchor.Date + newNextRun.TimeOfDay;
    }

    /// <summary>Wait before retrying after the given number of consecutive failures: 1, 2, 4 ... minutes, at most an hour.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures)
    {
        var exponent = Math.Clamp(consecutiveFailures, 1, 30) - 1;
        var minutes = Math.Min(Math.Pow(2, exponent), MaxRetryDelay.TotalMinutes);
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>A task is due once its slot has arrived and any retry back-off has passed.</summary>
    public static bool IsDue(DateTime nextRunAt, DateTime? retryAt, DateTime now) =>
        nextRunAt <= now && (retryAt is not DateTime retry || retry <= now);

    /// <summary>
    /// Startup catch-up policy for a slot that passed while the app was closed: if the most recent
    /// missed slot is within <paramref name="window"/> it runs once now (older missed slots are not
    /// replayed); otherwise it is skipped and a recurring task moves to its next future slot.
    /// </summary>
    public static CatchUpPlan PlanStartupCatchUp(
        ScheduledTaskRecurrence recurrence,
        DateTime anchor,
        DateTime nextRunAt,
        DateTime now,
        TimeSpan window)
    {
        if (nextRunAt > now)
            return new CatchUpPlan(MissedRunAction.None, default, nextRunAt);

        var missedAt = nextRunAt;
        if (recurrence != ScheduledTaskRecurrence.Once
            && LatestOccurrenceAtOrBefore(recurrence, anchor, now) is DateTime latest
            && latest > missedAt)
        {
            missedAt = latest;
        }

        if (now - missedAt <= window)
            return new CatchUpPlan(MissedRunAction.RunNow, missedAt, missedAt);

        var next = recurrence == ScheduledTaskRecurrence.Once
            ? nextRunAt
            : NextOccurrenceAfter(recurrence, anchor, now);
        return new CatchUpPlan(MissedRunAction.Skip, missedAt, next);
    }

    // Index k of the last slot at or before target (target >= anchor, recurrence is not Once).
    private static int IndexAtOrBefore(ScheduledTaskRecurrence recurrence, DateTime anchor, DateTime target)
    {
        double estimate = recurrence switch
        {
            ScheduledTaskRecurrence.Hourly => (target - anchor).TotalHours,
            ScheduledTaskRecurrence.Daily => (target - anchor).TotalDays,
            ScheduledTaskRecurrence.Weekly => (target - anchor).TotalDays / 7,
            ScheduledTaskRecurrence.Monthly => (target.Year - anchor.Year) * 12 + target.Month - anchor.Month,
            _ => 0
        };

        var k = (int)Math.Clamp(Math.Floor(estimate), 0, int.MaxValue - 1);
        while (k > 0 && Occurrence(recurrence, anchor, k) > target)
            k--;
        while (Occurrence(recurrence, anchor, k + 1) <= target)
            k++;
        return k;
    }
}
