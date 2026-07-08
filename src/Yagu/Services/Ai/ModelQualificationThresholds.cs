namespace Yagu.Services.Ai;

/// <summary>
/// User-chosen time limits for a model-qualification sweep: how long to wait for a candidate model to
/// load, and how long a simple / complex query may take before the candidate is abandoned as too slow.
/// Collected in the qualification dialog (as whole seconds) before the sweep starts, so the user — not a
/// hard-coded constant — decides how patient the check is on their hardware.
/// </summary>
public sealed record ModelQualificationThresholds
{
    /// <summary>Default max time to wait for a candidate model to load (ms).</summary>
    public const int DefaultModelLoadMaxMs = 20_000;

    /// <summary>Default max time a simple (single-constraint) query may take (ms). Sized so a large
    /// RECOMMENDED model (e.g. phi-4 14B on a GPU whose VRAM the model nearly fills) still passes: such
    /// models emit a capped-but-verbose response and their per-token speed varies, so a small-model
    /// gate would wrongly disqualify the very model the sweep recommends. The user can tighten it in the
    /// dialog.</summary>
    public const int DefaultSimpleQueryMaxMs = 20_000;

    /// <summary>Default max time a complex (multi-constraint) query may take (ms). See
    /// <see cref="DefaultSimpleQueryMaxMs"/> — a large recommended model's hardest probes can run
    /// noticeably longer, so this leaves headroom rather than abandoning an accurate model as "too slow."</summary>
    public const int DefaultComplexQueryMaxMs = 25_000;

    /// <summary>Grace band (ms) added to a per-query limit: a probe that answers CORRECTLY within this
    /// many ms OVER its limit still PASSES — but is flagged with a "slow" warning rather than failing the
    /// candidate — so a large recommended model's occasional latency spike doesn't disqualify it.</summary>
    public const int DefaultLatencyToleranceMs = 5_000;

    /// <summary>Max time to wait for a candidate model to load before abandoning it (ms).</summary>
    public int ModelLoadMaxMs { get; init; } = DefaultModelLoadMaxMs;

    /// <summary>Max time a simple (single-constraint) query may take (ms).</summary>
    public int SimpleQueryMaxMs { get; init; } = DefaultSimpleQueryMaxMs;

    /// <summary>Max time a complex (multi-constraint) query may take (ms).</summary>
    public int ComplexQueryMaxMs { get; init; } = DefaultComplexQueryMaxMs;

    /// <summary>Grace band (ms) over a per-query limit within which a correct-but-slow probe still passes
    /// (with a warning) instead of failing the candidate.</summary>
    public int LatencyToleranceMs { get; init; } = DefaultLatencyToleranceMs;

    /// <summary>The default limits (20s to load, 20s simple, 25s complex, +5s slow-pass tolerance).</summary>
    public static ModelQualificationThresholds Default { get; } = new();

    /// <summary>The per-query ceiling that applies to a probe of the given complexity.</summary>
    public int QueryMaxMs(SemanticProbeComplexity complexity) =>
        complexity == SemanticProbeComplexity.Complex ? ComplexQueryMaxMs : SimpleQueryMaxMs;
}
