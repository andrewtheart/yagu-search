namespace Yagu.Services.Index;

/// <summary>
/// One remediation the user can apply when an index has reached its storage budget.
/// </summary>
public enum IndexSizeBudgetRemedy
{
    /// <summary>Rebuild the index from scratch: collapses every accumulated layer into one small base.</summary>
    Rebuild,

    /// <summary>Raise this index's storage budget so maintenance resumes at the current size.</summary>
    RaiseBudget,

    /// <summary>Let this index be compacted regardless of size, folding its layers into a fresh base.</summary>
    AllowCompaction,

    /// <summary>Delete the stored index, freeing all of its space; searches read every file live.</summary>
    Delete,
}

/// <summary>
/// Why one index stopped updating, in terms a user can act on, plus which remedies apply.
/// </summary>
/// <param name="AtBudget">True when this index is at or over its storage budget.</param>
/// <param name="ActiveBytes">Current size of the index's active layers.</param>
/// <param name="BudgetMB">The storage budget being enforced.</param>
/// <param name="Mode">The size-management strategy in force.</param>
/// <param name="CompactionBlockedByCap">True when compaction could reclaim space but the auto-compaction cap forbids it at this size.</param>
/// <param name="MaxAutoCompactionSizeMB">The cap that is blocking compaction (0 = uncapped).</param>
public readonly record struct IndexSizeBudgetDiagnosis(
    bool AtBudget,
    long ActiveBytes,
    int BudgetMB,
    string Mode,
    bool CompactionBlockedByCap,
    int MaxAutoCompactionSizeMB)
{
    /// <summary>Nothing is wrong with this index's size.</summary>
    public static IndexSizeBudgetDiagnosis Healthy { get; } = new(
        AtBudget: false, ActiveBytes: 0, BudgetMB: 0,
        IndexSizeManagementModes.CoalesceThenCompact, CompactionBlockedByCap: false, MaxAutoCompactionSizeMB: 0);

    /// <summary>Current size in MB, for display next to the budget.</summary>
    public int ActiveMB => (int)(ActiveBytes / (1024 * 1024));

    /// <summary>
    /// A budget that clears the current size with room to keep working, rounded up to a whole GB so the
    /// user is not immediately back at the ceiling after a few updates.
    /// </summary>
    public int SuggestedBudgetMB
    {
        get
        {
            const int gb = 1024;
            long wanted = (long)(ActiveMB * 1.5) + gb;
            long rounded = ((wanted + gb - 1) / gb) * gb;
            return (int)Math.Clamp(rounded, ActiveMB + gb, int.MaxValue);
        }
    }

    /// <summary>Plain-language explanation of what happened and what it means for searches.</summary>
    public string Explain()
    {
        if (!AtBudget)
            return "This index is within its size budget.";

        return $"This index has reached the {BudgetMB:N0} MB limit you set for it (it is now {ActiveMB:N0} MB), "
            + "so Yagu has stopped adding to it rather than letting it grow without limit.\n\n"
            + "Your searches are still complete — anything the index no longer covers is read directly from "
            + "disk, so no match is missed. But the index is no longer being kept up to date, so searches "
            + "will gradually get slower until you deal with this.";
    }

    /// <summary>Why the automatic clean-up could not get the index back under its budget.</summary>
    public string ExplainWhyAutomaticCleanupFailed()
    {
        if (!AtBudget)
            return string.Empty;

        if (Mode == IndexSizeManagementModes.Off)
            return "Automatic clean-up is turned off for this index, so nothing reclaims its space.";

        string merging = "Merging small updates together only removes the overhead of having many layers, "
            + "not the indexed content itself, so it cannot bring a large index back under its budget.";

        if (!CompactionBlockedByCap)
            return merging;

        return merging
            + $" Rebuilding the whole index into one compact file would work, but that is only done "
            + $"automatically below {MaxAutoCompactionSizeMB:N0} MB because it briefly needs a lot of memory.";
    }

    /// <summary>The remedies worth offering, best first.</summary>
    public IReadOnlyList<IndexSizeBudgetRemedy> Remedies()
    {
        if (!AtBudget)
            return [];

        var remedies = new List<IndexSizeBudgetRemedy> { IndexSizeBudgetRemedy.Rebuild, IndexSizeBudgetRemedy.RaiseBudget };
        if (CompactionBlockedByCap)
            remedies.Add(IndexSizeBudgetRemedy.AllowCompaction);
        remedies.Add(IndexSizeBudgetRemedy.Delete);
        return remedies;
    }
}

/// <summary>
/// Detects the "index stopped updating because it hit its storage budget" state and describes it in
/// user-facing terms. Pure: the caller measures the index, so the wording and the remedy choice are
/// unit testable without touching disk.
/// </summary>
public static class IndexSizeBudgetAdvisor
{
    /// <summary>
    /// Diagnoses one index. Returns <see cref="IndexSizeBudgetDiagnosis.Healthy"/> unless the index is
    /// over its budget <b>and</b> its size-management mode cannot bring it back under, which is exactly
    /// the condition where maintenance stops.
    /// </summary>
    public static IndexSizeBudgetDiagnosis Diagnose(EffectiveIndexSizePolicy policy, long activeBytes)
    {
        if (activeBytes <= 0 || !policy.ExceedsBudget(activeBytes) || policy.AllowsCompactingIndexOf(activeBytes))
            return IndexSizeBudgetDiagnosis.Healthy;

        return new IndexSizeBudgetDiagnosis(
            AtBudget: true,
            activeBytes,
            policy.SizeBudgetMB,
            policy.Mode,
            // Compaction is the one thing that could still reclaim, so say so only when the cap is what stops it.
            CompactionBlockedByCap: policy.AllowsCompaction && policy.MaxAutoCompactionSizeMB > 0,
            policy.MaxAutoCompactionSizeMB);
    }

    /// <summary>Short status line for the index-health overview.</summary>
    public static string HealthStatus(IndexSizeBudgetDiagnosis diagnosis)
        => $"updates paused — reached its {diagnosis.BudgetMB:N0} MB size limit (now {diagnosis.ActiveMB:N0} MB); "
            + "searches stay complete but this index is no longer kept up to date";

    /// <summary>Button label for one remedy.</summary>
    public static string RemedyLabel(IndexSizeBudgetRemedy remedy) => remedy switch
    {
        IndexSizeBudgetRemedy.Rebuild => "Rebuild this index",
        IndexSizeBudgetRemedy.RaiseBudget => "Raise the limit",
        IndexSizeBudgetRemedy.AllowCompaction => "Compact it instead",
        IndexSizeBudgetRemedy.Delete => "Delete this index",
        _ => remedy.ToString(),
    };

    /// <summary>What one remedy will do, in plain language.</summary>
    public static string RemedyDescription(IndexSizeBudgetRemedy remedy, IndexSizeBudgetDiagnosis diagnosis) => remedy switch
    {
        IndexSizeBudgetRemedy.Rebuild =>
            "Recommended. Builds the index again from scratch, which frees almost all of the space and "
            + "starts keeping it up to date again. Your current index keeps working until the new one is ready.",
        IndexSizeBudgetRemedy.RaiseBudget =>
            $"Raises this index's limit to {diagnosis.SuggestedBudgetMB:N0} MB so it starts updating again "
            + "right away. It keeps the space it already uses and will grow further.",
        IndexSizeBudgetRemedy.AllowCompaction =>
            "Lets Yagu compact this index in place at its next update, which reclaims the space without a "
            + "full rebuild. Compacting a large index briefly uses a lot of memory.",
        IndexSizeBudgetRemedy.Delete =>
            "Frees all of the space now. Searches still find everything by reading files directly, but "
            + "they lose this index's speed-up until you build it again.",
        _ => string.Empty,
    };
}
