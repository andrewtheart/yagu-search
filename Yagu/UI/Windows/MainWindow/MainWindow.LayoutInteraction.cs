using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
namespace Yagu;

/// <summary>
/// Advanced-options layout synchronization and splitter interaction.
/// </summary>
public sealed partial class MainWindow
{
    private bool _splitterDragging;
    private double _splitterStartX;
    private double _col0StartWidth;
    private double _col2StartWidth;
    private bool _topSearchDrawerCompact;
    private const double CompactTopSearchDrawerThreshold = 440;
    private const double CompactTopSearchActionButtonWidth = 38;

    // Vertical gap between the floating search drawer (search card + status panel)
    // and the results panel beneath it in PreviewTopExpanded mode. Kept equal to the
    // normal stacked layout's gap (4px search-card bottom margin + 2px split-pane top
    // margin) so the gap stays a fixed size and does not visibly grow when the preview
    // panel is revealed for the first time and the panels reorganize.
    private const double PreviewTopExpandedDrawerGap = 6;

    // Minimum usable height for the Advanced Options drawer content before a
    // scrollbar is preferred over shrinking further.
    private const double MinAdvancedOptionsDrawerHeight = 160;

    private void InitializeAdvancedOptionsDrawerStateTracking()
    {
        // The drawer body lives in a Flyout (AdvancedOptionsFlyout) so it drops over the desktop
        // without ever growing the window. Keep the body bounded to the visible screen so very tall
        // option sets scroll internally instead of running off-screen.
        if (AdvancedOptionsScrollViewer.Content is FrameworkElement drawerContent)
            drawerContent.SizeChanged += (_, _) => UpdateAdvancedOptionsDrawerMaxHeight();
    }

    /// <summary>True while the Advanced Options flyout drawer is open.</summary>
    private bool IsAdvancedOptionsDrawerOpen => AdvancedOptionsFlyout?.IsOpen == true;

    private void OnAdvancedOptionsFlyoutOpened(object? sender, object e)
    {
        AdvancedOptionsExpandGlyph.Glyph = "\uE70E"; // chevron up
        SyncAdvancedOptionsDrawerWidth();
        UpdateAdvancedOptionsDrawerMaxHeight();
    }

    private void OnAdvancedOptionsFlyoutClosed(object? sender, object e)
    {
        AdvancedOptionsExpandGlyph.Glyph = "\uE70D"; // chevron down
    }

    /// <summary>
    /// Sizes the flyout drawer to half the search card's bottom action bar width, left-aligned under
    /// the "Advanced Options" toggle.
    /// </summary>
    private void SyncAdvancedOptionsDrawerWidth()
    {
        if (AdvancedOptionsScrollViewer is null || SearchCardBottomBar is null) return;
        double width = SearchCardBottomBar.ActualWidth * 0.5;
        if (width > 0)
            AdvancedOptionsScrollViewer.Width = width;
    }

    /// <summary>
    /// Bounds the flyout drawer body to the available screen height so very tall option sets show an
    /// internal scrollbar instead of running off the monitor. The flyout is its own visual root, so
    /// the limit is derived from the monitor work area rather than the window. No-op while closed.
    /// </summary>
    private void UpdateAdvancedOptionsDrawerMaxHeight()
    {
        if (AdvancedOptionsScrollViewer is null)
            return;

        if (!IsAdvancedOptionsDrawerOpen)
        {
            AdvancedOptionsScrollViewer.MaxHeight = double.PositiveInfinity;
            return;
        }

        try
        {
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;

            var displayArea = AppWindow is null
                ? null
                : Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                    AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            double workAreaHeightDip = displayArea is null ? 0 : displayArea.WorkArea.Height / scale;

            // Leave room for the toggle row + flyout chrome so the drawer never spans the whole screen.
            double maxHeight = workAreaHeightDip > 0
                ? workAreaHeightDip - 120
                : MinAdvancedOptionsDrawerHeight;
            AdvancedOptionsScrollViewer.MaxHeight =
                maxHeight > MinAdvancedOptionsDrawerHeight ? maxHeight : MinAdvancedOptionsDrawerHeight;
        }
        catch
        {
            AdvancedOptionsScrollViewer.MaxHeight = double.PositiveInfinity;
        }
    }

    private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
    {
        var border = (Border)sender;
        _splitterDragging = true;
        _splitterStartX = e.GetCurrentPoint(SplitPaneGrid).Position.X;
        _col0StartWidth = SplitPaneGrid.ColumnDefinitions[0].ActualWidth;
        _col2StartWidth = SplitPaneGrid.ColumnDefinitions[2].ActualWidth;
        border.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        double currentX = e.GetCurrentPoint(SplitPaneGrid).Position.X;
        double delta = currentX - _splitterStartX;
        double newCol0 = _col0StartWidth + delta;
        double newCol2 = _col2StartWidth - delta;
        double minWidth = 200;
        if (newCol0 < minWidth || newCol2 < minWidth) return;
        SplitPaneGrid.ColumnDefinitions[0].Width = new GridLength(newCol0, GridUnitType.Pixel);
        SplitPaneGrid.ColumnDefinitions[2].Width = new GridLength(newCol2, GridUnitType.Pixel);
        UpdateTopExpandedPreviewMeasurements();
        e.Handled = true;
    }

    private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging) return;
        _splitterDragging = false;
        ((Border)sender).ReleasePointerCapture(e.Pointer);
        // Convert back to star sizing so the layout adapts on window resize.
        double col0 = SplitPaneGrid.ColumnDefinitions[0].ActualWidth;
        double col2 = SplitPaneGrid.ColumnDefinitions[2].ActualWidth;
        double total = col0 + col2;
        if (total > 0)
        {
            SplitPaneGrid.ColumnDefinitions[0].Width = new GridLength(col0 / total, GridUnitType.Star);
            SplitPaneGrid.ColumnDefinitions[2].Width = new GridLength(col2 / total, GridUnitType.Star);
        }
        e.Handled = true;
        UpdateTopExpandedPreviewMeasurements();
        QueueActiveMatchOverlayRefresh();
    }

    private void ApplyTopSearchDrawerCompactState(bool compact)
    {
        _topSearchDrawerCompact = compact;
        BrowseDirectoryLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchCancelLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchSplitLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        if (compact)
        {
            BrowseDirectoryButton.Padding = new Thickness(0);
            SearchCardLoadSessionButton.Width = CompactTopSearchActionButtonWidth;
            SearchCardLoadSessionButton.Height = CompactTopSearchActionButtonWidth;
            SearchCancelButton.Padding = new Thickness(0);
            BrowseDirectoryButton.Width = CompactTopSearchActionButtonWidth;
            SearchCancelButton.Width = CompactTopSearchActionButtonWidth;
            return;
        }

        SearchCardLoadSessionButton.Width = 32;
        SearchCardLoadSessionButton.Height = 32;
        BrowseDirectoryButton.Padding = new Thickness(8, 6, 8, 6);
        SearchCancelButton.Padding = new Thickness(12, 6, 12, 6);
        SearchCancelButton.Width = double.NaN;
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SplitterBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Microsoft.UI.Colors.Gray);
        SplitterBorder.Opacity = 0.5;
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_splitterDragging)
        {
            SplitterBorder.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Transparent);
            SplitterBorder.Opacity = 1.0;
        }
    }
}
