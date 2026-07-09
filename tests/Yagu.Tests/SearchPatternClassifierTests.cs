using Yagu.Helpers;

namespace Yagu.Tests;

public class SearchPatternClassifierTests
{
    [Theory]
    [InlineData(".")]
    [InlineData(".*")]
    [InlineData(".+")]
    [InlineData(".?")]
    [InlineData("^.*$")]
    [InlineData("^.+$")]
    [InlineData("^.*")]
    [InlineData(".*$")]
    [InlineData("(?s).")]
    [InlineData("(?s).*")]
    [InlineData("[\\s\\S]")]
    [InlineData("[\\S\\s]")]
    [InlineData("[\\w\\W]")]
    [InlineData("  .  ")] // surrounding whitespace is trimmed
    public void IsMatchEverythingRegex_TrueForWholeMatchAllPatterns(string pattern)
    {
        Assert.True(SearchPatternClassifier.IsMatchEverythingRegex(pattern));
    }

    [Theory]
    [InlineData("report")]
    [InlineData("a.b")]        // a real pattern that merely contains a dot
    [InlineData("foo.*bar")]   // .* inside a larger pattern
    [InlineData("v\\d.\\d")]
    [InlineData("TODO")]
    [InlineData("\\d{3}")]
    [InlineData("\\bword\\b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsMatchEverythingRegex_FalseForRealOrEmptyPatterns(string? pattern)
    {
        Assert.False(SearchPatternClassifier.IsMatchEverythingRegex(pattern));
    }
}
