using Yagu.Helpers;
using Yagu.Models;

namespace Yagu.Tests;

/// <summary>
/// Extended branch coverage for GlobMatcher including regex mode, segment matching,
/// edge cases, and timeout behavior.
/// </summary>
public sealed class GlobMatcherBranchCoverageTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Basics / Construction
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void EmptyIncludes_MatchesEverything()
    {
        var m = new GlobMatcher([], []);
        Assert.True(m.Matches(@"C:\any\path\file.txt"));
    }

    [Fact]
    public void EmptyExcludes_DoesNotExclude()
    {
        var m = new GlobMatcher(["*.cs"], []);
        Assert.True(m.Matches(@"C:\src\file.cs"));
    }

    [Fact]
    public void NullIncludesAndExcludes_DoesNotThrow()
    {
        var m = new GlobMatcher(null!, null!);
        Assert.True(m.Matches(@"C:\file.txt"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Extension Patterns
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("cs", @"C:\src\file.cs", true)]
    [InlineData("cs", @"C:\src\file.txt", false)]
    [InlineData("txt", @"C:\dir\readme.txt", true)]
    [InlineData("json", @"C:\config\app.json", true)]
    [InlineData("json", @"C:\config\app.jsonl", false)]
    public void BareExtensionToken_MatchesByExtension(string include, string path, bool expected)
    {
        var m = new GlobMatcher([include], []);
        Assert.Equal(expected, m.Matches(path));
    }

    [Theory]
    [InlineData("*.cs", @"C:\src\file.cs", true)]
    [InlineData("*.cs", @"C:\src\file.txt", false)]
    [InlineData("*.tsx", @"C:\app\Component.tsx", true)]
    public void StarDotExtension_MatchesByExtension(string include, string path, bool expected)
    {
        var m = new GlobMatcher([include], []);
        Assert.Equal(expected, m.Matches(path));
    }

    [Fact]
    public void CommaSeparatedExtensions_AllMatch()
    {
        var m = new GlobMatcher(["cs, txt, json"], []);
        Assert.True(m.Matches(@"C:\file.cs"));
        Assert.True(m.Matches(@"C:\file.txt"));
        Assert.True(m.Matches(@"C:\file.json"));
        Assert.False(m.Matches(@"C:\file.py"));
    }

    [Fact]
    public void SemicolonSeparatedExtensions_AllMatch()
    {
        var m = new GlobMatcher(["cs; txt"], []);
        Assert.True(m.Matches(@"C:\file.cs"));
        Assert.True(m.Matches(@"C:\file.txt"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Segment Patterns
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("node_modules", @"C:\project\node_modules\pkg\index.js", true)]
    [InlineData("node_modules", @"C:\project\src\file.js", false)]
    [InlineData("bin", @"C:\project\binary\file.bin", true)]  // "bin" is <=5 chars alphanumeric -> extension match
    [InlineData("bin", @"C:\project\binary\file.dll", false)]
    public void SegmentPattern_MatchesPathSegment(string pattern, string path, bool expected)
    {
        var m = new GlobMatcher([pattern], []);
        Assert.Equal(expected, m.Matches(path));
    }

    [Fact]
    public void SegmentExclude_ExcludesMatchingPaths()
    {
        var m = new GlobMatcher([], ["node_modules"]);
        Assert.False(m.Matches(@"C:\project\node_modules\pkg\file.js"));
        Assert.True(m.Matches(@"C:\project\src\file.js"));
    }

    [Fact]
    public void SegmentPattern_MatchesExactFileName()
    {
        // A segment pattern like "Thumbs.db" should match as path segment
        var m = new GlobMatcher([], ["Thumbs.db"]);
        Assert.False(m.Matches(@"C:\dir\Thumbs.db"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Glob-to-Regex Patterns
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("src/**/*.cs", @"C:\project\src\deep\nested\file.cs", true)]
    [InlineData("src/**/*.cs", @"C:\project\src\file.cs", true)]
    [InlineData("src/**/*.cs", @"C:\project\lib\file.cs", false)]
    [InlineData("**/test/*.js", @"C:\project\test\spec.js", true)]
    [InlineData("**/test/*.js", @"C:\deep\path\test\spec.js", true)]
    [InlineData("**/test/*.js", @"C:\test\nested\spec.js", false)]
    public void GlobPattern_ConvertsToRegexCorrectly(string include, string path, bool expected)
    {
        var m = new GlobMatcher([include], []);
        Assert.Equal(expected, m.Matches(path));
    }

    [Fact]
    public void QuestionMarkGlob_MatchesSingleCharacter()
    {
        var m = new GlobMatcher(["file?.txt"], []);
        Assert.True(m.Matches(@"C:\dir\file1.txt"));
        Assert.True(m.Matches(@"C:\dir\fileA.txt"));
        Assert.False(m.Matches(@"C:\dir\file12.txt"));
    }

    [Fact]
    public void DoubleStarAlone_MatchesAnything()
    {
        var m = new GlobMatcher(["**/*.log"], []);
        Assert.True(m.Matches(@"C:\dir\app.log"));
        Assert.True(m.Matches(@"C:\deep\nested\dir\error.log"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Regex Mode
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RegexMode_Include_MatchesRegex()
    {
        var m = new GlobMatcher([@"\.cs$"], [], FilterPatternMode.Regex, FilterPatternMode.GlobPath);
        Assert.True(m.Matches(@"C:\src\file.cs"));
        Assert.False(m.Matches(@"C:\src\file.txt"));
    }

    [Fact]
    public void RegexMode_Exclude_MatchesRegex()
    {
        var m = new GlobMatcher([], [@"test|spec"], FilterPatternMode.GlobPath, FilterPatternMode.Regex);
        Assert.False(m.Matches(@"C:\src\test\file.cs"));
        Assert.False(m.Matches(@"C:\src\spec\file.cs"));
        Assert.True(m.Matches(@"C:\src\main\file.cs"));
    }

    [Fact]
    public void RegexMode_InvalidRegex_TreatedAsInvalid()
    {
        // Invalid regex should not crash, just not match
        var m = new GlobMatcher([@"[invalid"], [], FilterPatternMode.Regex, FilterPatternMode.GlobPath);
        Assert.False(m.Matches(@"C:\anything.cs"));
    }

    [Fact]
    public void RegexMode_CaseInsensitive()
    {
        var m = new GlobMatcher([@"DEBUG"], [], FilterPatternMode.Regex, FilterPatternMode.GlobPath);
        Assert.True(m.Matches(@"C:\project\debug\file.cs"));
        Assert.True(m.Matches(@"C:\project\Debug\file.cs"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Exclude takes priority
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Exclude_TakesPriorityOverInclude()
    {
        var m = new GlobMatcher(["*.cs"], ["*test*"]);
        Assert.True(m.Matches(@"C:\src\file.cs"));
        Assert.False(m.Matches(@"C:\src\test_file.cs"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Whitespace / empty patterns
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WhitespaceOnlyPatterns_AreIgnored()
    {
        var m = new GlobMatcher(["  ", "", "\t"], []);
        Assert.True(m.Matches(@"C:\anything.txt"));
    }

    [Fact]
    public void EmptyRegexPattern_TreatedAsMatchAll()
    {
        // Empty string is skipped by Compile (IsNullOrWhiteSpace check), so _includes is empty → match all
        var m = new GlobMatcher([""], [], FilterPatternMode.Regex, FilterPatternMode.GlobPath);
        Assert.True(m.Matches(@"C:\file.cs"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Backslash normalization
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void BackslashPaths_NormalizedToForwardSlash()
    {
        var m = new GlobMatcher(["src/**/*.cs"], []);
        Assert.True(m.Matches(@"C:\project\src\nested\file.cs"));
    }
}
