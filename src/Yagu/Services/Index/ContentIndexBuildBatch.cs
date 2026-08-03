namespace Yagu.Services.Index;

/// <summary>
/// A <b>persistence-only</b> build batch (plan §5.5): exactly the four things
/// <see cref="ContentIndexGenerationSerializer"/> writes to disk — the manifest, the per-document trigram
/// sets, the alias table, and the per-content durable identities — plus the diagnostic report. It
/// deliberately carries <b>no inverted <see cref="TrigramPostingIndex"/></b>, because <c>content.bin</c>
/// stores per-document trigrams and the full-build serializer never persists postings. Producing this
/// from <see cref="ContentIndexGenerationBuilder.BuildForPersistence"/> instead of a queryable
/// <see cref="ContentIndexGeneration"/> lets a paged full build skip building (and immediately discarding)
/// a posting index at every batch flush. The queryable <see cref="ContentIndexGeneration"/> remains the
/// path for tests, the managed query oracle, and compaction.
/// </summary>
internal sealed class ContentIndexBuildBatch
{
    public ContentIndexBuildBatch(
        IndexManifest manifest,
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents,
        IReadOnlyDictionary<string, (long AliasId, long ContentId)> aliases,
        IReadOnlyList<UsnFileIdentity?> contentIdentities,
        IndexBuildReport report)
    {
        Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        Documents = documents ?? throw new ArgumentNullException(nameof(documents));
        Aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));
        ContentIdentities = contentIdentities ?? throw new ArgumentNullException(nameof(contentIdentities));
        Report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The generation manifest (versions, scope identity, freshness checkpoint, counts).</summary>
    public IndexManifest Manifest { get; }

    /// <summary>Per-content-id distinct trigram sets (content id = list index) — persisted to <c>content.bin</c>.</summary>
    public IReadOnlyList<IReadOnlyCollection<Trigram>> Documents { get; }

    /// <summary>The alias table: normalized path → (alias id, content id) — persisted to <c>aliases.bin</c>.</summary>
    public IReadOnlyDictionary<string, (long AliasId, long ContentId)> Aliases { get; }

    /// <summary>Per-content-id durable identity (null when uncaptured) — persisted to <c>fileids.bin</c>.</summary>
    public IReadOnlyList<UsnFileIdentity?> ContentIdentities { get; }

    /// <summary>The accumulating build report (diagnostics only; not persisted).</summary>
    public IndexBuildReport Report { get; }
}

/// <summary>
/// The persistence-only analogue of <see cref="ContentIndexDeltaSegment"/> for paged full builds (plan
/// §5.5): a <see cref="ContentIndexBuildBatch"/> plus its tombstoned paths. Paged full-build batches carry
/// an empty removed-path set (pure additions), exactly as the queryable segment path did — so the on-disk
/// segment format is byte-identical whether written from a batch or a generation.
/// </summary>
internal sealed class ContentIndexDeltaSegmentBatch
{
    private readonly HashSet<string> _removedPaths;

    public ContentIndexDeltaSegmentBatch(ContentIndexBuildBatch added, IReadOnlyCollection<string> removedPaths)
    {
        Added = added ?? throw new ArgumentNullException(nameof(added));
        ArgumentNullException.ThrowIfNull(removedPaths);
        _removedPaths = new HashSet<string>(removedPaths, StringComparer.Ordinal);
    }

    /// <summary>The added / replaced documents for this segment (a self-contained persistence batch).</summary>
    public ContentIndexBuildBatch Added { get; }

    /// <summary>The normalized paths deleted in this segment (empty for a paged full-build batch).</summary>
    public IReadOnlySet<string> RemovedPaths => _removedPaths;
}
