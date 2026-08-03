using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// End-to-end tests for the managed reference index (plan §3.4/§3.5/§5): building a generation,
/// classifying discovered paths, provisionally pruning fresh nonmembers, and the B0/B1 reconciliation
/// that guarantees a match added after B0 is never hidden (invariant §5.1 #3). Also covers the
/// quiescent result-equivalence superset guarantee end-to-end.
/// </summary>
public sealed class ContentIndexQuerySessionTests
{
    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private static (ContentIndexGeneration Generation, long[] ContentIds) BuildGeneration(params (string Path, string Text)[] docs)
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: IndexTestIdentities.Provider);
        var ids = docs.Select(d => builder.AddDocument(d.Path, Encoding.UTF8.GetBytes(d.Text))).ToArray();
        var gen = builder.Build("scope-id", "vol-guid", @"C:\src", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        return (gen, ids);
    }

    private static TrigramExpression PlanLiteral(string literal)
    {
        var plan = TrigramQueryPlanner.Plan(
            new EffectiveSearchPattern(literal, isRegex: false, caseSensitive: true, multiline: false, dotAll: false));
        return Assert.IsType<TrigramPlan.Eligible>(plan).Query;
    }

    private static string Norm(string path) => IndexScopeIdentity.NormalizePath(path);

    // ─────────────────────────── Builder + generation ───────────────────────────

    [Fact]
    public void Builder_AdmitsTextAndReportsBuild()
    {
        var (gen, ids) = BuildGeneration(
            (@"C:\src\a.txt", "hello world"),
            (@"C:\src\b.txt", "planner content"));
        Assert.Equal(2, gen.AliasCount);
        Assert.Equal(0, ids[0]);
        Assert.Equal(1, ids[1]);
        Assert.Equal(2, gen.Report.IndexedCount);
        Assert.Equal(2, gen.Manifest.ContentCount);
    }

    [Fact]
    public void Builder_BinaryIsRejected_ButUtf8BomIsStrippedAndAdmitted()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        long good = builder.AddDocument(@"C:\src\ok.txt", Encoding.UTF8.GetBytes("hello world"));
        long binary = builder.AddDocument(@"C:\src\bin.dat", new byte[] { (byte)'a', 0, (byte)'b' });
        long bom = builder.AddDocument(@"C:\src\bom.txt", new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' });
        var gen = builder.Build("s", "v", @"C:\src", UsnCheckpoint.None, DateTimeOffset.UtcNow);

        Assert.Equal(0, good);
        Assert.Equal(-1, binary);
        Assert.Equal(1, bom);
        Assert.False(gen.TryGetAlias(Norm(@"C:\src\bin.dat"), out _, out _));
        Assert.True(gen.TryGetAlias(Norm(@"C:\src\ok.txt"), out _, out _));
        Assert.True(gen.TryGetAlias(Norm(@"C:\src\bom.txt"), out _, out _));
        Assert.Equal(1, gen.Report.SkipCount(IndexSkipReason.Binary));
        Assert.Equal(0, gen.Report.SkipCount(IndexSkipReason.UnsupportedEncoding));
    }

    [Fact]
    public void Builder_HardLink_SharesContent_DistinctAlias()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        long contentId = builder.AddDocument(@"C:\src\a.txt", Encoding.UTF8.GetBytes("shared content here"));
        long aliasId = builder.AddHardLink(@"C:\src\link.txt", contentId);
        var gen = builder.Build("s", "v", @"C:\src", UsnCheckpoint.None, DateTimeOffset.UtcNow);

        Assert.True(gen.TryGetAlias(Norm(@"C:\src\a.txt"), out long a1, out long c1));
        Assert.True(gen.TryGetAlias(Norm(@"C:\src\link.txt"), out long a2, out long c2));
        Assert.Equal(c1, c2);          // same content
        Assert.NotEqual(a1, a2);       // distinct aliases
        Assert.Equal(aliasId, a2);
    }

    [Fact]
    public void Builder_HardLink_InvalidContentId_Throws()
    {
        var builder = new ContentIndexGenerationBuilder(OpenPolicy);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddHardLink(@"C:\src\x.txt", 99));
    }

    // ─────────────────────────── CanAccelerate (trust surface) ───────────────────────────

    [Fact]
    public void CanAccelerate_TrustedContinuousEligible_UsesGeneration()
    {
        var (gen, _) = BuildGeneration((@"C:\src\a.txt", "hello"));
        Assert.IsType<GenerationDecision.UseGeneration>(
            ContentIndexQuerySession.CanAccelerate(gen, RootFreshnessVerdict.Continuous, queryEligible: true));
    }

    [Fact]
    public void CanAccelerate_NonContinuous_BypassRoot()
    {
        var (gen, _) = BuildGeneration((@"C:\src\a.txt", "hello"));
        Assert.IsType<GenerationDecision.BypassRoot>(
            ContentIndexQuerySession.CanAccelerate(gen, RootFreshnessVerdict.JournalUnavailable, queryEligible: true));
    }

    [Fact]
    public void CanAccelerate_IneligibleQuery_BypassRoot()
    {
        var (gen, _) = BuildGeneration((@"C:\src\a.txt", "hello"));
        Assert.IsType<GenerationDecision.BypassRoot>(
            ContentIndexQuerySession.CanAccelerate(gen, RootFreshnessVerdict.Continuous, queryEligible: false));
    }

    // ─────────────────────────── Classification ───────────────────────────

    [Fact]
    public void Classify_MemberNonmemberUnindexedDirty()
    {
        var (gen, ids) = BuildGeneration(
            (@"C:\src\match.txt", "this file has the needle in it"),
            (@"C:\src\other.txt", "this file has nothing special"));
        var query = PlanLiteral("needle");
        var dirty = new DirtyContentSet();
        var session = ContentIndexQuerySession.Begin(gen, query, dirty);

        Assert.IsType<IndexPathClassification.FreshIndexedMember>(session.Classify(Norm(@"C:\src\match.txt")));
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(session.Classify(Norm(@"C:\src\other.txt")));
        Assert.IsType<IndexPathClassification.Unindexed>(session.Classify(Norm(@"C:\src\absent.txt")));

        // Mark the matching file dirty → it must classify as DirtyByUsn.
        dirty.MarkDirty(ids[0]);
        var session2 = ContentIndexQuerySession.Begin(gen, query, dirty);
        Assert.IsType<IndexPathClassification.DirtyByUsn>(session2.Classify(Norm(@"C:\src\match.txt")));
    }

    [Fact]
    public void Route_NonmemberProvisionallyPruned_MemberLiveScanned()
    {
        var (gen, _) = BuildGeneration(
            (@"C:\src\match.txt", "the needle is here"),
            (@"C:\src\other.txt", "no match at all here"));
        var session = ContentIndexQuerySession.Begin(gen, PlanLiteral("needle"), new DirtyContentSet());

        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\src\match.txt")));
        var prune = Assert.IsType<PathDecision.ProvisionalPrune>(session.Route(Norm(@"C:\src\other.txt")));
        Assert.Contains(prune.AliasId, session.ProvisionalAliases);
    }

    [Fact]
    public void NonmemberWithoutCapturedIdentity_LiveScans_BecauseUsnCanNeverDirtyIt()
    {
        // A document whose durable file identity could not be captured at build time is invisible to the USN
        // change journal, so a post-B0 edit that adds a match could never be dirtied or rescued at B1. Such a
        // nonmember must therefore NEVER be pruned — it must live-scan (else the added match is silently hidden).
        var builder = new ContentIndexGenerationBuilder(
            OpenPolicy,
            identityProvider: path => path.Contains("noident", StringComparison.Ordinal)
                ? null
                : IndexTestIdentities.Capture(path));
        builder.AddDocument(@"C:\src\noident.txt", Encoding.UTF8.GetBytes("no match at all"));
        builder.AddDocument(@"C:\src\hasident.txt", Encoding.UTF8.GetBytes("also no match here"));
        var gen = builder.Build("s", "v", @"C:\src", new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);

        var session = ContentIndexQuerySession.Begin(gen, PlanLiteral("needle"), new DirtyContentSet());

        // The identity-less nonmember must live-scan (DirtyByUsn), not prune.
        Assert.IsType<IndexPathClassification.DirtyByUsn>(session.Classify(Norm(@"C:\src\noident.txt")));
        Assert.IsType<PathDecision.LiveScanPath>(session.Route(Norm(@"C:\src\noident.txt")));

        // A sibling nonmember WITH a captured identity still prunes normally.
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(session.Classify(Norm(@"C:\src\hasident.txt")));
        Assert.IsType<PathDecision.ProvisionalPrune>(session.Route(Norm(@"C:\src\hasident.txt")));
    }

    // ─────────────────────────── B0/B1 reconciliation (invariant §5.1 #3) ───────────────────────────

    [Fact]
    public void Reconcile_NonmemberThatGainsAMatchAfterB0_IsLiveScanned()
    {
        // "other.txt" does not contain "needle" at build time → provisionally pruned.
        var (gen, ids) = BuildGeneration(
            (@"C:\src\other.txt", "nothing interesting yet"));
        var session = ContentIndexQuerySession.Begin(gen, PlanLiteral("needle"), new DirtyContentSet());
        var prune = Assert.IsType<PathDecision.ProvisionalPrune>(session.Route(Norm(@"C:\src\other.txt")));

        // Between B0 and B1 the file is rewritten to contain "needle" → its content is marked dirty.
        var dirtyB1 = new DirtyContentSet();
        dirtyB1.MarkDirty(ids[0]);

        var reconciled = session.ReconcileAtB1(dirtyB1);
        Assert.Contains(prune.AliasId, reconciled);          // must be live-scanned → match not hidden
        Assert.DoesNotContain(prune.AliasId, session.ProvisionalAliases); // removed from provisional set
    }

    [Fact]
    public void Reconcile_QuiescentNonmember_StaysPruned()
    {
        var (gen, _) = BuildGeneration((@"C:\src\other.txt", "nothing interesting"));
        var session = ContentIndexQuerySession.Begin(gen, PlanLiteral("needle"), new DirtyContentSet());
        session.Route(Norm(@"C:\src\other.txt"));

        var reconciled = session.ReconcileAtB1(new DirtyContentSet()); // no changes
        Assert.Empty(reconciled);
    }

    [Fact]
    public void Reconcile_Uncertain_LiveScansAllProvisional()
    {
        var (gen, _) = BuildGeneration(
            (@"C:\src\a.txt", "nothing one"),
            (@"C:\src\b.txt", "nothing two"));
        var session = ContentIndexQuerySession.Begin(gen, PlanLiteral("needle"), new DirtyContentSet());
        var p1 = Assert.IsType<PathDecision.ProvisionalPrune>(session.Route(Norm(@"C:\src\a.txt")));
        var p2 = Assert.IsType<PathDecision.ProvisionalPrune>(session.Route(Norm(@"C:\src\b.txt")));

        var reconciled = session.ReconcileAtB1(new DirtyContentSet(), reconciliationCertain: false);
        Assert.Contains(p1.AliasId, reconciled);
        Assert.Contains(p2.AliasId, reconciled);
    }

    // ─────────────────────────── Quiescent result equivalence (§5.1 #2) ───────────────────────────

    [Fact]
    public void QuiescentSearch_ClassificationCoversEveryTrueMatch()
    {
        var corpus = new (string Path, string Text)[]
        {
            (@"C:\src\1.cs", "the planner produces trigram queries"),
            (@"C:\src\2.cs", "unrelated content with no keyword"),
            (@"C:\src\3.cs", "another planner reference here"),
            (@"C:\src\4.cs", "planner planner planner repeated"),
            (@"C:\src\5.cs", "just some ordinary lines of text"),
        };
        var (gen, _) = BuildGeneration(corpus);
        var session = ContentIndexQuerySession.Begin(gen, PlanLiteral("planner"), new DirtyContentSet());

        // For a quiescent corpus, every path that truly contains "planner" must be routed to a live scan
        // (member) — never pruned. Nonmembers are the ones without the substring.
        foreach (var (path, text) in corpus)
        {
            var decision = session.Route(Norm(path));
            bool trulyMatches = text.Contains("planner", StringComparison.Ordinal);
            if (trulyMatches)
                Assert.IsType<PathDecision.LiveScanPath>(decision);
        }
    }
}
