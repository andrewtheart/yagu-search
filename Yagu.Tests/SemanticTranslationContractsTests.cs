using System;
using Yagu.Models;
using Yagu.Services.Ai;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for the semantic-translation contract types declared alongside
/// <see cref="ISemanticQueryTranslator"/>: the <see cref="SemanticModelOption"/> descriptor, the
/// <see cref="SemanticTranslationResult"/> Ok/Fail factories, the ambient
/// <see cref="SemanticTranslationContext"/> defaults, and the human-readable
/// <see cref="SemanticTranslationProgress.Message"/> rendered for every stage.
/// </summary>
public sealed class SemanticTranslationContractsTests
{
    // ── SemanticModelOption ──

    [Fact]
    public void SemanticModelOption_RetainsAllProvidedValues()
    {
        var option = new SemanticModelOption
        {
            Alias = "phi-3.5-mini",
            DisplayName = "Phi 3.5 Mini",
            SizeBytes = 2_500_000_000,
            IsRecommended = true,
            IsBelowRecommended = false,
            IsCached = true,
            DeviceLabel = "GPU",
        };

        Assert.Equal("phi-3.5-mini", option.Alias);
        Assert.Equal("Phi 3.5 Mini", option.DisplayName);
        Assert.Equal(2_500_000_000, option.SizeBytes);
        Assert.True(option.IsRecommended);
        Assert.False(option.IsBelowRecommended);
        Assert.True(option.IsCached);
        Assert.Equal("GPU", option.DeviceLabel);
    }

    [Fact]
    public void SemanticModelOption_DefaultsOptionalFieldsToUnset()
    {
        var option = new SemanticModelOption { Alias = "x", DisplayName = "X" };

        Assert.Null(option.SizeBytes);
        Assert.False(option.IsRecommended);
        Assert.False(option.IsBelowRecommended);
        Assert.False(option.IsCached);
        Assert.Null(option.DeviceLabel);
    }

    // ── SemanticTranslationResult ──

    [Fact]
    public void Result_Ok_CapturesPlanAndRawOutput()
    {
        var plan = new SemanticSearchPlan { Pattern = "*.png" };

        var result = SemanticTranslationResult.Ok(plan, "{\"pattern\":\"*.png\"}");

        Assert.True(result.Success);
        Assert.Same(plan, result.Plan);
        Assert.Null(result.Error);
        Assert.Equal("{\"pattern\":\"*.png\"}", result.RawModelOutput);
    }

    [Fact]
    public void Result_Ok_RawOutputOptional()
    {
        var result = SemanticTranslationResult.Ok(new SemanticSearchPlan());

        Assert.True(result.Success);
        Assert.NotNull(result.Plan);
        Assert.Null(result.RawModelOutput);
    }

    [Fact]
    public void Result_Fail_CapturesErrorAndRawOutput()
    {
        var result = SemanticTranslationResult.Fail("bad output", "garbage");

        Assert.False(result.Success);
        Assert.Null(result.Plan);
        Assert.Equal("bad output", result.Error);
        Assert.Equal("garbage", result.RawModelOutput);
    }

    [Fact]
    public void Result_Fail_RawOutputOptional()
    {
        var result = SemanticTranslationResult.Fail("nope");

        Assert.False(result.Success);
        Assert.Equal("nope", result.Error);
        Assert.Null(result.RawModelOutput);
    }

    // ── SemanticTranslationContext ──

    [Fact]
    public void Context_DefaultsNowToCurrentInstantAndNullDirectory()
    {
        var before = DateTimeOffset.Now.AddSeconds(-5);
        var context = new SemanticTranslationContext();
        var after = DateTimeOffset.Now.AddSeconds(5);

        Assert.InRange(context.Now, before, after);
        Assert.Null(context.DefaultDirectory);
    }

    [Fact]
    public void Context_RetainsExplicitValues()
    {
        var now = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var context = new SemanticTranslationContext { Now = now, DefaultDirectory = "D:\\work" };

        Assert.Equal(now, context.Now);
        Assert.Equal("D:\\work", context.DefaultDirectory);
    }

    // ── SemanticTranslationProgress.Message ──

    [Fact]
    public void Progress_Message_Initializing()
    {
        var p = new SemanticTranslationProgress { Stage = SemanticTranslationStage.Initializing };
        Assert.Equal("Preparing the local AI model…", p.Message);
    }

    [Fact]
    public void Progress_Message_DownloadingExecutionProviders_WithPercent()
    {
        var p = new SemanticTranslationProgress
        {
            Stage = SemanticTranslationStage.DownloadingExecutionProviders,
            Percent = 42.6,
            Detail = "DirectML",
        };
        Assert.Equal("Downloading AI runtime (DirectML) — 43%", p.Message);
    }

    [Fact]
    public void Progress_Message_DownloadingExecutionProviders_Indeterminate()
    {
        var p = new SemanticTranslationProgress { Stage = SemanticTranslationStage.DownloadingExecutionProviders };
        Assert.Equal("Downloading AI runtime…", p.Message);
    }

    [Fact]
    public void Progress_Message_DownloadingModel_WithPercent()
    {
        var p = new SemanticTranslationProgress
        {
            Stage = SemanticTranslationStage.DownloadingModel,
            Percent = 7.0,
        };
        Assert.Equal("Downloading model — 7%", p.Message);
    }

    [Fact]
    public void Progress_Message_DownloadingModel_Indeterminate()
    {
        var p = new SemanticTranslationProgress { Stage = SemanticTranslationStage.DownloadingModel };
        Assert.Equal("Downloading model…", p.Message);
    }

    [Fact]
    public void Progress_Message_LoadingModel()
    {
        var p = new SemanticTranslationProgress { Stage = SemanticTranslationStage.LoadingModel };
        Assert.Equal("Loading the model…", p.Message);
    }

    [Fact]
    public void Progress_Message_Interpreting()
    {
        var p = new SemanticTranslationProgress { Stage = SemanticTranslationStage.Interpreting };
        Assert.Equal("Interpreting your request…", p.Message);
    }

    [Fact]
    public void Progress_Message_UnknownStage_FallsBackToWorking()
    {
        var p = new SemanticTranslationProgress { Stage = (SemanticTranslationStage)999 };
        Assert.Equal("Working…", p.Message);
    }
}
