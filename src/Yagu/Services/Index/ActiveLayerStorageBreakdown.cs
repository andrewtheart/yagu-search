namespace Yagu.Services.Index;

/// <summary>
/// The active index's on-disk bytes split by the cohort each layer actually belongs to.
/// <para>
/// A paged full build writes its later pages into the <b>segment</b> store, so "every segment is
/// accumulated update history" is wrong: those pages are disjoint parts of one build and collapsing
/// them reclaims nothing. Only layers with incremental provenance carry superseded records. Callers
/// that diagnose growth, choose a merge run, or estimate reclamation must reason on this split rather
/// than on <c>total - base</c>.
/// </para>
/// </summary>
/// <param name="BaseBytes">Bytes of the active base generation directory.</param>
/// <param name="BaseCount">1 when a trusted base is present.</param>
/// <param name="FullBuildPagingBytes">Bytes of active segments that are pages of the base's own build.</param>
/// <param name="FullBuildPagingCount">Number of full-build paging layers.</param>
/// <param name="IncrementalBytes">Bytes of active segments produced by incremental updates.</param>
/// <param name="IncrementalCount">Number of incremental layers.</param>
public readonly record struct ActiveLayerStorageBreakdown(
    long BaseBytes,
    int BaseCount,
    long FullBuildPagingBytes,
    int FullBuildPagingCount,
    long IncrementalBytes,
    int IncrementalCount)
{
    /// <summary>Total bytes of every active layer.</summary>
    public long TotalBytes => BaseBytes + FullBuildPagingBytes + IncrementalBytes;

    /// <summary>Total number of active layers, including the base.</summary>
    public int TotalCount => BaseCount + FullBuildPagingCount + IncrementalCount;

    /// <summary>Number of active delta segments, whichever cohort they belong to.</summary>
    public int SegmentCount => FullBuildPagingCount + IncrementalCount;

    /// <summary>Bytes an ideal full compaction could reclaim at best: the accumulated update history.</summary>
    public long IncrementalHistoryBytes => IncrementalBytes;
}

/// <summary>
/// A cheap time series summary for the active update-history cohort. The timestamps come from active
/// incremental manifests only; full-build paging layers are deliberately excluded for the same reason
/// their bytes are excluded from reclamation estimates.
/// </summary>
/// <param name="Breakdown">Current active-layer storage cohorts.</param>
/// <param name="OldestIncrementalBuiltUtc">Build time of the oldest active incremental layer.</param>
/// <param name="NewestIncrementalBuiltUtc">Build time of the newest active incremental layer.</param>
public readonly record struct ActiveLayerStorageTrend(
    ActiveLayerStorageBreakdown Breakdown,
    DateTimeOffset? OldestIncrementalBuiltUtc,
    DateTimeOffset? NewestIncrementalBuiltUtc);
