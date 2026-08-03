namespace Yagu.Services.Index;

/// <summary>
/// An in-memory content-index generation (plan §3.4) — the managed <em>reference</em> that ties the
/// manifest, trigram posting index, and the alias table together. Production ultimately builds and
/// serves generations from the isolated Rust worker, but this reference has identical query semantics
/// and is the test/differential oracle. A document (content) is identified by a content id (its posting
/// document id here); each discovered path is a distinct alias mapping to a content id, so hard links
/// share one content/posting set but remain independent result paths (plan §3.6).
/// </summary>
public sealed class ContentIndexGeneration
{
    private readonly Dictionary<string, (long AliasId, long ContentId)> _aliasesByPath;
    private readonly IReadOnlyList<UsnFileIdentity?> _contentIdentities;

    internal ContentIndexGeneration(
        IndexManifest manifest,
        TrigramPostingIndex postings,
        Dictionary<string, (long AliasId, long ContentId)> aliasesByPath,
        IndexBuildReport report,
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents,
        IReadOnlyList<UsnFileIdentity?> contentIdentities)
    {
        Manifest = manifest;
        Postings = postings;
        _aliasesByPath = aliasesByPath;
        Report = report;
        Documents = documents;
        _contentIdentities = contentIdentities;
    }

    /// <summary>The generation manifest (versions, scope identity, freshness checkpoint, counts).</summary>
    public IndexManifest Manifest { get; }

    /// <summary>The trigram posting index over the generation's content documents.</summary>
    public TrigramPostingIndex Postings { get; }

    /// <summary>The typed build report (skip/fail reasons).</summary>
    public IndexBuildReport Report { get; }

    /// <summary>Per-content-id distinct trigram sets, retained for serialization (content id = list index).</summary>
    internal IReadOnlyList<IReadOnlyCollection<Trigram>> Documents { get; }

    /// <summary>The alias table: normalized path → (alias id, content id).</summary>
    internal IReadOnlyDictionary<string, (long AliasId, long ContentId)> Aliases => _aliasesByPath;

    /// <summary>
    /// Per-content-id durable file identity (content id = list index), or null when the identity could
    /// not be captured at build time. Persisted as <c>fileids.bin</c> so the change journal can dirty
    /// exactly the affected content (plan §3.5).
    /// </summary>
    internal IReadOnlyList<UsnFileIdentity?> ContentIdentities => _contentIdentities;

    /// <summary>
    /// Whether the content at <paramref name="contentId"/> had its durable file identity captured at build
    /// time. A content with no captured identity is absent from the <see cref="FileIdMap"/>, so the change
    /// journal can never dirty it after B0 and B1 can never rescue it — callers must therefore never prune
    /// such a content as a fresh nonmember (it must live-scan instead), or a post-B0 edit that adds a match
    /// would be silently hidden.
    /// </summary>
    internal bool HasCapturedContentIdentity(long contentId)
        => contentId >= 0 && contentId < _contentIdentities.Count && _contentIdentities[(int)contentId].HasValue;

    /// <summary>Number of distinct path aliases in this generation.</summary>
    public int AliasCount => _aliasesByPath.Count;

    /// <summary>
    /// Builds the <see cref="FileIdMap"/> (in-memory <c>fileids.bin</c>) for this generation from the
    /// captured per-content identities and the manifest's volume serial (plan §3.5). Content whose
    /// identity was not captured is simply absent — a journal change to it can't be resolved, so it is
    /// handled conservatively by the caller.
    /// </summary>
    public FileIdMap BuildFileIdMap()
    {
        var map = new FileIdMap(Manifest.VolumeSerialNumber);
        for (int contentId = 0; contentId < _contentIdentities.Count; contentId++)
        {
            if (_contentIdentities[contentId] is { } identity)
                map.Add(contentId, identity);
        }
        return map;
    }

    /// <summary>
    /// Looks up a normalized path's alias. Returns false when the path is not indexed (absent, binary,
    /// unsupported encoding, over-cap, …) — such paths are always live-scanned.
    /// </summary>
    public bool TryGetAlias(string normalizedPath, out long aliasId, out long contentId)
    {
        if (_aliasesByPath.TryGetValue(normalizedPath, out var entry))
        {
            aliasId = entry.AliasId;
            contentId = entry.ContentId;
            return true;
        }
        aliasId = -1;
        contentId = -1;
        return false;
    }

    /// <summary>
    /// Rebuilds a generation from persisted data (used by the store reader). The posting index is
    /// reconstructed from the per-content trigram sets so it is byte-identical to a freshly built one.
    /// </summary>
    internal static ContentIndexGeneration FromPersisted(
        IndexManifest manifest,
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents,
        Dictionary<string, (long AliasId, long ContentId)> aliasesByPath,
        IReadOnlyList<UsnFileIdentity?>? contentIdentities = null,
        bool retainDocuments = true)
    {
        var postings = TrigramPostingIndex.Build(documents);
        var report = new IndexBuildReport();
        for (int i = 0; i < documents.Count; i++)
            report.RecordIndexed();
        var identities = contentIdentities ?? new UsnFileIdentity?[documents.Count];
        // Query-mode load (retainDocuments == false): the per-document trigram sets are only needed to
        // build the postings (done above) and for compaction/serialization. A live QUERY uses only the
        // postings + alias table, so drop the documents — otherwise the generation retains a second,
        // full-size copy of the same trigram data (the posting index is just its transpose), which for a
        // large drive is ~1 GB of dead weight held until the next search.
        var retainedDocuments = retainDocuments
            ? documents
            : (IReadOnlyList<IReadOnlyCollection<Trigram>>)Array.Empty<IReadOnlyCollection<Trigram>>();
        return new ContentIndexGeneration(manifest, postings, aliasesByPath, report, retainedDocuments, identities);
    }

    /// <summary>
    /// Query-mode rebuild that takes a PRE-BUILT posting index (streamed straight from <c>content.bin</c> by
    /// <see cref="TrigramPostingIndex.BuildFromContentBody"/> without ever materializing the per-document
    /// trigram sets) and stores NO documents. Used when a generation is opened only to evaluate candidates,
    /// not to compact or re-serialize it — so opening a large layered index no longer churns multiple GB of
    /// transient garbage building and discarding the whole corpus's document collections.
    /// </summary>
    internal static ContentIndexGeneration FromPersistedPostings(
        IndexManifest manifest,
        TrigramPostingIndex postings,
        int documentCount,
        Dictionary<string, (long AliasId, long ContentId)> aliasesByPath,
        IReadOnlyList<UsnFileIdentity?>? contentIdentities = null)
    {
        var report = new IndexBuildReport();
        for (int i = 0; i < documentCount; i++)
            report.RecordIndexed();
        var identities = contentIdentities ?? new UsnFileIdentity?[documentCount];
        return new ContentIndexGeneration(
            manifest, postings, aliasesByPath, report,
            Array.Empty<IReadOnlyCollection<Trigram>>(), identities);
    }
}

/// <summary>
/// Builds a <see cref="ContentIndexGeneration"/> in memory (plan §3.4/§3.6). Documents are added in
/// order; each admissible document gets the next content id (its posting document id) and a fresh alias
/// id. Non-admissible content (binary, unsupported encoding, over-cap) is recorded in the build report
/// but left <b>out</b> of the alias table, so it is classified <see cref="IndexPathClassification.Unindexed"/>
/// and live-scanned at query time. <see cref="AddHardLink"/> adds another alias to an existing content.
/// </summary>
public sealed class ContentIndexGenerationBuilder
{
    private readonly IndexIngestionPolicy _policy;
    private readonly Func<string, FileIdentity?>? _identityProvider;
    private readonly List<IReadOnlyCollection<Trigram>> _documents = new();
    private readonly List<UsnFileIdentity?> _identities = new();
    private readonly Dictionary<string, (long AliasId, long ContentId)> _aliasesByPath = new(StringComparer.Ordinal);
    private readonly IndexBuildReport _report;
    private long _nextAliasId;
    private ulong _volumeSerialNumber;
    private VolumeBinding? _volumeBinding;
    private long _retainedTrigramCount;

    /// <param name="identityProvider">Optional durable-identity capture for each admitted document
    /// (production passes <see cref="FileIdentityReader.TryGetIdentity"/>; tests can inject a fake or
    /// leave it null). When null, no identities are captured and the generation's <c>fileids.bin</c> is
    /// empty, so journal-based freshness for it degrades conservatively.</param>
    public ContentIndexGenerationBuilder(IndexIngestionPolicy policy, IndexBuildReport? report = null, Func<string, FileIdentity?>? identityProvider = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _report = report ?? new IndexBuildReport();
        _identityProvider = identityProvider;
    }

    /// <summary>The accumulating build report.</summary>
    public IndexBuildReport Report => _report;

    /// <summary>Total distinct document-trigram entries retained by this batch. Used by the paged builder
    /// to bound dense binary batches by actual representation size, not just document count.</summary>
    public long RetainedTrigramCount => _retainedTrigramCount;

    /// <summary>
    /// Classifies and adds a document's bytes. Returns the assigned content id, or <c>-1</c> when the
    /// content was not admitted (its path stays out of the index and will be live-scanned).
    /// </summary>
    public long AddDocument(string path, ReadOnlySpan<byte> content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string normalized = IndexScopeIdentity.NormalizePath(path);
        var classification = IndexIngestionClassifier.ClassifyContent(content, _policy);
        _report.Record(normalized, classification.Reason);
        if (!classification.Admitted)
            return -1;
        return AddAdmitted(normalized, classification.Trigrams, _identityProvider?.Invoke(path));
    }

    /// <summary>
    /// Adds a document whose content was <b>already classified</b> by the build content reader
    /// (<see cref="IIndexFileContentReader"/>, plan §5.1), avoiding a redundant re-classification of the
    /// same bytes. The durable identity is resolved from the builder's identity provider (used by callers
    /// that did not capture it from the read handle). Returns the assigned content id, or <c>-1</c> when
    /// the content was not admitted.
    /// </summary>
    public long AddClassifiedContent(string path, IndexContentClassification classification)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string normalized = IndexScopeIdentity.NormalizePath(path);
        _report.Record(normalized, classification.Reason);
        if (!classification.Admitted)
            return -1;
        return AddAdmitted(normalized, classification.Trigrams, _identityProvider?.Invoke(path));
    }

    /// <summary>
    /// Adds a document whose content was already classified <b>and whose durable identity was captured
    /// from the same read handle</b> (plan §5.4) — no second identity open. The provided
    /// <paramref name="identity"/> (which may be null when identity was unavailable) is used verbatim.
    /// Returns the assigned content id, or <c>-1</c> when the content was not admitted.
    /// </summary>
    public long AddClassifiedContent(string path, IndexContentClassification classification, FileIdentity? identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string normalized = IndexScopeIdentity.NormalizePath(path);
        _report.Record(normalized, classification.Reason);
        if (!classification.Admitted)
            return -1;
        return AddAdmitted(normalized, classification.Trigrams, identity);
    }

    private long AddAdmitted(string normalized, IReadOnlyCollection<Trigram> trigrams, FileIdentity? identity)
    {
        ValidateIdentityVolume(identity);
        long contentId = _documents.Count;
        _documents.Add(trigrams);
        _retainedTrigramCount += trigrams.Count;

        // Record the durable file identity so the change journal can dirty exactly this content (§3.5).
        _identities.Add(identity?.FileId);
        if (identity is { } id && _volumeSerialNumber == 0)
            _volumeSerialNumber = id.VolumeSerialNumber;

        long aliasId = _nextAliasId++;
        _aliasesByPath[normalized] = (aliasId, contentId);
        return contentId;
    }

    /// <summary>
    /// Adds another path alias to an already-added content id (a hard link, plan §3.6). Returns the new
    /// alias id.
    /// </summary>
    public long AddHardLink(string path, long contentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (contentId < 0 || contentId >= _documents.Count)
            throw new ArgumentOutOfRangeException(nameof(contentId));
        string normalized = IndexScopeIdentity.NormalizePath(path);
        long aliasId = _nextAliasId++;
        _aliasesByPath[normalized] = (aliasId, contentId);
        return aliasId;
    }

    /// <summary>
    /// Adds a document whose trigrams were <b>already computed</b> (no re-classification), preserving its
    /// captured durable identity. Used by compaction (plan §11.4) to fold an existing layer's document into
    /// a fresh base without re-reading the file. Returns the assigned content id. The volume serial is not
    /// derived from <paramref name="identity"/> (a file-id only) — seed it via <see cref="SeedVolumeSerialNumber"/>.
    /// </summary>
    public long AddClassifiedDocument(string path, IReadOnlyCollection<Trigram> trigrams, UsnFileIdentity? identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(trigrams);
        string normalized = IndexScopeIdentity.NormalizePath(path);

        long contentId = _documents.Count;
        _documents.Add(trigrams);
        _retainedTrigramCount += trigrams.Count;
        _identities.Add(identity);
        _report.RecordIndexed();

        long aliasId = _nextAliasId++;
        _aliasesByPath[normalized] = (aliasId, contentId);
        return contentId;
    }

    /// <summary>Seeds the volume serial number for the built manifest when it was not captured from an
    /// added document's identity (e.g. compaction, which supplies pre-classified documents). No-op once a
    /// non-zero serial is already set.</summary>
    public void SeedVolumeSerialNumber(ulong volumeSerialNumber)
    {
        if (volumeSerialNumber == 0)
            return;
        if (_volumeSerialNumber != 0 && _volumeSerialNumber != volumeSerialNumber)
            throw new IndexVolumeChangedException("Indexed files came from more than one mounted volume.");
        _volumeSerialNumber = volumeSerialNumber;
    }

    /// <summary>Pins the generation to the mounted volume captured before the crawl. Every same-handle
    /// file identity admitted later must carry this serial or the staged build is aborted.</summary>
    public void SeedVolumeBinding(VolumeBinding volumeBinding)
    {
        SeedVolumeSerialNumber(volumeBinding.VolumeSerialNumber);
        _volumeBinding = volumeBinding;
    }

    private void ValidateIdentityVolume(FileIdentity? identity)
    {
        if (identity is not { } value || value.VolumeSerialNumber == 0)
            return;
        if (_volumeSerialNumber != 0 && value.VolumeSerialNumber != _volumeSerialNumber)
            throw new IndexVolumeChangedException("A file read during indexing belongs to a different mounted volume.");
    }

    /// <summary>Finalizes the generation, building the posting index and stamping the manifest counts.</summary>
    public ContentIndexGeneration Build(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        UsnCheckpoint checkpoint,
        DateTimeOffset builtUtc,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? lastIncrementalUpdateUtc = null)
    {
        var manifest = BuildManifest(scopeId, volumeIdentity, normalizedRootPath, checkpoint, builtUtc,
            createdUtc, lastIncrementalUpdateUtc);
        var postings = TrigramPostingIndex.Build(_documents);
        return new ContentIndexGeneration(manifest, postings, _aliasesByPath, _report, _documents, _identities);
    }

    /// <summary>
    /// Finalizes a <b>persistence-only</b> batch (plan §5.5): stamps the same manifest and transfers the
    /// same immutable document/alias/identity collections <b>without invoking <see cref="TrigramPostingIndex.Build"/></b>.
    /// A paged full build serializes per-document trigrams (never postings), so it never needs the inverted
    /// index — using this instead of <see cref="Build"/> at each flush removes a whole posting-index
    /// construction (and its immediate discard) per batch. The queryable <see cref="Build"/> path is retained
    /// for tests, the query oracle, and compaction.
    /// </summary>
    internal ContentIndexBuildBatch BuildForPersistence(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        UsnCheckpoint checkpoint,
        DateTimeOffset builtUtc)
    {
        var manifest = BuildManifest(scopeId, volumeIdentity, normalizedRootPath, checkpoint, builtUtc);
        return new ContentIndexBuildBatch(manifest, _documents, _aliasesByPath, _identities, _report);
    }

    private IndexManifest BuildManifest(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        UsnCheckpoint checkpoint,
        DateTimeOffset builtUtc,
        DateTimeOffset? createdUtc = null,
        DateTimeOffset? lastIncrementalUpdateUtc = null)
        => new()
        {
            ScopeId = scopeId,
            VolumeIdentity = volumeIdentity,
            VolumeSerialNumber = _volumeSerialNumber,
            VolumeGuidPath = _volumeBinding?.VolumeGuidPath,
            FileSystemName = _volumeBinding?.FileSystemName,
            VolumeRelativeRootPath = _volumeBinding?.RootRelativePath,
            NormalizedRootPath = normalizedRootPath,
            FreshnessCheckpoint = checkpoint,
            ContentCount = _documents.Count,
            AliasCount = _aliasesByPath.Count,
            CreatedUtc = createdUtc ?? builtUtc,
            LastIncrementalUpdateUtc = lastIncrementalUpdateUtc,
            BuiltUtc = builtUtc,
        };
}
