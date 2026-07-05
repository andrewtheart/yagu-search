using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Tests for <see cref="ModelMemoryBudget"/> — the pure scaled-headroom math extracted from
/// <see cref="FoundryModelSelector"/> (which pulls in Foundry types and cannot be unit-tested). The
/// guard keeps a too-large model from being auto-selected on a CPU-only machine (it would load then OOM),
/// while letting a tiny model load on a low-RAM box that a flat reserve would over-reject.
/// </summary>
public class ModelMemoryBudgetTests
{
    [Theory]
    [InlineData(0, 512)]      // zero weights → floor only (runtime + OS slack)
    [InlineData(-100, 512)]   // negative clamped to the floor, never below
    [InlineData(2048, 1536)]  // ANCHOR: a ~2 GB model still reserves the previously-tuned ~1.5 GB
    [InlineData(582, 803)]    // qwen3-0.6b   (0.58 GB) → 512 + 291
    [InlineData(819, 921)]    // qwen2.5-0.5b (0.80 GB) → 512 + 409
    [InlineData(4800, 2912)]  // phi-4-mini   (4.80 GB) → 512 + 2400 (bigger model reserves MORE)
    public void EstimateHeadroomMb_ScalesWithWeights(int weightsMb, int expected)
        => Assert.Equal(expected, ModelMemoryBudget.EstimateHeadroomMb(weightsMb));

    [Fact]
    public void Fits_UnknownSize_AlwaysFits()
        => Assert.True(ModelMemoryBudget.Fits(null, availableMemoryMb: 100));

    [Theory]
    [InlineData(582, 1611, true)]   // qwen3-0.6b (needs 1385) fits the ~1.6 GB sandbox
    [InlineData(819, 1611, false)]  // qwen2.5-0.5b (needs 1740) does NOT fit ~1.6 GB
    [InlineData(4800, 4096, false)] // phi-4-mini stays correctly rejected on a 4 GB box
    [InlineData(2048, 3583, false)] // ~2 GB model needs 3584; 3583 free is one short
    [InlineData(2048, 3584, true)]  // exactly enough (weights 2048 + headroom 1536) → fits
    public void Fits_ChecksWeightsPlusScaledHeadroom(int weightsMb, int availableMb, bool expected)
        => Assert.Equal(expected, ModelMemoryBudget.Fits(weightsMb, availableMb));
}
