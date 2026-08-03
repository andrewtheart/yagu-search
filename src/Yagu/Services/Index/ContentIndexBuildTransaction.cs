namespace Yagu.Services.Index;

internal enum StagedPdfCommitMode
{
    Preserve,
    Replace,
    Delete,
}

/// <summary>
/// Private full-build workspace under <c>&lt;IndexRoot&gt;/.build-&lt;guid&gt;</c>. Paged base/segment
/// publications happen entirely inside this workspace. On success immutable artifacts are imported and
/// one live pointer slot is flipped; cancellation/failure deletes the workspace and leaves the prior live
/// index unchanged.
/// </summary>
internal sealed class ContentIndexBuildTransaction : IDisposable
{
    private readonly IContentIndexPathProvider _livePaths;
    private readonly string _scopeId;
    private readonly string _stagingRoot;
    private readonly object _lifecycleGate = new();
    private bool _committed;

    internal Action? BeforeImport { get; set; }

    public ContentIndexBuildTransaction(IContentIndexPathProvider livePaths, string scopeId)
    {
        _livePaths = livePaths ?? throw new ArgumentNullException(nameof(livePaths));
        ArgumentException.ThrowIfNullOrEmpty(scopeId);
        _scopeId = scopeId;
        _stagingRoot = Path.Combine(livePaths.IndexRoot, ".build-" + Guid.NewGuid().ToString("N"));
        Paths = new StagedPathProvider(livePaths.IndexRoot, _stagingRoot);
    }

    public IContentIndexPathProvider Paths { get; }

    public StagedIndexCommitResult Commit(
        IndexMutationContext mutation,
        int retainedGenerations,
        StagedPdfCommitMode pdfMode,
        StagedPdfCommitMode imageOcrMode = StagedPdfCommitMode.Preserve)
    {
        lock (_lifecycleGate)
            return CommitCore(mutation, retainedGenerations, pdfMode, imageOcrMode);
    }

    private StagedIndexCommitResult CommitCore(
        IndexMutationContext mutation,
        int retainedGenerations,
        StagedPdfCommitMode pdfMode,
        StagedPdfCommitMode imageOcrMode)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        mutation.EnsureOwns(_livePaths);
        if (_committed)
            throw new InvalidOperationException("The build transaction was already committed.");

        var liveStore = new ContentIndexStore(_livePaths, _scopeId, retainedGenerations);
        var stagedStore = new ContentIndexStore(Paths, _scopeId, retainedGenerations);

        var sourceTransactions = new[]
        {
            new StagedSourceTransaction(_livePaths, Paths, _scopeId, SpecialSourceKind.PdfText, pdfMode),
            new StagedSourceTransaction(_livePaths, Paths, _scopeId, SpecialSourceKind.ImageOcr, imageOcrMode),
        };

        try
        {
            foreach (StagedSourceTransaction source in sourceTransactions)
                PrepareExtendedSource(mutation, source);

            BeforeImport?.Invoke();
            IndexMutationFaults.Hit(IndexMutationFaults.BuildBeforeImport);
            StagedIndexCommitResult result = liveStore.ImportStagedUnderLease(mutation, stagedStore);
            _committed = true;
            IndexMutationFaults.Hit(IndexMutationFaults.BuildCommitted);

            foreach (StagedSourceTransaction source in sourceTransactions)
            {
                if (source.Mode == StagedPdfCommitMode.Replace
                    && ExtendedSourceStore.DeleteFileSafe(source.LiveStore.DisabledMarkerPath(source.Kind)))
                {
                    ExtendedSourceStore.DeleteFileSafe(Path.Combine(source.LiveDirectory, ExtendedSourceStore.ReplacementReadyMarkerFile));
                    if (source.Kind == SpecialSourceKind.PdfText)
                        IndexMutationFaults.Hit(IndexMutationFaults.PdfEnabled);
                }
                DeleteDirectorySafe(source.BackupDirectory);
            }
            IndexMutationFaults.Hit(IndexMutationFaults.PdfBackupDeleted);
            DeleteDirectorySafe(_stagingRoot);
            IndexMutationFaults.Hit(IndexMutationFaults.BuildWorkspaceDeleted);
            return result;
        }
        catch
        {
            // Restore a reversible replacement only before the raw pointer commit. Delete mode deliberately
            // remains disabled after failure: its durable state records that PDF pruning is no longer safe.
            if (!_committed)
            {
                foreach (StagedSourceTransaction source in sourceTransactions)
                {
                    if (source.Mode != StagedPdfCommitMode.Replace)
                        continue;
                    if (source.Installed)
                        DeleteDirectorySafe(source.LiveDirectory);
                    if (source.BackupDirectory is not null
                        && Directory.Exists(source.BackupDirectory)
                        && !Directory.Exists(source.LiveDirectory))
                    {
                        TryMoveDirectory(source.BackupDirectory, source.LiveDirectory, Directory.Move);
                    }
                }
            }
            throw;
        }
    }

    private void PrepareExtendedSource(IndexMutationContext mutation, StagedSourceTransaction source)
    {
        if (source.Mode == StagedPdfCommitMode.Preserve)
            return;
        if (source.Mode == StagedPdfCommitMode.Delete)
        {
            source.LiveStore.DeleteUnderLease(mutation, source.Kind);
            return;
        }

        string parent = Path.GetDirectoryName(source.LiveDirectory)!;
        Directory.CreateDirectory(parent);
        if (!Directory.Exists(source.StagedDirectory))
            throw new InvalidDataException($"The staged {source.Kind} namespace is missing.");
        ExtendedSourceStore.WriteMarker(Path.Combine(source.StagedDirectory, ExtendedSourceStore.ReplacementReadyMarkerFile));
        if (source.Kind == SpecialSourceKind.PdfText)
            IndexMutationFaults.Hit(IndexMutationFaults.PdfReplacementMarked);
        if (Directory.Exists(source.LiveDirectory))
        {
            source.BackupDirectory = Path.Combine(parent,
                ExtendedSourceStore.BackupPrefix + ExtendedSourceStore.KindFolder(source.Kind) + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(source.LiveDirectory, source.BackupDirectory);
            if (source.Kind == SpecialSourceKind.PdfText)
                IndexMutationFaults.Hit(IndexMutationFaults.PdfBackupMoved);
        }
        Directory.Move(source.StagedDirectory, source.LiveDirectory);
        source.Installed = true;
        if (source.Kind == SpecialSourceKind.PdfText)
            IndexMutationFaults.Hit(IndexMutationFaults.PdfReplacementInstalled);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (!_committed)
                DeleteDirectorySafe(_stagingRoot);
        }
    }

    private static void DeleteDirectorySafe(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static bool TryMoveDirectory(string source, string destination, Action<string, string> move)
    {
        ArgumentNullException.ThrowIfNull(move);
        try
        {
            move(source, destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class StagedSourceTransaction
    {
        public StagedSourceTransaction(
            IContentIndexPathProvider livePaths,
            IContentIndexPathProvider stagedPaths,
            string scopeId,
            SpecialSourceKind kind,
            StagedPdfCommitMode mode)
        {
            Kind = kind;
            Mode = mode;
            LiveStore = new ExtendedSourceStore(livePaths, scopeId);
            LiveDirectory = LiveStore.NamespaceDirectory(kind);
            StagedDirectory = new ExtendedSourceStore(stagedPaths, scopeId).NamespaceDirectory(kind);
        }

        public SpecialSourceKind Kind { get; }
        public StagedPdfCommitMode Mode { get; }
        public ExtendedSourceStore LiveStore { get; }
        public string LiveDirectory { get; }
        public string StagedDirectory { get; }
        public string? BackupDirectory { get; set; }
        public bool Installed { get; set; }
    }

    private sealed class StagedPathProvider(string indexRoot, string stagingRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;

        public string GetScopeDirectory(string scopeId)
        {
            ArgumentException.ThrowIfNullOrEmpty(scopeId);
            return Path.Combine(stagingRoot, scopeId);
        }
    }
}
