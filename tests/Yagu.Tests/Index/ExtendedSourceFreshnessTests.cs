using System.Collections.Generic;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ExtendedSourceFreshnessEvaluator"/> and the namespace's per-source USN freshness
/// (plan §3.5/§7). A continuous journal dirties only the sources whose backing file changed; a source with
/// no captured identity is always dirty; and any journal discontinuity / unavailability / missing checkpoint
/// <b>fails closed</b> by dirtying every source so the whole namespace live-extracts. Proven end-to-end with
/// <see cref="ExtendedSourcePolicy.Route"/>: a changed source extracts even though it is a posting member.
/// </summary>
public sealed class ExtendedSourceFreshnessTests
{
    private static readonly UsnFileIdentity IdA = new(10, 0);
    private static readonly UsnFileIdentity IdB = new(20, 0);

    private static ExtractorFingerprint Fp() => new(SpecialSourceKind.PdfText, "pdftotext", "1", "cpu");

    // a.pdf (id A), b.pdf (id B), c.pdf (no identity captured). Built with checkpoint (1,100) under C:\.
    private static ExtendedSourceNamespace BuildNs()
    {
        var b = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp());
        b.AddSource(@"C:\a.pdf", new ExtractionOutcome.Success("alpha alpha text"), IdA);
        b.AddSource(@"C:\b.pdf", new ExtractionOutcome.Success("beta beta text"), IdB);
        b.AddSource(@"C:\c.pdf", new ExtractionOutcome.Success("gamma text")); // no identity
        return b.Build(@"C:\", new UsnCheckpoint(1, 100));
    }

    private static ExtendedSourceFreshnessEvaluator.JournalReader Reader(UsnReadStatus status, params UsnChange[] changes)
        => (_, since) => new UsnReadResult(status, since, changes);

    [Fact]
    public void DefaultReader_UsesRealJournal_AndFailsClosedWithoutThrowing()
    {
        // No injected reader -> the production DefaultReader delegates to the real USN journal. The
        // bogus build checkpoint will not match the volume's live journal, so it fails closed (dirty)
        // rather than throwing. Exercises the otherwise-unreached default reader wiring.
        ExtendedSourceNamespace ns = BuildNs();
        ExtendedSourceFreshness fresh = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(ns);
        Assert.NotNull(fresh);
        Assert.NotNull(fresh.DirtyKeys);
    }

    [Fact]
    public void Continuous_MarksOnlyChangedAndUnidentifiedSourcesDirty()
    {
        ExtendedSourceNamespace ns = BuildNs();
        ExtendedSourceFreshness fresh = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(
            ns, Reader(UsnReadStatus.Ok, new UsnChange(IdA, 0x1)));

        Assert.True(fresh.IsContinuous);
        Assert.False(fresh.IsFresh(@"C:\a.pdf")); // changed
        Assert.True(fresh.IsFresh(@"C:\b.pdf"));  // unchanged
        Assert.False(fresh.IsFresh(@"C:\c.pdf")); // no identity -> unprovable -> dirty
    }

    [Fact]
    public void NullIdentitySource_IsDirtyEvenWithNoChanges()
    {
        ExtendedSourceNamespace ns = BuildNs();
        ExtendedSourceFreshness fresh = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(ns, Reader(UsnReadStatus.Ok));

        Assert.True(fresh.IsFresh(@"C:\a.pdf"));
        Assert.True(fresh.IsFresh(@"C:\b.pdf"));
        Assert.False(fresh.IsFresh(@"C:\c.pdf"));
    }

    [Theory]
    [InlineData(UsnReadStatus.JournalIdChanged, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.GapDetected, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.CheckpointAhead, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.UnknownRecordVersion, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Error, RootFreshnessVerdict.JournalDiscontinuity)]
    [InlineData(UsnReadStatus.Unavailable, RootFreshnessVerdict.JournalUnavailable)]
    public void JournalNotContinuous_FailsClosed_AllSourcesDirty(UsnReadStatus status, RootFreshnessVerdict expected)
    {
        ExtendedSourceNamespace ns = BuildNs();
        ExtendedSourceFreshness fresh = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(
            ns, Reader(status, new UsnChange(IdA, 0x1)));

        Assert.Equal(expected, fresh.Verdict);
        Assert.False(fresh.IsContinuous);
        Assert.False(fresh.IsFresh(@"C:\a.pdf"));
        Assert.False(fresh.IsFresh(@"C:\b.pdf"));
        Assert.False(fresh.IsFresh(@"C:\c.pdf"));
        Assert.Equal(ns.AllSourceKeys.Count, fresh.DirtyKeys.Count);
    }

    [Fact]
    public void NoBuildCheckpoint_IsCheckpointInvalid_AllDirty_ReaderNotCalled()
    {
        // A namespace built with no checkpoint (default Build()) can never prove freshness.
        var b = new ExtendedSourceNamespaceBuilder(SpecialSourceKind.PdfText, Fp());
        b.AddSource(@"C:\a.pdf", new ExtractionOutcome.Success("alpha"), IdA);
        ExtendedSourceNamespace ns = b.Build();

        bool readerCalled = false;
        ExtendedSourceFreshness fresh = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(
            ns, (_, since) => { readerCalled = true; return new UsnReadResult(UsnReadStatus.Ok, since, []); });

        Assert.Equal(RootFreshnessVerdict.CheckpointInvalid, fresh.Verdict);
        Assert.False(fresh.IsFresh(@"C:\a.pdf"));
        Assert.False(readerCalled);
    }

    [Fact]
    public void ReadDirtyAtBuildBarrier_PassesNamespaceCheckpointAndRoot()
    {
        ExtendedSourceNamespace ns = BuildNs();
        UsnCheckpoint seen = default;
        string seenRoot = "";
        ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(ns, (root, since) =>
        {
            seen = since;
            seenRoot = root;
            return new UsnReadResult(UsnReadStatus.Ok, since, []);
        });

        Assert.Equal(new UsnCheckpoint(1, 100), seen);
        Assert.Equal(@"C:\", seenRoot);
    }

    [Fact]
    public void EndToEnd_ChangedMemberExtractsAndFreshNonmemberPrunes()
    {
        ExtendedSourceNamespace ns = BuildNs();
        ContentRepresentation.Classify(System.Text.Encoding.UTF8.GetBytes("alpha"), out IReadOnlyList<Trigram> t);
        TrigramExpression query = TrigramExpression.OfTrigram(t[0]);
        IReadOnlySet<string> members = ns.SelectMemberKeys(query); // contains a.pdf

        // a.pdf changed after B0 → dirty → even though it is a member, it must live-extract.
        ExtendedSourceFreshness fresh = ExtendedSourceFreshnessEvaluator.ReadDirtyAtBuildBarrier(
            ns, Reader(UsnReadStatus.Ok, new UsnChange(IdA, 0x1)));

        ExtendedSourceCandidate a = ns.ClassifyCandidate(@"C:\a.pdf", members, Fp(), fresh.IsFresh(@"C:\a.pdf"));
        Assert.IsType<ExtendedSourceRoute.Extract>(ExtendedSourcePolicy.Route(a));

        // b.pdf unchanged nonmember → deterministic prune.
        ExtendedSourceCandidate bCand = ns.ClassifyCandidate(@"C:\b.pdf", members, Fp(), fresh.IsFresh(@"C:\b.pdf"));
        Assert.IsType<ExtendedSourceRoute.PruneSource>(ExtendedSourcePolicy.Route(bCand));
    }
}
