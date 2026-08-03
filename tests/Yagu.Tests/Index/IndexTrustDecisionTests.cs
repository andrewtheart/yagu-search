using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for the single trust-decision surface <see cref="IndexTrustDecision"/> (plan §3.4/§3.5):
/// structural version comparison (format/representation gate; ABI and planner versions excluded),
/// the per-generation bypass decision, and per-path routing where only a fresh nonmember is pruned.
/// </summary>
public sealed class IndexTrustDecisionTests
{
    private static readonly IndexVersionSet Current = new(FormatVersion: 3, RepresentationVersion: 1);

    [Fact]
    public void EvaluateStructural_SameVersions_Trusted()
        => Assert.Equal(IndexStructuralVerdict.Trusted, IndexTrustDecision.EvaluateStructural(Current, Current));

    [Fact]
    public void EvaluateStructural_FormatMismatch_IncompatibleFormat()
        => Assert.Equal(
            IndexStructuralVerdict.IncompatibleFormat,
            IndexTrustDecision.EvaluateStructural(new IndexVersionSet(2, 1), Current));

    [Fact]
    public void EvaluateStructural_RepresentationMismatch_IncompatibleRepresentation()
        => Assert.Equal(
            IndexStructuralVerdict.IncompatibleRepresentation,
            IndexTrustDecision.EvaluateStructural(new IndexVersionSet(3, 2), Current));

    [Fact]
    public void DecideGeneration_AllGood_UseGeneration()
    {
        var decision = IndexTrustDecision.DecideGeneration(
            new IndexTrustInputs(IndexStructuralVerdict.Trusted, RootFreshnessVerdict.Continuous, QueryEligible: true));
        Assert.IsType<GenerationDecision.UseGeneration>(decision);
    }

    [Theory]
    [InlineData(IndexStructuralVerdict.Missing)]
    [InlineData(IndexStructuralVerdict.IncompatibleFormat)]
    [InlineData(IndexStructuralVerdict.IncompatibleRepresentation)]
    [InlineData(IndexStructuralVerdict.Corrupt)]
    public void DecideGeneration_UntrustedStructural_BypassRoot(IndexStructuralVerdict structural)
    {
        var decision = IndexTrustDecision.DecideGeneration(
            new IndexTrustInputs(structural, RootFreshnessVerdict.Continuous, QueryEligible: true));
        var bypass = Assert.IsType<GenerationDecision.BypassRoot>(decision);
        Assert.Contains("structural", bypass.Reason);
    }

    [Theory]
    [InlineData(RootFreshnessVerdict.JournalUnavailable)]
    [InlineData(RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(RootFreshnessVerdict.UnsupportedFilesystem)]
    [InlineData(RootFreshnessVerdict.AccessDenied)]
    [InlineData(RootFreshnessVerdict.CheckpointInvalid)]
    public void DecideGeneration_NonContinuousFreshness_BypassRoot(RootFreshnessVerdict freshness)
    {
        var decision = IndexTrustDecision.DecideGeneration(
            new IndexTrustInputs(IndexStructuralVerdict.Trusted, freshness, QueryEligible: true));
        var bypass = Assert.IsType<GenerationDecision.BypassRoot>(decision);
        Assert.Contains("freshness", bypass.Reason);
    }

    [Fact]
    public void DecideGeneration_IneligibleQuery_BypassRoot()
    {
        var decision = IndexTrustDecision.DecideGeneration(
            new IndexTrustInputs(IndexStructuralVerdict.Trusted, RootFreshnessVerdict.Continuous, QueryEligible: false));
        var bypass = Assert.IsType<GenerationDecision.BypassRoot>(decision);
        Assert.Contains("ineligible", bypass.Reason);
    }

    [Fact]
    public void DecidePath_FreshNonmember_ProvisionalPrune()
    {
        var decision = IndexTrustDecision.DecidePath(
            new IndexPathClassification.FreshIndexedNonmember(AliasId: 42, ContentId: 7));
        var prune = Assert.IsType<PathDecision.ProvisionalPrune>(decision);
        Assert.Equal(42, prune.AliasId);
    }

    [Fact]
    public void DecidePath_FreshMember_LiveScan()
    {
        var decision = IndexTrustDecision.DecidePath(
            new IndexPathClassification.FreshIndexedMember(AliasId: 1, ContentId: 2));
        var live = Assert.IsType<PathDecision.LiveScanPath>(decision);
        Assert.Contains("member", live.Reason);
    }

    [Fact]
    public void DecidePath_DirtyByUsn_LiveScan()
    {
        var decision = IndexTrustDecision.DecidePath(
            new IndexPathClassification.DirtyByUsn(ContentId: 5, Reason: "same-length rewrite"));
        var live = Assert.IsType<PathDecision.LiveScanPath>(decision);
        Assert.Contains("dirty", live.Reason);
    }

    [Fact]
    public void DecidePath_Unindexed_LiveScan()
    {
        var decision = IndexTrustDecision.DecidePath(new IndexPathClassification.Unindexed("over-cap"));
        var live = Assert.IsType<PathDecision.LiveScanPath>(decision);
        Assert.Contains("unindexed", live.Reason);
    }

    [Theory]
    [InlineData(SpecialSourceKind.Archive)]
    [InlineData(SpecialSourceKind.ImageOcr)]
    [InlineData(SpecialSourceKind.PdfText)]
    public void DecidePath_SpecialSource_LiveScan(SpecialSourceKind kind)
    {
        var decision = IndexTrustDecision.DecidePath(new IndexPathClassification.SpecialSource(kind));
        var live = Assert.IsType<PathDecision.LiveScanPath>(decision);
        Assert.Contains("special source", live.Reason);
    }

    [Fact]
    public void DecidePath_UntrustedRoot_LiveScan()
    {
        var decision = IndexTrustDecision.DecidePath(new IndexPathClassification.UntrustedRoot("journal gap"));
        var live = Assert.IsType<PathDecision.LiveScanPath>(decision);
        Assert.Contains("untrusted root", live.Reason);
    }
}
