using System.Text.RegularExpressions;
using Yagu.Helpers;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for the developer "quick search" catalog surfaced on the Advanced Options ▸ Quick searches
/// tab. Each preset must be a valid regex with a stable, unique key so the XAML buttons can wire to it.
/// </summary>
public sealed class QuickSearchPresetsTests
{
    [Fact]
    public void All_PatternsAreValidRegexes()
    {
        foreach (var preset in QuickSearchPresets.All)
        {
            var ex = Record.Exception(() => _ = new Regex(preset.Pattern));
            Assert.True(ex is null, $"Preset '{preset.Key}' has an invalid regex: {preset.Pattern} ({ex?.Message})");
        }
    }

    [Fact]
    public void All_KeysAreUniqueAndNonEmpty()
    {
        var keys = QuickSearchPresets.All.Select(p => p.Key).ToList();
        Assert.All(keys, k => Assert.False(string.IsNullOrWhiteSpace(k)));
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CodeAnnotations_ReusesTheCanonicalSharedPattern()
    {
        var preset = QuickSearchPresets.Find(QuickSearchPresets.CodeAnnotationsKey);
        Assert.NotNull(preset);
        Assert.Equal(CodeAnnotationQuery.Pattern, preset!.Pattern);
        Assert.True(preset.CaseSensitive);
    }

    [Fact]
    public void Find_ReturnsPresetForKnownKey_AndNullOtherwise()
    {
        Assert.NotNull(QuickSearchPresets.Find("secrets"));
        Assert.Null(QuickSearchPresets.Find("does-not-exist"));
        Assert.Null(QuickSearchPresets.Find(null));
    }

    [Fact]
    public void Catalog_IncludesTheDeveloperFocusedPresets()
    {
        var keys = QuickSearchPresets.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[] { "code-annotations", "merge-conflicts", "debug-output", "secrets", "urls", "emails", "empty-catch", "deprecated", "guids" })
            Assert.Contains(expected, keys);
    }

    [Theory]
    [InlineData("merge-conflicts", "<<<<<<< HEAD", true)]
    [InlineData("merge-conflicts", "=======", true)]
    [InlineData("merge-conflicts", ">>>>>>> feature/x", true)]
    [InlineData("merge-conflicts", "const x = a === b;", false)]
    [InlineData("debug-output", "    console.log(\"here\")", true)]
    [InlineData("debug-output", "System.out.println(x)", true)]
    [InlineData("debug-output", "    return value;", false)]
    [InlineData("secrets", "apiKey = \"abc123\"", true)]
    [InlineData("secrets", "connection_string: server=...", true)]
    [InlineData("secrets", "the password field", false)]
    [InlineData("urls", "see https://example.com/x for details", true)]
    [InlineData("urls", "no link here", false)]
    [InlineData("emails", "reach me at dev@example.co.uk", true)]
    [InlineData("empty-catch", "} catch (e) {}", true)]
    [InlineData("empty-catch", "} catch (e) { log(e); }", false)]
    [InlineData("guids", "id: 550e8400-e29b-41d4-a716-446655440000", true)]
    public void Patterns_MatchRepresentativeSamples(string key, string line, bool expectMatch)
    {
        var preset = QuickSearchPresets.Find(key);
        Assert.NotNull(preset);
        var options = preset!.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
        Assert.Equal(expectMatch, Regex.IsMatch(line, preset.Pattern, options));
    }
}
