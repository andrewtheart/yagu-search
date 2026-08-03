namespace Yagu.Services.Index;

/// <summary>
/// Queries a layered index — one base <see cref="ContentIndexGeneration"/> plus an ordered list of
/// append-only <see cref="ContentIndexDeltaSegment"/>s (oldest → newest) — with <b>newest-first</b>
/// semantics (plan §3.4/§11.4). For a discovered path, the newest layer that has an alias for it (or
/// tombstones it) is authoritative; older layers are shadowed. This is the incremental analogue of
/// <see cref="ContentIndexQuerySession"/>, and preserves the same correctness invariant: it only ever
/// provisionally prunes a fresh posting nonmember (proven unchanged since that layer's build), and any
/// dirty / tombstoned / unindexed / uncertain path is live-scanned.
/// <para>
/// It is deliberately pure: the per-layer B0 dirty sets are supplied by the caller (the accelerator reads
/// each layer's change journal), so every branch is unit-testable without touching USN. Provisional-prune
/// alias ids are made globally unique across layers by a running counter, so the enclosing
/// <see cref="ContentIndexSearchGate"/> can track and reconcile them exactly as it does for a single
/// generation.
/// </para>
/// </summary>
public sealed class LayeredContentIndexQuerySession
{
    // A single queryable layer (base or one segment), captured at Begin.
    private sealed class Layer
    {
        public required ContentIndexGeneration Generation { get; init; }
        public required IReadOnlySet<int> Candidates { get; init; }
        public required DirtyContentSet DirtyAtB0 { get; init; }
        /// <summary>Tombstoned paths (segments only; empty for the base).</summary>
        public required IReadOnlySet<string> Removed { get; init; }
    }

    // Layers in NEWEST-FIRST order (segments reversed, then the base last).
    private readonly List<Layer> _layers;
    // Provisional prunes: global alias id → authoritative layer/local alias/content ids. The local alias id
    // lets B1 resolve a rescued path from the generation's existing path table, so the search gate does not
    // retain a duplicate Dictionary<long,string> for every pruned file.
    private readonly Dictionary<long, (Layer Layer, long LocalAliasId, long ContentId)> _provisional = new();
    // ReconcileAtB1 removes rescued entries from _provisional. Keep only those uncommon removed mappings until
    // the gate resolves their paths, then ClearProvisionalAliases releases both collections.
    private readonly Dictionary<long, (Layer Layer, long LocalAliasId, long ContentId)> _reconciled = new();
    private long _nextGlobalAlias;

    private LayeredContentIndexQuerySession(List<Layer> newestFirstLayers, int candidateCount)
    {
        _layers = newestFirstLayers;
        CandidateCount = candidateCount;
    }

    /// <summary>Upper-bound count of candidate documents across all layers (for the selectivity guard only;
    /// never affects correctness).</summary>
    public int CandidateCount { get; }

    /// <summary>Alias ids currently held in the provisional-prune set (awaiting B1 reconciliation).</summary>
    public IReadOnlyCollection<long> ProvisionalAliases => _provisional.Keys;

    /// <summary>
    /// Begins a layered query. <paramref name="segments"/> are oldest → newest;
    /// <paramref name="segmentDirtiesAtB0"/> must be 1:1 with them (each segment's dirty set at barrier B0).
    /// </summary>
    public static LayeredContentIndexQuerySession Begin(
        ContentIndexGeneration baseGeneration,
        IReadOnlyList<ContentIndexDeltaSegment> segments,
        TrigramExpression query,
        DirtyContentSet baseDirtyAtB0,
        IReadOnlyList<DirtyContentSet> segmentDirtiesAtB0)
    {
        ArgumentNullException.ThrowIfNull(baseGeneration);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(query);

        // Each layer's candidate set comes from its in-process posting index.
        IReadOnlySet<int> baseCandidates = baseGeneration.Postings.EvaluateSet(query);
        var segmentCandidates = new IReadOnlySet<int>[segments.Count];
        for (int i = 0; i < segments.Count; i++)
            segmentCandidates[i] = segments[i].Added.Postings.EvaluateSet(query);

        return BuildFromCandidates(baseGeneration, segments, baseCandidates, segmentCandidates, baseDirtyAtB0, segmentDirtiesAtB0);
    }

    /// <summary>
    /// Begins a layered query from candidate content-id sets produced by an alternative backend — the
    /// in-process memory-mapped format-v3 postings reader (plan §5.1), one per layer.
    /// <paramref name="baseCandidates"/> is the base generation's candidate set;
    /// <paramref name="segmentCandidates"/> is 1:1 with <paramref name="segments"/> (oldest → newest). Because
    /// each set is byte-identical to the in-process <see cref="TrigramPostingIndex.EvaluateSet"/> for the same
    /// layer, classification/reconciliation are unchanged — only the candidate producer differs.
    /// </summary>
    public static LayeredContentIndexQuerySession BeginWithCandidates(
        ContentIndexGeneration baseGeneration,
        IReadOnlyList<ContentIndexDeltaSegment> segments,
        IReadOnlySet<int> baseCandidates,
        IReadOnlyList<IReadOnlySet<int>> segmentCandidates,
        DirtyContentSet baseDirtyAtB0,
        IReadOnlyList<DirtyContentSet> segmentDirtiesAtB0)
    {
        ArgumentNullException.ThrowIfNull(baseGeneration);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(baseCandidates);
        ArgumentNullException.ThrowIfNull(segmentCandidates);
        if (segments.Count != segmentCandidates.Count)
            throw new ArgumentException("segmentCandidates must be 1:1 with segments.", nameof(segmentCandidates));

        return BuildFromCandidates(baseGeneration, segments, baseCandidates, segmentCandidates, baseDirtyAtB0, segmentDirtiesAtB0);
    }

    private static LayeredContentIndexQuerySession BuildFromCandidates(
        ContentIndexGeneration baseGeneration,
        IReadOnlyList<ContentIndexDeltaSegment> segments,
        IReadOnlySet<int> baseCandidates,
        IReadOnlyList<IReadOnlySet<int>> segmentCandidates,
        DirtyContentSet baseDirtyAtB0,
        IReadOnlyList<DirtyContentSet> segmentDirtiesAtB0)
    {
        ArgumentNullException.ThrowIfNull(baseDirtyAtB0);
        ArgumentNullException.ThrowIfNull(segmentDirtiesAtB0);
        if (segments.Count != segmentDirtiesAtB0.Count)
            throw new ArgumentException("segmentDirtiesAtB0 must be 1:1 with segments.", nameof(segmentDirtiesAtB0));

        var emptyRemoved = (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
        int candidateCount = 0;

        // Build newest-first: newest segment → oldest segment → base.
        var layers = new List<Layer>(segments.Count + 1);
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            ContentIndexDeltaSegment seg = segments[i];
            IReadOnlySet<int> cands = segmentCandidates[i];
            candidateCount += cands.Count;
            layers.Add(new Layer
            {
                Generation = seg.Added,
                Candidates = cands,
                DirtyAtB0 = segmentDirtiesAtB0[i],
                Removed = seg.RemovedPaths,
            });
        }

        candidateCount += baseCandidates.Count;
        layers.Add(new Layer
        {
            Generation = baseGeneration,
            Candidates = baseCandidates,
            DirtyAtB0 = baseDirtyAtB0,
            Removed = emptyRemoved,
        });

        return new LayeredContentIndexQuerySession(layers, candidateCount);
    }

    /// <summary>
    /// Classifies a discovered normalized path against the layered index (plan §3.5), newest-first. A path
    /// tombstoned by a segment (rediscovered after deletion) or absent from every layer is
    /// <see cref="IndexPathClassification.Unindexed"/> → live-scanned. Otherwise the newest layer that
    /// holds the path decides: dirty-since-its-build → <see cref="IndexPathClassification.DirtyByUsn"/>;
    /// a posting member → <see cref="IndexPathClassification.FreshIndexedMember"/>; a posting nonmember →
    /// <see cref="IndexPathClassification.FreshIndexedNonmember"/>.
    /// </summary>
    public IndexPathClassification Classify(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        foreach (Layer layer in _layers)
        {
            if (layer.Removed.Contains(normalizedPath))
                return new IndexPathClassification.Unindexed("tombstoned (rediscovered after deletion)");

            if (!layer.Generation.TryGetAlias(normalizedPath, out long aliasId, out long contentId))
                continue; // not in this layer — fall through to the next-older layer

            if (layer.DirtyAtB0.IsDirty(contentId))
                return new IndexPathClassification.DirtyByUsn(contentId, "changed since layer build");

            if (layer.Candidates.Contains((int)contentId))
                return new IndexPathClassification.FreshIndexedMember(aliasId, contentId);

            // A nonmember with no captured file identity is invisible to USN, so it can never be proven
            // fresh (dirtied) after B0 — never prune it; live-scan instead (see ContentIndexQuerySession).
            if (!layer.Generation.HasCapturedContentIdentity(contentId))
                return new IndexPathClassification.DirtyByUsn(contentId, "no captured file identity (cannot prove freshness)");

            return new IndexPathClassification.FreshIndexedNonmember(aliasId, contentId);
        }

        return new IndexPathClassification.Unindexed("absent from index");
    }

    /// <summary>
    /// Classifies and routes a discovered path: a fresh nonmember is recorded in the provisional set under
    /// a globally-unique alias id and returned as <see cref="PathDecision.ProvisionalPrune"/>; everything
    /// else is <see cref="PathDecision.LiveScanPath"/>.
    /// </summary>
    public PathDecision Route(string normalizedPath)
    {
        // Resolve the authoritative layer and classification together so we can bind the prune to its layer.
        foreach (Layer layer in _layers)
        {
            if (layer.Removed.Contains(normalizedPath))
                return new PathDecision.LiveScanPath("tombstoned (rediscovered after deletion)");

            if (!layer.Generation.TryGetAlias(normalizedPath, out long localAliasId, out long contentId))
                continue;

            if (layer.DirtyAtB0.IsDirty(contentId))
                return new PathDecision.LiveScanPath("dirty since layer build");

            if (layer.Candidates.Contains((int)contentId))
                return new PathDecision.LiveScanPath("fresh posting member");

            // A nonmember with no captured file identity can never be dirtied by USN — never prune it.
            if (!layer.Generation.HasCapturedContentIdentity(contentId))
                return new PathDecision.LiveScanPath("no captured file identity (cannot prove freshness)");

            long globalAlias = _nextGlobalAlias++;
            _provisional[globalAlias] = (layer, localAliasId, contentId);
            return new PathDecision.ProvisionalPrune(globalAlias);
        }

        return new PathDecision.LiveScanPath("absent from index");
    }

    /// <summary>
    /// Final reconciliation at barrier B1 (plan §3.5). <paramref name="baseDirtyAtB1"/> /
    /// <paramref name="segmentDirtiesAtB1"/> are each layer's dirty set replayed over <c>[B0, B1)</c>
    /// (1:1 with the segments passed to <see cref="Begin"/>). Returns the provisional alias ids that must
    /// now be live-scanned because their layer's content became dirty after B0. When any layer's B1 replay
    /// is not continuous, pass a conservatively-<c>dirty-everything</c> set for that layer so its
    /// provisional aliases are all returned. Returned aliases are removed from the provisional set.
    /// </summary>
    public IReadOnlyList<long> ReconcileAtB1(DirtyContentSet baseDirtyAtB1, IReadOnlyList<DirtyContentSet> segmentDirtiesAtB1)
    {
        ArgumentNullException.ThrowIfNull(baseDirtyAtB1);
        ArgumentNullException.ThrowIfNull(segmentDirtiesAtB1);
        if (segmentDirtiesAtB1.Count != _layers.Count - 1)
            throw new ArgumentException("segmentDirtiesAtB1 must be 1:1 with the segment layers.", nameof(segmentDirtiesAtB1));

        // Map each layer to its B1 dirty set. Layers are stored newest-first; the base is last.
        var dirtyByLayer = new Dictionary<Layer, DirtyContentSet>(_layers.Count);
        int segCount = segmentDirtiesAtB1.Count;
        // _layers = [newest seg, …, oldest seg, base]; segment layer at index k corresponds to
        // original segment index (segCount - 1 - k).
        for (int k = 0; k < _layers.Count; k++)
        {
            Layer layer = _layers[k];
            if (k < segCount)
            {
                int originalSegmentIndex = segCount - 1 - k;
                dirtyByLayer[layer] = segmentDirtiesAtB1[originalSegmentIndex];
            }
            else
            {
                dirtyByLayer[layer] = baseDirtyAtB1;
            }
        }

        var mustLiveScan = new List<long>();
        foreach (var (aliasId, entry) in _provisional)
        {
            if (dirtyByLayer[entry.Layer].IsDirty(entry.ContentId))
            {
                mustLiveScan.Add(aliasId);
                _reconciled[aliasId] = entry;
            }
        }

        foreach (long aliasId in mustLiveScan)
            _provisional.Remove(aliasId);

        mustLiveScan.Sort();
        return mustLiveScan;
    }

    /// <summary>
    /// Resolves globally-unique query alias ids back to normalized paths through each layer's existing alias
    /// table. Only local alias ids are tracked per prune; path strings are not duplicated by the search gate.
    /// </summary>
    internal IReadOnlyList<string> ResolveAliasPaths(IReadOnlyCollection<long> aliasIds)
    {
        ArgumentNullException.ThrowIfNull(aliasIds);
        if (aliasIds.Count == 0)
            return Array.Empty<string>();

        var wantedByLayer = new Dictionary<Layer, HashSet<long>>();
        foreach (long globalAliasId in aliasIds)
        {
            if (!_provisional.TryGetValue(globalAliasId, out var entry)
                && !_reconciled.TryGetValue(globalAliasId, out entry))
                continue;

            if (!wantedByLayer.TryGetValue(entry.Layer, out HashSet<long>? wanted))
            {
                wanted = new HashSet<long>();
                wantedByLayer.Add(entry.Layer, wanted);
            }
            wanted.Add(entry.LocalAliasId);
        }

        var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<Layer, HashSet<long>> layerEntry in wantedByLayer)
        {
            foreach (KeyValuePair<string, (long AliasId, long ContentId)> alias in layerEntry.Key.Generation.Aliases)
            {
                if (layerEntry.Value.Contains(alias.Value.AliasId))
                    uniquePaths.Add(alias.Key);
            }
        }

        var paths = new List<string>(uniquePaths);
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>Releases all per-query provisional and reconciled alias state after B1 path resolution.</summary>
    internal void ClearProvisionalAliases()
    {
        _provisional.Clear();
        _reconciled.Clear();
    }
}
