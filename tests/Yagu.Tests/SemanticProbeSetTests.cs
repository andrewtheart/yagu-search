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
        Assert.InRange(SemanticProbeSet.Default.Count, 4, 10);
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
                p.ExpectedHasDateFilter is not null;
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
}
