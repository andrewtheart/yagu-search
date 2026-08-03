using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// The Stage-4 <c>SearchService</c> seam for the out-of-process <b>pruning</b> pipeline (plan §5.3): discovery
/// offers each content-scan candidate; the pipeline forwards survivors to the caller's content-scan sink and
/// prunes fresh nonmembers. <see cref="CompleteOfferingAsync"/> MUST be awaited before the caller completes
/// its content-scan channel (so every survivor is enqueued first); <see cref="ReconcileAtB1Async"/> is called
/// after the scan drains and returns the paths to rescue-scan plus net-pruning accounting. A null seam (or a
/// not-accelerated result) means the search live-scanned — identical results.
/// </summary>
public interface IContentIndexPruningScan
{
    /// <summary>Offers a discovered candidate. <paramref name="scanPath"/> (the original OS path) is forwarded
    /// to the content-scan sink for survivors; <paramref name="classifyPath"/> (the normalized path) is what
    /// the worker classifies. Fresh nonmembers are provisionally pruned. Applies backpressure to discovery.</summary>
    ValueTask OfferAsync(string scanPath, string classifyPath, CancellationToken cancellationToken);

    /// <summary>Drains the classifier so every survivor is forwarded. Await this BEFORE completing the
    /// content-scan channel.</summary>
    Task CompleteOfferingAsync();

    /// <summary>Barrier B1: reconciles the provisional prunes against <c>[B0, B1)</c> changes and returns the
    /// paths to rescue-scan (call after the content-scan channel drains). Never throws to the caller.</summary>
    Task<PruningScanResult> ReconcileAtB1Async(CancellationToken cancellationToken);

    /// <summary>Unconditionally releases the worker session with a fresh bounded cleanup token. Idempotent
    /// and never throws; safe as a final backstop after success, cancellation, or an earlier failure.</summary>
    Task CleanupAsync();

    /// <summary>Whether <paramref name="normalizedPath"/> was classified an index <b>member</b> (a fresh
    /// posting candidate) during this search — the signal for the results-list "indexed" provenance badge.
    /// Thread-safe; a bounded best-effort set, so a false result never means "not a member" beyond the cap.</summary>
    bool WasIndexMember(string normalizedPath);
}

/// <summary>The outcome of a pruning scan's B1 reconciliation (plan §5.5): the paths that must be live-scanned
/// after all, plus gross/net pruning counts for the search's index-acceleration status.</summary>
public readonly record struct PruningScanResult(
    bool Accelerated,
    IReadOnlyList<string> RescuePaths,
    long GrossPruned,
    long Rescued)
{
    /// <summary>Net files skipped = pruned minus rescued (only positive when the index actually helped).</summary>
    public long NetPruned => Math.Max(0, GrossPruned - Rescued);

    /// <summary>The result for a scope that could not be pruned (the search live-scanned).</summary>
    public static PruningScanResult NotAccelerated { get; } = new(false, Array.Empty<string>(), 0, 0);
}

/// <summary>
/// The Stage-4 <b>pruning</b> pipeline (plan §5.3/§5.5) — the pruning analogue of
/// <see cref="ContentIndexShadowPipeline"/>. Unlike shadow mode (which classifies but never affects the
/// search), this pipeline actually <b>skips</b> the files a required-superset trigram query provably cannot
/// match: <c>discovery → bounded candidate channel → async batch classifier (mapped worker) → survivor sink /
/// recovery spool</c>. For each classified path it either forwards it to the caller's <b>survivor sink</b>
/// (the content-scan channel — for a member / dirty / unindexed / no-identity path) or records it in the
/// disk-backed <see cref="ContentIndexRecoverySpool"/> as a provisional prune (a fresh posting nonmember) and
/// does <b>not</b> forward it.
/// <para>
/// Correctness is guarded end-to-end:
/// <list type="bullet">
/// <item>Only a fresh posting nonmember is ever pruned (the worker's <c>RouteForPruning</c> guarantee).</item>
/// <item><see cref="ReconcileAtB1Async"/> replays the USN journal over <c>[B0, B1)</c> (in the worker) and
/// returns only the provisional paths whose content changed after B0 so they are scanned after all; the
/// caller feeds them into its end-of-search rescue scan. A not-certain reconciliation rescues every prune.</item>
/// <item><b>Any</b> worker fault (crash / timeout / malformed or stale reply / a failed reconcile) makes the
/// pipeline fail safe: the batch that faulted and every later offered path is forwarded to the survivor sink
/// (scanned), and at B1 the <b>entire recovery spool is replayed</b> — so no provisionally-pruned path is ever
/// lost. A pruned-then-failed search yields the same result multiset as a live scan.</item>
/// </list>
/// The pipeline does not own the worker client or the spool (the caller manages their lifetime); it
/// <see cref="ContentIndexRecoverySpool.Complete"/>s the spool once B1 has been reconciled.
/// </para>
/// </summary>
internal sealed class ContentIndexPruningPipeline
{
    private const string LogSource = "ContentIndex";

    private readonly IndexWorkerClient _client;
    private readonly ContentIndexRecoverySpool _spool;
    private readonly ContentIndexClassifyBatcher _batcher;
    private readonly Func<string, CancellationToken, ValueTask> _survivorSink;
    private readonly Func<int, CancellationToken, Task> _cancelSession;
    private readonly TimeSpan _latencyBudget;
    private readonly int _sessionId;
    // Each item pairs the SCAN path (the original OS path forwarded to the survivor sink / result display)
    // with the CLASSIFY path (the normalized path the worker looks up). They differ (e.g. '\' vs '/'), so the
    // pipeline must keep them aligned through the batcher.
    private readonly Channel<(string Scan, string Classify)> _channel;

    private Task? _pump;
    private bool _started;
    private bool _offeringComplete;
    private bool _reconciled;
    private bool _workerFailed;
    private string? _bypassReason;
    private Task? _cleanupTask;
    private readonly object _cleanupLock = new();

    // Touched only by the single pump task, then read after the pump is awaited in CompleteOfferingAsync.
    private long _offered;
    private long _grossPruned;
    private long _batches;
    private long _members;
    private long _dirty;
    private long _unindexed;

    // Normalized paths classified as index MEMBERS (the results-list "indexed" provenance signal). Written by
    // the pump, read from the UI thread at result-group time → a concurrent set. Bounded: for a selective
    // query (the worker path's target) members are few; the cap defends the non-selective worst case.
    private const int MaxTrackedMembers = 200_000;
    private readonly ConcurrentDictionary<string, byte> _memberPaths = new(StringComparer.Ordinal);

    // The SCAN paths accumulated for the batcher's current (not-yet-emitted) batch, kept 1:1 and in the same
    // order as the CLASSIFY paths inside the batcher, so a verdict at index i maps to the right survivor path.
    private List<string> _batchScanPaths = new();

    // The reconciled rescue result, cached so a repeated ReconcileAtB1Async call is idempotent.
    private IReadOnlyList<string> _lastRescued = Array.Empty<string>();
    private bool _lastCertain;

    /// <summary>The successful worker-open diagnostic snapshot, when the worker supplied one.</summary>
    internal IndexQueryOpenDiagnostics? OpenDiagnostics { get; private set; }

    public ContentIndexPruningPipeline(
        IndexWorkerClient client,
        ContentIndexRecoverySpool spool,
        ContentIndexClassifyBatcher batcher,
        Func<string, CancellationToken, ValueTask> survivorSink,
        int sessionId,
        TimeSpan latencyBudget,
        int channelCapacity,
        Func<int, CancellationToken, Task>? cancelSession = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _batcher = batcher ?? throw new ArgumentNullException(nameof(batcher));
        _survivorSink = survivorSink ?? throw new ArgumentNullException(nameof(survivorSink));
        _cancelSession = cancelSession ?? client.CancelSessionAsync;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(latencyBudget, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _sessionId = sessionId;
        _latencyBudget = latencyBudget;
        _channel = Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The outcome + counts of a pruning pipeline run (available after <see cref="ReconcileAtB1Async"/>).</summary>
    public sealed record PruningPipelineOutcome(
        bool Accelerated,
        long Offered,
        long GrossPruned,
        IReadOnlyList<string> RescuePaths,
        long Batches,
        long Members,
        long Dirty,
        long Unindexed,
        bool PruningCertain,
        string? BypassReason)
    {
        /// <summary>The number of provisionally-pruned paths that must be live-scanned after all.</summary>
        public long Rescued => RescuePaths.Count;

        /// <summary>Net files skipped = pruned minus rescued (only positive when the index actually helped).</summary>
        public long NetPruned => Math.Max(0, GrossPruned - Rescued);
    }

    /// <summary>
    /// Opens the worker pruning session for <paramref name="scope"/> (forcing
    /// <see cref="IndexQueryOpenRequest.PruningEnabled"/>). Returns false — a no-op pipeline the caller
    /// bypasses (live-scans everything) — when the scope is not mapped-queryable or the worker is unavailable.
    /// </summary>
    public async Task<bool> OpenAsync(IndexQueryOpenRequest scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.PruningEnabled = true;
        IndexQueryOpenResult? open = await _client.OpenQueryScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        if (open is null || !open.Accelerable)
        {
            _bypassReason = open?.BypassReason ?? "worker unavailable";
            return false;
        }

        OpenDiagnostics = open.Diagnostics;
        if (OpenDiagnostics is { } diagnostics)
        {
            YaguLog.For(LogSource).LogInformation(
                "Index query open diagnostics: base='{BaseDir}' layers={LayerCount} pathRecords={PathRecords} " +
                "tombstones={Tombstones} routeRecords={RouteRecords} distinctRoutes={DistinctRoutes} " +
                "superseded={SupersededRoutes} amplification={Amplification:F3} mapMs={MapMs:F1} " +
                "candidateMs={CandidateMs:F1} routingMs={RoutingMs:F1} workerOpenMs={WorkerOpenMs:F1} " +
                "hostRoundTripMs={HostRoundTripMs:F1} candidateMode={CandidateMode}.",
                scope.BaseDir,
                diagnostics.LayerCount,
                diagnostics.PathRecordCount,
                diagnostics.TombstoneRecordCount,
                diagnostics.RouteRecordCount,
                diagnostics.DistinctRouteHashCount,
                diagnostics.SupersededRouteRecordCount,
                diagnostics.RouteRecordAmplification,
                diagnostics.MapOpenMs,
                diagnostics.CandidateEvaluationMs,
                diagnostics.RoutingIndexMs,
                diagnostics.WorkerOpenMs,
                diagnostics.HostRoundTripMs,
                diagnostics.CandidatesEvaluatedInWorker ? "worker" : "supplied");
        }

        _started = true;
        _pump = Task.Run(() => PumpAsync(cancellationToken), CancellationToken.None);
        return true;
    }

    /// <summary>Offers a discovered content-scan candidate to the pipeline. <paramref name="scanPath"/> is the
    /// original OS path forwarded to the survivor sink (so result rows show the real path);
    /// <paramref name="classifyPath"/> is the normalized path the worker looks up. A no-op when the pipeline
    /// never opened; otherwise a bounded write that applies backpressure to discovery when the classifier +
    /// survivor sink fall behind.</summary>
    public async ValueTask OfferAsync(string scanPath, string classifyPath, CancellationToken cancellationToken)
    {
        if (!_started)
            return;
        await _channel.Writer.WriteAsync((scanPath, classifyPath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signals that discovery has drained and waits for the pump to classify every remaining batch and forward
    /// all survivors to the sink. MUST be awaited <b>before</b> the caller completes the content-scan channel,
    /// so every survivor is enqueued before the scanners finish. Never throws — a pump fault degrades the
    /// pipeline to "worker failed" (its offered paths were already forwarded to the sink), leaving the B1
    /// spool replay to rescue anything pruned before the fault.
    /// </summary>
    public async Task CompleteOfferingAsync()
    {
        if (!_started || _offeringComplete)
            return;
        _offeringComplete = true;
        _channel.Writer.TryComplete();
        try
        {
            if (_pump is not null)
                await _pump.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For(LogSource).LogDebug(ex, "pruning pipeline pump faulted; the recovery spool will be replayed at B1.");
            _workerFailed = true;
        }
    }

    /// <summary>
    /// Barrier B1 (plan §5.5): given each layer's <c>[B0, B1)</c> dirty content-id set (or
    /// <paramref name="certain"/> = false when the host's journal replay was discontinuous), returns the
    /// provisional paths that must now be live-scanned after all, and completes (deletes) the recovery spool.
    /// On any worker failure — or a not-certain / failed reconcile — the <b>entire spool is replayed</b>, so
    /// no pruned path is lost. Call AFTER the content-scan channel has drained (the caller scans the returned
    /// paths via its end-of-search rescue path). Idempotent; never throws to the caller.
    /// </summary>
    public async Task<PruningPipelineOutcome> ReconcileAtB1Async(
        IReadOnlySet<long> baseDirtyAtB1,
        IReadOnlyList<IReadOnlySet<long>> segmentDirtiesAtB1,
        bool certain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseDirtyAtB1);
        ArgumentNullException.ThrowIfNull(segmentDirtiesAtB1);

        if (!_started)
            return new PruningPipelineOutcome(false, _offered, 0, Array.Empty<string>(), 0,
                0, 0, 0, false, _bypassReason ?? "not opened");

        // Ensure offering has drained (in case the caller didn't call CompleteOfferingAsync explicitly).
        await CompleteOfferingAsync().ConfigureAwait(false);

        if (_reconciled)
            return BuildOutcome(_lastRescued, _lastCertain);
        _reconciled = true;

        try
        {
            IReadOnlyList<string> rescue;
            bool pruningCertain;
            if (_workerFailed || !certain)
            {
                // A worker fault, OR a discontinuous/uncertain journal replay (certain == false): live-scan EVERY
                // provisionally-pruned path by replaying the host spool locally — never trust a partial verdict and
                // never depend on the worker for the fail-safe case (the spool is the authoritative backstop).
                rescue = ReplaySpool();
                pruningCertain = false;
            }
            else
            {
                IndexWorkerReconcileResult result;
                try
                {
                    result = await _client.ReconcileB1Async(_sessionId, baseDirtyAtB1, segmentDirtiesAtB1, certain: true, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    YaguLog.For(LogSource).LogDebug(ex, "pruning pipeline reconcile faulted; replaying the recovery spool.");
                    result = IndexWorkerReconcileResult.Fail();
                }

                if (result.Success)
                {
                    rescue = result.RescuePaths;
                    pruningCertain = result.PruningCertain;
                }
                else
                {
                    // A failed reconcile → the worker's provisional set is unavailable; replay the whole spool.
                    rescue = ReplaySpool();
                    pruningCertain = false;
                }
            }

            _spool.Complete();

            _lastRescued = rescue;
            _lastCertain = pruningCertain;
            PruningPipelineOutcome outcome = BuildOutcome(rescue, pruningCertain);
            YaguLog.For(LogSource).LogInformation(
                "pruning pipeline: offered={Offered} grossPruned={GrossPruned} rescued={Rescued} net={Net} members={Members} dirty={Dirty} unindexed={Unindexed} batches={Batches} certain={Certain}.",
                outcome.Offered, outcome.GrossPruned, outcome.Rescued, outcome.NetPruned,
                outcome.Members, outcome.Dirty, outcome.Unindexed, outcome.Batches, outcome.PruningCertain);
            return outcome;
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Worker-acknowledged session teardown. Never reuse the search token here: it is commonly
    /// cancelled precisely when cleanup is most important, and passing it would prevent the request from
    /// reaching the worker. Concurrent/repeated callers share one cleanup task.</summary>
    public Task CleanupAsync()
    {
        if (!_started)
            return Task.CompletedTask;
        lock (_cleanupLock)
            return _cleanupTask ??= CleanupCoreAsync();
    }

    private async Task CleanupCoreAsync()
    {
        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            YaguLog.For(LogSource).LogDebug("canceling worker pruning session {SessionId} for cleanup.", _sessionId);
            await _cancelSession(_sessionId, cleanupCts.Token).ConfigureAwait(false);
            YaguLog.For(LogSource).LogInformation("worker pruning session {SessionId} cleanup acknowledged.", _sessionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For(LogSource).LogWarning(ex,
                "worker pruning session {SessionId} cleanup failed; mappings will be reclaimed when the worker exits.", _sessionId);
        }
    }

    private PruningPipelineOutcome BuildOutcome(IReadOnlyList<string> rescue, bool pruningCertain)
        => new(!_workerFailed && pruningCertain, _offered, _grossPruned, rescue, _batches,
            _members, _dirty, _unindexed, pruningCertain, _workerFailed ? "worker failed mid-pipeline" : null);

    // Materializes the whole spool (the failure backstop): every provisionally-pruned path must be scanned.
    private IReadOnlyList<string> ReplaySpool()
    {
        var all = new List<string>();
        foreach (string path in _spool.ReplayAll())
            all.Add(path);
        return all;
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        ChannelReader<(string Scan, string Classify)> reader = _channel.Reader;
        while (!cancellationToken.IsCancellationRequested)
        {
            Task<bool> wait = reader.WaitToReadAsync(cancellationToken).AsTask();
            Task completed = await Task.WhenAny(wait, Task.Delay(_latencyBudget, cancellationToken)).ConfigureAwait(false);

            if (ReferenceEquals(completed, wait))
            {
                bool more;
                try { more = await wait.ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                if (!more)
                    break; // channel completed + drained

                while (reader.TryRead(out (string Scan, string Classify) item))
                {
                    _offered++;
                    _batchScanPaths.Add(item.Scan);
                    IReadOnlyList<string>? batch = _batcher.Add(item.Classify, DateTimeOffset.UtcNow);
                    if (batch is not null)
                        await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                IReadOnlyList<string>? batch = _batcher.TryFlushDueToLatency(DateTimeOffset.UtcNow);
                if (batch is not null)
                    await FlushBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<string>? tail = _batcher.Flush();
        if (tail is not null)
            await FlushBatchAsync(tail, cancellationToken).ConfigureAwait(false);
    }

    // Detaches the SCAN paths accumulated in lockstep with this batch's CLASSIFY paths, then classifies. The
    // batcher emitted exactly the paths accumulated since the last flush, so _batchScanPaths is 1:1 with them.
    private async Task FlushBatchAsync(IReadOnlyList<string> classifyBatch, CancellationToken cancellationToken)
    {
        List<string> scanBatch = _batchScanPaths;
        _batchScanPaths = new List<string>();
        await ClassifyBatchAsync(classifyBatch, scanBatch, cancellationToken).ConfigureAwait(false);
    }

    private async Task ClassifyBatchAsync(IReadOnlyList<string> classifyBatch, IReadOnlyList<string> scanBatch, CancellationToken cancellationToken)
    {
        _batches++;

        // Once the worker has failed, we can no longer prune safely — forward every path to be scanned.
        if (_workerFailed)
        {
            await ForwardAllAsync(scanBatch, cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[]? verdicts = await _client.ClassifyPathsAsync(_sessionId, classifyBatch, cancellationToken, batchSeq: _batches).ConfigureAwait(false);
        if (verdicts is null)
        {
            // This batch's classification failed → scan every path in it (never prune on a failed verdict),
            // and switch to fail-safe for all later batches. The recovery spool (earlier prunes) replays at B1.
            _workerFailed = true;
            await ForwardAllAsync(scanBatch, cancellationToken).ConfigureAwait(false);
            return;
        }

        int count = Math.Min(Math.Min(classifyBatch.Count, scanBatch.Count), verdicts.Length);
        for (int i = 0; i < count; i++)
        {
            if (verdicts[i] == IndexQueryWorkerProtocol.Verdicts.Nonmember)
            {
                // A fresh posting nonmember: provisionally prune it. Record the NORMALIZED (classify) path so
                // the spool replay matches the worker's rescue set + the in-process B1 rescue's path form.
                _spool.Append(classifyBatch[i]);
                _grossPruned++;
            }
            else
            {
                if (verdicts[i] == IndexQueryWorkerProtocol.Verdicts.Member) _members++;
                else if (verdicts[i] == IndexQueryWorkerProtocol.Verdicts.DirtyByUsn) _dirty++;
                else _unindexed++;
                // A survivor: forward the ORIGINAL (scan) path so result rows show the real OS path. Record an
                // index MEMBER (bounded) so the results list can badge it as index-accelerated (plan §5.5).
                if (verdicts[i] == IndexQueryWorkerProtocol.Verdicts.Member && _memberPaths.Count < MaxTrackedMembers)
                    _memberPaths.TryAdd(classifyBatch[i], 0);
                await _survivorSink(scanBatch[i], cancellationToken).ConfigureAwait(false);
            }
        }

        // Defensive: a short verdict array must never drop paths — scan the unclassified remainder.
        for (int i = count; i < scanBatch.Count; i++)
        {
            await _survivorSink(scanBatch[i], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ForwardAllAsync(IReadOnlyList<string> scanBatch, CancellationToken cancellationToken)
    {
        foreach (string path in scanBatch)
        {
            await _survivorSink(path, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Whether <paramref name="normalizedPath"/> was classified an index member during this search
    /// (the results-list "indexed" provenance signal). Thread-safe; bounded best-effort.</summary>
    public bool WasIndexMember(string normalizedPath)
        => normalizedPath is not null && _memberPaths.ContainsKey(normalizedPath);
}
