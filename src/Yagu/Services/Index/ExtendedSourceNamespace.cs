using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// An immutable, in-memory posting namespace for one extended content source (archive / PDF-text / OCR)
/// (plan §7 Phase 4). It is the extended-source analogue of <see cref="ContentIndexGeneration"/> but far
/// simpler: each admitted source is one "document" of ephemeral extracted text reduced to trigrams, and
/// the namespace persists <b>only</b> postings, per-source keys, the extractor
/// <see cref="ExtractorFingerprint"/>, and durable negative proofs — <b>never</b> the extracted text
/// (§6.4). It never prunes ordinary raw files and ordinary raw-file postings never prune it; that
/// separation is structural (one namespace object per source kind).
/// </summary>
public sealed class ExtendedSourceNamespace
{
    /// <summary>Which extended content source this namespace covers.</summary>
    public SpecialSourceKind Kind { get; }

    /// <summary>The exact extractor configuration that produced these postings.</summary>
    public ExtractorFingerprint Fingerprint { get; }

    /// <summary>The trigram postings over admitted sources; document id = source ordinal.</summary>
    public TrigramPostingIndex Postings { get; }

    /// <summary>Source keys indexed by document id (a normalized path, or an archive entry digest).</summary>
    public IReadOnlyList<string> SourceKeys { get; }

    /// <summary>
    /// Source keys proven <see cref="ExtractionOutcome.DeterministicUnsupported"/> by a deterministic
    /// extractor — a durable negative exclusion. Empty for non-deterministic (OCR) namespaces.
    /// </summary>
    public IReadOnlySet<string> NegativeProofKeys { get; }

    /// <summary>
    /// Per-source distinct trigram sets (document id = source ordinal, parallel to <see cref="SourceKeys"/>).
    /// Retained so the namespace can be serialized and its postings rebuilt byte-identically on load; never
    /// the source text itself.
    /// </summary>
    internal IReadOnlyList<IReadOnlyCollection<Trigram>> Documents { get; }

    /// <summary>The normalized scope root the sources live under (used to read the volume's change journal).</summary>
    public string NormalizedRootPath { get; }

    /// <summary>
    /// The USN checkpoint captured when the namespace was built (plan §3.5). <see cref="UsnCheckpoint.None"/>
    /// means the journal was unavailable at build time, so no source can be proven fresh.
    /// </summary>
    public UsnCheckpoint FreshnessCheckpoint { get; }

    /// <summary>
    /// Build-time file identity for every source key — both admitted members and negative proofs. A null
    /// value means the identity could not be captured, so that source can never be proven fresh and always
    /// live-extracts (plan §3.5: a <c>(len, mtime)</c> proof is explicitly insufficient).
    /// </summary>
    public IReadOnlyDictionary<string, UsnFileIdentity?> SourceIdentityByKey { get; }

    internal ExtendedSourceNamespace(
        SpecialSourceKind kind,
        ExtractorFingerprint fingerprint,
        IReadOnlyList<IReadOnlyCollection<Trigram>> documents,
        IReadOnlyList<string> sourceKeys,
        IReadOnlySet<string> negativeProofKeys,
        IReadOnlyDictionary<string, UsnFileIdentity?> sourceIdentityByKey,
        string normalizedRootPath,
        UsnCheckpoint freshnessCheckpoint)
    {
        Kind = kind;
        Fingerprint = fingerprint;
        Documents = documents;
        Postings = TrigramPostingIndex.Build(documents);
        SourceKeys = sourceKeys;
        NegativeProofKeys = negativeProofKeys;
        SourceIdentityByKey = sourceIdentityByKey;
        NormalizedRootPath = normalizedRootPath;
        FreshnessCheckpoint = freshnessCheckpoint;
    }

    /// <summary>Number of admitted sources (posting documents).</summary>
    public int SourceCount => SourceKeys.Count;

    /// <summary>Every source key (admitted members and negative proofs) — the universe for fail-closed dirtying.</summary>
    public IReadOnlySet<string> AllSourceKeys => SourceIdentityByKey.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// True only when <paramref name="sourceKey"/> was seen and recorded when this namespace was built
    /// (an admitted posting document or a durable negative proof). A source the namespace has NEVER seen —
    /// because it was excluded from the build, added afterwards, or failed extraction — MUST NOT be pruned
    /// from it: the caller has no evidence its text cannot match, so it must live-extract. This is the guard
    /// that keeps a nonmember classification from silently hiding an un-indexed source.
    /// </summary>
    public bool IsKnownSource(string sourceKey)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        return SourceIdentityByKey.ContainsKey(sourceKey);
    }

    /// <summary>
    /// Resolves the set of source keys whose backing file changed over a journal interval. A source is dirty
    /// when its captured file identity appears in <paramref name="changes"/>, or when no identity was
    /// captured at build time (freshness cannot be proven → conservative). Feeds the per-source
    /// <c>sourceFresh</c> input for <see cref="ClassifyCandidate"/>.
    /// </summary>
    public IReadOnlySet<string> ResolveDirtyKeys(IEnumerable<UsnChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var changed = new HashSet<UsnFileIdentity>();
        foreach (UsnChange c in changes)
            changed.Add(c.Identity);

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, UsnFileIdentity? identity) in SourceIdentityByKey)
        {
            if (identity is not { } id || changed.Contains(id))
                dirty.Add(key);
        }
        return dirty;
    }

    /// <summary>
    /// Evaluates a planned query once against the postings and returns the set of member source keys —
    /// the sources whose extracted text can contain a match (a candidate superset, verified live).
    /// </summary>
    public IReadOnlySet<string> SelectMemberKeys(TrigramExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (int docId in Postings.EvaluateSet(query))
        {
            if ((uint)docId < (uint)SourceKeys.Count)
                members.Add(SourceKeys[docId]);
        }
        return members;
    }

    /// <summary>
    /// Builds the <see cref="ExtendedSourceCandidate"/> for one discovered source so the caller can route
    /// it through <see cref="ExtendedSourcePolicy.Route"/>. <paramref name="memberKeys"/> is the once-per-query
    /// result of <see cref="SelectMemberKeys"/>; <paramref name="currentFingerprint"/> is the extractor
    /// configuration that would run <em>now</em> (a mismatch forces live extraction); <paramref name="sourceFresh"/>
    /// is the source's local-NTFS + continuous-USN freshness (a <c>(len, mtime)</c> proof is insufficient).
    /// The in-memory namespace is structurally trusted, so <see cref="ExtendedSourceCandidate.NamespaceTrusted"/>
    /// is always true here.
    /// </summary>
    public ExtendedSourceCandidate ClassifyCandidate(
        string sourceKey,
        IReadOnlySet<string> memberKeys,
        ExtractorFingerprint currentFingerprint,
        bool sourceFresh)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(memberKeys);
        return new ExtendedSourceCandidate(
            Kind,
            NamespaceTrusted: true,
            FingerprintMatches: Fingerprint.Matches(currentFingerprint),
            SourceFresh: sourceFresh,
            IsPostingMember: memberKeys.Contains(sourceKey),
            HasPersistedDeterministicNegative: NegativeProofKeys.Contains(sourceKey));
    }
}

/// <summary>
/// Builds an <see cref="ExtendedSourceNamespace"/> from per-source extraction outcomes during an index
/// build (plan §7 Phase 4). It streams each source's <em>ephemeral</em> extracted text straight into
/// trigram classification and discards the text immediately — only postings, source keys, and durable
/// negative proofs are retained (§6.4). Determinism policy is enforced through
/// <see cref="ExtendedSourcePolicy"/>: only a <see cref="ExtractionOutcome.DeterministicUnsupported"/> from
/// a deterministic extractor records a negative proof; transient failures and cancellations record nothing
/// (retried on the next full build).
/// </summary>
public sealed class ExtendedSourceNamespaceBuilder
{
    private readonly SpecialSourceKind _kind;
    private readonly ExtractorFingerprint _fingerprint;
    private readonly List<string> _sourceKeys = [];
    private readonly List<IReadOnlyCollection<Trigram>> _documents = [];
    private readonly HashSet<string> _seenKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _negativeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UsnFileIdentity?> _identityByKey = new(StringComparer.Ordinal);

    public ExtendedSourceNamespaceBuilder(SpecialSourceKind kind, ExtractorFingerprint fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        if (fingerprint.Source != kind)
            throw new ArgumentException(
                $"Fingerprint source {fingerprint.Source} does not match namespace kind {kind}.", nameof(fingerprint));
        _kind = kind;
        _fingerprint = fingerprint;
    }

    /// <summary>Admitted source count so far (posting documents).</summary>
    public int AdmittedCount => _documents.Count;

    /// <summary>Durable negative-proof count so far.</summary>
    public int NegativeProofCount => _negativeKeys.Count;

    /// <summary>
    /// Ingests one source's extraction outcome. Duplicate keys within a build are ignored (idempotent).
    /// Returns the assigned document id (&gt;= 0) when the source became a posting member, else -1.
    /// The extracted text is classified into trigrams and immediately dropped — it is never stored.
    /// <paramref name="sourceIdentity"/> is the build-time file identity used for USN freshness (null when
    /// it could not be captured, forcing that source to live-extract; for an archive entry pass the
    /// containing archive file's identity).
    /// </summary>
    public int AddSource(string sourceKey, ExtractionOutcome outcome, UsnFileIdentity? sourceIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        ArgumentNullException.ThrowIfNull(outcome);

        switch (outcome)
        {
            case ExtractionOutcome.Success success:
                if (!_seenKeys.Add(sourceKey))
                    return -1;
                // Classify the ephemeral extracted text into trigrams, then discard the text (§6.4).
                byte[] bytes = Encoding.UTF8.GetBytes(success.Text);
                ContentRepresentationVerdict verdict = ContentRepresentation.Classify(bytes, out IReadOnlyList<Trigram> trigrams);
                // Extractor text is UTF-8; a non-Indexed verdict is unexpected but must never index bad data.
                IReadOnlyCollection<Trigram> set =
                    verdict == ContentRepresentationVerdict.Indexed ? trigrams : [];
                int docId = _documents.Count;
                _documents.Add(set);
                _sourceKeys.Add(sourceKey);
                _identityByKey[sourceKey] = sourceIdentity;
                return docId;

            case ExtractionOutcome.DeterministicUnsupported when ExtendedSourcePolicy.IsPersistableNegative(outcome, _kind):
                _seenKeys.Add(sourceKey);
                _negativeKeys.Add(sourceKey);
                _identityByKey[sourceKey] = sourceIdentity;
                return -1;

            // A DeterministicUnsupported from a NON-deterministic extractor, a TransientFailure, or a
            // Cancelled outcome persists nothing — the source is simply live-extracted next time.
            default:
                return -1;
        }
    }

    /// <summary>
    /// Finalizes the immutable namespace (builds the posting index from the admitted sources).
    /// <paramref name="normalizedRootPath"/> and <paramref name="freshnessCheckpoint"/> record where the
    /// sources live and the USN cursor at build time; omit them (defaults) for a namespace whose freshness
    /// is supplied externally, in which case every source is treated as unprovable-fresh.
    /// </summary>
    public ExtendedSourceNamespace Build(string? normalizedRootPath = null, UsnCheckpoint freshnessCheckpoint = default)
        => new(_kind, _fingerprint, _documents, _sourceKeys, _negativeKeys, _identityByKey,
               normalizedRootPath ?? string.Empty, freshnessCheckpoint);
}
