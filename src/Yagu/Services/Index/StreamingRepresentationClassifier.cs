namespace Yagu.Services.Index;

/// <summary>
/// Streaming equivalent of <see cref="ContentRepresentation.ValidateAndNormalize"/> +
/// <see cref="ContentRepresentation.ExtractTrigrams"/> (plan §5.3). It validates strict RFC 3629
/// BOM-less UTF-8, normalizes CRLF / bare CR to LF, and accumulates the distinct trigram set over the
/// canonical bytes — <b>in a single forward pass over pooled chunks</b>, never materializing the whole
/// raw file or a full normalized <c>List&lt;byte&gt;</c> the way the in-memory reference does.
/// <para>
/// It carries only the tiny cross-chunk state a byte stream needs: an in-progress multibyte sequence,
/// whether the previous byte was a CR awaiting a following LF, and the two most recent canonical bytes
/// for the rolling trigram window. BOM detection and binary sniffing are <b>not</b> its job — the reader
/// decides those from the first 8 KB prefix before ever constructing a classifier, exactly mirroring the
/// reference's first-8 KB binary sniff and leading-BOM check.
/// </para>
/// <para>
/// Design note: continuation/lead bytes are emitted into the trigram window <em>optimistically</em> as
/// they are read, and the multibyte sequence's overlong/surrogate/range validity is confirmed once the
/// sequence completes. This is safe because any invalid sequence rejects the <em>entire</em> file
/// (<see cref="ContentRepresentationVerdict.NotBomlessUtf8"/>), so the trigrams accumulated for a
/// rejected file are discarded — while a fully valid file emits exactly the same bytes, in the same
/// order, as the reference. This avoids buffering the in-flight sequence bytes.
/// </para>
/// Single use: construct one per file, <see cref="Feed"/> its chunks in order, then call
/// <see cref="Finish"/> once.
/// </summary>
internal sealed class StreamingRepresentationClassifier
{
    private readonly HashSet<Trigram> _trigrams = new();

    // Rolling window: the two most recent canonical bytes, and how many canonical bytes have been emitted
    // (saturated at 2 — we only need to know whether at least two precede the current byte).
    private byte _window0;
    private byte _window1;
    private int _emitted;

    // A CR was just normalized to LF; if the very next byte is LF it is the second half of a CRLF and is
    // skipped (the LF was already emitted for the CR).
    private bool _pendingCr;

    // In-progress multibyte UTF-8 sequence: total bytes expected (0 = none, else 2/3/4), how many are in,
    // the running code point, and the overlong-encoding minimum for the lead's length.
    private int _seqExpected;
    private int _seqHave;
    private uint _seqCodePoint;
    private uint _seqOverlongMin;

    private bool _invalid;

    /// <summary>True once an invalid UTF-8 byte/sequence has been seen (the file will be rejected).</summary>
    public bool IsInvalid => _invalid;

    /// <summary>
    /// Feeds the next contiguous chunk of raw bytes. Returns <c>false</c> as soon as an invalid UTF-8
    /// error is detected (the caller can stop reading); once <c>false</c>, further feeds are no-ops.
    /// </summary>
    public bool Feed(ReadOnlySpan<byte> chunk)
    {
        if (_invalid)
            return false;

        for (int i = 0; i < chunk.Length; i++)
        {
            byte b = chunk[i];

            if (_seqExpected > 0)
            {
                // Inside a multibyte sequence: the next byte must be a continuation byte.
                if ((b & 0xC0) != 0x80)
                {
                    _invalid = true;
                    return false;
                }
                Emit(b);
                _seqCodePoint = (_seqCodePoint << 6) | (uint)(b & 0x3F);
                _seqHave++;
                if (_seqHave == _seqExpected)
                {
                    if (_seqCodePoint < _seqOverlongMin ||      // overlong encoding
                        _seqCodePoint > 0x10FFFF ||             // out of Unicode range
                        (_seqCodePoint >= 0xD800 && _seqCodePoint <= 0xDFFF)) // UTF-16 surrogate half
                    {
                        _invalid = true;
                        return false;
                    }
                    _seqExpected = 0;
                }
                continue;
            }

            if (_pendingCr)
            {
                _pendingCr = false;
                if (b == (byte)'\n')
                    continue; // CRLF: the LF was already emitted for the CR; drop this LF.
            }

            if (b == (byte)'\r')
            {
                Emit((byte)'\n'); // bare CR or CR of a CRLF both normalize to a single LF.
                _pendingCr = true;
                continue;
            }

            if (b < 0x80)
            {
                Emit(b);
                continue;
            }

            // Multibyte lead byte.
            if ((b & 0xE0) == 0xC0) { _seqExpected = 2; _seqOverlongMin = 0x80; _seqCodePoint = (uint)(b & 0x1F); }
            else if ((b & 0xF0) == 0xE0) { _seqExpected = 3; _seqOverlongMin = 0x800; _seqCodePoint = (uint)(b & 0x0F); }
            else if ((b & 0xF8) == 0xF0) { _seqExpected = 4; _seqOverlongMin = 0x10000; _seqCodePoint = (uint)(b & 0x07); }
            else { _invalid = true; return false; } // continuation byte as lead, 0xF8+, etc.

            Emit(b);
            _seqHave = 1;
        }

        return true;
    }

    /// <summary>
    /// Finalizes classification. Returns <see cref="ContentRepresentationVerdict.Indexed"/> with the
    /// sorted distinct trigram set for well-formed text, or <see cref="ContentRepresentationVerdict.NotBomlessUtf8"/>
    /// when any byte was invalid or a multibyte sequence was truncated at EOF. A pending bare CR needs no
    /// action: its LF was already emitted when the CR was seen.
    /// </summary>
    public ContentRepresentationVerdict Finish(out IReadOnlyList<Trigram> trigrams)
    {
        trigrams = Array.Empty<Trigram>();
        if (_invalid || _seqExpected > 0)
            return ContentRepresentationVerdict.NotBomlessUtf8;

        if (_trigrams.Count > 0)
        {
            var list = new List<Trigram>(_trigrams);
            list.Sort();
            trigrams = list;
        }
        return ContentRepresentationVerdict.Indexed;
    }

    private void Emit(byte b)
    {
        if (_emitted >= 2)
            _trigrams.Add(new Trigram(_window0, _window1, b));
        _window0 = _window1;
        _window1 = b;
        if (_emitted < 2)
            _emitted++;
    }
}
