using System.Globalization;

namespace Yagu.Services.Index;

public enum IndexCompactionForecastKind
{
    CollectingHistory,
    AutomaticMaintenanceOff,
    AutomaticCompaction,
    CleanupAttentionLikely,
}

/// <summary>A user-facing estimate derived from active update-layer manifests and current cleanup policy.</summary>
public sealed record IndexCompactionForecast(
    IndexCompactionForecastKind Kind,
    DateTimeOffset? EstimatedUtc,
    double GrowthMiBPerHour,
    string Summary,
    string Details);

/// <summary>
/// Predicts when update history will cross the full-compaction threshold. This is intentionally an
/// estimate, not a scheduler promise: file churn can change, bounded coalescing may reclaim history,
/// and maintenance only runs while its configured trigger is eligible.
/// </summary>
public static class IndexCompactionForecaster
{
    private const double MiB = 1024.0 * 1024.0;

    /// <summary>A near-idle index implies a growth rate that would overflow <see cref="TimeSpan"/>.</summary>
    private const double MaxForecastHours = 24 * 365 * 10;

    public static IndexCompactionForecast Estimate(
        string root,
        ActiveLayerStorageTrend trend,
        EffectiveIndexSizePolicy policy,
        int maxDeltaSegments,
        int compactionThresholdMB,
        AppSettings settings,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(settings);

        ActiveLayerStorageBreakdown breakdown = trend.Breakdown;
        int thresholdMB = Math.Max(1, compactionThresholdMB);
        bool automaticIncremental = string.Equals(
            AppSettings.NormalizeIndexUpdateMode(settings.IndexUpdateMode),
            AppSettings.IndexUpdateModeAutomaticIncremental,
            StringComparison.Ordinal);
        string normalizedTrigger = AppSettings.NormalizeIndexBuildTrigger(settings.IndexBuildTrigger);
        bool automaticTrigger = !string.Equals(
            normalizedTrigger,
            ContentIndexBuildScheduler.TriggerManual,
            StringComparison.OrdinalIgnoreCase);

        if (!automaticIncremental || !automaticTrigger)
        {
            return new IndexCompactionForecast(
                IndexCompactionForecastKind.AutomaticMaintenanceOff,
                null,
                0,
                "Automatic compaction is not scheduled",
                $"{root} is not using recurring automatic incremental maintenance. Its update history is "
                    + $"currently {FormatMiB(breakdown.IncrementalHistoryBytes)} across "
                    + $"{breakdown.IncrementalCount:N0} layer(s). Enable an automatic trigger with Automatic "
                    + "incremental updates to receive a date estimate; manual Compact now remains available.");
        }

        if (breakdown.IncrementalCount < 2
            || trend.OldestIncrementalBuiltUtc is not { } oldest
            || trend.NewestIncrementalBuiltUtc is not { } newest
            || newest <= oldest)
        {
            return new IndexCompactionForecast(
                IndexCompactionForecastKind.CollectingHistory,
                null,
                0,
                "Compaction estimate: collecting update history",
                $"Yagu needs at least two active incremental layers for {root} before it can estimate a "
                    + $"growth rate. Current history: {FormatMiB(breakdown.IncrementalHistoryBytes)} across "
                    + $"{breakdown.IncrementalCount:N0} layer(s). The estimate will appear after another "
                    + "successful incremental update.");
        }

        double observedHours = (newest - oldest).TotalHours
            * breakdown.IncrementalCount / Math.Max(1, breakdown.IncrementalCount - 1);
        if (breakdown.IncrementalBytes <= 0)
        {
            return new IndexCompactionForecast(
                IndexCompactionForecastKind.CollectingHistory,
                null,
                0,
                "Compaction estimate: collecting update history",
                $"The active update layers for {root} do not yet span enough time to estimate their growth rate.");
        }

        double growthBytesPerHour = breakdown.IncrementalBytes / observedHours;
        // ShouldCompact weighs every ACTIVE SEGMENT, so full-build paging layers count toward the size
        // threshold and the layer-count limit even though only incremental layers grow over time.
        long activeSegmentBytes = breakdown.FullBuildPagingBytes + breakdown.IncrementalBytes;
        long thresholdBytes = (long)thresholdMB * 1024 * 1024;
        double remainingBytes = Math.Max(0, (thresholdMB * MiB) - activeSegmentBytes);
        double hoursRemaining = remainingBytes / growthBytesPerHour;
        TimeSpan delay = TimeSpan.FromHours(Math.Clamp(hoursRemaining, 0, MaxForecastHours));

        TimeSpan meanLayerInterval = TimeSpan.FromTicks(
            (newest - oldest).Ticks / Math.Max(1, breakdown.IncrementalCount - 1));
        int layersUntilLimit = Math.Max(0, Math.Max(1, maxDeltaSegments) + 1 - breakdown.SegmentCount);

        // A coalescing pass runs first and absorbs the layer-count trigger, so that limit only predicts a
        // full fold for an index that is not allowed to coalesce.
        bool layerLimitFirst = false;
        if (!policy.AllowsCoalescing)
        {
            TimeSpan layerDelay = TimeSpan.FromTicks(meanLayerInterval.Ticks * layersUntilLimit);
            if (layerDelay < delay)
            {
                delay = layerDelay;
                layerLimitFirst = true;
            }
        }

        bool continuous = AppSettings.IndexBuildTriggerHas(
            normalizedTrigger,
            ContentIndexBuildScheduler.TriggerContinuous);
        int continuousMinutes = AppSettings.NormalizeIndexContinuousIntervalMinutes(
            settings.IndexContinuousIntervalMinutes);
        if (continuous)
        {
            TimeSpan cadence = TimeSpan.FromMinutes(continuousMinutes);
            long passes = Math.Max(1, (long)Math.Ceiling(delay.Ticks / (double)cadence.Ticks));
            delay = TimeSpan.FromTicks(passes * cadence.Ticks);
        }

        DateTimeOffset estimatedUtc = now.ToUniversalTime().Add(delay);
        long projectedSegmentBytes = layerLimitFirst
            ? activeSegmentBytes + (long)Math.Max(0, growthBytesPerHour * delay.TotalHours)
            : Math.Max(activeSegmentBytes, thresholdBytes);
        long projectedTotalBytes = breakdown.BaseBytes + projectedSegmentBytes;
        bool automaticCompaction = policy.AllowsCompactingIndexOf(projectedTotalBytes);
        IndexCompactionForecastKind kind = automaticCompaction
            ? IndexCompactionForecastKind.AutomaticCompaction
            : IndexCompactionForecastKind.CleanupAttentionLikely;
        string localEstimate = estimatedUtc.ToLocalTime().ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
        string summary = automaticCompaction
            ? $"Estimated auto-compaction: {localEstimate}"
            : $"Estimated cleanup warning: {localEstimate}";

        DateTimeOffset? coalesceUtc = null;
        if (policy.AllowsCoalescing && layersUntilLimit > 0)
            coalesceUtc = now.ToUniversalTime().AddTicks(meanLayerInterval.Ticks * layersUntilLimit);
        else if (policy.AllowsCoalescing && breakdown.SegmentCount > maxDeltaSegments)
            coalesceUtc = now.ToUniversalTime();

        string cleanupOutcome = automaticCompaction
            ? $"At the threshold, the active index is projected to be about {FormatBytes(projectedTotalBytes)}, "
                + $"within the {FormatCap(policy.MaxAutoCompactionSizeMB)} automatic-compaction cap, so Yagu "
                + "should compact it automatically rather than show a warning."
            : $"At the threshold, the active index is projected to be about {FormatBytes(projectedTotalBytes)}, "
                + $"outside the {FormatCap(policy.MaxAutoCompactionSizeMB)} automatic-compaction allowance. "
                + "If bounded coalescing cannot reclaim enough history, Yagu will ask what to do instead of "
                + "starting a large full compaction on its own.";
        string coalescing = coalesceUtc is { } coalesce
            && coalesce < estimatedUtc
                ? $" A smaller bounded coalescing pass is expected first, around "
                    + $"{coalesce.ToLocalTime():ddd yyyy-MM-dd HH:mm}; it may move the full-cleanup estimate later."
                : string.Empty;
        string schedule = continuous
            ? $" Continuous maintenance checks approximately every {continuousMinutes:N0} minute(s)."
            : " The configured automatic trigger decides when Yagu next checks this threshold.";

        string paging = breakdown.FullBuildPagingCount > 0
            ? $" Its {breakdown.FullBuildPagingCount:N0} full-build page layer(s) "
                + $"({FormatMiB(breakdown.FullBuildPagingBytes)}) count toward the same clean-up limits even "
                + "though they are not update history."
            : string.Empty;
        string reachedTrigger = layerLimitFirst
            ? $"At that pace it passes the {Math.Max(1, maxDeltaSegments):N0}-layer clean-up limit around "
                + $"{localEstimate}, before its {thresholdMB:N0} MiB size threshold. "
            : $"At that pace the {thresholdMB:N0} MiB full-cleanup threshold is reached around {localEstimate}. ";

        string details = $"{root} currently has {FormatMiB(breakdown.IncrementalHistoryBytes)} of update "
            + $"history across {breakdown.IncrementalCount:N0} active layer(s).{paging} Those layers imply an observed "
            + $"growth rate of about {growthBytesPerHour / MiB:N0} MiB/hour. "
            + reachedTrigger
            + cleanupOutcome + coalescing + schedule
            + " This estimate assumes recent file churn continues and Yagu remains open. Pausing or closing "
            + "Yagu shifts the date later; a burst of changes can move it earlier.";

        return new IndexCompactionForecast(
            kind,
            estimatedUtc,
            growthBytesPerHour / MiB,
            summary,
            details);
    }

    private static string FormatMiB(long bytes) => $"{bytes / MiB:N1} MiB";

    private static string FormatBytes(long bytes) => ContentIndexUiStatus.FormatBytes(bytes);

    private static string FormatCap(int capMB)
        => capMB <= 0 ? "uncapped" : $"{capMB:N0} MiB";
}