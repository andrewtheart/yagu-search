using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="GpuInferencePolicy"/> — the pure decision that refuses to run a local model
/// on an integrated GPU (no/negligible dedicated VRAM), which crashes with a native 0xC0000409 on the ONNX
/// Runtime WebGPU/DirectML path regardless of model size, while still using a discrete GPU as before.
/// </summary>
public sealed class GpuInferencePolicyTests
{
    private const long Gb = 1024L * 1024L * 1024L;

    [Theory]
    [InlineData(2L * 1024 * 1024 * 1024)]   // 2 GB discrete card
    [InlineData(8L * 1024 * 1024 * 1024)]   // 8 GB discrete card
    [InlineData(24L * 1024 * 1024 * 1024)]  // 24 GB discrete card
    public void CanUseGpuForInference_DiscreteGpuWithDedicatedVram_ReturnsTrue(long vramBytes)
        => Assert.True(GpuInferencePolicy.CanUseGpuForInference(hasGpu: true, dedicatedVramBytes: vramBytes));

    [Fact]
    public void CanUseGpuForInference_ExactlyAtThreshold_ReturnsTrue()
        => Assert.True(GpuInferencePolicy.CanUseGpuForInference(hasGpu: true, dedicatedVramBytes: GpuInferencePolicy.MinDedicatedVramBytesForGpu));

    [Theory]
    [InlineData(0L)]                          // integrated GPU: no dedicated VRAM (the repro'd crash case)
    [InlineData(-1L)]                         // defensive: unknown / negative
    [InlineData(512L * 1024 * 1024)]          // tiny BIOS carve-out (still an iGPU)
    [InlineData((1024L * 1024 * 1024) - 1)]   // just under the 1 GB bar
    public void CanUseGpuForInference_IntegratedOrTinyVram_ReturnsFalse(long vramBytes)
        => Assert.False(GpuInferencePolicy.CanUseGpuForInference(hasGpu: true, dedicatedVramBytes: vramBytes));

    [Theory]
    [InlineData(0L)]
    [InlineData(8L * 1024 * 1024 * 1024)]     // even if VRAM looks ample, no GPU means no GPU inference
    public void CanUseGpuForInference_NoGpu_AlwaysFalse(long vramBytes)
        => Assert.False(GpuInferencePolicy.CanUseGpuForInference(hasGpu: false, dedicatedVramBytes: vramBytes));

    [Fact]
    public void MinDedicatedVramBytesForGpu_IsOneGigabyte()
        => Assert.Equal(Gb, GpuInferencePolicy.MinDedicatedVramBytesForGpu);
}
