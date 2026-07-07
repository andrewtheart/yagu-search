using System;
using System.Collections.Generic;
using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>Unit tests for <see cref="SemanticProbeScorer"/> — the pure per-probe pass/fail logic.</summary>
public sealed class SemanticProbeScorerTests
{
    private static ResolvedSearchPlan Plan(
        SearchMode? mode = null,
        string? pattern = null,
        IReadOnlyList<string>? include = null,
        DateTimeOffset? modifiedAfter = null,
        DateTimeOffset? createdBefore = null) =>
        new()
        {
            SearchMode = mode,
            Pattern = pattern,
            IncludeGlobs = include,
            ModifiedAfterDate = modifiedAfter,
            CreatedBeforeDate = createdBefore,
        };

    [Fact]
    public void Passes_NullPlan_Fails()
    {
        var probe = new SemanticProbe { Query = "x", Complexity = SemanticProbeComplexity.Simple };
        Assert.False(SemanticProbeScorer.Passes(probe, null));
    }

    [Fact]
    public void Passes_NoExpectations_PassesForAnyPlan()
    {
        var probe = new SemanticProbe { Query = "x", Complexity = SemanticProbeComplexity.Simple };
        Assert.True(SemanticProbeScorer.Passes(probe, Plan()));
    }

    [Fact]
    public void Passes_ExtensionQuery_MatchesIncludeGlobAndMode()
    {
        var probe = new SemanticProbe
        {
            Query = "all png files",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedSearchMode = SearchMode.FileNames,
            ExpectedIncludeGlob = "*.png",
        };
        Assert.True(SemanticProbeScorer.Passes(probe, Plan(mode: SearchMode.FileNames, include: ["*.png"])));
    }

    [Fact]
    public void Passes_IncludeGlob_IsCaseInsensitive()
    {
        var probe = new SemanticProbe
        {
            Query = "png",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedIncludeGlob = "*.png",
        };
        Assert.True(SemanticProbeScorer.Passes(probe, Plan(include: ["*.PNG"])));
    }

    [Fact]
    public void Passes_WrongSearchMode_Fails()
    {
        var probe = new SemanticProbe
        {
            Query = "all png files",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedSearchMode = SearchMode.FileNames,
            ExpectedIncludeGlob = "*.png",
        };
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(mode: SearchMode.Content, include: ["*.png"])));
    }

    [Fact]
    public void Passes_MissingIncludeGlob_Fails()
    {
        var probe = new SemanticProbe
        {
            Query = "all png files",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedIncludeGlob = "*.png",
        };
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(include: ["*.jpg"])));
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(include: null)));
    }

    [Fact]
    public void Passes_ContentQuery_MatchesPatternSubstring_CaseInsensitive()
    {
        var probe = new SemanticProbe
        {
            Query = "files containing error",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedSearchMode = SearchMode.Content,
            ExpectedPatternContains = "error",
        };
        Assert.True(SemanticProbeScorer.Passes(probe, Plan(mode: SearchMode.Content, pattern: "ERROR")));
    }

    [Fact]
    public void Passes_ContentQuery_WrongPattern_Fails()
    {
        var probe = new SemanticProbe
        {
            Query = "files containing error",
            Complexity = SemanticProbeComplexity.Simple,
            ExpectedPatternContains = "error",
        };
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(pattern: "warning")));
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(pattern: null)));
    }

    [Fact]
    public void Passes_DateQuery_RequiresAnyDateFilter()
    {
        var probe = new SemanticProbe
        {
            Query = "files modified in the last 7 days",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedHasDateFilter = true,
        };
        Assert.True(SemanticProbeScorer.Passes(probe, Plan(modifiedAfter: DateTimeOffset.Now.AddDays(-7))));
        Assert.True(SemanticProbeScorer.Passes(probe, Plan(createdBefore: DateTimeOffset.Now)));
        Assert.False(SemanticProbeScorer.Passes(probe, Plan()));
    }

    [Fact]
    public void Passes_AllExpectationsAreAnded()
    {
        var probe = new SemanticProbe
        {
            Query = "c# files containing async",
            Complexity = SemanticProbeComplexity.Complex,
            ExpectedIncludeGlob = "*.cs",
            ExpectedPatternContains = "async",
        };
        // Both satisfied → pass.
        Assert.True(SemanticProbeScorer.Passes(probe, Plan(pattern: "async", include: ["*.cs"])));
        // Only one satisfied → fail.
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(pattern: "async", include: ["*.py"])));
        Assert.False(SemanticProbeScorer.Passes(probe, Plan(pattern: "await", include: ["*.cs"])));
    }

    [Fact]
    public void Accuracy_CountsPassingProbes()
    {
        var probes = new List<SemanticProbe>
        {
            new() { Query = "a", Complexity = SemanticProbeComplexity.Simple, ExpectedIncludeGlob = "*.png" },
            new() { Query = "b", Complexity = SemanticProbeComplexity.Simple, ExpectedPatternContains = "error" },
        };
        var plans = new List<ResolvedSearchPlan?>
        {
            Plan(include: ["*.png"]),   // pass
            Plan(pattern: "warning"),   // fail
        };
        Assert.Equal(0.5, SemanticProbeScorer.Accuracy(probes, plans));
    }

    [Fact]
    public void Accuracy_EmptySet_IsZero()
    {
        Assert.Equal(0.0, SemanticProbeScorer.Accuracy([], []));
    }

    [Fact]
    public void Accuracy_MismatchedLengths_Throws()
    {
        var probes = new List<SemanticProbe> { new() { Query = "a", Complexity = SemanticProbeComplexity.Simple } };
        Assert.Throws<ArgumentException>(() => SemanticProbeScorer.Accuracy(probes, []));
    }
}
