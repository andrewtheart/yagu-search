using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using Yagu.Services;
using Yagu.Services.Logging;
namespace Yagu;

/// <summary>
/// Explorer context-menu registration, extension menu commands, and skip-count overlay controls.
/// </summary>
public sealed partial class MainWindow
{
    // ── First-run context menu prompt ──────────────────────────────────

    private async Task CheckFirstRunContextMenuAsync()
    {
        if (ViewModel.HasCompletedFirstRun)
            return;

        // Mark first run complete regardless of what the user chooses
        ViewModel.HasCompletedFirstRun = true;
        await ViewModel.PersistSettingsAsync();

        // If context menu is already registered, nothing to do
        if (_preparedContextMenuRegistered ?? IsContextMenuRegistered())
            return;

        if (await YaguDialog.ShowAsync(
            _hwnd,
            new YaguDialogOptions
            {
                Title = "Add Explorer Context Menu?",
                TitleGlyph = "\uEC50", // File Explorer
                Content = "Would you like to add a \"Search with Yagu\" option to the Windows Explorer right-click menu?\n\nThis lets you quickly search any folder by right-clicking it.",
                PrimaryButtonText = "Yes, add it",
                CloseButtonText = "No thanks",
                DefaultButton = YaguDialogDefaultButton.Primary,
                Width = 560,
                Height = 300,
            }) != YaguDialogResult.Primary)
            return;

        try
        {
            RegisterContextMenu();

            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Context Menu Installed",
                    TitleGlyph = "\uE930", // Completed
                    Content = "The \"Search with Yagu\" context menu has been added.\n\nTo use it: right-click any folder in Windows Explorer and select \"Search with Yagu\". Yagu will open with that folder ready to search.",
                    CloseButtonText = "OK",
                    DefaultButton = YaguDialogDefaultButton.Close,
                    Width = 560,
                    Height = 320,
                });
        }
        catch (Exception ex)
        {
            YaguLog.For("ContextMenu").LogWarning(ex, "Failed to register context menu");

            await YaguDialog.ShowAsync(
                _hwnd,
                new YaguDialogOptions
                {
                    Title = "Context Menu Registration Failed",
                    TitleGlyph = "\uEA39", // Error badge
                    Content = $"Could not register the context menu entry:\n{ex.Message}",
                    CloseButtonText = "OK",
                    DefaultButton = YaguDialogDefaultButton.Close,
                    Width = 560,
                    Height = 300,
                });
        }
    }

    private static bool IsContextMenuRegistered() => ExplorerContextMenu.IsRegistered();

    private static void RegisterContextMenu() => ExplorerContextMenu.Register();

    // ── Skip-extensions dropdown ──────────────────────────────────
    private void OnSkipExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnSkipExtensionToggled();

    private void OnSkipExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.SkipExtensionItems) item.IsEnabled = true;
        ViewModel.OnSkipExtensionToggled();
    }

    private void OnSkipExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.SkipExtensionItems) item.IsEnabled = false;
        ViewModel.OnSkipExtensionToggled();
    }

    // ── Binary-extensions dropdown ───────────────────────────────
    private void OnBinaryExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnBinaryExtensionToggled();

    private void OnBinaryExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.BinaryExtensionItems) item.IsEnabled = true;
        ViewModel.OnBinaryExtensionToggled();
    }

    private void OnBinaryExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.BinaryExtensionItems) item.IsEnabled = false;
        ViewModel.OnBinaryExtensionToggled();
    }

    // ── Archive-extensions dropdown ───────────────────────────────
    private void OnArchiveExtToggled(object sender, RoutedEventArgs e) => ViewModel.OnArchiveExtensionToggled();

    private void OnArchiveExtSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.ArchiveExtensionItems) item.IsEnabled = true;
        ViewModel.OnArchiveExtensionToggled();
    }

    private void OnArchiveExtSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var item in ViewModel.ArchiveExtensionItems) item.IsEnabled = false;
        ViewModel.OnArchiveExtensionToggled();
    }

    // ── Skip-count breakdown overlay ─────────────────────────────
    private bool _resultsPaneCollapsed;
    private int _resultsPaneExpandedWindowHeight;

    private void OnToggleResultsPane(object sender, RoutedEventArgs e)
    {
        _resultsPaneCollapsed = !_resultsPaneCollapsed;

        if (_resultsPaneCollapsed)
        {
            _resultsPaneExpandedWindowHeight = AppWindow?.Size.Height ?? 0;
            SplitPaneRow.Height = new GridLength(0);
            ProgressRow.Height = new GridLength(0);
            SplitPaneGrid.Visibility = Visibility.Collapsed;
        }
        else
        {
            SplitPaneRow.Height = new GridLength(1, GridUnitType.Star);
            ProgressRow.Height = GridLength.Auto;
            SplitPaneGrid.Visibility = Visibility.Visible;
        }

        UpdateBottomStatusBarVisibility();

        if (_resultsPaneCollapsed)
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FitWindowHeightToVisibleContent);
        else
            RestoreResultsPaneExpandedWindowHeight();
    }

    private void FitWindowHeightToVisibleContent()
    {
        try
        {
            if (AppWindow is null) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;

            double scale = (Content?.XamlRoot?.RasterizationScale) ?? 1.0;
            double measureWidthDip = AppWindow.ClientSize.Width > 0
                ? AppWindow.ClientSize.Width / scale
                : Math.Max(1, RootGrid.ActualWidth);

            RootGrid.UpdateLayout();
            RootGrid.Measure(new Windows.Foundation.Size(measureWidthDip, double.PositiveInfinity));

            int chromeHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
            int desiredHeight = (int)Math.Ceiling((Math.Max(MinimumLauncherHeightDip, RootGrid.DesiredSize.Height) + 2) * scale) + chromeHeight;
            if (desiredHeight >= AppWindow.Size.Height - 4) return;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var wa = displayArea?.WorkArea ?? default;
            int maxHeight = wa.Height > 0 ? Math.Max(0, wa.Y + wa.Height - AppWindow.Position.Y) : desiredHeight;
            if (maxHeight > 0)
                desiredHeight = Math.Min(desiredHeight, maxHeight);

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                desiredHeight));
        }
        catch { }
    }

    private void RestoreResultsPaneExpandedWindowHeight()
    {
        try
        {
            if (AppWindow is null || _resultsPaneExpandedWindowHeight <= 0) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Maximized }) return;
            if (AppWindow.Size.Height >= _resultsPaneExpandedWindowHeight - 4) return;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            var wa = displayArea?.WorkArea ?? default;
            int restoredHeight = _resultsPaneExpandedWindowHeight;
            if (wa.Height > 0)
                restoredHeight = Math.Min(restoredHeight, Math.Max(0, wa.Y + wa.Height - AppWindow.Position.Y));

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                restoredHeight));
        }
        catch { }
    }

    /// <summary>Grace period so the pointer can cross the gap from the icon into the overlay.</summary>
    private const int SkipBreakdownHoverHideDelayMs = 220;

    private bool _skipBreakdownPinned;
    private bool _skipBreakdownPointerOverIcon;
    private bool _skipBreakdownPointerOverPanel;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _skipBreakdownHoverHideTimer;

    private void OnSkipInfoClicked(object sender, RoutedEventArgs e)
    {
        _skipBreakdownPinned = !_skipBreakdownPinned;
        if (_skipBreakdownPinned)
            ShowSkipBreakdownOverlay();
        else
            HideSkipBreakdownOverlay();
    }

    private void OnSkipInfoPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _skipBreakdownPointerOverIcon = true;
        CancelSkipBreakdownHoverHide();
        ShowSkipBreakdownOverlay();
    }

    private void OnSkipInfoPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _skipBreakdownPointerOverIcon = false;
        ScheduleSkipBreakdownHoverHide();
    }

    private void OnSkipBreakdownOverlayPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _skipBreakdownPointerOverPanel = true;
        CancelSkipBreakdownHoverHide();
    }

    private void OnSkipBreakdownOverlayPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _skipBreakdownPointerOverPanel = false;
        ScheduleSkipBreakdownHoverHide();
    }

    private void OnSkipBreakdownCloseClicked(object sender, RoutedEventArgs e) => HideSkipBreakdownOverlay();

    private void ShowSkipBreakdownOverlay()
    {
        RenderSkipBreakdown();
        SkipBreakdownOverlay.Visibility = Visibility.Visible;
        PositionSkipBreakdownOverlay();
    }

    /// <summary>
    /// Rebuilds the breakdown table. Every category shares one Grid's columns so the glyph, label and
    /// count line up exactly — a monospaced font cannot do that here because the category emoji (several
    /// carrying a variation selector) render at different advance widths. The headline total is a
    /// separate summary section below a divider, sharing the same columns so it stays aligned.
    /// </summary>
    private void RenderSkipBreakdown()
    {
        if (SkipBreakdownContent is null)
            return;

        SkipBreakdownContent.Children.Clear();
        var entries = ViewModel.SkipBreakdownEntries;
        int total = ViewModel.SkipTotalCount;

        if (total == 0)
        {
            SkipBreakdownContent.Children.Add(new TextBlock { Text = "No files skipped", FontSize = 12 });
        }
        else
        {
            SkipBreakdownContent.Children.Add(new TextBlock
            {
                Text = "Skipped files breakdown",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            SkipBreakdownContent.Children.Add(BuildSkipTable(entries, total, SkipBreakdownOverlay.BorderBrush));
        }

        var discovery = ViewModel.SkipDiscoveryEntries;
        if (discovery.Count > 0)
        {
            SkipBreakdownContent.Children.Add(new TextBlock
            {
                Text = "Filtered during discovery (not counted above)",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
            });
            SkipBreakdownContent.Children.Add(BuildSkipTable(discovery, total: null, SkipBreakdownOverlay.BorderBrush));
        }
    }

    /// <summary>Builds one aligned table; a non-null <paramref name="total"/> appends the summary section.</summary>
    private static Grid BuildSkipTable(
        IReadOnlyList<ViewModels.MainViewModel.SkipBreakdownEntry> entries,
        int? total,
        Microsoft.UI.Xaml.Media.Brush? dividerBrush)
    {
        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;
        foreach (var entry in entries)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddSkipCells(grid, row++, entry.Glyph, entry.Label, entry.Count, emphasized: false);
        }

        if (total is not { } totalCount)
            return grid;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var divider = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Background = dividerBrush,
        };
        Grid.SetRow(divider, row);
        Grid.SetColumnSpan(divider, 3);
        grid.Children.Add(divider);
        row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddSkipCells(grid, row, glyph: string.Empty, label: "Total skipped", count: totalCount, emphasized: true);
        return grid;
    }

    private static void AddSkipCells(Grid grid, int row, string glyph, string label, int count, bool emphasized)
    {
        var weight = emphasized ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        if (!string.IsNullOrEmpty(glyph))
        {
            var glyphText = new TextBlock { Text = glyph, FontSize = 12, TextAlignment = TextAlignment.Center };
            Grid.SetRow(glyphText, row);
            grid.Children.Add(glyphText);
        }

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 12,
            FontWeight = weight,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(labelText, row);
        Grid.SetColumn(labelText, 1);
        grid.Children.Add(labelText);

        var countText = new TextBlock
        {
            Text = count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            FontSize = 12,
            FontWeight = weight,
            // Tabular digits keep the right-aligned counts on a common decimal grid.
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetRow(countText, row);
        Grid.SetColumn(countText, 2);
        grid.Children.Add(countText);
    }

    private void HideSkipBreakdownOverlay()
    {
        CancelSkipBreakdownHoverHide();
        _skipBreakdownPinned = false;
        _skipBreakdownPointerOverIcon = false;
        _skipBreakdownPointerOverPanel = false;
        SkipBreakdownOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Centers the overlay directly beneath the skipped-files icon. The overlay spans every row, so its
    /// margin is measured from the window's top-left corner and is clamped to keep it fully on-screen.
    /// </summary>
    private void PositionSkipBreakdownOverlay()
    {
        if (SkippedInfoButton.Visibility != Visibility.Visible || SkippedInfoButton.ActualWidth <= 0)
            return;

        // The overlay was just shown, so measure it before reading its width for the centering math.
        SkipBreakdownOverlay.UpdateLayout();
        double overlayWidth = SkipBreakdownOverlay.ActualWidth > 0
            ? SkipBreakdownOverlay.ActualWidth
            : SkipBreakdownOverlay.DesiredSize.Width;
        if (overlayWidth <= 0)
        {
            // Layout has not produced a width yet; retry once the pass completes so the first show is
            // still anchored instead of landing at a stale position.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (SkipBreakdownOverlay.Visibility == Visibility.Visible)
                    PositionSkipBreakdownOverlay();
            });
            return;
        }

        var anchor = SkippedInfoButton.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
        double left = anchor.X + (SkippedInfoButton.ActualWidth / 2) - (overlayWidth / 2);
        double top = anchor.Y + SkippedInfoButton.ActualHeight + 6;
        double maxLeft = Math.Max(8, RootGrid.ActualWidth - overlayWidth - 8);
        SkipBreakdownOverlay.Margin = new Thickness(Math.Clamp(left, 8, maxLeft), top, 0, 0);
    }

    private void ScheduleSkipBreakdownHoverHide()
    {
        if (_skipBreakdownPinned)
            return;

        CancelSkipBreakdownHoverHide();
        var timer = DispatcherQueue.CreateTimer();
        _skipBreakdownHoverHideTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(SkipBreakdownHoverHideDelayMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Ignore a stale queued tick from an older pointer transition.
            if (!ReferenceEquals(_skipBreakdownHoverHideTimer, timer))
                return;
            _skipBreakdownHoverHideTimer = null;
            if (!_skipBreakdownPinned && !_skipBreakdownPointerOverIcon && !_skipBreakdownPointerOverPanel)
                SkipBreakdownOverlay.Visibility = Visibility.Collapsed;
        };
        timer.Start();
    }

    private void CancelSkipBreakdownHoverHide()
    {
        _skipBreakdownHoverHideTimer?.Stop();
        _skipBreakdownHoverHideTimer = null;
    }
}
