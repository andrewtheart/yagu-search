using System.Buffers;

namespace Yagu.Helpers;

/// <summary>Bounded-memory operations for literal text streams.</summary>
internal static class LiteralTextOperations
{
    /// <summary>
    /// Counts non-overlapping occurrences without retaining the whole input. The carry window preserves
    /// matches spanning reader-buffer boundaries while remaining bounded by the needle length.
    /// </summary>
    internal static long CountNonOverlapping(
        TextReader reader,
        string needle,
        StringComparison comparison,
        int bufferLength = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (string.IsNullOrEmpty(needle))
            return 0;
        if (bufferLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(bufferLength));

        char[] buffer = ArrayPool<char>.Shared.Rent(bufferLength);
        string carry = string.Empty;
        long count = 0;
        try
        {
            while (true)
            {
                int read = reader.Read(buffer, 0, bufferLength);
                if (read == 0)
                {
                    int finalPosition = 0;
                    while (finalPosition <= carry.Length - needle.Length)
                    {
                        int match = carry.IndexOf(needle, finalPosition, comparison);
                        if (match < 0) break;
                        count++;
                        finalPosition = match + needle.Length;
                    }
                    return count;
                }

                string chunk = carry.Length == 0
                    ? new string(buffer, 0, read)
                    : string.Concat(carry, new ReadOnlySpan<char>(buffer, 0, read));
                int deferredCharacters = Math.Min(needle.Length - 1, chunk.Length);
                int safeEnd = chunk.Length - deferredCharacters;
                int position = 0;
                while (position <= chunk.Length - needle.Length)
                {
                    int match = chunk.IndexOf(needle, position, comparison);
                    if (match < 0 || match >= safeEnd) break;
                    count++;
                    position = match + needle.Length;
                }

                // Do not retain characters already consumed by a boundary-crossing match; otherwise a
                // self-overlapping needle (for example "aaaa") could be counted twice across chunks.
                int carryStart = Math.Max(safeEnd, Math.Min(position, chunk.Length));
                carry = chunk[carryStart..];
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}
