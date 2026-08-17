namespace Yagu.Services.Index;

/// <summary>
/// Named boundaries in the durable index-mutation protocol. Production leaves <see cref="OnHit"/> unset,
/// making every call a single null check. The Debug index-worker crash harness installs a fail-fast handler
/// and hard-terminates at one selected boundary; tests then reopen and recover the real on-disk state in a
/// fresh process. Keeping the seam in the persistence layer makes the crash matrix deterministic without
/// changing the commit protocol or relying on managed exceptions (which would run catch/finally cleanup).
/// </summary>
internal static class IndexMutationFaults
{
    internal const string ChecksummedBodyWritten = "checksummed.body-written";
    internal const string ChecksummedDigestWritten = "checksummed.digest-written";
    internal const string ChecksummedFlushed = "checksummed.flushed";
    internal const string V3HeaderWritten = "v3.header-written";
    internal const string V3BodyWritten = "v3.body-written";
    internal const string V3FileClosed = "v3.file-closed";
    internal const string V3Published = "v3.published";

    internal const string BaseWritten = "base.written";
    internal const string BaseValidated = "base.validated";
    internal const string BaseMarked = "base.marked";
    internal const string BasePromoted = "base.promoted";
    internal const string BasePointerPublished = "base.pointer-published";
    internal const string BaseMarkerCleared = "base.marker-cleared";
    internal const string BaseCleanupFinished = "base.cleanup-finished";

    internal const string SegmentWritten = "segment.written";
    internal const string SegmentValidated = "segment.validated";
    internal const string SegmentMarked = "segment.marked";
    internal const string SegmentPromoted = "segment.promoted";
    internal const string SegmentPointerPublished = "segment.pointer-published";
    internal const string SegmentMarkerCleared = "segment.marker-cleared";
    internal const string SegmentCleanupFinished = "segment.cleanup-finished";

    internal const string CoalesceWritten = "coalesce.written";
    internal const string CoalesceValidated = "coalesce.validated";
    internal const string CoalesceMarked = "coalesce.marked";
    internal const string CoalescePromoted = "coalesce.promoted";
    internal const string CoalescePointerPublished = "coalesce.pointer-published";
    internal const string CoalesceMarkerCleared = "coalesce.marker-cleared";
    internal const string CoalesceCleanupFinished = "coalesce.cleanup-finished";

    internal const string CompactionWorkspaceCreated = "compaction.workspace-created";
    internal const string CompactionPrepared = "compaction.prepared";

    internal const string PointerTempFlushed = "pointer.temp-flushed";
    internal const string PointerPublished = "pointer.published";
    internal const string RetentionStarted = "retention.started";

    internal const string ImportBaseMarked = "import.base-marked";
    internal const string ImportBaseMoved = "import.base-moved";
    internal const string ImportSegmentMarked = "import.segment-marked";
    internal const string ImportSegmentMoved = "import.segment-moved";
    internal const string ImportBeforePointer = "import.before-pointer";
    internal const string ImportPointerPublished = "import.pointer-published";
    internal const string ImportMarkersCleared = "import.markers-cleared";
    internal const string ImportCleanupFinished = "import.cleanup-finished";

    internal const string BuildBeforeImport = "build.before-import";
    internal const string BuildCommitted = "build.committed";
    internal const string BuildWorkspaceDeleted = "build.workspace-deleted";

    internal const string PdfDisabled = "pdf.disabled";
    internal const string PdfBackupMoved = "pdf.backup-moved";
    internal const string PdfReplacementMarked = "pdf.replacement-marked";
    internal const string PdfReplacementInstalled = "pdf.replacement-installed";
    internal const string PdfEnabled = "pdf.enabled";
    internal const string PdfBackupDeleted = "pdf.backup-deleted";

    internal const string ExtendedValidated = "extended.validated";
    internal const string ExtendedBackupMoved = "extended.backup-moved";
    internal const string ExtendedInstalled = "extended.installed";
    internal const string ExtendedEnabled = "extended.enabled";
    internal const string ExtendedBackupDeleted = "extended.backup-deleted";

    internal const string ReanchorManifestReplaced = "reanchor.manifest-replaced";
    internal const string ReanchorPointerPublished = "reanchor.pointer-published";

    internal const string RecoveryBuildWorkspaceDeleted = "recovery.build-workspace-deleted";
    internal const string RecoveryScopeReconciled = "recovery.scope-reconciled";
    internal const string RecoveryPdfRestored = "recovery.pdf-restored";
    internal const string RecoveryPdfBackupDeleted = "recovery.pdf-backup-deleted";
    internal const string RecoveryCompleted = "recovery.completed";

    private static Action<string>? s_onHit;

    /// <summary>Test/worker-only observer. It must be reset by an in-process test that installs it.</summary>
    internal static Action<string>? OnHit
    {
        get => Volatile.Read(ref s_onHit);
        set => Volatile.Write(ref s_onHit, value);
    }

    internal static void Hit(string point)
    {
        ArgumentException.ThrowIfNullOrEmpty(point);
        OnHit?.Invoke(point);
    }
}
