using System;

namespace Yagu.Services.Ai;

/// <summary>The pass/fail decision for a single candidate model, plus a short human-readable reason.</summary>
public readonly record struct QualificationVerdict(bool Passed, string Reason);

/// <summary>A single probe that ran slower than the user's per-query limit, disqualifying the candidate.
/// The limit is whichever ceiling (<see cref="ModelQualificationThresholds.SimpleQueryMaxMs"/> or
/// <see cref="ModelQualificationThresholds.ComplexQueryMaxMs"/>) applies to the probe's complexity.</summary>
public readonly record struct LatencyViolation(int LatencyMs, int LimitMs, SemanticProbeComplexity Complexity);

/// <summary>
/// Pure decision logic for whether a probed candidate model is good enough to be the
/// strongly-recommended on-device model. A model qualifies only when it did NOT crash, no single query
/// exceeded the user's per-query time limit, and its probe accuracy meets the minimum. The speed limits
/// are chosen by the user before the sweep (see <see cref="ModelQualificationThresholds"/>), so this
/// class only keeps the fixed accuracy bar. Kept free of Foundry/WinUI dependencies so it is fully
/// unit-testable (mirrors <see cref="GpuInferencePolicy"/>).
/// </summary>
public static class ModelQualificationPolicy
{
    /// <summary>Minimum fraction of probes a model must answer correctly (0..1). At 0.92 the model must
    /// be near-perfect: over the current probe set (~14 probes) it tolerates exactly one miss
    /// (13/14 ≈ 93% passes, 12/14 ≈ 86% fails), so only a model that handles essentially every probed
    /// mutation qualifies as the strong recommendation; anything short falls back to the best-effort pick.</summary>
    public const double MinAccuracy = 0.92;

    /// <summary>Evaluates a candidate from its aggregate probe outcome and the user-chosen limits.</summary>
    /// <param name="accuracy">Fraction of probes answered correctly (0..1).</param>
    /// <param name="crashed">True when the model crashed or failed to load in time during probing.</param>
    /// <param name="latencyViolation">The first probe that blew its per-query time limit, if any. When
    /// set, it is the true disqualifier (the sweep abandons the candidate after that probe, so accuracy
    /// is only partial), so its reason wins over the accuracy one.</param>
    public static QualificationVerdict Evaluate(double accuracy, bool crashed, LatencyViolation? latencyViolation)
    {
        if (crashed)
            return new QualificationVerdict(false, "the model crashed or failed to load in time");

        if (latencyViolation is { } violation)
        {
            string kind = violation.Complexity == SemanticProbeComplexity.Complex ? "complex" : "simple";
            return new QualificationVerdict(false,
                $"a {kind} query took {violation.LatencyMs / 1000.0:F1}s, over your {violation.LimitMs / 1000.0:F0}s limit");
        }

        if (accuracy < MinAccuracy)
            return new QualificationVerdict(false,
                $"accuracy {accuracy:P0} is below the {MinAccuracy:P0} minimum");

        return new QualificationVerdict(true, $"accuracy {accuracy:P0}");
    }
}

