using System.Collections.Generic;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ExtendedSourceNamespace"/> and <see cref="ExtendedSourceNamespaceBuilder"/>
/// (plan §7 Phase 4, build side). Proven invariants: a <see cref="ExtractionOutcome.Success"/> becomes a
/// trigram posting member (text is never stored); a <see cref="ExtractionOutcome.DeterministicUnsupported"/>
/// records a durable negative proof <b>only</b> for a deterministic extractor; transient/cancelled outcomes
/// persist nothing; and building the candidate + routing it through <see cref="ExtendedSourcePolicy"/>
/// prunes only a fresh, fingerprint-matched deterministic nonmember while OCR nonmembers always extract.
/// </summary>
public sealed class ExtendedSourceNamespaceTests
{
    private const string DistinctiveWord = "zephyrqux";

    private static ExtractorFingerprint Fp(SpecialSourceKind kind, string version = "1.0")
        => new(kind, "engine", version, "cpu");

    private static TrigramExpression TriQuery(string word)
    {
        ContentRepresentation.Classify(Encoding.UTF8.GetBytes(word), out IReadOnlyList<Trigram> t);
        Assert.NotEmpty(t);
        return TrigramExpression.OfTrigram(t[0]);
    }

    // ── build-side ingestion ──

    [Fact]
    public void Success_AdmitsSource_AndSelectsMemberByTrigram()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        Assert.Equal(0, builder.AddSource(@"C:\docs\a.pdf", new ExtractionOutcome.Success($"{DistinctiveWord} report")));
        ExtendedSourceNamespace ns = builder.Build();

        Assert.Equal(1, ns.SourceCount);
        Assert.Contains(@"C:\docs\a.pdf", ns.SelectMemberKeys(TriQuery(DistinctiveWord)));
        Assert.DoesNotContain(@"C:\docs\a.pdf", ns.SelectMemberKeys(TriQuery("ordinary")));
    }

    [Fact]
    public void DeterministicUnsupported_Deterministic_RecordsNegativeProof()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        Assert.Equal(-1, builder.AddSource(@"C:\docs\scan.pdf", new ExtractionOutcome.DeterministicUnsupported("image-only PDF")));
        ExtendedSourceNamespace ns = builder.Build();

        Assert.Equal(0, ns.SourceCount);
        Assert.Equal(1, builder.NegativeProofCount);
        Assert.Contains(@"C:\docs\scan.pdf", ns.NegativeProofKeys);
    }

    [Fact]
    public void DeterministicUnsupported_Ocr_DoesNotRecordNegativeProof()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.ImageOcr, Fp(SpecialSourceKind.ImageOcr));
        builder.AddSource(@"C:\img\photo.png", new ExtractionOutcome.DeterministicUnsupported("no text"));
        ExtendedSourceNamespace ns = builder.Build();

        Assert.Equal(0, builder.NegativeProofCount);
        Assert.Empty(ns.NegativeProofKeys);
    }

    [Fact]
    public void TransientFailureAndCancelled_PersistNothing()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        builder.AddSource(@"C:\a.pdf", new ExtractionOutcome.TransientFailure("timeout"));
        builder.AddSource(@"C:\b.pdf", new ExtractionOutcome.Cancelled());

        Assert.Equal(0, builder.AdmittedCount);
        Assert.Equal(0, builder.NegativeProofCount);
    }

    [Fact]
    public void DuplicateKey_IsIgnoredWithinBuild()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        Assert.Equal(0, builder.AddSource(@"C:\a.pdf", new ExtractionOutcome.Success("hello world")));
        Assert.Equal(-1, builder.AddSource(@"C:\a.pdf", new ExtractionOutcome.Success("different text")));
        Assert.Equal(1, builder.AdmittedCount);
    }

    [Fact]
    public void EmptySuccessText_IsAdmittedButNonmemberOfRealQueries()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        builder.AddSource(@"C:\empty.pdf", new ExtractionOutcome.Success(string.Empty));
        ExtendedSourceNamespace ns = builder.Build();

        Assert.Equal(1, ns.SourceCount);
        Assert.DoesNotContain(@"C:\empty.pdf", ns.SelectMemberKeys(TriQuery(DistinctiveWord)));
    }

    [Fact]
    public void BinarySuccessText_IsAdmittedWithoutUnsafePostings()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        Assert.Equal(0, builder.AddSource(@"C:\binary.pdf", new ExtractionOutcome.Success("text\0payload")));

        ExtendedSourceNamespace ns = builder.Build();
        Assert.Equal(1, ns.SourceCount);
        Assert.Empty(ns.Documents.Single());
    }

    [Fact]
    public void Builder_FingerprintSourceMismatch_Throws()
        => Assert.Throws<ArgumentException>(
            () => new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.Archive)));

    // ── query-side: candidate classification + routing (end-to-end with the policy) ──

    private static (ExtendedSourceNamespace ns, IReadOnlySet<string> members, TrigramExpression query) BuildPdf(
        string memberKey, string nonmemberKey)
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        builder.AddSource(memberKey, new ExtractionOutcome.Success($"{DistinctiveWord} content"));
        builder.AddSource(nonmemberKey, new ExtractionOutcome.Success("ordinary unrelated text"));
        ExtendedSourceNamespace ns = builder.Build();
        TrigramExpression query = TriQuery(DistinctiveWord);
        return (ns, ns.SelectMemberKeys(query), query);
    }

    [Fact]
    public void ClassifyCandidate_Member_RoutesToPrioritizedExtract()
    {
        (ExtendedSourceNamespace ns, IReadOnlySet<string> members, _) = BuildPdf(@"C:\m.pdf", @"C:\n.pdf");
        ExtendedSourceCandidate cand = ns.ClassifyCandidate(@"C:\m.pdf", members, Fp(SpecialSourceKind.PdfText), sourceFresh: true);

        var route = Assert.IsType<ExtendedSourceRoute.Extract>(ExtendedSourcePolicy.Route(cand));
        Assert.True(route.Prioritized);
    }

    [Fact]
    public void ClassifyCandidate_DeterministicFreshNonmember_RoutesToPrune()
    {
        (ExtendedSourceNamespace ns, IReadOnlySet<string> members, _) = BuildPdf(@"C:\m.pdf", @"C:\n.pdf");
        ExtendedSourceCandidate cand = ns.ClassifyCandidate(@"C:\n.pdf", members, Fp(SpecialSourceKind.PdfText), sourceFresh: true);

        Assert.IsType<ExtendedSourceRoute.PruneSource>(ExtendedSourcePolicy.Route(cand));
    }

    [Fact]
    public void ClassifyCandidate_FingerprintMismatch_RoutesToExtract()
    {
        (ExtendedSourceNamespace ns, IReadOnlySet<string> members, _) = BuildPdf(@"C:\m.pdf", @"C:\n.pdf");
        // A different extractor version => the namespace cannot be trusted for this nonmember.
        ExtendedSourceCandidate cand = ns.ClassifyCandidate(@"C:\n.pdf", members, Fp(SpecialSourceKind.PdfText, "2.0"), sourceFresh: true);

        Assert.IsType<ExtendedSourceRoute.Extract>(ExtendedSourcePolicy.Route(cand));
    }

    [Fact]
    public void ClassifyCandidate_StaleSource_RoutesToExtract()
    {
        (ExtendedSourceNamespace ns, IReadOnlySet<string> members, _) = BuildPdf(@"C:\m.pdf", @"C:\n.pdf");
        ExtendedSourceCandidate cand = ns.ClassifyCandidate(@"C:\n.pdf", members, Fp(SpecialSourceKind.PdfText), sourceFresh: false);

        Assert.IsType<ExtendedSourceRoute.Extract>(ExtendedSourcePolicy.Route(cand));
    }

    [Fact]
    public void ClassifyCandidate_OcrNonmember_RoutesToExtract_NeverPruned()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.ImageOcr, Fp(SpecialSourceKind.ImageOcr));
        builder.AddSource(@"C:\a.png", new ExtractionOutcome.Success($"{DistinctiveWord} sign"));
        builder.AddSource(@"C:\b.png", new ExtractionOutcome.Success("plain photo caption"));
        ExtendedSourceNamespace ns = builder.Build();
        IReadOnlySet<string> members = ns.SelectMemberKeys(TriQuery(DistinctiveWord));

        ExtendedSourceCandidate cand = ns.ClassifyCandidate(@"C:\b.png", members, Fp(SpecialSourceKind.ImageOcr), sourceFresh: true);
        Assert.IsType<ExtendedSourceRoute.Extract>(ExtendedSourcePolicy.Route(cand));
    }

    [Fact]
    public void ClassifyCandidate_ProvenDeterministicNegative_RoutesToPruneWithProvenReason()
    {
        var builder = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp(SpecialSourceKind.PdfText));
        builder.AddSource(@"C:\scan.pdf", new ExtractionOutcome.DeterministicUnsupported("image-only PDF"));
        ExtendedSourceNamespace ns = builder.Build();
        IReadOnlySet<string> members = ns.SelectMemberKeys(TriQuery(DistinctiveWord));

        ExtendedSourceCandidate cand = ns.ClassifyCandidate(@"C:\scan.pdf", members, Fp(SpecialSourceKind.PdfText), sourceFresh: true);
        var prune = Assert.IsType<ExtendedSourceRoute.PruneSource>(ExtendedSourcePolicy.Route(cand));
        Assert.Contains("proven", prune.Reason);
    }
}
