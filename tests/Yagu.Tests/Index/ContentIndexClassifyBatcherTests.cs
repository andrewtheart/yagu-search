using System;
using System.Collections.Generic;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexClassifyBatcher"/> — the pure batching state machine of the Stage-3 async
/// classification pipeline (plan §5.3). It must emit a batch on any of the three triggers (path count,
/// encoded bytes, latency), preserve order, reset cleanly between batches, and never emit an empty batch.
/// </summary>
public sealed class ContentIndexClassifyBatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    private static ContentIndexClassifyBatcher NewBatcher(int maxPaths = 1000, long maxBytes = 1_000_000, int maxLatencyMs = 100)
        => new(maxPaths, maxBytes, TimeSpan.FromMilliseconds(maxLatencyMs));

    [Fact]
    public void Add_BelowAllLimits_BuffersAndReturnsNull()
    {
        var batcher = NewBatcher();
        Assert.Null(batcher.Add(@"c:\a\one.txt", T0));
        Assert.Null(batcher.Add(@"c:\a\two.txt", T0));
        Assert.Equal(2, batcher.PendingCount);
    }

    [Fact]
    public void Add_ReachingPathCap_EmitsThatBatchAndResets()
    {
        var batcher = NewBatcher(maxPaths: 3);
        Assert.Null(batcher.Add("a", T0));
        Assert.Null(batcher.Add("b", T0));
        IReadOnlyList<string>? batch = batcher.Add("c", T0);

        Assert.NotNull(batch);
        Assert.Equal(new[] { "a", "b", "c" }, batch);
        Assert.Equal(0, batcher.PendingCount); // reset after a batch
        Assert.Null(batcher.Add("d", T0)); // the next batch starts fresh
    }

    [Fact]
    public void Add_ReachingByteCap_EmitsBatch()
    {
        // Each path is 10 UTF-8 bytes + 1 newline = 11; a 22-byte cap flushes after the 2nd path.
        var batcher = NewBatcher(maxBytes: 22);
        Assert.Equal(10, Encoding.UTF8.GetByteCount("abcde12345"));
        Assert.Null(batcher.Add("abcde12345", T0));
        IReadOnlyList<string>? batch = batcher.Add("fghij67890", T0);

        Assert.NotNull(batch);
        Assert.Equal(2, batch!.Count);
        Assert.Equal(0, batcher.PendingCount);
    }

    [Fact]
    public void TryFlushDueToLatency_BeforeBudget_ReturnsNull_AfterBudget_ReturnsBatch()
    {
        var batcher = NewBatcher(maxLatencyMs: 50);
        batcher.Add("a", T0);
        batcher.Add("b", T0.AddMilliseconds(10)); // oldest stays T0

        Assert.Null(batcher.TryFlushDueToLatency(T0.AddMilliseconds(49)));
        IReadOnlyList<string>? batch = batcher.TryFlushDueToLatency(T0.AddMilliseconds(50));
        Assert.NotNull(batch);
        Assert.Equal(new[] { "a", "b" }, batch);
        Assert.Equal(0, batcher.PendingCount);
    }

    [Fact]
    public void TryFlushDueToLatency_Empty_ReturnsNull()
    {
        var batcher = NewBatcher(maxLatencyMs: 1);
        Assert.Null(batcher.TryFlushDueToLatency(T0.AddSeconds(10)));
    }

    [Fact]
    public void LatencyClock_RestartsPerBatch()
    {
        var batcher = NewBatcher(maxPaths: 2, maxLatencyMs: 100);
        batcher.Add("a", T0);
        IReadOnlyList<string>? full = batcher.Add("b", T0); // path cap → batch, resets clock
        Assert.NotNull(full);

        batcher.Add("c", T0.AddMilliseconds(200)); // new batch's oldest is now this timestamp
        Assert.Null(batcher.TryFlushDueToLatency(T0.AddMilliseconds(250))); // only 50 ms old → not due
        Assert.NotNull(batcher.TryFlushDueToLatency(T0.AddMilliseconds(300))); // 100 ms old → due
    }

    [Fact]
    public void Flush_ReturnsRemaining_ThenNull()
    {
        var batcher = NewBatcher();
        batcher.Add("a", T0);
        batcher.Add("b", T0);
        Assert.Equal(new[] { "a", "b" }, batcher.Flush());
        Assert.Null(batcher.Flush()); // nothing left
    }

    [Fact]
    public void Constructor_RejectsInvalidBudgets()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentIndexClassifyBatcher(0, 100, TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentIndexClassifyBatcher(10, 0, TimeSpan.FromMilliseconds(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContentIndexClassifyBatcher(10, 100, TimeSpan.Zero));
    }

    [Fact]
    public void Add_NullPath_Throws()
    {
        var batcher = NewBatcher();
        Assert.Throws<ArgumentNullException>(() => batcher.Add(null!, T0));
    }
}
