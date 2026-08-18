namespace Yagu.Services.Index;

/// <summary>
/// The size-management strategies an index can use. Worker-safe: this file is linked into
/// <c>Yagu.IndexWorker</c>, so it must not reference <c>AppSettings</c> or anything else app-only.
/// </summary>
public static class IndexSizeManagementModes
{
    /// <summary>Never reorganize automatically. The index grows until an explicit rebuild.</summary>
    public const string Off = "Off";

    /// <summary>Only merge bounded contiguous runs of small segments. Never folds the base, so memory stays low.</summary>
    public const string Coalesce = "Coalesce";

    /// <summary>Only fold the whole layered index into a fresh base once it is over its segment/size bounds.</summary>
    public const string Compact = "Compact";

    /// <summary>Coalesce small runs first, then fold the remainder if the index is still over its bounds.</summary>
    public const string CoalesceThenCompact = "CoalesceThenCompact";

    /// <summary>Every accepted mode, in presentation order.</summary>
    public static readonly string[] All = [Off, Coalesce, Compact, CoalesceThenCompact];

    /// <summary>Coerces <paramref name="value"/> to a known mode (case-insensitively); unknown/blank becomes <see cref="CoalesceThenCompact"/>.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return CoalesceThenCompact;
        foreach (string mode in All)
        {
            if (string.Equals(mode, value.Trim(), StringComparison.OrdinalIgnoreCase))
                return mode;
        }
        return CoalesceThenCompact;
    }
}

/// <summary>
/// One root's fully-resolved size-management settings (produced app-side by
/// <see cref="IndexSizeManagementPolicy.Resolve"/> and carried into the maintenance worker).
/// <para>
/// Yagu's index is append-mostly: every incremental refresh publishes an immutable delta segment, and only
/// <b>coalescing</b> (merging a bounded contiguous run of small segments) or <b>compaction</b> (folding
/// every layer into a fresh base) ever gives storage back. This decides which of those an index may use,
/// and when it has hit its storage ceiling.
/// </para>
/// <para>
/// None of this can change search results. Leaving an index segmented, or halting its maintenance at the
/// budget, only reduces how much it can prune; anything it cannot prove safe to skip is still read live.
/// </para>
/// </summary>
/// <param name="Mode">The resolved strategy, one of <see cref="IndexSizeManagementModes.All"/>.</param>
/// <param name="SizeBudgetMB">Storage ceiling for this index in MB; 0 = no ceiling.</param>
/// <param name="MaxAutoCompactionSizeMB">Routine automatic fold cap while bounded coalescing can progress;
/// compact-capable modes may stream-compact above it when no bounded merge can run. 0 = no cap.</param>
/// <param name="CoalesceMaxSegmentMB">Largest individual segment eligible to join a coalescing run.</param>
/// <param name="CoalesceMaxBatchMB">Largest total size of one coalescing run.</param>
/// <param name="CoalesceMinRun">Fewest contiguous eligible segments that make a run worth merging.</param>
/// <param name="CoalesceMaxRunsPerPass">Most runs merged in a single maintenance pass.</param>
public readonly record struct EffectiveIndexSizePolicy(
    string Mode,
    int SizeBudgetMB,
    int MaxAutoCompactionSizeMB,
    int CoalesceMaxSegmentMB,
    int CoalesceMaxBatchMB,
    int CoalesceMinRun,
    int CoalesceMaxRunsPerPass)
{
    /// <summary>Largest contiguous run the coalescer will consider merging at once.</summary>
    public const int MaximumCoalesceRun = 32;

    /// <summary>The built-in defaults, used by callers that have no settings (tests, internal helpers).</summary>
    public static EffectiveIndexSizePolicy Default { get; } = new(
        IndexSizeManagementModes.CoalesceThenCompact,
        SizeBudgetMB: 0,
        MaxAutoCompactionSizeMB: 8192,
        CoalesceMaxSegmentMB: 1024,
        CoalesceMaxBatchMB: 4096,
        CoalesceMinRun: 3,
        CoalesceMaxRunsPerPass: 8);

    /// <summary>True when this index may merge bounded contiguous runs of small segments.</summary>
    public bool AllowsCoalescing
        => Mode is IndexSizeManagementModes.Coalesce or IndexSizeManagementModes.CoalesceThenCompact;

    /// <summary>True when this index may fold every layer into a fresh base.</summary>
    public bool AllowsCompaction
        => Mode is IndexSizeManagementModes.Compact or IndexSizeManagementModes.CoalesceThenCompact;

    /// <summary><see cref="CoalesceMaxSegmentMB"/> in bytes.</summary>
    public long CoalesceMaxSegmentBytes => (long)CoalesceMaxSegmentMB * 1024 * 1024;

    /// <summary><see cref="CoalesceMaxBatchMB"/> in bytes.</summary>
    public long CoalesceMaxBatchBytes => (long)CoalesceMaxBatchMB * 1024 * 1024;

    /// <summary>True when <paramref name="activeIndexBytes"/> is over this index's storage ceiling. A zero budget disables the ceiling.</summary>
    public bool ExceedsBudget(long activeIndexBytes)
        => SizeBudgetMB > 0 && activeIndexBytes > (long)SizeBudgetMB * 1024 * 1024;

    /// <summary>
    /// True when an index of <paramref name="activeIndexBytes"/> may be folded in one automatic compaction.
    /// A zero cap removes the limit.
    /// <para>
    /// Being over the storage budget deliberately does <b>not</b> lift this cap. The fold is memory-bounded,
    /// but a large automatic pass still consumes substantial I/O and temporary disk space. An index that
    /// cannot be compacted within its cap is instead held at the budget by <see cref="ExceedsBudget"/> and
    /// reclaimed by an explicitly approved compaction or rebuild.
    /// </para>
    /// </summary>
    public bool AllowsCompactingIndexOf(long activeIndexBytes)
    {
        if (!AllowsCompaction)
            return false;
        if (MaxAutoCompactionSizeMB <= 0)
            return true;
        return activeIndexBytes <= (long)MaxAutoCompactionSizeMB * 1024 * 1024;
    }

    /// <summary>
    /// True when an automatic pass may compact now. The configured size cap still defers routine full
    /// compaction while bounded coalescing is making progress. When no bounded merge can run, streaming
    /// compaction becomes the fallback for modes that allow it, regardless of size; otherwise the index
    /// would grow indefinitely even though its worker-isolated, bounded-memory cleanup path is available.
    /// </summary>
    public bool AllowsAutomaticCompactionOf(long activeIndexBytes, bool boundedMergeCanProgress)
        => AllowsCompactingIndexOf(activeIndexBytes)
            || (AllowsCompaction && !boundedMergeCanProgress);
}
