using System;
using System.Collections.Generic;
using Yagu.Services;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>Unit tests for the pure decide/persist logic of the first-run model-qualification flow.</summary>
public sealed class SemanticModelQualificationCoordinatorTests
{
    private static ModelQualificationResult Result(string? qualified, string? bestEffort) =>
        new()
        {
            QualifiedModelAlias = qualified,
            BestEffortModelAlias = bestEffort,
            Reports = Array.Empty<CandidateQualificationReport>(),
        };

    [Fact]
    public void ShouldOffer_WhenEnabledAvailableAndNotCompleted()
    {
        var settings = new AppSettings { SemanticSearchEnabled = true, SemanticModelQualificationCompleted = false };
        Assert.True(SemanticModelQualificationCoordinator.ShouldOffer(settings, semanticAvailable: true));
    }

    [Fact]
    public void ShouldOffer_FalseWhenAlreadyCompleted()
    {
        var settings = new AppSettings { SemanticSearchEnabled = true, SemanticModelQualificationCompleted = true };
        Assert.False(SemanticModelQualificationCoordinator.ShouldOffer(settings, semanticAvailable: true));
    }

    [Fact]
    public void ShouldOffer_FalseWhenSemanticDisabledOrUnavailable()
    {
        var disabled = new AppSettings { SemanticSearchEnabled = false, SemanticModelQualificationCompleted = false };
        Assert.False(SemanticModelQualificationCoordinator.ShouldOffer(disabled, semanticAvailable: true));

        var enabled = new AppSettings { SemanticSearchEnabled = true, SemanticModelQualificationCompleted = false };
        Assert.False(SemanticModelQualificationCoordinator.ShouldOffer(enabled, semanticAvailable: false));
    }

    [Fact]
    public void Suggestion_PrefersQualifiedOverBestEffort()
    {
        Assert.Equal("phi-4-mini", SemanticModelQualificationCoordinator.Suggestion(Result("phi-4-mini", "qwen2.5-0.5b")));
        Assert.Equal("qwen2.5-0.5b", SemanticModelQualificationCoordinator.Suggestion(Result(null, "qwen2.5-0.5b")));
        Assert.Null(SemanticModelQualificationCoordinator.Suggestion(Result(null, null)));
    }

    [Fact]
    public void ApplyResult_Accepted_MarksCompletedRecordsAndSetsEffectiveModel()
    {
        var settings = new AppSettings();
        SemanticModelQualificationCoordinator.ApplyResult(settings, Result("phi-4-mini", "x"), accepted: true);

        Assert.True(settings.SemanticModelQualificationCompleted);
        Assert.Equal("phi-4-mini", settings.SemanticQualifiedModelAlias);
        Assert.Equal("phi-4-mini", settings.SemanticModelAlias);
    }

    [Fact]
    public void ApplyResult_NotAccepted_RecordsSuggestionButLeavesEffectiveModelUntouched()
    {
        var settings = new AppSettings { SemanticModelAlias = "existing" };
        SemanticModelQualificationCoordinator.ApplyResult(settings, Result("phi-4-mini", "x"), accepted: false);

        Assert.True(settings.SemanticModelQualificationCompleted);
        Assert.Equal("phi-4-mini", settings.SemanticQualifiedModelAlias);
        Assert.Equal("existing", settings.SemanticModelAlias); // unchanged
    }

    [Fact]
    public void ApplyResult_AcceptedWithOverride_UsesChosenAlias()
    {
        var settings = new AppSettings();
        SemanticModelQualificationCoordinator.ApplyResult(
            settings, Result("phi-4-mini", "x"), accepted: true, chosenAlias: "  qwen2.5-1.5b  ");

        Assert.Equal("phi-4-mini", settings.SemanticQualifiedModelAlias); // suggestion recorded as-is
        Assert.Equal("qwen2.5-1.5b", settings.SemanticModelAlias); // trimmed override applied
    }

    [Fact]
    public void ApplyResult_NoUsableModel_MarksCompletedWithEmptyAliasAndNoOverride()
    {
        var settings = new AppSettings { SemanticModelAlias = "existing" };
        SemanticModelQualificationCoordinator.ApplyResult(settings, Result(null, null), accepted: true);

        Assert.True(settings.SemanticModelQualificationCompleted);
        Assert.Equal(string.Empty, settings.SemanticQualifiedModelAlias);
        Assert.Equal("existing", settings.SemanticModelAlias); // nothing to apply
    }

    [Fact]
    public void MarkDeclined_MarksCompletedWithoutTouchingModel()
    {
        var settings = new AppSettings { SemanticModelAlias = "existing" };
        SemanticModelQualificationCoordinator.MarkDeclined(settings);

        Assert.True(settings.SemanticModelQualificationCompleted);
        Assert.Equal(string.Empty, settings.SemanticQualifiedModelAlias);
        Assert.Equal("existing", settings.SemanticModelAlias);
    }

    [Fact]
    public void Reset_ClearsQualificationStateBackToFreshInstall()
    {
        var settings = new AppSettings
        {
            SemanticModelQualificationCompleted = true,
            SemanticQualifiedModelAlias = "phi-4-mini",
            SemanticModelAlias = "phi-4-mini",
        };

        SemanticModelQualificationCoordinator.Reset(settings);

        Assert.False(settings.SemanticModelQualificationCompleted);
        Assert.Equal(string.Empty, settings.SemanticQualifiedModelAlias);
        Assert.Equal(string.Empty, settings.SemanticModelAlias);
    }

    [Fact]
    public void Reset_ThenShouldOffer_WhenSemanticEnabledAndAvailable()
    {
        var settings = new AppSettings
        {
            SemanticSearchEnabled = true,
            SemanticModelQualificationCompleted = true,
            SemanticQualifiedModelAlias = "phi-4-mini",
        };

        SemanticModelQualificationCoordinator.Reset(settings);

        Assert.True(SemanticModelQualificationCoordinator.ShouldOffer(settings, semanticAvailable: true));
    }

    [Fact]
    public void Reset_DoesNotTouchSemanticSearchEnabled()
    {
        var disabled = new AppSettings
        {
            SemanticSearchEnabled = false,
            SemanticModelQualificationCompleted = true,
        };

        SemanticModelQualificationCoordinator.Reset(disabled);

        Assert.False(disabled.SemanticSearchEnabled);
    }
}
