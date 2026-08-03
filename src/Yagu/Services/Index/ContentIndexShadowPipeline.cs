using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The narrow seam <c>SearchService</c> wires to (plan §5.3, Stage 3): offer each discovered content-scan
/// candidate path, then complete once discovery drains. Kept to two methods so the hot-path wiring is a
/// thin, fail-safe pair of calls and can be exercised with a fake (the real implementation is
/// <see cref="ContentIndexShadowPipeline"/>). In Stage 3 the implementation is <b>shadow</b> — it never
/// prunes, so the caller still content-scans every offered path and the result set is unchanged.
/// </summary>
public interface IContentIndexShadowScan
{
    /// <summary>Offers a discovered candidate path to the shadow classifier (bounded — may apply backpressure).</summary>
    ValueTask OfferAsync(string normalizedPath, CancellationToken cancellationToken);

    /// <summary>Signals discovery has drained; waits for the classifier to finish and releases the session.</summary>
    Task CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The Stage-3 async classification pipeline (plan §5.3) as a self-contained, driveable stage:
/// <c>discovery → bounded candidate channel → async batch classifier → worker → recovery spool</c>. The
/// caller offers each discovered candidate path (<see cref="OfferAsync"/>) and, once discovery drains, calls
/// <see cref="CompleteAsync"/>; a single background pump drains the bounded channel into batches (via
/// <see cref="ContentIndexClassifyBatcher"/> — path-count / encoded-byte / latency triggers), classifies each
/// batch in the out-of-process mapped query worker, and appends every <b>would-prune</b> path (a fresh
/// posting nonmember) to the <see cref="ContentIndexRecoverySpool"/>.
/// <para>
/// In Stage 3 this runs in <b>shadow</b>: it never actually prunes (the caller keeps content-scanning every
/// path), so the result set is identical to a live scan — it exists to prove the pipeline plumbing (bounded
/// backpressure, batching, worker round-trips, spool recording) and to validate the worker's verdicts against
/// the in-process oracle before Stage 4 flips pruning on. A worker failure mid-pipeline simply stops
/// classifying (the pump keeps draining the channel so discovery never deadlocks); nothing is lost because
/// nothing is pruned. The pipeline does not own the worker client or the spool — the caller manages their
/// lifetime.
/// </para>
/// </summary>
internal sealed class ContentIndexShadowPipeline : IContentIndexShadowScan
{
    private const string LogSource = "ContentIndex";

    private readonly IndexWorkerClient _client;
    private readonly ContentIndexRecoverySpool _spool;
    private readonly ContentIndexClassifyBatcher _batcher;
    private readonly TimeSpan _latencyBudget;
    private readonly int _sessionId;
    private readonly Func<string, byte>? _oracleVerdict;
    private readonly Channel<string> _channel;

    private Task? _pump;
    private bool _started;
    private bool _workerFailed;
    private string? _bypassReason;

    // Touched only by the single pump task; read after the pump is awaited in CompleteAsync.
    private long _offered;
    private long _classified;
    private long _wouldPrune;
    private long _mismatches;
    private long _batches;

    public ContentIndexShadowPipeline(
        IndexWorkerClient client,
        ContentIndexRecoverySpool spool,
        ContentIndexClassifyBatcher batcher,
        int sessionId,
        TimeSpan latencyBudget,
        int channelCapacity,
        Func<string, byte>? oracleVerdict = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(latencyBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _sessionId = sessionId;
        _latencyBudget = latencyBudget;
        _oracleVerdict = oracleVerdict;
        // Bounded so a slow worker applies backpressure to discovery (plan §5.3), never unbounded memory.
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The outcome + counts of one shadow pipeline run.</summary>
    public sealed record ShadowPipelineMetrics(
        bool Accelerable,
        long Offered,
        long Classified,
        long WouldPrune,
        long Mismatches,
        long Batches,
        string? BypassReason);

    /// <summary>
    /// Opens the worker query session for the scope and, when accelerable, starts the background pump. When
    /// the scope is not mapped-queryable (or the worker is unavailable) returns false and the pipeline is a
    /// no-op — <see cref="OfferAsync"/> drops paths and the caller live-scans everything.
    /// </summary>
    public async Task<bool> OpenAsync(IndexQueryOpenRequest scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        IndexQueryOpenResult? open = await _client.OpenQueryScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        if (open is null || !open.Accelerable)
        {
            _bypassReason = open?.BypassReason ?? "worker unavailable";
            return false;
        }

        _started = true;
        _pump = Task.Run(() => PumpAsync(cancellationToken), CancellationToken.None);
        return true;
    }

    /// <summary>Offers a discovered candidate path to the pipeline. A no-op when the pipeline never opened;
    /// otherwise a bounded write that applies backpressure when the classifier falls behind.</summary>
    public async ValueTask OfferAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        await _channel.Writer.WriteAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals that discovery has drained, waits for the pump to classify every remaining batch, closes the
    /// worker session, and returns the run metrics. Does not touch the spool's lifetime (the caller
    /// completes/disposes it). Never throws to the caller — a pump fault degrades to a non-accelerable result.
    /// </summary>
    public async Task<ShadowPipelineMetrics> CompleteAsync(CancellationToken cancellationToken)
    {
        if (!_started)
            return new ShadowPipelineMetrics(false, _offered, 0, 0, 0, 0, _bypassReason ?? "not opened");

        _channel.Writer.TryComplete();
        try
        {
            if (_pump is not null)
                await _pump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For(LogSource).LogDebug(ex, "shadow pipeline pump faulted.");
            _workerFailed = true;
        }

        try { await _client.CloseQueryScopeAsync(_sessionId, cancellationToken).ConfigureAwait(false); }
        catch { /* best effort */ }

        var metrics = new ShadowPipelineMetrics(
            !_workerFailed, _offered, _classified, _wouldPrune, _mismatches, _batches,
            _workerFailed ? "worker failed mid-pipeline" : null);
        YaguLog.For(LogSource).LogInformation(
            "shadow pipeline: offered={Offered} classified={Classified} wouldPrune={WouldPrune} mismatches={Mismatches} batches={Batches} accelerable={Accelerable}.",
            _offered, _classified, _wouldPrune, _mismatches, _batches, metrics.Accelerable);
        return metrics;
    }

    /// <summary>Explicit <see cref="IContentIndexShadowScan"/> completion (discards the metrics record; the
    /// pipeline logs them). Fail-safe by contract — never throws to the search pipeline.</summary>
    async Task IContentIndexShadowScan.CompleteAsync(CancellationToken cancellationToken)
    {
        try { _ = await CompleteAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For(LogSource).LogDebug(ex, "shadow pipeline completion faulted (ignored — shadow never affects results).");
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        ChannelReader<string> reader = _channel.Reader;
        while (!cancellationToken.IsCancellationRequested)
        {
            // Wake either when a path arrives or when the latency budget elapses (to flush a partial batch).
            Task<bool> wait = reader.WaitToReadAsync(cancellationToken).AsTask();
            Task completed = await Task.WhenAny(wait, Task.Delay(_latencyBudget, cancellationToken)).ConfigureAwait(false);

            if (ReferenceEquals(completed, wait))
            {
                bool more;
                try { more = await wait.ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (!more)
                    break; // channel completed + drained

                while (reader.TryRead(out string? path))
                {
                    _offered++;
                    IReadOnlyList<string>? batch = _batcher.Add(path!, DateTimeOffset.UtcNow);
                    if (batch is not null)
                        await ClassifyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                IReadOnlyList<string>? batch = _batcher.TryFlushDueToLatency(DateTimeOffset.UtcNow);
                if (batch is not null)
                    await ClassifyBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }

        // Final flush of whatever remains once discovery has completed.
        IReadOnlyList<string>? tail = _batcher.Flush();
        if (tail is not null)
            await ClassifyBatchAsync(tail, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClassifyBatchAsync(IReadOnlyList<string> batch, CancellationToken cancellationToken)
    {
        _batches++;
        if (_workerFailed)
            return; // keep draining the channel so discovery never deadlocks, but stop classifying

        // The per-session batch sequence lets the transport drop a stale/misrouted reply (plan §5.2 framing).
        byte[]? verdicts = await _client.ClassifyPathsAsync(_sessionId, batch, cancellationToken, batchSeq: _batches).ConfigureAwait(false);
        if (verdicts is null)
        {
            _workerFailed = true;
            return;
        }

        int count = Math.Min(batch.Count, verdicts.Length);
        for (int i = 0; i < count; i++)
        {
            _classified++;
            // Record what we WOULD prune (a fresh posting nonmember) to the recovery spool — the Stage-4
            // backstop. In shadow we do not act on it; the caller still content-scans every path.
            if (verdicts[i] == IndexQueryWorkerProtocol.Verdicts.Nonmember)
            {
                _spool.Append(batch[i]);
                _wouldPrune++;
            }
            if (_oracleVerdict is not null && verdicts[i] != _oracleVerdict(batch[i]))
            {
                _mismatches++;
                YaguLog.For(LogSource).LogWarning(
                    "shadow pipeline MISMATCH for '{Path}': worker={WorkerVerdict} oracle={OracleVerdict}.",
                    batch[i], verdicts[i], _oracleVerdict(batch[i]));
            }
        }
    }
}
