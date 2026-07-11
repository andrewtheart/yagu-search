using System.Text.RegularExpressions;
using Yagu.Helpers;

namespace Yagu.Tests;

public sealed class CodeAnnotationQueryTests
{
    [Fact]
    public void Pattern_IsWholeWordAlternationOverMarkers()
    {
        Assert.Equal(@"\b(TODO|FIXME|HACK|BUG|XXX|NOTE|OPTIMIZE|REVIEW)\b", CodeAnnotationQuery.Pattern);
    }

    [Fact]
    public void Markers_IncludeTheCommonAnnotations()
    {
        Assert.Contains("TODO", CodeAnnotationQuery.Markers);
        Assert.Contains("FIXME", CodeAnnotationQuery.Markers);
        Assert.Contains("HACK", CodeAnnotationQuery.Markers);
        Assert.Contains("BUG", CodeAnnotationQuery.Markers);
    }

    [Theory]
    [InlineData("// TODO: fix this", true)]
    [InlineData("# FIXME later", true)]
    [InlineData("/* HACK around the bug */", true)]
    [InlineData("<!-- REVIEW this markup -->", true)]
    [InlineData("someOptimizeThing", false)]   // whole-word boundary: not part of an identifier
    [InlineData("mytodolist", false)]           // 'todo' inside a word doesn't match
    [InlineData("nothing to see", false)]
    public void Pattern_MatchesAnnotationsAsWholeWords(string line, bool expectMatch)
    {
        // Case-sensitive, matching how the preset runs it.
        bool matched = Regex.IsMatch(line, CodeAnnotationQuery.Pattern, RegexOptions.None);
        Assert.Equal(expectMatch, matched);
    }

    [Fact]
    public void Pattern_IsCaseSensitiveByConvention()
    {
        // Lowercase 'todo' is not an annotation marker under a case-sensitive match.
        Assert.False(Regex.IsMatch("todo something", CodeAnnotationQuery.Pattern, RegexOptions.None));
        Assert.True(Regex.IsMatch("TODO something", CodeAnnotationQuery.Pattern, RegexOptions.None));
    }
}
