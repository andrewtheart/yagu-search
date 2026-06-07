using System;
using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Keeps the results list anchored sensibly while live search batches add or
/// re-sort file groups.
/// </summary>
public sealed partial class MainWindow
{
    private const double ResultsListScrollEdgeEpsilon = 0.5;
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
    private ResultsListSmartScrollIntent _pendingResultsListSmartScrollIntent;

    private void InitializeResultsListSmartScroll()
    {
        ViewModel.ResultRows.CollectionChanging += OnResultGroupsChanging;
        ViewModel.ResultRows.CollectionChanged += OnResultGroupsCollectionChanged;
        ResultsList.Loaded += (_, _) =>
        {
            EnsureResultsListScrollViewerHooked();
            CaptureResultsListScrollPosition();
        };
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            EnsureResultsListScrollViewerHooked();
            CaptureResultsListScrollPosition();
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
        if (LogService.Instance.IsVerboseEnabled && intent != ResultsListSmartScrollIntent.None)
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
            return;
        }

        ResultsListSmartScrollIntent intent = ResolveResultsListSmartScrollIntent();
        if (intent != ResultsListSmartScrollIntent.None)
            QueueResultsListSmartScrollRestore(intent);
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
        if (LogService.Instance.IsVerboseEnabled)
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

}