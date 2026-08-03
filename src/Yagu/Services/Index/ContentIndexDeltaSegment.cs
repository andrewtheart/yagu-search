namespace Yagu.Services.Index;

/// <summary>
/// An immutable append-only delta segment (plan §3.4/§11.4): the incremental changes made to a scope
/// since the previous layer (the base generation or an older segment) was built. A segment carries two
/// things:
/// <list type="bullet">
/// <item><see cref="Added"/> — the added / replaced documents as a small self-contained generation
/// (its own segment-local content ids, aliases, durable identities, and freshness checkpoint). A
/// <em>replaced</em> file appears here with its new content; the newest layer that has a path wins, so
/// the older layer's entry is shadowed without an explicit tombstone.</item>
/// <item><see cref="RemovedPaths"/> — normalized paths that were <em>deleted</em> since the previous
/// layer. A tombstoned path stops older layers from classifying it (a rediscovered path is treated as
/// changed → live-scanned).</item>
/// </list>
/// Segments are queried newest-first over the base generation (see <see cref="LayeredContentIndexQuerySession"/>)
/// and periodically compacted into a fresh base once <see cref="AppSettings.IndexMaxDeltaSegments"/> or
/// <see cref="AppSettings.IndexCompactionThresholdMB"/> is exceeded.
/// </summary>
public sealed class ContentIndexDeltaSegment
{
    private readonly HashSet<string> _removedPaths;

    public ContentIndexDeltaSegment(ContentIndexGeneration added, IReadOnlyCollection<string> removedPaths)
    {
        Added = added ?? throw new ArgumentNullException(nameof(added));
        ArgumentNullException.ThrowIfNull(removedPaths);
        _removedPaths = new HashSet<string>(removedPaths, StringComparer.Ordinal);
    }

    /// <summary>The added / replaced documents for this segment (a self-contained generation).</summary>
    public ContentIndexGeneration Added { get; }

    /// <summary>The normalized paths deleted in this segment (queried before <see cref="Added"/>).</summary>
    public IReadOnlySet<string> RemovedPaths => _removedPaths;

    /// <summary>The USN checkpoint the segment was built at (its content is fresh as of this point).</summary>
    public UsnCheckpoint FreshnessCheckpoint => Added.Manifest.FreshnessCheckpoint;

    /// <summary>True when <paramref name="normalizedPath"/> was tombstoned by this segment.</summary>
    public bool IsRemoved(string normalizedPath) => _removedPaths.Contains(normalizedPath);
}
