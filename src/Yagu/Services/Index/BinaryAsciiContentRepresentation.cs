namespace Yagu.Services.Index;

/// <summary>
/// Bounded query representation for files positively identified as binary. It indexes trigrams only from
/// contiguous printable 7-bit ASCII runs. This is sufficient to prove absence for a search whose required
/// trigram expression is entirely printable ASCII (for example an email address) while avoiding the enormous
/// posting explosion produced by arbitrary compressed/machine-code bytes. A file that exceeds the distinct-
/// trigram cap is omitted from the index and therefore live-scanned — never falsely pruned.
/// </summary>
internal sealed class BinaryAsciiContentRepresentation
{
    /// <summary>Per-file bound chosen from a real binary-corpus sample: ~81% admission while keeping the
    /// query-side postings bounded. Overflow is fail-safe (the path remains unindexed).</summary>
    public const int MaxDistinctTrigramsPerFile = 32768;

    private readonly HashSet<Trigram> _trigrams = new();
    private byte _prior2;
    private byte _prior1;
    private int _runLength;

    /// <summary>True once the bound is exceeded. The caller may stop reading; this file must live-scan.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Feeds raw bytes. Non-printable bytes break a run; bytes on opposite sides of such a boundary
    /// are never joined into a trigram.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (Overflowed)
            return;

        foreach (byte value in bytes)
        {
            if (value is < 0x20 or > 0x7E)
            {
                _runLength = 0;
                continue;
            }

            if (_runLength >= 2 && _trigrams.Add(new Trigram(_prior2, _prior1, value))
                && _trigrams.Count > MaxDistinctTrigramsPerFile)
            {
                Overflowed = true;
                _trigrams.Clear();
                return;
            }

            _prior2 = _prior1;
            _prior1 = value;
            _runLength++;
        }
    }

    /// <summary>Returns a sorted immutable trigram list, or false when the file exceeded the bound.</summary>
    public bool TryFinish(out IReadOnlyList<Trigram> trigrams)
    {
        if (Overflowed)
        {
            trigrams = Array.Empty<Trigram>();
            return false;
        }

        var sorted = new List<Trigram>(_trigrams);
        sorted.Sort();
        trigrams = sorted;
        return true;
    }

    /// <summary>
    /// The binary representation can safely participate only when every required trigram byte is printable
    /// ASCII. Monotone AND/OR composition preserves the superset guarantee; All/None are rejected because
    /// they provide no useful required printable evidence.
    /// </summary>
    public static bool CanSafelyEvaluate(TrigramExpression query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Kind switch
        {
            TrigramExpression.NodeKind.Trigram => IsPrintable(query.Trigram),
            TrigramExpression.NodeKind.And or TrigramExpression.NodeKind.Or =>
                query.Children.All(CanSafelyEvaluate),
            _ => false,
        };
    }

    private static bool IsPrintable(Trigram trigram) =>
        trigram.Byte0 is >= 0x20 and <= 0x7E &&
        trigram.Byte1 is >= 0x20 and <= 0x7E &&
        trigram.Byte2 is >= 0x20 and <= 0x7E;
}
