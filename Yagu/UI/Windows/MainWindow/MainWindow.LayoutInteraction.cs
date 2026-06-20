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
    private bool _advancedOptionsDrawerMaxHeightRetryQueued;
    private int _advancedOptionsDrawerMaxHeightRetryCount;
    private bool _advancedOptionsOverlayActive;
    private const double CompactTopSearchDrawerThreshold = 440;
    private const double CompactTopSearchActionButtonWidth = 38;
    private const int MaxAdvancedOptionsDrawerMaxHeightRetries = 8;

    // Vertical gap between the floating search drawer (search card + status panel)
    // and the results panel beneath it in PreviewTopExpanded mode. Kept equal to the
    // normal stacked layout's gap (4px search-card bottom margin + 2px split-pane top
    // margin) so the gap stays a fixed size and does not visibly grow when the preview
    // panel is revealed for the first time and the panels reorganize.
    private const double PreviewTopExpandedDrawerGap = 6;

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

        if (AdvancedOptionsScrollViewer.Content is FrameworkElement drawerContent)
            drawerContent.SizeChanged += (_, _) => UpdateAdvancedOptionsDrawerMaxHeight();
    }

    private void OnAdvancedOptionsExpanding(Expander sender, ExpanderExpandingEventArgs args)
    {
        _advancedOptionsDrawerMaxHeightRetryQueued = false;
        _advancedOptionsDrawerMaxHeightRetryCount = 0;
        SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: true);

        if (_advancedOptionsOverlayActive)
        {
            // Traditional mode: float the drawer over the results pane. The header
            // chevron/layout needs a frame to settle before we can anchor the overlay
            // under it, so reposition again on the next low-priority tick.
            ShowAdvancedOptionsOverlay();
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (!AdvancedOptionsExpander.IsExpanded) return;
                    PositionAdvancedOptionsOverlay();
                    UpdateAdvancedOptionsDrawerMaxHeight();
                });
            return;
        }

        UpdateAdvancedOptionsDrawerMaxHeight();
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    private void OnAdvancedOptionsCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
    {
        SetAdvancedOptionsDrawerExpandedWidthState(isExpanded: false);

        if (_advancedOptionsOverlayActive)
        {
            HideAdvancedOptionsOverlay();
            return;
        }

        UpdateAdvancedOptionsDrawerMaxHeight();
        if (_launcherMode)
            ListenForExpanderResize();
        else
            ListenForExpanderLayoutSync();
    }

    /// <summary>
    /// Moves the Advanced Options drawer out of the inline Expander and into the floating
    /// overlay host so that expanding it (in traditional, non-launcher mode) renders above the
    /// results pane instead of reflowing the layout or growing the window. The Expander keeps
    /// its header + chevron as the open/close toggle. Idempotent; no-op in launcher mode.
    /// </summary>
    private void MoveAdvancedOptionsDrawerToOverlay()
    {
        if (_advancedOptionsOverlayActive || _launcherMode) return;
        if (AdvancedOptionsScrollViewer is null || AdvancedOptionsOverlayHost is null) return;

        // Detach the drawer from the Expander (replacing its inline content with a zero-height
        // placeholder so the toggle still works) and host it in the floating overlay.
        AdvancedOptionsExpander.Content = new Border { Height = 0 };
        AdvancedOptionsOverlayHost.Child = AdvancedOptionsScrollViewer;
        _advancedOptionsOverlayActive = true;

        // Keep the floating drawer anchored to the header as the window/content resizes.
        RootGrid.SizeChanged += (_, _) =>
        {
            if (_advancedOptionsOverlayActive && AdvancedOptionsExpander.IsExpanded)
            {
                PositionAdvancedOptionsOverlay();
                UpdateAdvancedOptionsDrawerMaxHeight();
            }
        };

        if (AdvancedOptionsExpander.IsExpanded)
            ShowAdvancedOptionsOverlay();
    }

    /// <summary>
    /// Traditional (non-launcher) startup: move the Advanced Options drawer into the floating
    /// overlay (so expanding it covers the results pane instead of reflowing), then keep a roomy
    /// default window. Runs immediately and again at Low priority once layout has settled.
    /// </summary>
    private void InitializeTraditionalAdvancedOptionsOverlay()
    {
        MoveAdvancedOptionsDrawerToOverlay();
        FitTraditionalWindowHeightToContent();
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            FitTraditionalWindowHeightToContent);
    }

    private void ShowAdvancedOptionsOverlay()
    {
        if (!_advancedOptionsOverlayActive || AdvancedOptionsOverlayHost is null) return;
        AdvancedOptionsOverlayScrim.Visibility = Visibility.Visible;
        AdvancedOptionsOverlayHost.Visibility = Visibility.Visible;
        PositionAdvancedOptionsOverlay();
        UpdateAdvancedOptionsDrawerMaxHeight();
    }

    private void HideAdvancedOptionsOverlay()
    {
        if (AdvancedOptionsOverlayHost is null) return;
        AdvancedOptionsOverlayScrim.Visibility = Visibility.Collapsed;
        AdvancedOptionsOverlayHost.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Anchors the floating drawer directly beneath the Advanced Options header, matched to the
    /// header's width, using the header's position in RootGrid coordinates.
    /// </summary>
    private void PositionAdvancedOptionsOverlay()
    {
        if (!_advancedOptionsOverlayActive || AdvancedOptionsOverlayHost is null) return;
        try
        {
            var transform = AdvancedOptionsExpander.TransformToVisual(RootGrid);
            var origin = transform.TransformPoint(
                new Windows.Foundation.Point(0, AdvancedOptionsExpander.ActualHeight));
            double width = AdvancedOptionsExpander.ActualWidth;
            if (width <= 0 || double.IsNaN(origin.X) || double.IsNaN(origin.Y))
                return;

            AdvancedOptionsOverlayHost.Margin = new Thickness(origin.X, origin.Y, 0, 0);
            AdvancedOptionsOverlayHost.Width = width;
        }
        catch
        {
            // Transform can throw if the visual isn't in the live tree yet; ignore and rely on
            // the deferred reposition tick.
        }
    }

    private void OnAdvancedOptionsOverlayScrimPressed(object sender, PointerRoutedEventArgs e)
    {
        if (AdvancedOptionsExpander.IsExpanded)
            AdvancedOptionsExpander.IsExpanded = false;
        e.Handled = true;
    }

    private void OnAdvancedOptionsOverlayKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && AdvancedOptionsExpander.IsExpanded)
        {
            AdvancedOptionsExpander.IsExpanded = false;
            e.Handled = true;
        }
    }

    // Escape closes the floating drawer regardless of where focus currently sits (the header
    // keeps focus when the drawer is opened by clicking it, so the KeyDown handler above would
    // never see the key). The accelerator is only live while the overlay host is visible.
    private void OnAdvancedOptionsOverlayEscapeInvoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (AdvancedOptionsExpander.IsExpanded)
            AdvancedOptionsExpander.IsExpanded = false;
        args.Handled = true;
    }

    private void SetAdvancedOptionsDrawerExpandedWidthState(bool isExpanded)
    {
        _advancedOptionsDrawerExpandedWidth = isExpanded;
        Grid.SetColumnSpan(AdvancedOptionsExpander, 1);
        AdvancedOptionsExpander.HorizontalAlignment = HorizontalAlignment.Stretch;
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
            _advancedOptionsDrawerMaxHeightRetryQueued = false;
            _advancedOptionsDrawerMaxHeightRetryCount = 0;
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
            QueueAdvancedOptionsDrawerMaxHeightRetry();
            return;
        }

        if (double.IsNaN(topY) || topY < 0)
        {
            QueueAdvancedOptionsDrawerMaxHeightRetry();
            return;
        }

        double ceiling = ResolveAdvancedOptionsDrawerCeiling();
        if (ceiling <= 0)
        {
            QueueAdvancedOptionsDrawerMaxHeightRetry();
            return;
        }

        double maxHeight = ceiling - topY;
        AdvancedOptionsScrollViewer.MaxHeight =
            maxHeight > MinAdvancedOptionsDrawerHeight ? maxHeight : MinAdvancedOptionsDrawerHeight;
        _advancedOptionsDrawerMaxHeightRetryQueued = false;
        _advancedOptionsDrawerMaxHeightRetryCount = 0;
    }

    private void QueueAdvancedOptionsDrawerMaxHeightRetry()
    {
        if (!AdvancedOptionsExpander.IsExpanded || _advancedOptionsDrawerMaxHeightRetryQueued)
            return;

        if (_advancedOptionsDrawerMaxHeightRetryCount >= MaxAdvancedOptionsDrawerMaxHeightRetries)
            return;

        _advancedOptionsDrawerMaxHeightRetryQueued = true;
        _advancedOptionsDrawerMaxHeightRetryCount++;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _advancedOptionsDrawerMaxHeightRetryQueued = false;
            if (!AdvancedOptionsExpander.IsExpanded)
            {
                _advancedOptionsDrawerMaxHeightRetryCount = 0;
                return;
            }

            UpdateAdvancedOptionsDrawerMaxHeight();
        });
    }

    /// <summary>
    /// Resolves the bottom limit (in DIPs, relative to the window client top) the
    /// drawer content may reach. Prefer the realized client height so the drawer
    /// scrolls inside the visible window instead of sizing itself to the monitor.
    /// </summary>
    private double ResolveAdvancedOptionsDrawerCeiling()
    {
        double reserve = _launcherMode ? LauncherDrawerBottomReserve : FullModeDrawerBottomReserve;
        double rootHeight = RootGrid.ActualHeight;
        if (rootHeight > 0)
            return rootHeight - reserve;

        if (!_launcherMode)
            return 0;

        try
        {
            if (AppWindow is null)
                return 0;

            double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0)
                return 0;

            double clientHeightDip = AppWindow.ClientSize.Height / scale;
            if (clientHeightDip > 0)
                return clientHeightDip - reserve;

            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (displayArea is null)
                return 0;

            double workAreaHeightDip = displayArea.WorkArea.Height / scale;
            return workAreaHeightDip - reserve;
        }
        catch
        {
            return 0;
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
