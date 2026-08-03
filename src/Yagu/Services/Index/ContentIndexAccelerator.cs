using System.Linq;
using Microsoft.Extensions.Logging;
using Yagu.Models;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// A live accelerated query bound to one trusted generation, or to a layered index (base + delta
/// segments) when Phase 3 incremental updates are active (plan §5/§11.4). Callers classify/route
/// discovered paths and reconcile at barrier B1 without touching the store, planner, or freshness
/// plumbing. Every classification is the single §3.5 verdict that also drives provenance and tests.
/// </summary>
public sealed class AcceleratedQuery
{
    private readonly ContentIndexQuerySession? _session;
    private readonly LayeredContentIndexQuerySession? _layered;
    private readonly IReadOnlyList<ContentIndexDeltaSegment>? _segments;
    private readonly IReadOnlyList<FreshnessRead>? _segmentFreshness;
    private readonly ContentIndexFreshnessEvaluator.JournalReader? _journalReader;

    internal AcceleratedQuery(
        ContentIndexGeneration generation,
        ContentIndexQuerySession session,
        FreshnessRead freshness,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader)
    {
        Generation = generation;
        _session = session;
        Freshness = freshness;
        _journalReader = journalReader;
    }

    /// <summary>Constructs a layered accelerated query over a base + ordered delta segments (Phase 3).</summary>
    internal AcceleratedQuery(
        ContentIndexGeneration baseGeneration,
        LayeredContentIndexQuerySession layered,
        FreshnessRead baseFreshness,
        IReadOnlyList<ContentIndexDeltaSegment> segments,
        IReadOnlyList<FreshnessRead> segmentFreshness,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader)
    {
        Generation = baseGeneration;
        _layered = layered;
        Freshness = baseFreshness;
        _segments = segments;
        _segmentFreshness = segmentFreshness;
        _journalReader = journalReader;
    }

    /// <summary>The trusted base generation this query accelerates against.</summary>
    public ContentIndexGeneration Generation { get; }

    /// <summary>The base freshness read (dirty set + verdict) captured at barrier B0.</summary>
    public FreshnessRead Freshness { get; }

    /// <summary>True when this query is layered over one or more incremental delta segments (plan §11.4).</summary>
    public bool IsLayered => _layered is not null;

    /// <summary>Alias ids currently provisionally pruned (awaiting B1 reconciliation).</summary>
    public IReadOnlyCollection<long> ProvisionalAliases => _layered?.ProvisionalAliases ?? _session!.ProvisionalAliases;

    /// <summary>Classifies a discovered normalized path (plan §3.5).</summary>
    public IndexPathClassification Classify(string normalizedPath)
        => _layered is not null ? _layered.Classify(normalizedPath) : _session!.Classify(normalizedPath);

    /// <summary>Classifies and routes a discovered path (fresh nonmembers → provisional prune).</summary>
    public PathDecision Route(string normalizedPath)
        => _layered is not null ? _layered.Route(normalizedPath) : _session!.Route(normalizedPath);

    /// <summary>Final B1 reconciliation (single-generation only): returns provisional aliases that must
    /// now be live-scanned.</summary>
    public IReadOnlyList<long> ReconcileAtB1(DirtyContentSet dirtyAtB1, bool reconciliationCertain = true)
        => _session!.ReconcileAtB1(dirtyAtB1, reconciliationCertain);

    /// <summary>Resolves query alias ids to canonical normalized paths without duplicating path storage in
    /// the search gate. Used only for uncommon B1 rescues or fail-safe fallback.</summary>
    internal IReadOnlyList<string> ResolveAliasPaths(IReadOnlyCollection<long> aliasIds)
        => _layered is not null
            ? _layered.ResolveAliasPaths(aliasIds)
            : _session!.ResolveAliasPaths(aliasIds);

    /// <summary>Releases per-query provisional alias state after B1 path resolution.</summary>
    internal void ClearProvisionalAliases()
    {
        if (_layered is not null)
            _layered.ClearProvisionalAliases();
        else
            _session!.ClearProvisionalAliases();
    }

    /// <summary>
    /// Completes the two-barrier protocol at the end of discovery (plan §3.5/§5.1 #3): replays the change
    /// journal over <c>[B0, B1)</c> and returns the provisional alias ids that must now be live-scanned
    /// because their content changed after B0 — so a match added after B0 can never be hidden. If the replay
    /// is not continuous, it throws (the gate then rescans <b>every</b> pruned path — the conservative
    /// fallback). For a layered query, every layer (base + each segment) is replayed from its own checkpoint.
    /// </summary>
    public IReadOnlyList<long> FinalizeAtB1(ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        ContentIndexFreshnessEvaluator.JournalReader? reader = journalReader ?? _journalReader;

        if (_layered is not null)
        {
            FreshnessRead baseB1 = ContentIndexFreshnessEvaluator.ReadDirtySince(Generation, Freshness.NextCheckpoint, reader);
            if (!baseB1.IsContinuous)
                throw new InvalidOperationException("Layered B1 base replay was not continuous.");

            var segmentDirties = new List<DirtyContentSet>(_segments!.Count);
            for (int i = 0; i < _segments.Count; i++)
            {
                FreshnessRead segB1 = ContentIndexFreshnessEvaluator.ReadDirtySince(
                    _segments[i].Added, _segmentFreshness![i].NextCheckpoint, reader);
                if (!segB1.IsContinuous)
                    throw new InvalidOperationException("Layered B1 segment replay was not continuous.");
                segmentDirties.Add(segB1.Dirty);
            }

            return _layered.ReconcileAtB1(baseB1.Dirty, segmentDirties);
        }

        FreshnessRead b1 = ContentIndexFreshnessEvaluator.ReadDirtySince(Generation, Freshness.NextCheckpoint, reader);
        return ReconcileAtB1(b1.Dirty, reconciliationCertain: b1.IsContinuous);
    }
}

/// <summary>Result of trying to accelerate a search: an <see cref="AcceleratedQuery"/> or a bypass reason.</summary>
public sealed record AcceleratorResult(AcceleratedQuery? Query, string BypassReason)
{
    /// <summary>True when the index can accelerate this search for this scope.</summary>
    public bool CanAccelerate => Query is not null;

    internal static AcceleratorResult Bypass(string reason) => new(null, reason);
}

/// <summary>
/// The single entry point that assembles the whole managed indexed-query path for a scope (plan §5):
/// resolve the effective pattern → plan a trigram query → honor the per-family acceleration gates →
/// open the current generation → evaluate freshness from the change journal → compose the trust decision
/// → begin a query session. It performs <b>no pruning itself</b> and does not stream results; it hands
/// the caller a classifier so the search pipeline can route each discovered path. Any failure or
/// uncertainty returns a bypass (the search then live-scans, unchanged). The journal reader is injectable
/// for testing; production reads the real USN journal.
/// </summary>
public sealed class ContentIndexAccelerator
{
    private readonly IContentIndexPathProvider _paths;
    private readonly int _retainedGenerations;
    private readonly ContentIndexFreshnessEvaluator.JournalReader? _journalReader;

    public ContentIndexAccelerator(
        IContentIndexPathProvider pathProvider,
        int retainedGenerations = 2,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        _paths = pathProvider ?? throw new ArgumentNullException(nameof(pathProvider));
        _retainedGenerations = Math.Max(1, retainedGenerations);
        _journalReader = journalReader;
    }

    /// <summary>
    /// Tries to begin an accelerated query for <paramref name="root"/> and <paramref name="options"/>,
    /// gated by <paramref name="settings"/>. Returns a bypass (never throws) when the master feature or
    /// per-search toggle is off, the query is ineligible or its family disabled, no trusted generation
    /// exists, or the root is not fresh — in every such case the caller live-scans exactly as today.
    /// <para>
    /// When <paramref name="candidateSource"/> is supplied and <see cref="AppSettings.IndexUseNativeWorker"/>
    /// is on, the candidate content-id set is produced by that source (the out-of-process
    /// <c>Yagu.IndexWorker</c>) instead of the in-process posting evaluation; on any failure it silently
    /// falls back to the in-process path (identical results, plan §3.3). This selection happens once here at
    /// barrier B0 — the per-discovered-path routing/classification hot loop is unaffected.
    /// </para>
    /// </summary>
    public AcceleratorResult TryBegin(
        string root,
        SearchOptions options,
        AppSettings settings,
        IIndexCandidateSource? candidateSource = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.EnableContentIndex || !options.UseContentIndex)
        {
            YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}': content index disabled for this search (EnableContentIndex={EnableContentIndex}, UseContentIndex={UseContentIndex}).", root, settings.EnableContentIndex, options.UseContentIndex);
            return AcceleratorResult.Bypass("content index disabled for this search");
        }

        // Resolve + plan the trigram query (plan §4). Ineligible → live-scan.
        var pattern = EffectiveSearchPattern.Resolve(options);
        var plan = TrigramQueryPlanner.Plan(pattern);
        if (plan is not TrigramPlan.Eligible eligible)
        {
            string planReason = plan is TrigramPlan.Ineligible ineligible ? ineligible.Reason : "not eligible";
            YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}': query ineligible for index acceleration ({PlanReason}) — pattern='{Pattern}', regex={IsRegex}, caseSensitive={CaseSensitive}.", root, planReason, pattern.Pattern, pattern.IsRegex, pattern.CaseSensitive);
            return AcceleratorResult.Bypass($"query is ineligible for index acceleration ({planReason})");
        }

        // Binary files are indexed from bounded PRINTABLE-ASCII runs. When this search includes binary
        // content, every required trigram must be representable by that namespace; otherwise a binary path
        // missing a non-printable trigram could be falsely pruned. Fail closed to the live scanner.
        if (!options.SkipBinary && !BinaryAsciiContentRepresentation.CanSafelyEvaluate(eligible.Query))
        {
            YaguLog.For("ContentIndex").LogDebug(
                "Accelerator bypass for '{Root}': binary search query has a non-printable required trigram.", root);
            return AcceleratorResult.Bypass("binary search query is not printable-ASCII indexable");
        }

        // Per-family acceleration gate (plan §6.1). Can only narrow — never forces an unsafe query on.
        if (!IsFamilyAccelerationEnabled(options, settings))
        {
            YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}': acceleration disabled for this query family (regex={UseRegex}, multiline={Multiline}, exact/wholeWord={ExactMatch}).", root, options.UseRegex, options.Multiline, options.ExactMatch);
            return AcceleratorResult.Bypass("acceleration disabled for this query family");
        }

        // Open the current layered index (base + any active delta segments) for this scope. This is a
        // QUERY-mode open: candidate evaluation uses only the postings + alias table, so drop each layer's
        // per-document trigram sets (retainDocuments: false) instead of retaining a second, full-size copy
        // of the index in memory until the next search.
        string scopeId = ContentIndexManager.ScopeIdForRoot(root);
        var store = new ContentIndexStore(_paths, scopeId, _retainedGenerations);
        ContentIndexStore.LayeredIndexHandle? handle = store.TryOpenLayered(retainDocuments: false);
        if (handle is null)
        {
            YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}' (scope {Scope}): no trusted index for this scope.", root, scopeId);
            return AcceleratorResult.Bypass("no trusted index for this scope");
        }

        ContentIndexGeneration generation = handle.Base;
        string generationDir = handle.BaseDir;

        // Evaluate the base freshness from the change journal at barrier B0.
        FreshnessRead freshness = ContentIndexFreshnessEvaluator.ReadDirtyAtBuildBarrier(generation, _journalReader);

        // Compose the single trust decision (structural + freshness + eligibility) for the base.
        GenerationDecision decision = ContentIndexQuerySession.CanAccelerate(generation, freshness.Verdict, queryEligible: true);
        if (decision is GenerationDecision.BypassRoot bypass)
        {
            // For a journal-discontinuity bypass, surface the specific USN read status (JournalIdChanged /
            // GapDetected / UnknownRecordVersion / Error) that the RootFreshnessVerdict collapses, so the
            // log says WHY continuity broke rather than just that it did.
            if (freshness.Verdict == RootFreshnessVerdict.JournalDiscontinuity)
                YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}' (scope {Scope}): {Reason} ({JournalStatus}).", root, scopeId, bypass.Reason, freshness.RawStatus);
            else
                YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}' (scope {Scope}): {Reason}.", root, scopeId, bypass.Reason);
            string detailedReason = freshness.RawStatus == UsnReadStatus.Ok
                ? bypass.Reason
                : $"{bypass.Reason} ({freshness.RawStatus})";
            return AcceleratorResult.Bypass(detailedReason);
        }

        // ── Layered (Phase 3) path: base + delta segments queried newest-first (plan §11.4). Used only when
        // every layer's B0 freshness is continuous; otherwise fall back to the safe base-only path below
        // (which still classifies changed/new files as dirty/unindexed → live-scanned, never wrongly pruned).
        if (handle.Segments.Count > 0
            && TryReadSegmentFreshness(handle.Segments, out List<FreshnessRead> segmentFreshness))
        {
            var segmentDirties = new List<DirtyContentSet>(segmentFreshness.Count);
            foreach (FreshnessRead sf in segmentFreshness)
                segmentDirties.Add(sf.Dirty);

            // Candidate producer for the layered path (identical result set either way): the in-process
            // memory-mapped format-v3 postings reader when enabled AND every layer (base + all segments) was
            // upgraded — all-or-nothing, since a single missing/faulted layer must not silently drop a layer's
            // candidates. Otherwise the in-process posting evaluation.
            LayeredContentIndexQuerySession layered;
            string layeredSourceLabel;
            if (settings.IndexUseV3QueryReader
                && TryEvaluateLayeredV3(handle, eligible.Query, out IReadOnlySet<int> baseV3, out IReadOnlyList<IReadOnlySet<int>> segmentV3))
            {
                layered = LayeredContentIndexQuerySession.BeginWithCandidates(
                    generation, handle.Segments, baseV3, segmentV3, freshness.Dirty, segmentDirties);
                layeredSourceLabel = "format-v3 reader";
            }
            else
            {
                layered = LayeredContentIndexQuerySession.Begin(
                    generation, handle.Segments, eligible.Query, freshness.Dirty, segmentDirties);
                layeredSourceLabel = "in-process";
            }

            long layeredTotal = generation.Manifest.ContentCount + handle.Segments.Sum(s => s.Added.Manifest.ContentCount);
            int layeredBudget = AppSettings.NormalizeIndexMaxCandidatePercent(settings.IndexMaxCandidatePercent);
            if (layeredTotal > 0 && (long)layered.CandidateCount * 100L > layeredTotal * layeredBudget)
            {
                YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}' (scope {Scope}): layered query not selective enough ({CandidateCount}/{TotalCount} candidates > {BudgetPercent}%).", root, scopeId, layered.CandidateCount, layeredTotal, layeredBudget);
                return AcceleratorResult.Bypass("query is not selective enough for the layered index");
            }

            YaguLog.For("ContentIndex").LogDebug("Accelerator accelerating '{Root}' (scope {Scope}) via LAYERED index ({CandidateSource}): base + {SegmentCount} segment(s), {CandidateCount} candidate(s).", root, scopeId, layeredSourceLabel, handle.Segments.Count, layered.CandidateCount);
            return new AcceleratorResult(
                new AcceleratedQuery(generation, layered, freshness, handle.Segments, segmentFreshness, _journalReader),
                string.Empty);
        }

        // ── Base-only path (no segments, or a segment's freshness was not continuous). ──
        // Candidate-set producer precedence at B0 (the result set is identical either way — only the source
        // of the candidate content-ids differs; the choice is made ONCE here, never per discovered path):
        //   (1) the in-process memory-mapped format-v3 postings reader when the generation was upgraded and
        //       the reader is enabled (plan §5.1 — resident memory tracks only touched pages; the managed
        //       reference the out-of-process worker mirrors),
        //   (2) the out-of-process worker when opted in and available (crash-isolated),
        //   (3) the in-process posting evaluation (always-correct fallback).
        // An un-upgraded generation (no v3 sidecars) or any read fault falls straight through to (2)/(3).
        ContentIndexQuerySession session;
        string candidateSourceLabel;
        if (settings.IndexUseV3QueryReader
            && V3ContentIndexCandidateSource.Instance.TryEvaluate(generationDir, eligible.Query, out IReadOnlySet<int> v3Candidates))
        {
            session = ContentIndexQuerySession.BeginWithCandidates(generation, v3Candidates, freshness.Dirty);
            candidateSourceLabel = "format-v3 reader";
        }
        else if (settings.IndexUseNativeWorker
            && candidateSource is not null
            && candidateSource.TryEvaluate(generationDir, eligible.Query, out IReadOnlySet<int> workerCandidates))
        {
            session = ContentIndexQuerySession.BeginWithCandidates(generation, workerCandidates, freshness.Dirty);
            candidateSourceLabel = "worker";
        }
        else
        {
            session = ContentIndexQuerySession.Begin(generation, eligible.Query, freshness.Dirty);
            candidateSourceLabel = "in-process";
        }

        // Selectivity guard (plan §6.1 IndexMaxCandidatePercent, default 25%): if the query's candidate set
        // is too large a fraction of the corpus, the index would prune too few files to be worth its
        // overhead — bypass to a live scan. Performance-only: bypassing never changes results, and it can
        // only ever choose live scan (never force pruning).
        long totalDocs = generation.Manifest.ContentCount;
        int budgetPercent = AppSettings.NormalizeIndexMaxCandidatePercent(settings.IndexMaxCandidatePercent);
        if (totalDocs > 0 && (long)session.CandidateCount * 100L > totalDocs * budgetPercent)
        {
                YaguLog.For("ContentIndex").LogDebug("Accelerator bypass for '{Root}' (scope {Scope}): query not selective enough ({CandidateCount}/{TotalCount} candidates > {BudgetPercent}%).", root, scopeId, session.CandidateCount, totalDocs, budgetPercent);
            return AcceleratorResult.Bypass("query is not selective enough for the index");
        }

        YaguLog.For("ContentIndex").LogDebug("Accelerator accelerating '{Root}' (scope {Scope}) via base index ({CandidateSource}): {CandidateCount}/{TotalCount} candidate(s).", root, scopeId, candidateSourceLabel, session.CandidateCount, totalDocs);
        return new AcceleratorResult(new AcceleratedQuery(generation, session, freshness, _journalReader), string.Empty);
    }

    /// <summary>
    /// Whether the acceleration gate for this search's query family is enabled (plan §6.1). Precedence:
    /// regex → multiline → whole-word/exact → plain literal (case-insensitive is already ineligible in
    /// the planner).
    /// </summary>
    internal static bool IsFamilyAccelerationEnabled(SearchOptions options, AppSettings settings)
    {
        if (options.UseRegex)
            return settings.IndexAccelerateRegex;
        if (options.Multiline)
            return settings.IndexAccelerateMultiline;
        if (options.ExactMatch)
            return settings.IndexAccelerateWholeWord;
        return settings.IndexAccelerateLiterals;
    }

    /// <summary>
    /// Tries to produce the per-layer candidate content-id sets for a layered index from the in-process
    /// memory-mapped format-v3 postings (plan §5.1). ALL-OR-NOTHING: returns true only when the base AND
    /// every segment has readable v3 sidecars; if any layer is un-upgraded or its v3 read faults, returns
    /// false so the caller falls back to the full in-process posting evaluation (never a partial mix that
    /// could drop a layer's candidates). <paramref name="segmentCandidates"/> is 1:1 with
    /// <see cref="ContentIndexStore.LayeredIndexHandle.Segments"/> (oldest → newest).
    /// </summary>
    private static bool TryEvaluateLayeredV3(
        ContentIndexStore.LayeredIndexHandle handle,
        TrigramExpression query,
        out IReadOnlySet<int> baseCandidates,
        out IReadOnlyList<IReadOnlySet<int>> segmentCandidates)
    {
        baseCandidates = System.Collections.Immutable.ImmutableHashSet<int>.Empty;
        segmentCandidates = Array.Empty<IReadOnlySet<int>>();

        V3ContentIndexCandidateSource source = V3ContentIndexCandidateSource.Instance;
        if (!source.TryEvaluate(handle.BaseDir, query, out IReadOnlySet<int> baseSet))
            return false;

        var segs = new IReadOnlySet<int>[handle.Segments.Count];
        for (int i = 0; i < handle.Segments.Count; i++)
        {
            if (!source.TryEvaluate(handle.SegmentDirs[i], query, out IReadOnlySet<int> segSet))
                return false;
            segs[i] = segSet;
        }

        baseCandidates = baseSet;
        segmentCandidates = segs;
        return true;
    }

    /// <summary>
    /// Reads each segment's barrier-B0 freshness. Returns true (with the 1:1 reads) only when EVERY segment's
    /// journal replay is continuous — the precondition for trusting the layered pruning. If any segment's read
    /// is non-continuous the caller falls back to the safe base-only path (segment content is then never
    /// pruned; those files live-scan).
    /// </summary>
    private bool TryReadSegmentFreshness(IReadOnlyList<ContentIndexDeltaSegment> segments, out List<FreshnessRead> freshness)
    {
        freshness = new List<FreshnessRead>(segments.Count);
        foreach (ContentIndexDeltaSegment segment in segments)
        {
            FreshnessRead read = ContentIndexFreshnessEvaluator.ReadDirtyAtBuildBarrier(segment.Added, _journalReader);
            if (!read.IsContinuous)
            {
                freshness = new List<FreshnessRead>();
                return false;
            }
            freshness.Add(read);
        }
        return true;
    }
}
