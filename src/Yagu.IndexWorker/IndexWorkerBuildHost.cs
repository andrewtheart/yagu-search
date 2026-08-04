using System.Text;
using System.Text.Json;
using Yagu.Services.Index;

namespace Yagu.IndexWorker;

internal static class IndexWorkerBuildHost
{
    public static (IndexMutationContext Mutation, object Operation) ValidateAndAcquire(IndexWorkerRequest request)
    {
        string operationJson = request.OperationJson ?? throw new InvalidDataException("operationJson is required.");
        if (Encoding.UTF8.GetByteCount(operationJson) > IndexBuildDefaults.MaxOperationJsonBytes)
            throw new InvalidDataException("The index operation payload is too large.");

        object operation;
        string storageDirectory;
        switch (request.Op)
        {
            case IndexWorkerProtocol.Ops.BuildScope:
                var build = JsonSerializer.Deserialize(operationJson, IndexOperationJsonContext.Default.IndexBuildOperation)
                    ?? throw new InvalidDataException("The build operation is empty.");
                IndexOperationValidator.Validate(build);
                operation = build;
                storageDirectory = build.StorageDirectory;
                break;
            case IndexWorkerProtocol.Ops.RefreshAuto:
                var maintenance = JsonSerializer.Deserialize(operationJson, IndexOperationJsonContext.Default.IndexMaintenanceOperation)
                    ?? throw new InvalidDataException("The maintenance operation is empty.");
                IndexOperationValidator.Validate(maintenance);
                operation = maintenance;
                storageDirectory = maintenance.StorageDirectory;
                break;
            case IndexWorkerProtocol.Ops.ValidateScope:
                var validation = JsonSerializer.Deserialize(operationJson, IndexOperationJsonContext.Default.IndexValidationOperation)
                    ?? throw new InvalidDataException("The validation operation is empty.");
                IndexOperationValidator.Validate(validation);
                operation = validation;
                storageDirectory = validation.StorageDirectory;
                break;
            default:
                throw new InvalidDataException($"Unsupported maintenance op '{request.Op}'.");
        }

        var paths = new FixedContentIndexPathProvider(storageDirectory);
        if (!IndexMutationContext.TryAcquire(paths, out IndexMutationContext? mutation))
            throw new IndexWriteBusyException(paths.IndexRoot);
        return (mutation!, operation);
    }

    public static IndexWorkerMessage Execute(
        IndexWorkerRequest request,
        IndexMutationContext mutation,
        object operation,
        CancellationToken cancellationToken,
        Action<IndexWorkerMessage> send)
    {
        switch (operation)
        {
            case IndexBuildOperation build:
            {
                long usedBytes = IndexBuildProgressEstimate.DriveUsedBytes(build.Root);
                IndexBuildSuccess result = IndexBuildExecutor.BuildFullScopeUnderLease(
                    mutation,
                    build,
                    cancellationToken,
                    progress => send(new IndexWorkerMessage
                    {
                        Type = IndexWorkerProtocol.MessageTypes.Progress,
                        Id = request.Id,
                        BytesCrawled = progress.BytesCrawled,
                        FilesCrawled = progress.FilesCrawled,
                        Percent = IndexBuildProgressEstimate.Percent(progress.BytesCrawled, usedBytes),
                        ProgressRoot = build.Root,
                        ProgressStage = "rawBuild",
                    }),
                    progress => send(new IndexWorkerMessage
                    {
                        Type = IndexWorkerProtocol.MessageTypes.Progress,
                        Id = request.Id,
                        Percent = progress.Total <= 0 ? -1 : 90 + Math.Clamp(progress.Processed * 5 / progress.Total, 0, 5),
                        ProgressRoot = build.Root,
                        ProgressStage = "pdf",
                    }),
                    imageOcrProgress: progress => send(new IndexWorkerMessage
                    {
                        Type = IndexWorkerProtocol.MessageTypes.Progress,
                        Id = request.Id,
                        Percent = progress.Total <= 0 ? -1 : 95 + Math.Clamp(progress.Processed * 4 / progress.Total, 0, 4),
                        ProgressRoot = build.Root,
                        ProgressStage = "ocr",
                    }),
                    postBuildCatchUpProgress: progress => send(new IndexWorkerMessage
                    {
                        Type = IndexWorkerProtocol.MessageTypes.Progress,
                        Id = request.Id,
                        Percent = progress < 0 ? -1 : 99,
                        ProgressRoot = build.Root,
                        ProgressStage = "postBuildCatchUp",
                    }));
                return BuildResult(request.Id, result);
            }
            case IndexMaintenanceOperation maintenance:
            {
                IndexMaintenanceSuccess result = IndexBuildExecutor.RunMaintenancePassUnderLease(
                    mutation,
                    maintenance,
                    cancellationToken,
                    (root, percent, stage) => send(new IndexWorkerMessage
                    {
                        Type = IndexWorkerProtocol.MessageTypes.Progress,
                        Id = request.Id,
                        Percent = percent,
                        ProgressRoot = root,
                        ProgressStage = stage,
                    }));
                string json = JsonSerializer.Serialize(
                    new IndexMaintenanceResultEnvelope { Roots = new List<IndexMaintenanceRootResult>(result.Roots) },
                    IndexOperationJsonContext.Default.IndexMaintenanceResultEnvelope);
                return new IndexWorkerMessage
                {
                    Type = IndexWorkerProtocol.MessageTypes.Result,
                    Id = request.Id,
                    Ok = true,
                    OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Ok,
                    Built = result.Built,
                    SkippedRoots = result.Skipped,
                    Failed = result.Failed,
                    MaintenanceResultJson = json,
                };
            }
            case IndexValidationOperation validation:
            {
                IndexValidationResult result = IndexBuildExecutor.ValidateScope(mutation, validation, cancellationToken);
                return new IndexWorkerMessage
                {
                    Type = IndexWorkerProtocol.MessageTypes.Result,
                    Id = request.Id,
                    Ok = true,
                    OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Ok,
                    Valid = result.Valid,
                    FailureReason = result.FailureReason,
                    DocumentCount = result.DocumentCount,
                    SegmentCount = result.SegmentCount,
                    RootPath = result.RootPath,
                };
            }
            default:
                throw new InvalidDataException("Unknown validated operation type.");
        }
    }

    public static IndexWorkerMessage MapFailure(int id, Exception exception)
    {
        return exception switch
        {
            OperationCanceledException => Failure(id, IndexWorkerProtocol.OutcomeKinds.Cancelled, "cancelled"),
            IndexWriteBusyException => Failure(id, IndexWorkerProtocol.OutcomeKinds.Busy, exception.Message),
            IndexDiskFullException disk => new IndexWorkerMessage
            {
                Type = IndexWorkerProtocol.MessageTypes.Result,
                Id = id,
                Ok = false,
                OutcomeKind = IndexWorkerProtocol.OutcomeKinds.DiskFull,
                Error = disk.Message,
                DriveName = disk.DriveDisplayName,
                UsedPercent = disk.UsedPercent,
                ThresholdPercent = disk.ThresholdPercent,
            },
            DirectoryNotFoundException => Failure(id, IndexWorkerProtocol.OutcomeKinds.DirectoryNotFound, exception.Message),
            _ => Failure(id, IndexWorkerProtocol.OutcomeKinds.Error, exception.Message),
        };
    }

    private static IndexWorkerMessage BuildResult(int id, IndexBuildSuccess result) => new()
    {
        Type = IndexWorkerProtocol.MessageTypes.Result,
        Id = id,
        Ok = true,
        OutcomeKind = IndexWorkerProtocol.OutcomeKinds.Ok,
        ScopeId = result.ScopeId,
        ActiveBaseGenerationId = result.ActiveBaseGenerationId,
        ActivePointerSequence = result.ActivePointerSequence,
        LastPublishedArtifactId = result.LastPublishedArtifactId,
        Summary = result.Summary,
        IndexedCount = result.IndexedCount,
        SkippedCount = result.TotalSkipped,
        PdfStatus = result.PdfStatus,
        PdfsSeen = result.PdfsSeen,
        PdfAdmitted = result.PdfAdmitted,
        PdfDeterminism = result.PdfDeterminism,
        ImageOcrStatus = result.ImageOcrStatus,
        ImagesSeen = result.ImagesSeen,
        ImagesAdmitted = result.ImagesAdmitted,
        ImagesFailed = result.ImagesFailed,
        PostBuildCatchUpChecked = result.PostBuildCatchUp.Checked,
        PostBuildCatchUpThresholdChanges = result.PostBuildCatchUp.ThresholdChanges,
        PostBuildCatchUpOutcome = result.PostBuildCatchUp.Outcome.ToString(),
        PostBuildCatchUpJournalChangeCount = result.PostBuildCatchUp.JournalChangeCount,
        PostBuildCatchUpChangeCountComplete = result.PostBuildCatchUp.ChangeCountComplete,
        PostBuildCatchUpThresholdExceeded = result.PostBuildCatchUp.ThresholdExceeded,
    };

    private static IndexWorkerMessage Failure(int id, string outcome, string error) => new()
    {
        Type = IndexWorkerProtocol.MessageTypes.Result,
        Id = id,
        Ok = false,
        OutcomeKind = outcome,
        Error = error,
    };
}
