using System;
using System.Linq;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="FoundryModelUpdateChecker"/> — the pure logic that detects newly-available,
/// updated, or variant Foundry Local models against a persisted baseline.
/// </summary>
public sealed class FoundryModelUpdateCheckerTests
{
    private static FoundryModelDescriptor M(string id, string alias, string? device = "GPU", long? size = 1_000_000_000)
        => new(id, alias, device, size);

    // ── ShouldCheck (throttle) ──

    [Fact]
    public void ShouldCheck_NeverChecked_ReturnsTrue()
        => Assert.True(FoundryModelUpdateChecker.ShouldCheck(null, DateTimeOffset.UtcNow, TimeSpan.FromHours(24)));

    [Fact]
    public void ShouldCheck_WithinInterval_ReturnsFalse()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(FoundryModelUpdateChecker.ShouldCheck(now.AddHours(-1), now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void ShouldCheck_PastInterval_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(FoundryModelUpdateChecker.ShouldCheck(now.AddHours(-25), now, TimeSpan.FromHours(24)));
    }

    // ── Detect: first-run baseline ──

    [Fact]
    public void Detect_FirstRun_SeedsBaselineWithoutAlerting()
    {
        var current = new[] { M("phi-4-mini-cuda-gpu:1", "phi-4-mini"), M("qwen-cpu:1", "qwen2.5") };

        var result = FoundryModelUpdateChecker.Detect(Array.Empty<string>(), current, hasBaseline: false);

        Assert.True(result.BaselineSeeded);
        Assert.Empty(result.Changes);
        Assert.Equal(new[] { "phi-4-mini-cuda-gpu:1", "qwen-cpu:1" }, result.CurrentIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Detect_NoChanges_ReturnsEmpty()
    {
        var known = new[] { "a:1", "b:1" };
        var current = new[] { M("a:1", "alpha"), M("b:1", "beta") };

        var result = FoundryModelUpdateChecker.Detect(known, current, hasBaseline: true);

        Assert.False(result.BaselineSeeded);
        Assert.Empty(result.Changes);
        Assert.Equal(new[] { "a:1", "b:1" }, result.CurrentIds.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ── Detect: new vs updated classification ──

    [Fact]
    public void Detect_BrandNewAlias_ClassifiedAsNew()
    {
        var known = new[] { "a:1" };
        var current = new[] { M("a:1", "alpha"), M("z:1", "zeta") };

        var change = Assert.Single(FoundryModelUpdateChecker.Detect(known, current, hasBaseline: true).Changes);

        Assert.Equal("z:1", change.Id);
        Assert.Equal("zeta", change.Alias);
        Assert.Equal(FoundryModelChangeKind.New, change.Kind);
    }

    [Fact]
    public void Detect_NewVariantOfKnownAlias_ClassifiedAsUpdated()
    {
        // alpha already has a known variant (a-gpu:1) still present; a-npu:1 is a new variant of it.
        var known = new[] { "a-gpu:1" };
        var current = new[] { M("a-gpu:1", "alpha", "GPU"), M("a-npu:1", "alpha", "NPU") };

        var change = Assert.Single(FoundryModelUpdateChecker.Detect(known, current, hasBaseline: true).Changes);

        Assert.Equal("a-npu:1", change.Id);
        Assert.Equal(FoundryModelChangeKind.Updated, change.Kind);
    }

    [Fact]
    public void Detect_IgnoresEmptyIds_AndDedupesDuplicates()
    {
        var known = new[] { "a:1" };
        var current = new[]
        {
            M("a:1", "alpha"),
            M("", "blank"),
            M("b:1", "beta"),
            M("b:1", "beta"),
        };

        var result = FoundryModelUpdateChecker.Detect(known, current, hasBaseline: true);

        var change = Assert.Single(result.Changes);
        Assert.Equal("b:1", change.Id);
        Assert.DoesNotContain("", result.CurrentIds);
        Assert.Single(result.CurrentIds, id => id == "b:1");
    }

    [Fact]
    public void Detect_CarriesDeviceAndSizeOntoChange()
    {
        var known = new[] { "a:1" };
        var current = new[] { M("a:1", "alpha"), M("b:1", "beta", "NPU", 2_147_483_648) };

        var change = Assert.Single(FoundryModelUpdateChecker.Detect(known, current, hasBaseline: true).Changes);

        Assert.Equal("NPU", change.DeviceLabel);
        Assert.Equal(2_147_483_648, change.SizeBytes);
    }

    // ── argument contract ──

    [Fact]
    public void DefaultCheckInterval_IsTwentyFourHours()
        => Assert.Equal(TimeSpan.FromHours(24), FoundryModelUpdateChecker.DefaultCheckInterval);

    [Fact]
    public void Detect_NullKnownIds_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            FoundryModelUpdateChecker.Detect(null!, Array.Empty<FoundryModelDescriptor>(), hasBaseline: true));

    [Fact]
    public void Detect_NullCurrentModels_Throws()
        => Assert.Throws<ArgumentNullException>(() =>
            FoundryModelUpdateChecker.Detect(Array.Empty<string>(), null!, hasBaseline: true));
}
