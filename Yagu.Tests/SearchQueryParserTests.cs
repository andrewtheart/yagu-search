using System.Text.RegularExpressions;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class SearchQueryParserTests
{
    [Fact]
    public void BuildLiteralRegexPattern_MultiTerms_MatchesEachTerm()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("test 123", exactMatch: false);

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "contains test only");
        Assert.Matches(regex, "contains 123 only");
        Assert.DoesNotMatch(regex, "contains neither value");
    }

    [Fact]
    public void BuildLiteralRegexPattern_ExactMatch_MatchesWholePhrase()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("test 123", exactMatch: true);

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "the value is test 123 here");
        Assert.DoesNotMatch(regex, "the value has test then later 123");
    }

    [Fact]
    public void BuildLiteralRegexPattern_ExactMatch_MatchesWholeWordOnly()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("async", exactMatch: true);

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "an async method");
        Assert.DoesNotMatch(regex, "runs asynchronously");
        Assert.DoesNotMatch(regex, "a preasync flag");
        Assert.DoesNotMatch(regex, "call _async() now");
    }

    [Fact]
    public void BuildLiteralRegexPattern_ExactMatch_PunctuationQuery_MatchesSubstring()
    {
        // A query with no word characters on its edges cannot be "whole word";
        // it should still match as a literal substring.
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("+=", exactMatch: true);

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "x += 1");
        Assert.Matches(regex, "count+=2");
    }

    [Fact]
    public void BuildLiteralRegexPattern_EscapesRegexCharacters()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("a+b c.d", exactMatch: false);

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "literal a+b");
        Assert.Matches(regex, "literal c.d");
        Assert.DoesNotMatch(regex, "regex-ish aaab");
        Assert.DoesNotMatch(regex, "regex-ish cxd");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildLiteralRegexPattern_EmptyOrWhitespace_ReturnsNull(string? query)
    {
        Assert.Null(SearchQueryParser.BuildLiteralRegexPattern(query!));
    }

    [Fact]
    public void BuildLiteralRegexPattern_ConsecutiveSpaces_IgnoresEmptyTerms()
    {
        // Double space between terms exercises the AddCurrentTerm empty-current branch
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("foo  bar", exactMatch: false);

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "has foo");
        Assert.Matches(regex, "has bar");
        Assert.DoesNotMatch(regex, "has baz");
    }

    [Fact]
    public void ParseLiteralTerms_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(SearchQueryParser.ParseLiteralTerms(null!));
        Assert.Empty(SearchQueryParser.ParseLiteralTerms(""));
        Assert.Empty(SearchQueryParser.ParseLiteralTerms("   "));
    }

    [Fact]
    public void ParseLiteralTerms_ExactMatch_ReturnsSingleTrimmedTerm()
    {
        var terms = SearchQueryParser.ParseLiteralTerms("  hello world  ", exactMatch: true);
        Assert.Single(terms);
        Assert.Equal("hello world", terms[0]);
    }

    [Fact]
    public void ParseLiteralTerms_MultiTerm_SplitsOnWhitespace()
    {
        var terms = SearchQueryParser.ParseLiteralTerms("foo bar baz", exactMatch: false);
        Assert.Equal(3, terms.Count);
        Assert.Equal("foo", terms[0]);
        Assert.Equal("bar", terms[1]);
        Assert.Equal("baz", terms[2]);
    }

    [Fact]
    public void ParseLiteralTerms_MultiTerm_IgnoresConsecutiveSpaces()
    {
        var terms = SearchQueryParser.ParseLiteralTerms("a  b", exactMatch: false);
        Assert.Equal(2, terms.Count);
        Assert.Equal("a", terms[0]);
        Assert.Equal("b", terms[1]);
    }

    [Fact]
    public void ParseLiteralTerms_DefaultExactMatch_IsTrue()
    {
        // Default exactMatch = true, so multi-word query stays as one term
        var terms = SearchQueryParser.ParseLiteralTerms("hello world");
        Assert.Single(terms);
        Assert.Equal("hello world", terms[0]);
    }
}