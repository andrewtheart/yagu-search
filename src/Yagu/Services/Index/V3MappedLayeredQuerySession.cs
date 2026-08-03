using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// Classifies a discovered path against a <b>layered</b> memory-mapped index — one base
/// <see cref="ContentIndexV3Reader"/> plus an ordered list of segment readers — with <b>newest-first</b>
/// semantics (plan §3.4/§5.6). It is the mapped equivalent of <see cref="LayeredContentIndexQuerySession"/>
/// that the out-of-process worker's Stage-2 shadow mode uses: it holds no index bytes resident, reading only
/// the mapped pages each per-layer lookup touches (postings candidates, path index, forward identities, and
/// the <b>tombstone index</b>).
/// <para>
/// The classification is <b>byte-for-byte identical</b> to
/// <see cref="LayeredContentIndexQuerySession.Classify"/>: for each layer newest-first, a tombstone shadows
/// older layers (<see cref="ContentIndexV3Reader.ContainsTombstone"/> == <c>Layer.Removed.Contains</c>), the
/// v3 path index reproduces <see cref="ContentIndexGeneration.TryGetAlias"/>, the v3 postings reproduce
/// <see cref="TrigramPostingIndex.EvaluateSet"/>, and a null forward identity reproduces
/// <c>!HasCapturedContentIdentity</c> (a nonmember with no captured identity live-scans, never prunes).
/// </para>
/// <para>
/// <b>Tombstone requirement:</b> a layer only correctly shadows older layers if its v3 has a tombstone index
/// (<see cref="ContentIndexV3Reader.HasTombstoneIndex"/>). Use <see cref="AllLayersHaveTombstoneIndex"/> to
/// gate the mapped path <b>all-or-nothing</b> before constructing the session — if any layer lacks it (an
/// older 3-file v3), fall back to the in-process layered evaluation so a tombstone can never be silently
/// missed. The session does not own the readers (the caller pins/disposes them).
/// </para>
/// <para>
/// <b>Pruning (plan §6 Stage 4).</b> <see cref="Classify"/> is a <b>pure</b> classifier (Stage-2 shadow
/// mode). For the Stage-4 out-of-process pruning path the same session gains an <b>opt-in</b> pruning brain,
/// the mapped equivalent of <see cref="LayeredContentIndexQuerySession.Route"/> /
/// <see cref="LayeredContentIndexQuerySession.ReconcileAtB1"/>: <see cref="RouteForPruning"/> records a
/// fresh posting nonmember as provisionally pruned (keyed by its normalized path, bound to the layer whose
/// posting made it a nonmember — the worker already holds the path strings, so no reverse alias→path index
/// is needed), and <see cref="ReconcileAtB1"/> returns the provisional paths whose layer's content became
/// dirty over <c>[B0, B1)</c> so they are live-scanned after all. The provisional set is only populated by
/// <see cref="RouteForPruning"/>; a session that only ever calls <see cref="Classify"/> stays a pure,
/// side-effect-free classifier.
/// </para>
/// </summary>
public sealed class V3MappedLayeredQuerySession
{
    private sealed class Layer
    {
        public required ContentIndexV3Reader Reader { get; init; }
        public required IReadOnlySet<int> Candidates { get; init; }
        public required DirtyContentSet DirtyAtB0 { get; init; }
    }

    // Layers in NEWEST-FIRST order (segments reversed, then the base last).
    private readonly List<Layer> _layers;

    // Flattened path-hash → NEWEST owning layer routing table. Without this, classifying each discovered
    // path walks every layer and performs two mapped binary searches (tombstone + path) per layer — O(N×L),
    // which made a 1.8M-path / 54-layer C:\ search spend ~60s classifying paths. This table is built once,
    // sequentially, when the worker opens the scope and makes each classification O(1) + one exact mapped
    // lookup. Hashes are routing hints only: the selected layer still collision-verifies the full UTF-8 path.
    // A cross-layer hash collision can therefore only return Unindexed (live-scan), never a false prune.
    private readonly Dictionary<ulong, int> _newestOwnerLayerByHash;

    // Provisionally-pruned paths (opt-in; populated only by RouteForPruning). Keyed by the discovered
    // normalized path — the worker holds the path strings, so B1 rescue needs no reverse alias→path index.
    // The value binds the prune to the LAYER (index into _layers, newest-first) whose posting proved it a
    // nonmember, so B1 reconciliation checks that layer's own [B0, B1) dirty set.
    private readonly Dictionary<string, (int LayerIndex, long ContentId)> _provisional =
        new(StringComparer.Ordinal);

    private V3MappedLayeredQuerySession(
        List<Layer> newestFirstLayers,
        int candidateCount,
        bool candidatesEvaluatedInWorker,
        double candidateEvaluationMs)
    {
        _layers = newestFirstLayers;
        var routingTimer = Stopwatch.StartNew();
        _newestOwnerLayerByHash = BuildNewestOwnerLayerIndex(
            newestFirstLayers,
            out long pathRecordCount,
            out long tombstoneRecordCount);
        routingTimer.Stop();
        CandidateCount = candidateCount;
        CandidatesEvaluatedInWorker = candidatesEvaluatedInWorker;
        CandidateEvaluationMs = candidateEvaluationMs;
        RoutingIndexMs = routingTimer.Elapsed.TotalMilliseconds;
        PathRecordCount = pathRecordCount;
        TombstoneRecordCount = tombstoneRecordCount;
    }

    /// <summary>Upper-bound count of candidate documents across all layers (for the selectivity guard only;
    /// never affects correctness), mirroring <see cref="LayeredContentIndexQuerySession.CandidateCount"/>.</summary>
    public int CandidateCount { get; }

    /// <summary>Number of mapped layers (base + active segments).</summary>
    public int LayerCount => _layers.Count;

    /// <summary>Alias/path records visited while building the newest-owner route.</summary>
    public long PathRecordCount { get; }

    /// <summary>Tombstone records visited while building the newest-owner route.</summary>
    public long TombstoneRecordCount { get; }

    /// <summary>Total route input records. Comparing this with <see cref="DistinctRouteHashCount"/> reveals
    /// superseded path history without treating disjoint memory-bounded build layers as fragmentation.</summary>
    public long RouteRecordCount => PathRecordCount + TombstoneRecordCount;

    /// <summary>Distinct newest-owner route hashes after replacement/tombstone precedence is applied.</summary>
    public int DistinctRouteHashCount => _newestOwnerLayerByHash.Count;

    /// <summary>Whether this session evaluated mapped postings itself rather than receiving candidate sets.</summary>
    public bool CandidatesEvaluatedInWorker { get; }

    /// <summary>Elapsed mapped-postings candidate evaluation time for all layers.</summary>
    public double CandidateEvaluationMs { get; }

    /// <summary>Elapsed time to build the flattened newest-owner routing table.</summary>
    public double RoutingIndexMs { get; }

    /// <summary>Number of distinct routed path hashes. Internal diagnostics/tests prove the layered session
    /// built the flattened O(1) route rather than falling back to a per-path layer walk.</summary>
    internal int RoutedPathHashCount => DistinctRouteHashCount;

    private static Dictionary<ulong, int> BuildNewestOwnerLayerIndex(
        IReadOnlyList<Layer> newestFirstLayers,
        out long pathRecordCount,
        out long tombstoneRecordCount)
    {
        pathRecordCount = 0;
        tombstoneRecordCount = 0;
        foreach (Layer layer in newestFirstLayers)
        {
            pathRecordCount += layer.Reader.PathCount;
            tombstoneRecordCount += layer.Reader.TombstoneCount;
        }

        long totalEntries = pathRecordCount + tombstoneRecordCount;
        int capacity = (int)Math.Min(totalEntries, int.MaxValue);
        var owners = new Dictionary<ulong, int>(capacity);

        // First writer wins because layers are newest-first. Tombstones go first within a layer so an
        // impossible/defensive same-layer path+tombstone duplicate fails safe as tombstoned.
        for (int layerIndex = 0; layerIndex < newestFirstLayers.Count; layerIndex++)
        {
            ContentIndexV3Reader reader = newestFirstLayers[layerIndex].Reader;
            for (int i = 0; i < reader.TombstoneCount; i++)
                owners.TryAdd(reader.TombstoneHashAt(i), layerIndex);
            for (int i = 0; i < reader.PathCount; i++)
                owners.TryAdd(reader.PathHashAt(i), layerIndex);
        }

        return owners;
    }

    /// <summary>
    /// Whether the base and <b>every</b> segment reader carries a tombstone index — the precondition for
    /// using the mapped layered path (a missing tombstone index on any layer means a tombstone could be
    /// silently missed, so the caller must fall back to the in-process layered evaluation).
    /// </summary>
    public static bool AllLayersHaveTombstoneIndex(ContentIndexV3Reader baseReader, IReadOnlyList<ContentIndexV3Reader> segmentReaders)
    {
        ArgumentNullException.ThrowIfNull(baseReader);
        ArgumentNullException.ThrowIfNull(segmentReaders);
        return baseReader.HasTombstoneIndex && segmentReaders.All(r => r.HasTombstoneIndex);
    }

    /// <summary>
    /// Begins a layered mapped query. <paramref name="segmentReaders"/> are oldest → newest;
    /// <paramref name="segmentDirtiesAtB0"/> must be 1:1 with them. Each layer's candidate set is evaluated
    /// from its mapped postings (identical to the in-process posting evaluation).
    /// </summary>
    public static V3MappedLayeredQuerySession Begin(
        ContentIndexV3Reader baseReader,
        IReadOnlyList<ContentIndexV3Reader> segmentReaders,
        TrigramExpression query,
        DirtyContentSet baseDirtyAtB0,
        IReadOnlyList<DirtyContentSet> segmentDirtiesAtB0,
        int parallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(baseReader);
        ArgumentNullException.ThrowIfNull(segmentReaders);
        ArgumentNullException.ThrowIfNull(query);

        int degree = Math.Clamp(parallelism, 1, IndexWorkerParallelism.Maximum);
        var readers = new ContentIndexV3Reader[segmentReaders.Count + 1];
        readers[0] = baseReader;
        for (int i = 0; i < segmentReaders.Count; i++)
            readers[i + 1] = segmentReaders[i];

        var candidateSets = new IReadOnlySet<int>[readers.Length];
        var candidateTimer = Stopwatch.StartNew();
        RunBounded(readers.Length, degree, i => candidateSets[i] = readers[i].EvaluateSet(query));
        candidateTimer.Stop();

        IReadOnlySet<int> baseCandidates = candidateSets[0];
        var segmentCandidates = new IReadOnlySet<int>[segmentReaders.Count];
        Array.Copy(candidateSets, 1, segmentCandidates, 0, segmentCandidates.Length);

        return BuildFromReaders(
            baseReader,
            segmentReaders,
            baseCandidates,
            segmentCandidates,
            baseDirtyAtB0,
            segmentDirtiesAtB0,
            candidatesEvaluatedInWorker: true,
            candidateTimer.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Begins a layered mapped query from <b>pre-computed</b> candidate sets (one per layer) produced by an
    /// alternative backend — the native engine (<c>yagu_core.dll</c>) evaluating each layer's mapped
    /// postings. Because each set is byte-identical to that layer's in-process
    /// <see cref="TrigramPostingIndex.EvaluateSet"/>, classification is unchanged — only the candidate
    /// producer differs. <paramref name="segmentCandidates"/> is 1:1 with <paramref name="segmentReaders"/>.
    /// </summary>
    public static V3MappedLayeredQuerySession BeginWithCandidates(
        ContentIndexV3Reader baseReader,
        IReadOnlyList<ContentIndexV3Reader> segmentReaders,
        IReadOnlySet<int> baseCandidates,
        IReadOnlyList<IReadOnlySet<int>> segmentCandidates,
        DirtyContentSet baseDirtyAtB0,
        IReadOnlyList<DirtyContentSet> segmentDirtiesAtB0)
    {
        ArgumentNullException.ThrowIfNull(baseReader);
        ArgumentNullException.ThrowIfNull(segmentReaders);
        ArgumentNullException.ThrowIfNull(baseCandidates);
        ArgumentNullException.ThrowIfNull(segmentCandidates);
        if (segmentReaders.Count != segmentCandidates.Count)
            throw new ArgumentException("segmentCandidates must be 1:1 with segmentReaders.", nameof(segmentCandidates));

        return BuildFromReaders(
            baseReader,
            segmentReaders,
            baseCandidates,
            segmentCandidates,
            baseDirtyAtB0,
            segmentDirtiesAtB0,
            candidatesEvaluatedInWorker: false,
            candidateEvaluationMs: 0);
    }

    private static V3MappedLayeredQuerySession BuildFromReaders(
        ContentIndexV3Reader baseReader,
        IReadOnlyList<ContentIndexV3Reader> segmentReaders,
        IReadOnlySet<int> baseCandidates,
        IReadOnlyList<IReadOnlySet<int>> segmentCandidates,
        DirtyContentSet baseDirtyAtB0,
        IReadOnlyList<DirtyContentSet> segmentDirtiesAtB0,
        bool candidatesEvaluatedInWorker,
        double candidateEvaluationMs)
    {
        ArgumentNullException.ThrowIfNull(baseDirtyAtB0);
        ArgumentNullException.ThrowIfNull(segmentDirtiesAtB0);
        if (segmentReaders.Count != segmentDirtiesAtB0.Count)
            throw new ArgumentException("segmentDirtiesAtB0 must be 1:1 with segmentReaders.", nameof(segmentDirtiesAtB0));

        int candidateCount = 0;
        var layers = new List<Layer>(segmentReaders.Count + 1);

        // Build newest-first: newest segment → oldest segment → base.
        for (int i = segmentReaders.Count - 1; i >= 0; i--)
        {
            IReadOnlySet<int> cands = segmentCandidates[i];
            candidateCount += cands.Count;
            layers.Add(new Layer
            {
                Reader = segmentReaders[i],
                Candidates = cands,
                DirtyAtB0 = segmentDirtiesAtB0[i],
            });
        }

        candidateCount += baseCandidates.Count;
        layers.Add(new Layer
        {
            Reader = baseReader,
            Candidates = baseCandidates,
            DirtyAtB0 = baseDirtyAtB0,
        });

        return new V3MappedLayeredQuerySession(
            layers,
            candidateCount,
            candidatesEvaluatedInWorker,
            candidateEvaluationMs);
    }

    /// <summary>
    /// Classifies a discovered normalized path against the layered mapped index (plan §3.5), newest-first.
    /// Reproduces <see cref="LayeredContentIndexQuerySession.Classify"/> exactly: a path tombstoned by a
    /// segment (or absent from every layer) is <see cref="IndexPathClassification.Unindexed"/>; otherwise the
    /// newest layer that holds it decides (dirty → <see cref="IndexPathClassification.DirtyByUsn"/>, member →
    /// <see cref="IndexPathClassification.FreshIndexedMember"/>, nonmember →
    /// <see cref="IndexPathClassification.FreshIndexedNonmember"/>).
    /// </summary>
    public IndexPathClassification Classify(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        return ClassifyCore(normalizedPath, out _, out _);
    }

    /// <summary>
    /// Classifies a batch with bounded read-only parallelism. Results preserve input order. When
    /// <paramref name="recordPruning"/> is true, fresh-nonmember decisions are recorded only after every
    /// lane completes, sequentially in input order; the mutable provisional dictionary is never shared by
    /// worker lanes and B1 reconciliation retains the exact legacy semantics.
    /// </summary>
    public IReadOnlyList<IndexPathClassification> ClassifyBatch(
        IReadOnlyList<string> normalizedPaths,
        int parallelism,
        bool recordPruning)
    {
        ArgumentNullException.ThrowIfNull(normalizedPaths);
        int count = normalizedPaths.Count;
        var classifications = new IndexPathClassification[count];
        var layerIndexes = new int[count];
        var contentIds = new long[count];
        int degree = Math.Clamp(parallelism, 1, IndexWorkerParallelism.Maximum);

        RunBounded(count, degree, i =>
        {
            string path = normalizedPaths[i] ?? throw new ArgumentNullException(nameof(normalizedPaths));
            classifications[i] = ClassifyCore(path, out layerIndexes[i], out contentIds[i]);
        });

        if (recordPruning)
        {
            for (int i = 0; i < count; i++)
                if (classifications[i] is IndexPathClassification.FreshIndexedNonmember)
                    _provisional[normalizedPaths[i]] = (layerIndexes[i], contentIds[i]);
        }

        return classifications;
    }

    /// <summary>
    /// The shared classification core. Returns the same closed <see cref="IndexPathClassification"/> as
    /// <see cref="Classify"/> and additionally reports, via the out parameters, the <b>winning layer</b>
    /// (index into <see cref="_layers"/>, newest-first; <c>-1</c> when the path is absent from every layer or
    /// tombstoned) and its <b>content id</b> (<c>-1</c> when there is none) — the binding
    /// <see cref="RouteForPruning"/> needs to reconcile a provisional prune against its own layer's B1 dirty
    /// set. Pure: it never mutates the provisional set.
    /// </summary>
    private IndexPathClassification ClassifyCore(string normalizedPath, out int layerIndex, out long contentId)
    {
        layerIndex = -1;
        contentId = -1;

        // Encode + hash ONCE per discovered path. The former layered walk encoded and hashed once for the
        // tombstone lookup and again for the path lookup in EVERY layer (up to 108 arrays/hashes on C:\).
        const int StackPathBytes = 512;
        int byteCount = Encoding.UTF8.GetByteCount(normalizedPath);
        byte[]? rented = null;
        Span<byte> target = byteCount <= StackPathBytes
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount)).AsSpan(0, byteCount);
        Encoding.UTF8.GetBytes(normalizedPath.AsSpan(), target);

        try
        {
            ulong pathHash = V3Fnv.Hash(target);
            if (!_newestOwnerLayerByHash.TryGetValue(pathHash, out int i))
                return new IndexPathClassification.Unindexed("absent from index");

            Layer layer = _layers[i];
            if (layer.Reader.ContainsTombstone(target, pathHash))
                return new IndexPathClassification.Unindexed("tombstoned (rediscovered after deletion)");

            // The routing hash can collide with an unrelated path in a newer layer. Exact verification
            // fails in that case and we conservatively live-scan; never probe an older layer and risk using
            // a shadowed entry or making a hash-only prune decision.
            if (!layer.Reader.TryLookupPath(target, pathHash, out long aliasId, out long cid))
                return new IndexPathClassification.Unindexed("path-hash collision (live scan)");

            layerIndex = i;
            contentId = cid;

            if (layer.DirtyAtB0.IsDirty(cid))
                return new IndexPathClassification.DirtyByUsn(cid, "changed since layer build");

            if (layer.Candidates.Contains((int)cid))
                return new IndexPathClassification.FreshIndexedMember(aliasId, cid);

            // A nonmember with no captured file identity is invisible to USN, so it can never be proven
            // fresh (dirtied) after B0 — never prune it; live-scan instead (see ContentIndexQuerySession).
            if (layer.Reader.TryGetIdentity((int)cid) is null)
                return new IndexPathClassification.DirtyByUsn(cid, "no captured file identity (cannot prove freshness)");

            return new IndexPathClassification.FreshIndexedNonmember(aliasId, cid);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Number of paths currently held provisionally pruned (awaiting B1 reconciliation).</summary>
    public int ProvisionalCount => _provisional.Count;

    /// <summary>The normalized paths currently held provisionally pruned (diagnostics / fail-safe rescue).</summary>
    public IReadOnlyCollection<string> ProvisionalPaths => _provisional.Keys;

    /// <summary>
    /// Classifies <paramref name="normalizedPath"/> and, when it is a <b>fresh posting nonmember</b> (the only
    /// prunable kind), records it as provisionally pruned and returns <c>true</c> (the caller may skip the
    /// content scan, subject to B1 reconciliation). Every other classification — member, dirty, unindexed,
    /// tombstoned, or a nonmember with no captured identity — returns <c>false</c> (the path must be
    /// live-scanned). This is the mapped equivalent of <see cref="LayeredContentIndexQuerySession.Route"/>;
    /// the resulting <paramref name="classification"/> is also returned so the worker can fold provenance
    /// (member vs live-scanned) into its reply. The provisional entry is keyed by the path itself, so B1
    /// rescue can name the exact paths to re-scan without a reverse alias→path index.
    /// </summary>
    public bool RouteForPruning(string normalizedPath, out IndexPathClassification classification)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        classification = ClassifyCore(normalizedPath, out int layerIndex, out long contentId);
        if (classification is IndexPathClassification.FreshIndexedNonmember)
        {
            _provisional[normalizedPath] = (layerIndex, contentId);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Final reconciliation at barrier B1 (plan §3.5 / §5.5). <paramref name="baseDirtyAtB1"/> /
    /// <paramref name="segmentDirtiesAtB1"/> are each layer's dirty set replayed over <c>[B0, B1)</c> — the
    /// segment dirty sets are 1:1 with, and in the same order as, the <c>segmentReaders</c> passed to
    /// <see cref="Begin"/> (oldest → newest). Returns the provisional paths that must now be live-scanned
    /// because their layer's content became dirty after B0, removing them from the provisional set. When a
    /// layer's B1 replay is not continuous, pass a conservatively dirty-everything set for that layer so all
    /// of its provisional paths are returned (or call <see cref="DrainAllProvisional"/> for a total rescue).
    /// </summary>
    public IReadOnlyList<string> ReconcileAtB1(DirtyContentSet baseDirtyAtB1, IReadOnlyList<DirtyContentSet> segmentDirtiesAtB1)
    {
        ArgumentNullException.ThrowIfNull(baseDirtyAtB1);
        ArgumentNullException.ThrowIfNull(segmentDirtiesAtB1);
        int segCount = _layers.Count - 1; // every layer except the base is a segment
        if (segmentDirtiesAtB1.Count != segCount)
            throw new ArgumentException("segmentDirtiesAtB1 must be 1:1 with the segment layers.", nameof(segmentDirtiesAtB1));

        // Map each layer index (newest-first) to its B1 dirty set. _layers = [newest seg, …, oldest seg,
        // base]; the segment layer at index k corresponds to original segment index (segCount - 1 - k), and
        // the last layer is the base. Mirrors LayeredContentIndexQuerySession.ReconcileAtB1 exactly.
        var dirtyByLayer = new DirtyContentSet[_layers.Count];
        for (int k = 0; k < _layers.Count; k++)
            dirtyByLayer[k] = k < segCount ? segmentDirtiesAtB1[segCount - 1 - k] : baseDirtyAtB1;

        var mustLiveScan = new List<string>();
        foreach (KeyValuePair<string, (int LayerIndex, long ContentId)> entry in _provisional)
        {
            if (dirtyByLayer[entry.Value.LayerIndex].IsDirty(entry.Value.ContentId))
                mustLiveScan.Add(entry.Key);
        }

        foreach (string path in mustLiveScan)
            _provisional.Remove(path);

        mustLiveScan.Sort(StringComparer.Ordinal);
        return mustLiveScan;
    }

    /// <summary>
    /// Returns <b>every</b> remaining provisional path and clears the provisional set — the fail-safe total
    /// rescue used when B1 cannot be reconciled with certainty (a discontinuous journal, a torn read, a
    /// worker fault, or any uncertainty). After this the session holds no prune decisions.
    /// </summary>
    public IReadOnlyList<string> DrainAllProvisional()
    {
        var all = new List<string>(_provisional.Keys);
        _provisional.Clear();
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    internal static void RunBounded(int itemCount, int parallelism, Action<int> action)
    {
        if (itemCount <= 0)
            return;
        int degree = Math.Min(itemCount, Math.Max(1, parallelism));
        if (degree == 1)
        {
            for (int i = 0; i < itemCount; i++)
                action(i);
            return;
        }

        var tasks = new Task[degree];
        for (int lane = 0; lane < degree; lane++)
        {
            int capturedLane = lane;
            tasks[lane] = Task.Run(() =>
            {
                for (int i = capturedLane; i < itemCount; i += degree)
                    action(i);
            });
        }
        Task.WhenAll(tasks).GetAwaiter().GetResult();
    }
}
