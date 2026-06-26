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
        ViewModel.ResetAdvancedOptionsToSavedDefaults();

        IncludeFilterBox.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
        ExcludeFilterBox.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
    }

    private void OnAdvancedOptionsApplyClick(object sender, RoutedEventArgs e)
        => CollapseAdvancedOptionsForSearch();
}
