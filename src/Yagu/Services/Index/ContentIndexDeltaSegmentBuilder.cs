namespace Yagu.Services.Index;

/// <summary>
/// Builds an immutable <see cref="ContentIndexDeltaSegment"/> from the changes a USN pass proved since the
/// previous layer was built (plan §3.4/§11.4). The parent (worker/app) walks the dirty set and feeds each
/// change here:
/// <list type="bullet">
/// <item><see cref="AddChangedDocument"/> — a created or modified file. Its new bytes are classified; if
/// admitted it becomes a fresh document in this segment (shadowing any older layer's entry). If the new
/// content is <em>not</em> admissible (e.g. it turned binary or over-cap), the path is <b>tombstoned</b>
/// instead, so an older layer's now-stale entry can never prune it.</item>
/// <item><see cref="AddTombstone"/> — a deleted (or renamed-away) file. The path is tombstoned so older
/// layers stop classifying it; a path rediscovered later is treated as changed → live-scanned.</item>
/// </list>
/// The result is byte-parity with a segment read back from disk (the <see cref="Added"/> part reuses the
/// base generation builder), so a compaction that folds segments into a new base is deterministic.
/// </summary>
public sealed class ContentIndexDeltaSegmentBuilder
{
    private readonly ContentIndexGenerationBuilder _added;
    private readonly HashSet<string> _tombstones = new(StringComparer.Ordinal);

    public ContentIndexDeltaSegmentBuilder(
        IndexIngestionPolicy policy,
        IndexBuildReport? report = null,
        Func<string, FileIdentity?>? identityProvider = null)
    {
        _added = new ContentIndexGenerationBuilder(policy, report, identityProvider);
    }

    /// <summary>The accumulating build report (shared with the underlying added-documents builder).</summary>
    public IndexBuildReport Report => _added.Report;

    /// <summary>The number of paths tombstoned so far.</summary>
    public int TombstoneCount => _tombstones.Count;

    /// <summary>Seeds the containing scope's known volume serial. Incremental maintenance calls this from
    /// the active base manifest so a segment remains volume-compatible even when every changed file's
    /// same-handle identity read is unavailable (those individual files still live-scan safely).</summary>
    public void SeedVolumeSerialNumber(ulong volumeSerialNumber)
        => _added.SeedVolumeSerialNumber(volumeSerialNumber);

    /// <summary>Copies the persisted mounted-volume binding into the new segment.</summary>
    public void SeedVolumeBinding(VolumeBinding volumeBinding)
        => _added.SeedVolumeBinding(volumeBinding);

    /// <summary>
    /// Records a created/modified file. Returns the assigned segment-local content id, or <c>-1</c> when
    /// the new content is not admissible — in which case the path is tombstoned so a stale older-layer
    /// entry cannot shadow-prune it.
    /// </summary>
    public long AddChangedDocument(string path, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        long contentId = _added.AddDocument(path, content);
        if (contentId < 0)
        {
            // Not admitted now (binary / over-cap / unsupported): tombstone so no older layer prunes it.
            _tombstones.Add(IndexScopeIdentity.NormalizePath(path));
        }
        else
        {
            // If a prior AddTombstone marked this path (e.g. delete-then-recreate within one pass), the
            // live add wins — drop the tombstone so the fresh content is authoritative.
            _tombstones.Remove(IndexScopeIdentity.NormalizePath(path));
        }
        return contentId;
    }

    /// <summary>Records a deleted / renamed-away path as a tombstone.</summary>
    public void AddTombstone(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _tombstones.Add(IndexScopeIdentity.NormalizePath(path));
    }

    /// <summary>
    /// Records a created/modified file that was <b>already classified</b> by the content reader (plan §5.4),
    /// with its identity captured from the same read handle — no re-read and no second identity open. Returns
    /// the assigned segment-local content id, or <c>-1</c> when the new content is not admissible, in which
    /// case the path is tombstoned so a stale older-layer entry cannot shadow-prune it.
    /// </summary>
    public long AddChangedClassified(string path, IndexContentClassification classification, FileIdentity? identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string normalized = IndexScopeIdentity.NormalizePath(path);
        long contentId = _added.AddClassifiedContent(path, classification, identity);
        if (contentId < 0)
            _tombstones.Add(normalized); // not admitted now → tombstone so no older layer prunes it
        else
            _tombstones.Remove(normalized); // a live add supersedes any earlier tombstone of the same path
        return contentId;
    }

    /// <summary>Finalizes the immutable delta segment (its added generation + the tombstone set).</summary>
    public ContentIndexDeltaSegment Build(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        UsnCheckpoint checkpoint,
        DateTimeOffset builtUtc)
    {
        ContentIndexGeneration added = _added.Build(
            scopeId,
            volumeIdentity,
            normalizedRootPath,
            checkpoint,
            builtUtc,
            lastIncrementalUpdateUtc: builtUtc);
        return new ContentIndexDeltaSegment(added, _tombstones);
    }
}
