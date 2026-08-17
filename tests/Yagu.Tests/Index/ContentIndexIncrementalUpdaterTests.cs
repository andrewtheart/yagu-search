using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests the Phase 3 incremental-update orchestration (plan §11.4): appending a delta segment for a resolved
/// change set, the no-changes / no-base outcomes, and the automatic compaction into a fresh base once the
/// segment/size bounds are exceeded — end-to-end against a real <see cref="ContentIndexStore"/> sandbox.
/// </summary>
public sealed class ContentIndexIncrementalUpdaterTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _root = @"C:\r";
    private readonly IContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexIncrementalUpdaterTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-incr-upd", Guid.NewGuid().ToString("N"));
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

    private static AppSettings Settings(int maxSegments = 8, int thresholdMB = 256)
        => new() { IndexMaxDeltaSegments = maxSegments, IndexCompactionThresholdMB = thresholdMB };

    [Fact]
    public void Apply_UnreclaimableHistory_HaltsOnlyWhenTheUserOptedIn()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // Ten incremental layers with a run minimum no run can reach, in a mode that forbids compaction:
        // nothing automatic can reclaim the accumulated history.
        for (int i = 0; i < 10; i++)
        {
            var builder = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
            builder.AddChangedDocument($@"C:\r\changed-{i}.txt", Encoding.UTF8.GetBytes($"changed {i}"));
            store.PublishSegment(builder.Build(
                _scopeId, "vol", _root, new UsnCheckpoint(1, 200 + i), DateTimeOffset.UtcNow));
        }

        var blocked = new IndexMaintenanceSettings
        {
            MaxDeltaSegments = 8,
            CompactionThresholdMB = 1,
            SizeManagementMode = IndexSizeManagementModes.Coalesce, // compaction is not permitted at all
            CoalesceMaxSegmentMB = 1024,
            CoalesceMaxBatchMB = 4096,
            CoalesceMinRun = 20, // no contiguous run can ever reach this length here
        };

        // Default: keep updating, only report the problem.
        IncrementalUpdateOutcome keptUpdating = updater.Apply(
            _scopeId, "vol", _root, [Change(@"C:\r\b.txt", "beta")], Array.Empty<string>(),
            new UsnCheckpoint(1, 400), blocked, DateTimeOffset.UtcNow);
        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, keptUpdating);

        blocked.HaltUpdatesWhenReclamationBlocked = true;
        int segmentsBefore = store.ActiveSegmentCount();
        IncrementalUpdateOutcome halted = updater.Apply(
            _scopeId, "vol", _root, [Change(@"C:\r\c.txt", "gamma")], Array.Empty<string>(),
            new UsnCheckpoint(1, 500), blocked, DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.ReclamationBlocked, halted);
        Assert.Equal(segmentsBefore, store.ActiveSegmentCount());
        Assert.NotNull(store.TryOpenLayered());
    }

    [Fact]
    public void Apply_NoChanges_IsNoOp()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        var outcome = updater.Apply(_scopeId, "vol", _root, Array.Empty<IncrementalChange>(), Array.Empty<string>(),
            new UsnCheckpoint(2, 200), Settings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.NoChanges, outcome);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_NoBase_NeedsFullRebuild()
    {
        var store = NewStore();
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        var outcome = updater.Apply(_scopeId, "vol", _root, new[] { Change(@"C:\r\a.txt", "alpha") }, Array.Empty<string>(),
            new UsnCheckpoint(2, 200), Settings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, outcome);
    }

    [Fact]
    public void Apply_ChangedAndDeleted_AppendsSegment()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"), (@"C:\r\gone.txt", "delete me"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        var outcome = updater.Apply(_scopeId, "vol", _root,
            new[] { Change(@"C:\r\b.txt", "the planner is new here") },
            new[] { IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt") },
            new UsnCheckpoint(2, 200), Settings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(1, store.ActiveSegmentCount());
        var handle = store.TryOpenLayered();
        Assert.Single(handle!.Segments);
        Assert.True(handle.Segments[0].IsRemoved(IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt")));
    }

    [Fact]
    public void ApplyUnderLease_ReportsOnlyExecutedPhasesInMonotonicOrder()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"), (@"C:\r\gone.txt", "delete me"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var progress = new List<(int Percent, string Stage)>();

        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            new[] { Change(@"C:\r\b.txt", "the planner is new here") },
            new[] { IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt") },
            new UsnCheckpoint(2, 200),
            IndexBuildOperationFactory.CreateMaintenanceSettings(Settings()),
            DateTimeOffset.UtcNow,
            progress: (percent, stage) => progress.Add((percent, stage)));

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(
            new[] { IndexUpdateStages.Merging, IndexUpdateStages.Writing, IndexUpdateStages.Publishing },
            progress.Select(item => item.Stage).Distinct());
        Assert.DoesNotContain(progress, item => item.Stage == IndexUpdateStages.Compacting);
        Assert.True(progress.Zip(progress.Skip(1), (left, right) => left.Percent <= right.Percent).All(value => value));
    }

    [Fact]
    public void ApplyUnderLease_WhenCompactionRuns_ReportsCompactingPhase()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha base"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        AppSettings settings = Settings(maxSegments: 1, thresholdMB: 4096);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, updater.Apply(
            _scopeId,
            "vol",
            _root,
            new[] { Change(@"C:\r\s0.txt", "first segment") },
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            settings,
            DateTimeOffset.UtcNow));

        var progress = new List<(int Percent, string Stage)>();
        using IndexMutationContext mutation = store.AcquireMutationContext();
        IncrementalUpdateOutcome outcome = updater.ApplyUnderLease(
            mutation,
            _scopeId,
            "vol",
            _root,
            new[] { Change(@"C:\r\s1.txt", "second segment") },
            Array.Empty<string>(),
            new UsnCheckpoint(1, 300),
            IndexBuildOperationFactory.CreateMaintenanceSettings(settings),
            DateTimeOffset.UtcNow,
            progress: (percent, stage) => progress.Add((percent, stage)));

        Assert.Equal(IncrementalUpdateOutcome.Compacted, outcome);
        Assert.Contains(progress, item => item == (IndexUpdateStages.CompactFloor, IndexUpdateStages.Compacting));
        Assert.Contains(progress, item => item.Stage == IndexUpdateStages.CompactAnalyzing);
        Assert.Contains(progress, item => item.Stage == IndexUpdateStages.CompactMerging);
        Assert.Contains(progress, item => item.Stage == IndexUpdateStages.CompactPublishing);
        Assert.True(progress.Zip(progress.Skip(1), (left, right) => left.Percent <= right.Percent).All(value => value));
    }

    [Fact]
    public void Apply_ChangedFileWithoutCapturedIdentity_InheritsBaseVolumeSerial()
    {
        const ulong volumeSerial = 0xCAFE;
        var store = NewStore();
        var baseBuilder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: _ => new FileIdentity(volumeSerial, new UsnFileIdentity(123, 0)));
        baseBuilder.AddDocument(@"C:\r\a.txt", Encoding.UTF8.GetBytes("alpha"));
        store.Publish(baseBuilder.Build(
            _scopeId,
            "vol",
            _root,
            new UsnCheckpoint(1, 100),
            DateTimeOffset.UtcNow));

        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        IncrementalUpdateOutcome outcome = updater.Apply(
            _scopeId,
            "vol",
            _root,
            new[] { Change(@"C:\r\identity-unavailable.txt", "new planner text") },
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            Settings(),
            DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        ContentIndexDeltaSegment segment = Assert.Single(store.TryOpenLayered()!.Segments);
        Assert.Equal(volumeSerial, segment.Added.Manifest.VolumeSerialNumber);
        Assert.Null(Assert.Single(segment.Added.ContentIdentities));
    }

    [Fact]
    public void Apply_ExceedingSegmentBound_CompactsIntoFreshBase()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha base"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var settings = Settings(maxSegments: 2, thresholdMB: 4096); // compact after >2 segments
        // Compact-only: coalescing would merge the run first and satisfy the bound without a full fold,
        // which is its own test below.
        settings.IndexSizeManagementMode = IndexSizeManagementModes.Compact;

        IncrementalUpdateOutcome last = IncrementalUpdateOutcome.NoChanges;
        for (int i = 0; i < 3; i++)
        {
            last = updater.Apply(_scopeId, "vol", _root,
                new[] { Change($@"C:\r\s{i}.txt", $"segment doc number {i} content") },
                Array.Empty<string>(),
                new UsnCheckpoint(1, 200 + i), settings, DateTimeOffset.UtcNow);
        }

        // The third append pushed the segment count over 2 → compaction folded everything into a new base.
        Assert.Equal(IncrementalUpdateOutcome.Compacted, last);
        Assert.Equal(0, store.ActiveSegmentCount());
        // All four documents survive in the compacted base (a + s0 + s1 + s2).
        Assert.Equal(4, store.TryOpenCurrent()!.AliasCount);
    }

    /// <summary>
    /// With the shipped default the cheap bounded merge absorbs the layer-count trigger, so exceeding it
    /// coalesces a run instead of paying for a full fold. Every path stays queryable either way.
    /// </summary>
    [Fact]
    public void Apply_ExceedingSegmentBound_CoalescesRunBeforeCompactingUnderTheDefaultMode()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha base"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var settings = Settings(maxSegments: 2, thresholdMB: 4096);
        Assert.Equal(IndexSizeManagementModes.CoalesceThenCompact, settings.IndexSizeManagementMode);
        Assert.Equal(3, AppSettings.DefaultIndexCoalesceMinRun);

        IncrementalUpdateOutcome last = IncrementalUpdateOutcome.NoChanges;
        for (int i = 0; i < 3; i++)
        {
            last = updater.Apply(_scopeId, "vol", _root,
                new[] { Change($@"C:\r\s{i}.txt", $"segment doc number {i} content") },
                Array.Empty<string>(),
                new UsnCheckpoint(1, 200 + i), settings, DateTimeOffset.UtcNow);
        }

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, last);
        Assert.Equal(1, store.ActiveSegmentCount());

        ContentIndexStore.LayeredIndexHandle? handle = store.TryOpenLayered();
        Assert.NotNull(handle);
        Assert.True(handle!.Base.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt"), out _, out _));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(handle.Segments[0].Added.TryGetAlias(
                IndexScopeIdentity.NormalizePath($@"C:\r\s{i}.txt"), out _, out _));
        }
    }

    [Fact]
    public void Apply_ExceedingSegmentBound_SkipsCompactionAboveAutomaticSizeCap()
    {
        var store = NewStore();
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        for (int i = 0; i < 700; i++)
        {
            string wideBody = string.Concat(System.Linq.Enumerable.Range(0, 40)
                .Select(_ => Guid.NewGuid().ToString("N")));
            builder.AddDocument($@"C:\r\base-{i}.txt", Encoding.UTF8.GetBytes(wideBody));
        }
        store.Publish(builder.Build(_scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));
        Assert.True(store.TotalActiveIndexBytes() > 1024 * 1024);

        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var settings = Settings(maxSegments: 1, thresholdMB: 4096);
        settings.IndexSizeManagementMode = IndexSizeManagementModes.Compact;
        settings.IndexMaxAutoCompactionSizeMB = 1;

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, updater.Apply(
            _scopeId, "vol", _root,
            new[] { Change(@"C:\r\s0.txt", "first segment") }, Array.Empty<string>(),
            new UsnCheckpoint(1, 200), settings, DateTimeOffset.UtcNow));
        IncrementalUpdateOutcome capped = updater.Apply(
            _scopeId, "vol", _root,
            new[] { Change(@"C:\r\s1.txt", "second segment") }, Array.Empty<string>(),
            new UsnCheckpoint(1, 300), settings, DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, capped);
        Assert.Equal(2, store.ActiveSegmentCount());

        settings.IndexMaxAutoCompactionSizeMB = 0;
        IncrementalUpdateOutcome uncapped = updater.Apply(
            _scopeId, "vol", _root,
            new[] { Change(@"C:\r\s2.txt", "third segment") }, Array.Empty<string>(),
            new UsnCheckpoint(1, 400), settings, DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.Compacted, uncapped);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_OverConfiguredSizeBudgetWithoutCompaction_HaltsBeforeAppending()
    {
        var store = NewStore();
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        for (int index = 0; index < 700; index++)
        {
            string wideBody = string.Concat(Enumerable.Range(0, 40)
                .Select(_ => Guid.NewGuid().ToString("N")));
            builder.AddDocument($@"C:\r\base-{index}.txt", Encoding.UTF8.GetBytes(wideBody));
        }
        store.Publish(builder.Build(
            _scopeId, "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow));
        Assert.True(store.TotalActiveIndexBytes() > 1024 * 1024);

        var settings = new IndexMaintenanceSettings
        {
            SizeBudgetMB = 1,
            SizeManagementMode = IndexSizeManagementModes.Coalesce,
        };
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        IncrementalUpdateOutcome outcome = updater.Apply(
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\new.txt", "new content")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 200),
            settings,
            DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SizeBudgetReached, outcome);
        Assert.Equal(0, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_CorruptOlderLayer_KeepsTheNewlyPublishedSegmentWhenReclamationFails()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\base.txt", "base content"));
        for (int layer = 0; layer < 3; layer++)
        {
            var segment = new ContentIndexDeltaSegmentBuilder(OpenPolicy);
            segment.AddChangedDocument(
                $@"C:\r\old-{layer}.txt",
                Encoding.UTF8.GetBytes($"old layer {layer}"));
            store.PublishSegment(segment.Build(
                _scopeId,
                "vol",
                _root,
                new UsnCheckpoint(1, 200 + layer),
                DateTimeOffset.UtcNow.AddMinutes(layer)));
        }

        string corruptAliases = Path.Combine(
            store.ScopeDirectory,
            "segments",
            "seg-000001",
            ContentIndexGenerationSerializer.AliasesFile);
        File.WriteAllBytes(corruptAliases, [1, 2, 3]);

        AppSettings settings = Settings(maxSegments: 2, thresholdMB: 4096);
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        IncrementalUpdateOutcome outcome = updater.Apply(
            _scopeId,
            "vol",
            _root,
            [Change(@"C:\r\new.txt", "new durable segment")],
            Array.Empty<string>(),
            new UsnCheckpoint(1, 500),
            settings,
            DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.Equal(4, store.ActiveSegmentCount());
    }

    [Fact]
    public void Apply_ChangedToBinary_TombstonesInSegment()
    {
        var store = NewStore();
        PublishBase(store, (@"C:\r\a.txt", "alpha"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);

        // a.txt turned into binary content (embedded NUL) → not admitted → tombstoned in the segment.
        var outcome = updater.Apply(_scopeId, "vol", _root,
            new[] { new IncrementalChange(@"C:\r\a.txt", IndexIngestionClassifier.ClassifyContent(new byte[] { (byte)'a', 0x00, (byte)'b' }, OpenPolicy), null) },
            Array.Empty<string>(),
            new UsnCheckpoint(2, 200), Settings(), DateTimeOffset.UtcNow);

        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        Assert.True(store.TryOpenLayered()!.Segments[0].IsRemoved(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt")));
    }

    [Fact]
    public void Apply_ManySmallSegments_CoalescesWithoutOpeningOrCompactingTheBase()
    {
        var store = NewStore();
        store.ProduceV3QueryStructures = true;
        PublishBase(store, (@"C:\r\a.txt", "old value"), (@"C:\r\gone.txt", "remove me"));
        var updater = new ContentIndexIncrementalUpdater(store, OpenPolicy);
        var settings = Settings(maxSegments: 8, thresholdMB: 8192);

        for (int i = 0; i < 9; i++)
        {
            IncrementalChange[] changed = i switch
            {
                0 => new[] { Change(@"C:\r\a.txt", "first replacement") },
                2 => new[] { Change(@"C:\r\a.txt", "newest planner replacement") },
                _ => new[] { Change($@"C:\r\s{i}.txt", $"small segment {i} planner text") },
            };
            string[] deleted = i == 1
                ? new[] { IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt") }
                : Array.Empty<string>();

            IncrementalUpdateOutcome outcome = updater.Apply(
                _scopeId, "vol", _root, changed, deleted,
                new UsnCheckpoint(1, 200 + i), settings, DateTimeOffset.UtcNow.AddSeconds(i));
            Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, outcome);
        }

        // Nine small layers exceeded the configured bound and were replaced by one bounded merged segment;
        // the base was not folded into a new generation.
        Assert.Equal(1, store.ActiveSegmentCount());
        ContentIndexStore.LayeredIndexHandle handle = store.TryOpenLayered()!;
        ContentIndexDeltaSegment merged = Assert.Single(handle.Segments);
        Assert.True(merged.IsRemoved(IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt")));
        Assert.True(merged.Added.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt"), out _, out long aContentId));
        IReadOnlyCollection<Trigram> expected = IndexIngestionClassifier
            .ClassifyContent(Encoding.UTF8.GetBytes("newest planner replacement"), OpenPolicy)
            .Trigrams;
        Assert.True(expected.ToHashSet().SetEquals(merged.Added.Documents[(int)aContentId]));
        Assert.True(merged.Added.TryGetAlias(IndexScopeIdentity.NormalizePath(@"C:\r\s8.txt"), out _, out _));
        using ContentIndexV3Reader mapped = ContentIndexV3Format.TryOpen(handle.SegmentDirs[0])!;
        Assert.True(mapped.TryLookupPath(IndexScopeIdentity.NormalizePath(@"C:\r\a.txt"), out _, out _));
        Assert.True(mapped.ContainsTombstone(IndexScopeIdentity.NormalizePath(@"C:\r\gone.txt")));

        // The other redundant pointer still references the complete pre-coalesce stack. Corrupting the new
        // slot proves readers can roll back to it rather than observing a partial merge.
        string scopeDir = _paths.GetScopeDirectory(_scopeId);
        string latestSlot = Directory.GetFiles(scopeDir, "current.*")
            .OrderByDescending(path => long.Parse(File.ReadLines(path).First()))
            .First();
        File.WriteAllText(latestSlot, "corrupt pointer");
        var fallbackStore = new ContentIndexStore(_paths, _scopeId, retainedGenerations: 4);
        Assert.Equal(9, fallbackStore.TryOpenLayered()!.Segments.Count);
    }

    // ── CreateFileReadClassifier (Stage 6): the on-disk read-and-classify delegate the incremental refresh
    //    feeds to the resolver — one handle for bytes + identity, matching the full build's classification. ──

    [Fact]
    public void CreateFileReadClassifier_TextFile_AdmitsAndCapturesIdentity()
    {
        string file = Path.Combine(_sandbox, "text.txt");
        File.WriteAllText(file, "the quick brown fox jumps over the lazy dog", new UTF8Encoding(false));
        var classify = ContentIndexIncrementalUpdater.CreateFileReadClassifier(OpenPolicy);

        IncrementalFileRead? read = classify(file);

        Assert.NotNull(read);
        Assert.True(read!.Value.Classification.Admitted);
        Assert.NotEmpty(read.Value.Classification.Trigrams);
        Assert.NotNull(read.Value.Identity); // identity captured from the same handle whose bytes were read
    }

    [Fact]
    public void CreateFileReadClassifier_BinaryFile_NotAdmitted()
    {
        string file = Path.Combine(_sandbox, "bin.dat");
        File.WriteAllBytes(file, new byte[] { (byte)'a', 0x00, (byte)'b', (byte)'c' });
        var classify = ContentIndexIncrementalUpdater.CreateFileReadClassifier(OpenPolicy);

        IncrementalFileRead? read = classify(file);

        Assert.NotNull(read);
        Assert.False(read!.Value.Classification.Admitted); // embedded NUL → binary → tombstoned by the caller
    }

    [Fact]
    public void CreateFileReadClassifier_MissingFile_ReturnsNull()
    {
        var classify = ContentIndexIncrementalUpdater.CreateFileReadClassifier(OpenPolicy);

        // Unreadable path → null → the change is dropped, the base entry stays, and the file live-scans.
        Assert.Null(classify(Path.Combine(_sandbox, "does-not-exist.txt")));
    }

    [Fact]
    public void BoundedFileClassifier_UncooperativeReadTimesOut_ThenUsesReplacementLane()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            var admitted = new IncrementalFileRead(
                Change(@"C:\r\fast.txt", "fast replacement").Classification,
                null);
            using var classifier = new BoundedIncrementalFileClassifier(
                (path, _) =>
                {
                    if (path == "hang")
                        release.Wait(); // deliberately ignores cancellation like a stuck filesystem open
                    return path == "fast" ? admitted : null;
                },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(40),
                TimeSpan.FromMilliseconds(20),
                maximumAbandonedLanes: 2);

            var timer = Stopwatch.StartNew();
            Assert.Null(classifier.Read("hang"));
            timer.Stop();

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Read was not bounded: {timer.Elapsed}");
            Assert.Equal(admitted, classifier.Read("fast"));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void BoundedFileClassifier_CancellableReadTimesOutWithoutAbandoningLane()
    {
        int calls = 0;
        using var classifier = new BoundedIncrementalFileClassifier(
            (_, token) =>
            {
                Interlocked.Increment(ref calls);
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return null;
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(40),
            TimeSpan.FromSeconds(1),
            maximumAbandonedLanes: 1);

        Assert.Null(classifier.Read("first"));
        Assert.Null(classifier.Read("second"));
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public void BoundedFileClassifier_TooManyUnstoppableReads_FailsThePass()
    {
        using var release = new ManualResetEventSlim(false);
        try
        {
            using var classifier = new BoundedIncrementalFileClassifier(
                (_, _) =>
                {
                    release.Wait();
                    return null;
                },
                CancellationToken.None,
                TimeSpan.FromMilliseconds(30),
                TimeSpan.FromMilliseconds(10),
                maximumAbandonedLanes: 2);

            Assert.Null(classifier.Read("first-hang"));
            IOException error = Assert.Throws<IOException>(() => classifier.Read("second-hang"));
            Assert.Contains("previous index remains unchanged", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void BoundedFileClassifier_ConvenienceCtors_UseDefaults()
    {
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        // Two- and three-argument public ctors chain onto the internal one with the default timeout/grace.
        using (new BoundedIncrementalFileClassifier(policy, CancellationToken.None)) { }
        using (new BoundedIncrementalFileClassifier(policy, CancellationToken.None, TimeSpan.FromSeconds(5))) { }
    }

    [Fact]
    public void BoundedFileClassifier_InvalidArguments_Throw()
    {
        var policy = new IndexIngestionPolicy(0, null, null, true, false, 0);
        Func<string, CancellationToken, IncrementalFileRead?> read = (_, _) => null;

        Assert.Throws<ArgumentNullException>(() =>
            new BoundedIncrementalFileClassifier(null!, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.Zero, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedIncrementalFileClassifier(policy, CancellationToken.None, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedIncrementalFileClassifier(read, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-1), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedIncrementalFileClassifier(read, CancellationToken.None, TimeSpan.FromSeconds(1), TimeSpan.Zero, 0));
    }

    [Fact]
    public void BoundedFileClassifier_ReadThrows_PropagatesTheException()
    {
        using var classifier = new BoundedIncrementalFileClassifier(
            (_, _) => throw new InvalidOperationException("boom"),
            CancellationToken.None,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50),
            maximumAbandonedLanes: 2);

        var ex = Assert.Throws<InvalidOperationException>(() => classifier.Read("x"));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public void BoundedFileClassifier_DoubleDispose_IsIdempotent()
    {
        var classifier = new BoundedIncrementalFileClassifier(
            (_, _) => null,
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            maximumAbandonedLanes: 1);
        classifier.Dispose();
        classifier.Dispose(); // second Dispose hits the already-disposed early return
        Assert.Throws<ObjectDisposedException>(() => classifier.Read("x"));
    }

    [Fact]
    public void BoundedFileClassifier_OperationCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        using var classifier = new BoundedIncrementalFileClassifier(
            (_, token) =>
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return null;
            },
            cancellation.Token,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50),
            maximumAbandonedLanes: 1);

        Assert.Throws<OperationCanceledException>(() => classifier.Read("cancel"));
    }

    [Fact]
    public void BoundedFileReadRequest_Lifecycle_IsIdempotentAndDeferred()
    {
        var deferred = new BoundedIncrementalFileClassifier.ReadRequest("file", CancellationToken.None);
        deferred.DisposeWhenCompleted();
        deferred.SetResult(null);
        deferred.SignalCompleted();
        Assert.Throws<ObjectDisposedException>(() => deferred.Completed.Wait(TimeSpan.Zero));

        var disposed = new BoundedIncrementalFileClassifier.ReadRequest("file", CancellationToken.None);
        disposed.Dispose();
        disposed.Cancel();
        disposed.Dispose();

        var completed = new BoundedIncrementalFileClassifier.ReadRequest("file", CancellationToken.None);
        completed.SignalCompleted();
        completed.DisposeWhenCompleted();
    }

    [Fact]
    public void BoundedFileIoLane_GuardsHandlesAndLifecycle()
    {
        var lane = new BoundedIncrementalFileClassifier.IoLane((_, _) => null);
        FieldInfo handleField = typeof(BoundedIncrementalFileClassifier.IoLane)
            .GetField("_threadHandle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (SafeWaitHandle)handleField.GetValue(lane)!;
        try
        {
            using var invalid = new SafeWaitHandle(IntPtr.Zero, ownsHandle: false);
            handleField.SetValue(lane, invalid);
            Assert.False(lane.CancelPendingSynchronousIo());

            var closed = new SafeWaitHandle(new IntPtr(1), ownsHandle: false);
            closed.Dispose();
            handleField.SetValue(lane, closed);
            Assert.False(lane.CancelPendingSynchronousIo());
        }
        finally
        {
            handleField.SetValue(lane, original);
            lane.Abandon();
            lane.Abandon();
            lane.Dispose();
            lane.Dispose();
        }
    }

    [Fact]
    public void BoundedFileIoLane_NativeCancellation_HandlesResultAndDisposalRace()
    {
        using var handle = new SafeWaitHandle(new IntPtr(1), ownsHandle: false);

        Assert.True(BoundedIncrementalFileClassifier.IoLane.TryCancelSynchronousIo(handle, _ => true));
        Assert.False(BoundedIncrementalFileClassifier.IoLane.TryCancelSynchronousIo(
            handle,
            _ => throw new ObjectDisposedException("thread handle")));
    }

    [Fact]
    public void BoundedFileClassifierPool_ValidatesIndexesAndDisposesEveryLane()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BoundedIncrementalFileClassifierPool(0, _ => null!));
        Assert.Throws<ArgumentNullException>(() =>
            new BoundedIncrementalFileClassifierPool(1, null!));

        int created = 0;
        using var pool = new BoundedIncrementalFileClassifierPool(2, _ =>
        {
            created++;
            return new BoundedIncrementalFileClassifier(
                (_, _) => null,
                CancellationToken.None,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero,
                maximumAbandonedLanes: 1);
        });

        Assert.Equal(2, created);
        Assert.NotNull(pool[0]);
        Assert.NotNull(pool[1]);
    }
}
