using System.Text.RegularExpressions;
using Yagu.Services;

namespace Yagu.Tests;

public sealed class SearchQueryParserTests
{
    [Fact]
    public void BuildLiteralRegexPattern_UnquotedTerms_MatchesEachTerm()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("test 123");

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "contains test only");
        Assert.Matches(regex, "contains 123 only");
        Assert.DoesNotMatch(regex, "contains neither value");
    }

    [Fact]
    public void BuildLiteralRegexPattern_QuotedPhrase_MatchesExactPhrase()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("\"test 123\"");

        Assert.NotNull(pattern);
        var regex = new Regex(pattern!, RegexOptions.CultureInvariant);
        Assert.Matches(regex, "the value is test 123 here");
        Assert.DoesNotMatch(regex, "the value has test then later 123");
    }

    [Fact]
    public void BuildLiteralRegexPattern_EscapesRegexCharacters()
    {
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("a+b c.d");

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
        string? pattern = SearchQueryParser.BuildLiteralRegexPattern("foo  bar");

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
}