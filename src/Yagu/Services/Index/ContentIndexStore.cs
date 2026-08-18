using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>The outcome of publishing a generation.</summary>
public readonly record struct PublishResult(string GenerationId, long Sequence);

/// <summary>Health of one on-disk index scope as exposed by manifest-only management reads.</summary>
public enum IndexStorageHealth
{
    /// <summary>The active pointer, base manifest, and every active segment manifest are trusted.</summary>
    Healthy,
    /// <summary>The source folder recorded by otherwise readable metadata no longer exists or is unavailable.</summary>
    SourceMissing,
    /// <summary>The persisted on-disk layout version differs from the version this build understands.</summary>
    IncompatibleFormat,
    /// <summary>The indexed content representation changed and the folder must be rebuilt.</summary>
    IncompatibleRepresentation,
    /// <summary>Pointer or manifest metadata is missing, corrupt, inconsistent, or incomplete.</summary>
    CorruptOrIncomplete,
}

/// <summary>
/// Cheap manifest-only stats for a scope's current index (base + segments): whether a trusted base is
/// present, the total stored content-record count, the active segment count, the active generation's
/// build time, and the indexed root path. Read without loading content/posting data (see
/// <see cref="ContentIndexStore.ReadStorageStat"/>).
/// </summary>
public readonly record struct StoredIndexStat(
    IndexStorageHealth Health,
    long DocumentCount,
    int SegmentCount,
    DateTimeOffset? BuiltUtc,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset? LastIncrementalUpdateUtc,
    string? RootPath,
    string? Problem)
{
    public bool Readable => Health == IndexStorageHealth.Healthy;
}

/// <summary>Identity of the artifacts made active by one staged full-build commit.</summary>
internal readonly record struct StagedIndexCommitResult(
    string ActiveBaseGenerationId,
    long ActivePointerSequence,
    string LastPublishedArtifactId);

/// <summary>
/// Manages a single scope's index generations on disk (plan §3.4 — managed reference). Publication is
/// transactional: a new generation is written to a temp directory, validated, atomically renamed to its
/// final id, and only then does a checksummed pointer slot flip to reference it. Two pointer slots
/// (<c>current.a</c>/<c>current.b</c>) alternate so a crash mid-flip can always recover the newest valid
/// slot; any corruption or missing/invalid pointer makes the scope untrusted and the caller live-scans.
/// Retention keeps the newest N generations (never deleting one referenced by a valid pointer).
/// <para>
/// This is the single-process reference for tests and design validation; production ultimately writes
/// generations from the isolated Rust worker. Every path is composed through an injected
/// <see cref="IContentIndexPathProvider"/> so tests never touch a real index (plan §9.2).
/// </para>
/// </summary>
public sealed class ContentIndexStore
{
    private const string GenerationsSubdir = "generations";
    private const string SegmentsSubdir = "segments";
    private const string SlotA = "current.a";
    private const string SlotB = "current.b";
    private const string GenerationPrefix = "gen-";
    private const string SegmentPrefix = "seg-";
    internal const string ImportMarkerFile = ".uncommitted-import";

    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeDir;
    private readonly string _scopeId;
    private readonly int _retainedGenerations;

    internal Func<string, long> DirectorySizeReader { get; set; } = DirectorySizeBytes;
    internal Func<string, long> MappedQuerySizeReader { get; set; } = MappedQueryFilesSize;
    internal Func<IEnumerable<string>> ExistingGenerationDirectoriesReader { get; set; }
    internal Func<string, VolumeBinding?> CurrentVolumeReader { get; set; } = VolumeBindingReader.TryCapture;
    internal Func<string, string> ManifestRootNormalizer { get; set; } = IndexScopeIdentity.NormalizePath;
    internal Func<string, string?> ManifestPathRootReader { get; set; } = Path.GetPathRoot;
    internal Action<string>? BeforeImportDestinationCheck { get; set; }
    internal Action? BeforeCacheWarmthCheck { get; set; }

    public ContentIndexStore(IContentIndexPathProvider pathProvider, string scopeId, int retainedGenerations = 2)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        _paths = pathProvider;
        _scopeDir = pathProvider.GetScopeDirectory(scopeId);
        _scopeId = scopeId;
        _retainedGenerations = Math.Max(1, retainedGenerations);
        ExistingGenerationDirectoriesReader = ExistingGenerationDirs;
    }

    /// <summary>The scope's storage directory.</summary>
    public string ScopeDirectory => _scopeDir;

    /// <summary>The shared index root, where a streaming merge creates its private workspace so the spool
    /// and the prepared layer stay on the index volume.</summary>
    internal string IndexRootDirectory => _paths.IndexRoot;

    internal IndexMutationContext AcquireMutationContext() => IndexMutationContext.Acquire(_paths);

    private string GenerationsDir => Path.Combine(_scopeDir, GenerationsSubdir);

    private string SegmentsDir => Path.Combine(_scopeDir, SegmentsSubdir);

    /// <summary>
    /// When true, each base-generation publish also writes the additive <b>format-v3 query structures</b>
    /// (plan §5.1) into the generation directory, transactionally (they ride the same staged temp dir → atomic
    /// move). Off by default: producing them is opt-in until a query stage consumes them, so the default build
    /// path is byte-for-byte unchanged. Best-effort — a v3 write failure never fails the base publish.
    /// </summary>
    public bool ProduceV3QueryStructures { get; set; }

    /// <summary>
    /// Publishes a generation transactionally and returns its id/sequence. Throws only for genuinely
    /// exceptional I/O; a validation failure of the freshly-written temp generation throws
    /// <see cref="InvalidDataException"/> after cleaning up the temp directory, leaving the active
    /// pointer unchanged.
    /// </summary>
    public PublishResult Publish(ContentIndexGeneration generation)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return PublishUnderLease(mutation, generation);
    }

    internal PublishResult PublishUnderLease(IndexMutationContext mutation, ContentIndexGeneration generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return PublishStagedBase(mutation, tempDir =>
        {
            ContentIndexGenerationSerializer.Write(tempDir, generation);
            if (ProduceV3QueryStructures)
                TryWriteV3Structures(tempDir, () => ContentIndexV3Format.Write(tempDir, generation));
        });
    }

    /// <summary>
    /// Persistence-only base publish (plan §5.5): identical staging, freshly-written validation, atomic
    /// directory move, and pointer-slot flip — but the batch was finalized without constructing a posting
    /// index.
    /// </summary>
    internal PublishResult PublishUnderLease(IndexMutationContext mutation, ContentIndexBuildBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return PublishStagedBase(mutation, tempDir =>
        {
            ContentIndexGenerationSerializer.Write(tempDir, batch);
            if (ProduceV3QueryStructures)
                TryWriteV3Structures(tempDir, () => ContentIndexV3Format.Write(tempDir, batch));
        });
    }

    /// <summary>
    /// Writes the format-v3 query structures best-effort: they are additive and no query path reads them yet,
    /// so a failure must NOT fail the base publish. On failure any partial v3 file is removed so a later open
    /// never sees a torn structure (and the scope simply live-scans until the next successful build).
    /// </summary>
    private void TryWriteV3Structures(string tempDir, Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Format-v3 query structures could not be written for scope {Scope}; base generation published without them.", _scopeId);
            foreach (string name in new[]
            {
                ContentIndexV3Format.PostingsFile,
                ContentIndexV3Format.PathIndexFile,
                ContentIndexV3Format.IdentitiesFile,
                ContentIndexV3Format.TombstonesFile,
            })
            {
                try
                {
                    File.Delete(Path.Combine(tempDir, name));
                    File.Delete(Path.Combine(tempDir, name + ".tmp"));
                }
                catch { /* best effort */ }
            }
        }
    }

    private PublishResult PublishStagedBase(IndexMutationContext mutation, Action<string> writeToTempDir)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        Directory.CreateDirectory(GenerationsDir);

        long newSequence = NextSequence(CurrentMaxSequence(), "generation/pointer");
        string generationId = GenerationPrefix + newSequence.ToString("D6", CultureInfo.InvariantCulture);
        string tempDir = Path.Combine(GenerationsDir, "." + generationId + ".tmp");
        string finalDir = Path.Combine(GenerationsDir, generationId);

        DeleteDirectorySafe(tempDir);
        writeToTempDir(tempDir);
        IndexMutationFaults.Hit(IndexMutationFaults.BaseWritten);

        // Structural read-after-write validation of the freshly written base (plan §5.7): verifies every
        // checksum + record boundary + count without building a posting index.
        if (!ContentIndexGenerationSerializer.TryValidateSerializedGeneration(tempDir, out _))
        {
            DeleteDirectorySafe(tempDir);
            YaguLog.For("ContentIndex").LogWarning("Publish rejected: freshly written base '{GenerationId}' failed validation (scope {Scope}); pointer unchanged.", generationId, _scopeId);
            throw new InvalidDataException("Freshly written generation failed validation; not published.");
        }
        IndexMutationFaults.Hit(IndexMutationFaults.BaseValidated);

        MarkImport(tempDir);
        IndexMutationFaults.Hit(IndexMutationFaults.BaseMarked);
        DeleteDirectorySafe(finalDir);
        Directory.Move(tempDir, finalDir);
        IndexMutationFaults.Hit(IndexMutationFaults.BasePromoted);

        // A new base supersedes all prior delta segments — reset the active segment list to empty.
        WriteTargetSlot(newSequence, generationId, Array.Empty<string>());
        IndexMutationFaults.Hit(IndexMutationFaults.BasePointerPublished);
        DeleteFileSafe(Path.Combine(finalDir, ImportMarkerFile));
        IndexMutationFaults.Hit(IndexMutationFaults.BaseMarkerCleared);
        RetainAfterCommit();
        IndexMutationFaults.Hit(IndexMutationFaults.BaseCleanupFinished);
        DeleteFileSafe(Path.Combine(_scopeDir, ContentIndexManager.AutomaticCompactionFailureFile));
        YaguLog.For("ContentIndex").LogDebug("Published base generation '{GenerationId}' seq={Sequence} (scope {Scope}, segments reset).", generationId, newSequence, _scopeId);
        return new PublishResult(generationId, newSequence);
    }

    /// <summary>
    /// Opens the current generation by selecting the newest valid pointer slot, or null when no trusted
    /// generation is available (missing/corrupt pointer or generation → the caller live-scans).
    /// </summary>
    public ContentIndexGeneration? TryOpenCurrent(bool retainDocuments = true) => TryOpenCurrent(out _, retainDocuments);

    /// <summary>
    /// Opens the current generation and also reports the directory it was read from (so the out-of-process
    /// worker can locate that generation's <c>content.bin</c>). <paramref name="generationDir"/> is null when
    /// no trusted generation is available. <paramref name="retainDocuments"/> false drops the per-document
    /// trigram sets after building postings (a query/staleness-mode load).
    /// </summary>
    public ContentIndexGeneration? TryOpenCurrent(out string? generationDir, bool retainDocuments = true)
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            string dir = Path.Combine(GenerationsDir, slot.GenerationId);
            var generation = ContentIndexGenerationSerializer.TryRead(dir, retainDocuments);
            if (generation is not null)
            {
                generationDir = dir;
                return generation;
            }
        }

        generationDir = null;
        return null;
    }

    /// <summary>
    /// Cheap freshness inputs for the current layered index: the base generation's file identities plus
    /// the <b>newest active layer's</b> checkpoint, read from the newest valid pointer slot WITHOUT
    /// deserializing content.bin. Earlier changes are already represented by active delta segments, so
    /// maintenance must replay from the newest checkpoint rather than repeatedly replaying from the old
    /// full-build checkpoint (which otherwise reaches the catch-up cap shortly after a successful update).
    /// Null when no trusted generation/layer metadata is available.
    /// </summary>
    public (IndexManifest Manifest, FileIdMap FileIds)? TryReadCurrentFreshnessInputs()
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            if (ContentIndexGenerationSerializer.TryReadFreshnessInputs(Path.Combine(GenerationsDir, slot.GenerationId)) is not { } inputs)
                continue;
            if (slot.SegmentIds.Count == 0)
                return inputs;
            long nextSyntheticContentId = inputs.Manifest.ContentCount;
            IndexManifest? newestManifest = null;
            bool allReadable = true;
            foreach (string segmentId in slot.SegmentIds)
            {
                string segmentDir = Path.Combine(SegmentsDir, segmentId);
                if (ContentIndexGenerationSerializer.TryReadFreshnessInputs(segmentDir) is not { } segmentInputs)
                {
                    allReadable = false;
                    break;
                }
                inputs.FileIds.MergeIdentitiesFrom(segmentInputs.FileIds, ref nextSyntheticContentId);
                newestManifest = segmentInputs.Manifest;
            }
            if (!allReadable || newestManifest is null)
                continue;
            return (inputs.Manifest with { FreshnessCheckpoint = newestManifest.FreshnessCheckpoint }, inputs.FileIds);
        }
        return null;
    }

    /// <summary>Reads only active-layer manifests and returns the base scope metadata with the newest active
    /// checkpoint. This is the first phase of incremental maintenance, before the journal identifies which
    /// durable identities need prior-path lookup.</summary>
    internal IndexManifest? TryReadCurrentIncrementalManifest()
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            if (ContentIndexGenerationSerializer.TryReadManifest(Path.Combine(GenerationsDir, slot.GenerationId)) is not { } baseManifest)
                continue;
            IndexManifest newestManifest = baseManifest;
            bool allReadable = true;
            foreach (string segmentId in slot.SegmentIds)
            {
                if (ContentIndexGenerationSerializer.TryReadManifest(Path.Combine(SegmentsDir, segmentId)) is not { } segmentManifest)
                {
                    allReadable = false;
                    break;
                }
                newestManifest = segmentManifest;
            }
            if (allReadable)
                return baseManifest with { FreshnessCheckpoint = newestManifest.FreshnessCheckpoint };
        }
        return null;
    }

    /// <summary>
    /// Lightweight state for one incremental refresh: the newest checkpoint/root manifest plus active prior
    /// paths for only the journal-changed identities after applying every segment oldest-to-newest. Reads
    /// only manifests, aliases, file identities, and tombstones; never reads <c>content.bin</c> or builds
    /// posting indexes. Newer aliases replace an older path, and tombstones suppress older aliases, matching
    /// layered query precedence. Returns null when any active layer's metadata is unreadable.
    /// </summary>
    internal (IndexManifest Manifest, IReadOnlyDictionary<UsnFileIdentity, IReadOnlyList<string>> PathsByIdentity)?
        TryReadCurrentIncrementalMetadata(
            IReadOnlySet<UsnFileIdentity> changedIdentities,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changedIdentities);
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string baseDir = Path.Combine(GenerationsDir, slot.GenerationId);
            if (ContentIndexGenerationSerializer.TryReadManifest(baseDir) is null)
                continue; // same slot-selection rule as TryReadCurrentIncrementalManifest
            if (ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                    baseDir,
                    changedIdentities,
                    new HashSet<string>(StringComparer.Ordinal),
                    cancellationToken) is not { } baseMetadata)
                return null; // accepted active slot, but its identity/path metadata is corrupt

            var pathsByIdentity = new Dictionary<UsnFileIdentity, HashSet<string>>(changedIdentities.Count);
            foreach ((UsnFileIdentity identity, IReadOnlyList<string> paths) in baseMetadata.PathsByIdentity)
                pathsByIdentity[identity] = new HashSet<string>(paths, StringComparer.Ordinal);

            IndexManifest newestManifest = baseMetadata.Manifest;
            bool allReadable = true;
            foreach (string segmentId in slot.SegmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string segmentDir = Path.Combine(SegmentsDir, segmentId);
                var activeTargetPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (HashSet<string> paths in pathsByIdentity.Values)
                    activeTargetPaths.UnionWith(paths);
                if (ContentIndexGenerationSerializer.TryReadManifest(segmentDir) is null)
                {
                    allReadable = false;
                    break; // manifest selection may safely fall back to the older redundant slot
                }
                if (ContentIndexGenerationSerializer.TryReadIncrementalLayerMetadata(
                        segmentDir,
                        changedIdentities,
                        activeTargetPaths,
                        cancellationToken) is not { } segmentMetadata
                    || ContentIndexDeltaSegmentSerializer.TryReadTombstones(segmentDir, activeTargetPaths, cancellationToken) is not { } tombstones)
                    return null; // do not mix a newer checkpoint with older-slot prior paths

                foreach (string path in tombstones)
                {
                    foreach (HashSet<string> paths in pathsByIdentity.Values)
                        paths.Remove(path);
                }
                foreach (string path in segmentMetadata.ShadowedPaths)
                {
                    foreach (HashSet<string> paths in pathsByIdentity.Values)
                        paths.Remove(path);
                }
                foreach ((UsnFileIdentity identity, IReadOnlyList<string> paths) in segmentMetadata.PathsByIdentity)
                {
                    if (!pathsByIdentity.TryGetValue(identity, out HashSet<string>? activePaths))
                        pathsByIdentity[identity] = activePaths = new HashSet<string>(StringComparer.Ordinal);
                    activePaths.UnionWith(paths);
                }
                newestManifest = segmentMetadata.Manifest;
            }
            if (!allReadable)
                continue;

            var resultPaths = new Dictionary<UsnFileIdentity, IReadOnlyList<string>>(pathsByIdentity.Count);
            foreach ((UsnFileIdentity identity, HashSet<string> paths) in pathsByIdentity)
            {
                if (paths.Count > 0)
                    resultPaths[identity] = paths.ToArray();
            }

            return (
                baseMetadata.Manifest with { FreshnessCheckpoint = newestManifest.FreshnessCheckpoint },
                resultPaths);
        }
        return null;
    }

    /// <summary>Full validation of the current generation (used by <c>Validate</c>/<c>--index-status</c>).</summary>
    public bool ValidateCurrent() => TryOpenCurrent() is not null;

    /// <summary>
    /// Reports the current layered index's on-disk directories — the base generation dir and each active
    /// segment dir (oldest → newest) — from the newest valid pointer slot WITHOUT deserializing any content
    /// (a cheap pointer-slot read). Lets the out-of-process query worker memory-map each layer's format-v3
    /// structures directly, so a large-scope query never loads the index into the host. Returns false when no
    /// valid slot references existing directories.
    /// </summary>
    public bool TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs)
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            string candidateBase = Path.Combine(GenerationsDir, slot.GenerationId);
            if (!Directory.Exists(candidateBase))
                continue;

            var segs = new List<string>(slot.SegmentIds.Count);
            bool allPresent = true;
            foreach (string segId in slot.SegmentIds)
            {
                string segDir = Path.Combine(SegmentsDir, segId);
                if (!Directory.Exists(segDir)) { allPresent = false; break; }
                segs.Add(segDir);
            }
            if (!allPresent)
                continue;

            baseDir = candidateBase;
            segmentDirs = segs;
            return true;
        }

        baseDir = null;
        segmentDirs = Array.Empty<string>();
        return false;
    }

    /// <summary>
    /// Advances the current <b>base</b> generation's freshness checkpoint in place to
    /// <paramref name="newCheckpoint"/> (a cheap manifest-only rewrite; content/aliases/fileids untouched),
    /// then invalidates the query-mode cache so the next search reads the fresher checkpoint. This is the
    /// proactive "re-anchor" that stops an unchanging root's checkpoint from aging out of the USN-journal
    /// window and forcing every future search to bypass the index. Only applies to a <b>base-only</b> scope:
    /// when active delta segments exist the base is the oldest layer and advancing only it would be
    /// inconsistent with the segments, so this returns false and the caller re-anchors via compaction or a
    /// rebuild instead. Returns false (no change) when there is no trusted base or the rewrite fails; never throws.
    /// </summary>
    public bool TryReanchorBaseCheckpoint(UsnCheckpoint newCheckpoint)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return TryReanchorBaseCheckpointUnderLease(mutation, newCheckpoint);
    }

    internal bool TryReanchorBaseCheckpointUnderLease(IndexMutationContext mutation, UsnCheckpoint newCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        try
        {
            foreach (SlotContents slot in ReadValidSlotsNewestFirst())
            {
                // Only base-only scopes re-anchor cheaply (see remarks).
                if (slot.SegmentIds.Count > 0)
                    return false;

                string baseDir = Path.Combine(GenerationsDir, slot.GenerationId);
                if (ContentIndexGenerationSerializer.TryReanchorManifestCheckpoint(baseDir, newCheckpoint))
                {
                    // Publish a new pointer sequence even though the immutable artifact ids are unchanged.
                    // Other Yagu processes include sequence in their query-cache signature, so they observe
                    // the in-place manifest re-anchor and reopen instead of retaining the old checkpoint.
                    WriteTargetSlot(NextSequence(CurrentMaxSequence(), "pointer"), slot.GenerationId, slot.SegmentIds);
                    IndexMutationFaults.Hit(IndexMutationFaults.ReanchorPointerPublished);
                    OpenedLayeredIndexCache.Remove(_scopeDir);
                    return true;
                }
                return false; // newest valid slot found but the rewrite was a no-op / failed
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex, "Re-anchor of base checkpoint failed for scope {Scope} → left unchanged.", _scopeId);
        }
        return false;
    }

    /// <summary>Deletes all data for this scope (Settings "Delete selected index").</summary>
    public void DeleteScope()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        DeleteScopeUnderLease(mutation);
    }

    internal void DeleteScopeUnderLease(IndexMutationContext mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        OpenedLayeredIndexCache.Remove(_scopeDir);
        DeleteDirectorySafe(_scopeDir);
    }

    // ─────────────────────────── delta segments (Phase 3, plan §11.4) ───────────────────────────

    /// <summary>A layered view of the current index: the base generation plus its ordered active delta
    /// segments (oldest → newest), each with the directory it was read from.</summary>
    public sealed record LayeredIndexHandle(
        ContentIndexGeneration Base,
        string BaseDir,
        IReadOnlyList<ContentIndexDeltaSegment> Segments,
        IReadOnlyList<string> SegmentDirs);

    /// <summary>
    /// Opens the current layered index (base + active segments) from the newest valid pointer slot. If the
    /// base or <b>any</b> referenced segment is missing/corrupt, that slot is skipped (the older slot is
    /// tried); when none is fully readable, returns null and the caller live-scans. Never throws.
    /// <paramref name="retainDocuments"/> false drops each layer's per-document trigram sets after its
    /// postings are built (a query-mode open); pass true (default) when the layers will be compacted or
    /// re-serialized, which still need the documents.
    /// </summary>
    public LayeredIndexHandle? TryOpenLayered(
        bool retainDocuments = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Query-mode opens (retainDocuments:false) are cached per scope + generation signature, so
            // repeated searches of the SAME unchanged index reuse the deserialized (immutable) generations
            // instead of re-reading and re-building postings for the base and every segment each time — the
            // latter churns multiple GB of transient garbage and is the source of the per-search memory
            // spike on a large layered index. A rebuild / compaction / segment append changes the signature
            // (generation id + segment ids) → cache miss → re-open (evicting the stale entry).
            string signature = slot.Sequence.ToString(CultureInfo.InvariantCulture) + "|" + slot.GenerationId + "|" + string.Join(",", slot.SegmentIds);
            if (!retainDocuments && OpenedLayeredIndexCache.TryGet(_scopeDir, signature) is { } cached)
                return cached;

            string baseDir = Path.Combine(GenerationsDir, slot.GenerationId);
            ContentIndexGeneration? baseGen = ContentIndexGenerationSerializer.TryRead(
                baseDir,
                retainDocuments,
                cancellationToken);
            if (baseGen is null)
                continue;

            var segments = new List<ContentIndexDeltaSegment>(slot.SegmentIds.Count);
            var segmentDirs = new List<string>(slot.SegmentIds.Count);
            bool allSegmentsOk = true;
            foreach (string segId in slot.SegmentIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string segDir = Path.Combine(SegmentsDir, segId);
                ContentIndexDeltaSegment? seg = ContentIndexDeltaSegmentSerializer.TryRead(
                    segDir,
                    retainDocuments,
                    cancellationToken);
                if (seg is null)
                {
                    allSegmentsOk = false; // a torn segment makes the whole layered set untrusted
                    break;
                }
                segments.Add(seg);
                segmentDirs.Add(segDir);
            }
            if (!allSegmentsOk)
                continue;

            var handle = new LayeredIndexHandle(baseGen, baseDir, segments, segmentDirs);
            if (!retainDocuments)
                OpenedLayeredIndexCache.Store(_scopeDir, signature, handle);
            return handle;
        }
        return null;
    }

    /// <summary>
    /// True when the current layered index (the newest valid slot's base + segments) is already deserialized
    /// in the process-wide query-mode cache — i.e. a <see cref="TryOpenLayered(bool)"/> query-mode open would
    /// be a fast cache hit rather than a cold, multi-second, multi-GB deserialize. Reads only the (cheap)
    /// pointer slot; never throws. Returns false when there is no trusted slot (nothing to accelerate anyway).
    /// </summary>
    public bool IsCurrentLayeredIndexCached()
    {
        try
        {
            BeforeCacheWarmthCheck?.Invoke();
            foreach (SlotContents slot in ReadValidSlotsNewestFirst())
            {
                string signature = slot.Sequence.ToString(CultureInfo.InvariantCulture) + "|" + slot.GenerationId + "|" + string.Join(",", slot.SegmentIds);
                return OpenedLayeredIndexCache.TryGet(_scopeDir, signature) is not null;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A slot-read failure just means "not warm" → the caller live-scans (always safe), but log the cause.
            YaguLog.For("ContentIndex").LogWarning(ex, "Layered-index cache-warmth check failed for scope {Scope} → treating as cold.", _scopeId);
        }
        return false;
    }

    /// <summary>
    /// Total on-disk byte size of the CURRENT layered index (the newest valid slot's base generation plus
    /// its active delta segments). Reads only file metadata; returns 0 when there is no trusted slot. Never
    /// throws. Used to decide whether the index is small enough to load into memory for a query.
    /// </summary>
    public long GetCurrentLayeredIndexSizeBytes()
    {
        try
        {
            foreach (SlotContents slot in ReadValidSlotsNewestFirst())
            {
                string baseDirectory = Path.Combine(GenerationsDir, slot.GenerationId);
                if (ContentIndexGenerationSerializer.TryReadManifest(baseDirectory) is null)
                    continue;
                long total = DirectorySizeReader(baseDirectory);
                bool allSegmentsReadable = true;
                foreach (string segId in slot.SegmentIds)
                {
                    string segmentDirectory = Path.Combine(SegmentsDir, segId);
                    if (ContentIndexGenerationSerializer.TryReadManifest(segmentDirectory) is null)
                    {
                        allSegmentsReadable = false;
                        break;
                    }
                    total += DirectorySizeReader(segmentDirectory);
                }
                if (!allSegmentsReadable)
                    continue;
                return total;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any read failure → 0 (treated as "no usable index" → the caller live-scans), but log the cause.
            YaguLog.For("ContentIndex").LogWarning(ex, "Layered-index size computation failed for scope {Scope} → treating as no usable index.", _scopeId);
        }
        return 0;
    }

    /// <summary>
    /// Total byte size of the CURRENT layer set's format-v3 files — exactly the files the out-of-process
    /// query worker memory-maps. Unlike <see cref="GetCurrentLayeredIndexSizeBytes"/>, this excludes legacy
    /// build/compaction payloads (<c>content.bin</c>, aliases, manifests) that never enter the worker's mapped
    /// query footprint. Returns 0 when any active layer is not v3-query-ready.
    /// </summary>
    public long GetCurrentLayeredMappedQuerySizeBytes()
    {
        try
        {
            if (!TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs)
                || baseDir is null)
                return 0;

            long total = MappedQuerySizeReader(baseDir);
            if (total <= 0)
                return 0;
            foreach (string segmentDir in segmentDirs)
            {
                long layer = MappedQuerySizeReader(segmentDir);
                if (layer <= 0)
                    return 0;
                total = checked(total + layer);
            }
            return total;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Mapped-query size computation failed for scope {Scope} → treating as not query-ready.", _scopeId);
            return 0;
        }
    }

    private static long MappedQueryFilesSize(string layerDir)
    {
        string[] required =
        [
            ContentIndexV3Format.PostingsFile,
            ContentIndexV3Format.PathIndexFile,
            ContentIndexV3Format.IdentitiesFile,
            ContentIndexV3Format.TombstonesFile,
        ];
        long total = 0;
        foreach (string name in required)
        {
            string path = Path.Combine(layerDir, name);
            if (!File.Exists(path))
                return 0;
            total = checked(total + new FileInfo(path).Length);
        }
        return total;
    }

    /// <summary>
    /// Publishes an immutable delta segment and atomically appends it to the current base's active segment
    /// list (plan §11.4). Requires a trusted current base + readable existing segments; throws
    /// <see cref="InvalidOperationException"/> otherwise (the caller then does a full rebuild instead).
    /// </summary>
    public PublishResult PublishSegment(ContentIndexDeltaSegment segment)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return PublishSegmentUnderLease(mutation, segment);
    }

    internal PublishResult PublishSegmentUnderLease(IndexMutationContext mutation, ContentIndexDeltaSegment segment)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentNullException.ThrowIfNull(segment);
        return PublishSegmentFastUnderLease(mutation, segment);
    }

    /// <summary>
    /// Appends a delta segment to the active base <b>without re-materializing</b> the base or the existing
    /// segments — it reads only the (cheap) pointer slot for the base id + current segment list and validates
    /// just the freshly written segment. This lets a paged build spill many batches to disk with memory
    /// bounded to a single batch (unlike <see cref="PublishSegment"/>, whose full <see cref="TryOpenLayered"/>
    /// re-read would reload the whole growing index on every flush). Requires an active pointer slot (the paged
    /// build publishes the base first); throws <see cref="InvalidOperationException"/> otherwise.
    /// </summary>
    public PublishResult PublishSegmentFast(ContentIndexDeltaSegment segment)
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        return PublishSegmentFastUnderLease(mutation, segment);
    }

    internal PublishResult PublishSegmentFastUnderLease(IndexMutationContext mutation, ContentIndexDeltaSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return PublishStagedSegmentFast(mutation, tempDir =>
        {
            ContentIndexDeltaSegmentSerializer.Write(tempDir, segment);
            if (ProduceV3QueryStructures)
                TryWriteV3Structures(tempDir, () => ContentIndexV3Format.Write(tempDir, segment.Added, segment.RemovedPaths));
        });
    }

    /// <summary>
    /// Persistence-only fast segment append (plan §5.5): identical to the queryable overload — reads only
    /// the cheap pointer slot, validates just the freshly written segment — but the added documents came
    /// from a <see cref="ContentIndexBuildBatch"/> that never built a posting index.
    /// </summary>
    internal PublishResult PublishSegmentFastUnderLease(IndexMutationContext mutation, ContentIndexDeltaSegmentBatch segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return PublishStagedSegmentFast(mutation, tempDir =>
        {
            ContentIndexDeltaSegmentSerializer.Write(tempDir, segment);
            if (ProduceV3QueryStructures)
                TryWriteV3Structures(tempDir, () => ContentIndexV3Format.Write(tempDir, segment.Added, segment.RemovedPaths));
        });
    }

    private PublishResult PublishStagedSegmentFast(IndexMutationContext mutation, Action<string> writeToTempDir)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);

        SlotContents? active = null;
        foreach (SlotContents slot in ReadValidSlotsNewestFirst()) { active = slot; break; }
        if (active is null)
        {
            YaguLog.For("ContentIndex").LogWarning("PublishSegmentFast: no active base to append a delta segment to (scope {Scope}).", _scopeId);
            throw new InvalidOperationException("No active base to append a delta segment to.");
        }

        Directory.CreateDirectory(SegmentsDir);
        long segSequence = NextSequence(CurrentMaxSegmentSequence(), "segment");
        string segmentId = SegmentPrefix + segSequence.ToString("D6", CultureInfo.InvariantCulture);
        string tempDir = Path.Combine(SegmentsDir, "." + segmentId + ".tmp");
        string finalDir = Path.Combine(SegmentsDir, segmentId);

        DeleteDirectorySafe(tempDir);
        writeToTempDir(tempDir);
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentWritten);
        if (!ContentIndexDeltaSegmentSerializer.TryValidateSerializedSegment(tempDir, out _))
        {
            DeleteDirectorySafe(tempDir);
            throw new InvalidDataException("Freshly written delta segment failed validation; not published.");
        }
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentValidated);
        MarkImport(tempDir);
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentMarked);
        DeleteDirectorySafe(finalDir);
        Directory.Move(tempDir, finalDir);
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentPromoted);

        var newSegmentIds = new List<string>(active.Value.SegmentIds) { segmentId };
        WriteTargetSlot(NextSequence(CurrentMaxSequence(), "pointer"), active.Value.GenerationId, newSegmentIds);
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentPointerPublished);
        DeleteFileSafe(Path.Combine(finalDir, ImportMarkerFile));
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentMarkerCleared);
        RetainAfterCommit();
        IndexMutationFaults.Hit(IndexMutationFaults.SegmentCleanupFinished);
        YaguLog.For("ContentIndex").LogDebug("Appended delta segment '{SegmentId}' seq={Sequence} over base '{BaseGenerationId}' (scope {Scope}, now {SegmentCount} segment(s)).", segmentId, segSequence, active.Value.GenerationId, _scopeId, newSegmentIds.Count);
        return new PublishResult(segmentId, segSequence);
    }

    /// <summary>A bounded contiguous run of physically small active segments that can be merged without
    /// opening the base or any segment outside the run. Segment ids/directories remain in pointer order.</summary>
    internal sealed record SegmentCoalesceRun(
        int StartIndex,
        IReadOnlyList<string> SegmentIds,
        IReadOnlyList<string> SegmentDirectories,
        long TotalBytes);

    /// <summary>
    /// Finds a bounded contiguous run of <b>incremental</b> active segments to merge. Full-build paging
    /// layers are never selected: they are disjoint parts of one build, so merging them reclaims nothing
    /// while paying the full merge cost. Only accumulated update history is eligible.
    /// </summary>
    internal bool TryFindIncrementalSegmentRun(
        int minimumSegments,
        int maximumSegments,
        long maximumIndividualBytes,
        long maximumTotalBytes,
        out SegmentCoalesceRun? run)
        => TryFindSmallSegmentRun(
            minimumSegments, maximumSegments, maximumIndividualBytes, maximumTotalBytes, out run,
            incrementalOnly: true);

    /// <summary>
    /// Finds the oldest contiguous active run containing at least <paramref name="minimumSegments"/> small
    /// segments. Selection is manifest-only plus directory sizes: no content/posting file is opened. The
    /// individual and aggregate byte caps bound the memory/IO of the later merge independently of total
    /// scope size, so a multi-GB base and unrelated large segments are never materialized.
    /// </summary>
    internal bool TryFindSmallSegmentRun(
        int minimumSegments,
        int maximumSegments,
        long maximumIndividualBytes,
        long maximumTotalBytes,
        out SegmentCoalesceRun? run,
        bool incrementalOnly = false)
    {
        run = null;
        if (minimumSegments < 2 || maximumSegments < minimumSegments
            || maximumIndividualBytes <= 0 || maximumTotalBytes < maximumIndividualBytes)
        {
            return false;
        }

        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            IndexManifest? baseManifest =
                ContentIndexGenerationSerializer.TryReadManifest(Path.Combine(GenerationsDir, slot.GenerationId));
            if (baseManifest is null)
                continue;

            int start = -1;
            long total = 0;
            bool? runIsFullBuildPaging = null;
            var ids = new List<string>(maximumSegments);
            var directories = new List<string>(maximumSegments);
            SegmentCoalesceRun? selected = null;

            bool FinishRun()
            {
                if (ids.Count < minimumSegments)
                    return false;
                selected = new SegmentCoalesceRun(start, ids.ToArray(), directories.ToArray(), total);
                return true;
            }

            void ResetRun()
            {
                start = -1;
                total = 0;
                runIsFullBuildPaging = null;
                ids.Clear();
                directories.Clear();
            }

            for (int i = 0; i < slot.SegmentIds.Count; i++)
            {
                string segmentId = slot.SegmentIds[i];
                string directory = Path.Combine(SegmentsDir, segmentId);
                IndexManifest? segmentManifest = ContentIndexGenerationSerializer.TryReadManifest(directory);
                bool readable = segmentManifest is not null;
                long bytes = readable ? DirectorySizeReader(directory) : long.MaxValue;
                bool individuallySmall = readable && bytes <= maximumIndividualBytes;

                if (!individuallySmall)
                {
                    if (FinishRun())
                    {
                        run = selected;
                        return true;
                    }
                    ResetRun();
                    continue;
                }

                bool isFullBuildPaging = IsFullBuildPagingLayer(baseManifest, segmentManifest!);
                if (incrementalOnly && isFullBuildPaging)
                {
                    if (FinishRun())
                    {
                        run = selected;
                        return true;
                    }
                    ResetRun();
                    continue;
                }
                if (ids.Count > 0
                    && (ids.Count >= maximumSegments
                        || total > maximumTotalBytes - bytes
                        || runIsFullBuildPaging != isFullBuildPaging))
                {
                    if (FinishRun())
                    {
                        run = selected;
                        return true;
                    }
                    ResetRun();
                }

                if (start < 0)
                {
                    start = i;
                    runIsFullBuildPaging = isFullBuildPaging;
                }
                ids.Add(segmentId);
                directories.Add(directory);
                total += bytes;
            }

            if (FinishRun())
            {
                run = selected;
                return true;
            }
            return false;
        }
        return false;
    }

    private static bool IsFullBuildPagingLayer(IndexManifest baseManifest, IndexManifest segmentManifest)
        => segmentManifest.LastIncrementalUpdateUtc is null
            && segmentManifest.FreshnessCheckpoint == baseManifest.FreshnessCheckpoint;

    /// <summary>
    /// Atomically replaces <paramref name="run"/> in the active pointer with one equivalent merged segment.
    /// The merged directory is fully written and validated before the redundant lower-sequence pointer slot
    /// changes. The other slot continues to reference the pre-merge run, preserving rollback safety; normal
    /// retention removes superseded directories only after neither valid pointer references them.
    /// </summary>
    internal bool TryReplaceSegmentRunUnderLease(
        IndexMutationContext mutation,
        SegmentCoalesceRun run,
        ContentIndexDeltaSegment mergedSegment)
    {
        ArgumentNullException.ThrowIfNull(mergedSegment);
        return TryReplaceSegmentRunCoreUnderLease(
            mutation,
            run,
            tempDir =>
            {
                ContentIndexDeltaSegmentSerializer.Write(tempDir, mergedSegment);
                if (ProduceV3QueryStructures)
                {
                    TryWriteV3Structures(tempDir,
                        () => ContentIndexV3Format.Write(tempDir, mergedSegment.Added, mergedSegment.RemovedPaths));
                }
            });
    }

    /// <summary>
    /// Publishes a merged segment that a streaming merge already wrote (and, when enabled, already produced
    /// format-v3 sidecars for) in a private workspace. The prepared directory is moved into place and then
    /// runs through the identical validate → mark → promote → redundant pointer flip → retention protocol as
    /// an in-memory merge, so the crash/rollback guarantees and fault points are unchanged.
    /// </summary>
    internal bool TryReplacePreparedSegmentRunUnderLease(
        IndexMutationContext mutation,
        SegmentCoalesceRun run,
        string preparedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparedDirectory);
        if (!Directory.Exists(preparedDirectory))
            return false;
        return TryReplaceSegmentRunCoreUnderLease(
            mutation,
            run,
            tempDir => Directory.Move(preparedDirectory, tempDir));
    }

    private bool TryReplaceSegmentRunCoreUnderLease(
        IndexMutationContext mutation,
        SegmentCoalesceRun run,
        Action<string> writeToTempDir)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(writeToTempDir);

        SlotContents? active = null;
        foreach (SlotContents slot in ReadValidSlotsNewestFirst()) { active = slot; break; }
        if (active is null || run.StartIndex < 0
            || run.StartIndex + run.SegmentIds.Count > active.Value.SegmentIds.Count)
        {
            return false;
        }
        for (int i = 0; i < run.SegmentIds.Count; i++)
        {
            if (!string.Equals(
                    active.Value.SegmentIds[run.StartIndex + i],
                    run.SegmentIds[i],
                    StringComparison.Ordinal))
            {
                return false; // active pointer changed or the caller supplied a stale/non-contiguous run
            }
        }

        Directory.CreateDirectory(SegmentsDir);
        long segmentSequence = NextSequence(CurrentMaxSegmentSequence(), "segment");
        string segmentId = SegmentPrefix + segmentSequence.ToString("D6", CultureInfo.InvariantCulture);
        string tempDir = Path.Combine(SegmentsDir, "." + segmentId + ".tmp");
        string finalDir = Path.Combine(SegmentsDir, segmentId);

        DeleteDirectorySafe(tempDir);
        writeToTempDir(tempDir);
            IndexMutationFaults.Hit(IndexMutationFaults.CoalesceWritten);
        if (!ContentIndexDeltaSegmentSerializer.TryValidateSerializedSegment(tempDir, out _))
        {
            DeleteDirectorySafe(tempDir);
            return false;
        }
        IndexMutationFaults.Hit(IndexMutationFaults.CoalesceValidated);
        MarkImport(tempDir);
        IndexMutationFaults.Hit(IndexMutationFaults.CoalesceMarked);
        DeleteDirectorySafe(finalDir);
        Directory.Move(tempDir, finalDir);
        IndexMutationFaults.Hit(IndexMutationFaults.CoalescePromoted);

        var newSegmentIds = active.Value.SegmentIds.ToList();
        newSegmentIds.RemoveRange(run.StartIndex, run.SegmentIds.Count);
        newSegmentIds.Insert(run.StartIndex, segmentId);
        WriteTargetSlot(NextSequence(CurrentMaxSequence(), "pointer"), active.Value.GenerationId, newSegmentIds);
        IndexMutationFaults.Hit(IndexMutationFaults.CoalescePointerPublished);
        DeleteFileSafe(Path.Combine(finalDir, ImportMarkerFile));
        IndexMutationFaults.Hit(IndexMutationFaults.CoalesceMarkerCleared);
        OpenedLayeredIndexCache.Remove(_scopeDir);
        RetainAfterCommit();
        IndexMutationFaults.Hit(IndexMutationFaults.CoalesceCleanupFinished);
        YaguLog.For("ContentIndex").LogInformation(
            "Coalesced {InputCount} small segments ({InputMB} MB) into '{SegmentId}' for scope {Scope}; active segment count {Before} -> {After}.",
            run.SegmentIds.Count, run.TotalBytes / (1024 * 1024), segmentId, _scopeId,
            active.Value.SegmentIds.Count, newSegmentIds.Count);
        return true;
    }

    /// <summary>Number of active delta segments layered over the current base (0 when none / untrusted).</summary>
    public int ActiveSegmentCount()
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            if (ContentIndexGenerationSerializer.TryReadManifest(Path.Combine(GenerationsDir, slot.GenerationId)) is not null)
                return slot.SegmentIds.Count;
        }
        return 0;
    }

    /// <summary>Cheap storage stats for the current index, read from MANIFESTS ONLY (never the content /
    /// posting data): the document count (base + every segment), segment count, build time, and root path.
    /// Reporting a paged multi-GB index therefore costs a few KB of I/O, not gigabytes of RAM. Returns a
    /// <c>Readable == false</c> value when no trusted base is present.</summary>
    public StoredIndexStat ReadStorageStat()
    {
        StoredIndexStat? newestProblem = null;
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            StoredIndexStat candidate = DescribeStorageSlot(slot);
            if (candidate.Readable)
                return candidate;
            newestProblem ??= candidate;
        }

        // If both redundant pointers are damaged (or reference missing artifacts), scan generation
        // manifests only. A checksum-valid manifest whose scope identity recomputes to this directory is
        // safe for DISPLAY/repair targeting even though it remains completely untrusted for searching.
        if (newestProblem is { RootPath: not null })
            return newestProblem.Value;
        StoredIndexStat? recovered = RecoverStorageIdentityFromGenerationManifests();
        if (recovered is { RootPath: not null })
            return recovered.Value;
        return newestProblem
            ?? new StoredIndexStat(
                IndexStorageHealth.CorruptOrIncomplete, 0, 0, null, null, null, null,
                "No valid pointer or checksum-valid generation manifest could identify this index.");
    }

    private StoredIndexStat DescribeStorageSlot(SlotContents slot)
    {
        ContentIndexGenerationSerializer.ManifestDiagnostic baseDiagnostic =
            ContentIndexGenerationSerializer.ReadManifestDiagnostic(Path.Combine(GenerationsDir, slot.GenerationId));
        IndexManifest? baseManifest = baseDiagnostic.Manifest;
        if (baseManifest is null || !IsManifestForThisScope(baseManifest))
        {
            return new StoredIndexStat(
                IndexStorageHealth.CorruptOrIncomplete, 0, slot.SegmentIds.Count, null, null, null, null,
                $"The active pointer references base generation '{slot.GenerationId}', but its manifest is missing, corrupt, or belongs to another scope.");
        }

        IndexStorageHealth baseHealth = HealthFor(baseDiagnostic.Verdict);
        if (baseHealth != IndexStorageHealth.Healthy)
        {
            return new StoredIndexStat(
                baseHealth, baseManifest.ContentCount, slot.SegmentIds.Count,
                baseManifest.BuiltUtc, baseManifest.CreatedUtc ?? baseManifest.BuiltUtc,
                baseManifest.LastIncrementalUpdateUtc, baseManifest.NormalizedRootPath,
                ProblemFor(baseDiagnostic.Verdict, baseManifest));
        }

        long documents = baseManifest.ContentCount;
        DateTimeOffset activeGenerationBuiltUtc = baseManifest.BuiltUtc;
        DateTimeOffset? lastIncrementalUpdateUtc = baseManifest.LastIncrementalUpdateUtc;
        foreach (string segId in slot.SegmentIds)
        {
            ContentIndexGenerationSerializer.ManifestDiagnostic segmentDiagnostic =
                ContentIndexGenerationSerializer.ReadManifestDiagnostic(Path.Combine(SegmentsDir, segId));
            IndexManifest? segmentManifest = segmentDiagnostic.Manifest;
            if (segmentManifest is null || !IsManifestForThisScope(segmentManifest))
            {
                return new StoredIndexStat(
                    IndexStorageHealth.CorruptOrIncomplete, documents, slot.SegmentIds.Count,
                    activeGenerationBuiltUtc, baseManifest.CreatedUtc ?? baseManifest.BuiltUtc,
                    lastIncrementalUpdateUtc, baseManifest.NormalizedRootPath,
                    $"Active segment '{segId}' is missing, corrupt, or belongs to another scope.");
            }

            documents += segmentManifest.ContentCount;
            if (segmentManifest.LastIncrementalUpdateUtc is { } segmentIncrementalUtc
                && (lastIncrementalUpdateUtc is not { } currentIncrementalUtc
                    || segmentIncrementalUtc > currentIncrementalUtc))
            {
                lastIncrementalUpdateUtc = segmentIncrementalUtc;
            }
            else if (IsFullBuildPagingLayer(baseManifest, segmentManifest)
                     && segmentManifest.BuiltUtc > activeGenerationBuiltUtc)
            {
                // Paged full builds use the segment store too and retain the base checkpoint. Legacy
                // incremental segments may have no provenance timestamp, but their advanced checkpoint
                // keeps them from being mislabeled as a full-build page.
                activeGenerationBuiltUtc = segmentManifest.BuiltUtc;
            }
            IndexStorageHealth segmentHealth = HealthFor(segmentDiagnostic.Verdict);
            if (segmentHealth != IndexStorageHealth.Healthy)
            {
                return new StoredIndexStat(
                    segmentHealth, documents, slot.SegmentIds.Count,
                    activeGenerationBuiltUtc, baseManifest.CreatedUtc ?? baseManifest.BuiltUtc,
                    lastIncrementalUpdateUtc, baseManifest.NormalizedRootPath,
                    $"Active segment '{segId}' is incompatible. {ProblemFor(segmentDiagnostic.Verdict, segmentManifest)}");
            }
        }

        return new StoredIndexStat(
            IndexStorageHealth.Healthy, documents, slot.SegmentIds.Count,
            activeGenerationBuiltUtc, baseManifest.CreatedUtc ?? baseManifest.BuiltUtc,
            lastIncrementalUpdateUtc, baseManifest.NormalizedRootPath, null);
    }

    private StoredIndexStat? RecoverStorageIdentityFromGenerationManifests()
    {
        try
        {
            foreach (string generationDir in ExistingGenerationDirectoriesReader()
                         .OrderByDescending(path => ParseGenerationSequence(Path.GetFileName(path)) ?? -1))
            {
                ContentIndexGenerationSerializer.ManifestDiagnostic diagnostic =
                    ContentIndexGenerationSerializer.ReadManifestDiagnostic(generationDir);
                IndexManifest? manifest = diagnostic.Manifest;
                if (manifest is null || !IsManifestForThisScope(manifest))
                    continue;

                IndexStorageHealth health = HealthFor(diagnostic.Verdict);
                string problem = health == IndexStorageHealth.Healthy
                    ? "A valid generation manifest was found, but both active pointer slots are missing or damaged."
                    : ProblemFor(diagnostic.Verdict, manifest);
                return new StoredIndexStat(
                    health == IndexStorageHealth.Healthy ? IndexStorageHealth.CorruptOrIncomplete : health,
                    manifest.ContentCount, 0, manifest.BuiltUtc, manifest.CreatedUtc ?? manifest.BuiltUtc,
                    manifest.LastIncrementalUpdateUtc, manifest.NormalizedRootPath, problem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return null;
    }

    internal bool IsManifestForThisScope(IndexManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ScopeId)
            || string.IsNullOrWhiteSpace(manifest.VolumeIdentity)
            || string.IsNullOrWhiteSpace(manifest.NormalizedRootPath)
            || !string.Equals(manifest.ScopeId, _scopeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            // Unit fixtures intentionally use readable synthetic ids such as "scope-id". Real persisted
            // scope directories are always 32 hex characters and get the stronger root recomputation check.
            if (_scopeId.Length != 32 || !_scopeId.All(Uri.IsHexDigit))
                return true;
            string root = ManifestRootNormalizer(manifest.NormalizedRootPath);
            string volume = ManifestPathRootReader(root) ?? manifest.VolumeIdentity;
            string recomputed = IndexScopeIdentity.ComputeScopeId(volume, root);
            return string.Equals(recomputed, _scopeId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static IndexStorageHealth HealthFor(IndexStructuralVerdict verdict) => verdict switch
    {
        IndexStructuralVerdict.Trusted => IndexStorageHealth.Healthy,
        IndexStructuralVerdict.IncompatibleFormat => IndexStorageHealth.IncompatibleFormat,
        IndexStructuralVerdict.IncompatibleRepresentation => IndexStorageHealth.IncompatibleRepresentation,
        _ => IndexStorageHealth.CorruptOrIncomplete,
    };

    internal static string ProblemFor(IndexStructuralVerdict verdict, IndexManifest manifest) => verdict switch
    {
        IndexStructuralVerdict.IncompatibleFormat =>
            $"Built with index format v{manifest.IndexFormatVersion}; this build requires v{IndexManifest.CurrentFormatVersion}.",
        IndexStructuralVerdict.IncompatibleRepresentation =>
            $"Built with content representation v{manifest.ContentRepresentationVersion}; this build requires v{ContentRepresentation.Version}.",
        IndexStructuralVerdict.Missing => "The manifest is missing.",
        _ => "The manifest is corrupt or incomplete.",
    };

    /// <summary>Total on-disk bytes of the current base's active delta segments (for the compaction size
    /// trigger, plan §11.4). 0 when there is no trusted base.</summary>
    public long TotalActiveSegmentBytes()
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            if (ContentIndexGenerationSerializer.TryReadManifest(Path.Combine(GenerationsDir, slot.GenerationId)) is null)
                continue;
            long total = 0;
            foreach (string segId in slot.SegmentIds)
                total += DirectorySizeReader(Path.Combine(SegmentsDir, segId));
            return total;
        }
        return 0;
    }

    /// <summary>Cheap total on-disk byte size of the ACTIVE index (newest valid base generation + its active
    /// delta segments), validated via MANIFESTS ONLY (never deserializing content/postings) so it never pulls
    /// the multi-GB index into memory. Lets a caller bound the memory cost of an in-process compaction BEFORE
    /// opening the (potentially huge) layered index. 0 when there is no trusted base.</summary>
    public long TotalActiveIndexBytes()
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            string baseDir = Path.Combine(GenerationsDir, slot.GenerationId);
            if (ContentIndexGenerationSerializer.TryReadManifest(baseDir) is null)
                continue;
            long total = DirectorySizeReader(baseDir);
            foreach (string segId in slot.SegmentIds)
                total += DirectorySizeReader(Path.Combine(SegmentsDir, segId));
            return total;
        }
        return 0;
    }

    /// <summary>
    /// Splits the active layers into base / full-build paging / incremental cohorts using the pointer
    /// slot, layer manifests, and directory sizes only — never <c>content.bin</c> or postings. Returns
    /// <see langword="null"/> when no trusted base is present or any active segment manifest is
    /// unreadable, so a caller can never misclassify a layer it could not identify.
    /// </summary>
    public ActiveLayerStorageBreakdown? TryReadActiveLayerStorageBreakdown()
        => TryReadActiveLayerStorageTrend()?.Breakdown;

    /// <summary>
    /// Reads the active storage cohorts plus the oldest/newest incremental-layer timestamps. Pointer,
    /// manifests, and directory sizes only; never opens content or postings.
    /// </summary>
    public ActiveLayerStorageTrend? TryReadActiveLayerStorageTrend()
    {
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            string baseDir = Path.Combine(GenerationsDir, slot.GenerationId);
            IndexManifest? baseManifest = ContentIndexGenerationSerializer.TryReadManifest(baseDir);
            if (baseManifest is null)
                continue;

            long pagingBytes = 0;
            int pagingCount = 0;
            long incrementalBytes = 0;
            int incrementalCount = 0;
            DateTimeOffset? oldestIncrementalBuiltUtc = null;
            DateTimeOffset? newestIncrementalBuiltUtc = null;
            foreach (string segId in slot.SegmentIds)
            {
                string segmentDir = Path.Combine(SegmentsDir, segId);
                IndexManifest? segmentManifest = ContentIndexGenerationSerializer.TryReadManifest(segmentDir);
                if (segmentManifest is null)
                    return null;

                long bytes = DirectorySizeReader(segmentDir);
                if (IsFullBuildPagingLayer(baseManifest, segmentManifest))
                {
                    pagingBytes += bytes;
                    pagingCount++;
                }
                else
                {
                    incrementalBytes += bytes;
                    incrementalCount++;
                    DateTimeOffset builtUtc = segmentManifest.BuiltUtc;
                    if (oldestIncrementalBuiltUtc is null || builtUtc < oldestIncrementalBuiltUtc)
                        oldestIncrementalBuiltUtc = builtUtc;
                    if (newestIncrementalBuiltUtc is null || builtUtc > newestIncrementalBuiltUtc)
                        newestIncrementalBuiltUtc = builtUtc;
                }
            }

            return new ActiveLayerStorageTrend(
                new ActiveLayerStorageBreakdown(
                    DirectorySizeReader(baseDir), 1, pagingBytes, pagingCount, incrementalBytes, incrementalCount),
                oldestIncrementalBuiltUtc,
                newestIncrementalBuiltUtc);
        }
        return null;
    }

    /// <summary>
    /// Whether the layered index should be compacted into a fresh base (plan §11.4): the active segment
    /// count exceeds <paramref name="maxDeltaSegments"/> OR their accumulated size exceeds
    /// <paramref name="compactionThresholdMB"/>, whichever is hit first.
    /// </summary>
    public bool ShouldCompact(int maxDeltaSegments, int compactionThresholdMB)
    {
        if (ActiveSegmentCount() > Math.Max(1, maxDeltaSegments))
            return true;
        long thresholdBytes = (long)Math.Max(1, compactionThresholdMB) * 1024 * 1024;
        return TotalActiveSegmentBytes() > thresholdBytes;
    }

    /// <summary>
    /// Publishes <paramref name="compactedBase"/> as the new base with an empty segment list — the caller
    /// has already folded the old base + all segments into it (plan §11.4). Retention then drops the old
    /// base and the now-orphaned segments. This is just <see cref="Publish"/>; named for intent/tests.
    /// </summary>
    public PublishResult Compact(ContentIndexGeneration compactedBase) => Publish(compactedBase);

    /// <summary>
    /// Publishes a base that a streaming compaction already wrote in a private workspace. The prepared
    /// directory is moved into the generations folder and then runs through the same staged validate →
    /// mark → promote → single pointer flip → retention protocol as any other base, so the previous
    /// generation stays intact as the rollback point until normal retention releases it.
    /// </summary>
    internal PublishResult CompactFromPreparedUnderLease(IndexMutationContext mutation, string preparedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparedDirectory);
        if (!Directory.Exists(preparedDirectory))
            throw new InvalidDataException("The prepared compaction directory no longer exists.");
        return PublishStagedBase(mutation, tempDir => Directory.Move(preparedDirectory, tempDir));
    }

    internal PublishResult CompactUnderLease(IndexMutationContext mutation, ContentIndexGeneration compactedBase)
        => PublishUnderLease(mutation, compactedBase);

    internal void RecoverOrphansUnderLease(IndexMutationContext mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_paths);
        Retain();
    }

    /// <summary>
    /// Imports a completely-built private scope into this live scope. Immutable base/segment directories
    /// are remapped to unused live ids first; only after every move succeeds is one redundant pointer slot
    /// flipped. Readers therefore keep using the prior complete index throughout a rebuild and see the new
    /// complete index only after the final durable slot write.
    /// </summary>
    internal StagedIndexCommitResult ImportStagedUnderLease(IndexMutationContext mutation, ContentIndexStore staged)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(staged);
        mutation.EnsureOwns(_paths);
        mutation.EnsureOwns(staged._paths);

        SlotContents? sourceSlot = null;
        IndexManifest? sourceManifest = null;
        foreach (SlotContents slot in staged.ReadValidSlotsNewestFirst())
        {
            IndexManifest? candidateManifest = ContentIndexGenerationSerializer.TryReadManifest(
                Path.Combine(staged.GenerationsDir, slot.GenerationId));
            if (candidateManifest is null)
                continue;
            bool segmentsReadable = slot.SegmentIds.All(segmentId =>
            {
                IndexManifest? segmentManifest = ContentIndexGenerationSerializer.TryReadManifest(
                    Path.Combine(staged.SegmentsDir, segmentId));
                return segmentManifest is not null
                    && VolumeManifestsAgree(candidateManifest, segmentManifest);
            });
            if (segmentsReadable)
            {
                sourceSlot = slot;
                sourceManifest = candidateManifest;
                break;
            }
        }
        if (sourceSlot is null || sourceManifest is null)
            throw new InvalidDataException("The staged build has no complete active index to commit.");

        Directory.CreateDirectory(GenerationsDir);
        Directory.CreateDirectory(SegmentsDir);

        long pointerSequence = NextSequence(CurrentMaxSequence(), "generation/pointer");
        string baseId = GenerationPrefix + pointerSequence.ToString("D6", CultureInfo.InvariantCulture);
        string sourceBase = Path.Combine(staged.GenerationsDir, sourceSlot.Value.GenerationId);
        string destinationBase = Path.Combine(GenerationsDir, baseId);
        BeforeImportDestinationCheck?.Invoke(destinationBase);
        if (Directory.Exists(destinationBase))
            throw new IOException($"The target generation '{baseId}' already exists.");
        MarkImport(sourceBase);
        IndexMutationFaults.Hit(IndexMutationFaults.ImportBaseMarked);
        Directory.Move(sourceBase, destinationBase);
        IndexMutationFaults.Hit(IndexMutationFaults.ImportBaseMoved);

        var importedSegments = new List<string>(sourceSlot.Value.SegmentIds.Count);
        long segmentSequence = CurrentMaxSegmentSequence();
        foreach (string sourceSegmentId in sourceSlot.Value.SegmentIds)
        {
            segmentSequence = NextSequence(segmentSequence, "segment");
            string targetSegmentId = SegmentPrefix + segmentSequence.ToString("D6", CultureInfo.InvariantCulture);
            string sourceSegment = Path.Combine(staged.SegmentsDir, sourceSegmentId);
            string destinationSegment = Path.Combine(SegmentsDir, targetSegmentId);
            BeforeImportDestinationCheck?.Invoke(destinationSegment);
            if (Directory.Exists(destinationSegment))
                throw new IOException($"The target segment '{targetSegmentId}' already exists.");
            MarkImport(sourceSegment);
            IndexMutationFaults.Hit(IndexMutationFaults.ImportSegmentMarked);
            Directory.Move(sourceSegment, destinationSegment);
            IndexMutationFaults.Hit(IndexMutationFaults.ImportSegmentMoved);
            importedSegments.Add(targetSegmentId);
        }

        // This is the only visibility switch. A crash before it leaves the old pointer active; a crash
        // during it is recovered by the other redundant slot.
        VolumeBinding currentVolume = CurrentVolumeReader(sourceManifest.NormalizedRootPath)
            ?? throw new IndexVolumeChangedException(
                $"The indexed volume for '{sourceManifest.NormalizedRootPath}' is unavailable at commit time.");
        if (!VolumeBindingReader.MatchesManifest(sourceManifest, currentVolume, out string volumeReason))
        {
            throw new IndexVolumeChangedException(
                $"The indexed volume changed before commit ({volumeReason}). The previous index remains active.");
        }
        IndexMutationFaults.Hit(IndexMutationFaults.ImportBeforePointer);
        WriteTargetSlot(pointerSequence, baseId, importedSegments);
        DeleteFileSafe(Path.Combine(_scopeDir, ContentIndexManager.AutomaticCompactionFailureFile));
        IndexMutationFaults.Hit(IndexMutationFaults.ImportPointerPublished);
        DeleteFileSafe(Path.Combine(destinationBase, ImportMarkerFile));
        foreach (string segmentId in importedSegments)
            DeleteFileSafe(Path.Combine(SegmentsDir, segmentId, ImportMarkerFile));
        IndexMutationFaults.Hit(IndexMutationFaults.ImportMarkersCleared);
        OpenedLayeredIndexCache.Remove(_scopeDir);
        RetainAfterCommit();
        IndexMutationFaults.Hit(IndexMutationFaults.ImportCleanupFinished);
        return new StagedIndexCommitResult(
            baseId,
            pointerSequence,
            importedSegments.Count == 0 ? baseId : importedSegments[^1]);
    }

    internal static bool VolumeManifestsAgree(IndexManifest first, IndexManifest second)
        => string.Equals(first.NormalizedRootPath, second.NormalizedRootPath, StringComparison.OrdinalIgnoreCase)
            && first.VolumeSerialNumber == second.VolumeSerialNumber
            && string.Equals(first.VolumeGuidPath, second.VolumeGuidPath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.FileSystemName, second.FileSystemName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.VolumeRelativeRootPath, second.VolumeRelativeRootPath, StringComparison.OrdinalIgnoreCase);

    private long CurrentMaxSegmentSequence()
    {
        long max = 0;
        if (Directory.Exists(SegmentsDir))
        {
            foreach (string dir in Directory.GetDirectories(SegmentsDir))
            {
                if (TryParseSegmentSequence(Path.GetFileName(dir), out long seq))
                    max = Math.Max(max, seq);
            }
        }
        return max;
    }

    internal static bool TryParseSegmentSequence(string name, out long sequence)
    {
        sequence = 0;
        if (!name.StartsWith(SegmentPrefix, StringComparison.Ordinal))
            return false;
        return long.TryParse(name.AsSpan(SegmentPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);
    }

    internal static long DirectorySizeBytes(string dir)
        => DirectorySizeBytes(
            dir,
            Directory.Exists,
            directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories),
            file => new FileInfo(file).Length);

    internal static long DirectorySizeBytes(
        string dir,
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateFiles,
        Func<string, long> fileLength)
    {
        if (!directoryExists(dir))
            return 0;
        long total = 0;
        try
        {
            foreach (string file in enumerateFiles(dir))
            {
                try { total += fileLength(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
        return total;
    }

    // ─────────────────────────── pointer slots ───────────────────────────

    /// <summary>A parsed pointer slot: the active base generation plus its ordered active delta segments
    /// (empty for a freshly published or legacy base).</summary>
    private readonly record struct SlotContents(long Sequence, string GenerationId, IReadOnlyList<string> SegmentIds);

    private void WriteTargetSlot(long sequence, string generationId, IReadOnlyList<string> segmentIds)
    {
        // Write to the slot that currently has the lower sequence (or is missing/invalid), so the newest
        // valid slot is never overwritten before the new one is durable.
        long seqA = TryReadSlot(SlotA, out var a) ? a.Sequence : -1;
        long seqB = TryReadSlot(SlotB, out var b) ? b.Sequence : -1;
        string target = seqA <= seqB ? SlotA : SlotB;
        WriteSlot(target, sequence, generationId, segmentIds);
    }

    private void WriteSlot(string slot, long sequence, string generationId, IReadOnlyList<string> segmentIds)
    {
        // Payload lines: sequence, generationId, segmentCsv. The digest is over the joined payload so a
        // torn/edited slot is rejected. A legacy 2-line payload (no segment line) still validates on read.
        // Write a same-directory temp and atomically replace the lower slot: a process crash therefore leaves
        // that slot entirely old or entirely new (the other redundant slot remains a rollback point too).
        string segmentCsv = string.Join(',', segmentIds);
        string payload = sequence.ToString(CultureInfo.InvariantCulture) + "\n" + generationId + "\n" + segmentCsv;
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        string content = payload + "\n" + digest + "\n";
        string path = Path.Combine(_scopeDir, slot);
        string tempPath = path + ".tmp";
        DeleteFileSafe(tempPath);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        IndexMutationFaults.Hit(IndexMutationFaults.PointerTempFlushed);
        File.Move(tempPath, path, overwrite: true);
        IndexMutationFaults.Hit(IndexMutationFaults.PointerPublished);
    }

    private bool TryReadSlot(string slot, out SlotContents parsed)
    {
        parsed = default;
        string path = Path.Combine(_scopeDir, slot);
        if (!File.Exists(path))
            return false;
        try
        {
            string[] lines = File.ReadAllText(path).Split('\n', StringSplitOptions.None);
            // New format: sequence, generationId, segmentCsv, digest (4+ lines).
            if (lines.Length >= 4)
            {
                string payload = lines[0] + "\n" + lines[1] + "\n" + lines[2];
                string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                if (string.Equals(expected, lines[3].Trim(), StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq))
                {
                    var segs = lines[2].Length == 0
                        ? (IReadOnlyList<string>)Array.Empty<string>()
                        : lines[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
                    parsed = new SlotContents(seq, lines[1], segs);
                    return true;
                }
            }
            // Legacy format: sequence, generationId, digest (3 lines, no segments).
            if (lines.Length >= 3)
            {
                string payload = lines[0] + "\n" + lines[1];
                string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                if (string.Equals(expected, lines[2].Trim(), StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq))
                {
                    parsed = new SlotContents(seq, lines[1], Array.Empty<string>());
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private List<SlotContents> ReadValidSlotsNewestFirst()
    {
        var slots = new List<SlotContents>();
        if (TryReadSlot(SlotA, out var a)) slots.Add(a);
        if (TryReadSlot(SlotB, out var b)) slots.Add(b);
        slots.Sort((x, y) => y.Sequence.CompareTo(x.Sequence));
        return slots;
    }

    // ─────────────────────────── retention & helpers ───────────────────────────

    private long CurrentMaxSequence()
    {
        long max = 0;
        if (TryReadSlot(SlotA, out var a)) max = Math.Max(max, a.Sequence);
        if (TryReadSlot(SlotB, out var b)) max = Math.Max(max, b.Sequence);
        foreach (string dir in ExistingGenerationDirs())
        {
            if (TryParseGenerationSequence(Path.GetFileName(dir), out long seq))
                max = Math.Max(max, seq);
        }
        return max;
    }

    private void Retain()
    {
        IndexMutationFaults.Hit(IndexMutationFaults.RetentionStarted);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            referenced.Add(slot.GenerationId);
        }

        var generations = ExistingGenerationDirs()
            .Select(dir => (Dir: dir, Name: Path.GetFileName(dir), Sequence: ParseGenerationSequence(Path.GetFileName(dir))))
            .Where(g => g.Sequence.HasValue)
            .OrderByDescending(g => g.Sequence!.Value)
            .ToList();

        int kept = 0;
        foreach (var (dir, name, _) in generations)
        {
            if (kept < _retainedGenerations || referenced.Contains(name))
            {
                kept++;
                continue;
            }
            DeleteDirectorySafe(dir);
        }

        ScavengeDefiniteOrphans();
    }

    private void RetainAfterCommit()
    {
        try
        {
            Retain();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The pointer is already durable. Cleanup is idempotent and is retried automatically on the
            // next writer-lease acquisition, so it must never turn a committed mutation into a failure.
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Post-commit retention failed for scope {Scope}; the committed index remains active and recovery will retry cleanup.",
                _scopeId);
        }
    }

    private void ScavengeDefiniteOrphans()
    {
        var referencedGenerations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SlotContents slot in ReadValidSlotsNewestFirst())
        {
            referencedGenerations.Add(slot.GenerationId);
            foreach (string segId in slot.SegmentIds)
                referencedSegments.Add(segId);
        }

        DeleteFileSafe(Path.Combine(_scopeDir, SlotA + ".tmp"));
        DeleteFileSafe(Path.Combine(_scopeDir, SlotB + ".tmp"));

        // Also drop stale temp directories (".*.tmp") that never became generations.
        foreach (string dir in Directory.Exists(GenerationsDir) ? Directory.GetDirectories(GenerationsDir) : Array.Empty<string>())
        {
            string name = Path.GetFileName(dir);
            if (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                DeleteDirectorySafe(dir);
            else if (File.Exists(Path.Combine(dir, ImportMarkerFile)))
            {
                if (referencedGenerations.Contains(name))
                    DeleteFileSafe(Path.Combine(dir, ImportMarkerFile));
                else
                    DeleteDirectorySafe(dir);
            }
            if (Directory.Exists(dir))
                DeleteArtifactTempFiles(dir);
        }

        // Drop delta segments no valid pointer references (compacted-away or orphaned temp segments).
        if (Directory.Exists(SegmentsDir))
        {
            foreach (string dir in Directory.GetDirectories(SegmentsDir))
            {
                string name = Path.GetFileName(dir);
                bool isTemp = name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                if (isTemp || (TryParseSegmentSequence(name, out _) && !referencedSegments.Contains(name)))
                    DeleteDirectorySafe(dir);
                else if (referencedSegments.Contains(name))
                    DeleteFileSafe(Path.Combine(dir, ImportMarkerFile));
                if (Directory.Exists(dir))
                    DeleteArtifactTempFiles(dir);
            }
        }
    }

    private static void DeleteArtifactTempFiles(string directory)
    {
        DeleteFileSafe(Path.Combine(directory, ContentIndexGenerationSerializer.ManifestFile + ".reanchor.tmp"));
        foreach (string name in new[]
        {
            ContentIndexV3Format.PostingsFile,
            ContentIndexV3Format.PathIndexFile,
            ContentIndexV3Format.IdentitiesFile,
            ContentIndexV3Format.TombstonesFile,
        })
        {
            DeleteFileSafe(Path.Combine(directory, name + ".tmp"));
        }
    }

    private IEnumerable<string> ExistingGenerationDirs()
    {
        if (!Directory.Exists(GenerationsDir))
            return Array.Empty<string>();
        return Directory.GetDirectories(GenerationsDir)
            .Where(d => Path.GetFileName(d).StartsWith(GenerationPrefix, StringComparison.Ordinal));
    }

    internal static bool TryParseGenerationSequence(string name, out long sequence)
    {
        sequence = 0;
        if (!name.StartsWith(GenerationPrefix, StringComparison.Ordinal))
            return false;
        return long.TryParse(name.AsSpan(GenerationPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out sequence);
    }

    internal static long? ParseGenerationSequence(string name)
        => TryParseGenerationSequence(name, out long sequence) ? sequence : null;

    internal static long NextSequence(long current, string kind)
    {
        if (current < 0 || current == long.MaxValue)
            throw new InvalidDataException($"The {kind} sequence is invalid or exhausted ({current}).");
        return current + 1;
    }

    private static void MarkImport(string directory)
    {
        using var stream = new FileStream(
            Path.Combine(directory, ImportMarkerFile), FileMode.Create, FileAccess.Write, FileShare.None, 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteFileSafe(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteDirectorySafe(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — a leftover directory is cleaned up on a later retain pass.
        }
    }
}

/// <summary>
/// A tiny process-wide cache of the most-recently opened QUERY-mode layered index handle, keyed by scope
/// directory. The cached generations are immutable (read-only postings/alias tables), so repeated searches
/// of the same unchanged index reuse them across searches — and, since concurrent read-only sharing is
/// safe, across the query + provenance-classification paths — instead of re-deserializing the base and
/// every segment each time (the source of the per-search memory spike on a large layered index). Validity
/// is the pointer-slot signature (generation id + segment ids): any rebuild / compaction / segment append
/// changes it, forcing a re-open. Bounded to <see cref="MaxEntries"/> so it never holds more than the
/// working set a single active search already retained.
/// </summary>
internal static class OpenedLayeredIndexCache
{
    // 1 = only the most-recently searched scope. Keeps the retained footprint at ~one opened index (the same
    // as the pre-cache behaviour, which held the active search's gates until the next search) while removing
    // the re-deserialize churn on repeated searches of that scope.
    private const int MaxEntries = 1;

    private static readonly object Gate = new();
    private static readonly LinkedList<Entry> Entries = new();

    private sealed record Entry(string ScopeDir, string Signature, ContentIndexStore.LayeredIndexHandle Handle);

    /// <summary>Returns the cached handle for <paramref name="scopeDir"/> iff its signature matches (the
    /// index is unchanged); a stale entry for the scope is dropped. Moves a hit to most-recently-used.</summary>
    public static ContentIndexStore.LayeredIndexHandle? TryGet(string scopeDir, string signature)
    {
        lock (Gate)
        {
            for (LinkedListNode<Entry>? node = Entries.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.ScopeDir, scopeDir, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(node.Value.Signature, signature, StringComparison.Ordinal))
                {
                    Entries.Remove(node);
                    Entries.AddFirst(node);
                    return node.Value.Handle;
                }
                Entries.Remove(node); // stale generation for this scope
                return null;
            }
            return null;
        }
    }

    /// <summary>Caches <paramref name="handle"/> as the current opened index for <paramref name="scopeDir"/>,
    /// replacing any prior entry for that scope and evicting the least-recently-used beyond the bound.</summary>
    public static void Store(string scopeDir, string signature, ContentIndexStore.LayeredIndexHandle handle)
    {
        lock (Gate)
        {
            for (LinkedListNode<Entry>? node = Entries.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.ScopeDir, scopeDir, StringComparison.OrdinalIgnoreCase))
                {
                    Entries.Remove(node);
                    break;
                }
            }
            Entries.AddFirst(new Entry(scopeDir, signature, handle));
            while (Entries.Count > MaxEntries)
                Entries.RemoveLast();
        }
    }

    /// <summary>Drops the cached handle for <paramref name="scopeDir"/> (on delete of that scope's index).</summary>
    public static void Remove(string scopeDir)
    {
        lock (Gate)
        {
            for (LinkedListNode<Entry>? node = Entries.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.ScopeDir, scopeDir, StringComparison.OrdinalIgnoreCase))
                {
                    Entries.Remove(node);
                    return;
                }
            }
        }
    }

    /// <summary>Drops every cached handle (e.g. under memory pressure, or to isolate a test).</summary>
    public static void Clear()
    {
        lock (Gate)
            Entries.Clear();
    }
}
