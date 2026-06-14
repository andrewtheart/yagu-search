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
    private bool _advancedOptionsDrawerExpandedWidth;
    private const double CompactTopSearchDrawerThreshold = 440;
    private const double CompactTopSearchActionButtonWidth = 38;

    // Minimum usable height for the Advanced Options drawer content before a
    // scrollbar is preferred over shrinking further.
    private const double MinAdvancedOptionsDrawerHeight = 160;
    // Space reserved below the drawer in full-window mode so the bottom status
    // bar stays visible when the drawer is tall.
    private const double FullModeDrawerBottomReserve = 48;
    // Space reserved below the drawer in launcher mode for window chrome + a gap,
    // so the auto-sized launcher window never extends past the monitor work area.
    private const double LauncherDrawerBottomReserve = 36;

    private void InitializeAdvancedOptionsDrawerStateTracking()
    {
        AdvancedOptionsExpander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, (_, _) =>
        {
            if (!AdvancedOptionsExpander.IsExpanded)
                SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: false);
        });
    }

    private void OnAdvancedOptionsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: true);
        UpdateAdvancedOptionsDrawerMaxHeight();
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    private void OnAdvancedOptionsCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: false);
        UpdateAdvancedOptionsDrawerMaxHeight();
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    private void SetAdvancedOptionsDrawerExpandedWidthState(bool isExpanded)
    {
        _advancedOptionsDrawerExpandedWidth = isExpanded;
        bool shouldFillSearchCardWidth = isExpanded || _terminalPaneExpanded;
        Grid.SetColumnSpan(AdvancedOptionsExpander, shouldFillSearchCardWidth ? 2 : 1);
        AdvancedOptionsExpander.HorizontalAlignment = shouldFillSearchCardWidth || ViewModel.AdvancedOptionsCollapsedWidthModeIndex == 0
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        AdvancedOptionsExpander.Width = double.NaN;
        AdvancedOptionsExpander.InvalidateMeasure();
        UpdateTerminalChevronVisibility();
    }

    /// <summary>
    /// Bounds the Advanced Options drawer to the available vertical space so it
    /// shows an internal scrollbar instead of being clipped at short window
    /// heights. No-op (unbounded) while the drawer is collapsed.
    /// </summary>
    private void UpdateAdvancedOptionsDrawerMaxHeight()
    {
        if (AdvancedOptionsScrollViewer is null)
            return;

        if (!AdvancedOptionsExpander.IsExpanded)
        {
            AdvancedOptionsScrollViewer.MaxHeight = double.PositiveInfinity;
            return;
        }

        double topY;
        try
        {
            topY = AdvancedOptionsScrollViewer
                .TransformToVisual(RootGrid)
                .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        }
        catch
        {
            return;
        }

        if (double.IsNaN(topY) || topY < 0)
            return;

        double ceiling = ResolveAdvancedOptionsDrawerCeiling();
        if (ceiling <= 0)
            return;

        double maxHeight = ceiling - topY;
        AdvancedOptionsScrollViewer.MaxHeight =
            maxHeight > MinAdvancedOptionsDrawerHeight ? maxHeight : MinAdvancedOptionsDrawerHeight;
    }

    /// <summary>
    /// Resolves the bottom limit (in DIPs, relative to the window client top) the
    /// drawer content may reach. Full-window mode uses the visible client height;
    /// launcher mode uses the monitor work area since the window auto-sizes.
    /// </summary>
    private double ResolveAdvancedOptionsDrawerCeiling()
    {
        if (!_launcherMode)
        {
            double height = RootGrid.ActualHeight;
            return height > 0 ? height - FullModeDrawerBottomReserve : 0;
        }

        try
        {
            if (AppWindow is null)
                return RootGrid.ActualHeight;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            if (displayArea is null || scale <= 0)
                return RootGrid.ActualHeight;

            double workAreaHeightDip = displayArea.WorkArea.Height / scale;
            return workAreaHeightDip - LauncherDrawerBottomReserve;
        }
        catch
        {
            return RootGrid.ActualHeight;
        }
    }

    /// <summary>
    /// Forces the root grid to re-measure on every frame during the Expander
    /// expand/collapse animation so the split pane resizes in perfect sync.
    /// </summary>
    private void ListenForExpanderLayoutSync()
    {
        var debounce = DispatcherQueue.CreateTimer();
        debounce.Interval = TimeSpan.FromMilliseconds(400);
        debounce.IsRepeating = false;

        void handler(object? s, object? e)
        {
            AdvancedOptionsExpander.InvalidateMeasure();
            RootGrid.UpdateLayout();
            UpdateAdvancedOptionsDrawerMaxHeight();
            UpdateTopExpandedPreviewMeasurements();
        }

        debounce.Tick += (t, a) =>
        {
            debounce.Stop();
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= handler;
            AdvancedOptionsExpander.InvalidateMeasure();
            RootGrid.UpdateLayout();
            UpdateAdvancedOptionsDrawerMaxHeight();
            UpdateTopExpandedPreviewMeasurements();
        };

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += handler;
        debounce.Start();
    }

    /// <summary>
    /// Tracks the expander animation by resizing the window on every
    /// SizeChanged event, keeping content and window perfectly in sync.
    /// A debounce timer detects when the animation has finished and
    /// unsubscribes the handler.
    /// </summary>
    private void ListenForExpanderResize()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;

        void handler(object s, SizeChangedEventArgs e)
        {
            if (_launcherMode) PositionLauncherWindow();
            timer.Stop();
            timer.Start();
        }

        timer.Tick += (t, a) =>
        {
            timer.Stop();
            RootGrid.SizeChanged -= handler;
            if (_launcherMode) PositionLauncherWindow();
        };

        RootGrid.SizeChanged += handler;
        timer.Start();
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
        AlignBrowseButtonToSearchButton();
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
