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
    public const int DefaultModelLoadMaxMs = 60_000;

    /// <summary>Default max time a simple (single-constraint) query may take (ms).</summary>
    public const int DefaultSimpleQueryMaxMs = 10_000;

    /// <summary>Default max time a complex (multi-constraint) query may take (ms).</summary>
    public const int DefaultComplexQueryMaxMs = 15_000;

    /// <summary>Max time to wait for a candidate model to load before abandoning it (ms).</summary>
    public int ModelLoadMaxMs { get; init; } = DefaultModelLoadMaxMs;

    /// <summary>Max time a simple (single-constraint) query may take (ms).</summary>
    public int SimpleQueryMaxMs { get; init; } = DefaultSimpleQueryMaxMs;

    /// <summary>Max time a complex (multi-constraint) query may take (ms).</summary>
    public int ComplexQueryMaxMs { get; init; } = DefaultComplexQueryMaxMs;

    /// <summary>The default limits (1 minute to load, 10s simple, 15s complex).</summary>
    public static ModelQualificationThresholds Default { get; } = new();

    /// <summary>The per-query ceiling that applies to a probe of the given complexity.</summary>
    public int QueryMaxMs(SemanticProbeComplexity complexity) =>
        complexity == SemanticProbeComplexity.Complex ? ComplexQueryMaxMs : SimpleQueryMaxMs;
}
