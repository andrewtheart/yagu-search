using Yagu.Services;
using Yagu.Services.Index;
using Xunit;

namespace Yagu.Tests.Index;

/// <summary>Exhaustive rebuild-advice policy tests for persisted Indexing settings.</summary>
public sealed class ContentIndexSettingsChangeAdvisorTests
{
    private static AppSettings Settings(params string[] roots)
        => new() { IndexedRoots = roots.ToList() };

    [Fact]
    public void EveryScalarConfigKey_HasAnExplicitImpactClassification()
    {
        foreach (string key in ContentIndexConfigService.Keys)
            Assert.True(
                ContentIndexSettingsChangeAdvisor.TryGetConfigKeyImpact(key, out _),
                $"Index setting '{key}' needs an explicit rebuild/no-rebuild policy decision.");
    }

    [Fact]
    public void UnknownScalarConfigKey_HasNoImpactClassification()
    {
        Assert.False(ContentIndexSettingsChangeAdvisor.TryGetConfigKeyImpact(
            "UnknownIndexSetting",
            out ContentIndexConfigChangeImpact impact));
        Assert.Equal(default, impact);
    }

    [Fact]
    public void Analyze_MissingScalarEntriesUseEmptyFallback()
    {
        var snapshot = new ContentIndexSettingsSnapshot(
            new Dictionary<string, string>(),
            [],
            new Dictionary<string, ContentIndexRootFilterSnapshot>());

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(snapshot, snapshot);

        Assert.False(advice.HasRecommendation);
        Assert.Empty(advice.Reasons);
        Assert.Empty(advice.AffectedRoots);
    }

    [Fact]
    public void Capture_NullPatternAndExtensionListsNormalizeToEmpty()
    {
        AppSettings settings = Settings(@"C:\");
        settings.IndexExcludedGlobs = null!;
        settings.IndexExcludedExtensions = null!;

        ContentIndexSettingsSnapshot snapshot = ContentIndexSettingsChangeAdvisor.Capture(settings);

        Assert.Equal(string.Empty, snapshot.ScalarValues["IndexExcludedGlobs"]);
        Assert.Equal(string.Empty, snapshot.ScalarValues["IndexExcludedExtensions"]);
    }

    [Theory]
    [InlineData("IndexStorageDirectory", @"D:\YaguIndex")]
    [InlineData("IndexMaxFileSizeMB", "222")]
    [InlineData("IndexFollowReparsePoints", "true")]
    [InlineData("IndexIncludeHiddenFiles", "false")]
    [InlineData("IndexExcludedGlobs", "**/obj/**")]
    [InlineData("IndexExcludedExtensions", ".map;.min.js")]
    public void BuildOutputSettingChange_RecommendsEveryMaintainedRoot(string key, string value)
    {
        AppSettings settings = Settings(@"C:\", @"D:\");
        ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);
        Assert.True(ContentIndexConfigService.Set(settings, key, value).Success);

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(
            before,
            ContentIndexSettingsChangeAdvisor.Capture(settings));

        Assert.True(advice.HasRecommendation);
        Assert.Equal(new[] { @"C:\", @"D:\" }, advice.AffectedRoots);
        Assert.Contains(advice.Reasons, reason => reason.SettingKey == key);
    }

    [Theory]
    [InlineData("IndexBuildPdfTextExtendedSource")]
    [InlineData("IndexBuildImageTextExtendedSource")]
    [InlineData("IndexProduceV3QueryStructures")]
    public void AdditiveBuildOutput_EnablingRecommendsRebuild_DisablingDoesNot(string key)
    {
        AppSettings settings = Settings(@"C:\");
        Assert.True(ContentIndexConfigService.Set(settings, key, "false").Success);
        ContentIndexSettingsSnapshot disabled = ContentIndexSettingsChangeAdvisor.Capture(settings);
        Assert.True(ContentIndexConfigService.Set(settings, key, "true").Success);
        ContentIndexSettingsSnapshot enabled = ContentIndexSettingsChangeAdvisor.Capture(settings);

        Assert.True(ContentIndexSettingsChangeAdvisor.Analyze(disabled, enabled).HasRecommendation);
        Assert.False(ContentIndexSettingsChangeAdvisor.Analyze(enabled, disabled).HasRecommendation);
    }

    [Theory]
    [InlineData("IndexAccelerateRegex", "false")]
    [InlineData("IndexQueryWorkerParallelism", "4")]
    [InlineData("IndexBuildTrigger", "Continuous")]
    [InlineData("IndexBuildMemoryBudgetMB", "512")]
    [InlineData("IndexMaxDeltaSegments", "16")]
    [InlineData("ShowIndexStatusInMainWindow", "false")]
    public void RuntimeSchedulingResourceOrPresentationChange_DoesNotRecommendRebuild(string key, string value)
    {
        AppSettings settings = Settings(@"C:\");
        ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);
        Assert.True(ContentIndexConfigService.Set(settings, key, value).Success);

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(
            before,
            ContentIndexSettingsChangeAdvisor.Capture(settings));

        Assert.False(advice.HasRecommendation);
        Assert.Empty(advice.Reasons);
        Assert.Empty(advice.AffectedRoots);
    }

    [Fact]
    public void AddedOrBroadenedRoot_RecommendsOnlyTheNewMaintainedRoot()
    {
        AppSettings settings = Settings(@"C:\src", @"D:\");
        ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);
        settings.IndexedRoots = IndexedRootsPolicy.Add(settings.IndexedRoots, @"C:\");

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(
            before,
            ContentIndexSettingsChangeAdvisor.Capture(settings));

        Assert.True(advice.HasRecommendation);
        Assert.Equal(new[] { @"C:\" }, advice.AffectedRoots);
        Assert.Contains(advice.Reasons, reason => reason.SettingKey == "IndexedRoots");
    }

    [Fact]
    public void RemovedRoot_DoesNotRecommendRebuildingUnmaintainedData()
    {
        AppSettings settings = Settings(@"C:\", @"D:\");
        ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);
        settings.IndexedRoots = IndexedRootsPolicy.Remove(settings.IndexedRoots, @"D:\");

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(
            before,
            ContentIndexSettingsChangeAdvisor.Capture(settings));

        Assert.False(advice.HasRecommendation);
    }

    [Fact]
    public void PerRootFilterChange_RecommendsOnlyThatMaintainedRoot()
    {
        AppSettings settings = Settings(@"C:\", @"D:\");
        ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);
        settings.IndexedRootFilters =
        [
            new IndexedRootFilter { Path = @"D:\", IncludeGlobs = "**/kept/**", ExcludeGlobs = "**/obj/**" },
        ];

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(
            before,
            ContentIndexSettingsChangeAdvisor.Capture(settings));

        Assert.True(advice.HasRecommendation);
        Assert.Equal(new[] { @"D:\" }, advice.AffectedRoots);
        Assert.Contains(advice.Reasons, reason => reason.SettingKey == "IndexedRootFilters");
    }

    [Fact]
    public void SemanticallyEquivalentListsAndFilters_DoNotCauseFalseRecommendation()
    {
        var settings = Settings(@"C:\");
        settings.IndexExcludedGlobs = "**/obj/**; **/bin/**";
        settings.IndexExcludedExtensions = ".MAP; *.min.js";
        settings.IndexedRootFilters =
        [
            new IndexedRootFilter
            {
                Path = @"C:\",
                IncludeGlobs = "**/keep/**;**/also/**",
                ExcludeGlobs = "**/tmp/**;**/cache/**",
            },
        ];
        ContentIndexSettingsSnapshot before = ContentIndexSettingsChangeAdvisor.Capture(settings);

        settings.IndexExcludedGlobs = "**/bin/**,**/OBJ/**";
        settings.IndexExcludedExtensions = "min.js;map";
        settings.IndexedRootFilters =
        [
            new IndexedRootFilter
            {
                Path = @"c:\",
                IncludeGlobs = "**/ALSO/**, **/keep/**",
                ExcludeGlobs = "**/cache/**, **/TMP/**",
            },
        ];

        ContentIndexSettingsChangeAdvice advice = ContentIndexSettingsChangeAdvisor.Analyze(
            before,
            ContentIndexSettingsChangeAdvisor.Capture(settings));

        Assert.False(advice.HasRecommendation);
    }

    [Fact]
    public void Snapshot_IsDeepAndUnaffectedByLaterListMutation()
    {
        AppSettings settings = Settings(@"C:\");
        settings.IndexedRootFilters =
        [
            new IndexedRootFilter { Path = @"C:\", ExcludeGlobs = "**/obj/**" },
        ];
        ContentIndexSettingsSnapshot snapshot = ContentIndexSettingsChangeAdvisor.Capture(settings);

        settings.IndexedRoots[0] = @"D:\";
        settings.IndexedRootFilters[0].ExcludeGlobs = "**/bin/**";

        Assert.Equal(new[] { @"C:\" }, snapshot.IndexedRoots);
        Assert.Equal("**/obj/**", snapshot.RootFilters[@"C:\"].ExcludeGlobs);
    }
}
