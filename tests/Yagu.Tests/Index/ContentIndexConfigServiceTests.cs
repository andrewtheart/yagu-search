using System.Collections.Generic;
using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>
/// Tests for <see cref="ContentIndexConfigService"/> (plan §6.3 <c>--index-config</c>): key enumeration,
/// get/set with shared validation and clamping, unknown-key and invalid-value rejection, batch
/// all-or-nothing application, and reset-to-defaults.
/// </summary>
public sealed class ContentIndexConfigServiceTests
{
    [Fact]
    public void Keys_CoverCoreSettings()
    {
        Assert.Contains("EnableContentIndex", ContentIndexConfigService.Keys);
        Assert.Contains("IndexQueryStartupBudgetMs", ContentIndexConfigService.Keys);
        Assert.Contains("IndexBuildTrigger", ContentIndexConfigService.Keys);
        Assert.Contains("IndexStorageDirectory", ContentIndexConfigService.Keys);
        Assert.True(ContentIndexConfigService.Keys.Count >= 25);
    }

    [Fact]
    public void GetAll_ReturnsEveryKey()
    {
        var map = ContentIndexConfigService.GetAll(new AppSettings());
        Assert.Equal(ContentIndexConfigService.Keys.Count, map.Count);
        Assert.Equal("true", map["EnableContentIndex"]);
        Assert.Equal("200", map["IndexQueryStartupBudgetMs"]);
        Assert.Equal("Manual", map["IndexBuildTrigger"]);
    }

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
        => Assert.Null(ContentIndexConfigService.Get(new AppSettings(), "NoSuchKey"));

    [Fact]
    public void Get_KnownKey_ReturnsCurrentValue()
        => Assert.Equal("true", ContentIndexConfigService.Get(new AppSettings { EnableContentIndex = true }, "EnableContentIndex"));

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("YES")]
    public void Set_Bool_AcceptsVariants(string value)
    {
        var settings = new AppSettings { EnableContentIndex = false };
        var result = ContentIndexConfigService.Set(settings, "EnableContentIndex", value);
        Assert.True(result.Success);
        Assert.True(settings.EnableContentIndex);
    }

    [Fact]
    public void Set_Bool_InvalidValue_Fails()
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "EnableContentIndex", "maybe");
        Assert.False(result.Success);
        Assert.Contains("boolean", result.Error);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    public void Set_Bool_AcceptsFalseVariants(string value)
    {
        var settings = new AppSettings { EnableContentIndex = true };
        Assert.True(ContentIndexConfigService.Set(settings, "EnableContentIndex", value).Success);
        Assert.False(settings.EnableContentIndex);
    }

    [Fact]
    public void SetMany_NullValue_IsCoalescedToEmpty()
    {
        // A null value must coalesce to empty for both validation and application. IndexStorageDirectory
        // validates any string (empty means "use the default location"), so this applies successfully.
        var settings = new AppSettings();
        var result = ContentIndexConfigService.SetMany(settings, new (string, string)[] { ("IndexStorageDirectory", null!) });
        Assert.True(result.Success);
    }

    [Fact]
    public void Set_Int_ClampsViaSharedNormalizer()
    {
        var settings = new AppSettings();
        Assert.True(ContentIndexConfigService.Set(settings, "IndexQueryStartupBudgetMs", "999999").Success);
        Assert.Equal(AppSettings.MaximumIndexQueryStartupBudgetMs, settings.IndexQueryStartupBudgetMs);
    }

    [Fact]
    public void Set_Int_NonNumeric_Fails()
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "IndexQueryStartupBudgetMs", "fast");
        Assert.False(result.Success);
        Assert.Contains("integer", result.Error);
    }

    [Fact]
    public void Set_Enum_CanonicalizesCasing()
    {
        var settings = new AppSettings();
        Assert.True(ContentIndexConfigService.Set(settings, "IndexScheduleMode", "weekly").Success);
        Assert.Equal("Weekly", settings.IndexScheduleMode);
    }

    [Fact]
    public void Set_Enum_Unknown_Fails()
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "IndexScheduleMode", "sometimes");
        Assert.False(result.Success);
        Assert.Contains("expected one of", result.Error);
    }

    [Fact]
    public void Set_Flags_AcceptsCombinationAndCanonicalizes()
    {
        var settings = new AppSettings();
        Assert.True(ContentIndexConfigService.Set(settings, "IndexBuildTrigger", "onschedule continuous atstartup").Success);
        Assert.Equal("AtStartup, Continuous, OnSchedule", settings.IndexBuildTrigger);
    }

    [Fact]
    public void Set_Flags_Unknown_Fails()
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "IndexBuildTrigger", "AtStartup, sometimes");
        Assert.False(result.Success);
        Assert.Contains("expected any combination", result.Error);
    }

    [Fact]
    public void Set_UnknownKey_Fails()
    {
        var result = ContentIndexConfigService.Set(new AppSettings(), "Bogus", "1");
        Assert.False(result.Success);
        Assert.Contains("unknown config key", result.Error);
    }

    [Fact]
    public void SetMany_AllOrNothing_OnFailure()
    {
        var settings = new AppSettings { EnableContentIndex = false };
        var result = ContentIndexConfigService.SetMany(settings, new[]
        {
            ("EnableContentIndex", "true"),
            ("IndexBuildTrigger", "invalid"), // fails validation → nothing applies
        });
        Assert.False(result.Success);
        Assert.False(settings.EnableContentIndex); // not applied
    }

    [Fact]
    public void SetMany_AppliesAllOnSuccess()
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.SetMany(settings, new[]
        {
            ("EnableContentIndex", "true"),
            ("IndexMaxCandidatePercent", "10"),
        });
        Assert.True(result.Success);
        Assert.True(settings.EnableContentIndex);
        Assert.Equal(10, settings.IndexMaxCandidatePercent);
    }

    [Fact]
    public void Reset_RestoresDefaults()
    {
        var settings = new AppSettings
        {
            EnableContentIndex = false,
            IndexBuildTrigger = "WhenIdle",
            IndexMaxCandidatePercent = 5,
        };
        ContentIndexConfigService.Reset(settings);
        Assert.True(settings.EnableContentIndex);
        Assert.Equal("Manual", settings.IndexBuildTrigger);
        Assert.Equal(AppSettings.DefaultIndexMaxCandidatePercent, settings.IndexMaxCandidatePercent);
    }

    [Fact]
    public void RoundTrip_GetAllValues_AreSettable()
    {
        // Parity guard: every key emitted by GetAll must be accepted by Set with its own value.
        var settings = new AppSettings();
        foreach (var (key, value) in ContentIndexConfigService.GetAll(settings))
        {
            var result = ContentIndexConfigService.Set(settings, key, value);
            Assert.True(result.Success, $"Key '{key}' failed to round-trip its own value '{value}': {result.Error}");
        }
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Set_IndexUseNativeWorker_ParsesBool(string value, bool expected)
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "IndexUseNativeWorker", value);
        Assert.True(result.Success);
        Assert.Equal(expected, settings.IndexUseNativeWorker);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Set_IndexUseWorkerQuerySessions_ParsesBool(string value, bool expected)
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "IndexUseWorkerQuerySessions", value);
        Assert.True(result.Success);
        Assert.Equal(expected, settings.IndexUseWorkerQuerySessions);
    }

    [Fact]
    public void Set_IndexMaxWorkerQuerySizeMB_NormalizesValue()
    {
        var settings = new AppSettings();
        var result = ContentIndexConfigService.Set(settings, "IndexMaxWorkerQuerySizeMB", "40960");
        Assert.True(result.Success);
        Assert.Equal(40960, settings.IndexMaxWorkerQuerySizeMB);
    }
}
