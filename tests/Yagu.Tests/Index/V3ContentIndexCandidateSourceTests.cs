using System.Text;
using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="V3ContentIndexCandidateSource"/> — the in-process memory-mapped format-v3 candidate
/// producer (plan §5.1 Stage 1 slice 6). It must return the SAME candidate content-id set as the in-memory
/// <see cref="TrigramPostingIndex.EvaluateSet"/> (so routing a query through it never changes results), and
/// fail safe to <c>false</c> for an un-upgraded generation (no v3 sidecars), a bad argument, or a corrupt
/// structure (so the accelerator falls back to the in-process evaluation).
/// </summary>
public sealed class V3ContentIndexCandidateSourceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _root = @"C:\v3src";

    public V3ContentIndexCandidateSourceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "yagu-v3-candidate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static IndexIngestionPolicy OpenPolicy => new(0, null, null, true, false, 0);

    private ContentIndexGeneration BuildGeneration()
    {
        ulong next = 5000;
        var assigned = new Dictionary<string, UsnFileIdentity>(StringComparer.Ordinal);
        FileIdentity? Provider(string path)
        {
            string norm = IndexScopeIdentity.NormalizePath(path);
            if (!assigned.TryGetValue(norm, out var id)) { id = new UsnFileIdentity(next++, next); assigned[norm] = id; }
            return new FileIdentity(0x7, id);
        }

        var builder = new ContentIndexGenerationBuilder(OpenPolicy, identityProvider: Provider);
        builder.AddDocument(_root + "\\a.txt", Encoding.UTF8.GetBytes("the planner produces trigram queries"));
        builder.AddDocument(_root + "\\b.txt", Encoding.UTF8.GetBytes("nothing whatsoever of interest here"));
        builder.AddDocument(_root + "\\c.txt", Encoding.UTF8.GetBytes("another planner mentions trigram indexing"));
        builder.AddDocument(_root + "\\d.txt", Encoding.UTF8.GetBytes("unrelated filler content and words"));
        return builder.Build(ContentIndexManager.ScopeIdForRoot(_root), "vol", _root, new UsnCheckpoint(1, 100), DateTimeOffset.UtcNow);
    }

    private static TrigramExpression PlanQuery(string term)
    {
        var options = new SearchOptions { Directory = @"C:\v3src", Query = term, CaseSensitive = true, ExactMatch = false, UseContentIndex = true };
        TrigramPlan plan = TrigramQueryPlanner.Plan(EffectiveSearchPattern.Resolve(options));
        return plan is TrigramPlan.Eligible eligible ? eligible.Query : TrigramExpression.All;
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("trigram")]
    [InlineData("interest")]
    [InlineData("indexing")]
    [InlineData("filler")]
    public void TryEvaluate_MatchesTheInProcessPostingSet(string term)
    {
        ContentIndexGeneration gen = BuildGeneration();
        ContentIndexV3Format.Write(_dir, gen);
        TrigramExpression query = PlanQuery(term);

        Assert.True(V3ContentIndexCandidateSource.Instance.TryEvaluate(_dir, query, out IReadOnlySet<int> actual));
        IReadOnlySet<int> expected = gen.Postings.EvaluateSet(query);
        Assert.True(expected.SetEquals(actual), $"term '{term}': expected [{string.Join(",", expected)}] got [{string.Join(",", actual)}]");
    }

    [Fact]
    public void TryEvaluate_NoV3Sidecars_ReturnsFalse()
    {
        // Un-upgraded generation: the directory has no query-*.v3 files → fall back to in-process.
        Assert.False(V3ContentIndexCandidateSource.Instance.TryEvaluate(_dir, PlanQuery("planner"), out IReadOnlySet<int> candidates));
        Assert.Empty(candidates);
    }

    [Fact]
    public void TryEvaluate_NullOrEmptyArguments_ReturnFalse()
    {
        Assert.False(V3ContentIndexCandidateSource.Instance.TryEvaluate("", PlanQuery("planner"), out _));
        Assert.False(V3ContentIndexCandidateSource.Instance.TryEvaluate(_dir, null!, out _));
    }

    [Fact]
    public void TryEvaluate_CorruptPostingsBody_FailsSafeToFalse()
    {
        ContentIndexGeneration gen = BuildGeneration();
        ContentIndexV3Format.Write(_dir, gen);

        // Corrupt the FIRST body byte (past the fixed header + single block-hash + header-hash). The header
        // hash still verifies so TryOpen succeeds, but reading the block fails integrity → the source catches
        // the InvalidDataException and returns false (→ in-process fallback).
        string postingsPath = Path.Combine(_dir, ContentIndexV3Format.PostingsFile);
        byte[] bytes = File.ReadAllBytes(postingsPath);
        int bodyStart = 24 + /*blockCount*/ 1 * 8 + /*headerHash*/ 8;
        bytes[bodyStart] ^= 0xFF;
        File.WriteAllBytes(postingsPath, bytes);

        Assert.False(V3ContentIndexCandidateSource.Instance.TryEvaluate(_dir, PlanQuery("planner"), out IReadOnlySet<int> candidates));
        Assert.Empty(candidates);
    }
}
