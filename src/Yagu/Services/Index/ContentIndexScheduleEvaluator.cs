namespace Yagu.Services.Index;

/// <summary>
/// Decides whether a scheduled content-index build pass is due right now (plan §6.1, the <c>OnSchedule</c>
/// build trigger). Two schedule modes are supported:
/// <list type="bullet">
/// <item><b>Interval</b> — repeat every N minutes (e.g. every 5 minutes).</item>
/// <item><b>Weekly</b> — run on chosen days of the week at a set time of day (e.g. Mon/Wed/Fri at 03:00).</item>
/// </list>
/// Pure and side-effect free so the decision is unit-tested; the caller owns the "last run" timestamp and
/// the wall clock. Because Yagu is a desktop app, the schedule only fires while Yagu is running — a time
/// that passes while the app is closed is simply skipped (it is not run retroactively on next launch).
/// </summary>
public static class ContentIndexScheduleEvaluator
{
    public const string ModeInterval = "Interval";
    public const string ModeWeekly = "Weekly";

    /// <summary>
    /// True when a scheduled build should start now, given the schedule settings, the local time
    /// <paramref name="now"/>, and when a scheduled pass <paramref name="lastRun"/> last ran (local).
    /// <para>Interval mode: due once <c>now - lastRun</c> reaches the configured interval.</para>
    /// <para>Weekly mode: due when today is a selected day, <paramref name="now"/> has reached the set time
    /// today, and no scheduled pass has run since today's set time (so it fires once per selected day).</para>
    /// </summary>
    public static bool IsDue(AppSettings settings, DateTimeOffset lastRun, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string mode = AppSettings.NormalizeIndexScheduleMode(settings.IndexScheduleMode);

        if (string.Equals(mode, ModeWeekly, StringComparison.Ordinal))
        {
            int mask = AppSettings.NormalizeIndexScheduleDaysOfWeekMask(settings.IndexScheduleDaysOfWeekMask);
            if (!IsDaySelected(mask, now.DayOfWeek))
                return false;

            TimeSpan scheduled = ParseTimeOfDay(settings.IndexScheduleTimeOfDay);
            if (now.TimeOfDay < scheduled)
                return false; // today's slot hasn't arrived yet

            // Fire once per selected day: only when nothing has run since today's scheduled instant.
            DateTimeOffset todaySlot = new(now.Year, now.Month, now.Day, scheduled.Hours, scheduled.Minutes, 0, now.Offset);
            return lastRun < todaySlot;
        }

        // Interval mode (default).
        int minutes = AppSettings.NormalizeIndexScheduleIntervalMinutes(settings.IndexScheduleIntervalMinutes);
        return now - lastRun >= TimeSpan.FromMinutes(minutes);
    }

    /// <summary>True when <paramref name="day"/>'s bit is set in <paramref name="mask"/> (bit 0 = Sunday).</summary>
    public static bool IsDaySelected(int mask, DayOfWeek day) => (mask & (1 << (int)day)) != 0;

    /// <summary>Parses an HH:mm time-of-day (already normalized on save), falling back to the default.</summary>
    public static TimeSpan ParseTimeOfDay(string? value)
    {
        if (TimeSpan.TryParse((value ?? string.Empty).Trim(), System.Globalization.CultureInfo.InvariantCulture, out TimeSpan t)
            && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return new TimeSpan(t.Hours, t.Minutes, 0);
        return ParseTimeOfDay(AppSettings.DefaultIndexScheduleTimeOfDay);
    }

    /// <summary>A short human-readable summary of the current schedule, for the settings description.</summary>
    public static string Describe(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string mode = AppSettings.NormalizeIndexScheduleMode(settings.IndexScheduleMode);
        if (string.Equals(mode, ModeWeekly, StringComparison.Ordinal))
        {
            int mask = AppSettings.NormalizeIndexScheduleDaysOfWeekMask(settings.IndexScheduleDaysOfWeekMask);
            string days = DescribeDays(mask);
            TimeSpan t = ParseTimeOfDay(settings.IndexScheduleTimeOfDay);
            return $"Runs {days} at {t:hh\\:mm}.";
        }

        int minutes = AppSettings.NormalizeIndexScheduleIntervalMinutes(settings.IndexScheduleIntervalMinutes);
        return minutes % 60 == 0
            ? $"Runs every {minutes / 60} hour(s)."
            : $"Runs every {minutes} minutes.";
    }

    private static string DescribeDays(int mask)
    {
        if ((mask & 0x7F) == 0x7F)
            return "every day";
        string[] abbr = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        var picked = new List<string>();
        for (int d = 0; d < 7; d++)
            if ((mask & (1 << d)) != 0)
                picked.Add(abbr[d]);
        return string.Join(", ", picked);
    }
}
