namespace Yagu.Services.Index;

/// <summary>
/// Orchestrates a single indexed query against one <see cref="ContentIndexGeneration"/> (plan §5),
/// tying the planned trigram query, posting membership, per-path classification (§3.5), and the trust
/// policy (§3.4) together. It classifies each discovered path, provisionally prunes fresh nonmembers,
/// and performs the final <b>B0/B1 reconciliation</b>: any provisionally-pruned path whose content was
/// marked dirty between the start-of-query barrier B0 and the end-of-discovery barrier B1 is returned
/// for a live scan, so a match added after B0 can never be hidden (invariant §5.1 #3).
/// </summary>
public sealed class ContentIndexQuerySession
{
    private readonly ContentIndexGeneration _generation;
    private readonly IReadOnlySet<int> _candidateContentIds;
    private readonly DirtyContentSet _dirtyAtB0;
    private readonly Dictionary<long, long> _provisionalAliasToContent = new();

    private ContentIndexQuerySession(
        ContentIndexGeneration generation,
        IReadOnlySet<int> candidateContentIds,
        DirtyContentSet dirtyAtB0)
    {
        _generation = generation;
        _candidateContentIds = candidateContentIds;
        _dirtyAtB0 = dirtyAtB0;
    }

    /// <summary>The number of documents the planned trigram query selected as candidates (plan §6.1). Used
    /// by the selectivity guard: a candidate set that is too large a fraction of the corpus is not worth
    /// accelerating.</summary>
    public int CandidateCount => _candidateContentIds.Count;

    /// <summary>
    /// Whether the whole generation may accelerate a search, composed through the single trust surface
    /// (structural version + root freshness + query eligibility). A convenience over
    /// <see cref="IndexTrustDecision.DecideGeneration"/>.
    /// </summary>
    public static GenerationDecision CanAccelerate(
        ContentIndexGeneration generation,
        RootFreshnessVerdict rootFreshness,
        bool queryEligible)
    {
        ArgumentNullException.ThrowIfNull(generation);
        return IndexTrustDecision.DecideGeneration(new IndexTrustInputs(
            generation.Manifest.EvaluateStructural(),
            rootFreshness,
            queryEligible));
    }

    /// <summary>
    /// Begins a query session by evaluating the planned query into the candidate content-id set and
    /// snapshotting the dirty set at barrier B0.
    /// </summary>
    public static ContentIndexQuerySession Begin(
        ContentIndexGeneration generation,
        TrigramExpression query,
        DirtyContentSet dirtyAtB0)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(dirtyAtB0);
        var candidates = generation.Postings.EvaluateSet(query);
        return new ContentIndexQuerySession(generation, candidates, dirtyAtB0);
    }

    /// <summary>
    /// Begins a query session from a <b>pre-computed</b> candidate content-id set instead of evaluating the
    /// planned query in-process (plan §3.3). This is the seam the out-of-process <c>Yagu.IndexWorker</c>
    /// path uses: the worker verifies + queries the generation's <c>content.bin</c> and returns the
    /// candidate ids, which are byte-for-byte identical to the in-process
    /// <see cref="TrigramPostingIndex.EvaluateSet"/> result (proven by <c>ContentIndexRustParityTests</c>).
    /// The caller is responsible for having evaluated the <em>same</em> planned query against the
    /// <em>same</em> generation; everything downstream (classification, provisional pruning, B0/B1
    /// reconciliation) is unchanged, so substituting the worker never changes results.
    /// </summary>
    public static ContentIndexQuerySession BeginWithCandidates(
        ContentIndexGeneration generation,
        IReadOnlySet<int> candidateContentIds,
        DirtyContentSet dirtyAtB0)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(candidateContentIds);
        ArgumentNullException.ThrowIfNull(dirtyAtB0);
        return new ContentIndexQuerySession(generation, candidateContentIds, dirtyAtB0);
    }

    /// <summary>The alias ids currently held in the provisional-prune set (awaiting B1 reconciliation).</summary>
    public IReadOnlyCollection<long> ProvisionalAliases => _provisionalAliasToContent.Keys;

    /// <summary>Classifies a discovered normalized path against this generation (plan §3.5).</summary>
    public IndexPathClassification Classify(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        if (!_generation.TryGetAlias(normalizedPath, out long aliasId, out long contentId))
            return new IndexPathClassification.Unindexed("absent from index");

        if (_dirtyAtB0.IsDirty(contentId))
            return new IndexPathClassification.DirtyByUsn(contentId, "changed since build");

        if (_candidateContentIds.Contains((int)contentId))
            return new IndexPathClassification.FreshIndexedMember(aliasId, contentId);

        // A nonmember can only be safely pruned if USN could later dirty it. A content whose durable file
        // identity was not captured at build time is invisible to the change journal, so a post-B0 edit
        // could never mark it dirty and B1 could never rescue it — never prune it; live-scan instead.
        if (!_generation.HasCapturedContentIdentity(contentId))
            return new IndexPathClassification.DirtyByUsn(contentId, "no captured file identity (cannot prove freshness)");

        return new IndexPathClassification.FreshIndexedNonmember(aliasId, contentId);
    }

    /// <summary>
    /// Classifies and routes a discovered path: fresh nonmembers are recorded in the provisional set and
    /// returned as <see cref="PathDecision.ProvisionalPrune"/>; everything else is
    /// <see cref="PathDecision.LiveScanPath"/>.
    /// </summary>
    public PathDecision Route(string normalizedPath)
    {
        IndexPathClassification classification = Classify(normalizedPath);
        PathDecision decision = IndexTrustDecision.DecidePath(classification);

        if (decision is PathDecision.ProvisionalPrune prune
            && classification is IndexPathClassification.FreshIndexedNonmember nonmember)
        {
            _provisionalAliasToContent[prune.AliasId] = nonmember.ContentId;
        }
        return decision;
    }

    /// <summary>
    /// Final reconciliation at barrier B1 (plan §3.5). Returns the provisional alias ids that must now be
    /// live-scanned because their content became dirty between B0 and B1. When
    /// <paramref name="reconciliationCertain"/> is false (an unresolved in-root create/rename/hard-link or
    /// a journal gap), <b>every</b> provisional alias is returned for a live scan (the conservative
    /// fallback). Once reconciled, the returned aliases are removed from the provisional set.
    /// </summary>
    public IReadOnlyList<long> ReconcileAtB1(DirtyContentSet dirtyAtB1, bool reconciliationCertain = true)
    {
        ArgumentNullException.ThrowIfNull(dirtyAtB1);

        List<long> mustLiveScan;
        if (!reconciliationCertain)
        {
            mustLiveScan = new List<long>(_provisionalAliasToContent.Keys);
        }
        else
        {
            mustLiveScan = new List<long>();
            foreach (var (aliasId, contentId) in _provisionalAliasToContent)
            {
                if (dirtyAtB1.IsDirty(contentId))
                    mustLiveScan.Add(aliasId);
            }
        }

        foreach (long aliasId in mustLiveScan)
            _provisionalAliasToContent.Remove(aliasId);

        mustLiveScan.Sort();
        return mustLiveScan;
    }

    /// <summary>
    /// Resolves provisional/reconciled alias ids back to normalized paths without requiring the search gate
    /// to retain a second alias-to-path dictionary. The generation already owns the canonical path table, so
    /// the uncommon B1 rescue path scans that table once and retains no duplicate path references.
    /// </summary>
    internal IReadOnlyList<string> ResolveAliasPaths(IReadOnlyCollection<long> aliasIds)
    {
        ArgumentNullException.ThrowIfNull(aliasIds);
        if (aliasIds.Count == 0)
            return Array.Empty<string>();

        IReadOnlySet<long> wanted = aliasIds as IReadOnlySet<long> ?? new HashSet<long>(aliasIds);
        var paths = new List<string>(Math.Min(aliasIds.Count, _generation.AliasCount));
        foreach (KeyValuePair<string, (long AliasId, long ContentId)> pair in _generation.Aliases)
        {
            if (wanted.Contains(pair.Value.AliasId))
                paths.Add(pair.Key);
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>Releases all per-query provisional state after the B1 paths have been resolved.</summary>
    internal void ClearProvisionalAliases() => _provisionalAliasToContent.Clear();
}
