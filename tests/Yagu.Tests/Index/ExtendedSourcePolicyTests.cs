using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ExtendedSourcePolicy"/> and <see cref="ExtractionOutcome"/> (plan §7 Phase 4):
/// the extended-source safety brain. The load-bearing invariants proven here are (1) only PDF-text and
/// archive extractors are deterministic and therefore prunable, (2) OCR nonmembers are <b>never</b>
/// pruned (only prioritized), (3) only a <see cref="ExtractionOutcome.DeterministicUnsupported"/> from a
/// deterministic extractor may persist a negative exclusion, and (4) <see cref="ExtendedSourcePolicy.Route"/>
/// extracts live for every case except a trusted/fresh/fingerprint-matched deterministic nonmember.
/// </summary>
public sealed class ExtendedSourcePolicyTests
{
    // ── Determinism / prunability ──

    [Theory]
    [InlineData(SpecialSourceKind.PdfText, true)]
    [InlineData(SpecialSourceKind.Archive, true)]
    [InlineData(SpecialSourceKind.ImageOcr, false)]
    public void IsDeterministicExtractor_ClassifiesEachSource(SpecialSourceKind kind, bool deterministic)
        => Assert.Equal(deterministic, ExtendedSourcePolicy.IsDeterministicExtractor(kind));

    [Theory]
    [InlineData(SpecialSourceKind.PdfText, true)]
    [InlineData(SpecialSourceKind.Archive, true)]
    [InlineData(SpecialSourceKind.ImageOcr, false)]
    public void CanPruneNonmembers_MirrorsDeterminism(SpecialSourceKind kind, bool prunable)
        => Assert.Equal(prunable, ExtendedSourcePolicy.CanPruneNonmembers(kind));

    [Fact]
    public void UnknownSourceKind_IsTreatedAsNonDeterministic_AndNotPrunable()
    {
        // Defensive default arm: an out-of-range kind must fail safe (non-deterministic, never pruned).
        var unknown = (SpecialSourceKind)999;
        Assert.False(ExtendedSourcePolicy.IsDeterministicExtractor(unknown));
        Assert.False(ExtendedSourcePolicy.CanPruneNonmembers(unknown));
    }

    // ── Persistable outcomes ──

    [Fact]
    public void IsPersistablePositive_OnlySuccess()
    {
        Assert.True(ExtendedSourcePolicy.IsPersistablePositive(new ExtractionOutcome.Success("hi")));
        Assert.True(ExtendedSourcePolicy.IsPersistablePositive(new ExtractionOutcome.Success("")));
        Assert.False(ExtendedSourcePolicy.IsPersistablePositive(new ExtractionOutcome.DeterministicUnsupported("img-only")));
        Assert.False(ExtendedSourcePolicy.IsPersistablePositive(new ExtractionOutcome.TransientFailure("timeout")));
        Assert.False(ExtendedSourcePolicy.IsPersistablePositive(new ExtractionOutcome.Cancelled()));
    }

    [Fact]
    public void IsPersistableNegative_OnlyDeterministicUnsupportedFromDeterministicExtractor()
    {
        var unsupported = new ExtractionOutcome.DeterministicUnsupported("image-only PDF");

        Assert.True(ExtendedSourcePolicy.IsPersistableNegative(unsupported, SpecialSourceKind.PdfText));
        Assert.True(ExtendedSourcePolicy.IsPersistableNegative(unsupported, SpecialSourceKind.Archive));

        // OCR is non-deterministic: even a "DeterministicUnsupported" outcome cannot persist a negative.
        Assert.False(ExtendedSourcePolicy.IsPersistableNegative(unsupported, SpecialSourceKind.ImageOcr));

        // Non-DeterministicUnsupported outcomes never persist a negative, even for deterministic extractors.
        Assert.False(ExtendedSourcePolicy.IsPersistableNegative(new ExtractionOutcome.Success(""), SpecialSourceKind.PdfText));
        Assert.False(ExtendedSourcePolicy.IsPersistableNegative(new ExtractionOutcome.TransientFailure("oom"), SpecialSourceKind.PdfText));
        Assert.False(ExtendedSourcePolicy.IsPersistableNegative(new ExtractionOutcome.Cancelled(), SpecialSourceKind.PdfText));
    }

    // ── Routing ──

    private static ExtendedSourceCandidate Candidate(
        SpecialSourceKind kind,
        bool trusted = true,
        bool fingerprint = true,
        bool fresh = true,
        bool member = false,
        bool negative = false)
        => new(kind, trusted, fingerprint, fresh, member, negative);

    [Fact]
    public void Route_UntrustedNamespace_ExtractsLive()
    {
        ExtendedSourceRoute route = ExtendedSourcePolicy.Route(Candidate(SpecialSourceKind.PdfText, trusted: false));
        var extract = Assert.IsType<ExtendedSourceRoute.Extract>(route);
        Assert.False(extract.Prioritized);
    }

    [Fact]
    public void Route_FingerprintMismatch_ExtractsLive()
        => Assert.IsType<ExtendedSourceRoute.Extract>(
            ExtendedSourcePolicy.Route(Candidate(SpecialSourceKind.PdfText, fingerprint: false)));

    [Fact]
    public void Route_StaleSource_ExtractsLive()
        => Assert.IsType<ExtendedSourceRoute.Extract>(
            ExtendedSourcePolicy.Route(Candidate(SpecialSourceKind.Archive, fresh: false)));

    [Theory]
    [InlineData(SpecialSourceKind.PdfText)]
    [InlineData(SpecialSourceKind.Archive)]
    [InlineData(SpecialSourceKind.ImageOcr)]
    public void Route_PostingMember_AlwaysExtractsLiveAndPrioritized(SpecialSourceKind kind)
    {
        ExtendedSourceRoute route = ExtendedSourcePolicy.Route(Candidate(kind, member: true));
        var extract = Assert.IsType<ExtendedSourceRoute.Extract>(route);
        Assert.True(extract.Prioritized);
    }

    [Theory]
    [InlineData(SpecialSourceKind.PdfText)]
    [InlineData(SpecialSourceKind.Archive)]
    public void Route_DeterministicNonmember_IsPruned(SpecialSourceKind kind)
        => Assert.IsType<ExtendedSourceRoute.PruneSource>(ExtendedSourcePolicy.Route(Candidate(kind)));

    [Fact]
    public void Route_DeterministicProvenUnsupported_IsPrunedWithProvenReason()
    {
        ExtendedSourceRoute route = ExtendedSourcePolicy.Route(Candidate(SpecialSourceKind.PdfText, negative: true));
        var prune = Assert.IsType<ExtendedSourceRoute.PruneSource>(route);
        Assert.Contains("proven", prune.Reason);
    }

    [Fact]
    public void Route_OcrNonmember_IsNeverPruned()
    {
        // Even with a persisted "deterministic negative" flag set, OCR must still re-extract — it is not
        // a deterministic extractor, so its nonmembers can never be pruned.
        Assert.IsType<ExtendedSourceRoute.Extract>(
            ExtendedSourcePolicy.Route(Candidate(SpecialSourceKind.ImageOcr)));
        var route = ExtendedSourcePolicy.Route(Candidate(SpecialSourceKind.ImageOcr, negative: true));
        var extract = Assert.IsType<ExtendedSourceRoute.Extract>(route);
        Assert.False(extract.Prioritized);
    }

    [Fact]
    public void ExtractionOutcome_Success_AllowsEmptyText()
        => Assert.Equal(string.Empty, new ExtractionOutcome.Success(string.Empty).Text);
}
