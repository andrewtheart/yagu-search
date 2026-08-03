using System;
using System.Collections.Generic;
using System.Text;

namespace Yagu.Services.Index;

/// <summary>
/// The pure batching state machine at the heart of the Stage-3 async classification pipeline (plan §5.3):
/// it accumulates discovered candidate paths and emits a batch when any of three triggers fires — a
/// <b>path-count</b> cap, an <b>encoded-byte</b> cap (so one batch never exceeds the worker's payload
/// budget), or a <b>latency</b> timer (so a slow trickle of candidates is still classified promptly rather
/// than waiting for a full batch). Order is preserved within a batch. It holds no channels, timers, worker,
/// or spool — the pipeline drives it — so all three flush triggers are deterministically unit-testable
/// (the clock is passed in), which matters because <c>SearchService</c> itself is not unit-testable.
/// </summary>
internal sealed class ContentIndexClassifyBatcher
{
    private readonly int _maxPaths;
    private readonly long _maxEncodedBytes;
    private readonly TimeSpan _maxLatency;
    private readonly List<string> _pending = new();
    private long _pendingBytes;
    private DateTimeOffset _oldestAt;

    /// <summary>
    /// </summary>
    /// <param name="maxPaths">Flush when the batch reaches this many paths (≥ 1).</param>
    /// <param name="maxEncodedBytes">Flush when the batch's approximate UTF-8 payload reaches this many bytes
    /// (≥ 1). This tracks the raw newline-joined UTF-8 size; the base64 wire form is ~4/3 larger, so size the
    /// budget accordingly.</param>
    /// <param name="maxLatency">Flush a partial batch whose oldest path has waited at least this long (&gt; 0).</param>
    public ContentIndexClassifyBatcher(int maxPaths, long maxEncodedBytes, TimeSpan maxLatency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPaths, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEncodedBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLatency, TimeSpan.Zero);
        _maxPaths = maxPaths;
        _maxEncodedBytes = maxEncodedBytes;
        _maxLatency = maxLatency;
    }

    /// <summary>Number of paths currently buffered (not yet emitted in a batch).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Adds a discovered candidate path. Returns a full batch (and resets) when the path-count or encoded-byte
    /// cap is reached; otherwise null (the path is buffered). <paramref name="now"/> stamps the batch's oldest
    /// path for the latency trigger.
    /// </summary>
    public IReadOnlyList<string>? Add(string normalizedPath, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        if (_pending.Count == 0)
            _oldestAt = now;
        _pending.Add(normalizedPath);
        _pendingBytes += Encoding.UTF8.GetByteCount(normalizedPath) + 1; // + newline separator

        if (_pending.Count >= _maxPaths || _pendingBytes >= _maxEncodedBytes)
            return TakeBatch();
        return null;
    }

    /// <summary>
    /// Returns a batch when the oldest buffered path has waited at least the latency budget as of
    /// <paramref name="now"/> (the pipeline calls this on a timer); otherwise null. Never emits an empty batch.
    /// </summary>
    public IReadOnlyList<string>? TryFlushDueToLatency(DateTimeOffset now)
    {
        if (_pending.Count > 0 && now - _oldestAt >= _maxLatency)
            return TakeBatch();
        return null;
    }

    /// <summary>Emits any remaining buffered paths as a final batch (call once discovery has drained), or null
    /// when nothing is buffered.</summary>
    public IReadOnlyList<string>? Flush()
        => _pending.Count > 0 ? TakeBatch() : null;

    private IReadOnlyList<string> TakeBatch()
    {
        var batch = _pending.ToArray();
        _pending.Clear();
        _pendingBytes = 0;
        _oldestAt = default;
        return batch;
    }
}
