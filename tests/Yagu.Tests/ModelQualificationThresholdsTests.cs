using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>Unit tests for <see cref="ModelQualificationThresholds"/> — the user-chosen time limits that
/// replace the old hard-coded speed gates. Verifies the documented defaults and the per-complexity
/// ceiling lookup.</summary>
public sealed class ModelQualificationThresholdsTests
{
    [Fact]
    public void Default_UsesDocumentedLimits()
    {
        var t = ModelQualificationThresholds.Default;
        Assert.Equal(60_000, t.ModelLoadMaxMs);
        Assert.Equal(15_000, t.SimpleQueryMaxMs);
        Assert.Equal(25_000, t.ComplexQueryMaxMs);
    }

    [Fact]
    public void DefaultConstants_MatchDefaultInstance()
    {
        Assert.Equal(ModelQualificationThresholds.DefaultModelLoadMaxMs, ModelQualificationThresholds.Default.ModelLoadMaxMs);
        Assert.Equal(ModelQualificationThresholds.DefaultSimpleQueryMaxMs, ModelQualificationThresholds.Default.SimpleQueryMaxMs);
        Assert.Equal(ModelQualificationThresholds.DefaultComplexQueryMaxMs, ModelQualificationThresholds.Default.ComplexQueryMaxMs);
    }

    [Fact]
    public void QueryMaxMs_PicksSimpleOrComplexLimit()
    {
        var t = new ModelQualificationThresholds { SimpleQueryMaxMs = 3_000, ComplexQueryMaxMs = 7_000 };
        Assert.Equal(3_000, t.QueryMaxMs(SemanticProbeComplexity.Simple));
        Assert.Equal(7_000, t.QueryMaxMs(SemanticProbeComplexity.Complex));
    }

    [Fact]
    public void Record_SupportsWithMutation()
    {
        var t = ModelQualificationThresholds.Default with { ModelLoadMaxMs = 5_000 };
        Assert.Equal(5_000, t.ModelLoadMaxMs);
        // Unchanged fields keep the defaults.
        Assert.Equal(15_000, t.SimpleQueryMaxMs);
    }
}
