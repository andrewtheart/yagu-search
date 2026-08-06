using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Yagu.Helpers;

namespace Yagu;

/// <summary>
/// The user-managed Quick searches list on the Advanced Options ▸ Quick searches tab: seeding on first
/// run, rendering each saved item, and the add / inline-edit / reorder / delete actions. Rows are built
/// here rather than in XAML because the list is data-driven and reorderable. Every mutation is written
/// through <see cref="Yagu.ViewModels.MainViewModel.PersistSettingsAsync"/> so it survives a restart.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Fixed lane reserved on the right of a row for its four hover actions (4 × 28 + spacing + inset).</summary>
    private const double QuickSearchActionsLaneWidth = (4 * 28) + (3 * 2) + 10;

    /// <summary>Icons offered by the editor's picker, so a glyph is chosen visually instead of typed as a codepoint.</summary>
    private static readonly string[] QuickSearchGlyphChoices =
    [
        "\uE721", "\uE8A5", "\uE8AB", "\uEBE8", "\uE72E", "\uE71B",
        "\uE715", "\uE7BA", "\uE81C", "\uE8AC", "\uE946", "\uE734",
        "\uE7C3", "\uE8F1", "\uE8B7", "\uE90B", "\uE9F5", "\uE943",
        "\uE72C", "\uE713", "\uE7EE", "\uE896", "\uE8EC", "\uE930",
    ];

    private List<QuickSearchItem> QuickSearches => ViewModel.Settings.QuickSearches;

    /// <summary>Seeds the built-in list once per profile, then renders the tab.</summary>
    private void InitializeQuickSearches()
    {
        var settings = ViewModel.Settings;
        if (!settings.QuickSearchesInitialized)
        {
            settings.QuickSearchesInitialized = true;
            if (settings.QuickSearches.Count == 0)
                settings.QuickSearches = QuickSearchCatalog.Defaults();
        }
        else
        {
            settings.QuickSearches = QuickSearchCatalog.Normalize(settings.QuickSearches);
        }

        RefreshUserQuickSearches();
    }

    private void RefreshUserQuickSearches()
    {
        if (UserQuickSearchesPanel is null)
            return;

        UserQuickSearchesPanel.Children.Clear();
        var items = QuickSearches;
        QuickSearchesEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < items.Count; i++)
            UserQuickSearchesPanel.Children.Add(BuildQuickSearchRow(items[i], i, items.Count));
    }

    /// <summary>
    /// Opens the definition controls in a flyout centered under the control that was clicked — Add, Save
    /// current options, or a row's pencil. Editing in place used to push the list around instead.
    /// </summary>
    private void ShowQuickSearchEditorFlyout(
        object sender, QuickSearchItem draft, string title, bool showOptionCaptureButtons)
    {
        if (sender is not FrameworkElement anchor)
            return;

        const double contentWidth = 460;

        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
            ShouldConstrainToRootBounds = false,
        };

        // The default FlyoutPresenter caps content at 456px, which clips this editor on the right.
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, contentWidth + 48));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 720d));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenter.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        flyout.FlyoutPresenterStyle = presenter;

        var body = BuildQuickSearchEditorPanel(draft, flyout.Hide, showOptionCaptureButtons);

        var panel = new StackPanel { Spacing = 10, Width = contentWidth };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        });
        panel.Children.Add(body);

        flyout.Content = panel;

        // Anchoring to a row action would drop the flyout over the row it belongs to, so anchor to the row
        // itself and shift it horizontally until it is centered on the icon that was clicked.
        if (FindQuickSearchRow(anchor) is { } row && row.ActualWidth > 0)
        {
            double iconCenterX = anchor
                .TransformToVisual(row)
                .TransformPoint(new Windows.Foundation.Point(anchor.ActualWidth / 2, 0)).X;

            flyout.ShowAt(row, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Bottom,
                Position = new Windows.Foundation.Point(
                    iconCenterX - ((contentWidth + 32) / 2), row.ActualHeight + 4),
            });
            return;
        }

        flyout.ShowAt(anchor);
    }

    /// <summary>The quick-search row containing <paramref name="start"/>, or null when it is not in a row.</summary>
    private FrameworkElement? FindQuickSearchRow(DependencyObject start)
    {
        DependencyObject? node = start;
        while (node is not null)
        {
            DependencyObject? parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            if (ReferenceEquals(parent, UserQuickSearchesPanel))
                return node as FrameworkElement;
            node = parent;
        }
        return null;
    }

    private Grid BuildQuickSearchRow(QuickSearchItem item, int index, int count)
    {
        var grid = new Grid();

        var content = new Grid { ColumnSpacing = 8 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        content.Children.Add(new FontIcon
        {
            Glyph = item.Glyph,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var label = new TextBlock
        {
            Text = item.Label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (item.Semantic)
            badges.Children.Add(new TextBlock { Text = "Semantic", FontSize = 10, Opacity = 0.7 });
        if (item.HasOptions)
        {
            var captured = new FontIcon { Glyph = "\uE713", FontSize = 12, Opacity = 0.7 };
            ToolTipService.SetToolTip(captured, "Restores the Advanced Options captured when this was saved.");
            badges.Children.Add(captured);
        }
        Grid.SetColumn(badges, 2);
        content.Children.Add(badges);

        var run = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // Right padding reserves the actions lane inside the button's own border, so the icons sit in a
            // fixed column and a long label ellipsizes instead of pushing them around.
            Padding = new Thickness(11, 5, QuickSearchActionsLaneWidth, 6),
            Tag = item.Id,
        };
        run.Click += OnRunQuickSearchItem;
        ToolTipService.SetToolTip(run, DescribeQuickSearch(item));
        grid.Children.Add(run);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            // Right-aligned siblings added after the button, so they draw on top of it, inside its border.
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
            // Hidden (not collapsed) so the row's width never shifts when the pointer enters.
            Opacity = 0,
            IsHitTestVisible = false,
        };

        actions.Children.Add(BuildQuickSearchAction("\uE70E", "Move up", item.Id, OnMoveQuickSearchUp, index > 0));
        actions.Children.Add(BuildQuickSearchAction("\uE70D", "Move down", item.Id, OnMoveQuickSearchDown, index < count - 1));
        actions.Children.Add(BuildQuickSearchAction("\uE70F", "Edit", item.Id, OnEditQuickSearch, true));
        actions.Children.Add(BuildQuickSearchAction("\uE74D", "Delete", item.Id, OnDeleteQuickSearch, true));
        grid.Children.Add(actions);

        grid.PointerEntered += (_, _) => SetQuickSearchActionsVisible(actions, true);
        grid.PointerExited += (_, _) => SetQuickSearchActionsVisible(actions, false);
        return grid;
    }

    private static void SetQuickSearchActionsVisible(StackPanel actions, bool visible)
    {
        actions.Opacity = visible ? 1 : 0;
        actions.IsHitTestVisible = visible;
    }

    /// <summary>Row hover text: what it searches for, and — because an unset folder means the whole
    /// machine — where it searches.</summary>
    private static string DescribeQuickSearch(QuickSearchItem item)
    {
        string what = string.IsNullOrWhiteSpace(item.Tooltip) ? item.Pattern : item.Tooltip;
        string where = item.SearchesAllDrives
            ? "Searches every drive from its root."
            : $"Searches {item.Directory}";
        return $"{what}\n\n{where}";
    }

    private static Button BuildQuickSearchAction(
        string glyph, string tooltip, string id, RoutedEventHandler handler, bool enabled)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 28,
            Height = 28,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = null,
            BorderThickness = new Thickness(0),
            Tag = id,
            IsEnabled = enabled,
        };
        button.Click += handler;
        ToolTipService.SetToolTip(button, tooltip);
        return button;
    }

    /// <summary>
    /// The quick-search definition controls. <paramref name="onClosed"/> runs after Save or Cancel so the
    /// hosting flyout can dismiss itself. <paramref name="showOptionCaptureButtons"/> adds Recapture/Clear,
    /// which only apply to an entry that already exists.
    /// </summary>
    private StackPanel BuildQuickSearchEditorPanel(
        QuickSearchItem item, Action? onClosed, bool showOptionCaptureButtons)
    {
        var editing = item.Clone();
        if (string.IsNullOrEmpty(editing.Glyph))
            editing.Glyph = QuickSearchItem.DefaultGlyph;

        var labelBox = new TextBox { Header = "Name", PlaceholderText = "Name", Text = editing.Label, Width = 240 };
        var patternBox = new TextBox
        {
            Header = "Search pattern",
            PlaceholderText = "Search pattern",
            Text = editing.Pattern,
        };

        var glyphPreview = new FontIcon { Glyph = editing.Glyph, FontSize = 16 };
        var glyphButton = new Button
        {
            Content = glyphPreview,
            Width = 40,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTipService.SetToolTip(glyphButton, "Choose the icon shown on this quick search");
        glyphButton.Flyout = BuildQuickSearchGlyphPicker(chosen =>
        {
            editing.Glyph = chosen;
            glyphPreview.Glyph = chosen;
        });
        var glyphField = new StackPanel { Spacing = 4 };
        // Matches a TextBox Header: default size and foreground, so "Icon" reads like "Name" beside it.
        glyphField.Children.Add(new TextBlock { Text = "Icon" });
        glyphField.Children.Add(glyphButton);

        var modeBox = new ComboBox { Header = "Mode", MinWidth = 130 };
        modeBox.Items.Add(new ComboBoxItem { Content = "Traditional" });
        modeBox.Items.Add(new ComboBoxItem { Content = "Semantic" });
        modeBox.SelectedIndex = editing.Semantic ? 1 : 0;

        var regex = new CheckBox { Content = "Regex", IsChecked = editing.UseRegex, MinWidth = 0 };
        var matchCase = new CheckBox { Content = "Case", IsChecked = editing.CaseSensitive, MinWidth = 0 };
        var multiline = new CheckBox { Content = "Multiline", IsChecked = editing.Multiline, MinWidth = 0 };
        var exact = new CheckBox { Content = "Exact", IsChecked = editing.ExactMatch, MinWidth = 0 };

        void SyncOptionAvailability()
        {
            bool semantic = modeBox.SelectedIndex == 1;
            foreach (var box in new[] { regex, matchCase, multiline, exact })
                box.IsEnabled = !semantic;
        }
        modeBox.SelectionChanged += (_, _) => SyncOptionAvailability();
        // Multiline is regex-only in the search box; keep the saved item consistent with that.
        multiline.Checked += (_, _) => regex.IsChecked = true;
        SyncOptionAvailability();

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        toggles.Children.Add(regex);
        toggles.Children.Add(matchCase);
        toggles.Children.Add(multiline);
        toggles.Children.Add(exact);

        var nameRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        nameRow.Children.Add(glyphField);
        nameRow.Children.Add(labelBox);
        nameRow.Children.Add(modeBox);

        var directoryBox = new TextBox
        {
            Header = "Search in folder",
            PlaceholderText = "All drives",
            Text = editing.Directory,
        };
        var browseButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE838", FontSize = 14 },
            Width = 36,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        ToolTipService.SetToolTip(browseButton, "Browse for a folder");
        // Anchored in-app browser: a modal Win32 folder dialog would light-dismiss the drawer behind it.
        browseButton.Flyout = UI.FolderBrowseFlyout.Create(
            () => directoryBox.Text,
            picked => directoryBox.Text = picked);

        var directoryRow = new Grid { ColumnSpacing = 6 };
        directoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        directoryRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        directoryRow.Children.Add(directoryBox);
        Grid.SetColumn(browseButton, 1);
        directoryRow.Children.Add(browseButton);

        var directoryHint = new TextBlock
        {
            Text = "Leave empty to search every drive from its root.",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var capturedText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var captureButton = new Button { MinWidth = 0, Padding = new Thickness(10, 3, 10, 3), FontSize = 12 };
        var clearCaptureButton = new Button
        {
            Content = "Clear",
            MinWidth = 0,
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 12,
        };
        ToolTipService.SetToolTip(clearCaptureButton, "Forget the captured options; running this leaves Advanced Options untouched.");

        void SyncCaptureRow()
        {
            bool has = editing.Options is not null;
            capturedText.Text = has
                ? "Advanced Options captured \u2014 running this restores all of them."
                : "No Advanced Options captured \u2014 running this leaves the drawer as-is.";
            captureButton.Content = has ? "Recapture" : "Capture current";
            clearCaptureButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        }
        captureButton.Click += (_, _) =>
        {
            editing.Options = ViewModel.CaptureAdvancedOptions();
            SyncCaptureRow();
        };
        clearCaptureButton.Click += (_, _) =>
        {
            editing.Options = null;
            SyncCaptureRow();
        };
        SyncCaptureRow();

        var captureRow = new Grid { ColumnSpacing = 8 };
        captureRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        captureRow.Children.Add(capturedText);
        // Recapturing or clearing only makes sense for an entry that already exists, so those controls
        // belong to the per-row inline editor, not the add flyout.
        if (showOptionCaptureButtons)
        {
            captureRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            captureRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(captureButton, 1);
            captureRow.Children.Add(captureButton);
            Grid.SetColumn(clearCaptureButton, 2);
            captureRow.Children.Add(clearCaptureButton);
        }

        var save = new Button { Content = "Save", MinWidth = 0, Padding = new Thickness(12, 4, 12, 4) };
        var cancel = new Button { Content = "Cancel", MinWidth = 0, Padding = new Thickness(12, 4, 12, 4) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        save.Click += async (_, _) =>
        {
            editing.Label = labelBox.Text;
            editing.Pattern = patternBox.Text;
            editing.Directory = directoryBox.Text;
            editing.Semantic = modeBox.SelectedIndex == 1;
            editing.UseRegex = regex.IsChecked == true;
            editing.CaseSensitive = matchCase.IsChecked == true;
            editing.Multiline = multiline.IsChecked == true;
            editing.ExactMatch = exact.IsChecked == true;

            if (!QuickSearchCatalog.Upsert(QuickSearches, editing))
            {
                patternBox.Focus(FocusState.Programmatic);
                return;
            }

            RefreshUserQuickSearches();
            onClosed?.Invoke();
            await ViewModel.PersistSettingsAsync();
        };

        cancel.Click += (_, _) => onClosed?.Invoke();

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(nameRow);
        panel.Children.Add(patternBox);
        panel.Children.Add(toggles);
        panel.Children.Add(directoryRow);
        panel.Children.Add(directoryHint);
        panel.Children.Add(captureRow);
        panel.Children.Add(buttons);

        patternBox.Loaded += (_, _) => labelBox.Focus(FocusState.Programmatic);
        return panel;
    }

    /// <summary>A grid of Segoe Fluent Icons to pick from, so the icon is never typed as a raw codepoint.</summary>
    private static Flyout BuildQuickSearchGlyphPicker(Action<string> onChosen)
    {
        const int columns = 6;
        var grid = new Grid { RowSpacing = 2, ColumnSpacing = 2 };
        for (int c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var flyout = new Flyout();
        for (int i = 0; i < QuickSearchGlyphChoices.Length; i++)
        {
            int row = i / columns;
            if (row >= grid.RowDefinitions.Count)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            string glyph = QuickSearchGlyphChoices[i];
            var choice = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 16 },
                Width = 36,
                Height = 36,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                Background = null,
                BorderThickness = new Thickness(0),
            };
            choice.Click += (_, _) =>
            {
                onChosen(glyph);
                flyout.Hide();
            };
            Grid.SetRow(choice, row);
            Grid.SetColumn(choice, i % columns);
            grid.Children.Add(choice);
        }

        flyout.Content = grid;
        return flyout;
    }

    private async void OnRunQuickSearchItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
            return;
        int index = QuickSearchCatalog.IndexOf(QuickSearches, id);
        if (index < 0)
            return;

        ViewModel.ApplyQuickSearchItem(QuickSearches[index]);
        await StartSearchFromUiAsync();
    }

    private void OnAddQuickSearch(object sender, RoutedEventArgs e)
        => ShowQuickSearchEditorFlyout(
            sender,
            new QuickSearchItem { Id = QuickSearchCatalog.NewId(), UseRegex = false },
            "Add new quick search",
            showOptionCaptureButtons: false);

    /// <summary>
    /// Seeds a new quick search from the live search box and the whole Advanced Options drawer, then opens
    /// the editor flyout so it can be named. The snapshot is read from the view model, not the settings
    /// file, so options the user changed but never saved as defaults are captured too.
    /// </summary>
    private void OnSaveCurrentOptionsAsQuickSearch(object sender, RoutedEventArgs e)
    {
        string pattern = (ViewModel.Query ?? string.Empty).Trim();
        ShowQuickSearchEditorFlyout(
            sender,
            new QuickSearchItem
            {
                Id = QuickSearchCatalog.NewId(),
                Label = pattern,
                Pattern = pattern,
                Directory = ViewModel.Directory ?? string.Empty,
                Semantic = ViewModel.IsSemanticQueryMode,
                UseRegex = ViewModel.UseRegex,
                CaseSensitive = ViewModel.CaseSensitive,
                Multiline = ViewModel.Multiline,
                ExactMatch = ViewModel.ExactMatch,
                Options = ViewModel.CaptureAdvancedOptions(),
            },
            "Add new quick search",
            showOptionCaptureButtons: false);
    }

    private void OnEditQuickSearch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
            return;
        int index = QuickSearchCatalog.IndexOf(QuickSearches, id);
        if (index < 0)
            return;

        // Recapture/Clear are offered here because an existing entry may already carry a snapshot.
        ShowQuickSearchEditorFlyout(
            sender, QuickSearches[index], "Edit quick search", showOptionCaptureButtons: true);
    }

    private async void OnDeleteQuickSearch(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
            return;
        if (!QuickSearchCatalog.Remove(QuickSearches, id))
            return;

        RefreshUserQuickSearches();
        await ViewModel.PersistSettingsAsync();
    }

    private async void OnMoveQuickSearchUp(object sender, RoutedEventArgs e) => await MoveQuickSearchAsync(sender, -1);

    private async void OnMoveQuickSearchDown(object sender, RoutedEventArgs e) => await MoveQuickSearchAsync(sender, 1);

    private async Task MoveQuickSearchAsync(object sender, int delta)
    {
        if ((sender as FrameworkElement)?.Tag is not string id)
            return;
        if (!QuickSearchCatalog.Move(QuickSearches, id, delta))
            return;

        RefreshUserQuickSearches();
        await ViewModel.PersistSettingsAsync();
    }
}
