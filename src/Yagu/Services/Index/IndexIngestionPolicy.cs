using Yagu.Helpers;

namespace Yagu.Services.Index;

/// <summary>
/// Typed reason a file was skipped or failed during an index build (plan §6.2/§9.4), modeled on
/// DocFetcher's <c>IndexingReporter</c> / <c>IndexingError.ErrorType</c>. <see cref="None"/> means the
/// file was admitted. Every non-<see cref="None"/> value means "this file always falls back to a live
/// scan", surfaced in the Indexing tab and <c>--index-status</c>. Diagnostics only — never changes results.
/// </summary>
public enum IndexSkipReason
{
    None,
    Binary,
    UnsupportedEncoding,
    OverSizeCap,
    ExcludedByGlob,
    ExcludedByExtension,
    Hidden,
    CloudOnly,
    ReparsePointSkipped,
    OverDepth,
    AccessDenied,
    IoTimeout,
    ExtractionFailure,
}

/// <summary>
/// The build-time ingestion policy (plan §3.6/§6.1). It holds only <em>representation constraints</em>
/// (ordinary text, size cap, cloud/reparse/hidden/depth policy, build-time excludes) — never the
/// mutable per-search filters. A file omitted by this policy is simply live-scanned when a later query
/// permits it, so changing a search filter never invalidates a generation.
/// </summary>
public sealed partial class IndexIngestionPolicy
{
    /// <summary>Per-file hard byte cap; 0 disables the cap.</summary>
    public long MaxFileSizeBytes { get; }

    /// <summary>Build-time exclude globs (not query filters).</summary>
    public IReadOnlyList<string> ExcludedGlobs { get; }

    /// <summary>Build-time exclude extensions (without leading dot, case-insensitive).</summary>
    public IReadOnlySet<string> ExcludedExtensions { get; }

    /// <summary>Whether hidden files are ingested.</summary>
    public bool IncludeHiddenFiles { get; }

    /// <summary>Whether same-volume/in-root reparse targets are ingested.</summary>
    public bool FollowReparsePoints { get; }

    /// <summary>Maximum crawl depth; 0 = unlimited. Over-deep subtrees are reported and left unindexed.</summary>
    public int MaxDepth { get; }

    /// <summary>Whether positively-detected binary files may be admitted through the bounded printable-ASCII
    /// representation. Overflowed/unsupported binaries remain unindexed and always live-scan.</summary>
    public bool IndexBinaryAsciiContent { get; }

    /// <summary>
    /// Per-root <em>re-admit</em> globs (gitignore-style negation): a path that a broader exclude would
    /// drop is still ingested when it matches one of these. Empty for the global-only policy.
    /// </summary>
    public IReadOnlyList<string> ReAdmitGlobs { get; }

    private readonly GlobMatcher _excludeMatcher;
    private readonly GlobMatcher? _reAdmitMatcher;

    public IndexIngestionPolicy(
        long maxFileSizeBytes,
        IReadOnlyList<string>? excludedGlobs,
        IReadOnlySet<string>? excludedExtensions,
        bool includeHiddenFiles,
        bool followReparsePoints,
        int maxDepth,
        IReadOnlyList<string>? reAdmitGlobs = null,
        bool indexBinaryAsciiContent = false)
    {
        MaxFileSizeBytes = Math.Max(0, maxFileSizeBytes);
        ExcludedGlobs = excludedGlobs ?? Array.Empty<string>();
        ExcludedExtensions = excludedExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IncludeHiddenFiles = includeHiddenFiles;
        FollowReparsePoints = followReparsePoints;
        MaxDepth = Math.Max(0, maxDepth);
        ReAdmitGlobs = reAdmitGlobs ?? Array.Empty<string>();
        IndexBinaryAsciiContent = indexBinaryAsciiContent;
        _excludeMatcher = new GlobMatcher(Array.Empty<string>(), ExcludedGlobs);
        // A GlobMatcher with the re-admit patterns as INCLUDES returns true iff a path matches one of them.
        _reAdmitMatcher = ReAdmitGlobs.Count > 0 ? new GlobMatcher(ReAdmitGlobs, Array.Empty<string>()) : null;
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is excluded from the build: it matches a build-time exclude
    /// glob AND is not re-admitted by a per-root include glob (gitignore-style negation).
    /// </summary>
    public bool IsGloballyExcluded(string fullPath)
    {
        if (ExcludedGlobs.Count == 0)
            return false;
        if (_excludeMatcher.Matches(fullPath))
            return false; // not matched by any exclude -> kept
        // Matched an exclude. A per-root re-admit glob overrides it for this root.
        return _reAdmitMatcher is null || !_reAdmitMatcher.Matches(fullPath);
    }

}

/// <summary>Metadata about a candidate file, independent of any actual filesystem access.</summary>
public readonly record struct IngestionFileInfo(
    string Path,
    long SizeBytes,
    int Depth,
    bool IsHidden,
    bool IsReparsePoint,
    bool IsCloudOnly);

/// <summary>The trigram/skip outcome of classifying a file's raw bytes against an ingestion policy.</summary>
public readonly record struct IndexContentClassification(IndexSkipReason Reason, IReadOnlyList<Trigram> Trigrams)
{
    /// <summary>True when the content was admitted into the index.</summary>
    public bool Admitted => Reason == IndexSkipReason.None;
}

/// <summary>
/// Pure classifier that decides whether a candidate file is admissible for indexing (plan §3.6). It is
/// split into a cheap metadata gate (<see cref="ClassifyFile"/>) and a content gate
/// (<see cref="ClassifyContent"/>) so the crawler can reject most files without opening them.
/// </summary>
public static class IndexIngestionClassifier
{
    /// <summary>
    /// Cheap metadata classification. Returns <see cref="IndexSkipReason.None"/> when the file passes
    /// every metadata gate (its content must still be classified). Order: depth → hidden → cloud-only →
    /// reparse → size cap → excluded extension → excluded glob.
    /// </summary>
    public static IndexSkipReason ClassifyFile(IngestionFileInfo file, IndexIngestionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.MaxDepth > 0 && file.Depth > policy.MaxDepth)
            return IndexSkipReason.OverDepth;
        if (!policy.IncludeHiddenFiles && file.IsHidden)
            return IndexSkipReason.Hidden;
        if (file.IsCloudOnly)
            return IndexSkipReason.CloudOnly; // v1 never hydrates online-only files (plan §6.1)
        if (file.IsReparsePoint && !policy.FollowReparsePoints)
            return IndexSkipReason.ReparsePointSkipped;
        if (policy.MaxFileSizeBytes > 0 && file.SizeBytes > policy.MaxFileSizeBytes)
            return IndexSkipReason.OverSizeCap;
        if (ExtensionExcluded(file.Path, policy))
            return IndexSkipReason.ExcludedByExtension;
        if (policy.IsGloballyExcluded(file.Path))
            return IndexSkipReason.ExcludedByGlob;
        return IndexSkipReason.None;
    }

    /// <summary>
    /// Content classification: applies the byte cap, then the canonical representation gate
    /// (<see cref="ContentRepresentation.Classify"/>). Binary and unsupported-encoding files are
    /// skipped; admissible BOM-less UTF-8 yields its trigrams.
    /// </summary>
    public static IndexContentClassification ClassifyContent(ReadOnlySpan<byte> content, IndexIngestionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.MaxFileSizeBytes > 0 && content.Length > policy.MaxFileSizeBytes)
            return new IndexContentClassification(IndexSkipReason.OverSizeCap, Array.Empty<Trigram>());

        ContentRepresentationVerdict verdict = ContentRepresentation.Classify(content, out var trigrams);
        return verdict switch
        {
            ContentRepresentationVerdict.Indexed => new IndexContentClassification(IndexSkipReason.None, trigrams),
            ContentRepresentationVerdict.Binary when policy.IndexBinaryAsciiContent => ClassifyBinary(content),
            ContentRepresentationVerdict.Binary => new IndexContentClassification(IndexSkipReason.Binary, Array.Empty<Trigram>()),
            _ => new IndexContentClassification(IndexSkipReason.UnsupportedEncoding, Array.Empty<Trigram>()),
        };
    }

    private static IndexContentClassification ClassifyBinary(ReadOnlySpan<byte> content)
    {
        var representation = new BinaryAsciiContentRepresentation();
        representation.Feed(content);
        return representation.TryFinish(out IReadOnlyList<Trigram> trigrams)
            ? new IndexContentClassification(IndexSkipReason.None, trigrams)
            : new IndexContentClassification(IndexSkipReason.Binary, Array.Empty<Trigram>());
    }

    private static bool ExtensionExcluded(string path, IndexIngestionPolicy policy)
    {
        if (policy.ExcludedExtensions.Count == 0)
            return false;
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Length == 0)
            return false;
        return policy.ExcludedExtensions.Contains(ext.TrimStart('.'));
    }
}
