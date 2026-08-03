using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Yagu.Models;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>
/// Assembles the <see cref="IndexQueryOpenRequest"/> for an out-of-process mapped query session (plan §6
/// Stage 3, slice 2d) <b>without ever deserializing the index into the host</b> — the whole point of the
/// large-scope worker path. It combines three cheap host-side reads:
/// <list type="number">
/// <item>the scope's current base + segment format-v3 directories, from
/// <see cref="ContentIndexStore.TryGetCurrentLayerDirectories"/> (reads only the pointer slot);</item>
/// <item>the RPN encoding of the planned trigram query, which the worker self-evaluates candidates from over
/// its memory-mapped v3 postings (so no host-computed candidate ids are needed); and</item>
/// <item>each layer's <b>B0 dirty</b> content-id set, produced by reading the shared root/checkpoint journal
/// interval once and resolving its changes through every layer's manifest + <c>fileids.bin</c>
/// (<see cref="FileIdMap"/>s, no content deserialize) via <see cref="ContentIndexFreshnessEvaluator"/>.</item>
/// </list>
/// It returns <c>null</c> (the search then live-scans, exactly as today) when the query is not
/// trigram-eligible, when there is no trusted layered index, or when any layer's freshness cannot be proven
/// continuous — the same fail-closed contract as the in-process gate. It never prunes and never throws to the
/// caller for the ordinary "cannot accelerate" outcomes (those return <c>null</c> with a reason).
/// <para>
/// The same cheap scope assembly backs both the Stage-3 <b>shadow</b> scan
/// (<see cref="TryCreateShadowScan"/>, classifies but never prunes) and the Stage-4 <b>pruning</b> scan
/// (<see cref="TryCreatePruningScan"/>, actually skips proven-nonmembers and rescues the dirty subset at B1).
/// </para>
/// </summary>
internal static class ContentIndexShadowScopeBuilder
{
    /// <summary>
    /// Builds an open request for <paramref name="store"/>'s current scope against <paramref name="options"/>,
    /// or <c>null</c> when the scope cannot be shadow-classified (see the type remarks). On a <c>null</c>
    /// result <paramref name="bypassReason"/> explains why (for the verbose log). The
    /// <paramref name="journalReader"/> reads the shared USN interval once for all layers; inject a fake in
    /// tests. Reads only pointer-slot + manifest + fileids bytes — never the index content.
    /// </summary>
    public static IndexQueryOpenRequest? TryBuild(
        ContentIndexStore store,
        SearchOptions options,
        int sessionId,
        ContentIndexFreshnessEvaluator.JournalReader journalReader,
        out string bypassReason,
        int workerParallelism = 1)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(journalReader);

        // 1) The query must be trigram-eligible — otherwise there is nothing the index can accelerate (the
        //    worker would just live-scan) and there is no RPN to send. This is the same eligibility the
        //    in-process gate uses, but computed WITHOUT opening the index.
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        if (plan is not TrigramPlan.Eligible eligible)
        {
            bypassReason = plan is TrigramPlan.Ineligible ineligible ? ineligible.Reason : "query not eligible";
            return null;
        }
        if (!options.SkipBinary && !BinaryAsciiContentRepresentation.CanSafelyEvaluate(eligible.Query))
        {
            bypassReason = "binary search query is not printable-ASCII indexable";
            return null;
        }

        // 2) A trusted layered index must exist. Reads only the newest valid pointer slot (no deserialize).
        if (!store.TryGetCurrentLayerDirectories(out string? baseDir, out IReadOnlyList<string> segmentDirs) || baseDir is null)
        {
            bypassReason = "no trusted index";
            return null;
        }

        // 3) Load every layer's manifest + fileids once, then replay their shared root/checkpoint interval
        //    exactly once at B0. The materialized change list is resolved independently through each layer's
        //    local content-id namespace, preserving one dirty payload per layer in pointer order.
        if (!TryLoadLayerFreshnessInputs(baseDir, segmentDirs, out LayerFreshnessInput[] layers, out UsnCheckpoint checkpoint, out bypassReason))
            return null;
        SharedBarrierRead barrier;
        try
        {
            barrier = ReadSharedBarrier("B0", layers, checkpoint, journalReader);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            YaguLog.For("ContentIndex").LogDebug(ex, "shared B0 USN replay failed; live-scanning.");
            bypassReason = $"journal read failed: {ex.Message}";
            return null;
        }
        if (!barrier.IsContinuous)
        {
            bypassReason = $"layer not fresh: {barrier.Verdict} ({barrier.RawStatus})";
            return null;
        }

        string baseDirty = IndexQueryWorkerProtocol.EncodeContentIds(barrier.Dirties[0].Snapshot());
        string[] segmentDirties = barrier.Dirties.Skip(1)
            .Select(static dirty => IndexQueryWorkerProtocol.EncodeContentIds(dirty.Snapshot()))
            .ToArray();

        bypassReason = "";
        return new IndexQueryOpenRequest
        {
            SessionId = sessionId,
            Parallelism = Math.Clamp(workerParallelism, 1, IndexWorkerParallelism.Maximum),
            BaseDir = baseDir,
            SegmentDirs = segmentDirs.ToArray(),
            // No host-computed candidates: the worker self-evaluates the candidate set for every layer from
            // this RPN over its memory-mapped v3 postings (slice 2d-1), so the host never holds the index.
            QueryRpnBase64 = Convert.ToBase64String(TrigramQueryRpn.Encode(eligible.Query)),
            BaseDirtyBase64 = baseDirty,
            SegmentDirtiesBase64 = segmentDirties,
        };
    }

    // Stage-3 shadow pipeline tuning: bound each classify batch and the discovery backpressure so a slow
    // worker never balloons host memory (plan §5.3). Latency flushes a partial batch so classification keeps
    // pace with a bursty discovery.
    // Large-scope production measurement: 1,024-path batches made a 1.8M-path scope pay 1,759 sequential
    // JSON/Base64/IPC round trips. The worker now performs O(1) layered routing per path, so use a larger
    // bounded batch to amortize protocol overhead without delaying first results or growing host memory
    // materially (~8K paths / <=4 MiB encoded; the latency flush still emits a partial batch after 25 ms).
    private const int BatchMaxPaths = 8192;
    private const int BatchMaxEncodedBytes = 4 << 20; // 4 MiB of UTF-8 path bytes per batch
    private const int ChannelCapacity = 32768;
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Builds AND opens a Stage-3 <b>shadow</b> classification scan for <paramref name="store"/>'s current
    /// scope, or returns null (→ the search live-scans) when the scope cannot be shadow-classified (see
    /// <see cref="TryBuild"/>) or the worker cannot map it. Owns the per-search recovery spool's lifetime — it
    /// is deleted when the returned scan completes, so shadow mode never leaks temp files (shadow never
    /// replays the spool; that is a Stage-4 concern). Blocks on the worker open because the
    /// <c>SearchService</c> shadow-scan factory is synchronous and runs off the UI thread at barrier B0 (like
    /// the in-process gate). Never throws for ordinary "cannot accelerate" outcomes — those return null.
    /// </summary>
    public static IContentIndexShadowScan? TryCreateShadowScan(
        IndexWorkerClient client,
        ContentIndexStore store,
        SearchOptions options,
        int sessionId,
        ContentIndexFreshnessEvaluator.JournalReader journalReader,
        string spoolDirectory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(spoolDirectory);

        IndexQueryOpenRequest? request = TryBuild(store, options, sessionId, journalReader, out _);
        if (request is null)
            return null;

        ContentIndexRecoverySpool? spool = null;
        try
        {
            spool = ContentIndexRecoverySpool.Create(spoolDirectory);
            var batcher = new ContentIndexClassifyBatcher(BatchMaxPaths, BatchMaxEncodedBytes, LatencyBudget);
            var pipeline = new ContentIndexShadowPipeline(client, spool, batcher, sessionId, LatencyBudget, ChannelCapacity);

            // Off the UI thread at B0 — blocking on the worker open is acceptable here (the in-process gate
            // reads the journal synchronously at the same barrier). ConfigureAwait(false) throughout the
            // pipeline means no captured context, so this never deadlocks.
            bool opened = pipeline.OpenAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            if (!opened)
            {
                spool.Dispose(); // deletes the just-created (empty) spool
                return null;
            }
            return new SpoolOwningShadowScan(pipeline, spool);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { spool?.Dispose(); } catch { /* best effort */ }
            YaguLog.For("ContentIndex").LogDebug(ex, "shadow scan creation failed → live-scan (no shadow).");
            return null;
        }
    }

    /// <summary>
    /// Adapts a <see cref="ContentIndexShadowPipeline"/> to <see cref="IContentIndexShadowScan"/> while owning
    /// the per-search <see cref="ContentIndexRecoverySpool"/>: forwards offers to the pipeline and, on
    /// completion, awaits the pipeline then deletes the spool (shadow never replays it). Fail-safe — spool
    /// disposal never surfaces to the search.
    /// </summary>
    private sealed class SpoolOwningShadowScan : IContentIndexShadowScan
    {
        private readonly ContentIndexShadowPipeline _pipeline;
        private readonly ContentIndexRecoverySpool _spool;

        public SpoolOwningShadowScan(ContentIndexShadowPipeline pipeline, ContentIndexRecoverySpool spool)
        {
            _pipeline = pipeline;
            _spool = spool;
        }

        public ValueTask OfferAsync(string normalizedPath, CancellationToken cancellationToken)
            => _pipeline.OfferAsync(normalizedPath, cancellationToken);

        public async Task CompleteAsync(CancellationToken cancellationToken)
        {
            try { await ((IContentIndexShadowScan)_pipeline).CompleteAsync(cancellationToken).ConfigureAwait(false); }
            finally { try { _spool.Dispose(); } catch { /* best effort — shadow never replays the spool */ } }
        }
    }

    /// <summary>
    /// Builds AND opens a Stage-4 <b>pruning</b> scan for <paramref name="store"/>'s current scope, or returns
    /// null (→ the search live-scans) when the scope cannot be pruned (see <see cref="TryBuild"/>) or the
    /// worker cannot map it. Survivors (members / dirty / unindexed) are forwarded to
    /// <paramref name="survivorSink"/> (the caller's content-scan channel); fresh nonmembers are provisionally
    /// pruned and reconciled at B1 by re-reading each layer's <c>[build, now)</c> dirty set (which yields the
    /// identical rescue set to a <c>[B0, now)</c> read — a provisional prune is never dirty at B0). Owns the
    /// per-search recovery spool. Blocks on the worker open at barrier B0 (off the UI thread, like the
    /// in-process gate). Never throws for ordinary "cannot accelerate" outcomes — those return null.
    /// </summary>
    public static IContentIndexPruningScan? TryCreatePruningScan(
        IndexWorkerClient client,
        ContentIndexStore store,
        SearchOptions options,
        int sessionId,
        ContentIndexFreshnessEvaluator.JournalReader journalReader,
        string spoolDirectory,
        Func<string, CancellationToken, ValueTask> survivorSink,
        int workerParallelism = 1,
        Action<bool, string>? onAttempt = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(survivorSink);
        ArgumentException.ThrowIfNullOrEmpty(spoolDirectory);

        void ReportAttempt(bool active, string reason)
        {
            try { onAttempt?.Invoke(active, reason); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                YaguLog.For("ContentIndex").LogWarning(ex, "Worker-pruning index-attempt callback failed.");
            }
        }

        IndexQueryOpenRequest? request = TryBuild(
            store, options, sessionId, journalReader, out string bypassReason, workerParallelism);
        if (request is null)
        {
            YaguLog.For("ContentIndex").LogDebug(
                "Worker pruning bypass for {Root}: {Reason}.", options.Directory, bypassReason);
            ReportAttempt(false, bypassReason);
            return null;
        }

        // The layer directories the B1 reconciliation re-reads (captured from the request the builder produced).
        string baseDir = request.BaseDir;
        string[] segmentDirs = request.SegmentDirs;

        ContentIndexRecoverySpool? spool = null;
        try
        {
            spool = ContentIndexRecoverySpool.Create(spoolDirectory);
            var batcher = new ContentIndexClassifyBatcher(BatchMaxPaths, BatchMaxEncodedBytes, LatencyBudget);
            var pipeline = new ContentIndexPruningPipeline(client, spool, batcher, survivorSink, sessionId, LatencyBudget, ChannelCapacity);

            // Off the UI thread at B0 — blocking on the worker open is acceptable (the in-process gate reads
            // the journal synchronously at the same barrier). ConfigureAwait(false) throughout means no
            // captured context, so this never deadlocks.
            bool opened = pipeline.OpenAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            if (!opened)
            {
                spool.Dispose(); // deletes the just-created (empty) spool
                ReportAttempt(false, "the index worker could not open the mapped query session");
                return null;
            }
            ReportAttempt(true, "worker pruning active");
            return new ContentIndexPruningScan(pipeline, spool, () => ReadB1Dirty(baseDir, segmentDirs, journalReader));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { spool?.Dispose(); } catch { /* best effort */ }
            YaguLog.For("ContentIndex").LogDebug(ex, "pruning scan creation failed → live-scan (no pruning).");
            ReportAttempt(false, $"worker pruning could not start: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Adapts a <see cref="ContentIndexPruningPipeline"/> to <see cref="IContentIndexPruningScan"/> while
    /// owning the per-search recovery spool and computing the B1 dirty sets. On reconcile it reads one shared
    /// journal interval, maps it into each layer's dirty content-id set (<paramref name="b1DirtyReader"/>),
    /// passes them to the pipeline, and (best effort) disposes the spool — the pipeline already
    /// <c>Complete</c>d it, so the dispose is a no-op.
    /// </summary>
    private sealed class ContentIndexPruningScan : IContentIndexPruningScan
    {
        private readonly ContentIndexPruningPipeline _pipeline;
        private readonly ContentIndexRecoverySpool _spool;
        private readonly Func<(IReadOnlySet<long> BaseDirty, IReadOnlyList<IReadOnlySet<long>> SegmentDirties, bool Certain)> _b1DirtyReader;

        public ContentIndexPruningScan(
            ContentIndexPruningPipeline pipeline,
            ContentIndexRecoverySpool spool,
            Func<(IReadOnlySet<long>, IReadOnlyList<IReadOnlySet<long>>, bool)> b1DirtyReader)
        {
            _pipeline = pipeline;
            _spool = spool;
            _b1DirtyReader = b1DirtyReader;
        }

        public ValueTask OfferAsync(string scanPath, string classifyPath, CancellationToken cancellationToken)
            => _pipeline.OfferAsync(scanPath, classifyPath, cancellationToken);

        public Task CompleteOfferingAsync() => _pipeline.CompleteOfferingAsync();

        public Task CleanupAsync() => _pipeline.CleanupAsync();

        public bool WasIndexMember(string normalizedPath) => _pipeline.WasIndexMember(normalizedPath);

        public async Task<PruningScanResult> ReconcileAtB1Async(CancellationToken cancellationToken)
        {
            try
            {
                (IReadOnlySet<long> baseDirty, IReadOnlyList<IReadOnlySet<long>> segmentDirties, bool certain) = _b1DirtyReader();
                ContentIndexPruningPipeline.PruningPipelineOutcome outcome =
                    await _pipeline.ReconcileAtB1Async(baseDirty, segmentDirties, certain, cancellationToken).ConfigureAwait(false);
                return new PruningScanResult(outcome.Accelerated, outcome.RescuePaths, outcome.GrossPruned, outcome.Rescued);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Never surface a reconcile error to the search; the pipeline already replayed the spool on any
                // internal failure, but if the B1 dirty read itself throws, fall back to a total spool replay.
                YaguLog.For("ContentIndex").LogDebug(ex, "pruning scan B1 reconcile failed; total spool replay.");
                ContentIndexPruningPipeline.PruningPipelineOutcome outcome = await _pipeline
                    .ReconcileAtB1Async(EmptyDirty, Array.Empty<IReadOnlySet<long>>(), certain: false, cancellationToken)
                    .ConfigureAwait(false);
                return new PruningScanResult(false, outcome.RescuePaths, outcome.GrossPruned, outcome.Rescued);
            }
            finally
            {
                await _pipeline.CleanupAsync().ConfigureAwait(false);
                try { _spool.Dispose(); } catch { /* best effort — the pipeline already completed it */ }
            }
        }
    }

    private sealed record LayerFreshnessInput(string Directory, IndexManifest Manifest, FileIdMap FileIds);

    private sealed record SharedBarrierRead(
        RootFreshnessVerdict Verdict,
        UsnReadStatus RawStatus,
        UsnCheckpoint NextCheckpoint,
        IReadOnlyList<DirtyContentSet> Dirties)
    {
        public bool IsContinuous => Verdict == RootFreshnessVerdict.Continuous;
    }

    /// <summary>
    /// Loads every active layer's manifest + fileids without opening index content, validates that all layers
    /// describe the same root and volume, and selects the exact shared checkpoint used before this optimization:
    /// the newest segment checkpoint when segments exist, otherwise the base checkpoint.
    /// </summary>
    private static bool TryLoadLayerFreshnessInputs(
        string baseDir,
        IReadOnlyList<string> segmentDirs,
        out LayerFreshnessInput[] layers,
        out UsnCheckpoint checkpoint,
        out string bypassReason)
    {
        var directories = new string[segmentDirs.Count + 1];
        directories[0] = baseDir;
        for (int i = 0; i < segmentDirs.Count; i++)
            directories[i + 1] = segmentDirs[i];

        layers = new LayerFreshnessInput[directories.Length];
        for (int i = 0; i < directories.Length; i++)
        {
            if (ContentIndexGenerationSerializer.TryReadFreshnessInputs(directories[i]) is not { } inputs)
            {
                checkpoint = UsnCheckpoint.None;
                bypassReason = "layer freshness inputs unreadable";
                return false;
            }
            layers[i] = new LayerFreshnessInput(directories[i], inputs.Manifest, inputs.FileIds);
        }

        LayerFreshnessInput first = layers[0];
        VolumeBinding? mounted = string.IsNullOrWhiteSpace(first.Manifest.VolumeGuidPath)
            ? null
            : VolumeBindingReader.TryCapture(first.Manifest.NormalizedRootPath);
        string volumeReason = "source volume unavailable";
        if (!string.IsNullOrWhiteSpace(first.Manifest.VolumeGuidPath)
            && (mounted is not { } currentVolume
                || !VolumeBindingReader.MatchesManifest(first.Manifest, currentVolume, out volumeReason)))
        {
            checkpoint = UsnCheckpoint.None;
            bypassReason = mounted is null
                ? "indexed source volume disconnected or unavailable"
                : $"mounted volume mismatch: {volumeReason}";
            return false;
        }
        ulong knownVolumeSerial = layers
            .Select(static layer => layer.Manifest.VolumeSerialNumber)
            .FirstOrDefault(static serial => serial != 0);
        for (int i = 0; i < layers.Length; i++)
        {
            LayerFreshnessInput layer = layers[i];
            bool conflictingKnownVolume = knownVolumeSerial != 0
                && layer.Manifest.VolumeSerialNumber != 0
                && layer.Manifest.VolumeSerialNumber != knownVolumeSerial;
            bool unknownVolumeCarriesIdentities = layer.Manifest.VolumeSerialNumber == 0
                && layer.FileIds.Count != 0;
            if (!string.Equals(first.Manifest.NormalizedRootPath, layer.Manifest.NormalizedRootPath, StringComparison.OrdinalIgnoreCase)
                || conflictingKnownVolume
                || unknownVolumeCarriesIdentities
                || first.Manifest.FreshnessCheckpoint.JournalId != layer.Manifest.FreshnessCheckpoint.JournalId)
            {
                checkpoint = UsnCheckpoint.None;
                bypassReason = "active layers disagree on root, known volume, or journal identity";
                return false;
            }
        }

        checkpoint = layers[^1].Manifest.FreshnessCheckpoint;
        if (checkpoint.JournalId == 0)
        {
            bypassReason = "layer not fresh: CheckpointInvalid (Ok)";
            return false;
        }

        bypassReason = "";
        return true;
    }

    /// <summary>Reads one shared journal interval and maps it through every layer-local file-id map.</summary>
    private static SharedBarrierRead ReadSharedBarrier(
        string barrier,
        IReadOnlyList<LayerFreshnessInput> layers,
        UsnCheckpoint checkpoint,
        ContentIndexFreshnessEvaluator.JournalReader journalReader)
    {
        Stopwatch journalTimer = Stopwatch.StartNew();
        UsnReadResult journal = journalReader(layers[0].Manifest.NormalizedRootPath, checkpoint);
        journalTimer.Stop();

        Stopwatch resolutionTimer = Stopwatch.StartNew();
        var dirties = new DirtyContentSet[layers.Count];
        FreshnessRead? firstRead = null;
        int aggregateDirty = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            FreshnessRead read = ContentIndexFreshnessEvaluator.ResolveDirty(journal, layers[i].FileIds);
            firstRead ??= read;
            dirties[i] = read.Dirty;
            aggregateDirty += read.Dirty.Count;
        }
        resolutionTimer.Stop();

        // Older ReFS generations persisted FILE_ID_128 while unprivileged journal reads emit an unrelated
        // V2 file-reference number. If any post-checkpoint V2 identity maps through no active layer, we
        // cannot distinguish a new file (safe) from a changed old nonmember (unsafe). Bypass pruning until
        // incremental maintenance resolves/reindexes it and advances the checkpoint.
        bool hasLegacyExtendedLayer = layers.Any(static layer => layer.FileIds.HasExtendedIdentities);
        bool hasUnmappedV2Change = hasLegacyExtendedLayer
            && journal.Status == UsnReadStatus.Ok
            && journal.Changes.Any(change => change.Identity.High == 0
                && !layers.Any(layer => layer.FileIds.TryGetContentId(change.Identity, out _)));

        YaguLog.For("ContentIndex").LogDebug(
            "shared USN replay barrier={Barrier} root={Root} checkpoint={JournalId}/{NextUsn} layers={LayerCount} " +
            "status={Status} records={RecordCount} next={NextJournalId}/{NextCheckpointUsn} journalMs={JournalMs:F1} " +
            "resolutionMs={ResolutionMs:F1} aggregateDirty={DirtyCount} journalInvocations=1.",
            barrier, layers[0].Manifest.NormalizedRootPath, checkpoint.JournalId, checkpoint.NextUsn, layers.Count,
            journal.Status, journal.Changes.Count, journal.NextCheckpoint.JournalId, journal.NextCheckpoint.NextUsn,
            journalTimer.Elapsed.TotalMilliseconds, resolutionTimer.Elapsed.TotalMilliseconds, aggregateDirty);

        FreshnessRead representative = firstRead!;
        if (hasUnmappedV2Change)
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Legacy ReFS file-identity mismatch at barrier {Barrier} for '{Root}'; bypassing index pruning until incremental maintenance advances the checkpoint.",
                barrier, layers[0].Manifest.NormalizedRootPath);
            return new SharedBarrierRead(
                RootFreshnessVerdict.JournalDiscontinuity,
                UsnReadStatus.IdentityMismatch,
                representative.NextCheckpoint,
                dirties);
        }
        return new SharedBarrierRead(representative.Verdict, representative.RawStatus, representative.NextCheckpoint, dirties);
    }

    private static readonly IReadOnlySet<long> EmptyDirty = new HashSet<long>();

    /// <summary>
    /// Recomputes every layer's dirty content-id set at barrier B1 (the base plus one per segment, oldest →
    /// newest). Replaying from each layer's <b>build</b> checkpoint (rather than the B0 cursor) is safe and
    /// yields the identical rescue set: a provisionally-pruned path is by definition not dirty at B0 (else it
    /// would have classified <c>DirtyByUsn</c> and been scanned), so intersecting the prunes with <c>[build,
    /// now)</c> equals intersecting with <c>[B0, now)</c>. <c>Certain</c> is true only when every layer's replay
    /// is continuous; otherwise the pipeline replays its whole spool (rescue everything).
    /// </summary>
    private static (IReadOnlySet<long> BaseDirty, IReadOnlyList<IReadOnlySet<long>> SegmentDirties, bool Certain) ReadB1Dirty(
        string baseDir, IReadOnlyList<string> segmentDirs, ContentIndexFreshnessEvaluator.JournalReader journalReader)
    {
        if (!TryLoadLayerFreshnessInputs(baseDir, segmentDirs, out LayerFreshnessInput[] layers, out UsnCheckpoint checkpoint, out _))
            return (EmptyDirty, Array.Empty<IReadOnlySet<long>>(), false);

        SharedBarrierRead barrier = ReadSharedBarrier("B1", layers, checkpoint, journalReader);
        var segmentDirties = new IReadOnlySet<long>[segmentDirs.Count];
        for (int i = 0; i < segmentDirties.Length; i++)
            segmentDirties[i] = barrier.Dirties[i + 1].Snapshot();
        return (barrier.Dirties[0].Snapshot(), segmentDirties, barrier.IsContinuous);
    }
}
