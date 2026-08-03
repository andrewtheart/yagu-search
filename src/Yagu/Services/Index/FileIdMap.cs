namespace Yagu.Services.Index;

/// <summary>
/// The generation's <c>fileids.bin</c> in memory (plan §3.4/§3.5): a volume-scoped map from a file's
/// durable <see cref="UsnFileIdentity"/> (normally the V2-compatible 64-bit reference in Low) to its
/// content id. It is the bridge
/// that lets name-less USN change records be resolved back to indexed content: a journal change reports a
/// file id, and this map turns it into the content id to mark dirty. Hard links to one content share a
/// single file id, so they map to one content id. Changes to files not in the index are simply ignored.
/// </summary>
public sealed class FileIdMap
{
    private readonly Dictionary<UsnFileIdentity, long> _byFileId = new();
    private bool _hasExtendedIdentities;

    /// <summary>The volume this map belongs to. A generation covers exactly one volume (plan §3.6).</summary>
    public ulong VolumeSerialNumber { get; }

    public FileIdMap(ulong volumeSerialNumber)
    {
        VolumeSerialNumber = volumeSerialNumber;
    }

    /// <summary>Number of distinct file identities in the map.</summary>
    public int Count => _byFileId.Count;

    /// <summary>True when this layer was built by the older ReFS identity scheme that persisted a true
    /// FILE_ID_128 (High != 0) while unprivileged journal reads reported incompatible V2 references.</summary>
    internal bool HasExtendedIdentities => _hasExtendedIdentities;

    /// <summary>
    /// Records that <paramref name="fileId"/> is the identity of content <paramref name="contentId"/>.
    /// Recording the same id again (e.g. a second hard-link alias of the same content) is idempotent.
    /// </summary>
    public void Add(long contentId, UsnFileIdentity fileId)
    {
        _byFileId[fileId] = contentId;
        _hasExtendedIdentities |= fileId.High != 0;
    }

    /// <summary>Resolves a file identity to its content id, or false when it is not indexed.</summary>
    public bool TryGetContentId(UsnFileIdentity fileId, out long contentId)
        => _byFileId.TryGetValue(fileId, out contentId);

    /// <summary>Merges identities from another layer into an aggregate freshness-only map, assigning
    /// synthetic content ids from <paramref name="nextContentId"/>. Used by lightweight layered-index
    /// staleness checks, where only "did any active-layer identity change?" matters and layer-local content
    /// ids are not globally unique.</summary>
    internal void MergeIdentitiesFrom(FileIdMap other, ref long nextContentId)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (UsnFileIdentity identity in other._byFileId.Keys)
            Add(nextContentId++, identity);
    }

    /// <summary>
    /// Marks every indexed content whose file identity appears in <paramref name="changes"/> as dirty
    /// (plan §3.5). Changes to files not in the index are ignored. Returns the number of content ids newly
    /// resolved from the changes (a content marked from two changes counts once per resolved change, but
    /// <see cref="DirtyContentSet"/> stays a monotonic set).
    /// </summary>
    public int ResolveDirty(IEnumerable<UsnChange> changes, DirtyContentSet dirty)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(dirty);

        int resolved = 0;
        foreach (var change in changes)
        {
            if (_byFileId.TryGetValue(change.Identity, out long contentId))
            {
                dirty.MarkDirty(contentId);
                resolved++;
            }
        }
        return resolved;
    }
}
