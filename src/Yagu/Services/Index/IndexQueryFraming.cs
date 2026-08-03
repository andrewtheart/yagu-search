using System;
using System.Buffers.Binary;

namespace Yagu.Services.Index;

/// <summary>
/// Fixed-width binary header for the Stage-3 framed query transport (plan §5.2). Each query request/reply is
/// a <see cref="SizeBytes"/>-byte little-endian header followed by a separately length-framed binary payload.
/// The header identifies which <b>worker generation</b> (<see cref="Epoch"/>), <b>query session</b>
/// (<see cref="SessionId"/>) and <b>batch</b> (<see cref="BatchSeq"/>) a frame belongs to, plus the payload's
/// item <see cref="Count"/> and byte <see cref="PayloadLength"/> and a per-request <see cref="DeadlineUnixMs"/>.
/// These are exactly the fields the transport needs to (a) route a reply to the right in-flight batch, (b)
/// drop a stale/late reply from a restarted worker or a superseded session/sequence (<see cref="QueryReplyGate"/>),
/// and (c) abandon a batch whose deadline elapsed — none of which the Stage-2 line-JSON transport can express.
/// <para>
/// This is the pure framing primitive: it is not yet wired into <c>IndexWorkerClient</c> / the worker host
/// (that is a later slice); it exists so the framing/gating logic is unit-tested in isolation first.
/// </para>
/// </summary>
public readonly record struct QueryFrameHeader(
    int Epoch,
    int SessionId,
    long BatchSeq,
    int Count,
    int PayloadLength,
    long DeadlineUnixMs)
{
    /// <summary>The fixed on-wire size of an encoded header (4 + 4 + 8 + 4 + 4 + 8 bytes).</summary>
    public const int SizeBytes = 32;

    /// <summary>A <see cref="DeadlineUnixMs"/> of 0 means "no deadline" (never expires).</summary>
    public const long NoDeadline = 0;

    /// <summary>Writes the header as <see cref="SizeBytes"/> little-endian bytes.</summary>
    public byte[] Encode()
    {
        var bytes = new byte[SizeBytes];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Writes the header into <paramref name="destination"/> (must be at least <see cref="SizeBytes"/>).</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SizeBytes)
            throw new ArgumentException($"Header destination must be at least {SizeBytes} bytes.", nameof(destination));
        BinaryPrimitives.WriteInt32LittleEndian(destination[0..4], Epoch);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..8], SessionId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..16], BatchSeq);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..20], Count);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..24], PayloadLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..32], DeadlineUnixMs);
    }

    /// <summary>
    /// Decodes a header from <paramref name="source"/>. Throws <see cref="FormatException"/> when the buffer
    /// is too short or carries a negative <see cref="Count"/>/<see cref="PayloadLength"/> (a corrupt frame →
    /// the transport drops the connection to live-scan rather than trusting bogus lengths).
    /// </summary>
    public static QueryFrameHeader Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < SizeBytes)
            throw new FormatException($"Query frame header requires {SizeBytes} bytes, got {source.Length}.");
        var header = new QueryFrameHeader(
            BinaryPrimitives.ReadInt32LittleEndian(source[0..4]),
            BinaryPrimitives.ReadInt32LittleEndian(source[4..8]),
            BinaryPrimitives.ReadInt64LittleEndian(source[8..16]),
            BinaryPrimitives.ReadInt32LittleEndian(source[16..20]),
            BinaryPrimitives.ReadInt32LittleEndian(source[20..24]),
            BinaryPrimitives.ReadInt64LittleEndian(source[24..32]));
        if (header.Count < 0)
            throw new FormatException("Query frame header has a negative item count.");
        if (header.PayloadLength < 0)
            throw new FormatException("Query frame header has a negative payload length.");
        return header;
    }

    /// <summary>True when this frame carries a deadline that has already elapsed at <paramref name="nowUnixMs"/>.</summary>
    public bool IsExpired(long nowUnixMs) => DeadlineUnixMs != NoDeadline && nowUnixMs > DeadlineUnixMs;
}

/// <summary>How the transport should treat an incoming query reply relative to what it currently expects.</summary>
public enum QueryReplyDisposition
{
    /// <summary>The reply matches the current epoch, session, and the awaited batch sequence — apply it.</summary>
    Accept,

    /// <summary>The reply is from a previous worker generation (the worker restarted) — drop it.</summary>
    StaleEpoch,

    /// <summary>The reply is for a different (already-closed/superseded) query session — drop it.</summary>
    StaleSession,

    /// <summary>The reply is for a batch other than the one currently awaited (a late/duplicate reply) — drop it.</summary>
    StaleSequence,
}

/// <summary>
/// The pure <b>late/stale-reply guard</b> (plan §5.2): decides whether a query reply frame should be applied
/// or dropped, given the transport's current worker epoch, open session id, and awaited batch sequence. A
/// reply from a restarted worker (different epoch), a superseded session, or any batch other than the one in
/// flight is dropped so it is never misapplied to the wrong batch's results — the correctness core of safe
/// worker queuing/multiplexing. Stateless and side-effect-free so it is trivially testable.
/// </summary>
public static class QueryReplyGate
{
    /// <summary>
    /// Classifies <paramref name="reply"/> against the transport's current expectation. Epoch is checked
    /// first (a whole-worker restart invalidates everything), then session, then the awaited batch sequence.
    /// </summary>
    public static QueryReplyDisposition Classify(int currentEpoch, int currentSessionId, long awaitedBatchSeq, QueryFrameHeader reply)
    {
        if (reply.Epoch != currentEpoch)
            return QueryReplyDisposition.StaleEpoch;
        if (reply.SessionId != currentSessionId)
            return QueryReplyDisposition.StaleSession;
        if (reply.BatchSeq != awaitedBatchSeq)
            return QueryReplyDisposition.StaleSequence;
        return QueryReplyDisposition.Accept;
    }
}
