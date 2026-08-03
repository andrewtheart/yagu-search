namespace Yagu.Services.Index;

/// <summary>Which non-raw-text content source a special path belongs to (plan §3.5/§5).</summary>
public enum SpecialSourceKind
{
    Archive,
    ImageOcr,
    PdfText,
}

/// <summary>
/// The tagged classification the index worker returns for every discovered path (plan §3.5). It is
/// deliberately a closed set of cases — never a bare bool — because the same verdict drives routing,
/// status, provenance, and tests. Content ids/alias ids are the persisted identities from the
/// generation (<c>long</c> here to stay above the on-disk <c>u32</c> id space).
/// </summary>
public abstract record IndexPathClassification
{
    private IndexPathClassification() { }

    /// <summary>A fresh, trusted, posting-selected ordinary-text file — verified live before display.</summary>
    public sealed record FreshIndexedMember(long AliasId, long ContentId) : IndexPathClassification;

    /// <summary>A fresh, trusted ordinary-text file that is <em>not</em> a posting member — provisionally pruned.</summary>
    public sealed record FreshIndexedNonmember(long AliasId, long ContentId) : IndexPathClassification;

    /// <summary>The content changed per the USN journal since the generation was built.</summary>
    public sealed record DirtyByUsn(long ContentId, string Reason) : IndexPathClassification;

    /// <summary>No trusted index entry (absent, over-cap, unsupported representation, identity mismatch, …).</summary>
    public sealed record Unindexed(string Reason) : IndexPathClassification;

    /// <summary>An archive/OCR/PDF candidate — routed to its extractor, never pruned by the raw-text index.</summary>
    public sealed record SpecialSource(SpecialSourceKind Kind) : IndexPathClassification;

    /// <summary>The covering root is not trusted for this search.</summary>
    public sealed record UntrustedRoot(string Reason) : IndexPathClassification;
}
