using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// The durable identity of one entry inside a (possibly nested) archive, for the archive posting
/// namespace (plan §7 Phase 4). Because duplicate names are legal within an archive, identity is the
/// ordered nested-archive <see cref="EntryChain"/> plus the exact <see cref="EntryName"/>, its
/// <see cref="Ordinal"/> among same-named siblings, its <see cref="UncompressedSize"/>, and its
/// <see cref="Crc32"/> where the format provides one. <b>Any</b> mismatch makes that archive entry
/// live-only. Compared by a canonical <see cref="Digest"/> to avoid the reference-equality pitfall of
/// the contained list.
/// </summary>
public sealed class ArchiveEntryIdentity
{
    /// <summary>
    /// The ordered chain of containing archives from the outermost archive down to the archive that
    /// directly holds this entry (empty when the entry lives in a top-level archive that is itself the
    /// discovered file). Nested archives append one segment each.
    /// </summary>
    public IReadOnlyList<string> EntryChain { get; }

    /// <summary>The exact entry name within its immediate container.</summary>
    public string EntryName { get; }

    /// <summary>The zero-based ordinal among entries that share <see cref="EntryName"/> in the container.</summary>
    public int Ordinal { get; }

    /// <summary>The uncompressed size of the entry in bytes (<c>-1</c> when unknown).</summary>
    public long UncompressedSize { get; }

    /// <summary>The entry CRC-32 where the archive format provides one; <c>null</c> otherwise.</summary>
    public uint? Crc32 { get; }

    /// <summary>Stable lower-case hex (128-bit) digest of the canonical identity; the sole equality key.</summary>
    public string Digest { get; }

    public ArchiveEntryIdentity(
        IEnumerable<string> entryChain,
        string entryName,
        int ordinal,
        long uncompressedSize,
        uint? crc32 = null)
    {
        EntryChain = (entryChain ?? []).Select(e => e ?? string.Empty).ToArray();
        EntryName = entryName ?? string.Empty;
        Ordinal = ordinal;
        UncompressedSize = uncompressedSize;
        Crc32 = crc32;
        Digest = ComputeDigest();
    }

    /// <summary>True when <paramref name="other"/> is the byte-identical archive entry identity.</summary>
    public bool Matches(ArchiveEntryIdentity? other) =>
        other is not null && string.Equals(Digest, other.Digest, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is ArchiveEntryIdentity other && string.Equals(Digest, other.Digest, StringComparison.Ordinal);

    public override int GetHashCode() => Digest.GetHashCode(StringComparison.Ordinal);

    private string ComputeDigest()
    {
        // Unit-separated canonical text (control chars can't appear in an archive entry name), so no
        // segment/name boundary can be spoofed by a crafted name.
        var sb = new StringBuilder();
        foreach (string seg in EntryChain)
            sb.Append(seg).Append('\u0001');
        sb.Append('\u0002').Append(EntryName)
          .Append('\u0002').Append(Ordinal.ToString(CultureInfo.InvariantCulture))
          .Append('\u0002').Append(UncompressedSize.ToString(CultureInfo.InvariantCulture))
          .Append('\u0002').Append(Crc32?.ToString(CultureInfo.InvariantCulture) ?? "-");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }
}
