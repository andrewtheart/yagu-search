using Yagu.Services.Index;

namespace Yagu.Tests.Index;

public sealed class IndexStorageRecoveryTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-recovery", Guid.NewGuid().ToString("N"));
    private readonly FixedContentIndexPathProvider _paths;

    public IndexStorageRecoveryTests()
    {
        _paths = new FixedContentIndexPathProvider(_sandbox);
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public void RecoverUnderLease_RemovesDefiniteCrashResidueWithoutDeletingRetainedGenerations()
    {
        string abandonedBuild = Path.Combine(_sandbox, ".build-abandoned");
        Directory.CreateDirectory(abandonedBuild);
        string scope = _paths.GetScopeDirectory("scope-one");
        string generations = Path.Combine(scope, "generations");
        string segments = Path.Combine(scope, "segments");
        Directory.CreateDirectory(Path.Combine(generations, ".gen-000001.tmp"));
        string retainedGeneration = Path.Combine(generations, "gen-123456");
        Directory.CreateDirectory(retainedGeneration);
        File.WriteAllText(Path.Combine(retainedGeneration, ContentIndexGenerationSerializer.ManifestFile + ".reanchor.tmp"), "partial");
        File.WriteAllText(Path.Combine(retainedGeneration, ContentIndexV3Format.PostingsFile + ".tmp"), "partial");
        string orphanImport = Path.Combine(generations, "gen-123457");
        Directory.CreateDirectory(orphanImport);
        File.WriteAllText(Path.Combine(orphanImport, ContentIndexStore.ImportMarkerFile), "");
        Directory.CreateDirectory(Path.Combine(segments, ".seg-000001.tmp"));
        Directory.CreateDirectory(Path.Combine(segments, "seg-000001"));
        Directory.CreateDirectory(Path.Combine(_sandbox, ".metadata"));
        Directory.CreateDirectory(scope);
        File.WriteAllText(Path.Combine(scope, "current.a.tmp"), "partial pointer");

        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        IndexRecoveryResult result = IndexStorageRecovery.RecoverUnderLease(mutation, _paths);

        Assert.Equal(1, result.DeletedBuildWorkspaces);
        Assert.Equal(1, result.RecoveredScopes);
        Assert.Equal(0, result.Failures);
        Assert.False(Directory.Exists(abandonedBuild));
        Assert.False(Directory.Exists(Path.Combine(generations, ".gen-000001.tmp")));
        Assert.True(Directory.Exists(Path.Combine(generations, "gen-123456")));
        Assert.False(Directory.Exists(orphanImport));
        Assert.False(Directory.Exists(Path.Combine(segments, ".seg-000001.tmp")));
        Assert.False(Directory.Exists(Path.Combine(segments, "seg-000001")));
        Assert.False(File.Exists(Path.Combine(scope, "current.a.tmp")));
        Assert.False(File.Exists(Path.Combine(retainedGeneration, ContentIndexGenerationSerializer.ManifestFile + ".reanchor.tmp")));
        Assert.False(File.Exists(Path.Combine(retainedGeneration, ContentIndexV3Format.PostingsFile + ".tmp")));
        Assert.True(Directory.Exists(Path.Combine(_sandbox, ".metadata")));
    }

    [Fact]
    public void RecoverUnderLease_RestoresNewestPdfBackupOrDeletesBackupsWhenLiveExists()
    {
        string scope = _paths.GetScopeDirectory("scope-pdf");
        string extended = Path.Combine(scope, "extended");
        string older = Path.Combine(extended, ".pdf-backup-older");
        string newer = Path.Combine(extended, ".pdf-backup-newer");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(older, "value.txt"), "old");
        File.WriteAllText(Path.Combine(newer, "value.txt"), "new");
        Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-2));
        Directory.SetLastWriteTimeUtc(newer, DateTime.UtcNow.AddMinutes(-1));

        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        IndexRecoveryResult restored = IndexStorageRecovery.RecoverUnderLease(mutation, _paths);

        string livePdf = Path.Combine(extended, "pdf");
        Assert.Equal(1, restored.RestoredPdfNamespaces);
        Assert.Equal(1, restored.DeletedPdfBackups);
        Assert.Equal("new", File.ReadAllText(Path.Combine(livePdf, "value.txt")));
        Assert.False(Directory.Exists(older));

        string stale = Path.Combine(extended, ".pdf-backup-stale");
        Directory.CreateDirectory(stale);
        IndexRecoveryResult cleaned = IndexStorageRecovery.RecoverUnderLease(mutation, _paths);
        Assert.Equal(0, cleaned.RestoredPdfNamespaces);
        Assert.Equal(1, cleaned.DeletedPdfBackups);
        Assert.True(Directory.Exists(livePdf));
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public void RecoverUnderLease_ClearsImportMarkerFromACommittedReferencedGeneration()
    {
        string corpus = Path.Combine(_sandbox, "corpus");
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "a.txt"), "planner content");
        var manager = new ContentIndexManager(_paths);
        BuildScopeResult built = manager.BuildScope(corpus, new IndexIngestionPolicy(0, null, null, true, false, 0));
        var store = new ContentIndexStore(_paths, built.ScopeId);
        store.TryOpenCurrent(out string? generationDirectory);
        string marker = Path.Combine(generationDirectory!, ContentIndexStore.ImportMarkerFile);
        File.WriteAllText(marker, "simulated crash after pointer flip");

        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        IndexStorageRecovery.RecoverUnderLease(mutation, _paths);

        Assert.False(File.Exists(marker));
        Assert.NotNull(store.TryOpenCurrent());
    }

    [Fact]
    public void RecoverUnderLease_DisabledPdfNeverRestoresBackupOrStaleLiveNamespace()
    {
        string scopeId = "scope-disabled-pdf";
        string scope = _paths.GetScopeDirectory(scopeId);
        var extendedStore = new ExtendedSourceStore(_paths, scopeId);
        string livePdf = extendedStore.NamespaceDirectory(SpecialSourceKind.PdfText);
        string extended = Path.GetDirectoryName(livePdf)!;
        string backup = Path.Combine(extended, ExtendedSourceStore.BackupPrefix + "pdf-old");
        Directory.CreateDirectory(livePdf);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(livePdf, "stale.txt"), "stale live");
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old backup");
        ExtendedSourceStore.WriteMarker(extendedStore.DisabledMarkerPath(SpecialSourceKind.PdfText));

        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        IndexStorageRecovery.RecoverUnderLease(mutation, _paths);

        Assert.True(File.Exists(extendedStore.DisabledMarkerPath(SpecialSourceKind.PdfText)));
        Assert.False(Directory.Exists(livePdf));
        Assert.False(Directory.Exists(backup));
        Assert.Null(extendedStore.TryLoad(SpecialSourceKind.PdfText));
        Assert.True(Directory.Exists(scope));
    }

    [Fact]
    public void RecoverUnderLease_DisabledOcrNeverRestoresBackupOrStaleLiveNamespace()
    {
        using IndexMutationContext mutation = IndexMutationContext.Acquire(_paths);
        string scope = _paths.GetScopeDirectory("ocr-scope");
        string extended = Path.Combine(scope, "extended");
        string live = Path.Combine(extended, "ocr");
        string backup = Path.Combine(extended, ExtendedSourceStore.BackupPrefix + "ocr-old");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(live, "stale.txt"), "stale");
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old");
        ExtendedSourceStore.WriteMarker(Path.Combine(
            extended, "ocr" + ExtendedSourceStore.DisabledMarkerSuffix));

        IndexStorageRecovery.RecoverUnderLease(mutation, _paths);

        Assert.False(Directory.Exists(live));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void RecoverUnderLease_CompletePdfReplacementClearsDisabledStateAndBackups()
    {
        string scopeId = "scope-replacement-pdf";
        var extendedStore = new ExtendedSourceStore(_paths, scopeId);
        string livePdf = extendedStore.NamespaceDirectory(SpecialSourceKind.PdfText);
        string extended = Path.GetDirectoryName(livePdf)!;
        string backup = Path.Combine(extended, ExtendedSourceStore.BackupPrefix + "pdf-old");
        Directory.CreateDirectory(livePdf);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(livePdf, "new.txt"), "new complete namespace");
        ExtendedSourceStore.WriteMarker(Path.Combine(livePdf, ExtendedSourceStore.ReplacementReadyMarkerFile));
        ExtendedSourceStore.WriteMarker(extendedStore.DisabledMarkerPath(SpecialSourceKind.PdfText));

        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        IndexStorageRecovery.RecoverUnderLease(mutation, _paths);

        Assert.False(File.Exists(extendedStore.DisabledMarkerPath(SpecialSourceKind.PdfText)));
        Assert.False(File.Exists(Path.Combine(livePdf, ExtendedSourceStore.ReplacementReadyMarkerFile)));
        Assert.True(File.Exists(Path.Combine(livePdf, "new.txt")));
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void RecoverUnderLease_IsolatesEnumerationDeleteScopeMoveTimestampAndBackupDeleteFailures()
    {
        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();

        var enumerateFailure = new IndexRecoveryFileSystem
        {
            DirectoryExists = _ => true,
            GetDirectories = _ => throw new IOException("enumerate"),
        };
        Assert.Equal(1, IndexStorageRecovery.RecoverUnderLease(mutation, _paths, fileSystem: enumerateFailure).Failures);

        string build = Path.Combine(_sandbox, ".build-fail");
        var deleteFailure = new IndexRecoveryFileSystem
        {
            DirectoryExists = _ => true,
            GetDirectories = path => path == _sandbox ? new[] { build } : Array.Empty<string>(),
            DeleteDirectory = _ => throw new UnauthorizedAccessException("delete"),
        };
        Assert.Equal(1, IndexStorageRecovery.RecoverUnderLease(mutation, _paths, fileSystem: deleteFailure).Failures);

        string scope = Path.Combine(_sandbox, "scope-fail");
        string extended = Path.Combine(scope, "extended");
        string backup = Path.Combine(extended, ".pdf-backup-one");
        var scopeAndMoveFailure = new IndexRecoveryFileSystem
        {
            DirectoryExists = path => path != Path.Combine(extended, "pdf"),
            GetDirectories = path => path switch
            {
                var p when p == _sandbox => new[] { scope },
                var p when p == extended => new[] { backup },
                _ => Array.Empty<string>(),
            },
            RecoverScope = (_, _, _, _) => throw new InvalidDataException("scope"),
            GetLastWriteTimeUtc = _ => throw new IOException("timestamp"),
            MoveDirectory = (_, _) => throw new IOException("move"),
        };
        IndexRecoveryResult moveFailed = IndexStorageRecovery.RecoverUnderLease(mutation, _paths, fileSystem: scopeAndMoveFailure);
        Assert.Equal(2, moveFailed.Failures);

        var backupDeleteFailure = new IndexRecoveryFileSystem
        {
            DirectoryExists = _ => true,
            GetDirectories = path => path switch
            {
                var p when p == _sandbox => new[] { scope },
                var p when p == extended => new[] { backup },
                _ => Array.Empty<string>(),
            },
            DeleteDirectory = _ => throw new IOException("backup delete"),
        };
        Assert.Equal(1, IndexStorageRecovery.RecoverUnderLease(mutation, _paths, fileSystem: backupDeleteFailure).Failures);
    }

    [Fact]
    public void RecoverUnderLease_IsolatesEveryPdfStateCleanupFailure()
    {
        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        string scope = Path.Combine(_sandbox, "scope-virtual");
        string extended = Path.Combine(scope, ExtendedSourceStore.ExtendedSubdir);
        string live = Path.Combine(extended, "pdf");
        string disabled = Path.Combine(extended, "pdf" + ExtendedSourceStore.DisabledMarkerSuffix);
        string disabledTemp = disabled + ExtendedSourceStore.DisabledMarkerTempSuffix;
        string replacement = Path.Combine(live, ExtendedSourceStore.ReplacementReadyMarkerFile);
        string publishTemp = Path.Combine(extended, ExtendedSourceStore.PublishTempPrefix + "pdf-one");
        string backup = Path.Combine(extended, ExtendedSourceStore.BackupPrefix + "pdf-one");

        IndexRecoveryResult Run(
            string[] extendedDirectories,
            HashSet<string> files,
            bool liveExists,
            Action<string>? deleteDirectory = null,
            Action<string>? deleteFile = null)
        {
            var fs = new IndexRecoveryFileSystem
            {
                DirectoryExists = path => path == _sandbox || path == scope || path == extended
                    || (path == live && liveExists),
                GetDirectories = path => path == _sandbox
                    ? new[] { scope }
                    : path == extended ? extendedDirectories : Array.Empty<string>(),
                FileExists = files.Contains,
                DeleteDirectory = deleteDirectory ?? (_ => { }),
                DeleteFile = deleteFile ?? (path => files.Remove(path)),
                RecoverScope = (_, _, _, _) => { },
            };
            return IndexStorageRecovery.RecoverUnderLease(mutation, _paths, fileSystem: fs);
        }

        Assert.Equal(1, Run(
            new[] { publishTemp },
            new HashSet<string>(StringComparer.Ordinal),
            liveExists: false,
            deleteDirectory: _ => throw new IOException("publish temp delete")).Failures);

        Assert.Equal(1, Run(
            Array.Empty<string>(),
            new HashSet<string>(new[] { disabledTemp }, StringComparer.Ordinal),
            liveExists: false,
            deleteFile: _ => throw new IOException("disabled temp delete")).Failures);

        Assert.Equal(2, Run(
            new[] { backup },
            new HashSet<string>(new[] { disabled }, StringComparer.Ordinal),
            liveExists: true,
            deleteDirectory: _ => throw new UnauthorizedAccessException("disabled live/backup delete")).Failures);

        Assert.Equal(0, Run(
            Array.Empty<string>(),
            new HashSet<string>(new[] { disabled }, StringComparer.Ordinal),
            liveExists: false).Failures);

        Assert.Equal(1, Run(
            Array.Empty<string>(),
            new HashSet<string>(new[] { disabled, replacement }, StringComparer.Ordinal),
            liveExists: true,
            deleteFile: path =>
            {
                if (path == disabled) throw new IOException("disabled marker delete");
            }).Failures);

        var replacementDeleteFiles = new HashSet<string>(new[] { disabled, replacement }, StringComparer.Ordinal);
        Assert.Equal(1, Run(
            Array.Empty<string>(),
            replacementDeleteFiles,
            liveExists: true,
            deleteFile: path =>
            {
                if (path == replacement) throw new IOException("replacement marker delete");
                replacementDeleteFiles.Remove(path);
            }).Failures);

        Assert.Equal(1, Run(
            Array.Empty<string>(),
            new HashSet<string>(new[] { replacement }, StringComparer.Ordinal),
            liveExists: true,
            deleteFile: _ => throw new UnauthorizedAccessException("standalone replacement marker delete")).Failures);
    }

    [Fact]
    public void RecoverUnderLease_ValidatesArgumentsAndLeaseOwnership()
    {
        using IndexMutationContext mutation = AcquireWithoutAutomaticRecovery();
        Assert.Throws<ArgumentNullException>(() => IndexStorageRecovery.RecoverUnderLease(null!, _paths));
        Assert.Throws<ArgumentNullException>(() => IndexStorageRecovery.RecoverUnderLease(mutation, null!));
        var other = new FixedContentIndexPathProvider(Path.Combine(_sandbox, "other"));
        Assert.Throws<InvalidOperationException>(() => IndexStorageRecovery.RecoverUnderLease(mutation, other));

        DateTime expected = new(2026, 7, 23, 1, 2, 3, DateTimeKind.Utc);
        Assert.Equal(expected, IndexStorageRecovery.SafeLastWriteTime("x", new IndexRecoveryFileSystem
        {
            GetLastWriteTimeUtc = _ => expected,
        }));
        Assert.Equal(DateTime.MinValue, IndexStorageRecovery.SafeLastWriteTime("x", new IndexRecoveryFileSystem
        {
            GetLastWriteTimeUtc = _ => throw new IOException(),
        }));
        Assert.Equal(DateTime.MinValue, IndexStorageRecovery.SafeLastWriteTime("x", new IndexRecoveryFileSystem
        {
            GetLastWriteTimeUtc = _ => throw new UnauthorizedAccessException(),
        }));
    }

    private IndexMutationContext AcquireWithoutAutomaticRecovery()
    {
        Assert.True(IndexMutationContext.TryAcquire(
            _paths,
            path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            out IndexMutationContext? mutation));
        return mutation!;
    }
}
