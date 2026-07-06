using System.Text;

namespace Yagu.Helpers;

/// <summary>
/// LF-normalized "shadow" of a text buffer for cross-line (multiline) regex matching. Collapses
/// every <c>\r\n</c> and lone <c>\r</c> to a single <c>\n</c> so <c>$</c>/<c>.</c> behave uniformly
/// and every engine scans byte-identical text (§8 of the multiline plan). Keeps a SPARSE map — the
/// shadow indices of the <c>\n</c> chars produced by collapsing a <c>\r\n</c> pair — so a shadow
/// offset can be translated back to the original buffer for replace/splice, in O(#CRLF) memory and
/// O(log n) per lookup. A dense per-char <c>int[]</c> map would itself be ~2× the string and is
/// deliberately avoided.
/// </summary>
public sealed class MultilineTextShadow
{
    /// <summary>The LF-only text scanned by the regex (the haystack).</summary>
    public string Lf { get; }

    /// <summary>The original, un-normalized text (used for replace/splice).</summary>
    public string Original { get; }

    // Sorted shadow indices of the '\n' chars produced by collapsing a '\r\n' pair. Only '\r\n'
    // collapses change length (one char removed); a lone '\r'→'\n' is a same-length substitution,
    // so it never shifts offsets and is not recorded here.
    private readonly int[] _crlfCollapsePositions;

    private MultilineTextShadow(string original, string lf, int[] crlfCollapsePositions)
    {
        Original = original;
        Lf = lf;
        _crlfCollapsePositions = crlfCollapsePositions;
    }

    /// <summary>True when <see cref="Lf"/> is byte-identical to <see cref="Original"/> (no <c>\r</c>).</summary>
    public bool IsIdentity => ReferenceEquals(Lf, Original);

    /// <summary>
    /// Builds the LF shadow of <paramref name="original"/>. When the text has no <c>\r</c> the shadow
    /// is the same string instance (identity mapping) and no allocation is done.
    /// </summary>
    public static MultilineTextShadow Build(string original)
    {
        original ??= string.Empty;
        if (original.IndexOf('\r') < 0)
            return new MultilineTextShadow(original, original, Array.Empty<int>());

        var sb = new StringBuilder(original.Length);
        var collapses = new List<int>();
        for (int i = 0; i < original.Length; i++)
        {
            char c = original[i];
            if (c == '\r')
            {
                if (i + 1 < original.Length && original[i + 1] == '\n')
                {
                    // "\r\n" -> "\n": drop the '\r', record the collapse at the shadow index of the '\n'.
                    collapses.Add(sb.Length);
                    sb.Append('\n');
                    i++; // consume the paired '\n'
                }
                else
                {
                    // Lone "\r" -> "\n": length-preserving substitution (no offset shift recorded).
                    sb.Append('\n');
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return new MultilineTextShadow(original, sb.ToString(), collapses.ToArray());
    }

    /// <summary>
    /// Translates an offset in the LF shadow to the corresponding offset in the original buffer.
    /// Works consistently for inclusive start and exclusive end offsets of a match span.
    /// </summary>
    public int ToOriginalOffset(int lfOffset)
    {
        var positions = _crlfCollapsePositions;
        if (positions.Length == 0) return lfOffset;

        // Count collapse positions strictly less than lfOffset (lower bound).
        int lo = 0, hi = positions.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (positions[mid] < lfOffset) lo = mid + 1;
            else hi = mid;
        }
        return lfOffset + lo;
    }
}
