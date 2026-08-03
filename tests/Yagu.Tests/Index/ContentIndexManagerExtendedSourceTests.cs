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

    private static IndexIngestionPolicy Policy(long maxBytes = 0)
        => new(maxBytes, null, null, true, false, 0);

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