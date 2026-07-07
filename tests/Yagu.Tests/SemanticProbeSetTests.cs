using System.Linq;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>Sanity checks for the curated <see cref="SemanticProbeSet"/>: it must stay small, contain a
/// mix of simple and complex probes, and give every probe at least one concrete expectation to score
/// against (an expectation-less probe would always pass and weaken qualification).</summary>
public sealed class SemanticProbeSetTests
{
    [Fact]
    public void Default_IsSmallEnoughForAFastSweep()
    {
        Assert.InRange(SemanticProbeSet.Default.Count, 4, 16);
    }

    [Fact]
    public void Default_ContainsBothSimpleAndComplexProbes()
    {
        Assert.Contains(SemanticProbeSet.Default, p => p.Complexity == SemanticProbeComplexity.Simple);
        Assert.Contains(SemanticProbeSet.Default, p => p.Complexity == SemanticProbeComplexity.Complex);
    }

    [Fact]
    public void EveryProbe_HasAtLeastOneExpectation()
    {
        foreach (var p in SemanticProbeSet.Default)
        {
            bool hasExpectation =
                p.ExpectedSearchMode is not null ||
                p.ExpectedIncludeGlob is not null ||
                p.ExpectedPatternContains is not null ||
                p.ExpectedHasDateFilter is not null ||
                p.ExpectedSearchHidden is not null ||
                p.ExpectedExcludeGlobContains is not null ||
                p.ExpectedSearchInsideArchives is not null ||
                p.ExpectedSearchImageText is not null ||
                p.ExpectedUseRegex is not null ||
                p.ExpectedExactMatch is not null ||
                p.ExpectedMultiline is not null ||
                p.ExpectedObeyGitignore is not null ||
                p.ExpectedHasCreatedBefore is not null;
            Assert.True(hasExpectation, $"Probe '{p.Query}' has no expectations to score against.");
        }
    }

    [Fact]
    public void EveryProbe_HasANonEmptyQuery()
    {
        Assert.All(SemanticProbeSet.Default, p => Assert.False(string.IsNullOrWhiteSpace(p.Query)));
    }

    [Fact]
    public void Queries_AreUnique()
    {
        var distinct = SemanticProbeSet.Default.Select(p => p.Query).Distinct().Count();
        Assert.Equal(SemanticProbeSet.Default.Count, distinct);
    }

    [Fact]
    public void Default_ExercisesTheSettingsMutationSurface()
    {
        var set = SemanticProbeSet.Default;
        Assert.Contains(set, p => p.ExpectedSearchHidden is not null);
        Assert.Contains(set, p => p.ExpectedExcludeGlobContains is not null);
        Assert.Contains(set, p => p.ExpectedSearchInsideArchives is not null);
        Assert.Contains(set, p => p.ExpectedSearchImageText is not null);
        Assert.Contains(set, p => p.ExpectedUseRegex is not null);
        Assert.Contains(set, p => p.ExpectedExactMatch is not null);
        Assert.Contains(set, p => p.ExpectedMultiline is not null);
        Assert.Contains(set, p => p.ExpectedObeyGitignore is not null);
        Assert.Contains(set, p => p.ExpectedHasCreatedBefore is not null);
    }
}
