using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Yagu.Services.Index;
using Yagu.Services.Pdf;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the PDF-text extended-source build slice (plan §7 Phase 4): the extractor fingerprint, the
/// determinism repeatability proof, the namespace populator, the on-disk store, and the critical
/// "unknown source is never pruned" gate guard that keeps a nonmember classification from silently hiding a
/// PDF that was never indexed.
/// </summary>
public sealed class PdfExtendedSourceTests
{
    // ─────────────────────────── a canned in-memory PDF extractor ───────────────────────────

    private sealed class FakePdfExtractor : PdfTextExtractor
    {
        private readonly Func<string, int, PdfTextResult> _fn;
        private readonly Dictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
        public FakePdfExtractor(Func<string, int, PdfTextResult> fn) : base(toolPathOverride: null) => _fn = fn;

        public override Task<PdfTextResult> ExtractAsync(string pdfPath, CancellationToken cancellationToken)
        {
            int n = _calls.TryGetValue(pdfPath, out int prev) ? prev : 0;
            _calls[pdfPath] = n + 1;
            return Task.FromResult(_fn(pdfPath, n));
        }
    }

    private static ExtractorFingerprint PdfFp(string exeHash = "deadbeef") =>
        new(SpecialSourceKind.PdfText, "pdftotext", "", "cpu",
            binaryHashes: [new ExtractorFileHash("exe", exeHash)],
            options: [new("enc", "UTF-8"), new("eol", "unix")]);

    // ─────────────────────────── PdfExtractorFingerprint ───────────────────────────

    [Fact]
    public void Fingerprint_HashesTheToolBinary_And_ChangesWhenBinaryChanges()
    {
        string a = Path.Combine(Path.GetTempPath(), "yagu-pdftool-" + Guid.NewGuid().ToString("N") + ".exe");
        string b = Path.Combine(Path.GetTempPath(), "yagu-pdftool-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(a, [1, 2, 3, 4]);
        File.WriteAllBytes(b, [9, 9, 9, 9]);
        try
        {
            ExtractorFingerprint? fpA = PdfExtractorFingerprint.TryCompute(new PdfTextExtractor(a));
            ExtractorFingerprint? fpA2 = PdfExtractorFingerprint.TryCompute(new PdfTextExtractor(a));
            ExtractorFingerprint? fpB = PdfExtractorFingerprint.TryCompute(new PdfTextExtractor(b));

            Assert.NotNull(fpA);
            Assert.Equal(SpecialSourceKind.PdfText, fpA!.Source);
            Assert.True(fpA.Matches(fpA2));        // identical binary → identical fingerprint
            Assert.False(fpA.Matches(fpB));        // a different pdftotext.exe → different fingerprint (never prunes)
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public void Fingerprint_Null_WhenToolMissing()
    {
        Assert.Null(PdfExtractorFingerprint.TryCompute(new PdfTextExtractor(@"Z:\does\not\exist.exe")));
    }

    [Fact]
    public void Fingerprint_Null_WhenResolvedToolCannotBeHashed()
    {
        string path = Path.Combine(Path.GetTempPath(), "yagu-pdftool-locked-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        try
        {
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Null(PdfExtractorFingerprint.TryCompute(new PdfTextExtractor(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ─────────────────────────── PdfExtractionDeterminism ───────────────────────────

    [Fact]
    public async Task Determinism_Deterministic_WhenRepeatsMatch()
    {
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            [@"C:\a.pdf", @"C:\b.pdf"],
            (path, _) => Task.FromResult(PdfTextResult.Ok("stable " + path)));
        Assert.Equal(PdfDeterminismVerdict.Deterministic, verdict);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenRepeatDiffers()
    {
        int calls = 0;
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            [@"C:\a.pdf"],
            (_, _) => Task.FromResult(PdfTextResult.Ok("text-" + Interlocked.Increment(ref calls))));
        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenNoSuccessfulSample()
    {
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            [@"C:\a.pdf", @"C:\b.pdf"],
            (_, _) => Task.FromResult(PdfTextResult.Fail("boom")));
        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenEmptyCandidates()
    {
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            Array.Empty<string>(), (_, _) => Task.FromResult(PdfTextResult.Ok("x")));
        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenSampleBudgetIsZero()
    {
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            new[] { @"C:\a.pdf" },
            (_, _) => throw new InvalidOperationException("extract should not run"),
            maxSamples: 0);

        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
    }

    [Fact]
    public async Task Determinism_StopsAfterSampleBudget()
    {
        int calls = 0;
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            new[] { @"C:\a.pdf", @"C:\b.pdf" },
            (_, _) =>
            {
                calls++;
                return Task.FromResult(PdfTextResult.Ok("stable"));
            },
            maxSamples: 1);

        Assert.Equal(PdfDeterminismVerdict.Deterministic, verdict);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenFirstExtractionThrows()
    {
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            new[] { @"C:\a.pdf" },
            (_, _) => throw new InvalidOperationException("first extraction failed"));

        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenReproductionThrows()
    {
        int calls = 0;
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            new[] { @"C:\a.pdf" },
            (_, _) => ++calls == 1
                ? Task.FromResult(PdfTextResult.Ok("stable"))
                : throw new InvalidOperationException("reproduction failed"));

        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Determinism_NotProven_WhenReproductionReturnsFailure()
    {
        int calls = 0;
        var verdict = await PdfExtractionDeterminism.ProbeAsync(
            new[] { @"C:\a.pdf" },
            (_, _) => Task.FromResult(++calls == 1
                ? PdfTextResult.Ok("stable")
                : PdfTextResult.Fail("reproduction failed")));

        Assert.Equal(PdfDeterminismVerdict.NotProven, verdict);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Determinism_FirstExtractionCancellation_Propagates()
        => await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PdfExtractionDeterminism.ProbeAsync(
                new[] { @"C:\a.pdf" },
                (_, _) => throw new OperationCanceledException()));

    [Fact]
    public async Task Determinism_ReproductionCancellation_Propagates()
    {
        int calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            PdfExtractionDeterminism.ProbeAsync(
                new[] { @"C:\a.pdf" },
                (_, _) => ++calls == 1
                    ? Task.FromResult(PdfTextResult.Ok("stable"))
                    : throw new OperationCanceledException()));

        Assert.Equal(2, calls);
    }

    // ─────────────────────────── PdfExtendedSourcePopulator ───────────────────────────

    [Fact]
    public void Populator_ConstructorRejectsInvalidDependencies()
    {
        var extractor = new FakePdfExtractor((_, _) => PdfTextResult.Ok("text"));
        ExtractorFingerprint fingerprint = PdfFp();

        Assert.Throws<ArgumentNullException>(() => new PdfExtendedSourcePopulator(null!, fingerprint, _ => null));
        Assert.Throws<ArgumentNullException>(() => new PdfExtendedSourcePopulator(extractor, null!, _ => null));
        Assert.Throws<ArgumentException>(() => new PdfExtendedSourcePopulator(
            extractor,
            new ExtractorFingerprint(SpecialSourceKind.ImageOcr, "ocr", "1", "cpu"),
            _ => null));
        Assert.Throws<ArgumentNullException>(() => new PdfExtendedSourcePopulator(extractor, fingerprint, null!));
    }

    [Fact]
    public async Task Populator_NullFailureAndThrowingIdentityProvider_DegradeSafely()
    {
        var extractor = new FakePdfExtractor((_, _) => new PdfTextResult(false, string.Empty, null));
        var populator = new PdfExtendedSourcePopulator(
            extractor, PdfFp(), _ => throw new IOException("identity unavailable"));

        PdfPopulationResult result = await populator.PopulateAsync(
            [@"C:\bad.pdf"], @"C:\", new UsnCheckpoint(1, 100));

        Assert.Equal(0, result.Admitted);
        Assert.False(result.Namespace.IsKnownSource(@"C:\bad.pdf"));
    }

    [Fact]
    public async Task Populator_AdmitsExtractedPdfs_AndProvesDeterminism()
    {
        var extractor = new FakePdfExtractor((path, _) =>
            path.EndsWith("bad.pdf", StringComparison.OrdinalIgnoreCase)
                ? PdfTextResult.Fail("open error")
                : PdfTextResult.Ok("zephyrqux content of " + path));
        var progress = new List<PdfBuildProgress>();
        var populator = new PdfExtendedSourcePopulator(extractor, PdfFp(), _ => new UsnFileIdentity(7, 0));

        PdfPopulationResult result = await populator.PopulateAsync(
            [@"C:\one.pdf", @"C:\two.pdf", @"C:\bad.pdf"], @"C:\", new UsnCheckpoint(1, 100),
            progress: progress.Add);

        Assert.Equal(3, result.PdfsSeen);
        Assert.Equal(2, result.Admitted);                       // bad.pdf failed → not admitted
        Assert.Equal(PdfDeterminismVerdict.Deterministic, result.Determinism);
        Assert.True(result.IsPrunable);
        Assert.Equal(2, result.Namespace.SourceCount);
        Assert.True(result.Namespace.IsKnownSource(@"C:\one.pdf"));
        Assert.False(result.Namespace.IsKnownSource(@"C:\bad.pdf")); // a failed PDF is NOT a known source → live-extract
        Assert.Equal(new PdfBuildProgress(3, 3), progress[^1]);
    }

    [Fact]
    public async Task Populator_NotPrunable_WhenExtractionNotRepeatable()
    {
        int seq = 0;
        var extractor = new FakePdfExtractor((_, _) => PdfTextResult.Ok("varies-" + Interlocked.Increment(ref seq)));
        var populator = new PdfExtendedSourcePopulator(extractor, PdfFp(), _ => null);

        PdfPopulationResult result = await populator.PopulateAsync([@"C:\a.pdf"], @"C:\", new UsnCheckpoint(1, 100));

        Assert.Equal(PdfDeterminismVerdict.NotProven, result.Determinism);
        Assert.False(result.IsPrunable);
    }

    // ─────────────────────────── ExtendedSourceStore ───────────────────────────

    [Fact]
    public void Store_PublishAndLoad_RoundTrips_ThenDelete()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-1");
        Assert.Null(store.TryLoad(SpecialSourceKind.PdfText)); // nothing yet

        var b = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, PdfFp());
        b.AddSource(@"C:\m.pdf", new ExtractionOutcome.Success("zephyrqux here"), new UsnFileIdentity(1, 0));
        ExtendedSourceNamespace ns = b.Build(@"C:\", new UsnCheckpoint(1, 100));
        Assert.True(store.Publish(ns));

        ExtendedSourceNamespace? loaded = store.TryLoad(SpecialSourceKind.PdfText);
        Assert.NotNull(loaded);
        Assert.Equal(1, loaded!.SourceCount);
        Assert.True(loaded.IsKnownSource(@"C:\m.pdf"));
        Assert.True(loaded.Fingerprint.Matches(PdfFp()));

        store.Delete(SpecialSourceKind.PdfText);
        Assert.Null(store.TryLoad(SpecialSourceKind.PdfText));
    }

    [Fact]
    public void Store_DisabledState_BlocksStaleDirectoryUntilCompleteReplacementPublishes()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-disabled");
        var originalBuilder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, PdfFp("old"));
        originalBuilder.AddSource(@"C:\old.pdf", new ExtractionOutcome.Success("old text"), new UsnFileIdentity(1, 0));
        Assert.True(store.Publish(originalBuilder.Build(@"C:\", new UsnCheckpoint(1, 100))));

        store.Delete(SpecialSourceKind.PdfText);
        string disabled = store.DisabledMarkerPath(SpecialSourceKind.PdfText);
        Assert.True(File.Exists(disabled));

        // Even if stale namespace files reappear after an interrupted delete, the durable state wins.
        string live = store.NamespaceDirectory(SpecialSourceKind.PdfText);
        var staleBuilder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, PdfFp("stale"));
        staleBuilder.AddSource(@"C:\stale.pdf", new ExtractionOutcome.Success("stale text"), new UsnFileIdentity(2, 0));
        ExtendedSourceNamespaceSerializer.Write(live, staleBuilder.Build(@"C:\", new UsnCheckpoint(1, 110)));
        Assert.Null(store.TryLoad(SpecialSourceKind.PdfText));

        var replacementBuilder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, PdfFp("new"));
        replacementBuilder.AddSource(@"C:\new.pdf", new ExtractionOutcome.Success("new text"), new UsnFileIdentity(3, 0));
        Assert.True(store.Publish(replacementBuilder.Build(@"C:\", new UsnCheckpoint(1, 200))));

        Assert.False(File.Exists(disabled));
        ExtendedSourceNamespace replacement = store.TryLoad(SpecialSourceKind.PdfText)!;
        Assert.True(replacement.IsKnownSource(@"C:\new.pdf"));
        Assert.False(replacement.IsKnownSource(@"C:\stale.pdf"));
    }

    [Fact]
    public void Store_FailedValidation_KeepsPriorNamespaceAndReturnsFalse()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-validation");
        ExtendedSourceNamespace old = Namespace(@"C:\old.pdf", "old text", "old");
        Assert.True(store.Publish(old));

        store.BeforeValidation = temp =>
            File.Delete(Path.Combine(temp, ExtendedSourceNamespaceSerializer.ContentFile));
        Assert.False(store.Publish(Namespace(@"C:\new.pdf", "new text", "new")));

        ExtendedSourceNamespace current = store.TryLoad(SpecialSourceKind.PdfText)!;
        Assert.True(current.IsKnownSource(@"C:\old.pdf"));
        Assert.False(current.IsKnownSource(@"C:\new.pdf"));
    }

    [Fact]
    public void Store_PreValidationIoFailure_KeepsPriorNamespaceWithoutCreatingABackup()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-pre-validation-failure");
        Assert.True(store.Publish(Namespace(@"C:\old.pdf", "old text", "old")));
        store.BeforeValidation = _ => throw new IOException("pre-validation failure");

        Assert.False(store.Publish(Namespace(@"C:\new.pdf", "new text", "new")));

        ExtendedSourceNamespace current = store.TryLoad(SpecialSourceKind.PdfText)!;
        Assert.True(current.IsKnownSource(@"C:\old.pdf"));
        Assert.DoesNotContain(
            Directory.GetDirectories(Path.GetDirectoryName(store.NamespaceDirectory(SpecialSourceKind.PdfText))!),
            path => Path.GetFileName(path).StartsWith(ExtendedSourceStore.BackupPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Store_InstallMoveFailure_RestoresBackupAndReturnsFalse()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-move-failure");
        Assert.True(store.Publish(Namespace(@"C:\old.pdf", "old text", "old")));
        int moves = 0;
        store.MoveDirectory = (source, destination) =>
        {
            if (Interlocked.Increment(ref moves) == 2)
                throw new IOException("install failed");
            Directory.Move(source, destination);
        };

        Assert.False(store.Publish(Namespace(@"C:\new.pdf", "new text", "new")));

        ExtendedSourceNamespace current = store.TryLoad(SpecialSourceKind.PdfText)!;
        Assert.True(current.IsKnownSource(@"C:\old.pdf"));
        Assert.False(current.IsKnownSource(@"C:\new.pdf"));
    }

    [Fact]
    public void Store_BackupMoveFailure_LeavesPriorNamespaceActive()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-backup-failure");
        Assert.True(store.Publish(Namespace(@"C:\old.pdf", "old text", "old")));
        store.MoveDirectory = (_, _) => throw new IOException("backup move failed");

        Assert.False(store.Publish(Namespace(@"C:\new.pdf", "new text", "new")));

        ExtendedSourceNamespace current = store.TryLoad(SpecialSourceKind.PdfText)!;
        Assert.True(current.IsKnownSource(@"C:\old.pdf"));
        Assert.False(current.IsKnownSource(@"C:\new.pdf"));
    }

    [Fact]
    public void Store_RollbackNeverOverwritesALiveDirectoryThatReappeared()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-live-reappeared");
        Assert.True(store.Publish(Namespace(@"C:\old.pdf", "old text", "old")));
        store.MoveDirectory = (source, destination) =>
        {
            Directory.Move(source, destination);
            Directory.CreateDirectory(source); // model an external directory appearing before rollback
            throw new IOException("move completed, then failed");
        };

        Assert.False(store.Publish(Namespace(@"C:\new.pdf", "new text", "new")));

        Assert.Null(store.TryLoad(SpecialSourceKind.PdfText)); // incomplete live state always extracts live
        Assert.Contains(
            Directory.GetDirectories(Path.GetDirectoryName(store.NamespaceDirectory(SpecialSourceKind.PdfText))!),
            path => Path.GetFileName(path).StartsWith(ExtendedSourceStore.BackupPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Store_PostInstallFailure_RestoresPriorDisabledState()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-post-install");
        Assert.True(store.Publish(Namespace(@"C:\old.pdf", "old text", "old")));
        ExtendedSourceStore.WriteMarker(store.DisabledMarkerPath(SpecialSourceKind.PdfText));
        store.AfterInstall = () => throw new IOException("after install");

        Assert.True(IndexMutationContext.TryAcquire(
            sandbox.Provider,
            path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None),
            out IndexMutationContext? mutation));
        using (mutation)
            Assert.False(store.PublishUnderLease(mutation!, Namespace(@"C:\new.pdf", "new text", "new")));

        Assert.True(File.Exists(store.DisabledMarkerPath(SpecialSourceKind.PdfText)));
        Assert.Null(store.TryLoad(SpecialSourceKind.PdfText));
        string live = store.NamespaceDirectory(SpecialSourceKind.PdfText);
        string extended = Path.GetDirectoryName(live)!;
        Assert.True(Directory.Exists(live),
            "The pre-commit namespace backup was not restored. Remaining: "
            + string.Join(", ", Directory.Exists(extended) ? Directory.GetDirectories(extended) : Array.Empty<string>()));
        ExtendedSourceNamespace? restored = ExtendedSourceNamespaceSerializer.TryRead(live);
        Assert.NotNull(restored);
        Assert.True(restored.IsKnownSource(@"C:\old.pdf"));
        Assert.False(restored.IsKnownSource(@"C:\new.pdf"));
    }

    [Fact]
    public void Store_FailSafeHelpers_CoverMoveDeleteMarkerAndKindBranches()
    {
        using var sandbox = new TempIndexRoot();
        var store = new ExtendedSourceStore(sandbox.Provider, "scope-helpers");
        Assert.EndsWith("ocr", store.NamespaceDirectory(SpecialSourceKind.ImageOcr));
        Assert.EndsWith("archive", store.NamespaceDirectory(SpecialSourceKind.Archive));
        Assert.EndsWith("other", store.NamespaceDirectory((SpecialSourceKind)999));

        bool moved = false;
        Assert.True(ExtendedSourceStore.TryMoveDirectory("a", "b", (_, _) => moved = true));
        Assert.True(moved);
        Assert.False(ExtendedSourceStore.TryMoveDirectory("a", "b", (_, _) => throw new IOException()));
        Assert.False(ExtendedSourceStore.TryMoveDirectory("a", "b", (_, _) => throw new UnauthorizedAccessException()));
        Assert.Throws<ArgumentNullException>(() => ExtendedSourceStore.TryMoveDirectory("a", "b", null!));

        string lockedFile = Path.Combine(sandbox.Root, "locked.bin");
        File.WriteAllText(lockedFile, "locked");
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(ExtendedSourceStore.DeleteFileSafe(lockedFile));
        Assert.True(ExtendedSourceStore.DeleteFileSafe(lockedFile));

        string lockedDirectory = Path.Combine(sandbox.Root, "locked-dir");
        Directory.CreateDirectory(lockedDirectory);
        string child = Path.Combine(lockedDirectory, "child.bin");
        File.WriteAllText(child, "locked");
        using (new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.None))
            ExtendedSourceStore.DeleteDirectorySafe(lockedDirectory);
        Assert.True(Directory.Exists(lockedDirectory));
        ExtendedSourceStore.DeleteDirectorySafe(lockedDirectory);
        Assert.False(Directory.Exists(lockedDirectory));
        ExtendedSourceStore.DeleteDirectorySafe(null);

        string markerTemp = store.DisabledMarkerPath(SpecialSourceKind.PdfText)
            + ExtendedSourceStore.DisabledMarkerTempSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(markerTemp)!);
        Directory.CreateDirectory(markerTemp); // FileStream.Create on a directory deterministically fails
        store.WriteDisabledMarkerSafe(SpecialSourceKind.PdfText);
        Assert.True(Directory.Exists(markerTemp));
    }

    private static ExtendedSourceNamespace Namespace(string path, string text, string hash)
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, PdfFp(hash));
        builder.AddSource(path, new ExtractionOutcome.Success(text), new UsnFileIdentity(123, 0));
        return builder.Build(@"C:\", new UsnCheckpoint(1, 100));
    }

    // ─────────────────────────── the gate's unknown-source guard ───────────────────────────

    [Fact]
    public void Gate_NeverPrunesAnUnknownSource_EvenThoughItWouldClassifyAsNonmember()
    {
        var b = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, PdfFp());
        b.AddSource(@"C:\known.pdf", new ExtractionOutcome.Success("ordinary text"), new UsnFileIdentity(1, 0));
        ExtendedSourceNamespace ns = b.Build(@"C:\", new UsnCheckpoint(1, 100));

        // Query for a distinctive word absent from every source → the known PDF is a nonmember (prunable).
        ContentRepresentation.Classify(Encoding.UTF8.GetBytes("zephyrqux"), out IReadOnlyList<Trigram> t);
        TrigramExpression query = TrigramExpression.OfTrigram(t[0]);
        ExtendedSourceSearchGate gate = ExtendedSourceSearchGate.Create(
            new Dictionary<SpecialSourceKind, (ExtendedSourceNamespace, ExtractorFingerprint)> { [ns.Kind] = (ns, PdfFp()) },
            query, (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, []));

        // A KNOWN nonmember is safely pruned...
        Assert.False(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\known.pdf"));
        // ...but an UNKNOWN PDF (never indexed) MUST be extracted — pruning it would silently hide a match.
        Assert.True(gate.ShouldExtract(SpecialSourceKind.PdfText, @"C:\never-indexed.pdf"));
        Assert.Equal(1, gate.TotalPruned);
    }

    private sealed class TempIndexRoot : IDisposable
    {
        public string Root { get; }
        public DefaultContentIndexPathProvider Provider { get; }

        public TempIndexRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "yagu-esrc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Provider = new DefaultContentIndexPathProvider(Root, Root);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }
}
