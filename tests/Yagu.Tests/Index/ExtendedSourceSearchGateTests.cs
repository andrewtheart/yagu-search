using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Yagu.Models;
using Yagu.Services;
using Yagu.Services.Index;
using Yagu.Services.Ocr;
using Yagu.Services.Pdf;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ExtendedSourceSearchGate"/> (plan §7 Phase 4) — the query-time router the search
/// pipeline consults per discovered image/PDF. It prunes only a trusted, fresh, fingerprint-matched
/// deterministic nonmember; members, OCR sources, changed sources, fingerprint mismatches, unknown kinds,
/// and any error all extract. B1 reconciliation re-extracts a pruned source whose file changed after B0.
/// </summary>
public sealed class ExtendedSourceSearchGateTests
{
    private const string Word = "zephyrqux";
    private static readonly UsnFileIdentity IdMember = new(1, 0);
    private static readonly UsnFileIdentity IdNonmember = new(2, 0);

    private static ExtractorFingerprint Fp(SpecialSourceKind k = SpecialSourceKind.PdfText, string version = "1")
        => new(k, "pdftotext", version, "cpu");

    private static TrigramExpression Query()
    {
        ContentRepresentation.Classify(Encoding.UTF8.GetBytes(Word), out IReadOnlyList<Trigram> t);
        return TrigramExpression.OfTrigram(t[0]);
    }

    // m.pdf contains the query word (member, id 1); n.pdf does not (nonmember, id 2). Built under C:\ @ (1,100).
    private static ExtendedSourceNamespace BuildPdfNs()
    {
        var b = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp());
        b.AddSource(@"C:\m.pdf", new ExtractionOutcome.Success($"{Word} report"), IdMember);
        b.AddSource(@"C:\n.pdf", new ExtractionOutcome.Success("ordinary text"), IdNonmember);
        return b.Build(@"C:\", new UsnCheckpoint(1, 100));
    }

    private static ExtendedSourceSearchGate Gate(
        ExtendedSourceNamespace ns, ExtractorFingerprint currentFp, ExtendedSourceFreshnessEvaluator.JournalReader reader)
        => ExtendedSourceSearchGate.Create(
            new Dictionary<SpecialSourceKind, (ExtendedSourceNamespace, ExtractorFingerprint)> { [ns.Kind] = (ns, currentFp) },
            Query(), reader);

    private static ExtendedSourceFreshnessEvaluator.JournalReader OkReader(params UsnChange[] changes)
        => (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, changes);

    [Fact]
    public void ConstructorAndCreate_RejectNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => new ExtendedSourceSearchGate(null!));
        Assert.Throws<ArgumentNullException>(() => ExtendedSourceSearchGate.Create(null!, Query()));
        Assert.Throws<ArgumentNullException>(() => ExtendedSourceSearchGate.Create(
            new Dictionary<SpecialSourceKind, (ExtendedSourceNamespace, ExtractorFingerprint)>(), null!));
    }

    [Fact]
    public void Member_IsExtracted()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\m.pdf", out bool prioritized));
        Assert.True(prioritized);
        Assert.Equal(0, gate.TotalPruned);
    }

    [Fact]
    public void UnknownSource_IsExtractedWithoutPriority()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());

        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\new.pdf", out bool prioritized));
        Assert.False(prioritized);
    }

    [Fact]
    public void FreshDeterministicNonmember_IsPruned()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());
        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        Assert.Equal(1, gate.TotalPruned);
        Assert.Equal(1, gate.PrunedCount);
    }

    [Fact]
    public void ChangedNonmember_IsExtracted_NotPruned()
    {
        // n.pdf's file changed since B0 → dirty → must extract even though it is a nonmember.
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader(new UsnChange(IdNonmember, 0x1)));
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        Assert.Equal(0, gate.TotalPruned);
    }

    [Fact]
    public void FingerprintMismatch_ExtractsEverything()
    {
        // A different extractor version than the one that built the namespace → never prune.
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(version: "2"), OkReader());
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        Assert.Equal(0, gate.TotalPruned);
    }

    [Fact]
    public void OcrNonmember_IsNeverPruned()
    {
        var b = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.ImageOcr, Fp(SpecialSourceKind.ImageOcr));
        b.AddSource(@"C:\a.png", new ExtractionOutcome.Success($"{Word} sign"), new UsnFileIdentity(5, 0));
        b.AddSource(@"C:\b.png", new ExtractionOutcome.Success("plain caption"), new UsnFileIdentity(6, 0));
        ExtendedSourceSearchGate gate = Gate(b.Build(@"C:\", new UsnCheckpoint(1, 100)), Fp(SpecialSourceKind.ImageOcr), OkReader());

        Assert.True(gate.ShouldExtract(SpecialSourceKind.ImageOcr, @"C:\b.png"));
        Assert.Equal(0, gate.TotalPruned);
    }

    [Fact]
    public void UnknownKind_IsExtracted()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());
        Assert.True(gate.ShouldExtract(SpecialSourceKind.ImageOcr, @"C:\x.png")); // no OCR namespace
    }

    [Fact]
    public void ClassificationError_FailsSafe_DisablesAndExtracts()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, null!)); // NormalizePath throws → fail-safe
        Assert.True(gate.PruningDisabled);
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf")); // stays disabled → extract
    }

    [Fact]
    public void Empty_NeverPrunes()
    {
        var gate = ExtendedSourceSearchGate.Create(
            new Dictionary<SpecialSourceKind, (ExtendedSourceNamespace, ExtractorFingerprint)>(), Query());
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        Assert.Equal(0, gate.TotalPruned);
    }

    [Fact]
    public void B1_QuiescentJournal_RescuesNothing()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());
        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf")); // pruned
        Assert.Empty(gate.GetSourcesToRescan());
    }

    [Fact]
    public void B1_SourceChangedAfterB0_IsRescued()
    {
        // Stateful reader: B0 (call 1) quiescent → n.pdf pruned; B1 (call 2) shows n.pdf changed → rescued.
        int calls = 0;
        ExtendedSourceFreshnessEvaluator.JournalReader stateful = (_, since) =>
        {
            calls++;
            UsnChange[] changes = calls >= 2 ? [new UsnChange(IdNonmember, 0x1)] : [];
            return new UsnReadResult(UsnReadStatus.Ok, since, changes);
        };
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), stateful);

        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        IReadOnlyList<string> rescue = gate.GetSourcesToRescan();
        Assert.Contains(@"C:\n.pdf", rescue);
    }

    [Fact]
    public void B1_JournalDiscontinuity_RescuesAllPruned()
    {
        int calls = 0;
        ExtendedSourceFreshnessEvaluator.JournalReader stateful = (_, since) =>
        {
            calls++;
            UsnReadStatus status = calls >= 2 ? UsnReadStatus.GapDetected : UsnReadStatus.Ok;
            return new UsnReadResult(status, since, []);
        };
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), stateful);

        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        Assert.Contains(@"C:\n.pdf", gate.GetSourcesToRescan());
    }

    [Fact]
    public void B1_OutOfMemory_IsNotHiddenByLiveExtractionFallback()
    {
        int calls = 0;
        ExtendedSourceFreshnessEvaluator.JournalReader reader = (_, since) =>
        {
            if (++calls >= 2)
                throw new OutOfMemoryException("extended-source journal oom");
            return new UsnReadResult(UsnReadStatus.Ok, since, []);
        };
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), reader);
        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));
        Assert.Throws<OutOfMemoryException>(() => gate.GetSourcesToRescan());
    }

    [Fact]
    public void B1_OrdinaryFailure_RescuesEveryPrunedSource()
    {
        int calls = 0;
        ExtendedSourceFreshnessEvaluator.JournalReader reader = (_, since) =>
        {
            if (++calls >= 2)
                throw new IOException("journal failed");
            return new UsnReadResult(UsnReadStatus.Ok, since, []);
        };
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), reader);
        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf"));

        Assert.Equal([@"C:\n.pdf"], gate.GetSourcesToRescan());
        Assert.Equal(0, gate.PrunedCount);
    }

    [Fact]
    public void GetSourcesToRescan_AfterPruningDisabled_DrainsEveryPrunedPath()
    {
        ExtendedSourceSearchGate gate = Gate(BuildPdfNs(), Fp(), OkReader());
        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\n.pdf")); // prune n.pdf
        gate.ShouldExtract(SpecialSourceKind.PdfText, null!);                      // error → PruningDisabled
        Assert.True(gate.PruningDisabled);

        IReadOnlyList<string> drained = gate.GetSourcesToRescan();                 // PruningDisabled → drain all
        Assert.Contains(@"C:\n.pdf", drained);
        Assert.Equal(0, gate.PrunedCount);
    }

    // ---- TryCreate: the GUI/CLI factory (never throws except ArgumentNull; any failure → null = extract live) ----

    private sealed class TempPathProvider(string indexRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => Path.Combine(IndexRoot, scopeId);
    }

    private sealed class ThrowingScopePathProvider(string indexRoot) : IContentIndexPathProvider
    {
        public string IndexRoot { get; } = indexRoot;
        public string GetScopeDirectory(string scopeId) => throw new InvalidOperationException("scope resolution failed");
    }

    private static SearchOptions OcrOptions() => new()
    {
        Directory = @"C:\r",
        Query = Word,
        CaseSensitive = true,
        UseContentIndex = true,
        SearchImageText = true,
    };

    private static void WithWorkerEnv(string? value, Action body)
    {
        string? previous = Environment.GetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerOcrEngine.WorkerPathEnvVar, previous);
        }
    }

    [Fact]
    public void TryCreate_NullArguments_Throw()
    {
        var paths = new TempPathProvider(Path.GetTempPath());
        var options = new SearchOptions { Directory = @"C:\r", Query = Word, UseContentIndex = true };
        var settings = new AppSettings { EnableContentIndex = true };
        Assert.Throws<ArgumentNullException>(() => ExtendedSourceSearchGate.TryCreate(null!, @"C:\r", options, settings));
        Assert.Throws<ArgumentNullException>(() => ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", null!, settings));
        Assert.Throws<ArgumentNullException>(() => ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", options, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_BlankRoot_ReturnsNull(string root)
    {
        var paths = new TempPathProvider(Path.GetTempPath());
        Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, root, OcrOptions(),
            new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true }));
    }

    [Fact]
    public void TryCreate_ContentIndexOff_ReturnsNull()
    {
        var paths = new TempPathProvider(Path.GetTempPath());
        // settings.EnableContentIndex = false.
        Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", OcrOptions(),
            new AppSettings { EnableContentIndex = false, IndexBuildImageTextExtendedSource = true }));
        // options.UseContentIndex = false.
        Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r",
            new SearchOptions { Directory = @"C:\r", Query = Word, UseContentIndex = false, SearchImageText = true },
            new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true }));
    }

    [Fact]
    public void TryCreate_NoExtendedSourceRequested_ReturnsNull()
    {
        var paths = new TempPathProvider(Path.GetTempPath());
        // Neither PDF nor image-text extraction requested → no namespaces → null.
        var options = new SearchOptions { Directory = @"C:\r", Query = Word, UseContentIndex = true };
        var settings = new AppSettings
        {
            EnableContentIndex = true,
            IndexBuildPdfTextExtendedSource = true,
            IndexBuildImageTextExtendedSource = true,
        };
        Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", options, settings));
    }

    [Fact]
    public void TryCreate_DefaultPdfExtractorWithoutNamespace_ReturnsNull()
    {
        var paths = new TempPathProvider(Path.GetTempPath());
        var options = new SearchOptions
        {
            Directory = @"C:\r",
            Query = Word,
            UseContentIndex = true,
            SearchPdfText = true,
        };
        var settings = new AppSettings { EnableContentIndex = true, IndexBuildPdfTextExtendedSource = true };

        Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", options, settings));
    }

    [Fact]
    public void TryCreate_OcrFingerprintUnavailable_ReturnsNull()
    {
        var paths = new TempPathProvider(Path.GetTempPath());
        var settings = new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true };
        string missing = Path.Combine(Path.GetTempPath(), "yagu-esg-missing-" + Guid.NewGuid().ToString("N") + ".bin");
        // No OCR worker → fingerprint null → OCR namespace never loaded → null.
        WithWorkerEnv(missing, () =>
            Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", OcrOptions(), settings)));
    }

    [Fact]
    public void TryCreate_OcrNamespaceNotPersisted_ReturnsNull()
    {
        string indexRoot = Path.Combine(Path.GetTempPath(), "yagu-esg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(indexRoot);
        string worker = Path.Combine(Path.GetTempPath(), "yagu-esg-worker-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "worker");
        try
        {
            var paths = new TempPathProvider(indexRoot);
            var settings = new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true };
            // Fingerprint computes (worker exists) but no namespace is persisted under the scope dir → null.
            WithWorkerEnv(worker, () =>
                Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", OcrOptions(), settings)));
        }
        finally
        {
            File.Delete(worker);
            Directory.Delete(indexRoot, recursive: true);
        }
    }

    [Fact]
    public void TryCreate_WhenScopeResolutionThrows_ReturnsNull_FailSafe()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-esg-worker-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "worker");
        try
        {
            var paths = new ThrowingScopePathProvider(Path.GetTempPath());
            var settings = new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true };
            // Fingerprint computes → reaches ExtendedSourceStore ctor → GetScopeDirectory throws → caught → null.
            WithWorkerEnv(worker, () =>
                Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", OcrOptions(), settings)));
        }
        finally
        {
            File.Delete(worker);
        }
    }

    [Fact]
    public void TryCreate_PersistedPdfNamespace_LoadsGate()
    {
        string indexRoot = Path.Combine(Path.GetTempPath(), "yagu-esg-pdf-" + Guid.NewGuid().ToString("N"));
        string tool = Path.Combine(indexRoot, "pdftotext.exe");
        Directory.CreateDirectory(indexRoot);
        File.WriteAllText(tool, "pdf tool");
        try
        {
            var paths = new TempPathProvider(indexRoot);
            var extractor = new PdfTextExtractor(tool);
            ExtractorFingerprint fingerprint = Assert.IsType<ExtractorFingerprint>(PdfExtractorFingerprint.TryCompute(extractor));
            PersistNamespace(paths, BuildNamespace(SpecialSourceKind.PdfText, fingerprint));
            var options = new SearchOptions
            {
                Directory = @"C:\r",
                Query = Word,
                CaseSensitive = true,
                UseContentIndex = true,
                SearchPdfText = true,
            };
            var settings = new AppSettings { EnableContentIndex = true, IndexBuildPdfTextExtendedSource = true };

            Assert.NotNull(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", options, settings, extractor));
        }
        finally
        {
            Directory.Delete(indexRoot, recursive: true);
        }
    }

    [Fact]
    public void TryCreate_PersistedOcrNamespace_LoadsGateAndRejectsIneligibleQuery()
    {
        string indexRoot = Path.Combine(Path.GetTempPath(), "yagu-esg-ocr-" + Guid.NewGuid().ToString("N"));
        string worker = Path.Combine(indexRoot, "ocr-worker.exe");
        Directory.CreateDirectory(indexRoot);
        File.WriteAllText(worker, "ocr worker");
        try
        {
            WithWorkerEnv(worker, () =>
            {
                var paths = new TempPathProvider(indexRoot);
                SearchOptions options = OcrOptions();
                ExtractorFingerprint fingerprint = Assert.IsType<ExtractorFingerprint>(
                    ImageOcrExtractorFingerprint.TryCompute(
                        options.ImageOcrEngine, options.ImageOcrModel, options.ImageOcrMaxSide));
                PersistNamespace(paths, BuildNamespace(SpecialSourceKind.ImageOcr, fingerprint));
                var settings = new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true };

                Assert.NotNull(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", options, settings));

                var ineligible = new SearchOptions
                {
                    Directory = @"C:\r",
                    Query = "xy",
                    CaseSensitive = true,
                    UseContentIndex = true,
                    SearchImageText = true,
                };
                Assert.Null(ExtendedSourceSearchGate.TryCreate(paths, @"C:\r", ineligible, settings));
            });
        }
        finally
        {
            Directory.Delete(indexRoot, recursive: true);
        }
    }

    [Fact]
    public void TryCreate_OutOfMemoryPropagates()
    {
        string worker = Path.Combine(Path.GetTempPath(), "yagu-esg-worker-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(worker, "worker");
        try
        {
            WithWorkerEnv(worker, () => Assert.Throws<OutOfMemoryException>(() =>
                ExtendedSourceSearchGate.TryCreate(
                    new OutOfMemoryPathProvider(),
                    @"C:\r",
                    OcrOptions(),
                    new AppSettings { EnableContentIndex = true, IndexBuildImageTextExtendedSource = true })));
        }
        finally
        {
            File.Delete(worker);
        }
    }

    private sealed class OutOfMemoryPathProvider : IContentIndexPathProvider
    {
        public string IndexRoot => throw new OutOfMemoryException("pressure");
        public string GetScopeDirectory(string scopeId) => throw new OutOfMemoryException("pressure");
    }

    private static ExtendedSourceNamespace BuildNamespace(SpecialSourceKind kind, ExtractorFingerprint fingerprint)
    {
        var builder = new ExtendedSourceNamespaceBuilder(kind, fingerprint);
        string path = kind == SpecialSourceKind.PdfText ? @"C:\sample.pdf" : @"C:\sample.png";
        builder.AddSource(path, new ExtractionOutcome.Success($"{Word} report"), new UsnFileIdentity(9, 0));
        return builder.Build(@"C:\r", new UsnCheckpoint(1, 100));
    }

    private static void PersistNamespace(TempPathProvider paths, ExtendedSourceNamespace sourceNamespace)
    {
        string scopeId = ContentIndexManager.ScopeIdForRoot(@"C:\r");
        var store = new ExtendedSourceStore(paths, scopeId);
        ExtendedSourceNamespaceSerializer.Write(store.NamespaceDirectory(sourceNamespace.Kind), sourceNamespace);
    }
}
