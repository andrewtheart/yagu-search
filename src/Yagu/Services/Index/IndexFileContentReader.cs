using System.Buffers;
using Yagu.Helpers;

namespace Yagu.Services.Index;

/// <summary>
/// The outcome of reading and classifying a single candidate file (plan §5.1). It carries the same
/// <see cref="IndexSkipReason"/>/trigram result the in-place <see cref="IndexIngestionClassifier.ClassifyContent"/>
/// would produce, plus the <em>actual</em> number of content bytes physically read — the metric the build
/// benchmark uses to prove the prefix-rejection I/O win on binary-heavy corpora — and, for an admitted
/// file, the durable <see cref="FileIdentity"/> captured from the very same handle the bytes came from
/// (plan §5.4), so the build never opens the file a second time just to read its identity.
/// </summary>
internal readonly record struct IndexFileReadResult(
    IndexSkipReason Reason,
    IReadOnlyList<Trigram> Trigrams,
    long BytesRead,
    FileIdentity? Identity = null)
{
    /// <summary>True when the content was admitted into the index.</summary>
    public bool Admitted => Reason == IndexSkipReason.None;

    /// <summary>The equivalent content classification (reason + trigrams) for the generation builder.</summary>
    public IndexContentClassification Classification => new(Reason, Trigrams);
}

/// <summary>
/// A worker-safe seam for reading and classifying a candidate file's content during an index build
/// (plan §5.1). Extracting this behind an interface lets the build loop:
/// <list type="bullet">
///   <item>reject BOM/binary files after at most the existing <see cref="ContentRepresentation.BinarySniffBytes"/>
///     sniff instead of reading the whole file (the binary-heavy win);</item>
///   <item>account for the actual bytes read; and</item>
///   <item>be driven by deterministic fake streams / fault injection in tests.</item>
/// </list>
/// It is deliberately internal — never a user-facing API.
/// </summary>
internal interface IIndexFileContentReader
{
    /// <summary>
    /// Reads and classifies <paramref name="path"/> using a single sequential file handle, returning the
    /// same reason/trigram result the reference in-memory classifier would for the file's bytes.
    /// </summary>
    /// <param name="path">The candidate file path (already crawler-admitted by the metadata gate).</param>
    /// <param name="expectedLength">The length observed at crawl time (a hint; the open handle is authoritative).</param>
    /// <param name="policy">The ingestion policy (supplies the size cap).</param>
    /// <param name="cancellationToken">Cancels a long read cooperatively.</param>
    IndexFileReadResult Read(string path, long expectedLength, IndexIngestionPolicy policy, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IIndexFileContentReader"/>: opens each candidate once with sequential-scan intent,
/// reads at most the first <see cref="ContentRepresentation.BinarySniffBytes"/> bytes, rejects UTF-16/32,
/// strips UTF-8 BOM, and (when enabled) streams bounded printable-ASCII trigrams from binary files. The
/// full body is then handed to the <b>unchanged reference classifier</b>
/// (<see cref="IndexIngestionClassifier.ClassifyContent"/>), so for every stable file the result is
/// byte-for-byte identical to the previous <c>File.ReadAllBytes</c> path — this stage isolates the
/// prefix-I/O win from the Stage 2 streaming state machine.
/// </summary>
internal sealed class IndexFileContentReader : IIndexFileContentReader
{
    public IndexFileReadResult Read(string path, long expectedLength, IndexIngestionPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();

        long cap = policy.MaxFileSizeBytes;

        // FileShare.Read matches File.ReadAllBytes' effective sharing semantics (plan risk register); do
        // NOT loosen it merely for speed. SequentialScan hints the cache manager for a forward read.
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.SequentialScan,
        });

        // The reference ClassifyContent applies the size cap to content.Length FIRST. For a stable file
        // the current on-disk length is exactly what File.ReadAllBytes would materialize, so reject an
        // over-cap file here without allocating its (potentially huge) body. The crawl metadata gate has
        // already rejected files whose stat length exceeded the cap; this catches one that grew since.
        if (cap > 0 && stream.Length > cap)
            return new IndexFileReadResult(IndexSkipReason.OverSizeCap, Array.Empty<Trigram>(), 0);

        byte[] prefixBuffer = ArrayPool<byte>.Shared.Rent(ContentRepresentation.BinarySniffBytes);
        try
        {
            int prefixLen = ReadUpTo(stream, prefixBuffer.AsSpan(0, ContentRepresentation.BinarySniffBytes), cancellationToken);
            ReadOnlySpan<byte> prefix = prefixBuffer.AsSpan(0, prefixLen);

            // Preserve the reference order: BOM rejection precedes binary detection. Every Unicode BOM is
            // within the first four bytes, and the binary sniff examines exactly min(fileLength, 8 KB)
            // bytes — the same span the in-memory classifier sees — so both verdicts match bit-for-bit.
            if (ContentRepresentation.StartsWithUtf8Bom(prefix))
                return StreamClassify(stream, prefixBuffer, prefixLen, cap, cancellationToken, prefixOffset: 3);
            if (ContentRepresentation.StartsWithBom(prefix))
                return new IndexFileReadResult(IndexSkipReason.UnsupportedEncoding, Array.Empty<Trigram>(), prefixLen);
            if (BinaryDetector.IsBinary(prefix))
            {
                if (!policy.IndexBinaryAsciiContent)
                    return new IndexFileReadResult(IndexSkipReason.Binary, Array.Empty<Trigram>(), prefixLen);
                return StreamClassifyBinary(stream, prefixBuffer, prefixLen, cap, cancellationToken);
            }

            // Plausible BOM-less text: validate + normalize + extract trigrams by STREAMING the remainder
            // from the same handle (plan §5.3), never allocating the whole raw body or a full normalized
            // copy. The prefix already covered exactly the reference's BOM + first-8 KB binary checks, so
            // the streaming pass only reproduces the reference's UTF-8/newline/trigram stage.
            return StreamClassify(stream, prefixBuffer, prefixLen, cap, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefixBuffer);
        }
    }

    /// <summary>Reads until <paramref name="buffer"/> is full or the stream reaches EOF; returns the count read.</summary>
    private static int ReadUpTo(Stream stream, Span<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int n = stream.Read(buffer[total..]);
            if (n <= 0)
                break;
            total += n;
        }
        return total;
    }

    /// <summary>Size of the pooled buffer used to stream a plausible-text file's body chunk by chunk.</summary>
    private const int StreamChunkBytes = 64 * 1024;

    /// <summary>
    /// Streams the plausible-text body (the already-read <paramref name="prefixBuffer"/> plus the
    /// remainder from the same handle) through the <see cref="StreamingRepresentationClassifier"/>, never
    /// buffering the whole file. Enforces the size cap by stopping once more than <c>cap</c> bytes have
    /// been read (a grow-during-read guard; the up-front <c>stream.Length</c> check already rejected a
    /// stably over-cap file). Cancellation is checked before each chunk read, bounding cancellation
    /// latency to one chunk.
    /// </summary>
    internal static IndexFileReadResult StreamClassify(
        FileStream stream, byte[] prefixBuffer, int prefixLen, long cap, CancellationToken cancellationToken,
        int prefixOffset = 0)
    {
        var classifier = new StreamingRepresentationClassifier();
        long total = prefixLen;

        // The prefix (<= 8 KB, and <= the file length which the up-front check proved <= cap) can never
        // itself be over-cap, so feed it directly.
        if (!classifier.Feed(prefixBuffer.AsSpan(prefixOffset, prefixLen - prefixOffset)))
            return new IndexFileReadResult(IndexSkipReason.UnsupportedEncoding, Array.Empty<Trigram>(), total);

        // A short prefix read means EOF was already reached — the whole file fit in the prefix.
        if (prefixLen == ContentRepresentation.BinarySniffBytes)
        {
            byte[] chunk = ArrayPool<byte>.Shared.Rent(StreamChunkBytes);
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0)
                        break;

                    bool ok = classifier.Feed(chunk.AsSpan(0, n));
                    total += n;
                    if (!ok)
                        return new IndexFileReadResult(IndexSkipReason.UnsupportedEncoding, Array.Empty<Trigram>(), total);

                    // The file grew past the cap since the up-front length check: reject as over-cap,
                    // matching the reference's read-all-then-cap-check outcome (conservative on a mutation).
                    if (cap > 0 && total > cap)
                        return new IndexFileReadResult(IndexSkipReason.OverSizeCap, Array.Empty<Trigram>(), total);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        ContentRepresentationVerdict verdict = classifier.Finish(out IReadOnlyList<Trigram> trigrams);
        if (verdict != ContentRepresentationVerdict.Indexed)
            return new IndexFileReadResult(IndexSkipReason.UnsupportedEncoding, Array.Empty<Trigram>(), total);

        // Admitted: capture the durable identity from the SAME handle whose bytes were just indexed
        // (plan §5.4) — no second CreateFileW, and the identity provably belongs to the exact file object
        // that was read even under a concurrent rename/replace.
        FileIdentity? identity = FileIdentityReader.TryGetIdentity(stream.SafeFileHandle);
        return new IndexFileReadResult(IndexSkipReason.None, trigrams, total, identity);
    }

    /// <summary>Streams a positively-detected binary file through the bounded printable-ASCII representation.
    /// Exceeding the trigram bound stops the read early and leaves the file unindexed/live-scanned.</summary>
    internal static IndexFileReadResult StreamClassifyBinary(
        FileStream stream, byte[] prefixBuffer, int prefixLen, long cap, CancellationToken cancellationToken)
    {
        var representation = new BinaryAsciiContentRepresentation();
        representation.Feed(prefixBuffer.AsSpan(0, prefixLen));
        long total = prefixLen;

        if (prefixLen == ContentRepresentation.BinarySniffBytes)
        {
            byte[] chunk = ArrayPool<byte>.Shared.Rent(StreamChunkBytes);
            try
            {
                while (!representation.Overflowed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0)
                        break;
                    total += n;
                    if (cap > 0 && total > cap)
                        return new IndexFileReadResult(IndexSkipReason.OverSizeCap, Array.Empty<Trigram>(), total);
                    representation.Feed(chunk.AsSpan(0, n));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        if (!representation.TryFinish(out IReadOnlyList<Trigram> trigrams))
            return new IndexFileReadResult(IndexSkipReason.Binary, Array.Empty<Trigram>(), total);

        FileIdentity? identity = FileIdentityReader.TryGetIdentity(stream.SafeFileHandle);
        return new IndexFileReadResult(IndexSkipReason.None, trigrams, total, identity);
    }
}
