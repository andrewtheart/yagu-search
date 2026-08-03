using Yagu.Services;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

/// <summary>Unit tests for the OnSchedule build-trigger due-check (interval + weekly-days-and-time).</summary>
public sealed class ContentIndexScheduleEvaluatorTests
{
    private static AppSettings Interval(int minutes) => new()
    {
        IndexScheduleMode = "Interval",
        IndexScheduleIntervalMinutes = minutes,
    };

    private static AppSettings Weekly(int daysMask, string time) => new()
    {
        IndexScheduleMode = "Weekly",
        IndexScheduleDaysOfWeekMask = daysMask,
        IndexScheduleTimeOfDay = time,
    };

    [Fact]
    public void Interval_IsDueOnlyOnceTheIntervalHasElapsed()
    {
        var s = Interval(5);
        var last = new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero);

        Assert.False(ContentIndexScheduleEvaluator.IsDue(s, last, last.AddMinutes(4)));
        Assert.True(ContentIndexScheduleEvaluator.IsDue(s, last, last.AddMinutes(5)));
        Assert.True(ContentIndexScheduleEvaluator.IsDue(s, last, last.AddMinutes(30)));
    }

    [Fact]
    public void Weekly_IsDueOnASelectedDayAtOrAfterTheTime_OncePerDay()
    {
        var now = new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero);
        var s = Weekly(1 << (int)now.DayOfWeek, "03:00"); // today is selected

        // Before the scheduled time today → not due.
        var beforeSlot = new DateTimeOffset(2026, 7, 21, 2, 59, 0, TimeSpan.Zero);
        var lastYesterday = beforeSlot.AddDays(-1);
        Assert.False(ContentIndexScheduleEvaluator.IsDue(s, lastYesterday, beforeSlot));

        // At/after the scheduled time and nothing ran since today's slot → due.
        Assert.True(ContentIndexScheduleEvaluator.IsDue(s, lastYesterday, now));

        // Already ran at/after today's slot → not due again today.
        Assert.False(ContentIndexScheduleEvaluator.IsDue(s, now, now.AddHours(2)));
    }

    [Fact]
    public void Weekly_IsNotDueOnAnUnselectedDay()
    {
        var now = new DateTimeOffset(2026, 7, 21, 5, 0, 0, TimeSpan.Zero);
        int otherDay = 1 << (((int)now.DayOfWeek + 1) % 7); // a day that is NOT today
        var s = Weekly(otherDay, "03:00");

        Assert.False(ContentIndexScheduleEvaluator.IsDue(s, DateTimeOffset.MinValue, now));
    }

    [Fact]
    public void IsDaySelected_MatchesBitmask()
    {
        Assert.True(ContentIndexScheduleEvaluator.IsDaySelected(0x7F, DayOfWeek.Wednesday));
        Assert.True(ContentIndexScheduleEvaluator.IsDaySelected(1 << (int)DayOfWeek.Sunday, DayOfWeek.Sunday));
        Assert.False(ContentIndexScheduleEvaluator.IsDaySelected(1 << (int)DayOfWeek.Monday, DayOfWeek.Tuesday));
    }

    [Fact]
    public void ParseTimeOfDay_ParsesHhMm_OrFallsBackToDefault()
    {
        Assert.Equal(new TimeSpan(3, 30, 0), ContentIndexScheduleEvaluator.ParseTimeOfDay("03:30"));
        Assert.Equal(new TimeSpan(3, 30, 0), ContentIndexScheduleEvaluator.ParseTimeOfDay("3:30"));
        Assert.Equal(new TimeSpan(3, 0, 0), ContentIndexScheduleEvaluator.ParseTimeOfDay("nonsense")); // default 03:00
        Assert.Equal(new TimeSpan(3, 0, 0), ContentIndexScheduleEvaluator.ParseTimeOfDay(null));
    }

    [Fact]
    public void Describe_SummarizesIntervalAndWeekly()
    {
        Assert.Contains("every", ContentIndexScheduleEvaluator.Describe(Interval(60)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5 minutes", ContentIndexScheduleEvaluator.Describe(Interval(5)), StringComparison.Ordinal);
        string weekly = ContentIndexScheduleEvaluator.Describe(Weekly(1 << (int)DayOfWeek.Monday, "03:00"));
        Assert.Contains("Mon", weekly);
        Assert.Contains("03:00", weekly);
        Assert.Contains("every day", ContentIndexScheduleEvaluator.Describe(Weekly(0x7F, "03:00")));
    }
}
