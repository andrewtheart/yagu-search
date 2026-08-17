namespace Yagu.Services.Index;

/// <summary>One action offered when an index needs size attention.</summary>
public enum IndexSizeAttentionRemedy
{
    /// <summary>Fold every accumulated layer into one fresh base by streaming (recommended).</summary>
    CompactNow,

    /// <summary>Read the source files again and write a fresh index.</summary>
    Rebuild,

    /// <summary>Delete the stored index; searches read every file live.</summary>
    Delete,

    /// <summary>Open the index size settings so the user can change the limits themselves.</summary>
    ReviewSizeSettings,
}

/// <summary>
/// Why an index needs attention, expressed structurally rather than as an arbitrary ratio.
/// </summary>
/// <param name="CleanupDue">Accumulated update history has passed the configured layer/size thresholds.</param>
/// <param name="ReclamationBlocked">Cleanup is due and every allowed automatic path is unavailable.</param>
/// <param name="Breakdown">What the index's active bytes actually consist of.</param>
/// <param name="Mode">The size-management strategy in force.</param>
/// <param name="MaxAutoCompactionSizeMB">The automatic full-compaction cap (0 = uncapped).</param>
/// <param name="HasEligibleIncrementalRun">Whether a bounded incremental merge could still run.</param>
/// <param name="MinimumRunLength">Fewest neighbouring layers a bounded merge is allowed to combine.</param>
public readonly record struct IndexReclamationDiagnosis(
    bool CleanupDue,
    bool ReclamationBlocked,
    ActiveLayerStorageBreakdown Breakdown,
    string Mode,
    int MaxAutoCompactionSizeMB,
    bool HasEligibleIncrementalRun,
    int MinimumRunLength)
{
    /// <summary>Nothing about this index's size needs attention.</summary>
    public static IndexReclamationDiagnosis Healthy { get; } = new(
        CleanupDue: false, ReclamationBlocked: false, default,
        IndexSizeManagementModes.CoalesceThenCompact, 0, HasEligibleIncrementalRun: false, MinimumRunLength: 0);

    /// <summary>Accumulated update history in MB — the only part a compaction can actually reclaim.</summary>
    public int IncrementalHistoryMB => (int)(Breakdown.IncrementalHistoryBytes / (1024 * 1024));

    /// <summary>Total active size in MB.</summary>
    public int TotalMB => (int)(Breakdown.TotalBytes / (1024 * 1024));

    /// <summary>Plain-language explanation of the state and what it means for searches.</summary>
    public string Explain()
    {
        if (!CleanupDue)
            return "This index does not need any size clean-up.";

        string shape = $"It is now {TotalMB:N0} MB in total, of which {IncrementalHistoryMB:N0} MB is "
            + $"accumulated update history spread over {Breakdown.IncrementalCount:N0} layer(s).";

        return ReclamationBlocked
            ? "This index keeps being updated, but Yagu can no longer reclaim the history those updates "
                + "leave behind, so it will keep growing.\n\n" + shape
                + "\n\nYour searches stay complete either way — anything the index does not cover is read "
                + "directly from disk."
            : "This index has accumulated enough update history to be worth cleaning up.\n\n" + shape;
    }

    /// <summary>Why the automatic clean-up cannot reclaim the accumulated history.</summary>
    public string ExplainWhyAutomaticCleanupIsUnavailable()
    {
        if (!ReclamationBlocked)
            return string.Empty;
        if (Mode == IndexSizeManagementModes.Off)
            return "Automatic clean-up is turned off for this index, so nothing reclaims its space.";

        string merging;
        if (HasEligibleIncrementalRun)
            merging = string.Empty;
        else if (MinimumRunLength > 0 && Breakdown.IncrementalCount < MinimumRunLength)
        {
            merging = $"It has {Breakdown.IncrementalCount:N0} update layer(s), fewer than the "
                + $"{MinimumRunLength:N0} neighbouring layers a merge is allowed to combine. ";
        }
        else
        {
            merging = "Its update layers are individually larger than the merge limits you set, so the "
                + "bounded automatic merge has nothing it is allowed to combine. ";
        }

        string compacting = MaxAutoCompactionSizeMB > 0
            ? $"Folding the whole index into one file would work, but that only happens automatically below "
                + $"{MaxAutoCompactionSizeMB:N0} MB."
            : "Folding the whole index into one file is not permitted by this index's clean-up mode.";
        return merging + compacting;
    }

    private static readonly IndexSizeAttentionRemedy[] NoRemedies = [];

    private static readonly IndexSizeAttentionRemedy[] CleanupRemedies =
    [
        IndexSizeAttentionRemedy.CompactNow,
        IndexSizeAttentionRemedy.Rebuild,
        IndexSizeAttentionRemedy.Delete,
        IndexSizeAttentionRemedy.ReviewSizeSettings,
    ];

    /// <summary>The remedies worth offering, best first.</summary>
    public IReadOnlyList<IndexSizeAttentionRemedy> Remedies() => CleanupDue ? CleanupRemedies : NoRemedies;
}

/// <summary>
/// Decides, from what an index is actually made of, whether its accumulated update history needs
/// clean-up and whether every allowed automatic path to reclaim it is unavailable.
/// <para>
/// It deliberately reasons about the <b>incremental cohort only</b>. Full-build paging layers are disjoint
/// parts of one build: merging them reclaims nothing, so counting them would raise a false alarm and
/// recommend expensive work with no benefit.
/// </para>
/// </summary>
public static class IndexReclamationAdvisor
{
    /// <summary>
    /// Diagnoses one index. <paramref name="hasEligibleIncrementalRun"/> comes from the same bounded
    /// selector the maintenance pass uses, so "blocked" means the exact runs maintenance would attempt do
    /// not exist — not an estimate.
    /// </summary>
    public static IndexReclamationDiagnosis Diagnose(
        ActiveLayerStorageBreakdown breakdown,
        EffectiveIndexSizePolicy policy,
        int maxDeltaSegments,
        int compactionThresholdMB,
        bool hasEligibleIncrementalRun)
    {
        bool overLayerCount = breakdown.IncrementalCount > Math.Max(1, maxDeltaSegments);
        bool overBytes = breakdown.IncrementalBytes > (long)Math.Max(1, compactionThresholdMB) * 1024 * 1024;
        bool cleanupDue = overLayerCount || overBytes;
        if (!cleanupDue)
            return IndexReclamationDiagnosis.Healthy;

        bool mergeAvailable = policy.AllowsCoalescing && hasEligibleIncrementalRun;
        bool compactionAvailable = policy.AllowsCompactingIndexOf(breakdown.TotalBytes);
        bool blocked = !mergeAvailable && !compactionAvailable;

        return new IndexReclamationDiagnosis(
            CleanupDue: true,
            ReclamationBlocked: blocked,
            breakdown,
            policy.Mode,
            policy.MaxAutoCompactionSizeMB,
            hasEligibleIncrementalRun,
            policy.CoalesceMinRun);
    }

    /// <summary>Short status line for the index-health overview.</summary>
    public static string HealthStatus(IndexReclamationDiagnosis diagnosis)
        => $"still updating, but {diagnosis.IncrementalHistoryMB:N0} MB of accumulated update history "
            + "cannot be reclaimed automatically; searches stay complete";

    /// <summary>Button label for one remedy.</summary>
    public static string RemedyLabel(IndexSizeAttentionRemedy remedy) => remedy switch
    {
        IndexSizeAttentionRemedy.CompactNow => "Compact now",
        IndexSizeAttentionRemedy.Rebuild => "Rebuild this index",
        IndexSizeAttentionRemedy.Delete => "Delete this index",
        IndexSizeAttentionRemedy.ReviewSizeSettings => "Review size settings",
        _ => remedy.ToString(),
    };

    /// <summary>What one remedy will do, in plain language.</summary>
    public static string RemedyDescription(IndexSizeAttentionRemedy remedy, IndexReclamationDiagnosis diagnosis) => remedy switch
    {
        IndexSizeAttentionRemedy.CompactNow =>
            $"Recommended. Folds every layer into one file, reclaiming most of the {diagnosis.IncrementalHistoryMB:N0} MB "
            + "of update history. It runs in the background worker and reads and writes the whole index once, so it "
            + "takes a while on a large index. Your current index keeps working until the new one is ready.",
        IndexSizeAttentionRemedy.Rebuild =>
            "Reads your files again and writes a fresh index. This also removes the update history, but it costs a "
            + "full re-read of every file and the result is still as large as the content it covers.",
        IndexSizeAttentionRemedy.Delete =>
            "Frees all of the space now. Searches still find everything by reading files directly, but they lose "
            + "this index's speed-up until you build it again.",
        IndexSizeAttentionRemedy.ReviewSizeSettings =>
            "Opens the index size settings so you can change how large a layer may be, how much one clean-up pass "
            + "may merge, and when Yagu is allowed to compact automatically.",
        _ => string.Empty,
    };
}
