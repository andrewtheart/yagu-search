using Yagu.Models;
using Yagu.Services.Pdf;
using Yagu.Services.Ocr;

using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The query-time pruning brain for extended content sources (archive / PDF-text / OCR) — the analogue of
/// <see cref="ContentIndexSearchGate"/> for plan §7 Phase 4. It holds, per source kind, the loaded
/// <see cref="ExtendedSourceNamespace"/>, the query's member keys, the barrier-B0 freshness read, and the
/// extractor fingerprint that would run now, and answers one question per discovered source — <c>should
/// this source be extracted?</c>. It is <b>fail-safe</b>: only a routed <see cref="ExtendedSourceRoute.PruneSource"/>
/// returns <c>false</c>, and that is emitted only for a trusted, fresh, fingerprint-matched <em>deterministic</em>
/// nonmember (never OCR, never a changed/mismatched source, never on error). Pruned sources are recorded so
/// end-of-discovery B1 reconciliation can rescue any whose file changed after B0 — so a match can never be
/// silently hidden. The <c>SearchService</c> enqueue loop stays a thin caller of this unit-tested brain.
/// <para>
/// v1 covers file-backed sources (PDF/OCR), whose key is the normalized file path; archive-entry sources
/// (keyed by an <see cref="ArchiveEntryIdentity"/> digest) are supported by the underlying policy/namespace
/// but routed through a dedicated entry seam that lands with the archive build.
/// </para>
/// </summary>
public sealed class ExtendedSourceSearchGate
{
    /// <summary>Per-kind query context: the namespace, its member keys for this query, B0 freshness, and the current fingerprint.</summary>
    public sealed record NamespaceContext(
        ExtendedSourceNamespace Namespace,
        IReadOnlySet<string> MemberKeys,
        ExtendedSourceFreshness Freshness,
        ExtractorFingerprint CurrentFingerprint);

    private readonly IReadOnlyDictionary<SpecialSourceKind, NamespaceContext> _byKind;
    private readonly ExtendedSourceFreshnessEvaluator.JournalReader? _journalReader;
    private readonly Dictionary<string, (SpecialSourceKind Kind, string Key)> _prunedByPath = new(StringComparer.OrdinalIgnoreCase);

    public ExtendedSourceSearchGate(
        IReadOnlyDictionary<SpecialSourceKind, NamespaceContext> byKind,
        ExtendedSourceFreshnessEvaluator.JournalReader? journalReader = null)
    {
        _byKind = byKind ?? throw new ArgumentNullException(nameof(byKind));
        _journalReader = journalReader;
    }

    /// <summary>
    /// Builds a gate from the loaded namespaces for a query. Each entry pairs a namespace with the
    /// extractor fingerprint that would run <em>now</em> (a mismatch forces live extraction). Member keys
    /// and B0 freshness are computed once here; returns a gate that prunes nothing when <paramref name="namespaces"/>
    /// is empty.
    /// </summary>
    public static ExtendedSourceSearchGate Create(
        IReadOnlyDictionary<SpecialSourceKind, (ExtendedSourceNamespace Namespace, ExtractorFingerprint CurrentFingerprint)> namespaces,
        TrigramExpression query,
        ExtendedSourceFreshnessEvaluator.JournalReader? journalReader = null)
    {
        ArgumentNullException.ThrowIfNull(namespaces);
        ArgumentNullException.ThrowIfNull(query);

        var byKind = new Dictionary<SpecialSourceKind, NamespaceContext>();
        foreach ((SpecialSourceKind kind, (ExtendedSourceNamespace ns, ExtractorFingerprint fp)) in namespaces)
        {
            IReadOnlySet<string> members = ns.SelectMemberKeys(query);
            ExtendedSourceFreshness freshness = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(ns, journalReader);
            byKind[kind] = new NamespaceContext(ns, members, freshness, fp);
        }
        return new ExtendedSourceSearchGate(byKind, journalReader);
    }

    /// <summary>
    /// The single entry point the GUI + CLI use to build the extended-source gate for a search, mirroring
    /// <see cref="ContentIndexSearchGate.TryCreate"/>. Loads each enabled, persisted extended-source namespace
    /// for <paramref name="root"/>, resolves the planned query, and returns a gate — or <c>null</c> (extract
    /// everything) when the feature/kind is disabled, the master index is off, no namespace is persisted, the
    /// extractor fingerprint can't be computed, or the query is index-ineligible (for example, too short for trigrams).
    /// NEVER throws: any failure returns <c>null</c> so the search live-extracts.
    /// <para>
    /// A persisted PDF namespace existing at all means the plan-mandated determinism proof passed at build
    /// time; the current fingerprint is compared per source, so a since-upgraded extractor forces live
    /// extraction. OCR namespaces contain positive postings only: nonmembers always run OCR live.
    /// </para>
    /// </summary>
    public static ExtendedSourceSearchGate? TryCreate(
        IContentIndexPathProvider pathProvider,
        string root,
        SearchOptions options,
        AppSettings settings,
        PdfTextExtractor? pdfExtractor = null,
        ExtendedSourceFreshnessEvaluator.JournalReader? journalReader = null)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(root) || !settings.EnableContentIndex || !options.UseContentIndex)
            return null;

        try
        {
            var namespaces = new Dictionary<SpecialSourceKind, (ExtendedSourceNamespace Namespace, ExtractorFingerprint CurrentFingerprint)>();

            // PDF-text: only when this search actually extracts PDFs AND the PDF extended-source is enabled.
            if (options.SearchPdfText && settings.IndexBuildPdfTextExtendedSource)
            {
                PdfTextExtractor extractor;
                if (pdfExtractor is null)
                    extractor = new PdfTextExtractor();
                else
                    extractor = pdfExtractor;
                ExtractorFingerprint? fp = PdfExtractorFingerprint.TryCompute(extractor);
                if (fp is not null)
                {
                    string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                    ExtendedSourceNamespace? ns = new ExtendedSourceStore(pathProvider, scopeId).TryLoad(SpecialSourceKind.PdfText);
                    if (ns is not null)
                        namespaces[SpecialSourceKind.PdfText] = (ns, fp);
                }
            }

            if (options.SearchImageText && settings.IndexBuildImageTextExtendedSource)
            {
                ExtractorFingerprint? fp = ImageOcrExtractorFingerprint.TryCompute(
                    options.ImageOcrEngine,
                    options.ImageOcrModel,
                    options.ImageOcrMaxSide);
                if (fp is not null)
                {
                    string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                    ExtendedSourceNamespace? ns = new ExtendedSourceStore(pathProvider, scopeId)
                        .TryLoad(SpecialSourceKind.ImageOcr);
                    if (ns is not null)
                        namespaces[SpecialSourceKind.ImageOcr] = (ns, fp);
                }
            }

            if (namespaces.Count == 0)
                return null;

            // Resolve the planned query; an index-ineligible query can't prune.
            EffectiveSearchPattern pattern = EffectiveSearchPattern.Resolve(options);
            if (TrigramQueryPlanner.Plan(pattern) is not TrigramPlan.Eligible eligible)
                return null;

            return Create(namespaces, eligible.Query, journalReader);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Extended-source gate creation failed for '{Root}' → live extraction (no pruning).", root);
            return null;
        }
    }

    /// <summary>True once pruning has been disabled (an error); every source is extracted thereafter.</summary>
    public bool PruningDisabled { get; private set; }

    /// <summary>Cumulative number of sources this gate has pruned over its lifetime (a truthful acceleration signal).</summary>
    public int TotalPruned { get; private set; }

    /// <summary>Number of sources currently pruned and awaiting B1 reconciliation.</summary>
    public int PrunedCount => _prunedByPath.Count;

    /// <summary>
    /// Decides whether a discovered file-backed source (<paramref name="sourcePath"/>) of the given
    /// <paramref name="kind"/> must be extracted. Returns <c>true</c> (the default-safe answer) for members,
    /// changed/mismatched/unindexed sources, OCR nonmembers, any error, and once pruning is disabled;
    /// returns <c>false</c> only for a trusted, fresh, fingerprint-matched <em>deterministic</em> nonmember,
    /// which is recorded for B1 reconciliation.
    /// </summary>
    public bool ShouldExtract(SpecialSourceKind kind, string sourcePath)
        => ShouldExtract(kind, sourcePath, out _);

    /// <summary>Like <see cref="ShouldExtract(SpecialSourceKind,string)"/>, and also reports whether a
    /// trusted fresh posting selected this source for priority extraction. OCR nonmembers still return
    /// true with <paramref name="prioritized"/> false; they are never pruned.</summary>
    public bool ShouldExtract(SpecialSourceKind kind, string sourcePath, out bool prioritized)
    {
        prioritized = false;
        if (PruningDisabled)
            return true;
        if (!_byKind.TryGetValue(kind, out NamespaceContext? ctx))
            return true; // no namespace for this source kind → extract live

        try
        {
            string key = IndexScopeIdentity.NormalizePath(sourcePath);

            // CRITICAL SAFETY GUARD: only a source the namespace actually saw at build time may be pruned.
            // An unknown source (excluded from the build, added afterwards, or whose extraction failed) has
            // no evidence its text cannot match — pruning it would silently hide a match. Always extract it.
            if (!ctx.Namespace.IsKnownSource(key))
                return true;

            ExtendedSourceCandidate candidate = ctx.Namespace.ClassifyCandidate(
                key, ctx.MemberKeys, ctx.CurrentFingerprint, ctx.Freshness.IsFresh(key));

            ExtendedSourceRoute route = ExtendedSourcePolicy.Route(candidate);
            if (route is ExtendedSourceRoute.Extract extract)
            {
                prioritized = extract.Prioritized;
                return true;
            }

            _ = (ExtendedSourceRoute.PruneSource)route;
            _prunedByPath[sourcePath] = (kind, key);
            TotalPruned++;
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Any classification error → stop pruning and extract everything from here (fail safe).
            PruningDisabled = true;
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Extended-source pruning disabled mid-search after {TotalPruned} pruned source(s); extracting everything (fail-safe).", TotalPruned);
            return true;
        }
    }

    /// <summary>
    /// Barrier B1: re-reads each namespace's change journal from its B0 cursor and returns the pruned source
    /// paths whose file changed after B0 (or every pruned path, on any journal discontinuity or error), so
    /// the caller re-extracts them before completing. Mirrors <see cref="ContentIndexSearchGate.GetPathsToRescan"/>.
    /// Drains the pruned set.
    /// </summary>
    public IReadOnlyList<string> GetSourcesToRescan()
    {
        if (PruningDisabled)
            return DrainPrunedPaths();

        var rescue = new List<string>();
        try
        {
            foreach ((string path, (SpecialSourceKind kind, string key)) in _prunedByPath)
            {
                NamespaceContext ctx = _byKind[kind];
                ExtendedSourceFreshness b1 = ExtendedSourceFreshnessEvaluator.ReadDirtySince(
                    ctx.Namespace, ctx.Freshness.NextCheckpoint, _journalReader);
                if (!b1.IsContinuous || !b1.IsFresh(key))
                    rescue.Add(path);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogWarning(ex,
                "Extended-source B1 reconciliation failed → re-extracting every pruned source (fail-safe).");
            return DrainPrunedPaths();
        }

        _prunedByPath.Clear();
        return rescue;
    }

    private List<string> DrainPrunedPaths()
    {
        var all = _prunedByPath.Keys.ToList();
        _prunedByPath.Clear();
        return all;
    }
}
