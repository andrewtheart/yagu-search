using Yagu.Helpers;
using Yagu.Services;

namespace Yagu.Tests;

/// <summary>
/// Unit tests for <see cref="AppUpdateModeChoices"/> — the one ordered list of update-check answers
/// shared by the first-run consent prompt and the Settings ▸ Updates picker. The index/mode mapping is
/// the drift-prone part: adding or reordering a mode used to require editing two hand-written switch
/// expressions in two different files.
/// </summary>
public sealed class AppUpdateModeChoicesTests
{
    [Fact]
    public void All_OffersEveryDecidedMode_AndNeverTheUndecidedPromptState()
    {
        AppUpdateCheckMode[] offered = [.. AppUpdateModeChoices.All.Select(c => c.Mode)];

        Assert.Equal(
            [
                AppUpdateCheckMode.AutomaticDaily,
                AppUpdateCheckMode.Automatic,
                AppUpdateCheckMode.Manual,
                AppUpdateCheckMode.Off,
            ],
            offered);

        // Prompt means "not decided yet"; offering it as a pickable answer would be meaningless.
        Assert.DoesNotContain(AppUpdateCheckMode.Prompt, offered);
    }

    [Fact]
    public void All_CoversEveryNonPromptEnumValue()
    {
        AppUpdateCheckMode[] expected = [.. Enum.GetValues<AppUpdateCheckMode>().Where(m => m != AppUpdateCheckMode.Prompt)];

        Assert.Equal(expected.Order(), AppUpdateModeChoices.All.Select(c => c.Mode).Order());
    }

    [Fact]
    public void DefaultMode_IsOncePerDay()
    {
        Assert.Equal(0, AppUpdateModeChoices.DefaultIndex);
        Assert.Equal(AppUpdateCheckMode.AutomaticDaily, AppUpdateModeChoices.DefaultMode);
        Assert.Equal(AppUpdateCheckMode.AutomaticDaily, AppUpdateModeChoices.All[AppUpdateModeChoices.DefaultIndex].Mode);
    }

    [Fact]
    public void DefaultChoice_IsMarkedRecommendedAndSaysDaily()
    {
        AppUpdateModeChoice daily = AppUpdateModeChoices.All[AppUpdateModeChoices.DefaultIndex];

        Assert.Contains("recommended", daily.PromptTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once a day", daily.PromptTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once a day", daily.PromptDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once a day", daily.SettingsLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WeeklyChoice_KeepsTheLessOftenWording()
    {
        AppUpdateModeChoice weekly = AppUpdateModeChoices.All.Single(c => c.Mode == AppUpdateCheckMode.Automatic);

        Assert.Contains("once a week", weekly.PromptTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once a week", weekly.SettingsLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryChoice_HasNonEmptyWordingForBothSurfaces()
    {
        foreach (AppUpdateModeChoice choice in AppUpdateModeChoices.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(choice.PromptTitle));
            Assert.False(string.IsNullOrWhiteSpace(choice.PromptDetail));
            Assert.False(string.IsNullOrWhiteSpace(choice.SettingsLabel));
        }
    }

    [Fact]
    public void PromptTitlesAndSettingsLabels_AreUnique()
    {
        Assert.Equal(AppUpdateModeChoices.All.Count, AppUpdateModeChoices.All.Select(c => c.PromptTitle).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(AppUpdateModeChoices.All.Count, AppUpdateModeChoices.All.Select(c => c.SettingsLabel).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(AppUpdateCheckMode.AutomaticDaily, 0)]
    [InlineData(AppUpdateCheckMode.Automatic, 1)]
    [InlineData(AppUpdateCheckMode.Manual, 2)]
    [InlineData(AppUpdateCheckMode.Off, 3)]
    public void IndexFor_MapsEachOfferedModeToItsRow(AppUpdateCheckMode mode, int expectedIndex)
        => Assert.Equal(expectedIndex, AppUpdateModeChoices.IndexFor(mode));

    [Fact]
    public void IndexFor_UndecidedPrompt_ShowsAsCheckOnDemand()
    {
        int index = AppUpdateModeChoices.IndexFor(AppUpdateCheckMode.Prompt);

        Assert.Equal(AppUpdateCheckMode.Manual, AppUpdateModeChoices.All[index].Mode);
    }

    [Fact]
    public void IndexFor_UnknownFutureValue_FallsBackToCheckOnDemand()
    {
        int index = AppUpdateModeChoices.IndexFor((AppUpdateCheckMode)999);

        Assert.Equal(AppUpdateCheckMode.Manual, AppUpdateModeChoices.All[index].Mode);
    }

    [Theory]
    [InlineData(0, AppUpdateCheckMode.AutomaticDaily)]
    [InlineData(1, AppUpdateCheckMode.Automatic)]
    [InlineData(2, AppUpdateCheckMode.Manual)]
    [InlineData(3, AppUpdateCheckMode.Off)]
    public void ModeFor_MapsEachRowToItsMode(int index, AppUpdateCheckMode expected)
        => Assert.Equal(expected, AppUpdateModeChoices.ModeFor(index));

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ModeFor_OutOfRange_NeverSilentlyEnablesBackgroundChecks(int index)
    {
        // An unselected ComboBox reports -1; a stale index must not turn network checks on by itself.
        AppUpdateCheckMode mode = AppUpdateModeChoices.ModeFor(index);

        Assert.Equal(AppUpdateCheckMode.Manual, mode);
        Assert.Null(AppUpdateChecker.GetAutoCheckInterval(mode));
    }

    [Fact]
    public void IndexAndMode_RoundTripForEveryRow()
    {
        for (int i = 0; i < AppUpdateModeChoices.All.Count; i++)
            Assert.Equal(i, AppUpdateModeChoices.IndexFor(AppUpdateModeChoices.ModeFor(i)));
    }

    [Fact]
    public void OnlyTheTwoAutomaticRows_HaveABackgroundCheckInterval()
    {
        var automatic = AppUpdateModeChoices.All
            .Where(c => AppUpdateChecker.GetAutoCheckInterval(c.Mode) is not null)
            .Select(c => c.Mode)
            .ToList();

        Assert.Equal([AppUpdateCheckMode.AutomaticDaily, AppUpdateCheckMode.Automatic], automatic);
    }

    [Fact]
    public void RowsAreOrderedFromMostToLeastFrequentChecking()
    {
        TimeSpan?[] intervals = [.. AppUpdateModeChoices.All.Select(c => AppUpdateChecker.GetAutoCheckInterval(c.Mode))];

        Assert.True(intervals[0] < intervals[1], "The daily row must precede the weekly row.");
        Assert.All(intervals.Skip(2), interval => Assert.Null(interval));
    }
}
