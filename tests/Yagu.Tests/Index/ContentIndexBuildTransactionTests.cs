using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class ContentIndexBuildTransactionTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-build-transaction", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _indexRoot;
    private readonly FixedContentIndexPathProvider _paths;
    private readonly string _scopeId;

    public ContentIndexBuildTransactionTests()
    {
        _root = Path.Combine(_sandbox, "root");
        _indexRoot = Path.Combine(_sandbox, "index");
        _paths = new FixedContentIndexPathProvider(_indexRoot);
        _scopeId = ContentIndexManager.ScopeIdForRoot(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "a.txt"), "planner transaction document");
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void Commit_Preserve_ImportsRawArtifactsAndRejectsSecondCommit()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);

        StagedIndexCommitResult result = transaction.Commit(mutation, 2, StagedPdfCommitMode.Preserve);

        Assert.Equal("gen-000001", result.ActiveBaseGenerationId);
        var store = new ContentIndexStore(_paths, _scopeId);
        Assert.NotNull(store.TryOpenCurrent(out string? generationDirectory));
        Assert.False(File.Exists(Path.Combine(generationDirectory!, ContentIndexStore.ImportMarkerFile)));
        Assert.Throws<InvalidOperationException>(() => transaction.Commit(mutation, 2, StagedPdfCommitMode.Preserve));
    }

    [Fact]
    public void ImportStaged_FallsBackFromCorruptNewestBase()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        var staged = new ContentIndexStore(transaction.Paths, _scopeId);
        ContentIndexGeneration first = staged.TryOpenCurrent()!;
        staged.PublishUnderLease(mutation, first);
        File.WriteAllBytes(
            Path.Combine(
                staged.ScopeDirectory,
                "generations",
                "gen-000002",
                ContentIndexGenerationSerializer.ManifestFile),
            new byte[] { 1, 2, 3 });

        var live = new ContentIndexStore(_paths, _scopeId);
        StagedIndexCommitResult result = live.ImportStagedUnderLease(mutation, staged);

        Assert.Equal("gen-000001", result.ActiveBaseGenerationId);
        Assert.NotNull(live.TryOpenCurrent());
    }

    [Fact]
    public void ImportStaged_WithSegment_ImportsEveryLayerAndClearsMarkers()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        var staged = new ContentIndexStore(transaction.Paths, _scopeId);
        ContentIndexGeneration baseGeneration = staged.TryOpenCurrent()!;
        staged.PublishSegmentUnderLease(
            mutation,
            new ContentIndexDeltaSegment(baseGeneration, Array.Empty<string>()));

        var live = new ContentIndexStore(_paths, _scopeId);
        StagedIndexCommitResult result = live.ImportStagedUnderLease(mutation, staged);

        Assert.Equal("seg-000001", result.LastPublishedArtifactId);
        ContentIndexStore.LayeredIndexHandle layered = live.TryOpenLayered()!;
        Assert.Single(layered.Segments);
        Assert.False(File.Exists(Path.Combine(layered.BaseDir, ContentIndexStore.ImportMarkerFile)));
        Assert.False(File.Exists(Path.Combine(layered.SegmentDirs[0], ContentIndexStore.ImportMarkerFile)));
    }

    [Fact]
    public void ImportStaged_MissingOrCrossVolumeSegmentManifest_RejectsTheWholeLayerSet()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        using (var transaction = CreateBuiltTransaction(mutation))
        {
            var staged = new ContentIndexStore(transaction.Paths, _scopeId);
            ContentIndexGeneration baseGeneration = staged.TryOpenCurrent()!;
            staged.PublishSegmentUnderLease(
                mutation,
                new ContentIndexDeltaSegment(baseGeneration, Array.Empty<string>()));
            staged.PublishSegmentUnderLease(
                mutation,
                new ContentIndexDeltaSegment(baseGeneration, Array.Empty<string>()));
            File.Delete(Path.Combine(
                staged.ScopeDirectory,
                "segments",
                "seg-000001",
                ContentIndexGenerationSerializer.ManifestFile));

            Assert.Throws<InvalidDataException>(() =>
                new ContentIndexStore(_paths, _scopeId).ImportStagedUnderLease(mutation, staged));
        }

        using (var transaction = CreateBuiltTransaction(mutation))
        {
            var staged = new ContentIndexStore(transaction.Paths, _scopeId);
            ContentIndexGeneration baseGeneration = staged.TryOpenCurrent()!;
            staged.PublishSegmentUnderLease(
                mutation,
                new ContentIndexDeltaSegment(baseGeneration, Array.Empty<string>()));
            staged.PublishSegmentUnderLease(
                mutation,
                new ContentIndexDeltaSegment(baseGeneration, Array.Empty<string>()));
            string manifestPath = Path.Combine(
                staged.ScopeDirectory,
                "segments",
                "seg-000001",
                ContentIndexGenerationSerializer.ManifestFile);
            IndexManifest mismatched = baseGeneration.Manifest with
            {
                VolumeSerialNumber = baseGeneration.Manifest.VolumeSerialNumber + 1,
            };
            ChecksummedFile.Write(manifestPath, System.Text.Encoding.UTF8.GetBytes(mismatched.Serialize()));

            Assert.Throws<InvalidDataException>(() =>
                new ContentIndexStore(_paths, _scopeId).ImportStagedUnderLease(mutation, staged));
        }
    }

    [Fact]
    public void ImportStaged_BaseAndSegmentDestinationCollisions_DoNotPublish()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        using (var transaction = CreateBuiltTransaction(mutation))
        {
            var staged = new ContentIndexStore(transaction.Paths, _scopeId);
            var live = new ContentIndexStore(_paths, _scopeId)
            {
                BeforeImportDestinationCheck = destination => Directory.CreateDirectory(destination),
            };

            Assert.Throws<IOException>(() => live.ImportStagedUnderLease(mutation, staged));
            Assert.Null(live.TryOpenCurrent());
        }

        Directory.Delete(_paths.GetScopeDirectory(_scopeId), recursive: true);
        using (var transaction = CreateBuiltTransaction(mutation))
        {
            var staged = new ContentIndexStore(transaction.Paths, _scopeId);
            ContentIndexGeneration baseGeneration = staged.TryOpenCurrent()!;
            staged.PublishSegmentUnderLease(
                mutation,
                new ContentIndexDeltaSegment(baseGeneration, Array.Empty<string>()));
            var live = new ContentIndexStore(_paths, _scopeId)
            {
                BeforeImportDestinationCheck = destination =>
                {
                    if (string.Equals(
                            Path.GetFileName(Path.GetDirectoryName(destination)),
                            "segments",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(destination);
                    }
                },
            };

            Assert.Throws<IOException>(() => live.ImportStagedUnderLease(mutation, staged));
            Assert.Null(live.TryOpenCurrent());
        }
    }

    [Fact]
    public void ImportStaged_UnavailableOrChangedVolume_LeavesPointerUnpublished()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);

        using (var transaction = CreateBuiltTransaction(mutation))
        {
            var staged = new ContentIndexStore(transaction.Paths, _scopeId);
            var live = new ContentIndexStore(_paths, _scopeId)
            {
                CurrentVolumeReader = _ => null,
            };

            Assert.Throws<IndexVolumeChangedException>(() =>
                live.ImportStagedUnderLease(mutation, staged));
            Assert.Null(live.TryOpenCurrent());
        }

        Directory.Delete(_paths.GetScopeDirectory(_scopeId), recursive: true);
        using (var transaction = CreateBuiltTransaction(mutation))
        {
            var staged = new ContentIndexStore(transaction.Paths, _scopeId);
            var live = new ContentIndexStore(_paths, _scopeId)
            {
                CurrentVolumeReader = _ => new VolumeBinding(
                    @"\\?\Volume{00000000-0000-0000-0000-000000000000}\",
                    ulong.MaxValue,
                    "DIFFERENT",
                    _root,
                    string.Empty),
            };

            Assert.Throws<IndexVolumeChangedException>(() =>
                live.ImportStagedUnderLease(mutation, staged));
            Assert.Null(live.TryOpenCurrent());
        }
    }

    [Fact]
    public void Commit_Replace_InstallsStagedPdfAndRemovesPriorNamespace()
    {
        string livePdf = new ExtendedSourceStore(_paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(livePdf);
        File.WriteAllText(Path.Combine(livePdf, "old.txt"), "old");

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        string stagedPdf = new ExtendedSourceStore(transaction.Paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(stagedPdf);
        File.WriteAllText(Path.Combine(stagedPdf, "new.txt"), "new");

        transaction.Commit(mutation, 2, StagedPdfCommitMode.Replace);

        Assert.False(File.Exists(Path.Combine(livePdf, "old.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(livePdf, "new.txt")));
    }

    [Fact]
    public void Commit_Delete_RemovesPriorPdfNamespace()
    {
        string livePdf = new ExtendedSourceStore(_paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(livePdf);
        File.WriteAllText(Path.Combine(livePdf, "old.txt"), "old");

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        transaction.Commit(mutation, 2, StagedPdfCommitMode.Delete);

        Assert.False(Directory.Exists(livePdf));
        Assert.True(File.Exists(new ExtendedSourceStore(_paths, _scopeId).DisabledMarkerPath(SpecialSourceKind.PdfText)));
    }

    [Fact]
    public void Commit_ReplaceImageOcr_InstallsStagedNamespaceWithoutChangingPdf()
    {
        var liveStore = new ExtendedSourceStore(_paths, _scopeId);
        string livePdf = liveStore.NamespaceDirectory(SpecialSourceKind.PdfText);
        string liveOcr = liveStore.NamespaceDirectory(SpecialSourceKind.ImageOcr);
        Directory.CreateDirectory(livePdf);
        Directory.CreateDirectory(liveOcr);
        File.WriteAllText(Path.Combine(livePdf, "keep.txt"), "pdf");
        File.WriteAllText(Path.Combine(liveOcr, "old.txt"), "old");

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        string stagedOcr = new ExtendedSourceStore(transaction.Paths, _scopeId)
            .NamespaceDirectory(SpecialSourceKind.ImageOcr);
        Directory.CreateDirectory(stagedOcr);
        File.WriteAllText(Path.Combine(stagedOcr, "new.txt"), "new");

        transaction.Commit(
            mutation,
            2,
            StagedPdfCommitMode.Preserve,
            StagedPdfCommitMode.Replace);

        Assert.Equal("pdf", File.ReadAllText(Path.Combine(livePdf, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(liveOcr, "old.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(liveOcr, "new.txt")));
    }

    [Fact]
    public void Commit_Delete_WhenRawImportFails_KeepsPdfDurablyDisabledInsteadOfResurrectingIt()
    {
        var extended = new ExtendedSourceStore(_paths, _scopeId);
        string livePdf = extended.NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(livePdf);
        File.WriteAllText(Path.Combine(livePdf, "old.txt"), "old unsafe namespace");

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = new ContentIndexBuildTransaction(_paths, _scopeId); // no staged raw index

        Assert.Throws<InvalidDataException>(() =>
            transaction.Commit(mutation, 2, StagedPdfCommitMode.Delete));

        Assert.False(Directory.Exists(livePdf));
        Assert.True(File.Exists(extended.DisabledMarkerPath(SpecialSourceKind.PdfText)));
        Assert.Null(extended.TryLoad(SpecialSourceKind.PdfText));
        Assert.Null(new ContentIndexStore(_paths, _scopeId).TryOpenCurrent());
    }

    [Fact]
    public void Commit_ReplaceWithoutStagedPdf_RestoresPriorPdfAndLeavesRawPointerUnchanged()
    {
        string livePdf = new ExtendedSourceStore(_paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(livePdf);
        File.WriteAllText(Path.Combine(livePdf, "old.txt"), "old");

        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);

        Assert.Throws<InvalidDataException>(() => transaction.Commit(mutation, 2, StagedPdfCommitMode.Replace));
        Assert.Equal("old", File.ReadAllText(Path.Combine(livePdf, "old.txt")));
        Assert.Null(new ContentIndexStore(_paths, _scopeId).TryOpenCurrent());
    }

    [Fact]
    public void Commit_WithoutCompleteStagedIndex_Throws_AndDisposeCleansWorkspace()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        string stagingScope;
        using (var transaction = new ContentIndexBuildTransaction(_paths, _scopeId))
        {
            stagingScope = transaction.Paths.GetScopeDirectory(_scopeId);
            Directory.CreateDirectory(stagingScope);
            Assert.Throws<InvalidDataException>(() => transaction.Commit(mutation, 2, StagedPdfCommitMode.Preserve));
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(stagingScope)));
    }

    [Fact]
    public void Commit_RestoresPriorPdfWhenRawImportFailsAfterStagedPdfWasInstalled()
    {
        string livePdf = new ExtendedSourceStore(_paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(livePdf);
        File.WriteAllText(Path.Combine(livePdf, "old.txt"), "old");
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = new ContentIndexBuildTransaction(_paths, _scopeId); // deliberately no raw staged index
        string stagedPdf = new ExtendedSourceStore(transaction.Paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(stagedPdf);
        File.WriteAllText(Path.Combine(stagedPdf, "new.txt"), "new");

        Assert.Throws<InvalidDataException>(() => transaction.Commit(mutation, 2, StagedPdfCommitMode.Replace));

        Assert.Equal("old", File.ReadAllText(Path.Combine(livePdf, "old.txt")));
        Assert.False(File.Exists(Path.Combine(livePdf, "new.txt")));
    }

    [Fact]
    public void ConstructorAndLockedCleanup_AreFailSafe()
    {
        Assert.Throws<ArgumentNullException>(() => new ContentIndexBuildTransaction(null!, _scopeId));
        Assert.Throws<ArgumentException>(() => new ContentIndexBuildTransaction(_paths, ""));

        var transaction = new ContentIndexBuildTransaction(_paths, _scopeId);
        string stagingScope = transaction.Paths.GetScopeDirectory(_scopeId);
        Directory.CreateDirectory(stagingScope);
        string lockedFile = Path.Combine(stagingScope, "locked.bin");
        File.WriteAllText(lockedFile, "locked");
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            transaction.Dispose(); // best-effort cleanup catches the sharing violation
        transaction.Dispose(); // after unlock, a second cleanup succeeds
        Assert.False(Directory.Exists(Path.GetDirectoryName(stagingScope)));

        bool moved = false;
        Assert.True(ContentIndexBuildTransaction.TryMoveDirectory("a", "b", (_, _) => moved = true));
        Assert.True(moved);
        Assert.False(ContentIndexBuildTransaction.TryMoveDirectory("a", "b", (_, _) => throw new IOException()));
        Assert.False(ContentIndexBuildTransaction.TryMoveDirectory("a", "b", (_, _) => throw new UnauthorizedAccessException()));
        Assert.Throws<ArgumentNullException>(() => ContentIndexBuildTransaction.TryMoveDirectory("a", "b", null!));
    }

    [Fact]
    public async Task Dispose_CannotDeleteStagingWhileCommitIsInProgress()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        var transaction = CreateBuiltTransaction(mutation);
        using var importEntered = new ManualResetEventSlim(false);
        using var releaseImport = new ManualResetEventSlim(false);
        transaction.BeforeImport = () =>
        {
            importEntered.Set();
            releaseImport.Wait();
        };

        Task<StagedIndexCommitResult> commit = Task.Run(() => transaction.Commit(
            mutation, 2, StagedPdfCommitMode.Preserve));
        Assert.True(importEntered.Wait(TimeSpan.FromSeconds(5)));
        Task dispose = Task.Run(transaction.Dispose);
        Assert.False(dispose.IsCompleted);
        releaseImport.Set();

        await commit;
        await dispose;
        Assert.NotNull(new ContentIndexStore(_paths, _scopeId).TryOpenCurrent());
    }

    [Fact]
    public void Commit_PostPointerRetentionFailure_IsCleanupOnlyAndStillReturnsCommittedSuccess()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.RetentionStarted)
                throw new IOException("injected retention failure");
        };
        try
        {
            StagedIndexCommitResult result = transaction.Commit(mutation, 2, StagedPdfCommitMode.Preserve);

            Assert.Equal("gen-000001", result.ActiveBaseGenerationId);
            Assert.NotNull(new ContentIndexStore(_paths, _scopeId).TryOpenCurrent());
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }
    }

    [Fact]
    public void Commit_Replace_PostCommitFailureNeverRollsBackThePublishedRawOrPdfState()
    {
        string livePdf = new ExtendedSourceStore(_paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(livePdf);
        File.WriteAllText(Path.Combine(livePdf, "old.txt"), "old");
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        using var transaction = CreateBuiltTransaction(mutation);
        string stagedPdf = new ExtendedSourceStore(transaction.Paths, _scopeId).NamespaceDirectory(SpecialSourceKind.PdfText);
        Directory.CreateDirectory(stagedPdf);
        File.WriteAllText(Path.Combine(stagedPdf, "new.txt"), "new");
        IndexMutationFaults.OnHit = point =>
        {
            if (point == IndexMutationFaults.PdfEnabled)
                throw new IOException("injected after durable raw commit");
        };
        try
        {
            Assert.Throws<IOException>(() =>
                transaction.Commit(mutation, 2, StagedPdfCommitMode.Replace));
        }
        finally
        {
            IndexMutationFaults.OnHit = null;
        }

        Assert.NotNull(new ContentIndexStore(_paths, _scopeId).TryOpenCurrent());
        Assert.True(File.Exists(Path.Combine(livePdf, "new.txt")));
        Assert.False(File.Exists(Path.Combine(livePdf, "old.txt")));
    }

    private ContentIndexBuildTransaction CreateBuiltTransaction(IndexMutationContext mutation)
    {
        var transaction = new ContentIndexBuildTransaction(_paths, _scopeId);
        var manager = new ContentIndexManager(transaction.Paths);
        manager.BuildScopeUnderLease(
            mutation,
            _root,
            new IndexIngestionPolicy(0, null, null, true, false, 0),
            buildMemoryBudgetMB: 64);
        return transaction;
    }
}
