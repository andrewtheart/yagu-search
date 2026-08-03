using System;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the Stage-3 framed query transport primitives (plan §5.2): the fixed-width
/// <see cref="QueryFrameHeader"/> binary codec and the pure <see cref="QueryReplyGate"/> late/stale-reply
/// guard. These are the correctness core of safe worker queuing/multiplexing — a reply must round-trip
/// exactly and a reply from a restarted worker / superseded session / wrong batch must be dropped, never
/// misapplied. Pure logic, no worker needed.
/// </summary>
public sealed class IndexQueryFramingTests
{
    // ── QueryFrameHeader codec ──

    [Fact]
    public void Header_Encode_IsFixedWidth()
    {
        byte[] bytes = new QueryFrameHeader(1, 2, 3, 4, 5, 6).Encode();
        Assert.Equal(QueryFrameHeader.SizeBytes, bytes.Length);
        Assert.Equal(32, bytes.Length);
    }

    [Fact]
    public void Header_RoundTrips_AllFields()
    {
        var header = new QueryFrameHeader(
            Epoch: 7,
            SessionId: 42,
            BatchSeq: 9_000_000_000L,          // > int.MaxValue → exercises the 64-bit field
            Count: 1234,
            PayloadLength: 56_789,
            DeadlineUnixMs: 1_800_000_000_000L);

        QueryFrameHeader decoded = QueryFrameHeader.Decode(header.Encode());

        Assert.Equal(header, decoded); // record-struct value equality over every field
    }

    [Fact]
    public void Header_Decode_ReadsLittleEndian()
    {
        // Epoch=1 in LE is 01 00 00 00; the codec must not be endian-swapped.
        byte[] bytes = new QueryFrameHeader(1, 0, 0, 0, 0, 0).Encode();
        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(0x00, bytes[1]);
    }

    [Fact]
    public void Header_WriteTo_TooSmall_Throws()
    {
        var header = new QueryFrameHeader(1, 1, 1, 1, 1, 1);
        Assert.Throws<ArgumentException>(() => header.WriteTo(new byte[QueryFrameHeader.SizeBytes - 1]));
    }

    [Fact]
    public void Header_Decode_TooShort_Throws()
    {
        Assert.Throws<FormatException>(() => QueryFrameHeader.Decode(new byte[QueryFrameHeader.SizeBytes - 1]));
    }

    [Theory]
    [InlineData(-1, 0)]  // negative count
    [InlineData(0, -1)]  // negative payload length
    public void Header_Decode_NegativeLengths_Throws(int count, int payloadLength)
    {
        byte[] bytes = new QueryFrameHeader(1, 1, 1, count, payloadLength, 0).Encode();
        Assert.Throws<FormatException>(() => QueryFrameHeader.Decode(bytes));
    }

    [Fact]
    public void Header_IsExpired_HonorsDeadlineAndZeroMeansNever()
    {
        var withDeadline = new QueryFrameHeader(1, 1, 1, 1, 1, DeadlineUnixMs: 1000);
        Assert.False(withDeadline.IsExpired(1000)); // exactly at the deadline is not yet expired
        Assert.True(withDeadline.IsExpired(1001));

        var noDeadline = withDeadline with { DeadlineUnixMs = QueryFrameHeader.NoDeadline };
        Assert.False(noDeadline.IsExpired(long.MaxValue)); // 0 = no deadline → never expires
    }

    // ── QueryReplyGate late/stale-reply guard ──

    [Fact]
    public void ReplyGate_Accepts_MatchingEpochSessionAndSequence()
    {
        var reply = new QueryFrameHeader(Epoch: 3, SessionId: 5, BatchSeq: 8, 0, 0, 0);
        Assert.Equal(QueryReplyDisposition.Accept,
            QueryReplyGate.Classify(currentEpoch: 3, currentSessionId: 5, awaitedBatchSeq: 8, reply));
    }

    [Fact]
    public void ReplyGate_DropsStaleEpoch_First()
    {
        // A restarted worker (old epoch) is dropped even if the session/sequence coincidentally match.
        var reply = new QueryFrameHeader(Epoch: 2, SessionId: 5, BatchSeq: 8, 0, 0, 0);
        Assert.Equal(QueryReplyDisposition.StaleEpoch,
            QueryReplyGate.Classify(currentEpoch: 3, currentSessionId: 5, awaitedBatchSeq: 8, reply));
    }

    [Fact]
    public void ReplyGate_DropsStaleSession_WhenEpochMatches()
    {
        var reply = new QueryFrameHeader(Epoch: 3, SessionId: 4, BatchSeq: 8, 0, 0, 0);
        Assert.Equal(QueryReplyDisposition.StaleSession,
            QueryReplyGate.Classify(currentEpoch: 3, currentSessionId: 5, awaitedBatchSeq: 8, reply));
    }

    [Fact]
    public void ReplyGate_DropsStaleSequence_WhenEpochAndSessionMatch()
    {
        // A late/duplicate reply for a batch we have moved past is dropped, never applied to the wrong batch.
        var reply = new QueryFrameHeader(Epoch: 3, SessionId: 5, BatchSeq: 7, 0, 0, 0);
        Assert.Equal(QueryReplyDisposition.StaleSequence,
            QueryReplyGate.Classify(currentEpoch: 3, currentSessionId: 5, awaitedBatchSeq: 8, reply));
    }
}
