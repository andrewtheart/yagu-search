using System.Text.RegularExpressions;
using Yagu.Models;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Additional branch coverage for SearchQueryParser: BuildLiteralAlternation ordering,
/// special characters, and interaction with SearchOptions defaults.
/// </summary>
public sealed class SearchQueryParserBranchCoverageTests
{
    // ═══════════════════════════════════════════════════════════════
    //  BuildLiteralAlternation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildLiteralAlternation_OrdersByLengthDescending()
    {
        var terms = new[] { "a", "longer", "ab" };
        var result = SearchQueryParser.BuildLiteralAlternation(terms);
        // Longest term first in the alternation
        Assert.StartsWith("longer", result);
    }

    [Fact]
    public void BuildLiteralAlternation_SkipsEmptyTerms()
    {
        var terms = new[] { "", "hello", "", "world", "" };
        var result = SearchQueryParser.BuildLiteralAlternation(terms);
        Assert.DoesNotContain("||", result);
        Assert.Contains("hello", result);
        Assert.Contains("world", result);
    }

    [Fact]
    public void BuildLiteralAlternation_EscapesRegexMetaCharacters()
    {
        var terms = new[] { "a+b", "c.d", "(e)", "[f]", "g*h", "i?j" };
        var result = SearchQueryParser.BuildLiteralAlternation(terms);
        // Each term should be escaped
        var regex = new Regex(result);
        Assert.Matches(regex, "literal a+b");
        Assert.Matches(regex, "literal c.d");
        Assert.Matches(regex, "literal (e)");
        Assert.DoesNotMatch(regex, "aab"); // + not treated as quantifier
    }

    [Fact]
    public void BuildLiteralAlternation_SingleTerm_NoAlternation()
    {
        var terms = new[] { "only" };
        var result = SearchQueryParser.BuildLiteralAlternation(terms);
        Assert.DoesNotContain("|", result);
        Assert.Equal("only", result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  BuildLiteralRegexPattern - edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BuildLiteralRegexPattern_SingleCharQuery_Works()
    {
        var pattern = SearchQueryParser.BuildLiteralRegexPattern("x");
        Assert.NotNull(pattern);
        Assert.Matches(new Regex(pattern!), "contains x here");
    }

    [Fact]
    public void BuildLiteralRegexPattern_TabsInQuery_TreatedAsWhitespace()
    {
        var terms = SearchQueryParser.ParseLiteralTerms("foo\tbar", exactMatch: false);
        Assert.Equal(2, terms.Count);
        Assert.Equal("foo", terms[0]);
        Assert.Equal("bar", terms[1]);
    }

    [Fact]
    public void BuildLiteralRegexPattern_UnicodeCharacters_Preserved()
    {
        var pattern = SearchQueryParser.BuildLiteralRegexPattern("café");
        Assert.NotNull(pattern);
        var regex = new Regex(pattern!);
        Assert.Matches(regex, "at the café today");
        Assert.DoesNotMatch(regex, "at the cafe today");
    }

    [Fact]
    public void BuildLiteralRegexPattern_LeadingTrailingSpaces_ExactMatch_Trimmed()
    {
        var pattern = SearchQueryParser.BuildLiteralRegexPattern("  test  ", exactMatch: true);
        Assert.NotNull(pattern);
        var regex = new Regex(pattern!);
        Assert.Matches(regex, "this is a test right here");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ParseLiteralTerms - additional edges
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ParseLiteralTerms_MixedWhitespace_SplitsCorrectly()
    {
        var terms = SearchQueryParser.ParseLiteralTerms("a\t\tb \n c", exactMatch: false);
        Assert.Equal(3, terms.Count);
        Assert.Equal("a", terms[0]);
        Assert.Equal("b", terms[1]);
        Assert.Equal("c", terms[2]);
    }

    [Fact]
    public void ParseLiteralTerms_OnlyWhitespaceChars_ReturnsEmpty()
    {
        Assert.Empty(SearchQueryParser.ParseLiteralTerms("\t \n \r", exactMatch: false));
    }
}

/// <summary>
/// Additional branch coverage for SearchOptions.ResolveContentSearchParallelism
/// at edge values and processor count boundaries.
/// </summary>
public sealed class SearchOptionsParallelismBranchTests
{
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 100, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(2, 4, 2)]
    [InlineData(2, 8, 4)]
    [InlineData(3, 4, 8)]
    [InlineData(3, 8, 16)]
    [InlineData(4, 4, 4)]
    [InlineData(4, 8, 8)]
    [InlineData(0, 8, 0)]
    [InlineData(5, 8, 0)]
    [InlineData(-1, 8, 0)]
    [InlineData(100, 8, 0)]
    public void ResolveContentSearchParallelism_EdgeCases(int index, int processorCount, int expected)
    {
        Assert.Equal(expected, SearchOptions.ResolveContentSearchParallelism(index, processorCount));
    }

    [Fact]
    public void ResolveContentSearchParallelism_ZeroProcessorCount_ClampsToOne()
    {
        // Math.Max(1, 0) = 1 -> index 1 returns 1
        Assert.Equal(1, SearchOptions.ResolveContentSearchParallelism(1, 0));
        // index 2 with 1 core = max(1, 1/2) = max(1,0) = 1
        Assert.Equal(1, SearchOptions.ResolveContentSearchParallelism(2, 0));
    }

    [Fact]
    public void ResolveContentSearchParallelism_NegativeProcessorCount_ClampsToOne()
    {
        Assert.Equal(1, SearchOptions.ResolveContentSearchParallelism(1, -5));
    }

    [Fact]
    public void SearchOptions_DefaultValues_AreCorrect()
    {
        var opts = new SearchOptions { Directory = ".", Query = "test" };
        Assert.True(opts.ExactMatch);
        Assert.Equal(3, opts.ContextLines);
        Assert.Equal(SearchMode.Both, opts.SearchMode);
        Assert.Equal(50_000, opts.MaxResults);
        Assert.True(opts.SkipBinary);
        Assert.True(opts.GitignoreTakesPrecedence);
        Assert.False(opts.ObeyGitignore);
        Assert.False(opts.CaseSensitive);
        Assert.False(opts.UseRegex);
        Assert.Equal(0, opts.MaxMatchesPerFile);
        Assert.Equal(0, opts.MaxSearchDepth);
        Assert.False(opts.SearchInsideArchives);
        Assert.True(opts.ExcludeAdminProtectedPaths);
        Assert.Equal(75, opts.MemoryPressurePercent);
    }
}
