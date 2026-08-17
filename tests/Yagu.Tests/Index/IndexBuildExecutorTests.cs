using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexBuildExecutorTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-executor", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _indexRoot;
    private readonly FixedContentIndexPathProvider _paths;

    public IndexBuildExecutorTests()
    {
        _root = Path.Combine(_sandbox, "root");
        _indexRoot = Path.Combine(_sandbox, "index");
        _paths = new FixedContentIndexPathProvider(_indexRoot);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "planner executor document");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("YAGU_PDFTOTEXT", null);
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void BuildFullScope_CommitsRawAndReportsUnavailablePdfWithoutFailingRaw()
    {
        Environment.SetEnvironmentVariable("YAGU_PDFTOTEXT", Path.Combine(_sandbox, "missing-pdftotext.exe"));
        IndexBuildOperation operation = BuildOperation();
        operation.BuildPdfText = true;
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IndexBuildSuccess result = IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation, operation, CancellationToken.None, null, null);

        Assert.Equal(1, result.IndexedCount);
        Assert.Equal(PdfExtendedSourceBuildStatus.SkippedToolUnavailable.ToString(), result.PdfStatus);
        Assert.NotNull(new ContentIndexStore(_paths, result.ScopeId).TryOpenCurrent());
    }

    [Fact]
    public void BuildFullScope_PublishedPdfMovesTheStagedNamespaceWithTheRawCommit()
    {
        IndexBuildOperation operation = BuildOperation();
        operation.BuildPdfText = true;
        var runtime = new IndexBuildRuntime
        {
            BuildPdf = (_, _, stagingPaths, root, _, _, progress) =>
            {
                string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                string stagedPdf = new ExtendedSourceStore(stagingPaths, scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
                Directory.CreateDirectory(stagedPdf);
                File.WriteAllText(Path.Combine(stagedPdf, "namespace.bin"), "staged");
                progress?.Invoke(new PdfBuildProgress(1, 1));
                return new PdfExtendedSourceBuildResult(
                    scopeId, PdfExtendedSourceBuildStatus.Published, 1, 1, PdfDeterminismVerdict.Deterministic);
            },
        };
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IndexBuildSuccess result = IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation, operation, CancellationToken.None, null, null, runtime);

        string livePdf = new ExtendedSourceStore(_paths, result.ScopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Assert.Equal("staged", File.ReadAllText(Path.Combine(livePdf, "namespace.bin")));
        Assert.Equal(PdfExtendedSourceBuildStatus.Published.ToString(), result.PdfStatus);
    }

    [Fact]
    public void BuildFullScope_UnexpectedPdfFailureCommitsRawButCancellationDoesNot()
    {
        IndexBuildOperation operation = BuildOperation();
        operation.BuildPdfText = true;
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var failing = new IndexBuildRuntime
        {
            BuildPdf = static (_, _, _, _, _, _, _) => throw new IOException("pdf failed"),
        };

        IndexBuildSuccess result = IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation, operation, CancellationToken.None, null, null, failing);
        Assert.Contains("Failed: pdf failed", result.PdfStatus);
        Assert.NotNull(new ContentIndexStore(_paths, result.ScopeId).TryOpenCurrent());

        string secondRoot = Path.Combine(_sandbox, "cancelled-root");
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(secondRoot, "b.txt"), "cancelled pdf build");
        operation.Root = secondRoot;
        var cancelling = new IndexBuildRuntime
        {
            BuildPdf = static (_, _, _, _, _, _, _) => throw new OperationCanceledException(),
        };
        Assert.Throws<OperationCanceledException>(() => IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation, operation, CancellationToken.None, null, null, cancelling));
        Assert.Null(new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(secondRoot)).TryOpenCurrent());
    }

    [Fact]
    public void BuildFullScope_PublishedImageOcrMovesStagedNamespaceWithRawCommit()
    {
        IndexBuildOperation operation = BuildOperation();
        operation.BuildImageText = true;
        var runtime = new IndexBuildRuntime
        {
            BuildImageOcr = (mutation, manager, stagedPaths, root, policy, build, ct, progress) =>
            {
                string scopeId = ContentIndexManager.ScopeIdForRoot(root);
                var builder = new ExtendedSourceNamespaceBuilder(
                    SpecialSourceKind.ImageOcr,
                    new ExtractorFingerprint(SpecialSourceKind.ImageOcr, "paddle", "", "cpu",
                        [new ExtractorFileHash("worker", "hash")], [new("model", "ChineseV5")]));
                string image = IndexScopeIdentity.NormalizePath(Path.Combine(root, "image.png"));
                builder.AddSource(image, new ExtractionOutcome.Success("indexed OCR text"), new UsnFileIdentity(1, 0));
                Assert.True(new ExtendedSourceStore(stagedPaths, scopeId).PublishUnderLease(
                    mutation, builder.Build(root, new UsnCheckpoint(1, 100))));
                progress?.Invoke(new ImageOcrBuildProgress(1, 1));
                return new ImageOcrExtendedSourceBuildResult(scopeId, ImageOcrExtendedSourceBuildStatus.Published, 1, 1, 0);
            },
        };
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IndexBuildSuccess result = IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation, operation, CancellationToken.None, null, null, runtime);

        ExtendedSourceNamespace? live = new ExtendedSourceStore(_paths, result.ScopeId)
            .TryLoad(SpecialSourceKind.ImageOcr);
        Assert.NotNull(live);
        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published.ToString(), result.ImageOcrStatus);
        Assert.Equal(1, result.ImagesAdmitted);
    }

    [Fact]
    public void BuildFullScope_RunsPostBuildCatchUpOnStagedIndexBeforeAtomicCommit()
    {
        IndexBuildOperation operation = BuildOperation();
        operation.PostBuildCatchUpSettings.PostBuildCatchUpThresholdChanges = 30_000;
        string scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        bool catchUpRan = false;
        var runtime = new IndexBuildRuntime
        {
            RunPostBuildCatchUp = (mutation, stagedPaths, build, cancellationToken, progress) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                catchUpRan = true;
                Assert.NotEqual(
                    _paths.GetScopeDirectory(scopeId),
                    stagedPaths.GetScopeDirectory(scopeId));
                var stagedStore = new ContentIndexStore(stagedPaths, scopeId);
                IndexManifest stagedManifest = stagedStore.TryReadCurrentIncrementalManifest()!;
                var segmentBuilder = new ContentIndexDeltaSegmentBuilder(build.Policy.ToPolicy());
                if (VolumeBindingReader.TryCapture(stagedManifest.NormalizedRootPath) is { } volumeBinding)
                    segmentBuilder.SeedVolumeBinding(volumeBinding);
                else
                    segmentBuilder.SeedVolumeSerialNumber(stagedManifest.VolumeSerialNumber);
                segmentBuilder.AddChangedDocument(
                    Path.Combine(_root, "a.txt"),
                    System.Text.Encoding.UTF8.GetBytes("changed during the staged build"));
                stagedStore.PublishSegmentUnderLease(
                    mutation,
                    segmentBuilder.Build(
                        scopeId,
                        stagedManifest.VolumeIdentity,
                        stagedManifest.NormalizedRootPath,
                        new UsnCheckpoint(
                            stagedManifest.FreshnessCheckpoint.JournalId,
                            stagedManifest.FreshnessCheckpoint.NextUsn + 1),
                        DateTimeOffset.UtcNow));
                Assert.Equal(1, stagedStore.ActiveSegmentCount());
                Assert.Null(new ContentIndexStore(_paths, scopeId).TryOpenCurrent());
                progress?.Invoke(100);
                return new PostBuildCatchUpResult(
                    true,
                    build.PostBuildCatchUpSettings.PostBuildCatchUpThresholdChanges,
                    IncrementalUpdateOutcome.SegmentAppended,
                    30_001,
                    true,
                    true);
            },
        };
        var catchUpProgress = new List<int>();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IndexBuildSuccess result = IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation,
            operation,
            CancellationToken.None,
            null,
            null,
            runtime,
            postBuildCatchUpProgress: catchUpProgress.Add);

        Assert.True(catchUpRan);
        Assert.Equal(new[] { 100 }, catchUpProgress);
        Assert.Equal(IncrementalUpdateOutcome.SegmentAppended, result.PostBuildCatchUp.Outcome);
        Assert.StartsWith("seg-", result.LastPublishedArtifactId);
        var liveStore = new ContentIndexStore(_paths, scopeId);
        Assert.NotNull(liveStore.TryOpenCurrent());
        Assert.Equal(1, liveStore.ActiveSegmentCount());
    }

    [Fact]
    public void BuildFullScope_CancelledPostBuildCatchUpPreservesPreviousGeneration()
    {
        IndexBuildSuccess previous;
        using (IndexMutationContext initialMutation = IndexMutationContext.Acquire(_paths))
        {
            previous = IndexBuildExecutor.BuildFullScopeUnderLease(
                initialMutation,
                BuildOperation(),
                CancellationToken.None,
                null,
                null);
        }
        IndexManifest previousManifest = new ContentIndexStore(_paths, previous.ScopeId)
            .TryReadCurrentIncrementalManifest()!;
        File.WriteAllText(Path.Combine(_root, "a.txt"), "replacement content");
        IndexBuildOperation replacement = BuildOperation();
        replacement.PostBuildCatchUpSettings.PostBuildCatchUpThresholdChanges = 0;
        var runtime = new IndexBuildRuntime
        {
            RunPostBuildCatchUp = static (_, _, _, _, _) => throw new OperationCanceledException(),
        };

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        Assert.Throws<OperationCanceledException>(() => IndexBuildExecutor.BuildFullScopeUnderLease(
            mutation,
            replacement,
            CancellationToken.None,
            null,
            null,
            runtime));

        IndexManifest active = new ContentIndexStore(_paths, previous.ScopeId)
            .TryReadCurrentIncrementalManifest()!;
        Assert.Equal(previousManifest.BuiltUtc, active.BuiltUtc);
        Assert.Equal(previousManifest.FreshnessCheckpoint, active.FreshnessCheckpoint);
    }

    [Fact]
    public void ProductionPostBuildCatchUp_WithoutAStagedBaseFailsClosedBeforeJournalAccess()
    {
        IndexBuildOperation operation = BuildOperation();
        operation.PostBuildCatchUpSettings.PostBuildCatchUpThresholdChanges = 30_000;
        var emptyPaths = new FixedContentIndexPathProvider(Path.Combine(_sandbox, "empty-index"));
        using IndexMutationContext mutation = IndexMutationContext.Acquire(emptyPaths);

        PostBuildCatchUpResult result = IndexMaintenanceRuntime.RunProductionPostBuildCatchUp(
            mutation,
            emptyPaths,
            operation,
            CancellationToken.None,
            null);

        Assert.Equal(new PostBuildCatchUpResult(
            true,
            30_000,
            IncrementalUpdateOutcome.NeedsFullRebuild,
            0,
            false,
            false), result);
    }

    [Fact]
    public void MaintenanceBuildMapping_PropagatesCatchUpAttentionAndProgressSemantics()
    {
        PostBuildCatchUpResult warning = new(
            true,
            30_000,
            IncrementalUpdateOutcome.NeedsFullRebuild,
            30_001,
            true,
            true);
        IndexBuildSuccess build = new(
            "scope",
            "gen-000001",
            1,
            "gen-000001",
            "summary",
            2,
            3,
            null,
            0,
            0,
            null,
            null,
            0,
            0,
            0,
            warning);

        IndexMaintenanceRootResult warned = IndexBuildExecutor.FromBuild(_root, build);
        IndexMaintenanceRootResult clean = IndexBuildExecutor.FromBuild(
            _root,
            build with
            {
                PostBuildCatchUp = new PostBuildCatchUpResult(
                    true,
                    30_000,
                    IncrementalUpdateOutcome.NoChanges,
                    0,
                    true,
                    false),
            });

        Assert.Equal(warning.Describe(), warned.Warning);
        Assert.Null(clean.Warning);
        Assert.Equal(-1, IndexBuildExecutor.MapPostBuildCatchUpProgress(-1));
        Assert.Equal(99, IndexBuildExecutor.MapPostBuildCatchUpProgress(0));
        Assert.Equal(99, IndexBuildExecutor.MapPostBuildCatchUpProgress(100));
    }

    [Fact]
    public void Maintenance_BuildDue_CoversMissingBuildFreshSkipDirtyRebuildAndMissingFolder()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeBuildDue, _root);
        var progress = new List<(string Root, int Percent, string Stage)>();
        var runtime = new IndexMaintenanceRuntime { CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Fresh };

        IndexMaintenanceSuccess built = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, (r, p, s) => progress.Add((r, p, s)), runtime);
        Assert.Equal(1, built.Built);
        Assert.Equal(IndexMaintenanceActions.Built, Assert.Single(built.Roots).Action);
        Assert.Contains(progress, item => item.Stage == "rawBuild");

        IndexMaintenanceSuccess skipped = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);
        Assert.Equal(1, skipped.Skipped);

        operation.RebuildWhenDirty = true;
        runtime = new IndexMaintenanceRuntime { CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Dirty };
        IndexMaintenanceSuccess rebuilt = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);
        Assert.Equal(1, rebuilt.Built);

        operation = Maintenance(IndexMaintenanceOperation.ModeBuildDue, Path.Combine(_sandbox, "missing"));
        IndexMaintenanceSuccess failed = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);
        Assert.Equal(1, failed.Failed);
        Assert.Equal("dirNotFound", Assert.Single(failed.Roots).Outcome);
    }

    [Theory]
    [InlineData(IncrementalUpdateOutcome.SegmentAppended, IndexMaintenanceActions.DeltaAppended, 1, 0)]
    [InlineData(IncrementalUpdateOutcome.Compacted, IndexMaintenanceActions.Compacted, 1, 0)]
    [InlineData(IncrementalUpdateOutcome.NoChanges, IndexMaintenanceActions.Skipped, 0, 1)]
    [InlineData(IncrementalUpdateOutcome.SizeBudgetReached, IndexMaintenanceActions.SizeBudgetReached, 0, 1)]
    [InlineData(IncrementalUpdateOutcome.ReclamationBlocked, IndexMaintenanceActions.ReclamationBlocked, 0, 1)]
    [InlineData(IncrementalUpdateOutcome.NeedsFullRebuild, IndexMaintenanceActions.Built, 1, 0)]
    [InlineData(IncrementalUpdateOutcome.NeedsCompatibilityRebuild, IndexMaintenanceActions.Built, 1, 0)]
    public void Maintenance_Incremental_MapsEveryRefreshOutcome(
        IncrementalUpdateOutcome refreshOutcome,
        string expectedAction,
        int expectedBuilt,
        int expectedSkipped)
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Dirty,
            Refresh = (_, _, _, _, _, _, _, report) =>
            {
                report?.Invoke(50, IndexUpdateStages.Resolving);
                report?.Invoke(100, IndexUpdateStages.Incremental);
                return refreshOutcome;
            },
        };
        var progress = new List<(int Percent, string Stage)>();

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation,
            Maintenance(IndexMaintenanceOperation.ModeIncremental, _root),
            CancellationToken.None,
            (_, percent, stage) => progress.Add((percent, stage)),
            runtime);

        Assert.Equal(expectedBuilt, result.Built);
        Assert.Equal(expectedSkipped, result.Skipped);
        Assert.Equal(expectedAction, Assert.Single(result.Roots).Action);
        Assert.Contains((50, IndexUpdateStages.Resolving), progress);
        Assert.Contains((100, IndexUpdateStages.Incremental), progress);
    }

    [Fact]
    public void Maintenance_CompactOnly_MissingIndexIsSkipped()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation,
            Maintenance(IndexMaintenanceOperation.ModeCompactOnly, Path.Combine(_sandbox, "missing")),
            CancellationToken.None,
            progress: null);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(IndexMaintenanceActions.Skipped, Assert.Single(result.Roots).Action);
    }

    [Fact]
    public void Maintenance_ForcedIncremental_NeedsFullRebuild_KeepsExistingIndexAndReportsFailure()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);
        operation.ForceRefresh = true;
        operation.AllowFullRebuildFallback = false;
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => throw new InvalidOperationException("forced refresh must bypass preflight"),
            Refresh = static (_, _, _, _, _, _, _, _) => IncrementalUpdateOutcome.NeedsFullRebuild,
        };

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Failed);
        IndexMaintenanceRootResult root = Assert.Single(result.Roots);
        Assert.Equal(IndexMaintenanceActions.Failed, root.Action);
        Assert.Equal("needsFullRebuild", root.Outcome);
        Assert.NotNull(new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public void Maintenance_ExplicitIncremental_DeclinesCompatibilityRebuild()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);
        operation.ForceRefresh = true;
        operation.AllowFullRebuildFallback = false;
        operation.AllowCompatibilityRebuild = false;
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => throw new InvalidOperationException("forced refresh must bypass preflight"),
            Refresh = static (_, _, _, _, _, _, _, _) => IncrementalUpdateOutcome.NeedsCompatibilityRebuild,
        };

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Failed);
        Assert.Equal("needsFullRebuild", Assert.Single(result.Roots).Outcome);
        Assert.NotNull(new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public void Maintenance_AutomaticIncremental_UncertainFreshness_RescanDisabled_IsNotMistakenForFreshOrRebuilt()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);
        operation.AllowFullRebuildFallback = false;
        operation.Settings.RescanOnJournalGap = false;
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, cap) =>
            {
                Assert.Equal(2_000_000, cap);
                return ContentIndexManager.ScopeFreshnessState.Uncertain;
            },
            Refresh = static (_, _, _, _, _, _, _, _) => throw new InvalidOperationException("uncertain preflight must not load/refresh the index"),
        };

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);

        Assert.Equal(0, result.Built);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Failed);
        Assert.Equal("needsFullRebuild", Assert.Single(result.Roots).Outcome);
        Assert.NotNull(new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public void Maintenance_AutomaticIncremental_UncertainFreshness_AttemptsARescanBeforeGivingUp()
    {
        // A wrapped journal is exactly the case a rescan can recover, so unprovable freshness must reach the
        // refresh rather than being failed outright as it was before.
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);
        operation.AllowFullRebuildFallback = false;
        bool refreshed = false;
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Uncertain,
            Refresh = (_, _, _, _, _, _, _, _) =>
            {
                refreshed = true;
                return IncrementalUpdateOutcome.SegmentAppended;
            },
        };

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);

        Assert.True(refreshed);
        Assert.Equal(1, result.Built);
        Assert.Equal(0, result.Failed);
        Assert.Equal(IndexMaintenanceActions.DeltaAppended, Assert.Single(result.Roots).Action);
    }

    [Fact]
    public void Maintenance_AutomaticIncremental_UncertainFreshness_UnrecoverableByRescan_StillNeedsFullRebuild()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);
        operation.AllowFullRebuildFallback = false;
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Uncertain,
            Refresh = static (_, _, _, _, _, _, _, _) => IncrementalUpdateOutcome.NeedsFullRebuild,
        };

        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, runtime);

        Assert.Equal(0, result.Built);
        Assert.Equal(1, result.Failed);
        Assert.Equal("needsFullRebuild", Assert.Single(result.Roots).Outcome);
        Assert.NotNull(new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root)).TryOpenCurrent());
    }

    [Fact]
    public void Maintenance_Incremental_CoversCompactReanchorSkipMissingBuildAndFailures()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);

        var compact = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Fresh,
            Compact = static (_, _, _, _, _) => true,
        };
        Assert.Equal(IndexMaintenanceActions.Compacted, Assert.Single(
            IndexBuildExecutor.RunMaintenancePassUnderLease(mutation, operation, CancellationToken.None, null, compact).Roots).Action);

        var reanchor = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Fresh,
            Compact = static (_, _, _, _, _) => false,
            Reanchor = static (_, _, _, _, _) => true,
        };
        Assert.Equal(IndexMaintenanceActions.Reanchored, Assert.Single(
            IndexBuildExecutor.RunMaintenancePassUnderLease(mutation, operation, CancellationToken.None, null, reanchor).Roots).Action);

        var skip = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Fresh,
            Compact = static (_, _, _, _, _) => false,
            Reanchor = static (_, _, _, _, _) => false,
        };
        Assert.Equal(IndexMaintenanceActions.Skipped, Assert.Single(
            IndexBuildExecutor.RunMaintenancePassUnderLease(mutation, operation, CancellationToken.None, null, skip).Roots).Action);

        string secondRoot = Path.Combine(_sandbox, "second");
        Directory.CreateDirectory(secondRoot);
        File.WriteAllText(Path.Combine(secondRoot, "b.txt"), "planner second root");
        operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, secondRoot);
        Assert.Equal(IndexMaintenanceActions.Built, Assert.Single(
            IndexBuildExecutor.RunMaintenancePassUnderLease(mutation, operation, CancellationToken.None, null, skip).Roots).Action);

        operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, Path.Combine(_sandbox, "absent"));
        Assert.Equal("dirNotFound", Assert.Single(
            IndexBuildExecutor.RunMaintenancePassUnderLease(mutation, operation, CancellationToken.None, null, skip).Roots).Outcome);

        operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);
        var error = new IndexMaintenanceRuntime { CheckFreshness = static (_, _, _) => throw new IOException("boom") };
        IndexMaintenanceRootResult failed = Assert.Single(
            IndexBuildExecutor.RunMaintenancePassUnderLease(mutation, operation, CancellationToken.None, null, error).Roots);
        Assert.Equal(IndexMaintenanceActions.Failed, failed.Action);
        Assert.Contains("boom", failed.Warning);
    }

    [Fact]
    public void Maintenance_CancellationPropagates()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation,
            Maintenance(IndexMaintenanceOperation.ModeBuildDue, _root),
            cts.Token,
            null));
    }

    [Fact]
    public void Maintenance_OperationCancellationInsideRootPropagates()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => throw new OperationCanceledException(),
        };
        Assert.Throws<OperationCanceledException>(() => IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation,
            Maintenance(IndexMaintenanceOperation.ModeIncremental, _root),
            CancellationToken.None,
            null,
            runtime));
    }

    [Fact]
    public void Maintenance_IncrementalRefresh_AllowsNoProgressObserver()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var runtime = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => ContentIndexManager.ScopeFreshnessState.Dirty,
            Refresh = static (_, _, _, _, _, _, _, report) =>
            {
                Assert.NotNull(report);
                report!(10, IndexUpdateStages.Resolving); // adapter's outer progress observer is null; must remain a safe no-op
                return IncrementalUpdateOutcome.NoChanges;
            },
        };
        IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation,
            Maintenance(IndexMaintenanceOperation.ModeIncremental, _root),
            CancellationToken.None,
            null,
            runtime);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Maintenance_CorruptScope_HonorsAutoRepairSetting()
    {
        BuildInitialIndex();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        store.TryOpenCurrent(out string? generationDirectory);
        File.WriteAllBytes(Path.Combine(generationDirectory!, ContentIndexGenerationSerializer.ManifestFile), new byte[] { 1, 2, 3 });
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeBuildDue, _root);
        operation.Settings.AutoRepair = false;
        IndexMaintenanceSuccess reportOnly = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null);
        IndexMaintenanceRootResult failure = Assert.Single(reportOnly.Roots);
        Assert.Equal("corrupt", failure.Outcome);
        Assert.Equal(1, reportOnly.Failed);

        operation.Settings.AutoRepair = true;
        IndexMaintenanceSuccess repaired = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null);
        Assert.Equal(1, repaired.Built);
        Assert.True(new ContentIndexManager(_paths).GetMetadataStatusForRoot(_root).MetadataReadable);
    }

    [Fact]
    public void Maintenance_IsolatesUnexpectedRootFailuresButPropagatesFatalAndTypedStops()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        IndexMaintenanceOperation operation = Maintenance(IndexMaintenanceOperation.ModeIncremental, _root);

        var unexpected = new IndexMaintenanceRuntime
        {
            CheckFreshness = static (_, _, _) => throw new NullReferenceException("unexpected"),
        };
        IndexMaintenanceSuccess isolated = IndexBuildExecutor.RunMaintenancePassUnderLease(
            mutation, operation, CancellationToken.None, null, unexpected);
        Assert.Equal(1, isolated.Failed);
        Assert.Contains("unexpected", Assert.Single(isolated.Roots).Warning);

        foreach (Exception exception in new Exception[]
        {
            new OutOfMemoryException("oom"),
            new IndexDiskFullException("C:", 99, 90),
            new IndexWriteBusyException(_indexRoot),
        })
        {
            var fatal = new IndexMaintenanceRuntime
            {
                CheckFreshness = (_, _, _) => throw exception,
            };
            Exception thrown = Assert.Throws(exception.GetType(), () => IndexBuildExecutor.RunMaintenancePassUnderLease(
                mutation, operation, CancellationToken.None, null, fatal));
            Assert.Same(exception, thrown);
        }
    }

    [Fact]
    public void ValidateScope_ReportsValidAndCorruptIndexes()
    {
        BuildInitialIndex();
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var operation = new IndexValidationOperation { StorageDirectory = _indexRoot, Root = _root };
        Assert.True(IndexBuildExecutor.ValidateScope(mutation, operation, CancellationToken.None).Valid);

        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        store.TryOpenCurrent(out string? generationDirectory);
        File.WriteAllBytes(Path.Combine(generationDirectory!, ContentIndexGenerationSerializer.ContentFile), new byte[] { 1 });
        Assert.False(IndexBuildExecutor.ValidateScope(mutation, operation, CancellationToken.None).Valid);
    }

    [Fact]
    public void RuntimeHelpers_FailSafeOnMissingInputs()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var manager = new ContentIndexManager(_paths);
        Assert.False(IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, _paths, 2, _root));
        Assert.Null(IndexBuildExecutor.ReadBytesSafe(Path.Combine(_sandbox, "missing.txt")));
        Assert.NotNull(IndexBuildExecutor.ReadBytesSafe(Path.Combine(_root, "a.txt")));

        var runtime = new IndexMaintenanceRuntime();
        Assert.False(runtime.Reanchor(mutation, manager, _paths, 2, _root));
        Assert.Equal(IncrementalUpdateOutcome.NeedsFullRebuild, runtime.Refresh(
            mutation,
            _paths,
            2,
            _root,
            new IndexIngestionPolicy(0, null, null, true, false, 0),
            new IndexMaintenanceSettings(),
            CancellationToken.None,
            null));
    }

    [Fact]
    public void ProactiveReanchor_CoversHealthyNearWrapPurgedAndJournalMismatch()
    {
        BuildInitialIndex();
        var store = new ContentIndexStore(_paths, ContentIndexManager.ScopeIdForRoot(_root));
        var inputs = Assert.IsType<(IndexManifest Manifest, FileIdMap FileIds)>(store.TryReadCurrentFreshnessInputs());
        UsnCheckpoint checkpoint = inputs.Manifest.FreshnessCheckpoint;
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var manager = new ContentIndexManager(_paths);

        bool reanchorCalled = false;
        bool Reanchor(IndexMutationContext _, ContentIndexManager __, string ___)
        {
            reanchorCalled = true;
            return true;
        }

        Assert.False(IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, _paths, 2, _root,
            _ => new UsnJournalInfo(checkpoint.JournalId, checkpoint.NextUsn - 100, checkpoint.NextUsn + 1, 0),
            Reanchor));
        Assert.False(reanchorCalled);

        Assert.True(IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, _paths, 2, _root,
            _ => new UsnJournalInfo(checkpoint.JournalId, checkpoint.NextUsn - 1, checkpoint.NextUsn + 100, 0),
            Reanchor));
        Assert.True(reanchorCalled);

        reanchorCalled = false;
        Assert.False(IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, _paths, 2, _root,
            _ => new UsnJournalInfo(checkpoint.JournalId, checkpoint.NextUsn + 1, checkpoint.NextUsn + 100, 0),
            Reanchor));
        Assert.False(reanchorCalled);

        Assert.False(IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, _paths, 2, _root,
            _ => new UsnJournalInfo(checkpoint.JournalId + 1, checkpoint.NextUsn, checkpoint.NextUsn + 100, 0),
            Reanchor));
        Assert.False(IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, _paths, 2, _root, _ => null, Reanchor));
    }

    private void BuildInitialIndex()
    {
        var coordinator = new IndexBuildCoordinator();
        coordinator.BuildFullScopePreferWorkerAsync(
            BuildOperation(), useWorker: false, CancellationToken.None).GetAwaiter().GetResult();
    }

    private IndexBuildOperation BuildOperation() => new()
    {
        StorageDirectory = _indexRoot,
        Root = _root,
        Policy = new IndexIngestionPolicySnapshot { IncludeHiddenFiles = true },
        BuildMemoryBudgetMB = 64,
    };

    private IndexMaintenanceOperation Maintenance(string mode, string root) => new()
    {
        StorageDirectory = _indexRoot,
        Mode = mode,
        Settings = new IndexMaintenanceSettings { BuildMemoryBudgetMB = 64 },
        Roots = new[]
        {
            new IndexMaintenanceRootOperation
            {
                Root = root,
                Policy = new IndexIngestionPolicySnapshot { IncludeHiddenFiles = true },
            },
        },
    };
}
