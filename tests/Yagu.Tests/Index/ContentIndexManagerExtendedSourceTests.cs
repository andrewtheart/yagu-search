using Yagu.Services.Index;
using Yagu.Services.Ocr;
using Yagu.Services.Pdf;

namespace Yagu.Tests.Index;

public sealed class ContentIndexManagerExtendedSourceTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _corpus;
    private readonly string _indexRoot;
    private readonly IContentIndexPathProvider _paths;

    public ContentIndexManagerExtendedSourceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "yagu-index-manager-extended", Guid.NewGuid().ToString("N"));
        _corpus = Path.Combine(_sandbox, "corpus");
        _indexRoot = Path.Combine(_corpus, "_index");
        Directory.CreateDirectory(_corpus);
        _paths = new DefaultContentIndexPathProvider(_indexRoot, _indexRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_MissingToolAndEmptyScopeDoNotPublish()
    {
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            checkpointReader: _ => null);
        PdfExtendedSourceBuildResult missingTool = await manager.BuildPdfExtendedSourceAsync(
            _corpus, Policy(), new FakePdfExtractor(Path.Combine(_sandbox, "missing.exe"), (_, _) => PdfTextResult.Ok("unused")));

        Assert.Equal(PdfExtendedSourceBuildStatus.SkippedToolUnavailable, missingTool.Status);

        string tool = CreateTool();
        PdfExtendedSourceBuildResult empty = await manager.BuildPdfExtendedSourceAsync(
            _corpus, Policy(), new FakePdfExtractor(tool, (_, _) => PdfTextResult.Ok("unused")));

        Assert.Equal(PdfExtendedSourceBuildStatus.NoPdfs, empty.Status);
        Assert.Equal(0, empty.PdfsSeen);
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_FiltersCandidatesAndPublishesDeterministicNamespace()
    {
        Write("keep.pdf", "pdf bytes");
        Write("ignore.txt", "not a pdf");
        Write("too-large.pdf", new string('x', 200));
        var progress = new List<PdfBuildProgress>();
        var extractor = new FakePdfExtractor(CreateTool(), (path, _) =>
            PdfTextResult.Ok("stable extracted text for " + Path.GetFileName(path)));
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100));

        PdfExtendedSourceBuildResult result = await manager.BuildPdfExtendedSourceAsync(
            _corpus, Policy(maxBytes: 100), extractor, progress: progress.Add);

        Assert.Equal(PdfExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(1, result.PdfsSeen);
        Assert.Equal(1, result.Admitted);
        Assert.Equal(PdfDeterminismVerdict.Deterministic, result.Determinism);
        Assert.Equal(new PdfBuildProgress(1, 1), Assert.Single(progress));
        ExtendedSourceNamespace? stored = new ExtendedSourceStore(_paths, result.ScopeId)
            .TryLoad(SpecialSourceKind.PdfText);
        Assert.NotNull(stored);
        Assert.True(stored!.IsKnownSource(Path.Combine(_corpus, "keep.pdf")));
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_NondeterministicExtractionDeletesNamespace()
    {
        Write("unstable.pdf", "pdf bytes");
        var extractor = new FakePdfExtractor(CreateTool(), (_, call) => PdfTextResult.Ok("text-" + call));
        var manager = new ContentIndexManager(_paths);

        PdfExtendedSourceBuildResult result = await manager.BuildPdfExtendedSourceAsync(
            _corpus, Policy(), extractor);

        Assert.Equal(PdfExtendedSourceBuildStatus.SkippedNotDeterministic, result.Status);
        Assert.Equal(PdfDeterminismVerdict.NotProven, result.Determinism);
        Assert.Null(new ExtendedSourceStore(_paths, result.ScopeId).TryLoad(SpecialSourceKind.PdfText));
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_ValidationRejectionDoesNotReportPublished()
    {
        Write("document.pdf", "pdf bytes");
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            checkpointReader: _ => null,
            extendedSourceStoreFactory: RejectingStore);

        await Assert.ThrowsAsync<IOException>(() => manager.BuildPdfExtendedSourceAsync(
            _corpus,
            Policy(),
            new FakePdfExtractor(CreateTool(), (_, _) => PdfTextResult.Ok("stable extracted text"))));
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_MissingRootAndCancellationPropagate()
    {
        var manager = new ContentIndexManager(_paths);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => manager.BuildPdfExtendedSourceAsync(
            Path.Combine(_sandbox, "missing"), Policy(), new FakePdfExtractor(CreateTool(), (_, _) => PdfTextResult.Ok("text"))));

        Write("cancel.pdf", "pdf bytes");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.BuildPdfExtendedSourceAsync(
            _corpus, Policy(), new FakePdfExtractor(CreateTool(), (_, _) => PdfTextResult.Ok("text")), cancellation.Token));
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_CloudSourceUsesBoundedLocalCopiesAndOriginalIdentity()
    {
        string pdf = Path.Combine(_corpus, "cloud.pdf");
        Write("cloud.pdf", "pdf bytes");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(
                    pdf,
                    new FileInfo(pdf).Length,
                    FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        string? firstCopy = null;
        int extractions = 0;
        var extractor = new FakePdfExtractor(CreateTool(), (extractionPath, _) =>
        {
            extractions++;
            Assert.NotEqual(pdf, extractionPath);
            if (firstCopy is not null)
                Assert.False(File.Exists(firstCopy));
            firstCopy = extractionPath;
            Assert.Equal("pdf bytes", File.ReadAllText(extractionPath));
            return PdfTextResult.Ok("stable cloud PDF text");
        });
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true);

        PdfExtendedSourceBuildResult result = await manager.BuildPdfExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            extractor,
            fileIoTimeout: TimeSpan.FromSeconds(1));

        Assert.Equal(PdfExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(1, result.PdfsSeen);
        Assert.Equal(1, result.Admitted);
        Assert.Equal(3, extractions);
        ExtendedSourceNamespace stored = Assert.IsType<ExtendedSourceNamespace>(
            new ExtendedSourceStore(_paths, result.ScopeId).TryLoad(SpecialSourceKind.PdfText));
        Assert.True(stored.IsKnownSource(pdf));
        Assert.NotNull(stored.SourceIdentityByKey[IndexScopeIdentity.NormalizePath(pdf)]);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_paths.GetScopeDirectory(result.ScopeId)),
            path => Path.GetFileName(path).StartsWith(".pdf-hydration-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_MixedCloudAndLocalSourcesDelegateToTheSafePath()
    {
        string local = Path.Combine(_corpus, "local.pdf");
        string cloud = Path.Combine(_corpus, "cloud.pdf");
        Write("local.pdf", "local bytes");
        Write("cloud.pdf", "cloud bytes");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(local, new FileInfo(local).Length, FileAttributes.Normal),
                new IndexCrawlEntry(cloud, new FileInfo(cloud).Length, FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        var extractedPaths = new List<string>();
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (source, destination, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
                return true;
            });

        PdfExtendedSourceBuildResult result = await manager.BuildPdfExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakePdfExtractor(CreateTool(), (path, _) =>
            {
                extractedPaths.Add(path);
                return PdfTextResult.Ok("stable extracted text");
            }));

        // A local PDF in the same batch as a cloud placeholder must still be extracted from its original
        // path, while the cloud file is extracted from a bounded local copy.
        Assert.Equal(PdfExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(2, result.PdfsSeen);
        Assert.Contains(local, extractedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(extractedPaths, path =>
            !string.Equals(path, cloud, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildPdfExtendedSourceAsync_ConfiguredFreeSpaceReserveRejectsProjectedCloudCopy()
    {
        string pdf = Path.Combine(_corpus, "cloud.pdf");
        Write("cloud.pdf", "pdf bytes");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(
                    pdf,
                    new FileInfo(pdf).Length,
                    FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        int hydrations = 0;
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (_, _, _) =>
            {
                hydrations++;
                return true;
            });

        PdfExtendedSourceBuildResult result = await manager.BuildPdfExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakePdfExtractor(CreateTool(), (_, _) => PdfTextResult.Ok("must not run")),
            minimumFreeSpaceMB: int.MaxValue);

        Assert.Equal(PdfExtendedSourceBuildStatus.SkippedNotDeterministic, result.Status);
        Assert.Equal(0, result.Admitted);
        Assert.Equal(0, hydrations);
        Assert.Null(new ExtendedSourceStore(_paths, result.ScopeId).TryLoad(SpecialSourceKind.PdfText));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildPdfExtendedSourceAsync_CloudHydrationFailureNeverInvokesExtractor(bool timeOut)
    {
        string pdf = Path.Combine(_corpus, "cloud.pdf");
        Write("cloud.pdf", "pdf bytes");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(
                    pdf,
                    new FileInfo(pdf).Length,
                    FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        int extractions = 0;
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (_, _, _) =>
            {
                if (timeOut)
                    Thread.Sleep(100);
                return false;
            });

        PdfExtendedSourceBuildResult result = await manager.BuildPdfExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakePdfExtractor(CreateTool(), (_, _) =>
            {
                extractions++;
                return PdfTextResult.Ok("must not run");
            }),
            fileIoTimeout: TimeSpan.FromMilliseconds(10));

        Assert.Equal(PdfExtendedSourceBuildStatus.SkippedNotDeterministic, result.Status);
        Assert.Equal(1, result.PdfsSeen);
        Assert.Equal(0, result.Admitted);
        Assert.Equal(0, extractions);
        Assert.Null(new ExtendedSourceStore(_paths, result.ScopeId).TryLoad(SpecialSourceKind.PdfText));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_MissingFingerprintAndEmptyScopeDoNotPublish()
    {
        var engine = new FakeOcrEngine(_ => OcrResult.Ok("unused"));
        var unavailable = new ContentIndexManager(_paths, 2, contentReader: null,
            ocrFingerprintReader: (_, _, _) => null);

        ImageOcrExtendedSourceBuildResult missingFingerprint = await unavailable.BuildImageOcrExtendedSourceAsync(
            _corpus, Policy(), engine, Extensions(), "model", 960);

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.SkippedEngineUnavailable, missingFingerprint.Status);

        var manager = OcrManager();
        ImageOcrExtendedSourceBuildResult empty = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus, Policy(), engine, Extensions(), "model", 960);

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.NoImages, empty.Status);
        Assert.Equal(0, empty.ImagesSeen);
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_FiltersCandidatesAndPublishesPositiveResults()
    {
        Write("good.png", "image");
        Write("bad.jpg", "image");
        Write("ignore.txt", "text");
        Write("too-large.png", new string('x', 200));
        var progress = new List<ImageOcrBuildProgress>();
        var engine = new FakeOcrEngine(path => path.EndsWith("good.png", StringComparison.OrdinalIgnoreCase)
            ? OcrResult.Ok("recognized invoice text")
            : OcrResult.Fail("unreadable"));
        ContentIndexManager manager = OcrManager();

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus, Policy(maxBytes: 100), engine, Extensions(), "model", 960, progress: progress.Add);

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(2, result.ImagesSeen);
        Assert.Equal(1, result.Admitted);
        Assert.Equal(1, result.Failed);
        Assert.Equal(new ImageOcrBuildProgress(2, 2), progress[^1]);
        ExtendedSourceNamespace? stored = new ExtendedSourceStore(_paths, result.ScopeId)
            .TryLoad(SpecialSourceKind.ImageOcr);
        Assert.NotNull(stored);
        Assert.True(stored!.IsKnownSource(Path.Combine(_corpus, "good.png")));
        Assert.False(stored.IsKnownSource(Path.Combine(_corpus, "bad.jpg")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildImageOcrExtendedSourceAsync_CloudHydrationMustFinishBeforeOcr(bool timeOut)
    {
        string image = Path.Combine(_corpus, "cloud.png");
        Write("cloud.png", "image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(
                    image,
                    new FileInfo(image).Length,
                    FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        int recognized = 0;
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (_, _, _) =>
            {
                if (timeOut)
                    Thread.Sleep(100);
                return false;
            });

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(_ =>
            {
                recognized++;
                return OcrResult.Ok("must not run");
            }),
            Extensions(),
            "model",
            960,
            fileIoTimeout: TimeSpan.FromMilliseconds(10));

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(1, result.ImagesSeen);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, recognized);
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_DefaultCloudHydratorReadsCandidateBeforeOcr()
    {
        string image = Path.Combine(_corpus, "cloud.png");
        Write("cloud.png", "image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(
                    image,
                    new FileInfo(image).Length,
                    FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        int recognized = 0;
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true);

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(ocrPath =>
            {
                recognized++;
                Assert.NotEqual(image, ocrPath);
                Assert.StartsWith(_paths.GetScopeDirectory(ContentIndexManager.ScopeIdForRoot(_corpus)), ocrPath);
                Assert.Equal("image", File.ReadAllText(ocrPath));
                return OcrResult.Ok("recognized cloud image");
            }),
            Extensions(),
            "model",
            960);

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(1, result.ImagesSeen);
        Assert.Equal(1, recognized);
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_paths.GetScopeDirectory(result.ScopeId)),
            path => Path.GetFileName(path).StartsWith(".ocr-hydration-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_MixedCloudAndLocalSourcesDelegateToTheSafePath()
    {
        string local = Path.Combine(_corpus, "local.png");
        string cloud = Path.Combine(_corpus, "cloud.png");
        Write("local.png", "local image");
        Write("cloud.png", "cloud image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(local, new FileInfo(local).Length, FileAttributes.Normal),
                new IndexCrawlEntry(cloud, new FileInfo(cloud).Length, FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        var recognizedPaths = new List<string>();
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (source, destination, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
                return true;
            });

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(path =>
            {
                recognizedPaths.Add(path);
                return OcrResult.Ok("recognized text");
            }),
            Extensions(),
            "model",
            960);

        // Mixing local and cloud images must not make local OCR read a hydrated copy, and must not let a
        // cloud placeholder be opened directly.
        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(2, result.ImagesSeen);
        Assert.Contains(local, recognizedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(recognizedPaths, path =>
            !string.Equals(path, cloud, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_CloudHydratorExceptionMarksOnlyThatImageFailed()
    {
        string image = Path.Combine(_corpus, "cloud.png");
        Write("cloud.png", "image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(image, new FileInfo(image).Length, FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        int recognized = 0;
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (_, _, _) => throw new IOException("provider disappeared"));

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(_ =>
            {
                recognized++;
                return OcrResult.Ok("must not run");
            }),
            Extensions(),
            "model",
            960);

        // A hydration exception means this placeholder cannot be indexed; it must not abort the whole
        // OCR namespace or read the cloud file directly.
        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(1, result.ImagesSeen);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, recognized);
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_CancellationDuringHydrationPropagates()
    {
        string image = Path.Combine(_corpus, "cloud.png");
        Write("cloud.png", "image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(image, new FileInfo(image).Length, FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        using var cancellation = new CancellationTokenSource();
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (_, destination, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, "partial image");
                cancellation.Cancel();
                return true;
            });

        // A user cancellation raised after the cloud copy returns must stay a cancellation, not be
        // downgraded to a per-file OCR failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(_ => OcrResult.Ok("must not run")),
            Extensions(),
            "model",
            960,
            cancellationToken: cancellation.Token));
        Assert.Null(new ExtendedSourceStore(_paths, ContentIndexManager.ScopeIdForRoot(_corpus))
            .TryLoad(SpecialSourceKind.ImageOcr));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_LockedHydratedFileDoesNotBreakPublication()
    {
        string image = Path.Combine(_corpus, "cloud.png");
        Write("cloud.png", "image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(image, new FileInfo(image).Length, FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        var heldHandles = new List<FileStream>();
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true);

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(path =>
            {
                heldHandles.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                return OcrResult.Ok("recognized cloud image");
            }),
            Extensions(),
            "model",
            960);

        try
        {
            // Cleanup of an already-open hydrated copy is best-effort; a scanner holding that file must
            // not turn a successful OCR namespace into a failed build.
            Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
            Assert.Contains(
                Directory.EnumerateDirectories(_paths.GetScopeDirectory(result.ScopeId)),
                path => Path.GetFileName(path).StartsWith(".ocr-hydration-", StringComparison.Ordinal));
        }
        finally
        {
            foreach (FileStream handle in heldHandles)
                handle.Dispose();
        }
    }

    [Fact]
    public void HydrateCloudFile_CancelledCopyDeletesPartialFileAndEmptyDirectory()
    {
        string source = Path.Combine(_corpus, "source.bin");
        Write("source.bin", "source bytes");
        string hydrationDir = Path.Combine(_sandbox, "hydrate-empty");
        string destination = Path.Combine(hydrationDir, "copy.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            InvokeHydrateCloudFile(source, destination, cancellation.Token));

        // A cancelled copy must not leave a partial hydrated cloud file that a later extractor could read
        // as if it were complete.
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(hydrationDir));
    }

    [Fact]
    public void HydrateCloudFile_CancelledCopyKeepsNonEmptyHydrationDirectory()
    {
        string source = Path.Combine(_corpus, "source.bin");
        Write("source.bin", "source bytes");
        string hydrationDir = Path.Combine(_sandbox, "hydrate-non-empty");
        Directory.CreateDirectory(hydrationDir);
        string sentinel = Path.Combine(hydrationDir, "other.tmp");
        File.WriteAllText(sentinel, "another in-flight copy");
        string destination = Path.Combine(hydrationDir, "copy.bin");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            InvokeHydrateCloudFile(source, destination, cancellation.Token));

        // Multiple cloud copies can share a hydration folder; cancelling one copy must remove only its
        // partial file and tolerate a non-empty directory.
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task HydratingOcrEngine_ExposesIdentityAndDelegatesNonCloudImages()
    {
        string local = Path.Combine(_corpus, "local.png");
        Write("local.png", "local image");
        using IDisposable hydration = CreateHydrationSession(
            Path.Combine(_corpus, "cloud.png"),
            isCloudOnly: true,
            (_, _, _) => true);
        object wrapper = CreateHydratingOcrEngine(
            new FakeOcrEngine(path =>
            {
                Assert.Equal(local, path);
                return OcrResult.Ok("recognized local image");
            }),
            hydration);
        Type wrapperType = wrapper.GetType();

        var recognize = (Task<OcrResult>)wrapperType.GetMethod("RecognizeAsync")!
            .Invoke(wrapper, [local, CancellationToken.None])!;
        OcrResult result = await recognize;

        // The hydration wrapper is transparent for diagnostics and for ordinary local images; only cloud
        // placeholders are routed through the bounded-copy path.
        Assert.Equal(OcrEngineFactory.PaddleId, wrapperType.GetProperty("Id")!.GetValue(wrapper));
        Assert.Equal("Fake OCR", wrapperType.GetProperty("DisplayName")!.GetValue(wrapper));
        Assert.True(result.Success);
        Assert.Equal("recognized local image", result.Text);
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_LaterCloudFailurePreservesEarlierStagedCandidate()
    {
        string available = Path.Combine(_corpus, "available.png");
        string disconnected = Path.Combine(_corpus, "disconnected.png");
        Write("available.png", "available image");
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            [
                new IndexCrawlEntry(available, new FileInfo(available).Length, FileAttributes.Normal | (FileAttributes)0x00400000),
                new IndexCrawlEntry(disconnected, 20, FileAttributes.Normal | (FileAttributes)0x00400000),
            ],
        };
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            crawlerFileSystem: fileSystem,
            cloudFileCanHydrate: (_, _) => true,
            cloudFileHydrator: (source, destination, _) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (source == disconnected)
                {
                    Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!));
                    return false;
                }
                File.Copy(source, destination);
                return true;
            });

        ImageOcrExtendedSourceBuildResult result = await manager.BuildImageOcrExtendedSourceAsync(
            _corpus,
            Policy(includeCloudOnlyFiles: true),
            new FakeOcrEngine(ocrPath =>
            {
                Assert.Equal("available image", File.ReadAllText(ocrPath));
                return OcrResult.Ok("recognized available image");
            }),
            Extensions(),
            "model",
            960);

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.Published, result.Status);
        Assert.Equal(2, result.ImagesSeen);
        Assert.Equal(1, result.Admitted);
        Assert.Equal(1, result.Failed);
        ExtendedSourceNamespace stored = Assert.IsType<ExtendedSourceNamespace>(
            new ExtendedSourceStore(_paths, result.ScopeId).TryLoad(SpecialSourceKind.ImageOcr));
        Assert.True(stored.IsKnownSource(available));
        Assert.False(stored.IsKnownSource(disconnected));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_UnavailableEngineDeletesNamespace()
    {
        Write("scan.png", "image");
        var engine = new FakeOcrEngine(_ => OcrResult.Ok("unused"), OcrResult.Fail("assets missing"));

        ImageOcrExtendedSourceBuildResult result = await OcrManager().BuildImageOcrExtendedSourceAsync(
            _corpus, Policy(), engine, Extensions(), "model", 960);

        Assert.Equal(ImageOcrExtendedSourceBuildStatus.SkippedEngineUnavailable, result.Status);
        Assert.Equal(1, result.ImagesSeen);
        Assert.Equal(1, result.Failed);
        Assert.Null(new ExtendedSourceStore(_paths, result.ScopeId).TryLoad(SpecialSourceKind.ImageOcr));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_ValidationRejectionPropagates()
    {
        Write("scan.png", "image");
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            checkpointReader: _ => null,
            ocrFingerprintReader: (_, _, _) => OcrFingerprint(),
            extendedSourceStoreFactory: RejectingStore);

        await Assert.ThrowsAsync<IOException>(() => manager.BuildImageOcrExtendedSourceAsync(
            _corpus, Policy(), new FakeOcrEngine(_ => OcrResult.Ok("recognized text")), Extensions(), "model", 960));
    }

    [Fact]
    public async Task BuildImageOcrExtendedSourceAsync_MissingRootAndCancellationPropagate()
    {
        ContentIndexManager manager = OcrManager();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => manager.BuildImageOcrExtendedSourceAsync(
            Path.Combine(_sandbox, "missing"), Policy(), new FakeOcrEngine(_ => OcrResult.Ok("text")), Extensions(), "model", 960));

        Write("cancel.png", "image");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.BuildImageOcrExtendedSourceAsync(
            _corpus, Policy(), new FakeOcrEngine(_ => OcrResult.Ok("text")), Extensions(), "model", 960, cancellation.Token));
    }

    [Fact]
    public void BuildScope_IncompleteCrawlDoesNotPublish()
    {
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ => throw new IOException("injected crawl failure"),
        };
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            volumeBindingReader: _ => CurrentBinding(),
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            journalInfoReader: _ => new UsnJournalInfo(7, 0, 200, 0),
            crawlerFileSystem: fileSystem);

        IOException error = Assert.Throws<IOException>(() => manager.BuildScope(_corpus, Policy()));

        Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.HasCurrentIndex(_corpus));
    }

    [Theory]
    [InlineData("end-unavailable")]
    [InlineData("volume-changed")]
    [InlineData("checkpoint-missing")]
    [InlineData("journal-unavailable")]
    [InlineData("journal-changed")]
    [InlineData("journal-gap")]
    [InlineData("checkpoint-ahead")]
    public void BuildScope_RejectsUnstableCompletionBarrierAndPreservesPriorGeneration(string failure)
    {
        Write("a.txt", "original indexed text");
        ContentIndexManager stable = BuildManager();
        BuildScopeResult prior = stable.BuildScope(_corpus, Policy());
        Write("b.txt", "new text must not publish");

        int volumeReads = 0;
        VolumeBinding binding = CurrentBinding();
        Func<string, VolumeBinding?> volumeReader = _ =>
        {
            volumeReads++;
            if (volumeReads == 1)
                return binding;
            return failure switch
            {
                "end-unavailable" => null,
                "volume-changed" => binding with { VolumeSerialNumber = binding.VolumeSerialNumber + 1 },
                _ => binding,
            };
        };
        UsnCheckpoint checkpoint = failure == "checkpoint-missing"
            ? UsnCheckpoint.None
            : new UsnCheckpoint(7, 100);
        Func<string, UsnJournalInfo?> journalReader = _ => failure switch
        {
            "journal-unavailable" => null,
            "journal-changed" => new UsnJournalInfo(8, 0, 200, 0),
            "journal-gap" => new UsnJournalInfo(7, 101, 200, 101),
            "checkpoint-ahead" => new UsnJournalInfo(7, 0, 99, 0),
            _ => new UsnJournalInfo(7, 0, 200, 0),
        };
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            volumeBindingReader: volumeReader,
            checkpointReader: _ => checkpoint,
            journalInfoReader: journalReader);

        Assert.Throws<IndexVolumeChangedException>(() => manager.BuildScope(_corpus, Policy()));

        ContentIndexGeneration? current = new ContentIndexStore(_paths, prior.ScopeId).TryOpenCurrent();
        Assert.NotNull(current);
        Assert.Equal(1, current!.Manifest.ContentCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildScope_RejectsUnavailableOrUnsupportedStartingVolume(bool unavailable)
    {
        Write("a.txt", "content");
        VolumeBinding binding = CurrentBinding();
        var manager = new ContentIndexManager(_paths, 2, contentReader: null,
            volumeBindingReader: _ => unavailable ? null : binding with { FileSystemName = "exFAT" },
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            journalInfoReader: _ => new UsnJournalInfo(7, 0, 200, 0));

        if (unavailable)
            Assert.Throws<IndexVolumeChangedException>(() => manager.BuildScope(_corpus, Policy()));
        else
            Assert.Throws<NotSupportedException>(() => manager.BuildScope(_corpus, Policy()));
    }

    [Fact]
    public void BuildScope_UsesPeriodicDiskChecksAndProgressCallbacks()
    {
        Write("a.txt", "first indexed content");
        Write("b.txt", "second indexed content");
        int diskChecks = 0;
        var progress = new List<IndexBuildProgress>();
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            volumeBindingReader: _ => CurrentBinding(),
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            journalInfoReader: _ => new UsnJournalInfo(7, 0, 200, 0),
            progressEveryFiles: 1,
            diskCheckEveryFiles: 1);

        BuildScopeResult result = manager.BuildScope(
            _corpus,
            Policy(),
            maxDiskUsagePercent: 90,
            diskUsedPercentProbe: _ =>
            {
                diskChecks++;
                return 0;
            },
            progress: progress.Add);

        Assert.Equal(2, result.Report.IndexedCount);
        Assert.True(diskChecks >= 4);
        Assert.Contains(progress, point => point.FilesCrawled == 1);
        Assert.Equal(2, progress[^1].FilesCrawled);
    }

    [Fact]
    public void BuildScope_SourceDeletedAfterCrawlFailsCompletionBarrier()
    {
        var externalPaths = new DefaultContentIndexPathProvider(
            Path.Combine(_sandbox, "external-index"),
            Path.Combine(_sandbox, "external-temp"));
        var fileSystem = new IndexCrawlerFileSystem
        {
            EnumerateEntries = _ =>
            {
                Directory.Delete(_corpus, recursive: true);
                return Array.Empty<IndexCrawlEntry>();
            },
        };
        VolumeBinding binding = CurrentBinding();
        var manager = new ContentIndexManager(
            externalPaths,
            2,
            contentReader: null,
            volumeBindingReader: _ => binding,
            checkpointReader: _ => new UsnCheckpoint(7, 100),
            journalInfoReader: _ => new UsnJournalInfo(7, 0, 200, 0),
            crawlerFileSystem: fileSystem);

        IndexVolumeChangedException error = Assert.Throws<IndexVolumeChangedException>(
            () => manager.BuildScope(_corpus, Policy()));

        Assert.Contains("disconnected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(manager.HasCurrentIndex(_corpus));
    }

    [Fact]
    public void BuildScope_DefaultDiskProbeAndMissingCheckpointFailClosed()
    {
        Write("a.txt", "indexed content");
        var manager = new ContentIndexManager(
            _paths,
            2,
            contentReader: null,
            volumeBindingReader: _ => CurrentBinding(),
            checkpointReader: _ => null,
            journalInfoReader: _ => new UsnJournalInfo(7, 0, 200, 0));

        IndexVolumeChangedException error = Assert.Throws<IndexVolumeChangedException>(
            () => manager.BuildScope(_corpus, Policy(), maxDiskUsagePercent: 101));

        Assert.Contains("no trustworthy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void BuildScope_HandlesConfiguredFileIoTimeouts(int timeoutMilliseconds)
    {
        Write("a.txt", "indexed content");

        BuildScopeResult result = BuildManager().BuildScope(
            _corpus,
            Policy(),
            fileIoTimeout: TimeSpan.FromMilliseconds(timeoutMilliseconds));

        Assert.Equal(1, result.Report.IndexedCount);
    }

    private ContentIndexManager OcrManager() => new(
        _paths,
        2,
        contentReader: null,
        checkpointReader: _ => new UsnCheckpoint(7, 100),
        ocrFingerprintReader: (_, _, _) => OcrFingerprint());

    private ContentIndexManager BuildManager() => new(
        _paths,
        2,
        contentReader: null,
        volumeBindingReader: _ => CurrentBinding(),
        checkpointReader: _ => new UsnCheckpoint(7, 100),
        journalInfoReader: _ => new UsnJournalInfo(7, 0, 200, 0));

    private string CreateTool()
    {
        string path = Path.Combine(_sandbox, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private ExtendedSourceStore RejectingStore(string scopeId)
    {
        var store = new ExtendedSourceStore(_paths, scopeId);
        store.BeforeValidation = directory =>
            File.Delete(Path.Combine(directory, ExtendedSourceNamespaceSerializer.ContentFile));
        return store;
    }

    private void Write(string relativePath, string content)
    {
        string path = Path.Combine(_corpus, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static IndexIngestionPolicy Policy(long maxBytes = 0, bool includeCloudOnlyFiles = false)
        => new(maxBytes, null, null, true, false, 0, includeCloudOnlyFiles: includeCloudOnlyFiles);

    private static HashSet<string> Extensions()
        => new(["png", "jpg"], StringComparer.OrdinalIgnoreCase);

    private VolumeBinding CurrentBinding()
        => VolumeBindingReader.TryCapture(_corpus)
            ?? throw new InvalidOperationException("The test corpus volume could not be identified.");

    private static ExtractorFingerprint OcrFingerprint() => new(
        SpecialSourceKind.ImageOcr,
        OcrEngineFactory.PaddleId,
        string.Empty,
        "cpu",
        [new ExtractorFileHash("worker", "deadbeef")],
        [new("model", "model"), new("maxSide", "960")]);

    private static void InvokeHydrateCloudFile(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        var method = typeof(ContentIndexManager).GetMethod(
            "HydrateCloudFile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method!.Invoke(null, [source, destination, cancellationToken]);
    }

    private IDisposable CreateHydrationSession(
        string source,
        bool isCloudOnly,
        Func<string, string, CancellationToken, bool> hydrator)
    {
        Type candidateType = typeof(ContentIndexManager).GetNestedType(
            "ExtendedSourceCandidate",
            System.Reflection.BindingFlags.NonPublic)!;
        Array candidates = Array.CreateInstance(candidateType, 1);
        candidates.SetValue(Activator.CreateInstance(
            candidateType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args: [source, 1L, isCloudOnly],
            culture: null), 0);

        Type sessionType = typeof(ContentIndexManager).GetNestedType(
            "CloudExtendedSourceHydrationSession",
            System.Reflection.BindingFlags.NonPublic)!;
        return (IDisposable)Activator.CreateInstance(
            sessionType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args:
            [
                candidates,
                Path.Combine(_paths.GetScopeDirectory(ContentIndexManager.ScopeIdForRoot(_corpus)), ".reflect-hydration"),
                TimeSpan.FromSeconds(5),
                0,
                0,
                hydrator,
            ],
            culture: null)!;
    }

    private static object CreateHydratingOcrEngine(IOcrEngine inner, IDisposable hydration)
    {
        Type wrapperType = typeof(ContentIndexManager).GetNestedType(
            "HydratingOcrEngine",
            System.Reflection.BindingFlags.NonPublic)!;
        return Activator.CreateInstance(
            wrapperType,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args: [inner, hydration],
            culture: null)!;
    }

    private sealed class FakePdfExtractor(
        string toolPath,
        Func<string, int, PdfTextResult> extract) : PdfTextExtractor(toolPath)
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

        public override Task<PdfTextResult> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
        {
            int call = _calls.TryGetValue(pdfPath, out int previous) ? previous : 0;
            _calls[pdfPath] = call + 1;
            return Task.FromResult(extract(pdfPath, call));
        }
    }

    private sealed class FakeOcrEngine(Func<string, OcrResult> recognize, OcrResult? ready = null) : IOcrEngine
    {
        public string Id => OcrEngineFactory.PaddleId;
        public string DisplayName => "Fake OCR";
        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
            => Task.FromResult(ready ?? OcrResult.Ok(string.Empty));
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult(recognize(imagePath));
    }
}