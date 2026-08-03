namespace Yagu.Services.Index;

/// <summary>
/// A USN journal checkpoint (plan §3.5): the journal identity and the next-USN cursor captured at a
/// point in time. Intervals are half-open <c>[Start, End)</c>. A change of <see cref="JournalId"/>
/// means the journal was deleted/recreated and continuity is lost.
/// </summary>
public readonly record struct UsnCheckpoint(ulong JournalId, long NextUsn)
{
    /// <summary>The empty checkpoint (no journal captured).</summary>
    public static readonly UsnCheckpoint None = new(0UL, 0L);

    /// <summary>True when this checkpoint continues <paramref name="earlier"/>: same journal and a
    /// non-decreasing cursor (a decreasing cursor implies wrap/gap).</summary>
    public bool ContinuesFrom(UsnCheckpoint earlier)
        => JournalId == earlier.JournalId && NextUsn >= earlier.NextUsn;
}

/// <summary>
/// A monotonic set of dirty content ids (plan §3.5). Dirty state only ever accumulates for the life of
/// a generation — advancing a cursor never clears an earlier dirty content id, and <see cref="MergeFrom"/>
/// unions (never removes). This is the in-memory form of the persisted freshness-overlay dirty bitmap.
/// </summary>
public sealed class DirtyContentSet
{
    private readonly HashSet<long> _dirty = new();

    /// <summary>Number of distinct dirty content ids.</summary>
    public int Count => _dirty.Count;

    /// <summary>Marks a content id dirty. Idempotent and never reversible.</summary>
    public void MarkDirty(long contentId) => _dirty.Add(contentId);

    /// <summary>True when the content id has been marked dirty.</summary>
    public bool IsDirty(long contentId) => _dirty.Contains(contentId);

    /// <summary>Unions another dirty set into this one (monotonic merge — never removes bits).</summary>
    public void MergeFrom(DirtyContentSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        _dirty.UnionWith(other._dirty);
    }

    /// <summary>A snapshot of the current dirty content ids (for diagnostics/persistence).</summary>
    public IReadOnlySet<long> Snapshot() => new HashSet<long>(_dirty);
}
