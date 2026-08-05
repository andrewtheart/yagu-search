using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Advanced Options tab switching, drag-reordering of the tab column, and panel-level actions.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Backs the Advanced Options tab column. Bound as an ItemsSource (rather than inline
    /// ListViewItems) because WinUI's built-in drag-reorder only rewrites a bound collection.
    /// Declared in shipped order; the user's saved order is applied on first drawer open.</summary>
    public ObservableCollection<AdvancedOptionsTabItem> AdvancedOptionsTabs { get; } =
    [
        new("search", "\uE721", "Search"),
        new("quick", "\uE945", "Quick searches"),
        new("filters", "\uE71C", "Filters"),
        new("size", "\uE8A5", "Size"),
        new("dates", "\uE787", "Dates"),
        new("advanced", "\uE713", "Advanced"),
    ];

    /// <summary>Stable tab keys in shipped order. These are persisted in
    /// <see cref="AppSettings.AdvancedOptionsTabOrder"/>, so renaming one silently resets a user's
    /// saved order — add new tabs to the end instead of repurposing an existing key.</summary>
    private static readonly string[] ShippedAdvancedOptionsTabOrder =
        ["search", "quick", "filters", "size", "dates", "advanced"];

    private const string AdvancedOptionsSearchTabKey = "search";

    private bool _advancedOptionsTabOrderApplied;

    /// <summary>Set while the tab order is being rewritten, because moving items makes the
    /// ListView raise SelectionChanged with a transient empty selection that must not switch tabs.</summary>
    private bool _reorderingAdvancedOptionsTabs;

    private static string? AdvancedOptionsTabKeyOf(object? item)
        => (item as AdvancedOptionsTabItem)?.Key;

    private FrameworkElement? ResolveAdvancedOptionsTabContent(string? tabKey) => tabKey switch
    {
        "search" => AdvancedOptionsSearchTabContent,
        "quick" => AdvancedOptionsQuickSearchesTabContent,
        "filters" => AdvancedOptionsFiltersTabContent,
        "size" => AdvancedOptionsSizeTabContent,
        "dates" => AdvancedOptionsDatesTabContent,
        "advanced" => AdvancedOptionsAdvancedTabContent,
        _ => null,
    };

    private void OnAdvancedOptionsTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard: SelectionChanged fires during XAML load before fields are resolved, and again while
        // a drag-reorder is rewriting the item list.
        if (AdvancedOptionsSearchTabContent is null || _reorderingAdvancedOptionsTabs)
            return;

        string? tabKey = AdvancedOptionsTabKeyOf(AdvancedOptionsTabList.SelectedItem);
        if (tabKey is null)
        {
            SelectAdvancedOptionsTab(AdvancedOptionsSearchTabKey);
            return;
        }

        SetAdvancedOptionsTab(tabKey);
    }

    /// <summary>Shows the content pane for <paramref name="tabKey"/> and hides the rest. Keyed by tab
    /// identity rather than list position so drag-reordering cannot desynchronize tab and content.</summary>
    private void SetAdvancedOptionsTab(string tabKey)
    {
        foreach (var key in ShippedAdvancedOptionsTabOrder)
        {
            var content = ResolveAdvancedOptionsTabContent(key);
            if (content is not null)
                SetAdvancedOptionsTabVisibility(content, string.Equals(key, tabKey, StringComparison.Ordinal));
        }

        UpdateAdvancedOptionsDrawerMaxHeight();
    }

    /// <summary>Selects the tab with the given key wherever the user has dragged it to.</summary>
    private void SelectAdvancedOptionsTab(string tabKey)
    {
        foreach (var tab in AdvancedOptionsTabs)
        {
            if (!string.Equals(tab.Key, tabKey, StringComparison.Ordinal))
                continue;

            if (!ReferenceEquals(AdvancedOptionsTabList.SelectedItem, tab))
                AdvancedOptionsTabList.SelectedItem = tab; // fires SelectionChanged -> SetAdvancedOptionsTab
            else
                SetAdvancedOptionsTab(tabKey); // already selected: make sure the content matches
            return;
        }

        SetAdvancedOptionsTab(tabKey);
    }

    private static void SetAdvancedOptionsTabVisibility(FrameworkElement tabContent, bool isVisible)
        => tabContent.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Selects the Search tab so the Advanced Options drawer always opens on Search rather than
    /// reopening on whatever tab was last viewed. Called each time the flyout opens, and also the
    /// first-open hook that restores the user's saved tab order.
    /// </summary>
    private void ResetAdvancedOptionsToSearchTab()
    {
        // Guard: the flyout can open before the templated tab fields are resolved.
        if (AdvancedOptionsTabList is null || AdvancedOptionsSearchTabContent is null)
            return;

        ApplySavedAdvancedOptionsTabOrder();
        SelectAdvancedOptionsTab(AdvancedOptionsSearchTabKey);
    }

    /// <summary>
    /// Reapplies the user's persisted tab order, once per session on first open (the drawer is the
    /// only thing that reads it, so doing this lazily keeps it off the startup path). Tabs missing
    /// from the saved list keep their shipped position and unknown keys are ignored, so the setting
    /// degrades gracefully across versions that add or remove tabs.
    /// </summary>
    private void ApplySavedAdvancedOptionsTabOrder()
    {
        if (_advancedOptionsTabOrderApplied)
            return;

        _advancedOptionsTabOrderApplied = true;

        var savedOrder = ViewModel.AdvancedOptionsTabOrder;
        if (savedOrder is null || savedOrder.Count == 0)
            return;

        var byKey = new Dictionary<string, AdvancedOptionsTabItem>(StringComparer.Ordinal);
        foreach (var tab in AdvancedOptionsTabs)
            byKey[tab.Key] = tab;

        var desired = new List<AdvancedOptionsTabItem>(AdvancedOptionsTabs.Count);
        foreach (var key in savedOrder)
        {
            if (byKey.TryGetValue(key, out var tab) && !desired.Contains(tab))
                desired.Add(tab);
        }

        // Tabs the saved order never mentioned (added in a newer version) keep their shipped position.
        foreach (var tab in AdvancedOptionsTabs)
        {
            if (!desired.Contains(tab))
                desired.Add(tab);
        }

        if (desired.Count != AdvancedOptionsTabs.Count)
            return; // defensive: never drop a tab

        _reorderingAdvancedOptionsTabs = true;
        try
        {
            for (int target = 0; target < desired.Count; target++)
            {
                int current = AdvancedOptionsTabs.IndexOf(desired[target]);
                if (current != target && current >= 0)
                    AdvancedOptionsTabs.Move(current, target);
            }
        }
        finally
        {
            _reorderingAdvancedOptionsTabs = false;
        }
    }

    /// <summary>Persists the tab column order after the user finishes a drag-reorder.</summary>
    private async void OnAdvancedOptionsTabsReordered(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move)
            return;

        var order = new List<string>(AdvancedOptionsTabs.Count);
        foreach (var tab in AdvancedOptionsTabs)
            order.Add(tab.Key);

        if (order.Count == 0)
            return;

        ViewModel.AdvancedOptionsTabOrder = order;

        // Keep the content pane matching the selected tab: a drag can change which item is selected.
        string? selectedKey = AdvancedOptionsTabKeyOf(AdvancedOptionsTabList.SelectedItem);
        if (selectedKey is not null)
            SetAdvancedOptionsTab(selectedKey);

        await ViewModel.PersistSettingsAsync();
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
