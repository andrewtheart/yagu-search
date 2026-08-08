using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit.Abstractions;

namespace Yagu.Tests.Index;

/// <summary>
/// Explicitly opted-in validation against the developer's real C: index. This test is never part of
/// the iterative suite and returns immediately unless YAGU_RUN_LIVE_C_INCREMENTAL_TEST=1.
/// </summary>
public sealed class ContentIndexLiveIncrementalTests
{
    private const string OptInVariable = "YAGU_RUN_LIVE_C_INCREMENTAL_TEST";
    private const string IndexedRoot = @"C:\";

    private readonly ITestOutputHelper _output;

    public ContentIndexLiveIncrementalTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Slow")]
    [Trait("Category", "Live")]
    public async Task RealCDrive_TwoIncrementalPassesRepresentEveryMutationThroughCapturedBarriers()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(OptInVariable), "1", StringComparison.Ordinal))
        {
            _output.WriteLine($"Skipped: set {OptInVariable}=1 to mutate and verify the real C: index.");
            return;
        }

        AppSettings settings = new SettingsService(SettingsService.DefaultPath()).Load();
        Assert.True(settings.EnableContentIndex, "Content indexing must already be enabled.");
        Assert.True(settings.IndexUseNativeWorker, "The live test requires the production maintenance-worker path.");
        string? coveringRoot = IndexedRootsPolicy.FindBestCoveringRoot(settings.IndexedRoots, IndexedRoot);
        Assert.Equal(IndexedRoot, coveringRoot, ignoreCase: true);

        var paths = DefaultContentIndexPathProvider.Create(settings.IndexStorageDirectory);
        int retained = AppSettings.NormalizeIndexRetainedGenerationCount(settings.IndexRetainedGenerationCount);
        string scopeId = ContentIndexManager.ScopeIdForRoot(IndexedRoot);
        var store = new ContentIndexStore(paths, scopeId, retained);
        Assert.NotNull(store.TryReadCurrentFreshnessInputs());

        string runId = Guid.NewGuid().ToString("N");
        string probeDirectory = Path.Combine(@"C:\src\Yagu\TestResults\LiveIncremental", runId);
        string modifiedPath = Path.Combine(probeDirectory, "modified.txt");
        string renamedOldPath = Path.Combine(probeDirectory, "rename-source.txt");
        string renamedFinalPath = Path.Combine(probeDirectory, "rename-final.txt");
        string deletedPath = Path.Combine(probeDirectory, "delete-after-first-pass.txt");
        string addedSecondPassPath = Path.Combine(probeDirectory, "added-second-pass.txt");
        string ephemeralPath = Path.Combine(probeDirectory, "create-delete-before-pass.txt");

        string oldToken = "YAGULIVEOLD" + runId;
        string finalToken = "YAGULIVEFINAL" + runId;
        string renamedToken = "YAGULIVERENAMED" + runId;
        string deletedToken = "YAGULIVEDELETED" + runId;
        string addedToken = "YAGULIVEADDED" + runId;

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        bool probeCreated = false;
        try
        {
            // Establish a committed baseline immediately before the controlled mutation sequence.
            IndexMaintenanceSuccess baseline = await RunForcedIncrementalAsync(settings, timeout.Token);
            Assert.Equal(0, baseline.Failed);
            UsnCheckpoint baselineCheckpoint = CurrentCheckpoint(store);
            _output.WriteLine($"Baseline checkpoint: journal={baselineCheckpoint.JournalId}, nextUsn={baselineCheckpoint.NextUsn:N0}");

            Directory.CreateDirectory(probeDirectory);
            probeCreated = true;
            File.WriteAllText(modifiedPath, $"phase one {oldToken}\r\n", new UTF8Encoding(false));
            File.WriteAllText(renamedOldPath, $"rename payload {renamedToken}\r\n", new UTF8Encoding(false));
            File.WriteAllText(deletedPath, $"delete payload {deletedToken}\r\n", new UTF8Encoding(false));
            UsnCheckpoint firstMutationBarrier = CaptureBarrier();
            Assert.True(firstMutationBarrier.NextUsn > baselineCheckpoint.NextUsn);

            ContentIndexDeltaSegment firstSegment = await RunAndReadNewSegmentAsync(settings, store, timeout.Token);
            UsnCheckpoint firstCommittedCheckpoint = CurrentCheckpoint(store);
            AssertCheckpointCovers(firstCommittedCheckpoint, firstMutationBarrier);
            AssertMember(firstSegment, modifiedPath, oldToken);
            AssertMember(firstSegment, renamedOldPath, renamedToken);
            AssertMember(firstSegment, deletedPath, deletedToken);
            _output.WriteLine($"First pass committed through USN {firstCommittedCheckpoint.NextUsn:N0}; new C: files are indexed.");

            File.WriteAllText(modifiedPath, $"phase two {finalToken}\r\n", new UTF8Encoding(false));
            File.Move(renamedOldPath, renamedFinalPath);
            File.Delete(deletedPath);
            File.WriteAllText(addedSecondPassPath, $"new payload {addedToken}\r\n", new UTF8Encoding(false));
            File.WriteAllText(ephemeralPath, "created and deleted before the next pass", new UTF8Encoding(false));
            File.Delete(ephemeralPath);
            UsnCheckpoint secondMutationBarrier = CaptureBarrier();
            Assert.True(secondMutationBarrier.NextUsn > firstCommittedCheckpoint.NextUsn);

            ContentIndexDeltaSegment secondSegment = await RunAndReadNewSegmentAsync(settings, store, timeout.Token);
            UsnCheckpoint secondCommittedCheckpoint = CurrentCheckpoint(store);
            AssertCheckpointCovers(secondCommittedCheckpoint, secondMutationBarrier);

            AssertMember(secondSegment, modifiedPath, finalToken);
            AssertNonmember(secondSegment, modifiedPath, oldToken);
            AssertMember(secondSegment, renamedFinalPath, renamedToken);
            AssertMember(secondSegment, addedSecondPassPath, addedToken);
            Assert.Contains(IndexScopeIdentity.NormalizePath(renamedOldPath), secondSegment.RemovedPaths);
            Assert.Contains(IndexScopeIdentity.NormalizePath(deletedPath), secondSegment.RemovedPaths);
            Assert.False(secondSegment.Added.TryGetAlias(IndexScopeIdentity.NormalizePath(ephemeralPath), out _, out _));
            _output.WriteLine($"Second pass committed through USN {secondCommittedCheckpoint.NextUsn:N0}; modify/rename/add/delete states verified.");
        }
        finally
        {
            if (probeCreated && Directory.Exists(probeDirectory))
                Directory.Delete(probeDirectory, recursive: true);

            if (probeCreated)
            {
                UsnCheckpoint cleanupBarrier = CaptureBarrier();
                IndexMaintenanceSuccess cleanup = await RunForcedIncrementalAsync(settings, timeout.Token);
                Assert.Equal(0, cleanup.Failed);
                AssertCheckpointCovers(CurrentCheckpoint(store), cleanupBarrier);
                _output.WriteLine("Cleanup pass committed; probe directory removals are represented by the live index.");
            }
        }
    }

    private async Task<ContentIndexDeltaSegment> RunAndReadNewSegmentAsync(
        AppSettings settings,
        ContentIndexStore store,
        CancellationToken cancellationToken)
    {
        Assert.True(store.TryGetCurrentLayerDirectories(out _, out IReadOnlyList<string> beforeDirectories));
        var before = beforeDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);

        IndexMaintenanceSuccess result = await RunForcedIncrementalAsync(settings, cancellationToken);
        Assert.Equal(0, result.Failed);
        IndexMaintenanceRootResult root = Assert.Single(result.Roots);
        Assert.Equal(IndexMaintenanceActions.DeltaAppended, root.Action);

        Assert.True(store.TryGetCurrentLayerDirectories(out _, out IReadOnlyList<string> afterDirectories));
        string newSegmentDirectory = Assert.Single(afterDirectories, path => !before.Contains(path));
        ContentIndexDeltaSegment? segment = ContentIndexDeltaSegmentSerializer.TryRead(newSegmentDirectory);
        Assert.NotNull(segment);
        _output.WriteLine($"Published {Path.GetFileName(newSegmentDirectory)}: indexed={root.IndexedCount:N0}, skipped={root.SkippedCount:N0}");
        return segment!;
    }

    private async Task<IndexMaintenanceSuccess> RunForcedIncrementalAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        IndexMaintenanceOperation operation = IndexBuildOperationFactory.CreateMaintenance(
            settings,
            [IndexedRoot],
            IndexMaintenanceOperation.ModeIncremental,
            rebuildWhenDirty: false);
        operation.ForceRefresh = true;
        operation.AllowFullRebuildFallback = false;
        operation.AllowCompatibilityRebuild = false;
        operation.Settings.SizeManagementMode = IndexSizeManagementModes.Off;
        operation.Settings.MaxDeltaSegments = 64;
        operation.Settings.MaxAutoCompactionSizeMB = 0;

        var coordinator = new IndexBuildCoordinator();
        return await coordinator.RunMaintenancePreferWorkerAsync(
            operation,
            useWorker: true,
            cancellationToken,
            (root, percent, stage) => _output.WriteLine($"{root}: {percent}% {stage}"));
    }

    private static UsnCheckpoint CaptureBarrier()
        => UsnJournalReader.TryCaptureCheckpoint(IndexedRoot)
            ?? throw new InvalidOperationException("C: does not expose a readable USN journal checkpoint.");

    private static UsnCheckpoint CurrentCheckpoint(ContentIndexStore store)
        => store.TryReadCurrentFreshnessInputs()?.Manifest.FreshnessCheckpoint
            ?? throw new InvalidOperationException("The current C: index freshness checkpoint is unreadable.");

    private static void AssertCheckpointCovers(UsnCheckpoint committed, UsnCheckpoint barrier)
    {
        Assert.Equal(barrier.JournalId, committed.JournalId);
        Assert.True(committed.NextUsn >= barrier.NextUsn,
            $"Committed checkpoint {committed.NextUsn:N0} did not cover mutation barrier {barrier.NextUsn:N0}.");
    }

    private static void AssertMember(ContentIndexDeltaSegment segment, string path, string token)
    {
        string normalized = IndexScopeIdentity.NormalizePath(path);
        Assert.True(segment.Added.TryGetAlias(normalized, out _, out long contentId), $"Missing alias for {normalized}");
        Assert.Contains((int)contentId, Evaluate(segment, token));
    }

    private static void AssertNonmember(ContentIndexDeltaSegment segment, string path, string token)
    {
        string normalized = IndexScopeIdentity.NormalizePath(path);
        Assert.True(segment.Added.TryGetAlias(normalized, out _, out long contentId), $"Missing alias for {normalized}");
        Assert.DoesNotContain((int)contentId, Evaluate(segment, token));
    }

    private static IReadOnlySet<int> Evaluate(ContentIndexDeltaSegment segment, string token)
    {
        var options = new SearchOptions
        {
            Directory = IndexedRoot,
            Query = token,
            CaseSensitive = true,
            ExactMatch = false,
            UseContentIndex = true,
        };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        TrigramExpression query = Assert.IsType<TrigramPlan.Eligible>(plan).Query;
        return segment.Added.Postings.EvaluateSet(query);
    }
}
