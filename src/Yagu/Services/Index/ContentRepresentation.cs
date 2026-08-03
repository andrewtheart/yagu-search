using Yagu.Helpers;

namespace Yagu.Services.Index;

/// <summary>
/// Why a file/byte span is not admitted into the content index (plan §3.2). Only
/// <see cref="Indexed"/> yields a usable trigram set; everything else means the file is
/// <em>unindexed</em> and must be live-scanned when a query permits it.
/// </summary>
public enum ContentRepresentationVerdict
{
    /// <summary>Well-formed BOM-less UTF-8 text (RFC 3629). Trigrams were extracted.</summary>
    Indexed,

    /// <summary>Detected as binary by the shared <see cref="BinaryDetector"/> sniff.</summary>
    Binary,

    /// <summary>
    /// Not admissible under the strict v1 allowlist: carries a Unicode BOM (UTF-8/16/32),
    /// or contains an invalid/overlong/surrogate/truncated UTF-8 sequence.
    /// </summary>
    NotBomlessUtf8,
}

/// <summary>
/// The canonical indexed text representation (plan §3.2). v1 admits only well-formed <b>BOM-less
/// UTF-8</b> (an optional UTF-8 BOM is stripped); CRLF and bare CR are normalized to LF; no Unicode normalization is performed. Distinct
/// 3-byte trigrams are extracted from the normalized bytes. Any unsupported representation (BOM,
/// invalid UTF-8), binary content, or empty/short input is handled conservatively — a non-<see
/// cref="ContentRepresentationVerdict.Indexed"/> verdict means "live-scan this file", never a wrong
/// result.
/// </summary>
public static class ContentRepresentation
{
    /// <summary>
    /// Version of the canonical representation contract. Bumped whenever decoding, binary detection,
    /// newline handling, normalization, or the verifier-equivalence allowlist changes (plan §3.2);
    /// older generations become ineligible. Version 3 adds bounded printable-ASCII binary indexing and
    /// UTF-8 BOM stripping; UTF-16/32 remain live-only.
    /// </summary>
    public const int Version = 3;

    /// <summary>Bytes sniffed by the binary detector (mirrors <see cref="BinaryDetector.SampleBytes"/>).</summary>
    public const int BinarySniffBytes = BinaryDetector.SampleBytes;

    /// <summary>
    /// Classifies <paramref name="content"/> and, when it is admissible UTF-8, extracts its
    /// sorted distinct trigram set over the canonical LF-normalized bytes.
    /// </summary>
    /// <param name="content">Raw file bytes.</param>
    /// <param name="trigrams">
    /// Sorted, de-duplicated trigrams when the verdict is <see cref="ContentRepresentationVerdict.Indexed"/>;
    /// otherwise an empty list. A valid file of canonical length 0–2 yields an empty set (plan §5.1 #7).
    /// </param>
    /// <returns>The classification verdict.</returns>
    public static ContentRepresentationVerdict Classify(ReadOnlySpan<byte> content, out IReadOnlyList<Trigram> trigrams)
    {
        trigrams = Array.Empty<Trigram>();

        // UTF-8 BOM is a transport marker, not searchable content: strip it and index the same bytes the
        // matcher sees after decoding. UTF-16/32 remain outside the representation because printable ASCII
        // bytes are separated by NULs and raw trigrams could not safely prove decoded-query absence.
        ReadOnlySpan<byte> payload = content;
        if (StartsWithUtf8Bom(content))
            payload = content[3..];
        else if (StartsWithBom(content))
            return ContentRepresentationVerdict.NotBomlessUtf8;

        // 2. Shared 8 KB binary sniff — NUL bytes, magic numbers, control-byte ratio.
        ReadOnlySpan<byte> sniff = payload.Length > BinarySniffBytes ? payload[..BinarySniffBytes] : payload;
        if (BinaryDetector.IsBinary(sniff))
            return ContentRepresentationVerdict.Binary;

        // 3. Validate strict UTF-8 while normalizing CRLF / bare CR to LF into a scratch buffer.
        var normalized = new List<byte>(payload.Length);
        if (!ValidateAndNormalize(payload, normalized))
            return ContentRepresentationVerdict.NotBomlessUtf8;

        trigrams = ExtractTrigrams(normalized);
        return ContentRepresentationVerdict.Indexed;
    }

    /// <summary>True if the span begins with a UTF-8, UTF-16 (LE/BE), or UTF-32 (LE/BE) byte-order mark.</summary>
    internal static bool StartsWithBom(ReadOnlySpan<byte> content)
    {
        // UTF-32 BOMs must be checked before UTF-16 LE (they share the FF FE prefix).
        if (content.Length >= 4 && content[0] == 0x00 && content[1] == 0x00 && content[2] == 0xFE && content[3] == 0xFF)
            return true; // UTF-32 BE
        if (content.Length >= 4 && content[0] == 0xFF && content[1] == 0xFE && content[2] == 0x00 && content[3] == 0x00)
            return true; // UTF-32 LE
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return true; // UTF-8
        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return true; // UTF-16 LE
        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            return true; // UTF-16 BE
        return false;
    }

    internal static bool StartsWithUtf8Bom(ReadOnlySpan<byte> content) =>
        content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF;

    /// <summary>
    /// Validates that <paramref name="src"/> is well-formed UTF-8 under RFC 3629 (rejecting overlong
    /// encodings, surrogate code points, values above U+10FFFF, and truncated sequences) while
    /// appending the canonical bytes to <paramref name="dst"/> with CRLF and bare CR normalized to LF.
    /// Returns false on the first decoder error.
    /// </summary>
    internal static bool ValidateAndNormalize(ReadOnlySpan<byte> src, List<byte> dst)
    {
        int i = 0;
        int n = src.Length;
        while (i < n)
        {
            byte b = src[i];

            if (b == (byte)'\r')
            {
                dst.Add((byte)'\n');
                // Collapse CRLF into a single LF; a bare CR also becomes LF.
                i += (i + 1 < n && src[i + 1] == (byte)'\n') ? 2 : 1;
                continue;
            }

            if (b < 0x80)
            {
                dst.Add(b);
                i++;
                continue;
            }

            int extra;
            uint min;
            uint cp;
            if ((b & 0xE0) == 0xC0) { extra = 1; min = 0x80; cp = (uint)(b & 0x1F); }
            else if ((b & 0xF0) == 0xE0) { extra = 2; min = 0x800; cp = (uint)(b & 0x0F); }
            else if ((b & 0xF8) == 0xF0) { extra = 3; min = 0x10000; cp = (uint)(b & 0x07); }
            else return false; // invalid lead byte (continuation byte, 0xF8+, etc.)

            if (i + extra >= n)
                return false; // truncated multi-byte sequence

            for (int k = 1; k <= extra; k++)
            {
                byte c = src[i + k];
                if ((c & 0xC0) != 0x80)
                    return false; // bad continuation byte
                cp = (cp << 6) | (uint)(c & 0x3F);
            }

            if (cp < min) return false;                       // overlong encoding
            if (cp > 0x10FFFF) return false;                  // out of range
            if (cp >= 0xD800 && cp <= 0xDFFF) return false;   // UTF-16 surrogate half

            for (int k = 0; k <= extra; k++)
                dst.Add(src[i + k]);
            i += extra + 1;
        }
        return true;
    }

    /// <summary>Extracts the sorted, de-duplicated 3-byte trigram set from canonical bytes.</summary>
    internal static IReadOnlyList<Trigram> ExtractTrigrams(IReadOnlyList<byte> bytes)
    {
        if (bytes.Count < 3)
            return Array.Empty<Trigram>();

        var set = new HashSet<Trigram>();
        for (int i = 0; i + 2 < bytes.Count; i++)
            set.Add(new Trigram(bytes[i], bytes[i + 1], bytes[i + 2]));

        var list = new List<Trigram>(set);
        list.Sort();
        return list;
    }
}
