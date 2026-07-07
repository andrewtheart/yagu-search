using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>Unit tests for <see cref="ModelQualificationPolicy"/> — the pass/fail decision that decides
/// whether a probed model is good enough to be the strongly-recommended on-device pick. The speed limits
/// are now user-chosen (see <see cref="ModelQualificationThresholds"/>) and surface here as an optional
/// <see cref="LatencyViolation"/>; the policy keeps only the fixed accuracy bar.</summary>
public sealed class ModelQualificationPolicyTests
{
    [Fact]
    public void Evaluate_Crashed_AlwaysFails_RegardlessOfOtherMetrics()
    {
        var v = ModelQualificationPolicy.Evaluate(accuracy: 1.0, crashed: true, latencyViolation: null);
        Assert.False(v.Passed);
        Assert.Contains("crash", v.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_AccurateAndNoViolation_Passes()
    {
        var v = ModelQualificationPolicy.Evaluate(accuracy: 1.0, crashed: false, latencyViolation: null);
        Assert.True(v.Passed);
    }

    [Fact]
    public void Evaluate_AtExactMinAccuracy_Passes()
    {
        var v = ModelQualificationPolicy.Evaluate(
            accuracy: ModelQualificationPolicy.MinAccuracy, crashed: false, latencyViolation: null);
        Assert.True(v.Passed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.74)]
    public void Evaluate_BelowMinAccuracy_Fails(double accuracy)
    {
        var v = ModelQualificationPolicy.Evaluate(accuracy, crashed: false, latencyViolation: null);
        Assert.False(v.Passed);
        Assert.Contains("accuracy", v.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_LatencyViolation_Fails_EvenWithPerfectAccuracy()
    {
        var v = ModelQualificationPolicy.Evaluate(
            accuracy: 1.0,
            crashed: false,
            latencyViolation: new LatencyViolation(18_000, 15_000, SemanticProbeComplexity.Complex));
        Assert.False(v.Passed);
        Assert.Contains("complex", v.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_LatencyViolation_WinsOverLowAccuracy()
    {
        // A candidate abandoned after one over-limit probe has only partial (low) accuracy, but the real
        // disqualifier is the per-query timeout, so the latency reason must win over the accuracy one.
        var v = ModelQualificationPolicy.Evaluate(
            accuracy: 0.2,
            crashed: false,
            latencyViolation: new LatencyViolation(12_000, 10_000, SemanticProbeComplexity.Simple));
        Assert.False(v.Passed);
        Assert.Contains("simple", v.Reason, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accuracy", v.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_LatencyViolationReason_MentionsMeasuredAndLimitSeconds()
    {
        var v = ModelQualificationPolicy.Evaluate(
            accuracy: 1.0,
            crashed: false,
            latencyViolation: new LatencyViolation(18_200, 15_000, SemanticProbeComplexity.Complex));
        Assert.False(v.Passed);
        Assert.Contains("18.2s", v.Reason, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15s", v.Reason, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MinAccuracy_IsASaneFraction()
    {
        double minAccuracy = ModelQualificationPolicy.MinAccuracy;
        Assert.True(minAccuracy > 0 && minAccuracy <= 1);
    }
}

