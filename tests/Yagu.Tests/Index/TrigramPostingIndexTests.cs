using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="TrigramPostingIndex"/> (plan §3.1/§5): posting construction, boolean query
/// evaluation (rare-first AND, OR union), the internal consistency between the index candidate set
/// and per-document <see cref="TrigramExpression.Evaluate"/>, and the end-to-end superset guarantee
/// that planner + index never prune a truly matching document.
/// </summary>
public sealed class TrigramPostingIndexTests
{
    private static IReadOnlyCollection<Trigram> Doc(string ascii)
    {
        ContentRepresentation.Classify(Encoding.UTF8.GetBytes(ascii), out var trigrams);
        return trigrams;
    }

    private static TrigramPostingIndex BuildFrom(params string[] docs)
        => TrigramPostingIndex.Build(docs.Select(Doc).ToList());

    private static Trigram Tg(string s) => new((byte)s[0], (byte)s[1], (byte)s[2]);

    [Fact]
    public void Build_AssignsContiguousDocIds_AndCounts()
    {
        var index = BuildFrom("hello", "world", "help");
        Assert.Equal(3, index.DocumentCount);
        Assert.True(index.TrigramCount > 0);
    }

    [Fact]
    public void GetPosting_ReturnsSortedDocIds()
    {
        var index = BuildFrom("abcd", "xabc", "nope");
        // "abc" appears in docs 0 and 1.
        Assert.Equal(new[] { 0, 1 }, index.GetPosting(Tg("abc")));
        Assert.Empty(index.GetPosting(Tg("zzz")));
        Assert.Equal(2, index.DocumentFrequency(Tg("abc")));
        Assert.Equal(0, index.DocumentFrequency(Tg("zzz")));
    }

    [Fact]
    public void Build_DeduplicatesRepeatedTrigramWithinDocument()
    {
        // "aaaa" has trigram "aaa" twice but the posting must list doc 0 once.
        var index = BuildFrom("aaaa");
        Assert.Equal(new[] { 0 }, index.GetPosting(Tg("aaa")));
    }

    [Fact]
    public void Evaluate_SingleTrigram_ReturnsPosting()
    {
        var index = BuildFrom("hello", "help", "world");
        var query = TrigramExpression.OfTrigram(Tg("hel"));
        Assert.Equal(new[] { 0, 1 }, index.Evaluate(query));
    }

    [Fact]
    public void Evaluate_And_IntersectsPostings()
    {
        var index = BuildFrom("foobar", "foobaz", "barfoo", "unrelated");
        var query = TrigramExpression.And(
            TrigramExpression.OfTrigram(Tg("foo")),
            TrigramExpression.OfTrigram(Tg("bar")));
        // doc0 foobar: has foo,bar; doc2 barfoo: has bar,foo. doc1 foobaz: foo but no bar.
        Assert.Equal(new[] { 0, 2 }, index.Evaluate(query));
    }

    [Fact]
    public void Evaluate_And_EmptyConjunctShortCircuits()
    {
        var index = BuildFrom("foobar");
        var query = TrigramExpression.And(
            TrigramExpression.OfTrigram(Tg("foo")),
            TrigramExpression.OfTrigram(Tg("zzz"))); // absent
        Assert.Empty(index.Evaluate(query));
    }

    [Fact]
    public void Evaluate_Or_UnionsPostings()
    {
        var index = BuildFrom("aaax", "bbby", "cccz");
        var query = TrigramExpression.Or(
            TrigramExpression.OfTrigram(Tg("aaa")),
            TrigramExpression.OfTrigram(Tg("bbb")));
        Assert.Equal(new[] { 0, 1 }, index.Evaluate(query));
    }

    [Fact]
    public void Evaluate_All_ReturnsEveryDocument_None_ReturnsNothing()
    {
        var index = BuildFrom("a", "b", "c");
        Assert.Equal(new[] { 0, 1, 2 }, index.Evaluate(TrigramExpression.All));
        Assert.Empty(index.Evaluate(TrigramExpression.None));
    }

    [Fact]
    public void Evaluate_NestedAndOr_ProducesCorrectCandidates()
    {
        var index = BuildFrom("foobar", "fooqux", "quxbar", "nothing");
        // foo AND (bar OR qux)
        var query = TrigramExpression.And(
            TrigramExpression.OfTrigram(Tg("foo")),
            TrigramExpression.Or(
                TrigramExpression.OfTrigram(Tg("bar")),
                TrigramExpression.OfTrigram(Tg("qux"))));
        Assert.Equal(new[] { 0, 1 }, index.Evaluate(query));
    }

    [Fact]
    public void EvaluateSet_MatchesEvaluate()
    {
        var index = BuildFrom("foobar", "foobaz");
        var query = TrigramExpression.OfTrigram(Tg("foo"));
        Assert.Equal(new HashSet<int> { 0, 1 }, index.EvaluateSet(query));
    }

    [Fact]
    public void Build_NullDocumentEntry_IsSkipped()
    {
        var docs = new List<IReadOnlyCollection<Trigram>> { Doc("hello"), null! };
        var index = TrigramPostingIndex.Build(docs);
        Assert.Equal(2, index.DocumentCount);
        Assert.Equal(new[] { 0 }, index.Evaluate(TrigramExpression.OfTrigram(Tg("hel"))));
    }

    [Theory]
    [MemberData(nameof(MalformedContentBodies))]
    public void BuildFromContentBody_MalformedInput_Throws(byte[] body)
        => Assert.Throws<System.IO.InvalidDataException>(() =>
            TrigramPostingIndex.BuildFromContentBody(body, out _));

    public static IEnumerable<object[]> MalformedContentBodies()
    {
        yield return new object[] { Array.Empty<byte>() };
        yield return new object[] { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF } };
        yield return new object[] { new byte[] { 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF } };
        yield return new object[] { new byte[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 2 } };
    }

    [Fact]
    public void BuildFromContentBody_StreamsDistinctPostingsAndDeduplicatesPerDocument()
    {
        Trigram a = Tg("aaa");
        Trigram b = Tg("bbb");
        byte[] body = ContentBody(
            new[] { a.Value, a.Value, b.Value },
            new[] { a.Value });

        TrigramPostingIndex index = TrigramPostingIndex.BuildFromContentBody(body, out int documentCount);

        Assert.Equal(2, documentCount);
        Assert.Equal(new[] { 0, 1 }, index.GetPosting(a));
        Assert.Equal(new[] { 0 }, index.GetPosting(b));
    }

    [Fact]
    public void Evaluate_MergeLists_CoversEitherExhaustionOrder()
    {
        Trigram a = Tg("aaa");
        Trigram b = Tg("bbb");
        var index = TrigramPostingIndex.Build(new IReadOnlyCollection<Trigram>[]
        {
            new[] { b },
            new[] { a, b },
            new[] { a },
        });

        TrigramExpression aNode = TrigramExpression.OfTrigram(a);
        TrigramExpression bNode = TrigramExpression.OfTrigram(b);
        Assert.Equal(new[] { 1 }, index.Evaluate(TrigramExpression.And(aNode, bNode)));
        Assert.Equal(new[] { 0, 1, 2 }, index.Evaluate(TrigramExpression.Or(aNode, bNode)));
        Assert.Equal(new[] { 0, 1, 2 }, index.Evaluate(TrigramExpression.Or(bNode, aNode)));
    }

    [Fact]
    public void MergePrimitives_HandleLessEqualGreaterAndBothTails()
    {
        Assert.Equal(new[] { 1, 2 }, TrigramPostingIndex.Union(
            new[] { 1, 2 },
            Array.Empty<int>()));
        Assert.Equal(new[] { 3 }, TrigramPostingIndex.Intersect(
            new[] { 1, 3, 5 },
            new[] { 2, 3, 4 }));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, TrigramPostingIndex.Union(
            new[] { 1, 3, 5 },
            new[] { 2, 3, 4 }));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, TrigramPostingIndex.Union(
            new[] { 2, 3, 4 },
            new[] { 1, 3, 5 }));
    }

    private static byte[] ContentBody(params uint[][] documents)
    {
        using var stream = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(stream);
        writer.Write(documents.Length);
        foreach (uint[] document in documents)
        {
            writer.Write(document.Length);
            foreach (uint trigram in document)
                writer.Write(trigram);
        }
        return stream.ToArray();
    }

    // ─────────────────────────── End-to-end with the planner ───────────────────────────

    private static readonly string[] Corpus =
    {
        "the quick brown fox jumps over the lazy dog",
        "hello world, this is a content index test",
        "async await task completion source pattern",
        "foobar baz qux the end of the line",
        "colour and color are both spelled here",
        "TODO: fix the parser and the planner soon",
        "aaa bbb ccc ddd eee fff ggg hhh iii jjj",
        "no interesting words at all in this one!!",
        "the planner produces required trigram queries",
        "grep utility yagu searches file contents fast",
    };

    private static void AssertSuperset(EffectiveSearchPattern pattern, Func<string, bool> trueMatch)
    {
        var index = TrigramPostingIndex.Build(Corpus.Select(Doc).ToList());
        var plan = TrigramQueryPlanner.Plan(pattern);
        if (plan is not TrigramPlan.Eligible eligible)
            return; // ineligible → full live scan, nothing to assert

        var candidates = index.EvaluateSet(eligible.Query);
        for (int i = 0; i < Corpus.Length; i++)
        {
            if (trueMatch(Corpus[i]))
                Assert.Contains(i, candidates); // superset: a true match is never pruned
        }
    }

    [Fact]
    public void EndToEnd_Literal_CandidateSetIsSuperset()
    {
        var pattern = new EffectiveSearchPattern("planner", isRegex: false, caseSensitive: true, multiline: false, dotAll: false);
        AssertSuperset(pattern, doc => doc.Contains("planner", StringComparison.Ordinal));
    }

    [Fact]
    public void EndToEnd_Regex_CandidateSetIsSuperset()
    {
        var pattern = new EffectiveSearchPattern("the.*trigram", isRegex: true, caseSensitive: true, multiline: false, dotAll: false);
        var regex = new Regex("the.*trigram");
        AssertSuperset(pattern, doc => regex.IsMatch(doc));
    }

    [Fact]
    public void EndToEnd_EligibleQuery_IsSelective()
    {
        // "planner" appears in only 2 of 10 docs → the candidate set must be a strict subset.
        var pattern = new EffectiveSearchPattern("planner", isRegex: false, caseSensitive: true, multiline: false, dotAll: false);
        var index = TrigramPostingIndex.Build(Corpus.Select(Doc).ToList());
        var eligible = Assert.IsType<TrigramPlan.Eligible>(TrigramQueryPlanner.Plan(pattern));
        var candidates = index.EvaluateSet(eligible.Query);
        Assert.True(candidates.Count < Corpus.Length, "Selective query should prune most documents.");
        Assert.True(candidates.Count >= 2, "Both true-match documents must remain.");
    }

    // ─────────────────── Consistency: index candidacy == per-doc expression eval ───────────────────

    [Fact]
    public void IndexCandidacy_MatchesPerDocumentExpressionEvaluation()
    {
        var docTrigrams = Corpus.Select(d => new HashSet<Trigram>(Doc(d))).ToList();
        var index = TrigramPostingIndex.Build(docTrigrams.Select(s => (IReadOnlyCollection<Trigram>)s).ToList());

        var rng = new Random(4242);
        const string alphabet = "abcdefghijklmnop ";
        for (int iter = 0; iter < 300; iter++)
        {
            string literal = RandomWord(rng, alphabet, 3, 6);
            var plan = TrigramQueryPlanner.Plan(
                new EffectiveSearchPattern(literal, isRegex: false, caseSensitive: true, multiline: false, dotAll: false));
            if (plan is not TrigramPlan.Eligible eligible)
                continue;

            var candidates = index.EvaluateSet(eligible.Query);
            for (int docId = 0; docId < docTrigrams.Count; docId++)
            {
                bool inIndex = candidates.Contains(docId);
                bool byExpression = eligible.Query.Evaluate(docTrigrams[docId]);
                Assert.Equal(byExpression, inIndex);
            }
        }
    }

    private static string RandomWord(Random rng, string alphabet, int min, int max)
    {
        int len = rng.Next(min, max + 1);
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
            sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }
}
