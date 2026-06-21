using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Keeps the results list anchored sensibly while live search batches add or
/// re-sort file groups.
/// </summary>
public sealed partial class MainWindow
{
    private const double ResultsListScrollEdgeEpsilon = 0.5;
    private double ResultsFileOverlayFallbackHeight => ViewModel.FileListOverlayHeight;
    private const int ResultsListSmartScrollRestorePasses = 3;

    private enum ResultsListSmartScrollIntent
    {
        None,
        KeepTop,
        FollowBottom,
    }

    private ScrollViewer? _resultsListScrollViewer;
    private bool _resultsListScrollViewerHooked;
    private bool _resultsListWasAtTop = true;
    private bool _resultsListWasAtBottom = true;
    private bool _resultsListSmartScrollPending;
    private bool _resultsListTopRestoreInProgress;
    private bool _resultsListShowMoreRestoreInProgress;
    private bool _resultsFileOverlayUpdatePending;
    private FileGroup? _resultsFileOverlayGroup;
    private ResultsListSmartScrollIntent _pendingResultsListSmartScrollIntent;

    private void InitializeResultsListSmartScroll()
    {
        ViewModel.ResultRows.CollectionChanging += OnResultGroupsChanging;
        ViewModel.ResultRows.CollectionChanged += OnResultGroupsCollectionChanged;
        ResultsList.Loaded += (_, _) =>
        {
            EnsureResultsListScrollViewerHooked();
            CaptureResultsListScrollPosition();
            QueueResultsFileOverlayUpdate();
        };
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            EnsureResultsListScrollViewerHooked();
            CaptureResultsListScrollPosition();
            QueueResultsFileOverlayUpdate();
        });
    }

    private void DisposeResultsListSmartScroll()
    {
        ViewModel.ResultRows.CollectionChanging -= OnResultGroupsChanging;
        ViewModel.ResultRows.CollectionChanged -= OnResultGroupsCollectionChanged;
        if (_resultsListScrollViewer is not null)
            _resultsListScrollViewer.ViewChanged -= OnResultsListScrollViewerViewChanged;
        _resultsListScrollViewer = null;
        _resultsListScrollViewerHooked = false;
        _resultsFileOverlayGroup = null;
    }

    private void EnsureResultsListScrollViewerHooked()
    {
        if (_resultsListScrollViewerHooked)
            return;

        if (FindVisualDescendant<ScrollViewer>(ResultsList) is not { } scroller)
            return;

        _resultsListScrollViewer = scroller;
        _resultsListScrollViewerHooked = true;
        scroller.ViewChanged += OnResultsListScrollViewerViewChanged;
    }

    private void OnResultsListScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        CaptureResultsListScrollPosition();
        QueueResultsFileOverlayUpdate();
    }

    private void CaptureResultsListScrollPosition()
    {
        EnsureResultsListScrollViewerHooked();
        var scroller = _resultsListScrollViewer;
        if (scroller is null)
        {
            bool hasGroupsWithoutScroller = ViewModel.ResultRows.Count > 0;
            _resultsListWasAtTop = hasGroupsWithoutScroller;
            _resultsListWasAtBottom = hasGroupsWithoutScroller;
            return;
        }

        bool hasVisibleGroups = ViewModel.ResultRows.Count > 0;
        _resultsListWasAtTop = hasVisibleGroups && IsResultsListAtTop(scroller);
        _resultsListWasAtBottom = hasVisibleGroups && IsResultsListAtBottom(scroller);
    }

    private double? CaptureResultsListVerticalOffset()
    {
        EnsureResultsListScrollViewerHooked();
        return _resultsListScrollViewer?.VerticalOffset;
    }

    private void QueueRestoreResultsListVerticalOffsetAfterShowMore(double? verticalOffset, string filePath)
    {
        if (verticalOffset is not double targetOffset || double.IsNaN(targetOffset) || double.IsInfinity(targetOffset))
        {
            _resultsListShowMoreRestoreInProgress = false;
            CaptureResultsListScrollPosition();
            return;
        }

        RestoreResultsListVerticalOffsetAfterShowMore(targetOffset, filePath, ResultsListSmartScrollRestorePasses + 2);
    }

    private bool ApplyResultsListVerticalOffsetAfterShowMore(double targetOffset, string filePath, bool log)
    {
        EnsureResultsListScrollViewerHooked();
        if (_resultsListScrollViewer is not { } scroller)
            return false;

        double clampedOffset = Math.Clamp(targetOffset, 0, Math.Max(0, scroller.ScrollableHeight));
        bool accepted = scroller.ChangeView(null, clampedOffset, null, disableAnimation: true);
        if (log && LogService.Instance.IsVerboseEnabled)
        {
            LogService.Instance.Verbose("ResultsList",
                $"RestoreResultsListVerticalOffsetAfterShowMore: file='{System.IO.Path.GetFileName(filePath)}', requested={targetOffset:N1}, clamped={clampedOffset:N1}, accepted={accepted}, current={scroller.VerticalOffset:N1}");
        }

        CaptureResultsListScrollPosition();
        return true;
    }

    private void RestoreResultsListVerticalOffsetAfterShowMore(double targetOffset, string filePath, int remainingPasses)
    {
        if (!ApplyResultsListVerticalOffsetAfterShowMore(targetOffset, filePath, log: remainingPasses == ResultsListSmartScrollRestorePasses + 2))
        {
            _resultsListShowMoreRestoreInProgress = false;
            CaptureResultsListScrollPosition();
            return;
        }

        if (remainingPasses > 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                RestoreResultsListVerticalOffsetAfterShowMore(targetOffset, filePath, remainingPasses - 1));
            return;
        }

        _resultsListShowMoreRestoreInProgress = false;
    }

    private void OnResultGroupsChanging(object? sender, EventArgs e)
    {
        CaptureResultsListScrollPosition();
        ResultsListSmartScrollIntent intent = ResolveResultsListSmartScrollIntent();
        if (!ViewModel.IsSearching && LogService.Instance.IsVerboseEnabled && intent != ResultsListSmartScrollIntent.None)
        {
            LogService.Instance.Verbose("ResultsList",
                $"ResultRowsChanging: intent={intent}, atTop={_resultsListWasAtTop}, atBottom={_resultsListWasAtBottom}, rows={ViewModel.ResultRows.Count}, groups={ViewModel.ResultGroups.Count}, autoScroll={_autoScrollEnabled}");
        }
        if (intent != ResultsListSmartScrollIntent.None)
            QueueResultsListSmartScrollRestore(intent);
    }

    private void OnResultGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.ResultRows.Count == 0)
        {
            CaptureResultsListScrollPosition();
            QueueResultsFileOverlayUpdate();
            return;
        }

        ResultsListSmartScrollIntent intent = ResolveResultsListSmartScrollIntent();
        if (intent != ResultsListSmartScrollIntent.None)
            QueueResultsListSmartScrollRestore(intent);
        QueueResultsFileOverlayUpdate();
    }

    private void QueueResultsFileOverlayUpdate()
    {
        if (_resultsFileOverlayUpdatePending)
            return;

        _resultsFileOverlayUpdatePending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resultsFileOverlayUpdatePending = false;
            UpdateResultsFileOverlay();
        });
    }

    private void UpdateResultsFileOverlay()
    {
        EnsureResultsListScrollViewerHooked();
        if (_resultsListScrollViewer is null || ViewModel.ResultRows.Count == 0 || ResultsList.ActualHeight <= 0)
        {
            HideResultsFileOverlay();
            return;
        }

        var group = FindCurrentResultsFileGroupWithHiddenHeader();
        if (group is null)
        {
            HideResultsFileOverlay();
            return;
        }

        if (!ReferenceEquals(_resultsFileOverlayGroup, group) || ResultsFileOverlay.Visibility != Visibility.Visible)
        {
            _resultsFileOverlayGroup = group;
            ResultsFileOverlayFileName.Text = group.FileName;
            ToolTipService.SetToolTip(ResultsFileOverlayFileName, group.FilePath);
            ResultsFileOverlayExplorerButton.Tag = group.FilePath;
            ApplyFileListOverlayFontSettings();
        }

        ResultsFileOverlay.Visibility = Visibility.Visible;
    }

    private void HideResultsFileOverlay()
    {
        _resultsFileOverlayGroup = null;
        ResultsFileOverlay.Visibility = Visibility.Collapsed;
        ResultsFileOverlayExplorerButton.Tag = null;
    }

    private void OnResultsFileOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        if (CollapseResultsFileOverlayFromInput(e.OriginalSource as DependencyObject))
            e.Handled = true;
    }

    private void OnResultsFileOverlayDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (CollapseResultsFileOverlayFromInput(e.OriginalSource as DependencyObject))
            e.Handled = true;
    }

    private bool CollapseResultsFileOverlayFromInput(DependencyObject? originalSource)
    {
        if (IsInsideHeaderCommand(originalSource, ResultsFileOverlay))
            return false;

        var group = _resultsFileOverlayGroup;
        if (group is null)
        {
            HideResultsFileOverlay();
            return true;
        }

        if (group.IsExpanded)
            group.IsExpanded = false;

        HideResultsFileOverlay();
        QueueResultsFileOverlayUpdate();
        return true;
    }

    private FileGroup? FindCurrentResultsFileGroupWithHiddenHeader()
    {
        double viewportBottom = Math.Max(0, ResultsList.ActualHeight);
        double overlayBottom = GetResultsFileOverlayReservedHeight();
        FileGroup? bestGroup = null;
        double bestTop = double.NegativeInfinity;
        bool visibleHeaderBlocksOverlay = false;

        Visit(ResultsList);
        return visibleHeaderBlocksOverlay ? null : bestGroup;

        void Visit(DependencyObject parent)
        {
            if (visibleHeaderBlocksOverlay)
                return;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Expander expander)
                    VisitExpander(expander);

                if (visibleHeaderBlocksOverlay)
                    return;

                Visit(child);
            }
        }

        void VisitExpander(Expander expander)
        {
            if (expander.DataContext is not FileGroup group || expander.ActualHeight <= 0)
                return;

            if (!TryGetElementBoundsInResultsList(expander, out double top, out double bottom))
                return;

            if (bottom <= 0 || top >= viewportBottom)
                return;

            // If the entire expander (header + content) fits within the viewport,
            // the header is already visible — no overlay needed.
            if (top >= -ResultsListScrollEdgeEpsilon && bottom <= viewportBottom + ResultsListScrollEdgeEpsilon)
                return;

            if (!TryGetFileGroupHeaderBoundsInResultsList(expander, fallbackTop: top, out double headerTop, out double headerBottom))
                return;

            if (headerBottom > ResultsListScrollEdgeEpsilon && headerTop < overlayBottom)
            {
                visibleHeaderBlocksOverlay = true;
                return;
            }

            if (headerBottom > ResultsListScrollEdgeEpsilon)
                return;

            if (bottom <= overlayBottom + ResultsListScrollEdgeEpsilon)
                return;

            if (top > bestTop)
            {
                bestTop = top;
                bestGroup = group;
            }
        }
    }

    private double GetResultsFileOverlayReservedHeight()
    {
        double actualHeight = ResultsFileOverlay.ActualHeight;
        return double.IsFinite(actualHeight) && actualHeight > ResultsListScrollEdgeEpsilon
            ? actualHeight
            : ResultsFileOverlayFallbackHeight;
    }

    private bool TryGetFileGroupHeaderBoundsInResultsList(Expander expander, double fallbackTop, out double headerTop, out double headerBottom)
    {
        headerTop = fallbackTop;
        headerBottom = fallbackTop + Math.Min(expander.ActualHeight, ResultsFileOverlayFallbackHeight);
        if (expander.Header is FrameworkElement header && header.ActualHeight > 0)
        {
            try
            {
                var point = header.TransformToVisual(ResultsList).TransformPoint(new Windows.Foundation.Point(0, 0));
                headerTop = point.Y;
                headerBottom = point.Y + header.ActualHeight;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryGetElementBoundsInResultsList(FrameworkElement element, out double top, out double bottom)
    {
        top = 0;
        bottom = 0;
        try
        {
            var point = element.TransformToVisual(ResultsList).TransformPoint(new Windows.Foundation.Point(0, 0));
            top = point.Y;
            bottom = top + element.ActualHeight;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private ResultsListSmartScrollIntent ResolveResultsListSmartScrollIntent()
    {
        if (_resultsListShowMoreRestoreInProgress)
            return ResultsListSmartScrollIntent.None;

        if (_resultsListWasAtTop)
            return ResultsListSmartScrollIntent.KeepTop;
        if (_autoScrollEnabled)
            return ResultsListSmartScrollIntent.FollowBottom;
        if (_resultsListWasAtBottom)
            return ResultsListSmartScrollIntent.FollowBottom;

        return ResultsListSmartScrollIntent.None;
    }

    private void QueueResultsListSmartScrollRestore(ResultsListSmartScrollIntent intent)
    {
        if (intent == ResultsListSmartScrollIntent.KeepTop)
        {
            _pendingResultsListSmartScrollIntent = ResultsListSmartScrollIntent.KeepTop;
            _resultsListTopRestoreInProgress = true;
        }
        else if (_pendingResultsListSmartScrollIntent == ResultsListSmartScrollIntent.None)
        {
            _pendingResultsListSmartScrollIntent = intent;
        }

        if (_resultsListSmartScrollPending)
            return;

        _resultsListSmartScrollPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resultsListSmartScrollPending = false;
            ApplyPendingResultsListSmartScrollRestore();
        });
    }

    private void ApplyPendingResultsListSmartScrollRestore()
    {
        var intent = _pendingResultsListSmartScrollIntent;
        _pendingResultsListSmartScrollIntent = ResultsListSmartScrollIntent.None;

        if (ViewModel.ResultRows.Count == 0)
        {
            _resultsListTopRestoreInProgress = false;
            return;
        }

        ApplyResultsListSmartScrollIntent(intent, ResultsListSmartScrollRestorePasses);
    }

    private void ApplyResultsListSmartScrollIntent(ResultsListSmartScrollIntent intent, int remainingPasses)
    {
        if (ViewModel.ResultRows.Count == 0)
        {
            _resultsListTopRestoreInProgress = false;
            return;
        }

        if (intent == ResultsListSmartScrollIntent.KeepTop)
            ScrollResultsListToTop();
        else if (intent == ResultsListSmartScrollIntent.FollowBottom)
            ScrollResultsListToBottom();

        if (intent == ResultsListSmartScrollIntent.KeepTop && remainingPasses > 0)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                ApplyResultsListSmartScrollIntent(intent, remainingPasses - 1));
            return;
        }

        if (intent == ResultsListSmartScrollIntent.KeepTop)
            _resultsListTopRestoreInProgress = false;
    }

    private void ScrollResultsListToTop()
    {
        if (ViewModel.ResultRows.Count == 0)
            return;

        EnsureResultsListScrollViewerHooked();
        ResultsList.ScrollIntoView(ViewModel.ResultRows[0], ScrollIntoViewAlignment.Leading);
        _resultsListScrollViewer?.ChangeView(null, 0, null, disableAnimation: true);
        if (!ViewModel.IsSearching && LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("ResultsList", $"ScrollResultsListToTop: rows={ViewModel.ResultRows.Count}, groups={ViewModel.ResultGroups.Count}");
        CaptureResultsListScrollPosition();
    }

    private void ScrollResultsListToBottom()
    {
        if (ViewModel.ResultRows.Count == 0)
            return;

        EnsureResultsListScrollViewerHooked();
        ResultsList.ScrollIntoView(ViewModel.ResultRows[^1]);
        if (_resultsListScrollViewer is { } scroller)
            scroller.ChangeView(null, scroller.ScrollableHeight, null, disableAnimation: true);
        CaptureResultsListScrollPosition();
    }

    private static bool IsResultsListAtTop(ScrollViewer scroller)
        => scroller.VerticalOffset <= ResultsListScrollEdgeEpsilon;

    private static bool IsResultsListAtBottom(ScrollViewer scroller)
        => scroller.ScrollableHeight <= ResultsListScrollEdgeEpsilon
           || scroller.ScrollableHeight - scroller.VerticalOffset <= ResultsListScrollEdgeEpsilon;

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var nested = FindVisualDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    // Theme-adaptive default colors for file-list labels. The persisted defaults
    // (white at descending opacity) are tuned for a dark surface; on a Light
    // surface white text is invisible, so when the user is still on the built-in
    // default we substitute equivalent black-based opacities. Custom colors are
    // always honored verbatim.
    private static readonly Windows.UI.Color DrawerPrimaryLightColor = Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00);
    private static readonly Windows.UI.Color DrawerSecondaryLightColor = Windows.UI.Color.FromArgb(0x9E, 0x00, 0x00, 0x00);
    private static readonly Windows.UI.Color DrawerTertiaryLightColor = Windows.UI.Color.FromArgb(0x72, 0x00, 0x00, 0x00);

    private readonly System.Collections.Generic.HashSet<Grid> _realizedFileGroupHeaders = new();

    private static Windows.UI.Color ResolveThemedLabelColor(
        string? configuredColor, string defaultColor,
        Windows.UI.Color darkDefault, Windows.UI.Color lightDefault, bool isLight)
    {
        bool isDefault = string.IsNullOrWhiteSpace(configuredColor)
            || string.Equals(configuredColor.Trim(), defaultColor, System.StringComparison.OrdinalIgnoreCase);
        if (isDefault)
            return isLight ? lightDefault : darkDefault;
        return ColorStringHelper.Parse(configuredColor, isLight ? lightDefault : darkDefault);
    }

    /// <summary>Re-applies drawer/overlay label colors to realized rows after a
    /// live theme switch so theme-default colors track the new surface.</summary>
    internal void RefreshDrawerLabelThemes()
    {
        ApplyFileListOverlayFontSettings();
        foreach (var headerGrid in _realizedFileGroupHeaders)
            ApplyDrawerLabelSettings(headerGrid);
    }

    private void ApplyFileListOverlayFontSettings()
    {
        ResultsFileOverlayFileName.FontSize = ViewModel.FileListOverlayFontSize;
        ResultsFileOverlayFileName.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(ViewModel.FileListOverlayFontFamily);
        bool isLight = ResultsFileOverlay.ActualTheme == ElementTheme.Light;
        var color = ResolveThemedLabelColor(
            ViewModel.FileListOverlayFontColor, AppSettings.DefaultFileListOverlayFontColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), DrawerPrimaryLightColor, isLight);
        ResultsFileOverlayFileName.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        ResultsFileOverlay.Height = ViewModel.FileListOverlayHeight;
    }

    private void ApplyDrawerLabelSettings(FrameworkElement headerGrid)
    {
        if (headerGrid is not Grid grid) return;

        bool isLight = grid.ActualTheme == ElementTheme.Light;
        var fileNameBrush = new SolidColorBrush(ResolveThemedLabelColor(
            ViewModel.DrawerFileNameFontColor, AppSettings.DefaultDrawerFileNameFontColor,
            Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), DrawerPrimaryLightColor, isLight));
        var dirBrush = new SolidColorBrush(ResolveThemedLabelColor(
            ViewModel.DrawerDirectoryFontColor, AppSettings.DefaultDrawerDirectoryFontColor,
            Windows.UI.Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), DrawerSecondaryLightColor, isLight));
        var metaBrush = new SolidColorBrush(ResolveThemedLabelColor(
            ViewModel.DrawerMetadataFontColor, AppSettings.DefaultDrawerMetadataFontColor,
            Windows.UI.Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF), DrawerTertiaryLightColor, isLight));
        var fileNameFont = new FontFamily(ViewModel.DrawerFileNameFontFamily);
        var dirFont = new FontFamily(ViewModel.DrawerDirectoryFontFamily);
        var metaFont = new FontFamily(ViewModel.DrawerMetadataFontFamily);

        // Column 2 is the StackPanel containing file name, compact dir, and metadata
        foreach (var child in grid.Children)
        {
            if (child is StackPanel stack && Grid.GetColumn((FrameworkElement)child) == 2)
            {
                foreach (var item in stack.Children)
                {
                    if (item is TextBlock tb)
                    {
                        if (tb.FontWeight.Weight >= 600) // SemiBold = FileName
                        {
                            tb.FontSize = ViewModel.DrawerFileNameFontSize;
                            tb.FontFamily = fileNameFont;
                            tb.Foreground = fileNameBrush;
                        }
                        else if (tb.Tag is "CompactDir") // Directory
                        {
                            tb.FontSize = ViewModel.DrawerDirectoryFontSize;
                            tb.FontFamily = dirFont;
                            tb.Foreground = dirBrush;
                            tb.Opacity = 1.0; // color already encodes opacity
                        }
                        else // Metadata (Modified/Size)
                        {
                            tb.FontSize = ViewModel.DrawerMetadataFontSize;
                            tb.FontFamily = metaFont;
                            tb.Foreground = metaBrush;
                            tb.Opacity = 1.0;
                        }
                    }
                    else if (item is StackPanel metaPanel) // inner StackPanel with metadata TextBlock
                    {
                        foreach (var metaChild in metaPanel.Children)
                        {
                            if (metaChild is TextBlock metaTb)
                            {
                                metaTb.FontSize = ViewModel.DrawerMetadataFontSize;
                                metaTb.FontFamily = metaFont;
                                metaTb.Foreground = metaBrush;
                                metaTb.Opacity = 1.0;
                            }
                        }
                    }
                }
            }
            // Column 3 = wide directory label
            else if (child is TextBlock dirTb && Grid.GetColumn((FrameworkElement)child) == 3
                     && dirTb.Tag is "WideDir")
            {
                dirTb.FontSize = ViewModel.DrawerDirectoryFontSize;
                dirTb.FontFamily = dirFont;
                dirTb.Foreground = dirBrush;
                dirTb.Opacity = 1.0;
            }
        }
    }

}
