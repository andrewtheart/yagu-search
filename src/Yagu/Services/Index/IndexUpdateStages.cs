namespace Yagu.Services.Index;

/// <summary>
/// Progress stage names an incremental (delta) index update reports alongside its percent estimate.
/// Every value keeps the <see cref="Incremental"/> prefix so the existing stage contract still holds:
/// any stage starting with it means "updating an existing index", never a full rebuild.
///
/// The phases exist because publishing a delta is not a quick finalization step. Resolving journal
/// changes, merging them into a segment, serializing that segment, and any follow-up coalescing are all
/// separately expensive on a large index, and lumping them together left the UI on one static label for
/// the majority of the update.
/// </summary>
public static class IndexUpdateStages
{
    /// <summary>Unphased incremental update (kept for older workers and generic callers).</summary>
    public const string Incremental = "incremental";

    /// <summary>Mapping journal records to paths and classifying changed file content.</summary>
    public const string Resolving = "incremental.resolving";

    /// <summary>Folding resolved changes and tombstones into the pending delta segment.</summary>
    public const string Merging = "incremental.merging";

    /// <summary>Serializing the delta segment.</summary>
    public const string Writing = "incremental.writing";

    /// <summary>Atomically extending the active pointer with the new segment.</summary>
    public const string Publishing = "incremental.publishing";

    /// <summary>Coalescing or compacting layers after the delta was durably appended.</summary>
    public const string Compacting = "incremental.compacting";

    /// <summary>True when the stage denotes an update to an existing index rather than a full build.</summary>
    public static bool IsIncremental(string? stage)
        => stage is not null && stage.StartsWith(Incremental, StringComparison.OrdinalIgnoreCase);

    /// <summary>Percent boundaries so each phase advances a distinct, monotonic slice of the bar.</summary>
    public const int ResolveCeiling = 60;
    public const int MergeFloor = ResolveCeiling;
    public const int MergeCeiling = 88;
    public const int WriteFloor = MergeCeiling;
    public const int PublishFloor = 94;
    public const int CompactFloor = 97;
}
