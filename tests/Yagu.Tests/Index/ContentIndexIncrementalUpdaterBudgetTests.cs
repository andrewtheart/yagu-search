using System.Text;
using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ContentIndexIncrementalUpdaterBudgetTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexIncrementalUpdaterBudgetTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-incr-upd-budget", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _paths = new DefaultContentIndexPathProvider(_sandbox, _sandbox);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexStore NewStore(int retained = 4) => new(_paths, _scopeId, retained);

    private void PublishBase(ContentIndexStore store, params (string Path, string Text)[] docs)
    {
        var b = new ContentIndexGenerationBuilder(OpenPolicy);
        foreach (var (p, t) in docs)
            b.AddDocument(p, Encoding.UTF8.GetBytes(t));
        store.Publish(b.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));
    }

    private static IncrementalChange Change(string path, string text)
        => new(path, IndexIngestionClassifier.ClassifyContent(Encoding.UTF8.GetBytes(text), OpenPolicy), null);

    [Fact]
    public void Apply_PrePublishBudgetCoalesceCancellationPreservesExistingIndex()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 2 * 1024 * 1024);
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => throw new OperationCanceledException(),
        };

        // Cancellation before a budget-repair append must leave the old checkpoint in place; a partial
        // cleanup cannot stand in for the uncommitted journal range.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        Assert.Throws<OperationCanceledException>(() => updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.Coalesce),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime));

        Assert.Equal(0, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_PrePublishBudgetCoalesceFailurePausesWithoutAppending()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 2 * 1024 * 1024);
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => throw new IOException("coalesce failed"),
        };

        // A failed best-effort coalesce must not publish a new segment while the index is already over its
        // hard budget; pausing keeps searches complete through live scanning.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.Coalesce),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.SizeBudgetReached, outcome);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_PrePublishBudgetCompactionDeferralDoesNotAppend()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 2 * 1024 * 1024);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
            UtcNow = () => now,
            GetDeferredCompactionFailure = (_, _, _) => new IndexAutomaticCompactionFailure(now, now.AddMinutes(5), "disk full"),
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // A recorded compaction backoff must stop before appending another over-budget delta; otherwise the
        // retry marker would not bound growth after a failed cleanup.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.CompactionFailed, outcome);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_HaltWhenReclamationBlockedOnlyStopsWhenAdvisorFindsNoCleanupPath()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        var settings = new IndexMaintenanceSettings
        {
            HaltUpdatesWhenReclamationBlocked = true,
            SizeManagementMode = IndexSizeManagementModes.CoalesceThenCompact,
            MaxDeltaSegments = 8,
            CompactionThresholdMB = 4096,
            CoalesceMaxSegmentMB = 128,
            CoalesceMaxBatchMB = 512,
            CoalesceMinRun = 2,
            CoalesceMaxRunsPerPass = 8,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // The opt-in halt must not become a blanket maintenance pause; if the advisor sees no unreclaimable
        // history, the delta still commits with its checkpoint.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            settings,
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            new ContentIndexIncrementalUpdateRuntime());

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_PrePublishBudgetCompactionCancellationAndFailureKeepOldCheckpoint()
    {
        foreach (bool cancel in new[] { true, false })
        {
            var store = NewStore();
            PublishBase(store, (@"C:\r\a.txt", "alpha"));
            MakeActiveIndexAppear(store, baseBytes: 2 * 1024 * 1024);
            Exception? recorded = null;
            var runtime = new ContentIndexIncrementalUpdateRuntime
            {
                CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
                GetDeferredCompactionFailure = static (_, _, _) => null,
                RunStreamingCompaction = (_, _, _, _, _, _, _, _) =>
                {
                    if (cancel)
                        throw new OperationCanceledException();
                    throw new IOException("compact failed");
                },
                RecordAutomaticCompactionFailure = (_, ex, _) => recorded = ex,
            };
            var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

            // A failed or cancelled pre-publish compaction is not a committed delta. Cancellation bubbles to
            // the caller; ordinary I/O failure records a retry marker and leaves the previous checkpoint.
            using IndexMutationContext mutation = store.AcquireMutationContext();
            if (cancel)
            {
                Assert.Throws<OperationCanceledException>(() => updater.ApplyUnderLease(
                    mutation, _scopeId, "vol", _root, [Change(@"C:\r\b.txt", "beta")], Array.Empty<string>(),
                    new UsnCheckpoint(1, 200), BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
                    DateTimeOffset.UtcNow, CancellationToken.None, false, null, runtime));
                Assert.Null(recorded);
            }
            else
            {
                IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
                    mutation, _scopeId, "vol", _root, [Change(@"C:\r\b.txt", "beta")], Array.Empty<string>(),
                    new UsnCheckpoint(1, 200), BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
                    DateTimeOffset.UtcNow, CancellationToken.None, false, null, runtime);
                Assert.Equal(IncrementalUpdateOutcome.CompactionFailed, outcome);
                Assert.IsType<IOException>(recorded);
            }

            Assert.Equal(0, store.ActiveSegmentCount());
            Assert.Equal(new UsnCheckpoint(1, 100), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
        }
    }

    [Fact]
    public void Apply_PrePublishBudgetSuccessfulCompactionAllowsDeltaAndClearsFailure()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        bool overBudget = true;
        store.DirectorySizeReader = _ => overBudget ? 2 * 1024 * 1024 : 1;
        int clears = 0;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
            GetDeferredCompactionFailure = static (_, _, _) => null,
            RunStreamingCompaction = (_, _, _, _, _, _, _, _) => overBudget = false,
            ClearAutomaticCompactionFailure = _ => clears++,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Once pre-publish compaction proves the active index is back under budget, the pending journal
        // delta can be committed and any stale retry marker must be cleared.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
        Assert.True(clears >= 1);
    }

    [Fact]
    public void Apply_PostPublishBudgetCoalesceCancellationLeavesDurableDeltaForReplay()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 1, segmentBytes: 2 * 1024 * 1024);
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => throw new OperationCanceledException(),
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // After the delta is published, cancellation during budget cleanup must surface while keeping the
        // complete segment active so the next pass resumes from the last committed checkpoint.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        Assert.Throws<OperationCanceledException>(() => updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.Coalesce),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime));

        Assert.Equal(1, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_PostPublishBudgetCoalesceFailurePausesWithDeltaRetained()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 1, segmentBytes: 2 * 1024 * 1024);
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => throw new IOException("coalesce failed"),
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // A post-publish cleanup failure is safe to report as a budget halt because the delta and checkpoint
        // are already durable; rolling back would make the journal replay ambiguous.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.Coalesce),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.SizeBudgetReached, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_PostPublishBudgetCompactionDeferralKeepsPublishedDelta()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 1, segmentBytes: 2 * 1024 * 1024);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
            UtcNow = () => now,
            GetDeferredCompactionFailure = (_, _, _) => new IndexAutomaticCompactionFailure(now, now.AddMinutes(5), "retry later"),
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Once the delta is committed, a compaction retry marker must report typed failure without removing
        // the segment that represents the just-accepted journal range.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.CompactionFailed, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_PostPublishBudgetCompactionSuccessClearsMarkerAndReportsCompacted()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        bool overBudget = false;
        store.DirectorySizeReader = path =>
            overBudget && path.Contains(@"\segments\", StringComparison.OrdinalIgnoreCase)
                ? 2 * 1024 * 1024
                : 1;
        int clears = 0;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
            GetDeferredCompactionFailure = static (_, _, _) => null,
            RunStreamingCompaction = (_, _, _, _, _, _, _, _) => overBudget = false,
            ClearAutomaticCompactionFailure = _ => clears++,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Successful post-publish compaction keeps the delta's checkpoint represented while clearing any
        // previous retry marker so maintenance does not keep backing off.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        overBudget = true;
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.Compacted, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
        Assert.Equal(1, clears);
    }

    [Fact]
    public void Apply_PostPublishBudgetCompactionFailureRecordsRetryAndRetainsDelta()
    {
        foreach (bool cancel in new[] { true, false })
        {
            var store = NewStore();
            PublishBase(store, (@"C:\r\a.txt", "alpha"));
            MakeActiveIndexAppear(store, baseBytes: 1, segmentBytes: 2 * 1024 * 1024);
            Exception? recorded = null;
            var runtime = new ContentIndexIncrementalUpdateRuntime
            {
                CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
                GetDeferredCompactionFailure = static (_, _, _) => null,
                RunStreamingCompaction = (_, _, _, _, _, _, _, _) =>
                {
                    if (cancel)
                        throw new OperationCanceledException();
                    throw new IOException("compact failed");
                },
                RecordAutomaticCompactionFailure = (_, ex, _) => recorded = ex,
            };
            var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

            // Compaction is optional after the segment is durable. Cancellation still propagates, while I/O
            // failure records a retry marker and keeps the new checkpoint queryable.
            using IndexMutationContext mutation = store.AcquireMutationContext();
            if (cancel)
            {
                Assert.Throws<OperationCanceledException>(() => updater.ApplyUnderLease(
                    mutation, _scopeId, "vol", _root, [Change(@"C:\r\b.txt", "beta")], Array.Empty<string>(),
                    new UsnCheckpoint(1, 200), BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
                    DateTimeOffset.UtcNow, CancellationToken.None, false, null, runtime));
                Assert.Null(recorded);
            }
            else
            {
                IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
                    mutation, _scopeId, "vol", _root, [Change(@"C:\r\b.txt", "beta")], Array.Empty<string>(),
                    new UsnCheckpoint(1, 200), BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
                    DateTimeOffset.UtcNow, CancellationToken.None, false, null, runtime);
                Assert.Equal(IncrementalUpdateOutcome.CompactionFailed, outcome);
                Assert.IsType<IOException>(recorded);
            }

            Assert.Equal(1, store.ActiveSegmentCount());
            Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
        }
    }

    [Fact]
    public void Apply_PostPublishBudgetCompactionThatCannotFitRecordsFailureAndPauses()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        MakeActiveIndexAppear(store, baseBytes: 1, segmentBytes: 2 * 1024 * 1024);
        Exception? recorded = null;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = static (_, _, _, _, _, _) => 0,
            GetDeferredCompactionFailure = static (_, _, _) => null,
            RunStreamingCompaction = static (_, _, _, _, _, _, _, _) => { },
            RecordAutomaticCompactionFailure = (_, ex, _) => recorded = ex,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Even a completed fold cannot be treated as success if it still exceeds the hard budget; the
        // marker drives later retries while the published delta remains safe to query.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.CoalesceThenCompact),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.SizeBudgetReached, outcome);
        Assert.IsType<InvalidOperationException>(recorded);
        Assert.Contains("still exceeds", recorded!.Message);
        Assert.Equal(1, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_PostPublishBudgetCoalescingThatReclaimsEnoughKeepsCommittedDelta()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        bool overBudgetAfterPublish = true;
        store.DirectorySizeReader = path =>
            overBudgetAfterPublish && path.Contains(@"\segments\", StringComparison.OrdinalIgnoreCase)
                ? 2 * 1024 * 1024
                : 1;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = (_, _, _, _, _, _) =>
            {
                overBudgetAfterPublish = false;
                return 1;
            },
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // A bounded coalesce that brings the active layers back under the hard budget should keep the
        // newly published delta and avoid an unnecessary full fold.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\b.txt", "beta")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            BudgetSettings(IndexSizeManagementModes.Coalesce),
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 200), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_RoutineCompactionDeferralRetainsPublishedDelta()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        PublishSegment(store, 200, @"C:\r\old.txt", "old");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            GetDeferredCompactionFailure = (_, _, _) => new IndexAutomaticCompactionFailure(now, now.AddMinutes(5), "retry later"),
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Routine compaction backoff is a maintenance failure, not a delta failure: the newly published
        // segment must stay active so the journal checkpoint advances atomically with its content.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\new.txt", "new")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 300),
            new IndexMaintenanceSettings
            {
                MaxDeltaSegments = 1,
                CompactionThresholdMB = 4096,
                SizeManagementMode = IndexSizeManagementModes.Compact,
                MaxAutoCompactionSizeMB = 8192,
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.CompactionFailed, outcome);
        Assert.Equal(2, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 300), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_RoutineCoalesceFailureKeepsCommittedDelta()
    {
        foreach (bool cancel in new[] { true, false })
        {
            var store = NewStore();
            PublishBase(store, (@"C:\r\a.txt", "alpha"));
            PublishSegment(store, 200, @"C:\r\old.txt", "old");
            var runtime = new ContentIndexIncrementalUpdateRuntime
            {
                CoalesceSmallSegments = (_, _, _, _, _, _) =>
                {
                    if (cancel)
                        throw new OperationCanceledException();
                    throw new IOException("routine coalesce failed");
                },
            };
            var settings = new IndexMaintenanceSettings
            {
                MaxDeltaSegments = 1,
                CompactionThresholdMB = 4096,
                SizeManagementMode = IndexSizeManagementModes.Coalesce,
                CoalesceMaxSegmentMB = 128,
                CoalesceMaxBatchMB = 512,
                CoalesceMinRun = 2,
                CoalesceMaxRunsPerPass = 8,
            };
            var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

            // Routine coalescing runs after the delta is committed. Cancellation propagates, while an
            // ordinary merge failure falls back to a valid layered index with the new checkpoint intact.
            using IndexMutationContext mutation = store.AcquireMutationContext();
            if (cancel)
            {
                Assert.Throws<OperationCanceledException>(() => updater.ApplyUnderLease(
                    mutation, _scopeId, "vol", _root, [Change(@"C:\r\new.txt", "new")], Array.Empty<string>(),
                    new UsnCheckpoint(1, 300), settings, DateTimeOffset.UtcNow,
                    CancellationToken.None, false, null, runtime));
            }
            else
            {
                IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
                    mutation, _scopeId, "vol", _root, [Change(@"C:\r\new.txt", "new")], Array.Empty<string>(),
                    new UsnCheckpoint(1, 300), settings, DateTimeOffset.UtcNow,
                    CancellationToken.None, false, null, runtime);
                Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
            }

            Assert.Equal(2, store.ActiveSegmentCount());
            Assert.Equal(new UsnCheckpoint(1, 300), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
        }
    }

    [Fact]
    public void Apply_RoutineStreamingCompactionCancellationRetainsCommittedDelta()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        PublishSegment(store, 200, @"C:\r\old.txt", "old");
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            GetDeferredCompactionFailure = static (_, _, _) => null,
            RunStreamingCompaction = static (_, _, _, _, _, _, _, _) => throw new OperationCanceledException(),
        };
        var settings = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 1,
            CompactionThresholdMB = 4096,
            SizeManagementMode = IndexSizeManagementModes.Compact,
            MaxAutoCompactionSizeMB = 8192,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Cancellation during the optional full fold must not roll back the already-published delta; the
        // next maintenance pass resumes from that committed checkpoint.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        Assert.Throws<OperationCanceledException>(() => updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\new.txt", "new")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 300),
            settings,
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime));

        Assert.Equal(2, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 300), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void Apply_RoutineCompactionOverCapKeepsDeltaWhenBoundedMergeCanStillProgress()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        PublishSegment(store, 200, @"C:\r\old.txt", "old");
        bool reportOverBudgetAtRoutineDecision = false;
        store.DirectorySizeReader = path =>
            reportOverBudgetAtRoutineDecision && path.Contains(@"\segments\", StringComparison.OrdinalIgnoreCase)
                ? 2 * 1024 * 1024
                : 1;
        var runtime = new ContentIndexIncrementalUpdateRuntime
        {
            CoalesceSmallSegments = (_, _, _, _, _, _) =>
            {
                reportOverBudgetAtRoutineDecision = true;
                return 0;
            },
        };
        var settings = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 1,
            CompactionThresholdMB = 4096,
            SizeManagementMode = IndexSizeManagementModes.CoalesceThenCompact,
            SizeBudgetMB = 1,
            MaxAutoCompactionSizeMB = 1,
            CoalesceMinRun = 2,
            CoalesceMaxSegmentMB = 128,
            CoalesceMaxBatchMB = 512,
            CoalesceMaxRunsPerPass = 8,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // If a bounded run can still make progress, routine maintenance must not start an over-cap full
        // fold; the delta remains published and the next pass can attempt the cheap merge first.
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\new.txt", "new")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 300),
            settings,
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            commitCheckpointWhenUnchanged: false,
            progress: null,
            runtime);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(2, store.ActiveSegmentCount());
        Assert.Equal(new UsnCheckpoint(1, 300), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
    }

    [Fact]
    public void CoalesceSmallSegments_FailuresLeaveLayeredPointerUnchanged()
    {
        foreach (string failure in new[] { "cancel", "invalid", "lost" })
        {
            var store = NewStore();
            PublishBase(store, (@"C:\r\a.txt", "alpha"));
            PublishSegment(store, 200, @"C:\r\s0.txt", "old");
            PublishSegment(store, 300, @"C:\r\s1.txt", "new");
            var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
            using IndexMutationContext mutation = store.AcquireMutationContext();

            // Coalescing is an optimization over already-committed layers; cancellation, invalid input, or
            // a lost prepared layer must never rewrite the active pointer.
            if (failure == "cancel")
            {
                Assert.Throws<OperationCanceledException>(() => updater.CoalesceSmallSegmentsUnderLease(
                    mutation,
                    maxSegments: 1,
                    CancellationToken.None,
                    CoalescingPolicy(),
                    IndexMergeResourceBudget.Default,
                    static (_, _, _, _, _, _) => throw new OperationCanceledException(),
                    static (_, _, _, _) => true));
            }
            else
            {
                int removed = updater.CoalesceSmallSegmentsUnderLease(
                    mutation,
                    maxSegments: 1,
                    CancellationToken.None,
                    CoalescingPolicy(),
                    IndexMergeResourceBudget.Default,
                    failure == "invalid"
                        ? static (_, _, _, _, _, _) => throw new InvalidDataException("bad run")
                        : static (_, _, _, _, _, _) => default,
                    failure == "lost"
                        ? static (_, _, _, _) => false
                        : static (_, _, _, _) => true);
                Assert.Equal(0, removed);
            }

            Assert.Equal(2, store.ActiveSegmentCount());
            Assert.Equal(new UsnCheckpoint(1, 300), store.TryReadCurrentIncrementalManifest()!.FreshnessCheckpoint);
        }
    }


    private static IndexMaintenanceSettings BudgetSettings(string mode) => new()
    {
        SizeBudgetMB = 1,
        SizeManagementMode = mode,
        MaxDeltaSegments = 64,
        CompactionThresholdMB = 4096,
        MaxAutoCompactionSizeMB = 8192,
        CoalesceMaxSegmentMB = 128,
        CoalesceMaxBatchMB = 512,
        CoalesceMinRun = 2,
        CoalesceMaxRunsPerPass = 8,
    };

    private static EffectiveIndexSizePolicy CoalescingPolicy() => new(
        IndexSizeManagementModes.CoalesceThenCompact,
        SizeBudgetMB: 0,
        MaxAutoCompactionSizeMB: 8192,
        CoalesceMaxSegmentMB: 128,
        CoalesceMaxBatchMB: 512,
        CoalesceMinRun: 2,
        CoalesceMaxRunsPerPass: 8);

    private void PublishSegment(ContentIndexStore store, long nextUsn, string path, string text)
    {
        var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
        builder.SeedVolumeSerialNumber(1);
        builder.AddChangedDocument(path, Encoding.UTF8.GetBytes(text));
        store.PublishSegment(builder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, nextUsn),
            DateTimeOffset.UtcNow));
    }

    private static void MakeActiveIndexAppear(ContentIndexStore store, long baseBytes, long segmentBytes = 0)
        => store.DirectorySizeReader = path =>
            path.Contains(@"\segments\", StringComparison.OrdinalIgnoreCase) ? segmentBytes : baseBytes;
}
