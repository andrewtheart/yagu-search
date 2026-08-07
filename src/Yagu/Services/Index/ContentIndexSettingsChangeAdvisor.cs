namespace Yagu.Services.Index;

/// <summary>Whether changing one scalar Indexing setting requires existing index bytes to be rebuilt.</summary>
public enum ContentIndexConfigChangeImpact
{
    /// <summary>The setting affects query choice, scheduling, resources, cleanup, or presentation only.</summary>
    NoRebuild,

    /// <summary>Any semantic value change affects what a full build stores for every maintained root.</summary>
    RebuildAllOnChange,

    /// <summary>Only changing the setting from false to true adds build output missing from older indexes.</summary>
    RebuildAllWhenEnabled,
}

/// <summary>A deep, normalized snapshot of the settings that can affect index build output.</summary>
public sealed record ContentIndexSettingsSnapshot(
    IReadOnlyDictionary<string, string> ScalarValues,
    IReadOnlyList<string> IndexedRoots,
    IReadOnlyDictionary<string, ContentIndexRootFilterSnapshot> RootFilters);

/// <summary>Normalized per-root build-time filters captured independently from mutable settings objects.</summary>
public sealed record ContentIndexRootFilterSnapshot(string IncludeGlobs, string ExcludeGlobs);

/// <summary>One saved setting change that makes a build/rebuild advisable.</summary>
public sealed record ContentIndexRebuildReason(string SettingKey, string Description);

/// <summary>
/// Recommendation produced after comparing two settings snapshots. Existing indexes remain correctness-safe:
/// files omitted by an older policy live-scan, and unavailable optional query structures cause a safe fallback. Rebuilding
/// makes coverage, optional namespaces, and storage location match the newly saved settings.
/// </summary>
public sealed record ContentIndexSettingsChangeAdvice(
    IReadOnlyList<ContentIndexRebuildReason> Reasons,
    IReadOnlyList<string> AffectedRoots)
{
    public bool HasRecommendation => Reasons.Count > 0 && AffectedRoots.Count > 0;
}

/// <summary>
/// Exhaustive policy for deciding whether saved Indexing changes should prompt for a rebuild. Every scalar
/// key exposed by <see cref="ContentIndexConfigService"/> is explicitly classified: only build-output changes
/// recommend a rebuild; query, scheduling, resource, retention, telemetry, and presentation changes apply
/// immediately or at the next operation and deliberately do not nag the user.
/// </summary>
public static class ContentIndexSettingsChangeAdvisor
{
    private static readonly Dictionary<string, ContentIndexConfigChangeImpact> RebuildImpacts =
        new Dictionary<string, ContentIndexConfigChangeImpact>(StringComparer.OrdinalIgnoreCase)
        {
            ["IndexStorageDirectory"] = ContentIndexConfigChangeImpact.RebuildAllOnChange,
            ["IndexMaxFileSizeMB"] = ContentIndexConfigChangeImpact.RebuildAllOnChange,
            ["IndexFollowReparsePoints"] = ContentIndexConfigChangeImpact.RebuildAllOnChange,
            ["IndexIncludeHiddenFiles"] = ContentIndexConfigChangeImpact.RebuildAllOnChange,
            ["IndexExcludedGlobs"] = ContentIndexConfigChangeImpact.RebuildAllOnChange,
            ["IndexExcludedExtensions"] = ContentIndexConfigChangeImpact.RebuildAllOnChange,
            ["IndexBuildPdfTextExtendedSource"] = ContentIndexConfigChangeImpact.RebuildAllWhenEnabled,
            ["IndexBuildImageTextExtendedSource"] = ContentIndexConfigChangeImpact.RebuildAllWhenEnabled,
            ["IndexProduceV3QueryStructures"] = ContentIndexConfigChangeImpact.RebuildAllWhenEnabled,
        };

    // Keep this explicit rather than treating unknown keys as harmless. The exhaustive unit test compares
    // this policy with ContentIndexConfigService.Keys, so adding a setting forces an intentional decision.
    private static readonly HashSet<string> NoRebuildKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "EnableContentIndex",
        "UseContentIndexByDefault",
        "IndexAccelerateLiterals",
        "IndexAccelerateWholeWord",
        "IndexAccelerateRegex",
        "IndexAccelerateMultiline",
        "IndexUseNativeWorker",
        "IndexUseWorkerQuerySessions",
        "IndexUseV3QueryReader",
        "IndexQueryStartupBudgetMs",
        "IndexMaxCandidatePercent",
        "IndexMaxInProcessSizeMB",
        "IndexMaxWorkerQuerySizeMB",
        "IndexQueryMemoryBudgetMB",
        "IndexQueryWorkerParallelism",
        "IndexMaxDiskSizeMB",
        "IndexMinimumFreeSpaceMB",
        "IndexMaxDiskUsagePercent",
        "IndexRetainedGenerationCount",
        "IndexStaleTemporaryHours",
        "IndexQuarantineRetentionDays",
        "IndexBuildTrigger",
        "IndexScheduleMode",
        "IndexScheduleIntervalMinutes",
        "IndexScheduleDaysOfWeekMask",
        "IndexScheduleTimeOfDay",
        "IndexUpdateMode",
        "IndexIdleDelayMinutes",
        "IndexContinuousIntervalMinutes",
        "IndexBuildMemoryBudgetMB",
        "IndexBuildWorkerParallelism",
        "IndexPauseDuringForegroundSearch",
        "IndexPauseOnBattery",
        "IndexRemovableDrivePolicy",
        "IndexMaxJournalCatchupMB",
        "IndexMaxJournalCatchupRecords",
        "IndexPostBuildCatchUpThresholdChanges",
        "IndexUseWatcherHints",
        "IndexMaxDeltaSegments",
        "IndexCompactionThresholdMB",
        "IndexMaxAutoCompactionSizeMB",
        // Size management alters how an existing index reorganizes or when its maintenance stops; it never
        // changes which files were ingested, so it never warrants a rebuild prompt.
        "IndexSizeManagementMode",
        "IndexCoalesceMaxSegmentMB",
        "IndexCoalesceMaxBatchMB",
        "IndexCoalesceMinRun",
        "IndexCoalesceMaxRunsPerPass",
        "ShareAggregateIndexTelemetry",
        "IndexAutoRepair",
        "ShowIndexStatusInMainWindow",
        "ShowIndexBuildNotifications",
        "ShowIndexProvenanceInResults",
    };

    private static readonly Dictionary<string, string> ReasonDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["IndexStorageDirectory"] = "The index data location changed; maintained folders need indexes in the new location.",
            ["IndexMaxFileSizeMB"] = "The maximum file size included in index builds changed.",
            ["IndexFollowReparsePoints"] = "The junction/symlink ingestion policy changed.",
            ["IndexIncludeHiddenFiles"] = "The hidden-file ingestion policy changed.",
            ["IndexExcludedGlobs"] = "The global build-time excluded globs changed.",
            ["IndexExcludedExtensions"] = "The global build-time excluded extensions changed.",
            ["IndexBuildPdfTextExtendedSource"] = "PDF-text index generation was enabled; existing indexes do not necessarily contain that namespace.",
            ["IndexBuildImageTextExtendedSource"] = "Image-text index generation was enabled; existing indexes do not contain OCR candidate postings.",
            ["IndexProduceV3QueryStructures"] = "Format-v3 query structures were enabled; existing layers do not necessarily contain the required query files stored alongside each layer.",
            ["IndexedRoots"] = "One or more maintained index folders were added or broadened.",
            ["IndexedRootFilters"] = "Per-folder build-time include/exclude filters changed.",
        };

    /// <summary>Captures normalized scalar settings, maintained roots, and per-root filters by value.</summary>
    public static ContentIndexSettingsSnapshot Capture(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var scalar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in ContentIndexConfigService.GetAll(settings))
            scalar[key] = NormalizeScalarValue(key, value);

        List<string> roots = IndexedRootsPolicy.Normalize(settings.IndexedRoots);
        var filters = new Dictionary<string, ContentIndexRootFilterSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (IndexedRootFilter filter in IndexedRootFilterPolicy.Normalize(settings.IndexedRootFilters))
        {
            filters[IndexScopeIdentity.NormalizePath(filter.Path)] = new ContentIndexRootFilterSnapshot(
                NormalizePatternList(filter.IncludeGlobs),
                NormalizePatternList(filter.ExcludeGlobs));
        }

        return new ContentIndexSettingsSnapshot(scalar, roots.ToArray(), filters);
    }

    /// <summary>Compares two snapshots and returns deterministic reasons plus the minimal maintained-root set.</summary>
    public static ContentIndexSettingsChangeAdvice Analyze(
        ContentIndexSettingsSnapshot before,
        ContentIndexSettingsSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var reasons = new List<ContentIndexRebuildReason>();
        var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool rebuildAll = false;

        foreach (string key in ContentIndexConfigService.Keys)
        {
            ContentIndexConfigChangeImpact impact = RebuildImpacts.TryGetValue(key, out ContentIndexConfigChangeImpact rebuildImpact)
                ? rebuildImpact
                : ContentIndexConfigChangeImpact.NoRebuild;

            string oldValue = before.ScalarValues.TryGetValue(key, out string? oldSetting) ? oldSetting : string.Empty;
            string newValue = after.ScalarValues.TryGetValue(key, out string? newSetting) ? newSetting : string.Empty;
            if (string.Equals(oldValue, newValue, ScalarComparisonFor(key)))
                continue;

            bool recommends = impact == ContentIndexConfigChangeImpact.RebuildAllOnChange
                || (impact == ContentIndexConfigChangeImpact.RebuildAllWhenEnabled && IsTrue(newValue));
            if (!recommends)
                continue;

            rebuildAll = true;
            reasons.Add(new ContentIndexRebuildReason(key, ReasonDescriptions[key]));
        }

        var oldRoots = new HashSet<string>(before.IndexedRoots, StringComparer.OrdinalIgnoreCase);
        foreach (string root in after.IndexedRoots)
        {
            if (!oldRoots.Contains(root))
                affected.Add(root);
        }
        if (affected.Count > 0)
            reasons.Add(new ContentIndexRebuildReason("IndexedRoots", ReasonDescriptions["IndexedRoots"]));

        var afterRootSet = new HashSet<string>(after.IndexedRoots, StringComparer.OrdinalIgnoreCase);
        bool rootFiltersChanged = false;
        foreach (string root in after.IndexedRoots)
        {
            before.RootFilters.TryGetValue(root, out ContentIndexRootFilterSnapshot? oldFilter);
            after.RootFilters.TryGetValue(root, out ContentIndexRootFilterSnapshot? newFilter);
            if (Equals(oldFilter, newFilter))
                continue;
            affected.Add(root);
            rootFiltersChanged = true;
        }
        if (rootFiltersChanged)
            reasons.Add(new ContentIndexRebuildReason("IndexedRootFilters", ReasonDescriptions["IndexedRootFilters"]));

        if (rebuildAll)
        {
            affected.Clear();
            foreach (string root in after.IndexedRoots)
                affected.Add(root);
        }
        else
        {
            affected.RemoveWhere(root => !afterRootSet.Contains(root));
        }

        string[] orderedRoots = after.IndexedRoots.Where(affected.Contains).ToArray();
        return new ContentIndexSettingsChangeAdvice(reasons, orderedRoots);
    }

    /// <summary>Returns the explicit policy classification for a scalar CLI/UI config key.</summary>
    public static bool TryGetConfigKeyImpact(string key, out ContentIndexConfigChangeImpact impact)
    {
        if (RebuildImpacts.TryGetValue(key, out impact))
            return true;
        if (NoRebuildKeys.Contains(key))
        {
            impact = ContentIndexConfigChangeImpact.NoRebuild;
            return true;
        }
        impact = default;
        return false;
    }

    private static bool IsTrue(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static StringComparison ScalarComparisonFor(string key)
        => key == "IndexStorageDirectory" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizeScalarValue(string key, string value)
        => key switch
        {
            "IndexExcludedGlobs" => NormalizePatternList(value),
            "IndexExcludedExtensions" => NormalizeExtensionList(value),
            "IndexStorageDirectory" => NormalizeStoragePath(value),
            _ => value,
        };

    private static string NormalizeStoragePath(string? value)
    {
        string normalized = AppSettings.NormalizeIndexStorageDirectory(value).Replace('/', '\\');
        if (normalized.Length > 3)
            normalized = normalized.TrimEnd('\\');
        return normalized;
    }

    private static string NormalizePatternList(string? value)
        => string.Join(';', SplitList(value)
            .Select(static item => item.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase));

    private static string NormalizeExtensionList(string? value)
    {
        IEnumerable<string> normalized = SplitList(value).Select(static item =>
        {
            string extension = item;
            if (extension.StartsWith("*.", StringComparison.Ordinal))
                extension = extension[2..];
            else if (extension.StartsWith('.'))
                extension = extension[1..];
            return extension.ToLowerInvariant();
        });
        return string.Join(';', normalized
            .Where(static item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase));
    }

    private static string[] SplitList(string? value)
        => (value ?? string.Empty).Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
