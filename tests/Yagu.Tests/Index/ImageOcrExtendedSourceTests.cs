using System.Text;
using Yagu.Services.Index;
using Yagu.Services.Ocr;

namespace Yagu.Tests.Index;

public sealed class ImageOcrExtendedSourceTests
{
    private sealed class FakeOcrEngine(Func<string, OcrResult> recognize, OcrResult? ready = null) : IOcrEngine
    {
        public string Id => OcrEngineFactory.PaddleId;
        public string DisplayName => "Fake OCR";
        public Task<OcrResult> EnsureReadyAsync(CancellationToken cancellationToken)
            => Task.FromResult(ready ?? OcrResult.Ok(string.Empty));
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken)
            => Task.FromResult(recognize(imagePath));
    }

    private static ExtractorFingerprint Fingerprint() => new(
        SpecialSourceKind.ImageOcr,
        OcrEngineFactory.PaddleId,
        string.Empty,
        "cpu",
        [new ExtractorFileHash("worker", "deadbeef")],
        [new("model", "ChineseV5"), new("maxSide", "960")]);

    [Fact]
    public void Populator_ConstructorRejectsInvalidDependencies()
    {
        var engine = new FakeOcrEngine(_ => OcrResult.Ok("text"));
        ExtractorFingerprint fingerprint = Fingerprint();

        Assert.Throws<ArgumentNullException>(() => new ImageOcrExtendedSourcePopulator(null!, fingerprint, _ => null));
        Assert.Throws<ArgumentNullException>(() => new ImageOcrExtendedSourcePopulator(engine, null!, _ => null));
        Assert.Throws<ArgumentException>(() => new ImageOcrExtendedSourcePopulator(
            engine,
            new ExtractorFingerprint(SpecialSourceKind.PdfText, "pdf", "1", "cpu"),
            _ => null));
        Assert.Throws<ArgumentNullException>(() => new ImageOcrExtendedSourcePopulator(engine, fingerprint, null!));
    }

    [Fact]
    public async Task Populator_PersistsPositiveTextButNeverFailureAsNegative()
    {
        var engine = new FakeOcrEngine(path => path.EndsWith("good.png", StringComparison.OrdinalIgnoreCase)
            ? OcrResult.Ok("zephyrqux invoice")
            : OcrResult.Fail("unreadable"));
        var progress = new List<ImageOcrBuildProgress>();
        var populator = new ImageOcrExtendedSourcePopulator(
            engine, Fingerprint(), _ => new UsnFileIdentity(7, 0));

        ImageOcrPopulationResult result = await populator.PopulateAsync(
            [@"C:\good.png", @"C:\bad.png"],
            @"C:\",
            new UsnCheckpoint(1, 100),
            progress: progress.Add);

        Assert.Equal(2, result.ImagesSeen);
        Assert.Equal(1, result.Admitted);
        Assert.Equal(1, result.Failed);
        Assert.True(result.Namespace.IsKnownSource(@"C:\good.png"));
        Assert.False(result.Namespace.IsKnownSource(@"C:\bad.png"));
        Assert.Empty(result.Namespace.NegativeProofKeys);
        Assert.Equal(new ImageOcrBuildProgress(2, 2), progress[^1]);
    }

    [Fact]
    public async Task Populator_UnavailableEngineThrowsTypedFailureBeforeRecognizing()
    {
        bool recognized = false;
        var engine = new FakeOcrEngine(_ =>
        {
            recognized = true;
            return OcrResult.Ok("text");
        }, OcrResult.Fail("assets unavailable"));
        var populator = new ImageOcrExtendedSourcePopulator(engine, Fingerprint(), _ => null);

        ImageOcrIndexUnavailableException error = await Assert.ThrowsAsync<ImageOcrIndexUnavailableException>(() =>
            populator.PopulateAsync([@"C:\a.png"], @"C:\", new UsnCheckpoint(1, 100)));

        Assert.Contains("assets unavailable", error.Message);
        Assert.False(recognized);
    }

    [Fact]
    public async Task Populator_NullReadyErrorUsesFallbackMessage()
    {
        var engine = new FakeOcrEngine(
            _ => OcrResult.Ok("unused"),
            new OcrResult(false, string.Empty, null));
        var populator = new ImageOcrExtendedSourcePopulator(engine, Fingerprint(), _ => null);

        ImageOcrIndexUnavailableException error = await Assert.ThrowsAsync<ImageOcrIndexUnavailableException>(() =>
            populator.PopulateAsync([], @"C:\", new UsnCheckpoint(1, 100)));

        Assert.Equal("OCR engine unavailable.", error.Message);
    }

    [Fact]
    public async Task Populator_ThrowingIdentityProviderStillAdmitsText()
    {
        var engine = new FakeOcrEngine(_ => OcrResult.Ok("zephyrqux invoice"));
        var populator = new ImageOcrExtendedSourcePopulator(
            engine, Fingerprint(), _ => throw new IOException("identity unavailable"));

        ImageOcrPopulationResult result = await populator.PopulateAsync(
            [@"C:\good.png"], @"C:\", new UsnCheckpoint(1, 100));

        Assert.Equal(1, result.Admitted);
        Assert.Null(result.Namespace.SourceIdentityByKey[@"C:\good.png"]);
    }

    [Fact]
    public void OcrNamespace_MemberIsPrioritized_NonmemberStillExtracts()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.ImageOcr, Fingerprint());
        builder.AddSource(@"C:\known.png", new ExtractionOutcome.Success("zephyrqux text"), new UsnFileIdentity(1, 0));
        ExtendedSourceNamespace ns = builder.Build(@"C:\", new UsnCheckpoint(1, 100));
        ContentRepresentation.Classify(Encoding.UTF8.GetBytes("zephyrqux"), out IReadOnlyList<Trigram> trigrams);
        TrigramExpression query = TrigramExpression.OfTrigram(trigrams[0]);
        IReadOnlySet<string> members = ns.SelectMemberKeys(query);

        ExtendedSourceRoute member = ExtendedSourcePolicy.Route(
            ns.ClassifyCandidate(@"C:\known.png", members, Fingerprint(), sourceFresh: true));
        ExtendedSourceRoute nonmember = ExtendedSourcePolicy.Route(
            ns.ClassifyCandidate(@"C:\other.png", members, Fingerprint(), sourceFresh: true));

        Assert.True(Assert.IsType<ExtendedSourceRoute.Extract>(member).Prioritized);
        Assert.False(Assert.IsType<ExtendedSourceRoute.Extract>(nonmember).Prioritized);

        ExtendedSourceSearchGate gate = ExtendedSourceSearchGate.Create(
            new Dictionary<SpecialSourceKind, (ExtendedSourceNamespace, ExtractorFingerprint)>
            {
                [SpecialSourceKind.ImageOcr] = (ns, Fingerprint()),
            },
            query,
            (_, since) => new UsnReadResult(UsnReadStatus.Ok, since, []));
        Assert.True(gate.ShouldExtract(SpecialSourceKind.ImageOcr, @"C:\known.png", out bool prioritized));
        Assert.True(prioritized);
        Assert.True(gate.ShouldExtract(SpecialSourceKind.ImageOcr, @"C:\never-indexed.png", out prioritized));
        Assert.False(prioritized);
        Assert.Equal(0, gate.TotalPruned);
    }
}