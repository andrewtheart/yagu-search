using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Yagu;

/// <summary>
/// Keeps no-wrap preview text selection usable when the pointer is dragged past
/// the visible horizontal edge of the preview.
/// </summary>
public sealed partial class MainWindow
{
    private const double PreviewSelectionAutoScrollEdgeDip = 36;
    private const double PreviewSelectionAutoScrollMinStepDip = 4;
    private const double PreviewSelectionAutoScrollMaxStepDip = 56;

    private DispatcherTimer? _previewSelectionAutoScrollTimer;
    private RichTextBlock? _previewSelectionAutoScrollBlock;
    private ScrollViewer? _previewSelectionAutoScrollScroller;
    private uint _previewSelectionAutoScrollPointerId;
    private double _previewSelectionAutoScrollPointerX;

    private void AttachPreviewSelectionAutoScroll(RichTextBlock block)
    {
        block.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerPressed),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerMoved),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerEnded),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerEnded),
            handledEventsToo: true);
    }

    private void OnPreviewSelectionAutoScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block || !e.GetCurrentPoint(block).Properties.IsLeftButtonPressed)
            return;

        var scroller = ResolvePreviewSelectionAutoScrollScroller(block);
        if (scroller is null)
            return;

        _previewSelectionAutoScrollBlock = block;
        _previewSelectionAutoScrollScroller = scroller;
        _previewSelectionAutoScrollPointerId = e.Pointer.PointerId;
        _previewSelectionAutoScrollPointerX = e.GetCurrentPoint(scroller).Position.X;
    }

    private void OnPreviewSelectionAutoScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block || !ReferenceEquals(block, _previewSelectionAutoScrollBlock))
            return;
        if (e.Pointer.PointerId != _previewSelectionAutoScrollPointerId)
            return;

        UIElement pointTarget = _previewSelectionAutoScrollScroller ?? (UIElement)block;
        var point = e.GetCurrentPoint(pointTarget);
        if (!point.Properties.IsLeftButtonPressed)
        {
            StopPreviewSelectionAutoScroll();
            return;
        }

        if (_previewSelectionAutoScrollScroller is null)
            return;

        _previewSelectionAutoScrollPointerX = point.Position.X;
        if (TryGetPreviewSelectionAutoScrollStep(_previewSelectionAutoScrollScroller, _previewSelectionAutoScrollPointerX, out _))
            EnsurePreviewSelectionAutoScrollTimer();
        else
            _previewSelectionAutoScrollTimer?.Stop();
    }

    private void OnPreviewSelectionAutoScrollPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block || !ReferenceEquals(block, _previewSelectionAutoScrollBlock))
            return;
        if (e.Pointer.PointerId == _previewSelectionAutoScrollPointerId)
            StopPreviewSelectionAutoScroll();
    }

    private ScrollViewer? ResolvePreviewSelectionAutoScrollScroller(RichTextBlock block)
    {
        if (ReferenceEquals(block, PreviewBlock))
            return PreviewScrollViewer;

        return _sectionMatchNavs.TryGetValue(block, out var sectionNav)
            ? sectionNav.Scroller
            : null;
    }

    private void EnsurePreviewSelectionAutoScrollTimer()
    {
        if (_previewSelectionAutoScrollTimer is null)
        {
            _previewSelectionAutoScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _previewSelectionAutoScrollTimer.Tick += (_, _) => ApplyPreviewSelectionAutoScroll();
        }

        if (!_previewSelectionAutoScrollTimer.IsEnabled)
            _previewSelectionAutoScrollTimer.Start();
    }

    private void ApplyPreviewSelectionAutoScroll()
    {
        var block = _previewSelectionAutoScrollBlock;
        var scroller = _previewSelectionAutoScrollScroller;
        if (block is null || scroller is null)
        {
            StopPreviewSelectionAutoScroll();
            return;
        }

        if (block.TextWrapping != TextWrapping.NoWrap
            || scroller.HorizontalScrollMode != ScrollMode.Enabled
            || scroller.ScrollableWidth <= 0.5)
        {
            StopPreviewSelectionAutoScroll();
            return;
        }

        if (!TryGetPreviewSelectionAutoScrollStep(scroller, _previewSelectionAutoScrollPointerX, out double step))
        {
            _previewSelectionAutoScrollTimer?.Stop();
            return;
        }

        double targetX = Math.Clamp(scroller.HorizontalOffset + step, 0, scroller.ScrollableWidth);
        if (Math.Abs(targetX - scroller.HorizontalOffset) <= 0.5)
            return;

        scroller.ChangeView(targetX, null, null, disableAnimation: true);
        UpdateStickyHorizontalScrollBar();
    }

    private static bool TryGetPreviewSelectionAutoScrollStep(ScrollViewer scroller, double pointerX, out double step)
    {
        step = 0;
        double viewportWidth = scroller.ViewportWidth > 0 ? scroller.ViewportWidth : scroller.ActualWidth;
        if (viewportWidth <= 0)
            return false;

        double edge = Math.Min(PreviewSelectionAutoScrollEdgeDip, Math.Max(8, viewportWidth / 4));
        double distanceBeyondEdge;
        if (pointerX > viewportWidth - edge)
            distanceBeyondEdge = pointerX - (viewportWidth - edge);
        else if (pointerX < edge)
            distanceBeyondEdge = pointerX - edge;
        else
            return false;

        double magnitude = Math.Clamp(Math.Abs(distanceBeyondEdge) * 0.65,
            PreviewSelectionAutoScrollMinStepDip,
            PreviewSelectionAutoScrollMaxStepDip);
        step = Math.Sign(distanceBeyondEdge) * magnitude;
        return Math.Abs(step) > 0.5;
    }

    private void StopPreviewSelectionAutoScroll()
    {
        _previewSelectionAutoScrollTimer?.Stop();
        _previewSelectionAutoScrollBlock = null;
        _previewSelectionAutoScrollScroller = null;
        _previewSelectionAutoScrollPointerId = 0;
        _previewSelectionAutoScrollPointerX = 0;
    }
}