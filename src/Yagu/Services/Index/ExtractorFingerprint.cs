using System.Security.Cryptography;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// One file that is part of an extractor's identity — its executable, a runtime/provider library, or
/// a model file — captured by content hash so a swapped or upgraded binary invalidates the fingerprint
/// (plan §7 Phase 4). The <paramref name="Role"/> is a stable label (e.g. <c>exe</c>, <c>model</c>).
/// </summary>
public readonly record struct ExtractorFileHash(string Role, string Sha256)
{
    /// <summary>Canonical, case-folded <c>role=sha</c> form used to build the fingerprint digest.</summary>
    public string Canonical() =>
        (Role ?? string.Empty).Trim().ToLowerInvariant() + "=" + (Sha256 ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>
/// The exact identity of the extractor configuration that produced posting text for an extended
/// content source (archive / PDF-text / OCR). <b>Any</b> difference makes that source namespace
/// live-only: fingerprints are compared by their canonical <see cref="Digest"/>, and a mismatch is
/// never pruned (plan §7 Phase 4). It deliberately includes hashes of the executable/library/model
/// files, the runtime/provider, the engine version, and every relevant option — never just a friendly
/// version string, because a byte-different extractor can produce byte-different text. The class is
/// immutable and canonicalizes its inputs (sorts the hash and option lists, trims/case-folds), so two
/// fingerprints built from the same facts in any order compare equal.
/// </summary>
public sealed class ExtractorFingerprint
{
    /// <summary>Which extended content source this extractor serves.</summary>
    public SpecialSourceKind Source { get; }

    /// <summary>Stable extractor id (e.g. <c>pdftotext</c>, an OCR engine id).</summary>
    public string EngineId { get; }

    /// <summary>The extractor/tool version string (part of the fingerprint, never the whole of it).</summary>
    public string EngineVersion { get; }

    /// <summary>The runtime/provider that ran the extractor (e.g. <c>cpu</c>, <c>dml</c>, a RID).</summary>
    public string Runtime { get; }

    /// <summary>Content hashes of the executable/library/model files, canonically sorted.</summary>
    public IReadOnlyList<ExtractorFileHash> BinaryHashes { get; }

    /// <summary>Every option that affects extractor output, canonically sorted by key.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Options { get; }

    /// <summary>Stable lower-case hex SHA-256 over the canonical serialization; the sole equality key.</summary>
    public string Digest { get; }

    public ExtractorFingerprint(
        SpecialSourceKind source,
        string engineId,
        string engineVersion,
        string runtime,
        IEnumerable<ExtractorFileHash>? binaryHashes = null,
        IEnumerable<KeyValuePair<string, string>>? options = null)
    {
        Source = source;
        EngineId = (engineId ?? string.Empty).Trim();
        EngineVersion = (engineVersion ?? string.Empty).Trim();
        Runtime = (runtime ?? string.Empty).Trim();

        // Canonicalize so element order never changes the digest.
        BinaryHashes = (binaryHashes ?? [])
            .Select(h => new ExtractorFileHash(
                (h.Role ?? string.Empty).Trim().ToLowerInvariant(),
                (h.Sha256 ?? string.Empty).Trim().ToLowerInvariant()))
            .OrderBy(h => h.Role, StringComparer.Ordinal)
            .ThenBy(h => h.Sha256, StringComparer.Ordinal)
            .ToArray();
        Options = (options ?? [])
            .Select(o => new KeyValuePair<string, string>((o.Key ?? string.Empty).Trim(), o.Value ?? string.Empty))
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .ThenBy(o => o.Value, StringComparer.Ordinal)
            .ToArray();

        Digest = ComputeDigest();
    }

    /// <summary>True when <paramref name="other"/> is the byte-identical extractor configuration.</summary>
    public bool Matches(ExtractorFingerprint? other) =>
        other is not null && string.Equals(Digest, other.Digest, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is ExtractorFingerprint other && string.Equals(Digest, other.Digest, StringComparison.Ordinal);

    public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);

    private string ComputeDigest()
    {
        var sb = new StringBuilder();
        sb.Append("src=").Append((int)Source).Append('\n');
        sb.Append("engine=").Append(EngineId).Append('\n');
        sb.Append("version=").Append(EngineVersion).Append('\n');
        sb.Append("runtime=").Append(Runtime).Append('\n');
        sb.Append("bins=");
        foreach (ExtractorFileHash h in BinaryHashes)
            sb.Append(h.Canonical()).Append(';');
        sb.Append('\n').Append("opts=");
        foreach (KeyValuePair<string, string> o in Options)
            sb.Append(o.Key).Append('=').Append(o.Value).Append(';');

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
