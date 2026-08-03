using System;
using System.Collections.Generic;

namespace Yagu.Services.Index;

/// <summary>
/// Classifies a discovered path against a single generation's <b>memory-mapped format-v3 query
/// structures</b> (<see cref="ContentIndexV3Reader"/>, plan §5.1) instead of the deserialized in-memory
/// <see cref="ContentIndexGeneration"/> — the mapped equivalent of <see cref="ContentIndexQuerySession"/>
/// (base-only). This is the classification brain the out-of-process <c>Yagu.IndexWorker</c> mapped
/// shadow-mode uses (plan §6 Stage 2): it holds no index bytes resident, reading only the mapped pages a
/// lookup touches through the reader, so the classifying process' footprint is paged on demand rather than
/// tracking index size.
/// <para>
/// The classification is <b>byte-for-byte identical</b> to <see cref="ContentIndexQuerySession.Classify"/>:
/// the v3 path index reproduces <see cref="ContentIndexGeneration.TryGetAlias"/> exactly (collision-verified,
/// plan §5.1), the v3 postings reproduce <see cref="TrigramPostingIndex.EvaluateSet"/> exactly (proven by
/// <c>ContentIndexV3FormatTests</c>/<c>ContentIndexRustParityTests</c>), and the forward identity table
/// reproduces <see cref="ContentIndexGeneration.HasCapturedContentIdentity"/> (a null identity means the
/// content could not be captured, so USN can never dirty it — never prune it). Routing a search through the
/// mapped session therefore never changes which files are scanned.
/// </para>
/// <para>
/// This session is a pure classifier: it does <b>not</b> own the <see cref="ContentIndexV3Reader"/> (the
/// caller pins and disposes it), and — matching the Stage 2 "shadow mode" contract — it does not prune,
/// track a provisional set, or reconcile at B1. Those follow once shadow classification is proven to match
/// the in-process oracle.
/// </para>
/// </summary>
public sealed class V3MappedQuerySession
{
    private readonly ContentIndexV3Reader _reader;
    private readonly IReadOnlySet<int> _candidateContentIds;
    private readonly DirtyContentSet _dirtyAtB0;

    private V3MappedQuerySession(
        ContentIndexV3Reader reader,
        IReadOnlySet<int> candidateContentIds,
        DirtyContentSet dirtyAtB0)
    {
        _reader = reader;
        _candidateContentIds = candidateContentIds;
        _dirtyAtB0 = dirtyAtB0;
    }

    /// <summary>The number of documents the planned trigram query selected as candidates (plan §6.1),
    /// mirroring <see cref="ContentIndexQuerySession.CandidateCount"/> for the selectivity guard.</summary>
    public int CandidateCount => _candidateContentIds.Count;

    /// <summary>
    /// Begins a mapped query session by evaluating the planned query into the candidate content-id set over
    /// the mapped postings and snapshotting the dirty set at barrier B0. The candidate set is identical to
    /// <see cref="ContentIndexQuerySession.Begin"/>'s in-process evaluation.
    /// </summary>
    public static V3MappedQuerySession Begin(
        ContentIndexV3Reader reader,
        TrigramExpression query,
        DirtyContentSet dirtyAtB0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(dirtyAtB0);
        return new V3MappedQuerySession(reader, reader.EvaluateSet(query), dirtyAtB0);
    }

    /// <summary>
    /// Begins a mapped query session from a <b>pre-computed</b> candidate content-id set instead of
    /// evaluating the planned query in this process (plan §3.3) — the seam the worker uses when the native
    /// engine (<c>yagu_core.dll</c>) evaluated the mapped postings. The candidate ids must be the result of
    /// evaluating the <em>same</em> planned query against the <em>same</em> generation, so the classification
    /// stays identical to <see cref="Begin"/>.
    /// </summary>
    public static V3MappedQuerySession BeginWithCandidates(
        ContentIndexV3Reader reader,
        IReadOnlySet<int> candidateContentIds,
        DirtyContentSet dirtyAtB0)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(candidateContentIds);
        ArgumentNullException.ThrowIfNull(dirtyAtB0);
        return new V3MappedQuerySession(reader, candidateContentIds, dirtyAtB0);
    }

    /// <summary>
    /// Classifies a discovered normalized path against the mapped generation (plan §3.5). Reproduces
    /// <see cref="ContentIndexQuerySession.Classify"/> exactly, reading only the mapped pages the lookups
    /// touch.
    /// </summary>
    public IndexPathClassification Classify(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        if (!_reader.TryLookupPath(normalizedPath, out long aliasId, out long contentId))
            return new IndexPathClassification.Unindexed("absent from index");

        if (_dirtyAtB0.IsDirty(contentId))
            return new IndexPathClassification.DirtyByUsn(contentId, "changed since build");

        if (_candidateContentIds.Contains((int)contentId))
            return new IndexPathClassification.FreshIndexedMember(aliasId, contentId);

        // A nonmember can only be safely pruned if USN could later dirty it. A content whose durable file
        // identity was not captured at build time is invisible to the change journal, so a post-B0 edit
        // could never mark it dirty and B1 could never rescue it — never prune it; live-scan instead.
        if (_reader.TryGetIdentity((int)contentId) is null)
            return new IndexPathClassification.DirtyByUsn(contentId, "no captured file identity (cannot prove freshness)");

        return new IndexPathClassification.FreshIndexedNonmember(aliasId, contentId);
    }
}
