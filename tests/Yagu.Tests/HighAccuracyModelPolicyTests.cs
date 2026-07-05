using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="HighAccuracyModelPolicy"/> — the pure VRAM-gated decision that upgrades
/// AUTO model selection to a larger, more-accurate model (e.g. phi-4 14B) on a strong GPU while keeping
/// the small default on weak / low-VRAM / CPU-only machines.
/// </summary>
public sealed class HighAccuracyModelPolicyTests
{
    [Theory]
    [InlineData(16384, "phi-4")]   // exactly at the bar
    [InlineData(24576, "phi-4")]   // 24 GB card
    [InlineData(32768, "phi-4")]   // 32 GB card
    public void UpgradeAliasFor_AmpleVram_ReturnsLargerModel(int vramMb, string expected)
        => Assert.Equal(expected, HighAccuracyModelPolicy.UpgradeAliasFor(vramMb));

    [Theory]
    [InlineData(0)]        // unknown / no GPU
    [InlineData(-1)]       // defensive guard
    [InlineData(8192)]     // 8 GB card -> keep the small default
    [InlineData(12288)]    // 12 GB -> below the phi-4 bar
    [InlineData(16383)]    // just under the bar
    public void UpgradeAliasFor_InsufficientOrUnknownVram_ReturnsNull(int vramMb)
        => Assert.Null(HighAccuracyModelPolicy.UpgradeAliasFor(vramMb));

    [Fact]
    public void Models_AreOrderedBestFirst_WithPositiveVramBars()
    {
        Assert.NotEmpty(HighAccuracyModelPolicy.Models);
        Assert.Equal("phi-4", HighAccuracyModelPolicy.Models[0].Alias);
        Assert.True(HighAccuracyModelPolicy.Models[0].MinVramMb >= 16384);
        // Every bar must be positive so a 0 (unknown) budget can never match.
        Assert.All(HighAccuracyModelPolicy.Models, m => Assert.True(m.MinVramMb > 0));
    }
}
