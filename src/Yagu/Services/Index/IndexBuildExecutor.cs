using Microsoft.Extensions.Logging;
using Yagu.Services.Logging;
using Yagu.Services.Pdf;
using Yagu.Services.Ocr;

namespace Yagu.Services.Index;

internal sealed class IndexBuildRuntime
{
    public Func<IndexMutationContext, ContentIndexManager, IContentIndexPathProvider, string, IndexIngestionPolicy, CancellationToken, Action<PdfBuildProgress>?, PdfExtendedSourceBuildResult> BuildPdf { get; init; }
        = BuildProductionPdf;

    private static PdfExtendedSourceBuildResult BuildProductionPdf(
        IndexMutationContext mutation,
        ContentIndexManager manager,
        IContentIndexPathProvider stagingPaths,
        string root,
        IndexIngestionPolicy policy,
        CancellationToken cancellationToken,
        Action<PdfBuildProgress>? progress)
    {
        string? toolPath = IndexWorkerToolPaths.ResolvePdfTextToolPath();
        var extractor = new PdfTextExtractor(toolPath);
        return manager.BuildPdfExtendedSourceUnderLeaseAsync(
            mutation, root, policy, extractor, cancellationToken, progress).GetAwaiter().GetResult();
    }

    public Func<IndexMutationContext, ContentIndexManager, IContentIndexPathProvider, string, IndexIngestionPolicy, IndexBuildOperation, CancellationToken, Action<ImageOcrBuildProgress>?, ImageOcrExtendedSourceBuildResult> BuildImageOcr { get; init; }
        = BuildProductionImageOcr;

    public Func<IndexMutationContext, IContentIndexPathProvider, IndexBuildOperation, CancellationToken, Action<int>?, PostBuildCatchUpResult> RunPostBuildCatchUp { get; init; }
        = IndexMaintenanceRuntime.RunProductionPostBuildCatchUp;

    private static ImageOcrExtendedSourceBuildResult BuildProductionImageOcr(
        IndexMutationContext mutation,
        ContentIndexManager manager,
        IContentIndexPathProvider stagingPaths,
        string root,
        IndexIngestionPolicy policy,
        IndexBuildOperation operation,
        CancellationToken cancellationToken,
        Action<ImageOcrBuildProgress>? progress)
    {
        IOcrEngine engine = OcrEngineFactory.Create(
            operation.ImageOcrEngine,
            operation.ImageOcrModel,
            operation.ImageOcrMaxSide,
            operation.ImageOcrWorkerParallelism);
        try
        {
            return manager.BuildImageOcrExtendedSourceUnderLeaseAsync(
                mutation,
                root,
                policy,
                engine,
                new HashSet<string>(operation.ImageOcrExtensions, StringComparer.OrdinalIgnoreCase),
                operation.ImageOcrModel,
                operation.ImageOcrMaxSide,
                cancellationToken,
                progress).GetAwaiter().GetResult();
        }
        finally
        {
            if (engine is IAsyncDisposable asyncDisposable)
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            else if (engine is IDisposable disposable)
                disposable.Dispose();
        }
    }
}

internal sealed class IndexMaintenanceRuntime
{
    public Func<ContentIndexManager, string, int, ContentIndexManager.ScopeFreshnessState> CheckFreshness { get; init; }
        = static (manager, root, maxCatchupRecords) => manager.GetScopeFreshnessState(
            root, ContentIndexFreshnessEvaluator.CreateReader(maxCatchupRecords));

    public Func<IndexMutationContext, ContentIndexManager, string, IndexIngestionPolicy, IndexMaintenanceSettings, bool> Compact { get; init; }
        = static (mutation, manager, root, policy, settings) =>
            manager.CompactScopeIfOverSegmentedUnderLease(mutation, root, policy, settings, DateTimeOffset.UtcNow);

    public Func<IndexMutationContext, ContentIndexManager, IContentIndexPathProvider, int, string, bool> Reanchor { get; init; }
        = TryProductionReanchor;

    public Func<IndexMutationContext, IContentIndexPathProvider, int, string, IndexIngestionPolicy, IndexMaintenanceSettings, CancellationToken, Action<int>?, IncrementalUpdateOutcome> Refresh { get; init; }
        = RunProductionRefresh;

    private static bool TryProductionReanchor(
        IndexMutationContext mutation,
        ContentIndexManager manager,
        IContentIndexPathProvider paths,
        int retainedGenerations,
        string root)
        => IndexBuildExecutor.TryProactiveReanchorUnderLease(
            mutation, manager, paths, retainedGenerations, root);

    private static IncrementalUpdateOutcome RunProductionRefresh(
        IndexMutationContext mutation,
        IContentIndexPathProvider paths,
        int retainedGenerations,
        string root,
        IndexIngestionPolicy policy,
        IndexMaintenanceSettings settings,
        CancellationToken cancellationToken,
        Action<int>? progress)
        => RunProductionRefreshWithDetails(
            mutation,
            paths,
            retainedGenerations,
            root,
            policy,
            settings,
            minimumJournalChanges: null,
            cancellationToken,
            progress).Outcome;

    internal static PostBuildCatchUpResult RunProductionPostBuildCatchUp(
        IndexMutationContext mutation,
        IContentIndexPathProvider paths,
        IndexBuildOperation operation,
        CancellationToken cancellationToken,
        Action<int>? progress)
    {
        int threshold = operation.PostBuildCatchUpSettings.PostBuildCatchUpThresholdChanges;
        IncrementalRefreshResult result = RunProductionRefreshWithDetails(
            mutation,
            paths,
            operation.RetainedGenerations,
            operation.Root,
            operation.Policy.ToPolicy(),
            operation.PostBuildCatchUpSettings,
            threshold,
            cancellationToken,
            progress);
        return new PostBuildCatchUpResult(
            Checked: true,
            ThresholdChanges: threshold,
            result.Outcome,
            result.JournalChangeCount,
            result.ChangeCountComplete,
            result.ThresholdExceeded);
    }

    private static IncrementalRefreshResult RunProductionRefreshWithDetails(
        IndexMutationContext mutation,
        IContentIndexPathProvider paths,
        int retainedGenerations,
        string root,
        IndexIngestionPolicy policy,
        IndexMaintenanceSettings settings,
        int? minimumJournalChanges,
        CancellationToken cancellationToken,
        Action<int>? progress)
    {
        string scopeId = ContentIndexManager.ScopeIdForRoot(root);
        var store = new ContentIndexStore(paths, scopeId, retainedGenerations);
        store.ProduceV3QueryStructures = settings.ProduceV3QueryStructures;
        using var fileClassifier = new BoundedIncrementalFileClassifier(
            policy,
            cancellationToken,
            TimeSpan.FromSeconds(settings.FileIoTimeoutSeconds));
        TimeSpan ioTimeout = TimeSpan.FromSeconds(settings.FileIoTimeoutSeconds);
        var refresher = new ContentIndexIncrementalRefresher(
            store,
            policy,
            paths.IndexRoot,
            (journalRoot, since) => UsnJournalReader.TryCollectChangesBounded(
                journalRoot,
                since,
                ioTimeout,
                maxRecords: settings.MaxJournalCatchupRecords,
                cancellationToken: cancellationToken),
            resolverRoot => BoundedFileIdPathResolver.ForRoot(resolverRoot, ioTimeout, cancellationToken),
            fileClassifier.Read,
            FileIdentityReader.TryGetIdentity);
        return refresher.RefreshWithDetailsUnderLease(
            mutation,
            scopeId,
            settings,
            DateTimeOffset.UtcNow,
            minimumJournalChanges,
            progress,
            cancellationToken);
    }
}

/// <summary>Shared synchronous build/maintenance core used identically by the maintenance worker and by
/// the worker-disabled/unavailable in-process fallback. Process placement changes; index semantics do not.</summary>
internal static class IndexBuildExecutor
{
    internal static IndexBuildSuccess BuildFullScopeUnderLease(
        IndexMutationContext mutation,
        IndexBuildOperation operation,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress,
        Action<PdfBuildProgress>? pdfProgress,
        IndexBuildRuntime? runtime = null,
        Action<ImageOcrBuildProgress>? imageOcrProgress = null,
        Action<int>? postBuildCatchUpProgress = null)
    {
        IndexOperationValidator.Validate(operation);
        runtime ??= new IndexBuildRuntime();
        var livePaths = new FixedContentIndexPathProvider(operation.StorageDirectory);
        mutation.EnsureOwns(livePaths);
        string scopeId = ContentIndexManager.ScopeIdForRoot(operation.Root);
        using var transaction = new ContentIndexBuildTransaction(livePaths, scopeId);
        var stagedManager = new ContentIndexManager(transaction.Paths, operation.RetainedGenerations)
        {
            ProduceV3QueryStructures = operation.ProduceV3QueryStructures,
        };
        IndexIngestionPolicy policy = operation.Policy.ToPolicy();

        BuildScopeResult raw = stagedManager.BuildScopeUnderLease(
            mutation,
            operation.Root,
            policy,
            cancellationToken,
            operation.BuildMemoryBudgetMB,
            operation.MaxDiskUsagePercent,
            progress: progress,
            buildParallelism: operation.BuildParallelism,
            fileIoTimeout: TimeSpan.FromSeconds(operation.FileIoTimeoutSeconds));

        string? pdfStatus = null;
        int pdfsSeen = 0;
        int pdfAdmitted = 0;
        string? pdfDeterminism = null;
        StagedPdfCommitMode pdfMode = StagedPdfCommitMode.Preserve;
        if (operation.BuildPdfText)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PdfExtendedSourceBuildResult pdf = runtime.BuildPdf(
                    mutation, stagedManager, transaction.Paths, operation.Root, policy, cancellationToken, pdfProgress);
                pdfStatus = pdf.Status.ToString();
                pdfsSeen = pdf.PdfsSeen;
                pdfAdmitted = pdf.Admitted;
                pdfDeterminism = pdf.Determinism.ToString();
                pdfMode = pdf.Status == PdfExtendedSourceBuildStatus.Published
                    ? StagedPdfCommitMode.Replace
                    : StagedPdfCommitMode.Delete;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The complete raw staged index remains commit-worthy. Never carry an old PDF namespace
                // across a new raw build after an unexpected population failure: deleting it is the safe
                // live-extraction fallback, and the warning is returned to the caller.
                pdfStatus = "Failed: " + ex.Message;
                pdfMode = StagedPdfCommitMode.Delete;
            }
        }

        string? imageOcrStatus = null;
        int imagesSeen = 0;
        int imagesAdmitted = 0;
        int imagesFailed = 0;
        StagedPdfCommitMode imageOcrMode = StagedPdfCommitMode.Preserve;
        if (operation.BuildImageText)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ImageOcrExtendedSourceBuildResult imageOcr = runtime.BuildImageOcr(
                    mutation, stagedManager, transaction.Paths, operation.Root, policy, operation, cancellationToken, imageOcrProgress);
                imageOcrStatus = imageOcr.Status.ToString();
                imagesSeen = imageOcr.ImagesSeen;
                imagesAdmitted = imageOcr.Admitted;
                imagesFailed = imageOcr.Failed;
                imageOcrMode = imageOcr.Status == ImageOcrExtendedSourceBuildStatus.Published
                    ? StagedPdfCommitMode.Replace
                    : StagedPdfCommitMode.Delete;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                imageOcrStatus = "Failed: " + ex.Message;
                imageOcrMode = StagedPdfCommitMode.Delete;
            }
        }

        PostBuildCatchUpResult postBuildCatchUp = default;
        if (operation.PostBuildCatchUpSettings.PostBuildCatchUpThresholdChanges >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            postBuildCatchUp = runtime.RunPostBuildCatchUp(
                mutation,
                transaction.Paths,
                operation,
                cancellationToken,
                postBuildCatchUpProgress);
        }

        cancellationToken.ThrowIfCancellationRequested();
        StagedIndexCommitResult commit = transaction.Commit(
            mutation, operation.RetainedGenerations, pdfMode, imageOcrMode);
        return new IndexBuildSuccess(
            raw.ScopeId,
            commit.ActiveBaseGenerationId,
            commit.ActivePointerSequence,
            commit.LastPublishedArtifactId,
            raw.Report.Summarize(),
            raw.Report.IndexedCount,
            raw.Report.TotalSkipped,
            pdfStatus,
            pdfsSeen,
            pdfAdmitted,
            pdfDeterminism,
            imageOcrStatus,
            imagesSeen,
            imagesAdmitted,
            imagesFailed,
            postBuildCatchUp);
    }

    internal static IndexMaintenanceSuccess RunMaintenancePassUnderLease(
        IndexMutationContext mutation,
        IndexMaintenanceOperation operation,
        CancellationToken cancellationToken,
        Action<string, int, string>? progress,
        IndexMaintenanceRuntime? runtime = null)
    {
        IndexOperationValidator.Validate(operation);
        runtime ??= new IndexMaintenanceRuntime();
        var paths = new FixedContentIndexPathProvider(operation.StorageDirectory);
        mutation.EnsureOwns(paths);
        var manager = new ContentIndexManager(paths, operation.RetainedGenerations)
        {
            ProduceV3QueryStructures = operation.Settings.ProduceV3QueryStructures,
        };
        var roots = new List<IndexMaintenanceRootResult>(operation.Roots.Length);
        int built = 0;
        int skipped = 0;
        int failed = 0;

        foreach (IndexMaintenanceRootOperation rootOperation in operation.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root = rootOperation.Root;
            IndexIngestionPolicy policy = rootOperation.Policy.ToPolicy();
            try
            {
                IndexMetadataStatus metadata = manager.GetMetadataStatusForRoot(root);
                bool exists = metadata.Exists && metadata.MetadataReadable;
                bool corrupt = metadata.Exists && !metadata.MetadataReadable;
                if (corrupt && !operation.Settings.AutoRepair)
                {
                    roots.Add(new IndexMaintenanceRootResult
                    {
                        Root = root,
                        Action = IndexMaintenanceActions.Failed,
                        Outcome = "corrupt",
                        Warning = "Index metadata is corrupt or incomplete; automatic repair is disabled.",
                    });
                    failed++;
                    continue;
                }
                if (operation.Mode == IndexMaintenanceOperation.ModeBuildDue)
                {
                    ContentIndexManager.ScopeFreshnessState freshness = exists
                        ? runtime.CheckFreshness(manager, root, operation.Settings.MaxJournalCatchupRecords)
                        : ContentIndexManager.ScopeFreshnessState.Missing;
                    bool stale = exists && operation.RebuildWhenDirty
                        && freshness == ContentIndexManager.ScopeFreshnessState.Dirty;
                    if (exists && !stale)
                    {
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Skipped });
                        skipped++;
                        continue;
                    }
                    if (!Directory.Exists(root))
                    {
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Failed, Outcome = "dirNotFound" });
                        failed++;
                        continue;
                    }

                    IndexBuildSuccess success = BuildNestedFullScope(mutation, operation, rootOperation, cancellationToken, progress);
                    roots.Add(FromBuild(root, success));
                    built++;
                    continue;
                }

                ContentIndexManager.ScopeFreshnessState incrementalFreshness = operation.ForceRefresh
                    ? ContentIndexManager.ScopeFreshnessState.Dirty
                    : runtime.CheckFreshness(manager, root, operation.Settings.MaxJournalCatchupRecords);
                // Unprovable freshness is only terminal when a rescan cannot be attempted. With rescan on,
                // fall through to the refresh: it re-reads the journal, and a wrapped or over-length interval
                // recovers by sweeping per-file change USNs. Any other cause still returns needsFullRebuild.
                if (exists
                    && incrementalFreshness == ContentIndexManager.ScopeFreshnessState.Uncertain
                    && !operation.Settings.RescanOnJournalGap)
                {
                    roots.Add(new IndexMaintenanceRootResult
                    {
                        Root = root,
                        Action = IndexMaintenanceActions.Failed,
                        Outcome = "needsFullRebuild",
                        Warning = "The change journal could not prove continuity within the configured catch-up limit; the existing index was kept unchanged.",
                    });
                    failed++;
                    continue;
                }
                if (exists && incrementalFreshness == ContentIndexManager.ScopeFreshnessState.Fresh)
                {
                    if (runtime.Compact(mutation, manager, root, policy, operation.Settings))
                    {
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Compacted });
                        built++;
                        continue;
                    }
                    if (runtime.Reanchor(mutation, manager, paths, operation.RetainedGenerations, root))
                    {
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Reanchored });
                        built++;
                        continue;
                    }
                    roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Skipped });
                    skipped++;
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Failed, Outcome = "dirNotFound" });
                    failed++;
                    continue;
                }

                if (!exists)
                {
                    IndexBuildSuccess success = BuildNestedFullScope(mutation, operation, rootOperation, cancellationToken, progress);
                    roots.Add(FromBuild(root, success));
                    built++;
                    continue;
                }

                progress?.Invoke(root, -1, "incremental");
                IncrementalUpdateOutcome outcome = runtime.Refresh(
                    mutation,
                    paths,
                    operation.RetainedGenerations,
                    root,
                    policy,
                    operation.Settings,
                    cancellationToken,
                    pct => progress?.Invoke(root, pct, "incremental"));
                switch (outcome)
                {
                    case IncrementalUpdateOutcome.SegmentAppended:
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.DeltaAppended });
                        built++;
                        break;
                    case IncrementalUpdateOutcome.Compacted:
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Compacted });
                        built++;
                        break;
                    case IncrementalUpdateOutcome.NoChanges:
                        // No file changes: still do the cheap maintenance work that previously lived in
                        // the pre-refresh "fresh" branch (compaction / proactive checkpoint re-anchor).
                        if (runtime.Compact(mutation, manager, root, policy, operation.Settings))
                        {
                            roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Compacted });
                            built++;
                        }
                        else if (runtime.Reanchor(mutation, manager, paths, operation.RetainedGenerations, root))
                        {
                            roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Reanchored });
                            built++;
                        }
                        else
                        {
                            roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.Skipped });
                            skipped++;
                        }
                        break;
                    case IncrementalUpdateOutcome.SizeBudgetReached:
                        // A storage-budget halt is a deliberate stop, not a broken index, so it must never
                        // escalate into the full-rebuild fallback below.
                        roots.Add(new IndexMaintenanceRootResult { Root = root, Action = IndexMaintenanceActions.SizeBudgetReached });
                        skipped++;
                        break;
                    case IncrementalUpdateOutcome.NeedsCompatibilityRebuild when operation.AllowCompatibilityRebuild:
                    {
                        IndexBuildSuccess success = BuildNestedFullScope(
                            mutation, operation, rootOperation, cancellationToken, progress);
                        roots.Add(FromBuild(root, success));
                        built++;
                        break;
                    }
                    default:
                        if (operation.AllowFullRebuildFallback)
                        {
                            IndexBuildSuccess success = BuildNestedFullScope(mutation, operation, rootOperation, cancellationToken, progress);
                            roots.Add(FromBuild(root, success));
                            built++;
                        }
                        else
                        {
                            roots.Add(new IndexMaintenanceRootResult
                            {
                                Root = root,
                                Action = IndexMaintenanceActions.Failed,
                                Outcome = "needsFullRebuild",
                                Warning = "Incremental update could not prove journal continuity; the existing index was kept unchanged.",
                            });
                            failed++;
                        }
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or IndexDiskFullException or IndexWriteBusyException))
            {
                roots.Add(new IndexMaintenanceRootResult
                {
                    Root = root,
                    Action = IndexMaintenanceActions.Failed,
                    Outcome = "error",
                    Warning = ex.Message,
                });
                failed++;
                YaguLog.For("ContentIndex").LogWarning(ex,
                    "Index maintenance failed for root '{Root}' and was isolated; continuing with the remaining roots.", root);
            }
        }

        return new IndexMaintenanceSuccess(built, skipped, failed, roots);
    }

    internal static IndexValidationResult ValidateScope(
        IndexMutationContext mutation,
        IndexValidationOperation operation,
        CancellationToken cancellationToken)
    {
        IndexOperationValidator.Validate(operation);
        var paths = new FixedContentIndexPathProvider(operation.StorageDirectory);
        mutation.EnsureOwns(paths);
        return new ContentIndexManager(paths, operation.RetainedGenerations)
            .ValidateScopeUnderLease(mutation, operation.Root, cancellationToken);
    }

    private static IndexBuildSuccess BuildNestedFullScope(
        IndexMutationContext mutation,
        IndexMaintenanceOperation pass,
        IndexMaintenanceRootOperation root,
        CancellationToken cancellationToken,
        Action<string, int, string>? progress)
    {
        var build = new IndexBuildOperation
        {
            StorageDirectory = pass.StorageDirectory,
            RetainedGenerations = pass.RetainedGenerations,
            Root = root.Root,
            Policy = root.Policy,
            BuildMemoryBudgetMB = pass.Settings.BuildMemoryBudgetMB,
            BuildParallelism = root.BuildParallelism,
            MaxDiskUsagePercent = pass.Settings.MaxDiskUsagePercent,
            FileIoTimeoutSeconds = pass.Settings.FileIoTimeoutSeconds,
            Rebuild = false,
            BuildPdfText = pass.Settings.BuildPdfText,
            BuildImageText = pass.Settings.BuildImageText,
            ImageOcrEngine = pass.Settings.ImageOcrEngine,
            ImageOcrModel = pass.Settings.ImageOcrModel,
            ImageOcrMaxSide = pass.Settings.ImageOcrMaxSide,
            ImageOcrWorkerParallelism = pass.Settings.ImageOcrWorkerParallelism,
            ImageOcrExtensions = pass.Settings.ImageOcrExtensions,
            ProduceV3QueryStructures = pass.Settings.ProduceV3QueryStructures,
            PostBuildCatchUpSettings = pass.Settings,
        };
        long usedBytes = IndexBuildProgressEstimate.DriveUsedBytes(root.Root);
        return BuildFullScopeUnderLease(
            mutation,
            build,
            cancellationToken,
            p => progress?.Invoke(root.Root, IndexBuildProgressEstimate.Percent(p.BytesCrawled, usedBytes), "rawBuild"),
            p => progress?.Invoke(root.Root, p.Total <= 0 ? -1 : 90 + Math.Clamp(p.Processed * 5 / p.Total, 0, 5), "pdf"),
            imageOcrProgress: p => progress?.Invoke(root.Root, p.Total <= 0 ? -1 : 95 + Math.Clamp(p.Processed * 4 / p.Total, 0, 4), "ocr"),
            postBuildCatchUpProgress: p =>
                progress?.Invoke(root.Root, MapPostBuildCatchUpProgress(p), "postBuildCatchUp"));
    }

    internal static int MapPostBuildCatchUpProgress(int progress) => progress < 0 ? -1 : 99;

    internal static IndexMaintenanceRootResult FromBuild(string root, IndexBuildSuccess success) => new()
    {
        Root = root,
        Action = IndexMaintenanceActions.Built,
        IndexedCount = success.IndexedCount,
        SkippedCount = success.TotalSkipped,
        PdfStatus = success.PdfStatus,
        ImageOcrStatus = success.ImageOcrStatus,
        Warning = success.PostBuildCatchUp.NeedsAttention ? success.PostBuildCatchUp.Describe() : null,
    };

    internal static bool TryProactiveReanchorUnderLease(
        IndexMutationContext mutation,
        ContentIndexManager manager,
        IContentIndexPathProvider paths,
        int retainedGenerations,
        string root,
        Func<string, UsnJournalInfo?>? journalInfoProvider = null,
        Func<IndexMutationContext, ContentIndexManager, string, bool>? reanchor = null)
    {
        journalInfoProvider ??= UsnJournalReader.TryQueryJournalInfo;
        reanchor ??= static (heldMutation, heldManager, heldRoot) =>
            heldManager.TryReanchorFreshScopeUnderLease(heldMutation, heldRoot);
        string scopeId = ContentIndexManager.ScopeIdForRoot(root);
        var store = new ContentIndexStore(paths, scopeId, retainedGenerations);
        if (store.TryReadCurrentFreshnessInputs() is not { } inputs
            || journalInfoProvider(root) is not { } journal)
            return false;
        UsnHeadroomVerdict verdict = UsnJournalHeadroom.Evaluate(journal, inputs.Manifest.FreshnessCheckpoint);
        return verdict.ShouldRefreshSoon
            && !verdict.CheckpointPurged
            && !verdict.JournalIdMismatch
            && reanchor(mutation, manager, root);
    }

    internal static byte[]? ReadBytesSafe(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
