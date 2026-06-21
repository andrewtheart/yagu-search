using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Advanced Options tab switching and panel-level actions.
/// </summary>
public sealed partial class MainWindow
{
    private void OnAdvancedOptionsTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: SelectionChanged fires during XAML load before fields are resolved.
        if (AdvancedOptionsSearchTabContent is null)
            return;

        int selectedIndex = AdvancedOptionsTabList.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex > 4)
        {
            AdvancedOptionsTabList.SelectedIndex = 0;
            selectedIndex = 0;
        }

        SetAdvancedOptionsTab(selectedIndex);
    }

    private void SetAdvancedOptionsTab(int selectedIndex)
    {
        FrameworkElement[] tabContents =
        [
            AdvancedOptionsSearchTabContent,
            AdvancedOptionsFiltersTabContent,
            AdvancedOptionsSizeTabContent,
            AdvancedOptionsDatesTabContent,
            AdvancedOptionsAdvancedTabContent,
        ];

        for (int index = 0; index < tabContents.Length; index++)
            SetAdvancedOptionsTabVisibility(tabContents[index], selectedIndex == index);

        UpdateAdvancedOptionsDrawerMaxHeight();
    }

    private static void SetAdvancedOptionsTabVisibility(FrameworkElement tabContent, bool isVisible)
        => tabContent.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

    private void OnAdvancedOptionsResetClick(object sender, RoutedEventArgs e)
    {
        AppSettings settings = new SettingsService().Load();

        ViewModel.SearchModeIndex = 0;
        ViewModel.IncludeFilterModeIndex = settings.IncludeFilterModeIndex;
        ViewModel.ExcludeFilterModeIndex = settings.ExcludeFilterModeIndex;
        ViewModel.IncludeGlobs = settings.IncludeGlobs;
        ViewModel.ExcludeGlobs = settings.ExcludeGlobs;
        ViewModel.ObeyGitignore = settings.ObeyGitignore;

        ViewModel.SettingsSkipExtensions = settings.SkipExtensions;
        ViewModel.SkipExtensions = settings.SkipExtensions;
        ViewModel.SearchBinary = !settings.SkipBinary;
        ViewModel.SettingsBinaryExtensions = settings.BinaryExtensions;
        ViewModel.BinaryExtensions = settings.BinaryExtensions;
        ViewModel.SearchInsideArchives = settings.SearchInsideArchives;
        ViewModel.SettingsArchiveExtensions = settings.ArchiveExtensions;
        ViewModel.ArchiveExtensions = settings.ArchiveExtensions;

        ViewModel.DefaultMinFileSizeBytes = settings.DefaultMinFileSizeBytes;
        ViewModel.DefaultMaxFileSizeBytes = settings.DefaultMaxFileSizeBytes;
        ViewModel.MinFileSizeBytes = settings.DefaultMinFileSizeBytes;
        ViewModel.MaxFileSizeBytes = settings.DefaultMaxFileSizeBytes;
        ViewModel.DefaultCreatedAfterDate = settings.DefaultCreatedAfterDate;
        ViewModel.DefaultCreatedBeforeDate = settings.DefaultCreatedBeforeDate;
        ViewModel.DefaultModifiedAfterDate = settings.DefaultModifiedAfterDate;
        ViewModel.DefaultModifiedBeforeDate = settings.DefaultModifiedBeforeDate;
        ViewModel.CreatedAfterDate = settings.DefaultCreatedAfterDate;
        ViewModel.CreatedBeforeDate = settings.DefaultCreatedBeforeDate;
        ViewModel.ModifiedAfterDate = settings.DefaultModifiedAfterDate;
        ViewModel.ModifiedBeforeDate = settings.DefaultModifiedBeforeDate;
        ViewModel.MaxSearchDepth = double.NaN;

        IncludeFilterBox.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
        ExcludeFilterBox.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
    }

    private void OnAdvancedOptionsApplyClick(object sender, RoutedEventArgs e)
        => CollapseAdvancedOptionsForSearch();

    private const double FiltersTabWrapThreshold = 280;

    private void OnFiltersTabSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SkipExtRow is null) return;
        var orientation = e.NewSize.Width < FiltersTabWrapThreshold
            ? Orientation.Vertical
            : Orientation.Horizontal;
        SkipExtRow.Orientation = orientation;
        BinaryExtRow.Orientation = orientation;
        ArchiveExtRow.Orientation = orientation;

        var spacing = orientation == Orientation.Vertical ? 6.0 : 12.0;
        SkipExtRow.Spacing = spacing;
        BinaryExtRow.Spacing = spacing;
        ArchiveExtRow.Spacing = spacing;
    }
}
