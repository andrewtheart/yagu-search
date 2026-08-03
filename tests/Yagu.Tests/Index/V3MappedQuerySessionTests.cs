using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="V3MappedQuerySession"/> — the memory-mapped format-v3 classification brain the
/// out-of-process worker's Stage-2 shadow mode uses (plan §6 Stage 2). Its <see cref="V3MappedQuerySession.Classify"/>
/// must be <b>byte-for-byte identical</b> to the in-process oracle
/// <see cref="ContentIndexQuerySession.Classify"/> for every discovered path — member, nonmember, absent,
/// dirty-since-build, and missing-file-identity — so classifying a search over the mapped session never
/// changes which files are scanned.
/// </summary>
public sealed class V3MappedQuerySessionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root = @"C:\v3mapped";

    public V3MappedQuerySessionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-v3-mapped", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    /// <summary>Deterministic, distinct, non-null identities for every path (so nothing hits the
    /// missing-identity branch unless a test deliberately opts out).</summary>
    private static ContentIndexGeneration BuildGeneration(string root)
    {
        ulong next = 6000;
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out UsnFileIdentity id)) { id = new UsnFileIdentity(next++, next); assigned[norm] = id; }
            return new FileIdentity(0x9, id);
        }

        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(root + "\\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(root + "\\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        builder.AddDocument(root + "\\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        builder.AddDocument(root + "\\d.txt", Encoding.UTF8.GetBytes("unrelated filler content and words"));
        return builder.Build(ContentIndexManager.ScopeIdForRoot(root), "vol", root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\v3mapped", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    private static string Norm(string root, string file) => IndexScopeIdentity.NormalizePath(root + "\\" + file);

    /// <summary>
    /// The core Stage-2 gate: for a battery of queries the mapped classifier's verdict for every discovered
    /// path equals the in-process oracle's verdict. Each side evaluates candidates from the SAME planned
    /// query through its OWN engine (in-memory postings vs mapped v3 postings), so this proves the whole
    /// classify pipeline — candidate evaluation, collision-verified path lookup, and identity capture — is
    /// identical, not merely that Classify agrees given a shared candidate set.
    /// </summary>
    [Theory]
    [InlineData("planner")]
    [InlineData("trigram")]
    [InlineData("interest")]
    [InlineData("filler")]
    [InlineData("indexing")]
    public void Classify_MatchesInProcessOracle_ForEveryDiscoveredPath(string term)
    {
        ContentIndexGeneration gen = BuildGeneration(_root);
        ContentIndexV3Format.Write(_dir, gen);
        TrigramExpression query = PlanQuery(term);
        var dirty = new DirtyContentSet();

        ContentIndexQuerySession oracle = ContentIndexQuerySession.Begin(gen, query, dirty);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        V3MappedQuerySession mapped = V3MappedQuerySession.Begin(reader, query, dirty);

        Assert.Equal(oracle.CandidateCount, mapped.CandidateCount);
        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "d.txt", "absent.txt" })
        {
            string norm = Norm(_root, file);
            Assert.Equal(oracle.Classify(norm), mapped.Classify(norm));
        }
    }

    [Fact]
    public void Classify_MemberAndNonmember_MatchExpectedVerdicts()
    {
        ContentIndexGeneration gen = BuildGeneration(_root);
        ContentIndexV3Format.Write(_dir, gen);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        // "filler" appears only in d.txt.
        V3MappedQuerySession mapped = V3MappedQuerySession.Begin(reader, PlanQuery("filler"), new DirtyContentSet());

        Assert.IsType<IndexPathClassification.FreshIndexedMember>(mapped.Classify(Norm(_root, "d.txt")));
        Assert.IsType<IndexPathClassification.FreshIndexedNonmember>(mapped.Classify(Norm(_root, "a.txt")));
        Assert.IsType<IndexPathClassification.Unindexed>(mapped.Classify(Norm(_root, "missing.txt")));
    }

    [Fact]
    public void Classify_DirtySinceBuild_IsDirtyByUsn_LikeTheOracle()
    {
        ContentIndexGeneration gen = BuildGeneration(_root);
        ContentIndexV3Format.Write(_dir, gen);
        // Mark a member content dirty at B0 — dirty is checked BEFORE membership, so even a member live-scans.
        Assert.True(gen.TryGetAlias(Norm(_root, "d.txt"), out _, out long dContentId));
        var dirty = new DirtyContentSet();
        dirty.MarkDirty(dContentId);

        ContentIndexQuerySession oracle = ContentIndexQuerySession.Begin(gen, PlanQuery("filler"), dirty);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        V3MappedQuerySession mapped = V3MappedQuerySession.Begin(reader, PlanQuery("filler"), dirty);

        IndexPathClassification mappedVerdict = mapped.Classify(Norm(_root, "d.txt"));
        Assert.IsType<IndexPathClassification.DirtyByUsn>(mappedVerdict);
        Assert.Equal(oracle.Classify(Norm(_root, "d.txt")), mappedVerdict);
    }

    [Fact]
    public void Classify_NonmemberWithoutCapturedIdentity_LiveScans_LikeTheOracle()
    {
        // A doc whose durable identity could not be captured (provider returns null) can never be dirtied by
        // USN, so it must never be pruned as a fresh nonmember — both engines return DirtyByUsn instead.
        FileIdentity? Provider(string path)
            => IndexScopeIdentity.NormalizePath(path).EndsWith("noident.txt", StringComparison.Ordinal)
                ? null
                : new FileIdentity(0x9, new UsnFileIdentity(0xABCD, 0x55));

        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(_root + "\\noident.txt", Encoding.UTF8.GetBytes("distinctive zephyrqux content"));
        builder.AddDocument(_root + "\\other.txt", Encoding.UTF8.GetBytes("unrelated filler words here"));
        ContentIndexGeneration gen = builder.Build(ContentIndexManager.ScopeIdForRoot(_root), "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
        ContentIndexV3Format.Write(_dir, gen);

        // A query where noident.txt is a NONMEMBER (it has no "filler" trigram).
        TrigramExpression query = PlanQuery("filler");
        ContentIndexQuerySession oracle = ContentIndexQuerySession.Begin(gen, query, new DirtyContentSet());
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        V3MappedQuerySession mapped = V3MappedQuerySession.Begin(reader, query, new DirtyContentSet());

        string norm = Norm(_root, "noident.txt");
        IndexPathClassification mappedVerdict = mapped.Classify(norm);
        Assert.IsType<IndexPathClassification.DirtyByUsn>(mappedVerdict);
        Assert.Equal(oracle.Classify(norm), mappedVerdict);
    }

    [Fact]
    public void BeginWithCandidates_ClassifiesIdenticallyToBegin()
    {
        ContentIndexGeneration gen = BuildGeneration(_root);
        ContentIndexV3Format.Write(_dir, gen);
        TrigramExpression query = PlanQuery("trigram");
        var dirty = new DirtyContentSet();

        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;
        V3MappedQuerySession viaBegin = V3MappedQuerySession.Begin(reader, query, dirty);
        V3MappedQuerySession viaCandidates = V3MappedQuerySession.BeginWithCandidates(reader, reader.EvaluateSet(query), dirty);

        Assert.Equal(viaBegin.CandidateCount, viaCandidates.CandidateCount);
        foreach (string file in new[] { "a.txt", "b.txt", "c.txt", "d.txt" })
        {
            string norm = Norm(_root, file);
            Assert.Equal(viaBegin.Classify(norm), viaCandidates.Classify(norm));
        }
    }

    [Fact]
    public void Begin_NullArguments_Throw()
    {
        ContentIndexGeneration gen = BuildGeneration(_root);
        ContentIndexV3Format.Write(_dir, gen);
        using ContentIndexV3Reader reader = ContentIndexV3Format.TryOpen(_dir)!;

        Assert.Throws<ArgumentNullException>(() => V3MappedQuerySession.Begin(null!, PlanQuery("planner"), new DirtyContentSet()));
        Assert.Throws<ArgumentNullException>(() => V3MappedQuerySession.Begin(reader, null!, new DirtyContentSet()));
        Assert.Throws<ArgumentNullException>(() => V3MappedQuerySession.Begin(reader, PlanQuery("planner"), null!));
        Assert.Throws<ArgumentNullException>(() => V3MappedQuerySession.BeginWithCandidates(reader, null!, new DirtyContentSet()));

        V3MappedQuerySession mapped = V3MappedQuerySession.Begin(reader, PlanQuery("planner"), new DirtyContentSet());
        Assert.Throws<ArgumentNullException>(() => mapped.Classify(null!));
    }
}
