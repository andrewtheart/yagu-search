namespace Yagu.Services.Ai;

/// <summary>
/// Pure policy deciding whether a detected GPU should be used to RUN a local model. Extracted from
/// <see cref="FoundryLocalSemanticQueryTranslator"/> (which pulls in Foundry types and cannot be
/// unit-tested) so the integrated-GPU exclusion is directly testable.
///
/// <para>
/// An INTEGRATED GPU (Intel UHD/Iris, AMD Radeon iGPU) has no dedicated video memory — it shares system
/// RAM — and reports zero dedicated VRAM (its registry <c>HardwareInformation.qwMemorySize</c> is absent,
/// so <see cref="GpuNpuCapabilityDetector.GetMaxDedicatedGpuMemoryBytes"/> returns 0). Running a model on
/// it through ONNX Runtime's WebGPU/DirectML execution provider crashes with a native <c>0xC0000409</c>
/// (STATUS_STACK_BUFFER_OVERRUN) fail-fast on the FIRST inference — a fault a managed try/catch cannot
/// catch — <b>regardless of model size</b>. This was confirmed empirically on an Intel UHD (Raptor Lake)
/// iGPU where BOTH a 3.7 GB <c>phi-4-mini generic-gpu</c> build AND a ~1 GB <c>qwen2.5-1.5b generic-gpu</c>
/// build faulted identically on the first inference (downloaded and loaded fine, then aborted). It is the
/// iGPU/WebGPU path that is broken, not the model.
/// </para>
///
/// <para>
/// A discrete GPU always reports its real dedicated VRAM (≥ ~2 GB today), so the dedicated-VRAM amount
/// cleanly separates the two cases: below the threshold we refuse the GPU and fall back to the CPU build
/// (slower but stable, and no less accurate — the <c>generic-cpu</c> build is higher-precision than the
/// int4 <c>generic-gpu</c> build); at/above it the GPU is used as before. A real discrete GPU whose VRAM
/// is (rarely) misreported as 0 also falls back to CPU, trading some speed for guaranteed stability.
/// </para>
/// </summary>
internal static class GpuInferencePolicy
{
    /// <summary>
    /// Minimum DEDICATED video memory (bytes) a GPU must report before it is trusted to run a local model.
    /// Integrated GPUs report 0 (no dedicated VRAM) or a tiny BIOS carve-out; discrete GPUs report ≥ ~2 GB.
    /// 1 GB cleanly separates them while never excluding a real discrete card.
    /// </summary>
    public const long MinDedicatedVramBytesForGpu = 1024L * 1024L * 1024L; // 1 GB

    /// <summary>
    /// Whether the GPU may be used to run a local model. True only when a real GPU is present AND it reports
    /// at least <see cref="MinDedicatedVramBytesForGpu"/> of DEDICATED VRAM. A machine with no GPU, or with
    /// an integrated GPU that has no/negligible dedicated VRAM, returns false so model selection stays on the
    /// CPU (or NPU) build and never loads a GPU variant that would crash on the iGPU WebGPU/DirectML path.
    /// </summary>
    /// <param name="hasGpu">Whether the capability detector found a real GPU adapter (includes iGPUs).</param>
    /// <param name="dedicatedVramBytes">Largest dedicated VRAM reported for that GPU, in bytes (0 = unknown/none).</param>
    public static bool CanUseGpuForInference(bool hasGpu, long dedicatedVramBytes)
        => hasGpu && dedicatedVramBytes >= MinDedicatedVramBytesForGpu;
}
