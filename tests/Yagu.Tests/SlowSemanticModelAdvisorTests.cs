using System;
using System.Collections.Generic;
using System.Linq;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Pure-logic tests for <see cref="SlowSemanticModelAdvisor"/>: identifying the running model among the
/// available options and selecting the smaller/faster alternatives offered by the slow-interpretation
/// prompt. No Foundry/WinUI dependencies, so these run headless.
/// </summary>
public sealed class SlowSemanticModelAdvisorTests
{
    private static SemanticModelOption Opt(
        string alias, long? sizeBytes, string? id = null, bool recommended = false,
        bool belowRecommended = false, bool cached = false) => new()
        {
            Alias = alias,
            DisplayName = alias,
            Id = id ?? $"{alias}-generic-cpu:1",
            SizeBytes = sizeBytes,
            IsRecommended = recommended,
            IsBelowRecommended = belowRecommended,
            IsCached = cached,
            DeviceLabel = "CPU",
        };

    private const long Mb = 1024L * 1024L;

    [Fact]
    public void SelectFasterOptions_ReturnsSmallerModels_SmallestFirst()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("big", 4000 * Mb, recommended: true),
            Opt("medium", 2000 * Mb, belowRecommended: true),
            Opt("small", 800 * Mb, belowRecommended: true),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: "big-generic-cpu:1", currentAlias: "");

        Assert.Equal(new[] { "small", "medium" }, faster.Select(o => o.Alias).ToArray());
    }

    [Fact]
    public void SelectFasterOptions_ExcludesCurrentAndLargerModels()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("huge", 8000 * Mb),
            Opt("current", 2000 * Mb, id: "current-generic-cpu:2"),
            Opt("tiny", 500 * Mb),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: "current-generic-cpu:2", currentAlias: "");

        Assert.Equal(new[] { "tiny" }, faster.Select(o => o.Alias).ToArray());
    }

    [Fact]
    public void SelectFasterOptions_IdentifiesCurrentByAlias()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("current", 2000 * Mb),
            Opt("smaller", 700 * Mb),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: "current", currentAlias: "");

        Assert.Single(faster);
        Assert.Equal("smaller", faster[0].Alias);
    }

    [Fact]
    public void SelectFasterOptions_IdentifiesCurrentByOverrideAlias_WhenKeyMissing()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("pinned", 3000 * Mb),
            Opt("smaller", 900 * Mb),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: null, currentAlias: "pinned");

        Assert.Single(faster);
        Assert.Equal("smaller", faster[0].Alias);
    }

    [Fact]
    public void SelectFasterOptions_FallsBackToRecommended_WhenAutomatic()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("recommended", 2500 * Mb, recommended: true),
            Opt("smaller", 600 * Mb, belowRecommended: true),
        };

        // No override (automatic) and no resolved key yet -> the recommended pick is what runs.
        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: null, currentAlias: "");

        Assert.Single(faster);
        Assert.Equal("smaller", faster[0].Alias);
    }

    [Fact]
    public void SelectFasterOptions_ReturnsEmpty_WhenCurrentSizeUnknown()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("current", sizeBytes: null, recommended: true),
            Opt("smaller", 600 * Mb),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: "current", currentAlias: "");

        Assert.Empty(faster);
    }

    [Fact]
    public void SelectFasterOptions_ReturnsEmpty_WhenCurrentNotFound()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("a", 1000 * Mb),
            Opt("b", 500 * Mb),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: "does-not-exist", currentAlias: "also-missing");

        Assert.Empty(faster);
    }

    [Fact]
    public void SelectFasterOptions_ExcludesOptionsWithUnknownSize()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("current", 2000 * Mb, recommended: true),
            Opt("unknownSize", sizeBytes: null),
            Opt("smaller", 700 * Mb),
        };

        var faster = SlowSemanticModelAdvisor.SelectFasterOptions(options, currentModelKey: "current", currentAlias: "");

        Assert.Equal(new[] { "smaller" }, faster.Select(o => o.Alias).ToArray());
    }

    [Fact]
    public void SelectFasterOptions_ReturnsEmpty_ForEmptyInput()
    {
        Assert.Empty(SlowSemanticModelAdvisor.SelectFasterOptions(Array.Empty<SemanticModelOption>(), "x", "y"));
    }

    [Fact]
    public void FindCurrentOption_PrefersVariantIdOverAlias()
    {
        var options = new List<SemanticModelOption>
        {
            Opt("phi", 2000 * Mb, id: "phi-generic-cpu:1"),
            Opt("phi", 2000 * Mb, id: "phi-generic-gpu:1"),
        };

        var found = SlowSemanticModelAdvisor.FindCurrentOption(options, currentModelKey: "phi-generic-gpu:1", currentAlias: "");

        Assert.NotNull(found);
        Assert.Equal("phi-generic-gpu:1", found!.Id);
    }
}
