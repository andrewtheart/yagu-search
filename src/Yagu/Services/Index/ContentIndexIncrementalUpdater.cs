using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;

namespace Yagu.Services.Index;

/// <summary>The outcome of an incremental update pass (plan §11.4).</summary>
public enum IncrementalUpdateOutcome
{
    /// <summary>No changes were supplied — nothing to do.</summary>
    NoChanges,

    /// <summary>A delta segment was appended over the existing base.</summary>
    SegmentAppended,

    /// <summary>A segment was appended and then the layered index was compacted into a fresh base.</summary>
    Compacted,

    /// <summary>No trusted base exists to append to — the caller should do a full rebuild instead.</summary>
    NeedsFullRebuild,

    /// <summary>A pre-fix ReFS layer uses incompatible extended identities. Automatic maintenance should
    /// perform one compatibility rebuild; an explicitly incremental-only action may decline it.</summary>
    NeedsCompatibilityRebuild,

    /// <summary>The index is at its configured storage budget and could not be reclaimed within its
    /// size-management mode, so no segment was appended. The existing index stays valid and queryable; the
    /// files it no longer covers are simply live-scanned until it is rebuilt.</summary>
    SizeBudgetReached,

    /// <summary>Accumulated update history has passed the clean-up thresholds, nothing can reclaim it
    /// automatically, and the user opted to stop appending rather than let the index keep growing. The
    /// existing index and its checkpoint are untouched; affected files are live-scanned.</summary>
    ReclamationBlocked,
}

/// <summary>
/// A changed file's content classification + durable identity, produced by reading it <b>once</b> through
/// the optimized build content reader (plan §5.4): the same reason/trigrams the full build would compute,
/// and the identity taken from the very same handle whose bytes were read — no second open, and binary/BOM
/// files are decided after at most the 8 KB prefix.
/// </summary>
public readonly record struct IncrementalFileRead(
    IndexContentClassification Classification,
    FileIdentity? Identity,
    long BytesRead = 0);

/// <summary>A single changed (created or modified) file the incremental updater should re-index, already
/// classified by the content reader (the classification + captured identity, not the raw bytes).</summary>
public readonly record struct IncrementalChange(string Path, IndexContentClassification Classification, FileIdentity? Identity);

/// <summary>
/// Orchestrates one Phase 3 incremental-update pass (plan §11.4): it turns a resolved set of created/
/// modified files and deleted paths into an immutable delta segment, appends it to the current base via the
/// <see cref="ContentIndexStore"/>, and — when the segment/size bounds are exceeded — streams the whole layered
/// index into a fresh base through a bounded-memory external merge.
/// <para>
/// It is deliberately pure w.r.t. change discovery: the caller supplies the already-resolved changes (from a
/// continuous USN replay), so this class is fully unit-testable without the journal. It never throws to the
/// caller — a missing base returns <see cref="IncrementalUpdateOutcome.NeedsFullRebuild"/> so the scheduler
/// can fall back to a full rebuild, preserving the "index never suppresses a live scan" invariant.
/// </para>
/// </summary>
public sealed partial class ContentIndexIncrementalUpdater
{
    // A small-run merge is deliberately independent of total scope size: it merges a bounded contiguous run
    // without opening the base or unrelated segments, so a 30+ GiB layered index can shed layers without
    // paying for a full-index streaming pass. The bounds
    // come from the per-index EffectiveIndexSizePolicy, because fixed bounds that no real whole-drive index
    // could satisfy left those indexes with no reclamation path at all.
    internal const int SmallSegmentMaximumRun = EffectiveIndexSizePolicy.MaximumCoalesceRun;

    private readonly ContentIndexStore _store;
    private readonly IndexIngestionPolicy _policy;
    private readonly Func<string, FileIdentity?>? _identityProvider;

    public ContentIndexIncrementalUpdater(
        ContentIndexStore store,
        IndexIngestionPolicy policy,
        Func<string, FileIdentity?>? identityProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _identityProvider = identityProvider;
    }

    /// <summary>
    /// Builds the read-and-classify delegate an incremental refresh feeds to
    /// <see cref="ContentIndexUsnChangeResolver.Resolve"/> (plan §5.4/Stage 6): each changed path is read
    /// once through the optimized <see cref="IIndexFileContentReader"/> — one handle for its bytes and its
    /// durable identity, with binary/BOM files rejected after the 8 KB prefix and text streamed. Returns
    /// null for an unreadable path; the resolver then tombstones its current/prior aliases so the advanced
    /// journal checkpoint can never make stale older content look fresh. The physical file is live-scanned.
    /// </summary>
    public static Func<string, IncrementalFileRead?> CreateFileReadClassifier(IndexIngestionPolicy policy)
    {
        Func<string, CancellationToken, IncrementalFileRead?> cancellable =
            CreateCancellableFileReadClassifier(policy);
        return path => cancellable(path, CancellationToken.None);
    }

    /// <summary>Cancellation-aware form used by the bounded maintenance I/O lane. The token reaches
    /// every streamed chunk read; the lane additionally uses <c>CancelSynchronousIo</c> when the initial
    /// synchronous file open itself exceeds its deadline.</summary>
    internal static Func<string, CancellationToken, IncrementalFileRead?> CreateCancellableFileReadClassifier(
        IndexIngestionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var reader = new IndexFileContentReader();
        return (path, cancellationToken) =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                long expectedLength = 0;
                try { expectedLength = new FileInfo(path).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* hint only */ }

                IndexFileReadResult read = reader.Read(path, expectedLength, policy, cancellationToken);
                return new IncrementalFileRead(read.Classification, read.Identity, read.BytesRead);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null; // unreadable → resolver tombstones aliases → file live-scans
            }
        };
    }

    /// <summary>
    /// Applies <paramref name="changed"/> (created/modified files) and <paramref name="deletedPaths"/>
    /// against the current base at USN <paramref name="checkpoint"/>, then compacts if the settings bounds
    /// are exceeded. Returns the outcome; never throws.
    /// </summary>
    public IncrementalUpdateOutcome Apply(
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        IReadOnlyList<IncrementalChange> changed,
        IReadOnlyList<string> deletedPaths,
        UsnCheckpoint checkpoint,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        CancellationToken cancellationToken = default)
    {
        using IndexMutationContext mutation = _store.AcquireMutationContext();
        return ApplyUnderLease(
            mutation, scopeId, volumeIdentity, normalizedRootPath, changed, deletedPaths,
            checkpoint, settings, builtUtc, cancellationToken);
    }

    internal IncrementalUpdateOutcome ApplyUnderLease(
        IndexMutationContext mutation,
        string scopeId,
        string volumeIdentity,
        string normalizedRootPath,
        IReadOnlyList<IncrementalChange> changed,
        IReadOnlyList<string> deletedPaths,
        UsnCheckpoint checkpoint,
        IndexMaintenanceSettings settings,
        DateTimeOffset builtUtc,
        CancellationToken cancellationToken = default,
        bool commitCheckpointWhenUnchanged = false,
        Action<int, string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(deletedPaths);
        ArgumentNullException.ThrowIfNull(settings);

        // A rescan that proves nothing changed still has to publish its barrier, otherwise the root stays
        // permanently unprovable and every later pass repeats the rescan.
        if (changed.Count == 0 && deletedPaths.Count == 0 && !commitCheckpointWhenUnchanged)
            return IncrementalUpdateOutcome.NoChanges;

        cancellationToken.ThrowIfCancellationRequested();

        // Must have a trusted active pointer/layer metadata to append to. Never deserialize the layered
        // content here: the publisher validates the fresh segment and atomically extends that pointer.
        if (!_store.TryGetCurrentLayerDirectories(out string? baseDir, out _)
            || baseDir is null
            || ContentIndexGenerationSerializer.TryReadManifest(baseDir) is not { } baseManifest)
        {
            YaguLog.For("ContentIndex").LogWarning("Incremental update for scope {Scope}: no trusted base to layer over → needs full rebuild.", scopeId);
            return IncrementalUpdateOutcome.NeedsFullRebuild;
        }

        VolumeBinding? mounted = string.IsNullOrWhiteSpace(baseManifest.VolumeGuidPath)
            ? null
            : VolumeBindingReader.TryCapture(normalizedRootPath);
        string volumeReason = "source volume unavailable";
        if (!string.IsNullOrWhiteSpace(baseManifest.VolumeGuidPath)
            && (mounted is not { } currentVolume
                || !VolumeBindingReader.MatchesManifest(baseManifest, currentVolume, out volumeReason)))
        {
            YaguLog.For("ContentIndex").LogWarning(
                "Incremental update for scope {Scope}: mounted volume mismatch ({Reason}) → previous checkpoint retained.",
                scopeId,
                mounted is null ? "source volume unavailable" : volumeReason);
            return IncrementalUpdateOutcome.NeedsFullRebuild;
        }

        EffectiveIndexSizePolicy size = settings.ResolveSizePolicy(normalizedRootPath);
        int budgetMaxSegments = Math.Clamp(settings.MaxDeltaSegments, 1, 64);

        // Opt-in growth stop: when clean-up is due and no allowed path can reclaim the accumulated update
        // history, halt before appending rather than letting the index grow without limit. Off by default,
        // because halting costs index coverage (slower searches, never wrong ones).
        if (settings.HaltUpdatesWhenReclamationBlocked
            && _store.TryReadActiveLayerStorageBreakdown() is { } breakdown)
        {
            bool hasRun = size.AllowsCoalescing && _store.TryFindIncrementalSegmentRun(
                size.CoalesceMinRun, SmallSegmentMaximumRun,
                size.CoalesceMaxSegmentBytes, size.CoalesceMaxBatchBytes, out _);
            IndexReclamationDiagnosis diagnosis = IndexReclamationAdvisor.Diagnose(
                breakdown, size, budgetMaxSegments,
                Math.Clamp(settings.CompactionThresholdMB, 1, 8192), hasRun);
            if (diagnosis.ReclamationBlocked)
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "Scope {Scope} for '{Root}' has {HistoryMB} MB of unreclaimable update history across {Layers} layer(s); updates are paused because you asked Yagu to stop instead of growing further. Searches stay complete \u2014 uncovered files are read live. Compact or rebuild this index to resume.",
                    scopeId, normalizedRootPath, diagnosis.IncrementalHistoryMB, breakdown.IncrementalCount);
                return IncrementalUpdateOutcome.ReclamationBlocked;
            }
        }

        if (size.SizeBudgetMB > 0 && size.ExceedsBudget(_store.TotalActiveIndexBytes()))
        {
            // Over the storage ceiling. Try the bounded, low-memory reclamation first; only if that cannot
            // bring the index back under budget do we stop appending. Halting is the safe way to bound
            // growth: the existing index stays valid and queryable, and anything it no longer covers is
            // live-scanned, whereas folding an oversized index would trade disk growth for substantial
            // background I/O and temporary disk consumption.
            try
            {
                CoalesceSmallSegmentsUnderLease(
                    mutation, budgetMaxSegments, cancellationToken, size, IndexMergeResourceBudget.FromSettings(settings));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Budget-triggered coalescing failed for scope {Scope}; keeping the valid layered index.", scopeId);
            }

            long afterCoalesce = _store.TotalActiveIndexBytes();
            if (size.ExceedsBudget(afterCoalesce) && !size.AllowsCompactingIndexOf(afterCoalesce))
            {
                YaguLog.For("ContentIndex").LogWarning(
                    "Scope {Scope} for '{Root}' is {IndexMB} MB, at or over its {BudgetMB} MB size budget, and mode '{Mode}' cannot reclaim further — pausing index updates for this root. Searches still return every match (uncovered files are read live); rebuild this index to reclaim the space.",
                    scopeId, normalizedRootPath, afterCoalesce / (1024 * 1024), size.SizeBudgetMB, size.Mode);
                return IncrementalUpdateOutcome.SizeBudgetReached;
            }
        }

        var segmentBuilder = new ContentIndexDeltaSegmentBuilder(_policy, identityProvider: _identityProvider);
        if (mounted is { } boundVolume)
            segmentBuilder.SeedVolumeBinding(boundVolume);
        else
            segmentBuilder.SeedVolumeSerialNumber(baseManifest.VolumeSerialNumber);

        var phaseTimer = System.Diagnostics.Stopwatch.StartNew();
        int mergeTotal = changed.Count + deletedPaths.Count;
        int merged = 0;
        int lastMergePercent = -1;
        progress?.Invoke(IndexUpdateStages.MergeFloor, IndexUpdateStages.Merging);
        foreach (IncrementalChange change in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentBuilder.AddChangedClassified(change.Path, change.Classification, change.Identity);
            ReportMerge(++merged);
        }
        foreach (string deleted in deletedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            segmentBuilder.AddTombstone(deleted);
            ReportMerge(++merged);
        }
        long mergeMs = phaseTimer.ElapsedMilliseconds;

        progress?.Invoke(IndexUpdateStages.WriteFloor, IndexUpdateStages.Writing);
        phaseTimer.Restart();
        ContentIndexDeltaSegment segment = segmentBuilder.Build(scopeId, volumeIdentity, normalizedRootPath, checkpoint, builtUtc);
        long buildMs = phaseTimer.ElapsedMilliseconds;

        progress?.Invoke(IndexUpdateStages.PublishFloor, IndexUpdateStages.Publishing);
        phaseTimer.Restart();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.PublishSegmentFastUnderLease(mutation, segment);
        }
        catch (InvalidOperationException ex)
        {
            // Lost the base between the check and publish → let the scheduler do a full rebuild.
            YaguLog.For("ContentIndex").LogWarning(ex, "Incremental update for scope {Scope}: base was lost between check and publish → needs full rebuild.", scopeId);
            return IncrementalUpdateOutcome.NeedsFullRebuild;
        }

        YaguLog.For("ContentIndex").LogInformation(
            "Incremental update for scope {Scope}: appended delta segment ({ChangedCount} changed, {DeletedCount} deleted) — merge {MergeMs} ms, serialize {BuildMs} ms, publish {PublishMs} ms.",
            scopeId, changed.Count, deletedPaths.Count, mergeMs, buildMs, phaseTimer.ElapsedMilliseconds);

        // Verbose-only, bounded, root-relative churn hint so a user investigating an index that grows
        // faster than it can be cleaned up can see which folders to exclude. Never telemetry, never an
        // absolute path, and never an automatic filter change.
        if (LogService.Instance.IsVerboseEnabled)
        {
            IReadOnlyList<IndexChurnEntry> busiest = IndexChurnSummary.TopRootRelativeDirectories(
                changed.Select(change => change.Path).Concat(deletedPaths),
                normalizedRootPath,
                depth: 2,
                take: 5);
            if (IndexChurnSummary.Describe(busiest) is { } churn)
            {
                YaguLog.For("ContentIndex").LogDebug(
                    "Incremental update for scope {Scope}: busiest folders this pass — {Churn}.", scopeId, churn);
            }
        }

        int maxSegments = budgetMaxSegments;
        int thresholdMB = Math.Clamp(settings.CompactionThresholdMB, 1, 8192);
        if (_store.ActiveSegmentCount() > maxSegments && size.AllowsCoalescing)
        {
            progress?.Invoke(IndexUpdateStages.CompactFloor, IndexUpdateStages.Compacting);
            try
            {
                CoalesceSmallSegmentsUnderLease(
                    mutation, maxSegments, cancellationToken, size, IndexMergeResourceBudget.FromSettings(settings));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Coalescing is an optimization after the new delta was durably appended. Any failure leaves
                // that complete pointer active and falls through to the existing full-compaction decision.
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Small-segment coalescing failed for scope {Scope}; keeping the valid layered index.", scopeId);
            }
        }

        if (_store.ShouldCompact(maxSegments, thresholdMB))
        {
            // Automatic full compaction can still be expensive in disk I/O and temporary storage, so the
            // per-index policy decides whether to start it. Once allowed, the fold itself is an external
            // merge bounded by the configured build-memory budget; it never opens the layered index in memory.
            long indexBytes = _store.TotalActiveIndexBytes();
            if (!size.AllowsCompactingIndexOf(indexBytes))
            {
                if (size.ExceedsBudget(indexBytes))
                {
                    YaguLog.For("ContentIndex").LogWarning(
                        "Scope {Scope} is {IndexMB} MB, over its {BudgetMB} MB size budget, but its '{Mode}' size-management mode cannot reclaim further — the appended segment remains active. Rebuild this index to reclaim the space.",
                        scopeId, indexBytes / (1024 * 1024), size.SizeBudgetMB, size.Mode);
                }
                else
                {
                    YaguLog.For("ContentIndex").LogInformation(
                        "Skipping post-incremental auto-compaction for scope {Scope}: the index is {IndexMB} MB (> {MaxCompactMB} MB cap, mode '{Mode}') — the appended segment remains active.",
                        scopeId, indexBytes / (1024 * 1024), size.MaxAutoCompactionSizeMB, size.Mode);
                }
                return IncrementalUpdateOutcome.SegmentAppended;
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(IndexUpdateStages.CompactFloor, IndexUpdateStages.Compacting);
            YaguLog.For("ContentIndex").LogInformation("Incremental update for scope {Scope}: compaction bounds exceeded (maxSegments={MaxSegments}, thresholdMB={ThresholdMB}) → streaming the layered index into a fresh base.", scopeId, maxSegments, thresholdMB);
            try
            {
                ContentIndexManager.RunStreamingCompactionUnderLease(
                    mutation,
                    _store,
                    scopeId,
                    normalizedRootPath,
                    settings,
                    builtUtc,
                    progress is null ? null : ReportStreamingCompaction,
                    cancellationToken);
                YaguLog.For("ContentIndex").LogInformation("Incremental update for scope {Scope}: streaming compaction complete.", scopeId);
                return IncrementalUpdateOutcome.Compacted;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The delta was published before this optional reclamation step. A failed compaction leaves
                // that complete layered pointer active, so report the durable append rather than rebuilding.
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Post-incremental streaming compaction failed for scope {Scope}; keeping the valid layered index.",
                    scopeId);
                return IncrementalUpdateOutcome.SegmentAppended;
            }
        }

        return IncrementalUpdateOutcome.SegmentAppended;

        void ReportMerge(int done)
        {
            if (progress is null || mergeTotal <= 0)
                return;
            int percent = IndexUpdateStages.MergeFloor
                + (int)((long)done * (IndexUpdateStages.MergeCeiling - IndexUpdateStages.MergeFloor) / mergeTotal);
            if (percent == lastMergePercent)
                return;
            lastMergePercent = percent;
            progress(percent, IndexUpdateStages.Merging);
        }

        void ReportStreamingCompaction(int percent, string stage)
        {
            int bounded = Math.Clamp(percent, 0, 100);
            int mapped = IndexUpdateStages.CompactFloor
                + ((100 - IndexUpdateStages.CompactFloor) * bounded / 100);
            progress!(mapped, stage);
        }
    }

    internal int CoalesceSmallSegmentsUnderLease(
        IndexMutationContext mutation,
        int maxSegments,
        CancellationToken cancellationToken,
        EffectiveIndexSizePolicy? sizePolicy = null,
        IndexMergeResourceBudget? resources = null)
    {
        EffectiveIndexSizePolicy size = sizePolicy ?? EffectiveIndexSizePolicy.Default;
        if (!size.AllowsCoalescing)
            return 0;
        IndexMergeResourceBudget budget = resources ?? IndexMergeResourceBudget.Default;

        int mergedRuns = 0;
        int removedLayers = 0;
        while (mergedRuns < size.CoalesceMaxRunsPerPass
               && _store.ActiveSegmentCount() > maxSegments
               && _store.TryFindIncrementalSegmentRun(
                   size.CoalesceMinRun,
                   SmallSegmentMaximumRun,
                   size.CoalesceMaxSegmentBytes,
                   size.CoalesceMaxBatchBytes,
                   out ContentIndexStore.SegmentCoalesceRun? run)
               && run is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var workspace = IndexCompactionWorkspace.Create(_store.IndexRootDirectory);
            var diskGuard = new IndexCompactionDiskGuard(
                _store.IndexRootDirectory, budget.MinimumFreeSpaceMB, budget.MaxDiskUsagePercent);
            try
            {
                StreamingSegmentRunMerger.Merge(
                    run.SegmentDirectories,
                    workspace,
                    budget.MemoryBudgetBytes,
                    diskGuard,
                    _store.ProduceV3QueryStructures,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IndexCompactionDiskGuardException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Fail safe: every existing pointer and layer is untouched; only the workspace is discarded.
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Streaming segment merge aborted before publication; the layered index is unchanged.");
                return removedLayers;
            }

            if (!_store.TryReplacePreparedSegmentRunUnderLease(mutation, run, workspace.PreparedDirectory))
                return removedLayers;

            mergedRuns++;
            removedLayers += run.SegmentIds.Count - 1;
        }

        if (removedLayers > 0)
        {
            YaguLog.For("ContentIndex").LogInformation(
                "Bounded small-segment maintenance removed {RemovedLayerCount} layer(s) across {MergedRunCount} run(s); {RemainingSegmentCount} active segment(s) remain.",
                removedLayers, mergedRuns, _store.ActiveSegmentCount());
        }
        return removedLayers;
    }

    /// <summary>
    /// Produces one segment with the exact newest-wins meaning of a contiguous input run, materializing
    /// every input layer in memory. Production now merges by streaming; this remains the <b>reference
    /// oracle</b> the differential tests compare the streaming merge against. Inputs are walked
    /// newest-to-oldest like the layered query: the first alias/tombstone deciding a path wins; documents are
    /// copied from their existing trigram sets (never re-read from source); hard links within a layer remain
    /// shared. The newest input checkpoint/time becomes the merged segment's logical barrier.
    /// </summary>
    internal ContentIndexDeltaSegment MergeSegmentRun(
        IReadOnlyList<ContentIndexDeltaSegment> segments,
        CancellationToken cancellationToken)
    {
        if (segments.Count < 2)
            throw new ArgumentException("At least two segments are required to coalesce a run.", nameof(segments));

        var builder = new ContentIndexGenerationBuilder(_policy);
        var decided = new HashSet<string>(StringComparer.Ordinal);
        var tombstones = new HashSet<string>(StringComparer.Ordinal);
        IndexManifest firstManifest = segments[0].Added.Manifest;
        UsnCheckpoint priorCheckpoint = firstManifest.FreshnessCheckpoint;
        for (int i = 1; i < segments.Count; i++)
        {
            IndexManifest manifest = segments[i].Added.Manifest;
            if (!string.Equals(manifest.ScopeId, firstManifest.ScopeId, StringComparison.Ordinal)
                || !string.Equals(manifest.NormalizedRootPath, firstManifest.NormalizedRootPath, StringComparison.OrdinalIgnoreCase)
                || manifest.VolumeSerialNumber != firstManifest.VolumeSerialNumber
                || !string.Equals(manifest.VolumeGuidPath, firstManifest.VolumeGuidPath, StringComparison.OrdinalIgnoreCase)
                || manifest.FreshnessCheckpoint.JournalId != firstManifest.FreshnessCheckpoint.JournalId
                || manifest.FreshnessCheckpoint.NextUsn < priorCheckpoint.NextUsn)
            {
                throw new InvalidDataException("Small-segment coalescing inputs disagree on scope, root, volume, journal, or checkpoint order.");
            }
            priorCheckpoint = manifest.FreshnessCheckpoint;
        }
        if (!string.IsNullOrWhiteSpace(firstManifest.VolumeGuidPath)
            && VolumeBindingReader.TryCapture(firstManifest.NormalizedRootPath) is { } volumeBinding
            && VolumeBindingReader.MatchesManifest(firstManifest, volumeBinding, out _))
        {
            builder.SeedVolumeBinding(volumeBinding);
        }
        else
        {
            builder.SeedVolumeSerialNumber(firstManifest.VolumeSerialNumber);
        }

        for (int i = segments.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ContentIndexDeltaSegment segment = segments[i];
            foreach (string path in segment.RemovedPaths)
            {
                if (decided.Add(path))
                    tombstones.Add(path);
            }

            ContentIndexGeneration layer = segment.Added;
            var contentIdMap = new Dictionary<long, long>();
            foreach (var (path, entry) in layer.Aliases
                         .OrderBy(static item => item.Value.ContentId)
                         .ThenBy(static item => item.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!decided.Add(path))
                    continue;

                if (contentIdMap.TryGetValue(entry.ContentId, out long mergedContentId))
                {
                    builder.AddHardLink(path, mergedContentId);
                    continue;
                }

                IReadOnlyCollection<Trigram> trigrams = layer.Documents[(int)entry.ContentId];
                UsnFileIdentity? identity = entry.ContentId < layer.ContentIdentities.Count
                    ? layer.ContentIdentities[(int)entry.ContentId]
                    : null;
                long assigned = builder.AddClassifiedDocument(path, trigrams, identity);
                contentIdMap[entry.ContentId] = assigned;
            }
        }

        IndexManifest newest = segments[^1].Added.Manifest;
        DateTimeOffset? lastIncrementalUpdateUtc = null;
        foreach (ContentIndexDeltaSegment segment in segments)
        {
            DateTimeOffset? candidate = segment.Added.Manifest.LastIncrementalUpdateUtc;
            if (candidate is { } timestamp && (lastIncrementalUpdateUtc is null || timestamp > lastIncrementalUpdateUtc))
                lastIncrementalUpdateUtc = timestamp;
        }
        ContentIndexGeneration added = builder.Build(
            newest.ScopeId,
            newest.VolumeIdentity,
            newest.NormalizedRootPath,
            newest.FreshnessCheckpoint,
            newest.BuiltUtc,
            newest.CreatedUtc,
            lastIncrementalUpdateUtc);
        return new ContentIndexDeltaSegment(added, tombstones);
    }
}
