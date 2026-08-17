using System.Text.Json;

namespace Yagu.Services.Index;

/// <summary>App-only process coordinator for explicit builds, automatic maintenance, and full validation.
/// It owns the strict fallback boundary: only worker unavailability before acceptance may retry once
/// in-process. Once a worker accepts memory-heavy work, a crash is surfaced rather than repeating the same
/// workload inside Yagu and defeating process fault/OOM isolation. Busy, typed failures, and cancellation
/// never fall back.</summary>
internal sealed class IndexBuildCoordinator
{
    private readonly Func<IndexMaintenanceWorkerClient> _clientFactory;

    public IndexBuildCoordinator()
        : this(static () => new IndexMaintenanceWorkerClient())
    {
    }

    internal IndexBuildCoordinator(Func<IndexMaintenanceWorkerClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    }

    public Task<IndexBuildSuccess> BuildFullScopePreferWorkerAsync(
        IndexBuildOperation operation,
        bool useWorker,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress = null,
        Action<PdfBuildProgress>? pdfProgress = null,
        Action<ImageOcrBuildProgress>? imageOcrProgress = null,
        Action<int>? postBuildCatchUpProgress = null)
    {
        IndexOperationValidator.Validate(operation);
        return useWorker
            ? BuildWithWorkerAsync(operation, cancellationToken, progress, pdfProgress, imageOcrProgress, postBuildCatchUpProgress)
            : RunBuildFallbackAsync(operation, cancellationToken, progress, pdfProgress, imageOcrProgress, postBuildCatchUpProgress);
    }

    public Task<IndexMaintenanceSuccess> RunMaintenancePreferWorkerAsync(
        IndexMaintenanceOperation operation,
        bool useWorker,
        CancellationToken cancellationToken,
        Action<string, int, string>? progress = null)
    {
        IndexOperationValidator.Validate(operation);
        return useWorker
            ? MaintenanceWithWorkerAsync(operation, cancellationToken, progress)
            : RunMaintenanceFallbackAsync(operation, cancellationToken, progress);
    }

    public Task<IndexValidationResult> ValidatePreferWorkerAsync(
        IndexValidationOperation operation,
        bool useWorker,
        CancellationToken cancellationToken)
    {
        IndexOperationValidator.Validate(operation);
        return useWorker
            ? ValidateWithWorkerAsync(operation, cancellationToken)
            : RunValidationFallbackAsync(operation, cancellationToken);
    }

    private async Task<IndexBuildSuccess> BuildWithWorkerAsync(
        IndexBuildOperation operation,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress,
        Action<PdfBuildProgress>? pdfProgress,
        Action<ImageOcrBuildProgress>? imageOcrProgress,
        Action<int>? postBuildCatchUpProgress)
    {
        string json = JsonSerializer.Serialize(operation, IndexOperationJsonContext.Default.IndexBuildOperation);
        await using IndexMaintenanceWorkerClient client = _clientFactory();
        IndexMaintenanceWorkerResult worker = await client.ExecuteAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.BuildScope, OperationJson = json },
            message =>
            {
                if (message.ProgressStage == IndexBuildStages.Pdf)
                    pdfProgress?.Invoke(new PdfBuildProgress(Math.Max(0, message.Percent - 90), 5));
                else if (message.ProgressStage == IndexBuildStages.Ocr)
                    imageOcrProgress?.Invoke(new ImageOcrBuildProgress(Math.Max(0, message.Percent - 95), 4));
                else if (message.ProgressStage == IndexBuildStages.PostBuildCatchUp)
                    postBuildCatchUpProgress?.Invoke(message.Percent);
                else
                    progress?.Invoke(new IndexBuildProgress(message.BytesCrawled, message.FilesCrawled));
            },
            cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        if (ShouldFallback(worker))
            return await RunBuildFallbackAsync(
                operation,
                cancellationToken,
                progress,
                pdfProgress,
                imageOcrProgress,
                postBuildCatchUpProgress).ConfigureAwait(false);
        IndexWorkerMessage terminal = RequireTerminal(worker);
        ThrowIfFailed(terminal, cancellationToken, operation.StorageDirectory);
        return new IndexBuildSuccess(
            terminal.ScopeId ?? ContentIndexManager.ScopeIdForRoot(operation.Root),
            terminal.ActiveBaseGenerationId ?? "",
            terminal.ActivePointerSequence,
            terminal.LastPublishedArtifactId ?? "",
            terminal.Summary ?? "",
            terminal.IndexedCount,
            terminal.SkippedCount,
            terminal.PdfStatus,
            terminal.PdfsSeen,
            terminal.PdfAdmitted,
            terminal.PdfDeterminism,
            terminal.ImageOcrStatus,
            terminal.ImagesSeen,
            terminal.ImagesAdmitted,
            terminal.ImagesFailed,
            new PostBuildCatchUpResult(
                terminal.PostBuildCatchUpChecked,
                terminal.PostBuildCatchUpThresholdChanges,
                terminal.PostBuildCatchUpChecked
                    ? ParseIncrementalOutcome(terminal.PostBuildCatchUpOutcome)
                    : IncrementalUpdateOutcome.NoChanges,
                terminal.PostBuildCatchUpJournalChangeCount,
                terminal.PostBuildCatchUpChangeCountComplete,
                terminal.PostBuildCatchUpThresholdExceeded));
    }

    private async Task<IndexMaintenanceSuccess> MaintenanceWithWorkerAsync(
        IndexMaintenanceOperation operation,
        CancellationToken cancellationToken,
        Action<string, int, string>? progress)
    {
        string json = JsonSerializer.Serialize(operation, IndexOperationJsonContext.Default.IndexMaintenanceOperation);
        await using IndexMaintenanceWorkerClient client = _clientFactory();
        IndexMaintenanceWorkerResult worker = await client.ExecuteAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.RefreshAuto, OperationJson = json },
            message => progress?.Invoke(message.ProgressRoot ?? "", message.Percent, message.ProgressStage ?? ""),
            cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        if (ShouldFallback(worker))
            return await RunMaintenanceFallbackAsync(operation, cancellationToken, progress).ConfigureAwait(false);
        IndexWorkerMessage terminal = RequireTerminal(worker);
        ThrowIfFailed(terminal, cancellationToken, operation.StorageDirectory);
        IndexMaintenanceResultEnvelope? envelope = string.IsNullOrWhiteSpace(terminal.MaintenanceResultJson)
            ? null
            : JsonSerializer.Deserialize(terminal.MaintenanceResultJson, IndexOperationJsonContext.Default.IndexMaintenanceResultEnvelope);
        return new IndexMaintenanceSuccess(
            terminal.Built,
            terminal.SkippedRoots,
            terminal.Failed,
            envelope is null ? Array.Empty<IndexMaintenanceRootResult>() : envelope.Roots);
    }

    private async Task<IndexValidationResult> ValidateWithWorkerAsync(
        IndexValidationOperation operation,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(operation, IndexOperationJsonContext.Default.IndexValidationOperation);
        await using IndexMaintenanceWorkerClient client = _clientFactory();
        IndexMaintenanceWorkerResult worker = await client.ExecuteAsync(
            new IndexWorkerRequest { Op = IndexWorkerProtocol.Ops.ValidateScope, OperationJson = json },
            null,
            cancellationToken).ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        if (ShouldFallback(worker))
            return await RunValidationFallbackAsync(operation, cancellationToken).ConfigureAwait(false);
        IndexWorkerMessage terminal = RequireTerminal(worker);
        ThrowIfFailed(terminal, cancellationToken, operation.StorageDirectory);
        return new IndexValidationResult(
            terminal.Valid,
            terminal.FailureReason,
            terminal.DocumentCount,
            terminal.SegmentCount,
            terminal.RootPath);
    }

    internal static bool ShouldFallback(IndexMaintenanceWorkerResult result)
        => !result.WorkerStarted || (!result.Accepted && result.Terminal is null && result.WorkerExited);

    internal static IndexWorkerMessage RequireTerminal(IndexMaintenanceWorkerResult result)
        => result.Terminal ?? throw new IOException(result.Failure ?? "maintenance worker failed without a terminal result");

    internal static void ThrowIfFailed(IndexWorkerMessage terminal, CancellationToken cancellationToken, string storageDirectory)
    {
        if (terminal.Ok && terminal.OutcomeKind is null or IndexWorkerProtocol.OutcomeKinds.Ok)
            return;
        switch (terminal.OutcomeKind)
        {
            case IndexWorkerProtocol.OutcomeKinds.Cancelled:
                throw new OperationCanceledException(terminal.Error ?? "Index operation cancelled.", cancellationToken);
            case IndexWorkerProtocol.OutcomeKinds.DiskFull:
                throw new IndexDiskFullException(
                    terminal.DriveName ?? Path.GetPathRoot(storageDirectory)!,
                    terminal.UsedPercent,
                    terminal.ThresholdPercent);
            case IndexWorkerProtocol.OutcomeKinds.DirectoryNotFound:
                throw new DirectoryNotFoundException(terminal.Error);
            case IndexWorkerProtocol.OutcomeKinds.Busy:
                throw new IndexWriteBusyException(storageDirectory);
            default:
                throw new InvalidDataException(terminal.Error ?? "Index maintenance worker failed.");
        }
    }

    private static IncrementalUpdateOutcome ParseIncrementalOutcome(string? value)
    {
        if (Enum.TryParse(value, ignoreCase: false, out IncrementalUpdateOutcome outcome)
            && Enum.IsDefined(outcome))
        {
            return outcome;
        }

        throw new InvalidDataException($"The maintenance worker returned an invalid post-build catch-up outcome '{value}'.");
    }

    private static Task<IndexBuildSuccess> RunBuildFallbackAsync(
        IndexBuildOperation operation,
        CancellationToken cancellationToken,
        Action<IndexBuildProgress>? progress,
        Action<PdfBuildProgress>? pdfProgress,
        Action<ImageOcrBuildProgress>? imageOcrProgress,
        Action<int>? postBuildCatchUpProgress)
        => Task.Run(() =>
        {
            var paths = new FixedContentIndexPathProvider(operation.StorageDirectory);
            using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
            return IndexBuildExecutor.BuildFullScopeUnderLease(
                mutation, operation, cancellationToken, progress, pdfProgress,
                imageOcrProgress: imageOcrProgress,
                postBuildCatchUpProgress: postBuildCatchUpProgress);
        }, cancellationToken);

    private static Task<IndexMaintenanceSuccess> RunMaintenanceFallbackAsync(
        IndexMaintenanceOperation operation,
        CancellationToken cancellationToken,
        Action<string, int, string>? progress)
        => Task.Run(() =>
        {
            var paths = new FixedContentIndexPathProvider(operation.StorageDirectory);
            using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
            return IndexBuildExecutor.RunMaintenancePassUnderLease(
                mutation, operation, cancellationToken, progress);
        }, cancellationToken);

    private static Task<IndexValidationResult> RunValidationFallbackAsync(
        IndexValidationOperation operation,
        CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            var paths = new FixedContentIndexPathProvider(operation.StorageDirectory);
            using IndexMutationContext mutation = IndexMutationContext.Acquire(paths);
            return IndexBuildExecutor.ValidateScope(mutation, operation, cancellationToken);
        }, cancellationToken);
}
