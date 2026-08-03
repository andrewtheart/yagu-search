using Microsoft.Extensions.Logging;
using Yagu.Models;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The pruning "brain" that a live search consults per discovered path (plan §5). It wraps an
/// <see cref="AcceleratedQuery"/> and answers one question — <c>should this path be content-scanned?</c>
/// — recording the paths it prunes so the end-of-discovery B1 reconciliation can rescue any that changed
/// after B0. It is deliberately a small, pure, unit-tested unit so the (untestable) <c>SearchService</c>
/// streaming loop stays a thin caller. <b>Correctness invariants:</b> it only ever prunes a
/// <see cref="IndexPathClassification.FreshIndexedNonmember"/> (which a required-superset trigram query
/// provably cannot match and USN proves unchanged); any error or uncertainty disables pruning and returns
/// every pruned path for a live scan — so a match can never be silently hidden. Pruned paths are tracked
/// only as alias ids in the query session and reverse-resolved from the generation at B1, avoiding the old
/// duplicate path dictionary and its 200,000-path fallback on large scopes.
/// </summary>
public sealed class ContentIndexSearchGate
{
    private readonly AcceleratedQuery _query;
    private readonly Action<bool, string>? _onStatusChanged;
    private bool _fallbackStatusReported;
    private int _cumulativeRescued;

    public ContentIndexSearchGate(AcceleratedQuery query, Action<bool, string>? onStatusChanged = null)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _onStatusChanged = onStatusChanged;
    }

    /// <summary>
    /// Builds a gate for a search over <paramref name="root"/>, or null when the index can't accelerate it
    /// (feature off, ineligible query, no trusted generation, root not fresh, …). This is the single entry
    /// point the GUI and CLI call to opt a search into index pruning; it never throws — a null result means
    /// the search live-scans exactly as today. Intended to run off the UI thread (it opens the generation
    /// and reads the journal at barrier B0).
    /// </summary>
    public static ContentIndexSearchGate? TryCreate(
        IContentIndexPathProvider pathProvider,
        string root,
        SearchOptions options,
        AppSettings settings,
        int retainedGenerations = 2,
        ContentIndexFreshnessEvaluator.JournalReader? journalReader = null,
        IIndexCandidateSource? candidateSource = null,
        Action<bool, string>? onAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(root))
        {
            NotifyAttempt(onAttempt, accelerated: false, "search root is blank");
            return null;
        }

        try
        {
            // Enforce the configurable USN catch-up cap (AppSettings.IndexMaxJournalCatchupRecords): when no
            // reader is injected (production), read the real journal with that cap so a scope whose change
            // delta since build exceeds it fails closed (UsnReadStatus.Incomplete → live-scan) instead of
            // trusting a partial delta and unsafely pruning.
            ContentIndexFreshnessEvaluator.JournalReader effectiveReader = journalReader
                ?? ContentIndexFreshnessEvaluator.CreateReader(
                    AppSettings.NormalizeIndexMaxJournalCatchupRecords(settings.IndexMaxJournalCatchupRecords));
            var accelerator = new ContentIndexAccelerator(pathProvider, retainedGenerations, effectiveReader);
            AcceleratorResult result = accelerator.TryBegin(root, options, settings, candidateSource);
            NotifyAttempt(
                onAttempt,
                result.CanAccelerate,
                result.CanAccelerate ? "index acceleration active" : result.BypassReason);
            return result.CanAccelerate ? new ContentIndexSearchGate(result.Query!, onAttempt) : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any failure creating the gate → live-scan (no acceleration), never disrupt the search.
            YaguLog.For("ContentIndex").LogWarning(ex, "Search gate creation failed for '{Root}' → live-scanning (no acceleration).", root);
            NotifyAttempt(onAttempt, accelerated: false, "index initialization failed; live-scanning");
            return null;
        }
    }

    private static void NotifyAttempt(Action<bool, string>? callback, bool accelerated, string reason)
    {
        if (callback is null)
            return;
        try { callback(accelerated, reason); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Diagnostic/UI notification must never change search routing.
            YaguLog.For("ContentIndex").LogWarning(ex, "Index-attempt status callback failed.");
        }
    }

    /// <summary>
    /// True when an accelerating index exists for <paramref name="root"/> AND its current on-disk size is at
    /// or under <paramref name="maxInProcessSizeMB"/> — i.e. it is small enough to load into memory without
    /// the multi-GB footprint that degrades search speed. False (→ live-scan) when there is no index, when
    /// <paramref name="maxInProcessSizeMB"/> is 0 (never load in-process), or on any error. Cheap; never throws.
    /// </summary>
    public static bool IsScopeWithinInProcessSizeLimit(IContentIndexPathProvider pathProvider, string root, int retainedGenerations, int maxInProcessSizeMB)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        if (maxInProcessSizeMB <= 0)
            return false; // 0 = never load an index in-process (always live-scan)
        try
        {
            if (string.IsNullOrWhiteSpace(root))
                return false;
            string scopeId = ContentIndexManager.ScopeIdForRoot(root);
            long sizeBytes = new ContentIndexStore(pathProvider, scopeId, Math.Max(1, retainedGenerations)).GetCurrentLayeredIndexSizeBytes();
            if (sizeBytes <= 0)
                return false; // no trusted index → nothing to load (live-scan)
            return sizeBytes <= (long)maxInProcessSizeMB * 1024L * 1024L;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No functional impact (the caller live-scans), but never swallow the cause silently.
            YaguLog.For("ContentIndex").LogWarning(ex, "Index in-process size-limit check failed for {Root} → treating as not loadable (live-scan).", root);
            return false;
        }
    }

    /// <summary>True when an accelerating v3 index exists and the active files the out-of-process worker
    /// memory-maps are within <paramref name="maxMappedSizeMB"/>. Legacy build payloads are deliberately not
    /// charged: they are never mapped or read by the query worker. 0 disables worker queries.</summary>
    public static bool IsScopeWithinWorkerMappedSizeLimit(
        IContentIndexPathProvider pathProvider, string root, int retainedGenerations, int maxMappedSizeMB)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        if (maxMappedSizeMB <= 0)
            return false;
        try
        {
            if (string.IsNullOrWhiteSpace(root))
                return false;
            string scopeId = ContentIndexManager.ScopeIdForRoot(root);
            long sizeBytes = new ContentIndexStore(pathProvider, scopeId, Math.Max(1, retainedGenerations))
                .GetCurrentLayeredMappedQuerySizeBytes();
            return sizeBytes > 0 && sizeBytes <= (long)maxMappedSizeMB * 1024L * 1024L;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Index worker mapped-size check failed for {Root} → live-scan.", root);
            return false;
        }
    }

    /// <summary>
    /// True when the current layered index for <paramref name="root"/> is already deserialized in the
    /// process-wide query-mode cache — i.e. building a gate for it would be a fast cache hit, not a cold
    /// multi-second, multi-GB open. Cheap (reads only the pointer slot) and never throws. A false result
    /// tells the caller to live-scan this search (and warm the index for the next one).
    /// </summary>
    public static bool IsScopeWarm(IContentIndexPathProvider pathProvider, string root, int retainedGenerations)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        try
        {
            if (string.IsNullOrWhiteSpace(root))
                return false;
            string scopeId = ContentIndexManager.ScopeIdForRoot(root);
            return new ContentIndexStore(pathProvider, scopeId, Math.Max(1, retainedGenerations)).IsCurrentLayeredIndexCached();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No functional impact (the caller live-scans and warms), but never swallow the cause silently.
            YaguLog.For("ContentIndex").LogWarning(ex, "Index warmth check failed for {Root} → treating as cold (live-scan + background warm).", root);
            return false;
        }
    }

    /// <summary>The accelerated query this gate prunes against.</summary>
    public AcceleratedQuery Query => _query;

    /// <summary>True once pruning has been disabled by an error or uncertain reconciliation.</summary>
    public bool PruningDisabled { get; private set; }

    /// <summary>Number of aliases currently pruned (awaiting B1 reconciliation).</summary>
    public int PrunedCount => _query.ProvisionalAliases.Count;

    /// <summary>
    /// Cumulative number of paths this gate has pruned over its lifetime — unlike <see cref="PrunedCount"/>
    /// it is never reset by <see cref="GetPathsToRescan"/> draining, so it remains a truthful "how much did
    /// the index accelerate this search?" signal after discovery completes (used by the verbose search log).
    /// </summary>
    public int TotalPruned { get; private set; }

    /// <summary>
    /// Decides whether <paramref name="path"/> must be content-scanned. Returns <c>true</c> to scan (the
    /// default-safe answer) for members, dirty/unindexed/special/untrusted paths, any classification
    /// error, or once pruning is disabled. Returns <c>false</c> only for a fresh posting nonmember, whose
    /// compact alias id is already recorded by the query session for B1 reconciliation. The gate deliberately
    /// stores no duplicate path dictionary and imposes no arbitrary path-count fallback.
    /// </summary>
    public bool ShouldContentScan(string path, string normalizedPath)
    {
        if (PruningDisabled)
            return true;

        PathDecision decision;
        try
        {
            decision = _query.Route(normalizedPath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any classification error → stop pruning and scan everything (fail safe).
            PruningDisabled = true;
            YaguLog.For("ContentIndex").LogWarning(ex, "Index pruning disabled mid-search: classification threw after {TotalPruned} pruned path(s); scanning everything from here (fail-safe).", TotalPruned);
            ReportFallback("index pruning failed during path classification; scanning live");
            return true;
        }

        if (decision is PathDecision.ProvisionalPrune)
        {
            TotalPruned++;
            return false;
        }

        return true; // LiveScanPath (member/dirty/unindexed/special/untrusted)
    }

    /// <summary>
    /// The paths that must be (re)scanned at end of discovery (barrier B1, plan §3.5/§5.1 #3). When
    /// pruning was disabled by an error, <b>every</b> pruned path is returned — nothing is trusted partially.
    /// Otherwise the journal is replayed over <c>[B0, B1)</c> and only the pruned paths whose
    /// content changed (or all of them, if reconciliation is uncertain) are returned. Idempotent enough
    /// for a single end-of-discovery call.
    /// </summary>
    public IReadOnlyCollection<string> GetPathsToRescan(ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        if (PruningDisabled)
            return ResolveAndClear(_query.ProvisionalAliases);

        IReadOnlyList<long> rescuedAliases;
        try
        {
            rescuedAliases = _query.FinalizeAtB1(journalReader);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Uncertain reconciliation → rescan everything we pruned (fail safe).
            PruningDisabled = true;
            YaguLog.For("ContentIndex").LogWarning(ex, "B1 reconciliation was uncertain; rescanning all {PrunedCount} pruned path(s) (fail-safe).", PrunedCount);
            ReportFallback("index reconciliation was uncertain; scanning live");
            return ResolveAndClear(_query.ProvisionalAliases);
        }

        IReadOnlyList<string> result = ResolveAndClear(rescuedAliases);
        if (TotalPruned > 0 && rescuedAliases.Count >= TotalPruned)
        {
            PruningDisabled = true;
            ReportFallback("every index-pruned path required a live rescan");
        }
        return result;
    }

    /// <summary>
    /// One <b>after-scan-drain</b> B1 reconciliation pass (plan §5.4, option (b)). Unlike
    /// <see cref="GetPathsToRescan"/> — a single end-of-discovery call that clears the whole provisional set —
    /// this replays the change journal over <c>[B0, now)</c> against only the <b>still-pruned</b> aliases and
    /// returns just those whose content changed, leaving the remaining (proven-clean) provisional entries in
    /// place so a <i>bounded rescue-and-re-drain loop</i> can call it again after scanning the rescued paths —
    /// catching any pruned file edited <i>during</i> that rescue scan, right up to the end of the whole search.
    /// <see cref="B1RescuePass.MorePassesUseful"/> is false once a pass finds nothing new, pruning is disabled,
    /// or everything pruned has been rescued, so the caller stops. Fail-safe: any error or uncertainty disables
    /// pruning and returns every remaining pruned path for a live scan, so a match can never be silently hidden.
    /// </summary>
    public B1RescuePass ReconcileB1Pass(ContentIndexFreshnessEvaluator.JournalReader? journalReader = null)
    {
        if (PruningDisabled)
            return new B1RescuePass(DrainAllProvisional(), MorePassesUseful: false);

        IReadOnlyList<long> rescuedAliases;
        try
        {
            rescuedAliases = _query.FinalizeAtB1(journalReader);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Uncertain reconciliation → rescan every remaining pruned path (fail safe).
            PruningDisabled = true;
            YaguLog.For("ContentIndex").LogWarning(ex, "B1 reconciliation was uncertain; rescanning all {PrunedCount} remaining pruned path(s) (fail-safe).", PrunedCount);
            ReportFallback("index reconciliation was uncertain; scanning live");
            return new B1RescuePass(DrainAllProvisional(), MorePassesUseful: false);
        }

        _cumulativeRescued += rescuedAliases.Count;

        if (rescuedAliases.Count == 0)
        {
            // Nothing pruned changed over [B0, now): the remaining provisional paths are proven clean, so they
            // are released without a rescan and no further pass is useful.
            _query.ClearProvisionalAliases();
            return new B1RescuePass(Array.Empty<string>(), MorePassesUseful: false);
        }

        IReadOnlyList<string> paths = _query.ResolveAliasPaths(rescuedAliases);

        if (TotalPruned > 0 && _cumulativeRescued >= TotalPruned)
        {
            // Every path the index pruned needed a live rescan — the index did not help this search.
            PruningDisabled = true;
            ReportFallback("every index-pruned path required a live rescan");
            _query.ClearProvisionalAliases();
            return new B1RescuePass(paths, MorePassesUseful: false);
        }

        return new B1RescuePass(paths, MorePassesUseful: true);
    }

    private IReadOnlyCollection<string> DrainAllProvisional()
    {
        IReadOnlyList<string> paths = _query.ResolveAliasPaths(_query.ProvisionalAliases);
        _query.ClearProvisionalAliases();
        return paths;
    }

    private IReadOnlyList<string> ResolveAndClear(IReadOnlyCollection<long> aliasIds)
    {
        IReadOnlyList<string> paths = _query.ResolveAliasPaths(aliasIds);
        _query.ClearProvisionalAliases();
        return paths;
    }

    private void ReportFallback(string reason)
    {
        if (_fallbackStatusReported)
            return;
        _fallbackStatusReported = true;
        NotifyAttempt(_onStatusChanged, accelerated: false, reason);
    }

    /// <summary>
    /// Read-only candidacy provenance for a path that a search actually produced a result for (plan §6.2):
    /// a fresh indexed member → <see cref="IndexProvenanceKind.IndexAccelerated"/>; anything else (dirty,
    /// unindexed, rescued nonmember, uncovered) → <see cref="IndexProvenanceKind.LiveScanned"/>. It reads
    /// only the immutable generation/candidate/dirty snapshot (never the mutable prune set), so it is safe
    /// to call from the UI thread concurrently with the discovery loop's <see cref="ShouldContentScan"/>.
    /// </summary>
    public IndexProvenanceKind ClassifyProvenance(string normalizedPath)
        => ContentIndexUiStatus.ProvenanceFor(_query.Classify(normalizedPath));
}

/// <summary>
/// The outcome of one <see cref="ContentIndexSearchGate.ReconcileB1Pass"/> after-scan-drain B1 pass: the
/// pruned paths that must now be live-scanned, and whether another pass could still surface a newly-dirty
/// pruned path (false → the caller stops the bounded rescue-and-re-drain loop).
/// </summary>
public readonly record struct B1RescuePass(IReadOnlyCollection<string> PathsToScan, bool MorePassesUseful);
