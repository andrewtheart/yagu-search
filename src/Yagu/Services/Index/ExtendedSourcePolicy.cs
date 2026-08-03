namespace Yagu.Services.Index;

/// <summary>
/// The facts needed to route one discovered extended-source candidate (archive / PDF-text / OCR)
/// through <see cref="ExtendedSourcePolicy.Route"/> (plan §7 Phase 4). Every field is something the
/// build/query already knows; the routing itself is a pure function so it can never disagree between
/// build, query, status, and tests.
/// </summary>
/// <param name="Kind">The extended content source this candidate belongs to.</param>
/// <param name="NamespaceTrusted">
/// The source posting namespace exists and its format/representation version is compatible.
/// </param>
/// <param name="FingerprintMatches">
/// The stored <see cref="ExtractorFingerprint"/> equals the current extractor configuration.
/// </param>
/// <param name="SourceFresh">
/// The source file (and, for archives, the entry identity) passes the same local-NTFS + continuous-USN
/// freshness gate as raw text. A <c>(len, mtime)</c>-only proof is explicitly insufficient (§3.5).
/// </param>
/// <param name="IsPostingMember">
/// The query's required trigrams selected this source in the namespace's postings.
/// </param>
/// <param name="HasPersistedDeterministicNegative">
/// A stored <see cref="ExtractionOutcome.DeterministicUnsupported"/> proof exists for this source under
/// the matching fingerprint (only meaningful for a deterministic extractor).
/// </param>
public readonly record struct ExtendedSourceCandidate(
    SpecialSourceKind Kind,
    bool NamespaceTrusted,
    bool FingerprintMatches,
    bool SourceFresh,
    bool IsPostingMember,
    bool HasPersistedDeterministicNegative);

/// <summary>
/// The routing verdict for one extended-source candidate (plan §7 Phase 4). Mirrors
/// <c>IndexTrustDecision.DecidePath</c>: a closed record so the caller must handle both cases, and a
/// <see cref="PruneSource"/> is only ever emitted for a trusted, fresh, fingerprint-matched,
/// <em>deterministic</em> namespace.
/// </summary>
public abstract record ExtendedSourceRoute
{
    private ExtendedSourceRoute() { }

    /// <summary>
    /// Run the extractor live. <see cref="Prioritized"/> marks a posting-selected positive candidate
    /// whose extraction should be scheduled ahead of unranked sources.
    /// </summary>
    public sealed record Extract(string Reason, bool Prioritized) : ExtendedSourceRoute;

    /// <summary>
    /// Skip running the extractor entirely — the source is a trusted, fresh, fingerprint-matched
    /// nonmember (or proven deterministically unsupported) of a <em>deterministic</em> namespace.
    /// Never emitted for a non-deterministic (OCR) source.
    /// </summary>
    public sealed record PruneSource(string Reason) : ExtendedSourceRoute;
}

/// <summary>
/// The single safety brain for extended-source (archive / PDF-text / OCR) index acceleration
/// (plan §7 Phase 4). Every decision is <b>fail-safe</b>: anything short of a trusted, fresh,
/// fingerprint-matched, <em>deterministic</em> namespace runs the extractor live, so an extended-source
/// bug can never hide a match. The namespaces are independent — raw-text postings never prune these
/// sources and these namespaces never prune raw files — so that separation is structural, not enforced
/// here. OCR is treated as non-deterministic: its positive postings may only prioritize candidates, and
/// an OCR nonmember must always be re-extracted (negative OCR pruning is a deferred product decision).
/// </summary>
public static class ExtendedSourcePolicy
{
    /// <summary>
    /// Whether the extractor for <paramref name="kind"/> is a deterministic byte-to-text function.
    /// PDF-text (<c>pdftotext</c>) and archive entry extraction are deterministic; image OCR is
    /// <b>not</b> (output varies across hardware providers, runtime versions, kernels, and resource
    /// conditions), so only deterministic extractors may negatively prune.
    /// </summary>
    public static bool IsDeterministicExtractor(SpecialSourceKind kind) => kind switch
    {
        SpecialSourceKind.PdfText => true,
        SpecialSourceKind.Archive => true,
        SpecialSourceKind.ImageOcr => false,
        _ => false,
    };

    /// <summary>
    /// Whether a <em>nonmember</em> of this namespace may be pruned (skipped) — only for deterministic
    /// extractors. An OCR nonmember must always re-run OCR and can only be prioritized, never pruned.
    /// </summary>
    public static bool CanPruneNonmembers(SpecialSourceKind kind) => IsDeterministicExtractor(kind);

    /// <summary>
    /// Whether a <see cref="ExtractionOutcome.Success"/> outcome's trigrams may be persisted as positive
    /// postings for candidate selection/prioritization. Positive postings are allowed for every source
    /// kind (including OCR, which prioritizes but never prunes). Non-<c>Success</c> outcomes never
    /// contribute positive postings.
    /// </summary>
    public static bool IsPersistablePositive(ExtractionOutcome outcome) =>
        outcome is ExtractionOutcome.Success;

    /// <summary>
    /// Whether an outcome may persist a <em>negative</em> exclusion proof (a durable "this source has no
    /// extractable text" record used to skip it next time). <b>Only</b> a
    /// <see cref="ExtractionOutcome.DeterministicUnsupported"/> from a deterministic extractor qualifies;
    /// empty OCR output, transient failures, cancellations, and exceptions never do.
    /// </summary>
    public static bool IsPersistableNegative(ExtractionOutcome outcome, SpecialSourceKind kind) =>
        IsDeterministicExtractor(kind) && outcome is ExtractionOutcome.DeterministicUnsupported;

    /// <summary>
    /// The fail-safe routing decision for one discovered extended-source candidate. Only a trusted,
    /// fresh, fingerprint-matched, <em>deterministic</em> namespace ever prunes; every other path
    /// extracts live. A posting member always re-runs its extractor (for current line/entry context)
    /// but is prioritized ahead of unranked sources.
    /// </summary>
    public static ExtendedSourceRoute Route(in ExtendedSourceCandidate candidate)
    {
        if (!candidate.NamespaceTrusted)
            return new ExtendedSourceRoute.Extract("namespace untrusted or absent", Prioritized: false);
        if (!candidate.FingerprintMatches)
            return new ExtendedSourceRoute.Extract("extractor fingerprint changed", Prioritized: false);
        if (!candidate.SourceFresh)
            return new ExtendedSourceRoute.Extract("source changed since the namespace was built", Prioritized: false);

        // Trusted + fresh + fingerprint-matched from here on.

        // A posting-selected positive candidate always re-runs the extractor for live line/entry
        // context, but is prioritized ahead of unranked sources. (This also correctly handles the
        // contradictory input of a "member" that also carries a negative proof: members always extract.)
        if (candidate.IsPostingMember)
            return new ExtendedSourceRoute.Extract("posting member re-verified live", Prioritized: true);

        // A nonmember of a deterministic namespace cannot contain a match → prune it.
        if (IsDeterministicExtractor(candidate.Kind))
        {
            string reason = candidate.HasPersistedDeterministicNegative
                ? "deterministically unsupported (proven)"
                : "deterministic nonmember cannot contain a match";
            return new ExtendedSourceRoute.PruneSource(reason);
        }

        // A non-deterministic (OCR) nonmember must still be extracted — never pruned.
        return new ExtendedSourceRoute.Extract("non-deterministic source cannot be pruned", Prioritized: false);
    }
}
