namespace Yagu.Services.Ai;

/// <summary>
/// Pure, dependency-free memory-budget math for CPU-only model selection. Isolated from
/// <see cref="FoundryModelSelector"/> (which pulls in Foundry Local / WindowsAppSDK types and so
/// cannot be unit-tested) so the headroom/fit logic can be exercised directly.
/// </summary>
internal static class ModelMemoryBudget
{
    /// <summary>Floor (MB) reserved for the ONNX runtime + OS slack even for a tiny model.</summary>
    public const int MinHeadroomMb = 512;

    /// <summary>
    /// Estimates the RAM (MB) to reserve ON TOP of a model's on-disk weights for its KV cache, the
    /// ONNX runtime, and OS headroom. The KV cache — the dominant extra cost for the large ~8.5K-token
    /// system prompt — scales with the model's architecture (layers x hidden size), which tracks weight
    /// size, so a FLAT reserve badly over-rejects tiny models (a 0.5-0.6 GB model needs only a few
    /// hundred MB of KV cache, not the ~1.5 GB a ~4 GB model needs). We therefore scale the reserve with
    /// the weights: <see cref="MinHeadroomMb"/> floor plus half the weight size. This is anchored so a
    /// ~2 GB model still reserves the previously-tuned ~1.5 GB (512 + 2048/2 = 1536) — bigger models
    /// reserve MORE (safer), while a 0.5-0.6 GB model reserves ~0.8 GB and so becomes loadable on a
    /// ~2 GB machine instead of being over-rejected. Negative inputs are clamped to the floor.
    /// </summary>
    public static int EstimateHeadroomMb(int weightsMb)
        => MinHeadroomMb + (weightsMb > 0 ? weightsMb / 2 : 0);

    /// <summary>
    /// Whether a model's estimated footprint (weights + <see cref="EstimateHeadroomMb"/>) fits within
    /// <paramref name="availableMemoryMb"/>. A null <paramref name="weightsMb"/> (unknown size) always
    /// fits — the guard cannot judge it, so selection is not blocked on missing catalog metadata.
    /// </summary>
    public static bool Fits(int? weightsMb, int availableMemoryMb)
        => weightsMb is not int mb || mb + EstimateHeadroomMb(mb) <= availableMemoryMb;
}
