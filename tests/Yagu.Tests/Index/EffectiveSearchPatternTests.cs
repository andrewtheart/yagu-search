using Yagu.Models;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests that <see cref="EffectiveSearchPattern.Resolve"/> reproduces the exact reduction order used
/// by <see cref="Yagu.Services.SearchService"/> (plan §4): explicit regex passthrough → whole-word
/// (exact) → multi-term alternation → single-term multiline promotion → plain literal.
/// </summary>
public sealed class EffectiveSearchPatternTests
{
    private static SearchOptions Options(
        string query,
        bool useRegex = false,
        bool exactMatch = false,
        bool caseSensitive = true,
        bool multiline = false,
        bool dotAll = false)
        => new()
        {
            Directory = @"C:\x",
            Query = query,
            UseRegex = useRegex,
            ExactMatch = exactMatch,
            CaseSensitive = caseSensitive,
            Multiline = multiline,
            MultilineDotAll = dotAll,
        };

    [Fact]
    public void Resolve_ExplicitRegex_PassesThrough()
    {
        var p = EffectiveSearchPattern.Resolve(Options("fo+bar", useRegex: true));
        Assert.True(p.IsRegex);
        Assert.Equal("fo+bar", p.Pattern);
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void Resolve_ExactMatch_WrapsWholeWordRegex()
    {
        var p = EffectiveSearchPattern.Resolve(Options("async", exactMatch: true));
        Assert.True(p.IsRegex);
        Assert.Equal(@"\basync\b", p.Pattern);
    }

    [Fact]
    public void Resolve_MultiTerm_BuildsAlternationRegex()
    {
        var p = EffectiveSearchPattern.Resolve(Options("foo bar", exactMatch: false));
        Assert.True(p.IsRegex);
        // Terms are ordered longest-first; both are 3 chars so order is stable by input.
        Assert.Contains("foo", p.Pattern);
        Assert.Contains("bar", p.Pattern);
        Assert.Contains("|", p.Pattern);
    }

    [Fact]
    public void Resolve_SingleTermMultiline_PromotesToEscapedRegex()
    {
        var p = EffectiveSearchPattern.Resolve(Options("a.b", exactMatch: false, multiline: true));
        Assert.True(p.IsRegex);
        Assert.Equal(@"a\.b", p.Pattern);
        Assert.True(p.Multiline);
    }

    [Fact]
    public void Resolve_SingleLiteral_StaysLiteral()
    {
        var p = EffectiveSearchPattern.Resolve(Options("hello", exactMatch: false));
        Assert.False(p.IsRegex);
        Assert.Equal("hello", p.Pattern);
    }

    [Fact]
    public void Resolve_EmptyQuery_IsEmpty()
    {
        var p = EffectiveSearchPattern.Resolve(Options("   ", exactMatch: false));
        Assert.True(p.IsEmpty);
    }

    [Fact]
    public void Resolve_CarriesCaseAndMultilineFlags()
    {
        var p = EffectiveSearchPattern.Resolve(Options("re", useRegex: true, caseSensitive: false, multiline: true, dotAll: true));
        Assert.False(p.CaseSensitive);
        Assert.True(p.Multiline);
        Assert.True(p.DotAll);
    }

    [Fact]
    public void Constructor_NullPattern_BecomesEmpty()
    {
        var p = new EffectiveSearchPattern(null!, isRegex: false, caseSensitive: true, multiline: false, dotAll: false);
        Assert.True(p.IsEmpty);
        Assert.Equal(string.Empty, p.Pattern);
    }

    [Fact]
    public void Resolve_NullQuery_TreatedAsEmpty_AcrossPaths()
    {
        // A null query must be coalesced to empty on every reduction path (regex passthrough and the
        // literal-term parse), never throwing.
        Assert.True(EffectiveSearchPattern.Resolve(Options(null!, useRegex: true)).IsEmpty);
        Assert.True(EffectiveSearchPattern.Resolve(Options(null!, exactMatch: false)).IsEmpty);
        Assert.True(EffectiveSearchPattern.Resolve(Options(null!, exactMatch: true)).IsEmpty);
    }
}
