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

    /// <summary>
    /// Selects the Search tab (index 0) so the Advanced Options drawer always opens on Search rather
    /// than reopening on whatever tab was last viewed. Called each time the flyout opens.
    /// </summary>
    private void ResetAdvancedOptionsToSearchTab()
    {
        // Guard: the flyout can open before the templated tab fields are resolved.
        if (AdvancedOptionsTabList is null || AdvancedOptionsSearchTabContent is null)
            return;

        if (AdvancedOptionsTabList.SelectedIndex != 0)
            AdvancedOptionsTabList.SelectedIndex = 0; // fires SelectionChanged -> SetAdvancedOptionsTab(0)
        else
            SetAdvancedOptionsTab(0); // already 0: ensure the Search content is the visible one
    }

    private void OnAdvancedOptionsResetClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetAdvancedOptionsToSavedDefaults();

        IncludeFilterBox.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
        ExcludeFilterBox.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
    }

    private async void OnAdvancedOptionsSaveDefaultsClick(object sender, RoutedEventArgs e)
    {
        // Confirm first, showing a summary of exactly what is about to be written to the settings file.
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "These Advanced Options will be written to your settings file as the new defaults. "
                 + "They become what a fresh search \u2014 and the \u201cReset\u201d button \u2014 start from.",
            TextWrapping = TextWrapping.Wrap,
        });

        var list = new StackPanel { Spacing = 3 };
        foreach (var line in ViewModel.DescribeAdvancedOptionDefaults())
            list.Children.Add(new TextBlock { Text = line, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 240,
        });

        var result = await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Save Advanced Options as defaults",
                TitleGlyph = "\uE74E", // Save
                Content = panel,
                PrimaryButtonText = "Save as defaults",
                CloseButtonText = "Cancel",
                DefaultButton = YaguDialogDefaultButton.Primary,
                RequestedTheme = RootGrid.ActualTheme,
                ShowTitleBar = false,
                Width = 600,
                Height = 440,
                MaxContentHeight = 340,
            });

        if (result != YaguDialogResult.Primary)
            return;

        await ViewModel.SaveAdvancedOptionsAsDefaultsAsync();

        // The Reset target just changed; refresh the filter placeholders to match the new defaults.
        IncludeFilterBox.PlaceholderText = ViewModel.IncludeFilterPlaceholder;
        ExcludeFilterBox.PlaceholderText = ViewModel.ExcludeFilterPlaceholder;
    }

    private void OnAdvancedOptionsApplyClick(object sender, RoutedEventArgs e)
        => CollapseAdvancedOptionsForSearch();
}
