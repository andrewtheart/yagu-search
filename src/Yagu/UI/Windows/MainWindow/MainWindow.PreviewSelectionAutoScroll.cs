using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Yagu.Services;
using Yagu.Services.Logging;
using System.Globalization;

namespace Yagu;

/// <summary>
/// Keeps no-wrap preview text selection usable when the pointer is dragged past
/// the visible horizontal edge of the preview.
/// </summary>
public sealed partial class MainWindow
{
    private const double PreviewSelectionAutoScrollEdgeDip = 36;
    private const double PreviewSelectionAutoScrollMinVelocityDipPerSecond = 420;
    private const double PreviewSelectionAutoScrollMaxVelocityDipPerSecond = 6200;
    private const double PreviewSelectionAutoScrollVelocityScale = 42;
    private const int PreviewSelectionAutoScrollTimerIntervalMs = 16;
    private const double PreviewSelectionAutoScrollMaxFrameSeconds = 0.20;
    private const long PreviewSelectionAutoScrollLogIntervalMs = 250;
    private const double PreviewSelectionAutoScrollDelayedFrameMs = 24;
    private const int PreviewCustomSelectionOverlayMaxMarkers = 512;

    private RichTextBlock? _previewSelectionAutoScrollBlock;
    private ScrollViewer? _previewSelectionAutoScrollScroller;
    // The scroller that actually moves the preview vertically. On the single-file
    // block surface this is the same object as the horizontal scroller above
    // (PreviewScrollViewer). On the multi-section surface each section's inner
    // scroller has vertical scrolling disabled, so the shared outer
    // PreviewScrollViewer is the real vertical scroller.
    private ScrollViewer? _previewSelectionAutoScrollVerticalScroller;
    private Timer? _previewSelectionAutoScrollTimer;
    private RichTextBlock? _previewCustomSelectionBlock;
    /// <summary>
    /// Every drawer of the same file when Ctrl+A selected a file that overflowed into continuation
    /// drawers, in panel order and including <see cref="_previewCustomSelectionBlock"/>. Null for an
    /// ordinary single-drawer selection. Non-primary members are always selected in full.
    /// </summary>
    private List<RichTextBlock>? _previewCustomSelectionGroupBlocks;
    private TextHighlighter? _previewCustomSelectionHighlighter;
    private readonly List<Border> _previewCustomSelectionOverlayMarkers = new();
    private readonly SolidColorBrush _previewCustomSelectionOverlayBrush = new(Windows.UI.Color.FromArgb(135, 0, 120, 215));
    private uint _previewSelectionAutoScrollPointerId;
    private int _previewSelectionAutoScrollTickQueued;
    private bool _previewSelectionAutoScrollTimerRunning;
    private bool _previewSelectionAutoScrollWasAtEdge;
    private bool _previewCustomSelectionDragging;
    private RichTextBlock? _previewCustomSelectionLastRangeBlock;
    private Point _previewSelectionAutoScrollPointerPointInScroller;
    private double _previewSelectionAutoScrollPointerX;
    private double _previewSelectionAutoScrollPointerY;
    // Pointer position relative to the vertical scroller's viewport. Tracked
    // separately because vertical auto-scroll runs against PreviewScrollViewer
    // while horizontal auto-scroll runs against the (possibly inner) section
    // scroller; the two scrollers differ on the sections surface.
    private Point _previewSelectionAutoScrollPointerPointInVerticalScroller;
    private double _previewSelectionAutoScrollPointerYInVertical;
    private int _previewCustomSelectionAnchorIndex = -1;
    private int _previewCustomSelectionCurrentIndex = -1;
    private int _previewCustomSelectionLastRangeStart = -1;
    private int _previewCustomSelectionLastRangeEnd = -1;
    private long _previewSelectionAutoScrollLastTick;
    private long _previewSelectionAutoScrollStartedTick;
    private long _previewSelectionAutoScrollLastLogTick;
    private long _previewSelectionAutoScrollPointerMoveCount;
    private long _previewSelectionAutoScrollFrameCount;
    private long _previewSelectionAutoScrollDelayedFrameCount;
    private long _previewSelectionAutoScrollChangeViewAcceptedCount;
    private long _previewSelectionAutoScrollChangeViewRejectedCount;
    private long _previewSelectionAutoScrollNoOpFrameCount;
    private double _previewSelectionAutoScrollMaxFrameMs;
    private double _previewSelectionAutoScrollMaxRawFrameMs;
    private double _previewSelectionAutoScrollMaxLagDip;
    private double _previewSelectionAutoScrollTotalRequestedDip;
    private double _previewSelectionAutoScrollLastRequestedX = double.NaN;
    private double _previewSelectionAutoScrollPrevBeforeX = double.NaN;
    private double _previewSelectionAutoScrollPrevBeforeY = double.NaN;
    private double _previewSelectionLastViewOffsetY = double.NaN;
    private int _previewSelectionAutoScrollStuckFrameCount;

    // Native RichTextBlock DoubleTapped is suppressed while the custom selection
    // handlers capture the pointer and mark PointerPressed handled, so the
    // double-click-to-open-editor gesture is detected here instead. This keeps
    // double-click-to-editor working in every wrap mode now that native text
    // selection (the source of the word-select crash) stays disabled.
    private long _previewSelectionLastClickTick;
    private Point _previewSelectionLastClickPoint;
    private RichTextBlock? _previewSelectionLastClickBlock;
    private long _previewEditorPointerOpenTick;
    private const int PreviewSelectionDoubleClickMaxMs = 500;
    private const double PreviewSelectionDoubleClickMaxDistance = 8;
    private const int PreviewEditorPointerOpenGuardMs = 700;

    private void AttachPreviewSelectionAutoScroll(RichTextBlock block)
    {
        block.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerPressed),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerMoved),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler(OnPreviewShowMorePointerMoved),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerExitedEvent,
            new PointerEventHandler(OnPreviewShowMorePointerExited),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerEnded),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerEnded),
            handledEventsToo: true);
        block.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnPreviewSelectionAutoScrollPointerEnded),
            handledEventsToo: true);
    }

    private void OnPreviewSelectionAutoScrollPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block || !e.GetCurrentPoint(block).Properties.IsLeftButtonPressed)
            return;

        if (IsPreviewShowMorePointerSource(e.OriginalSource))
        {
            CancelPreviewSelectionAutoScrollForShowMore("show-more-pointer-pressed");
            return;
        }

        HidePreviewShowMoreTooltipForContentPointer();

        var scroller = ResolvePreviewSelectionAutoScrollScroller(block);
        if (scroller is null)
            return;

        if (!ShouldUseCustomPreviewSelection(block, scroller))
        {
            ClearPreviewCustomSelection();
            return;
        }

        // Detect the double-click ourselves: the custom selection logic below
        // captures the pointer and marks the press handled, which suppresses the
        // native DoubleTapped gesture that normally opens the inline editor.
        // Double-clicking any preview text (highlighted match or plain text) must
        // still jump to that line in the editor.
        Point pressPoint = e.GetCurrentPoint(block).Position;
        long pressTick = Environment.TickCount64;
        bool isDoubleClick =
            ReferenceEquals(_previewSelectionLastClickBlock, block)
            && pressTick - _previewSelectionLastClickTick <= PreviewSelectionDoubleClickMaxMs
            && Math.Abs(pressPoint.X - _previewSelectionLastClickPoint.X) <= PreviewSelectionDoubleClickMaxDistance
            && Math.Abs(pressPoint.Y - _previewSelectionLastClickPoint.Y) <= PreviewSelectionDoubleClickMaxDistance;
        if (isDoubleClick)
        {
            _previewSelectionLastClickTick = 0;
            _previewSelectionLastClickBlock = null;
            YaguLog.For("PreviewEditor").LogDebug(
                "Double-click detected: point=({PointX:N1},{PointY:N1}), wrap={Wrap}, surface={Surface}", pressPoint.X, pressPoint.Y, block.TextWrapping, ReferenceEquals(block, PreviewBlock) ? "single" : "section");
            StopPreviewSelectionAutoScroll("double-click-editor");
            ClearPreviewCustomSelection();
            e.Handled = true;
            _ = EnterPreviewEditorFromPointerDoubleClickAsync(block, pressPoint);
            return;
        }
        _previewSelectionLastClickTick = pressTick;
        _previewSelectionLastClickPoint = pressPoint;
        _previewSelectionLastClickBlock = block;

        _previewSelectionAutoScrollBlock = block;
        _previewSelectionAutoScrollScroller = scroller;
        _previewSelectionAutoScrollVerticalScroller = ResolvePreviewSelectionAutoScrollVerticalScroller(block);
        _previewSelectionAutoScrollPointerId = e.Pointer.PointerId;
        _previewSelectionAutoScrollPointerPointInScroller = e.GetCurrentPoint(scroller).Position;
        _previewSelectionAutoScrollPointerX = _previewSelectionAutoScrollPointerPointInScroller.X;
        _previewSelectionAutoScrollPointerY = _previewSelectionAutoScrollPointerPointInScroller.Y;
        _previewSelectionAutoScrollPointerPointInVerticalScroller =
            e.GetCurrentPoint(_previewSelectionAutoScrollVerticalScroller).Position;
        _previewSelectionAutoScrollPointerYInVertical = _previewSelectionAutoScrollPointerPointInVerticalScroller.Y;
        _previewSelectionLastViewOffsetY = _previewSelectionAutoScrollVerticalScroller.VerticalOffset;
        _previewSelectionAutoScrollLastTick = Environment.TickCount64;
        _previewSelectionAutoScrollWasAtEdge = false;
        ResetPreviewSelectionAutoScrollDiagnostics(_previewSelectionAutoScrollLastTick);
        bool pointerCaptured = block.CapturePointer(e.Pointer);
        BeginPreviewCustomSelection(block, scroller);
        e.Handled = true;
        LogPreviewSelectionAutoScrollStart(block, scroller, pointerCaptured);
    }

    /// <summary>
    /// Opens the inline editor at <paramref name="point"/> (block coordinates) in
    /// response to a double-click detected by the custom selection pointer handler.
    /// Mirrors the native <c>DoubleTapped</c> path, which is suppressed while the
    /// custom selection captures the pointer.
    /// </summary>
    private async Task EnterPreviewEditorFromPointerDoubleClickAsync(RichTextBlock block, Point point)
    {
        if (_previewMutating)
        {
            YaguLog.For("PreviewEditor").LogDebug(
                "Pointer double-click editor entry skipped: preview is mutating");
            return;
        }
        DismissActiveIntroTip();
        _previewEditorPointerOpenTick = Environment.TickCount64;
        var filePath = ResolvePreviewBlockFilePath(block);
        YaguLog.For("PreviewEditor").LogDebug(
            "Pointer double-click editor entry: file='{File}', point=({PointX:N1},{PointY:N1})", filePath is null ? "null" : Path.GetFileName(filePath), point.X, point.Y);
        bool opened = await TryEnterPreviewEditorAtPointAsync(block, point, filePath);
        YaguLog.For("PreviewEditor").LogDebug(
            "Pointer double-click editor entry result: opened={Opened}", opened);
    }

    private static bool IsPreviewShowMorePointerSource(object originalSource)
        => TryGetPreviewShowMoreAction(originalSource, out _);

    private static bool TryGetPreviewShowMoreAction(object originalSource, out PreviewShowMoreAction action)
    {
        for (DependencyObject? current = originalSource as DependencyObject;
             current is not null;)
        {
            if (s_previewShowMoreActions.TryGetValue(current, out var value)
                && value is PreviewShowMoreAction showMoreAction)
            {
                action = showMoreAction;
                return true;
            }

            try
            {
                current = VisualTreeHelper.GetParent(current);
            }
            catch (ArgumentException)
            {
                break;
            }
        }

        action = default!;
        return false;
    }

    private void OnPreviewSelectionAutoScrollPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block || !ReferenceEquals(block, _previewSelectionAutoScrollBlock))
            return;
        if (e.Pointer.PointerId != _previewSelectionAutoScrollPointerId)
            return;

        if (_previewSelectionAutoScrollScroller is null)
            return;

        var point = e.GetCurrentPoint(_previewSelectionAutoScrollScroller);
        if (!point.Properties.IsLeftButtonPressed)
        {
            StopPreviewSelectionAutoScroll("left-button-released");
            return;
        }

        _previewSelectionAutoScrollPointerX = point.Position.X;
        _previewSelectionAutoScrollPointerY = point.Position.Y;
        _previewSelectionAutoScrollPointerPointInScroller = point.Position;
        var verticalScroller = _previewSelectionAutoScrollVerticalScroller;
        if (verticalScroller is not null)
        {
            _previewSelectionAutoScrollPointerPointInVerticalScroller =
                e.GetCurrentPoint(verticalScroller).Position;
            _previewSelectionAutoScrollPointerYInVertical =
                _previewSelectionAutoScrollPointerPointInVerticalScroller.Y;
        }
        _previewSelectionAutoScrollPointerMoveCount++;

        bool horizontalEdge = TryGetPreviewSelectionAutoScrollVelocity(
            _previewSelectionAutoScrollScroller,
            _previewSelectionAutoScrollPointerX,
            out double velocity);
        double verticalVelocity = 0;
        bool verticalEdge = verticalScroller is not null
            && TryGetPreviewSelectionAutoScrollVerticalVelocity(
                verticalScroller,
                _previewSelectionAutoScrollPointerYInVertical,
                out verticalVelocity);
        bool isAtEdge = horizontalEdge || verticalEdge;

        if (_previewCustomSelectionDragging)
        {
            int outwardDirection = verticalEdge
                ? Math.Sign(verticalVelocity)
                : horizontalEdge ? Math.Sign(velocity) : 0;
            UpdatePreviewCustomSelectionFromCurrentPointer(outwardDirection);
            e.Handled = true;
        }

        if (isAtEdge && !_previewSelectionAutoScrollWasAtEdge)
            LogPreviewSelectionAutoScrollEdge("edge-enter", _previewSelectionAutoScrollScroller, velocity);
        else if (!isAtEdge && _previewSelectionAutoScrollWasAtEdge)
            LogPreviewSelectionAutoScrollEdge("edge-exit", _previewSelectionAutoScrollScroller, 0);
        _previewSelectionAutoScrollWasAtEdge = isAtEdge;

        if (isAtEdge)
            EnsurePreviewSelectionAutoScrollTimer();
        else
            StopPreviewSelectionAutoScrollTimer("inside-edge");
    }

    private void OnPreviewSelectionAutoScrollPointerEnded(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not RichTextBlock block || !ReferenceEquals(block, _previewSelectionAutoScrollBlock))
            return;
        if (e.Pointer.PointerId == _previewSelectionAutoScrollPointerId)
        {
            if (_previewCustomSelectionDragging)
                e.Handled = true;
            StopPreviewSelectionAutoScroll("pointer-ended");
        }
    }

    private ScrollViewer? ResolvePreviewSelectionAutoScrollScroller(RichTextBlock block)
    {
        if (ReferenceEquals(block, PreviewBlock))
            return PreviewScrollViewer;

        return _sectionMatchNavs.TryGetValue(block, out var sectionNav)
            ? sectionNav.Scroller
            : null;
    }

    // The preview always scrolls vertically through the shared outer
    // PreviewScrollViewer. Section drawers' inner scrollers have vertical
    // scrolling disabled (VerticalScrollBarVisibility = Disabled), so the
    // outer scroller is the correct target for vertical drag-select auto-scroll
    // on both the block and the sections surface.
    private ScrollViewer ResolvePreviewSelectionAutoScrollVerticalScroller(RichTextBlock block)
        => PreviewScrollViewer;

    private void EnsurePreviewSelectionAutoScrollTimer()
    {
        if (_previewSelectionAutoScrollTimerRunning)
            return;

        _previewSelectionAutoScrollTimer ??= new Timer(
            OnPreviewSelectionAutoScrollTimerElapsed,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
        Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 0);
        _previewSelectionAutoScrollLastTick = Environment.TickCount64;
        _previewSelectionAutoScrollTimerRunning = true;
        _previewSelectionAutoScrollTimer.Change(
            PreviewSelectionAutoScrollTimerIntervalMs,
            PreviewSelectionAutoScrollTimerIntervalMs);
        LogPreviewSelectionAutoScrollTimerState("high-timer-start");
    }

    private void StopPreviewSelectionAutoScrollTimer(string reason)
    {
        if (!_previewSelectionAutoScrollTimerRunning)
            return;

        _previewSelectionAutoScrollTimerRunning = false;
        _previewSelectionAutoScrollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 0);
        _previewSelectionAutoScrollLastTick = 0;
        LogPreviewSelectionAutoScrollTimerState($"high-timer-stop:{reason}");
    }

    private void OnPreviewSelectionAutoScrollTimerElapsed(object? state)
    {
        if (!_previewSelectionAutoScrollTimerRunning || _disposed)
            return;
        if (Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 1) != 0)
            return;

        bool queued = DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
            () =>
            {
                Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 0);
                if (!_previewSelectionAutoScrollTimerRunning || _disposed)
                    return;
                OnPreviewSelectionAutoScrollTimerTick();
            });

        if (!queued)
            Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 0);
    }

    private void OnPreviewSelectionAutoScrollTimerTick()
    {
        long now = Environment.TickCount64;
        long last = _previewSelectionAutoScrollLastTick;
        _previewSelectionAutoScrollLastTick = now;
        double rawElapsedSeconds = last == 0
            ? 1d / 60d
            : Math.Max((now - last) / 1000d, 1d / 240d);
        double elapsedSeconds = Math.Min(rawElapsedSeconds, PreviewSelectionAutoScrollMaxFrameSeconds);
        ApplyPreviewSelectionAutoScroll(elapsedSeconds, rawElapsedSeconds);
    }

    private void ApplyPreviewSelectionAutoScroll(double elapsedSeconds, double rawElapsedSeconds)
    {
        var block = _previewSelectionAutoScrollBlock;
        var scroller = _previewSelectionAutoScrollScroller;
        if (block is null || scroller is null)
        {
            StopPreviewSelectionAutoScroll("state-missing");
            return;
        }

        // Vertical auto-scroll always drives the shared outer PreviewScrollViewer.
        // On the sections surface that is a different ScrollViewer than the inner
        // section scroller used for horizontal auto-scroll; on the block surface it
        // is the same object.
        var verticalScroller = _previewSelectionAutoScrollVerticalScroller ?? scroller;

        // Horizontal auto-scroll only applies to no-wrap content that overflows sideways;
        // vertical auto-scroll applies whenever the (outer) content overflows vertically.
        // Supporting both keeps diagonal (45-degree) selection drags reacting naturally
        // instead of only sliding along one axis.
        bool canScrollHorizontally = block.TextWrapping == TextWrapping.NoWrap
            && scroller.HorizontalScrollMode == ScrollMode.Enabled
            && scroller.ScrollableWidth > 0.5;
        bool canScrollVertically = verticalScroller.VerticalScrollMode != ScrollMode.Disabled
            && verticalScroller.ScrollableHeight > 0.5;
        if (!canScrollHorizontally && !canScrollVertically)
        {
            StopPreviewSelectionAutoScroll(
                $"invalid-state wrap={block.TextWrapping}, horizontalMode={scroller.HorizontalScrollMode}, scrollableW={scroller.ScrollableWidth:N1}, scrollableH={verticalScroller.ScrollableHeight:N1}");
            return;
        }

        double velocity = 0;
        bool hasHorizontalVelocity = canScrollHorizontally
            && TryGetPreviewSelectionAutoScrollVelocity(scroller, _previewSelectionAutoScrollPointerX, out velocity);
        double verticalVelocity = 0;
        bool hasVerticalVelocity = canScrollVertically
            && TryGetPreviewSelectionAutoScrollVerticalVelocity(verticalScroller, _previewSelectionAutoScrollPointerYInVertical, out verticalVelocity);
        if (!hasHorizontalVelocity && !hasVerticalVelocity)
        {
            StopPreviewSelectionAutoScrollTimer("no-velocity");
            return;
        }

        double frameMs = elapsedSeconds * 1000d;
        double rawFrameMs = rawElapsedSeconds * 1000d;
        _previewSelectionAutoScrollFrameCount++;
        _previewSelectionAutoScrollMaxFrameMs = Math.Max(_previewSelectionAutoScrollMaxFrameMs, frameMs);
        _previewSelectionAutoScrollMaxRawFrameMs = Math.Max(_previewSelectionAutoScrollMaxRawFrameMs, rawFrameMs);
        if (rawFrameMs >= PreviewSelectionAutoScrollDelayedFrameMs)
            _previewSelectionAutoScrollDelayedFrameCount++;

        double beforeX = scroller.HorizontalOffset;
        double beforeY = verticalScroller.VerticalOffset;
        if (!double.IsNaN(_previewSelectionAutoScrollLastRequestedX))
            _previewSelectionAutoScrollMaxLagDip = Math.Max(
                _previewSelectionAutoScrollMaxLagDip,
                Math.Abs(beforeX - _previewSelectionAutoScrollLastRequestedX));

        double step = velocity * elapsedSeconds;
        double verticalStep = verticalVelocity * elapsedSeconds;
        _previewSelectionAutoScrollTotalRequestedDip += Math.Abs(step) + Math.Abs(verticalStep);
        double targetX = Math.Clamp(scroller.HorizontalOffset + step, 0, scroller.ScrollableWidth);
        double targetY = Math.Clamp(verticalScroller.VerticalOffset + verticalStep, 0, verticalScroller.ScrollableHeight);
        bool horizontalMoved = Math.Abs(targetX - beforeX) > 0.5;
        bool verticalMoved = Math.Abs(targetY - beforeY) > 0.5;
        if (!horizontalMoved && !verticalMoved)
        {
            _previewSelectionAutoScrollNoOpFrameCount++;
            if (_previewCustomSelectionDragging)
                UpdatePreviewCustomSelectionFromCurrentPointer(
                    hasVerticalVelocity ? Math.Sign(verticalVelocity) : Math.Sign(velocity));
            MaybeLogPreviewSelectionAutoScrollSample(scroller, velocity, step, beforeX, targetX, false, frameMs, rawFrameMs, "noop");
            bool horizontalAtBoundary = !hasHorizontalVelocity
                || (beforeX <= 0.5 && step < 0)
                || (beforeX >= scroller.ScrollableWidth - 0.5 && step > 0);
            bool verticalAtBoundary = !hasVerticalVelocity
                || (beforeY <= 0.5 && verticalStep < 0)
                || (beforeY >= verticalScroller.ScrollableHeight - 0.5 && verticalStep > 0);
            if (horizontalAtBoundary && verticalAtBoundary)
                StopPreviewSelectionAutoScrollTimer("scroll-boundary-noop");
            return;
        }

        bool accepted;
        if (ReferenceEquals(scroller, verticalScroller))
        {
            accepted = scroller.ChangeView(
                horizontalMoved ? targetX : (double?)null,
                verticalMoved ? targetY : (double?)null,
                null,
                disableAnimation: true);
        }
        else
        {
            // Sections surface: the horizontal offset lives on the inner section
            // scroller while the vertical offset lives on the outer PreviewScrollViewer.
            accepted = false;
            if (horizontalMoved)
                accepted |= scroller.ChangeView(targetX, null, null, disableAnimation: true);
            if (verticalMoved)
                accepted |= verticalScroller.ChangeView(null, targetY, null, disableAnimation: true);
        }
        if (accepted)
            _previewSelectionAutoScrollChangeViewAcceptedCount++;
        else
            _previewSelectionAutoScrollChangeViewRejectedCount++;
        _previewSelectionAutoScrollLastRequestedX = targetX;

        // Detect a stuck scroller and stop the timer so it can never spin forever. The scroller is
        // "stuck" when neither offset has moved across consecutive frames — whether ChangeView was
        // accepted-but-not-applied OR repeatedly rejected (accepted == false). The earlier version
        // only counted the accepted case; a ScrollViewer that keeps REJECTING ChangeView during a
        // drag-select (observed on very wide single-line previews) left the offset pinned forever,
        // so the timer never terminated and each no-progress frame kept calling the expensive
        // UpdatePreviewCustomSelectionFromCurrentPointer() — ballooning memory until the UI hung.
        if (!double.IsNaN(_previewSelectionAutoScrollPrevBeforeX)
            && Math.Abs(beforeX - _previewSelectionAutoScrollPrevBeforeX) <= 0.5
            && !double.IsNaN(_previewSelectionAutoScrollPrevBeforeY)
            && Math.Abs(beforeY - _previewSelectionAutoScrollPrevBeforeY) <= 0.5)
        {
            _previewSelectionAutoScrollStuckFrameCount++;
            if (_previewSelectionAutoScrollStuckFrameCount >= 5)
            {
                if (_previewCustomSelectionDragging)
                    UpdatePreviewCustomSelectionFromCurrentPointer(
                        hasVerticalVelocity ? Math.Sign(verticalVelocity) : Math.Sign(velocity));
                MaybeLogPreviewSelectionAutoScrollSample(scroller, velocity, step, beforeX, targetX, accepted, frameMs, rawFrameMs, "frame");
                StopPreviewSelectionAutoScrollTimer("stuck-scroller");
                _previewSelectionAutoScrollPrevBeforeX = beforeX;
                _previewSelectionAutoScrollPrevBeforeY = beforeY;
                return;
            }
        }
        else
        {
            _previewSelectionAutoScrollStuckFrameCount = 0;
        }
        _previewSelectionAutoScrollPrevBeforeX = beforeX;
        _previewSelectionAutoScrollPrevBeforeY = beforeY;

        if (_previewCustomSelectionDragging)
            UpdatePreviewCustomSelectionFromCurrentPointer(
                hasVerticalVelocity ? Math.Sign(verticalVelocity) : Math.Sign(velocity));
        MaybeLogPreviewSelectionAutoScrollSample(scroller, velocity, step, beforeX, targetX, accepted, frameMs, rawFrameMs, "frame");
    }

    private static bool TryGetPreviewSelectionAutoScrollVelocity(ScrollViewer scroller, double pointerX, out double velocity)
    {
        velocity = 0;
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

        double magnitude = Math.Clamp(Math.Abs(distanceBeyondEdge) * PreviewSelectionAutoScrollVelocityScale,
            PreviewSelectionAutoScrollMinVelocityDipPerSecond,
            PreviewSelectionAutoScrollMaxVelocityDipPerSecond);
        velocity = Math.Sign(distanceBeyondEdge) * magnitude;
        return Math.Abs(velocity) > 0.5;
    }

    private static bool TryGetPreviewSelectionAutoScrollVerticalVelocity(ScrollViewer scroller, double pointerY, out double velocity)
    {
        velocity = 0;
        double viewportHeight = scroller.ViewportHeight > 0 ? scroller.ViewportHeight : scroller.ActualHeight;
        if (viewportHeight <= 0)
            return false;

        double edge = Math.Min(PreviewSelectionAutoScrollEdgeDip, Math.Max(8, viewportHeight / 4));
        double distanceBeyondEdge;
        if (pointerY > viewportHeight - edge)
            distanceBeyondEdge = pointerY - (viewportHeight - edge);
        else if (pointerY < edge)
            distanceBeyondEdge = pointerY - edge;
        else
            return false;

        double magnitude = Math.Clamp(Math.Abs(distanceBeyondEdge) * PreviewSelectionAutoScrollVelocityScale,
            PreviewSelectionAutoScrollMinVelocityDipPerSecond,
            PreviewSelectionAutoScrollMaxVelocityDipPerSecond);
        velocity = Math.Sign(distanceBeyondEdge) * magnitude;
        return Math.Abs(velocity) > 0.5;
    }

    private static void ConfigurePreviewSelectionMode(RichTextBlock block)
    {
        // Native RichTextBlock text selection (IsTextSelectionEnabled = true) runs a
        // native word-select hit-test on double-tap (TextSelectionManager::OnDoubleTapped
        // -> RichTextBlockView::GetCharacterIndex) that dereferences a stale inline
        // collection while the block is mid-reflow, faulting the process with a native
        // access violation (0xc0000005) that managed try/catch cannot trap. The custom
        // overlay selection below drives BOTH wrap and no-wrap modes, so native
        // selection stays disabled at all times to remove the crash entirely.
        if (block.IsTextSelectionEnabled)
            block.IsTextSelectionEnabled = false;
    }

    // Custom overlay selection now drives both wrap and no-wrap modes (native
    // selection is permanently disabled in ConfigurePreviewSelectionMode), so the
    // custom selection pipeline always applies.
    private static bool ShouldUseCustomPreviewSelection(RichTextBlock block, ScrollViewer scroller)
        => true;

    private void BeginPreviewCustomSelection(RichTextBlock block, ScrollViewer scroller)
    {
        ClearPreviewCustomSelection();
        _previewCustomSelectionBlock = block;
        _previewCustomSelectionDragging = true;
        try { block.Focus(FocusState.Pointer); } catch { }

        bool resolved = TryResolvePreviewSelectionIndexFromCurrentPointer(block, scroller, out int index);
        YaguLog.For("PreviewSelection").LogDebug(
            "BeginPreviewCustomSelection: clicked block={Block}, indexResolved={IndexResolved}, index={Index}", DescribePreviewSelectionBlock(block), resolved, index);
        if (resolved)
        {
            _previewCustomSelectionAnchorIndex = index;
            _previewCustomSelectionCurrentIndex = index;
            UpdatePreviewCustomSelectionHighlighter();
        }
    }

    private void UpdatePreviewCustomSelectionFromCurrentPointer(int outwardDirection = 0)
    {
        var block = _previewCustomSelectionBlock;
        var scroller = _previewSelectionAutoScrollScroller;
        if (!_previewCustomSelectionDragging || block is null || scroller is null)
            return;

        if (!TryResolvePreviewSelectionIndexFromCurrentPointer(block, scroller, out int index))
            return;

        // A ScrollViewer can report a transient stale hit-test position immediately after ChangeView,
        // especially when a wrapped RichTextBlock is reflowing. While the view is moving outward at an
        // edge, never let that stale position pull the endpoint back toward the anchor and visibly erase
        // text the user already selected. An ordinary pointer drag inside the viewport passes direction 0,
        // so intentionally reversing the selection with the mouse remains unchanged.
        index = PreserveOutwardPreviewSelectionEndpoint(
            _previewCustomSelectionCurrentIndex, index, outwardDirection);

        if (index == _previewCustomSelectionCurrentIndex)
            return;

        _previewCustomSelectionCurrentIndex = index;
        UpdatePreviewCustomSelectionHighlighter();
    }

    internal static int PreserveOutwardPreviewSelectionEndpoint(
        int currentIndex,
        int candidateIndex,
        int outwardDirection)
        => outwardDirection > 0
            ? Math.Max(currentIndex, candidateIndex)
            : outwardDirection < 0
                ? Math.Min(currentIndex, candidateIndex)
                : candidateIndex;

    /// <summary>
    /// Extends a held drag-selection when the preview is scrolled by the mouse wheel, touchpad, or
    /// scrollbar. ViewChanged previously repainted the old range only; the endpoint did not follow the
    /// text under the captured pointer unless the custom auto-scroll timer also happened to be running.
    /// </summary>
    private void UpdatePreviewCustomSelectionForViewChange()
    {
        if (!_previewCustomSelectionDragging)
            return;

        var verticalScroller = _previewSelectionAutoScrollVerticalScroller ?? PreviewScrollViewer;
        double currentOffset = verticalScroller.VerticalOffset;
        int direction = double.IsNaN(_previewSelectionLastViewOffsetY)
            ? 0
            : currentOffset > _previewSelectionLastViewOffsetY + 0.5
                ? 1
                : currentOffset < _previewSelectionLastViewOffsetY - 0.5 ? -1 : 0;
        _previewSelectionLastViewOffsetY = currentOffset;
        if (direction != 0)
            UpdatePreviewCustomSelectionFromCurrentPointer(direction);
    }

    private bool TryResolvePreviewSelectionIndexFromCurrentPointer(RichTextBlock block, ScrollViewer scroller, out int index)
    {
        index = 0;
        Point blockPoint;
        try
        {
            blockPoint = scroller.TransformToVisual(block).TransformPoint(_previewSelectionAutoScrollPointerPointInScroller);
        }
        catch
        {
            blockPoint = new Point(
                scroller.HorizontalOffset + _previewSelectionAutoScrollPointerX,
                _previewSelectionAutoScrollPointerPointInScroller.Y);
        }

        // The vertical offset is owned by the (possibly different) outer vertical
        // scroller, so resolve Y through it. This keeps the selection extending while
        // the outer scroller auto-scrolls under a held-still pointer on the sections
        // surface, matching how every text editor follows the caret during drag-select.
        var verticalScroller = _previewSelectionAutoScrollVerticalScroller;
        if (verticalScroller is not null && !ReferenceEquals(verticalScroller, scroller))
        {
            try
            {
                Point verticalBlockPoint = verticalScroller.TransformToVisual(block)
                    .TransformPoint(_previewSelectionAutoScrollPointerPointInVerticalScroller);
                blockPoint.Y = verticalBlockPoint.Y;
            }
            catch
            {
                // Keep the horizontal-scroller Y on failure.
            }
        }

        if (block.ActualWidth > 1)
            blockPoint.X = Math.Clamp(blockPoint.X, 0, block.ActualWidth - 1);
        if (block.ActualHeight > 1)
            blockPoint.Y = Math.Clamp(blockPoint.Y, 0, block.ActualHeight - 1);

        TextPointer? pointer;
        try { pointer = block.GetPositionFromPoint(blockPoint); }
        catch { pointer = null; }
        if (pointer is null)
            return false;

        index = MapPreviewTextPointerToBlockIndex(block, pointer);
        return true;
    }

    private int MapPreviewTextPointerToBlockIndex(RichTextBlock block, TextPointer pointer)
    {
        int pointerOffset = pointer.Offset;
        ParagraphMetrics metrics = GetPreviewSelectionParagraphMetrics(block);
        Paragraph[] paragraphs = metrics.TextParagraphs!;
        int[] textStarts = metrics.TextStarts!;
        int[] textLengths = metrics.TextLengths!;
        int[] nativeStarts = metrics.NativeStarts!;
        int[] nativeEnds = metrics.NativeEnds!;
        for (int i = 0; i < paragraphs.Length; i++)
        {
            if (pointerOffset <= nativeStarts[i])
                return textStarts[i];
            if (pointerOffset <= nativeEnds[i])
            {
                int localIndex = MapPreviewTextPointerToParagraphIndex(paragraphs[i], pointerOffset, textLengths[i]);
                return textStarts[i] + localIndex;
            }
        }
        return paragraphs.Length == 0
            ? 0
            : textStarts[^1] + textLengths[^1] + 1;
    }

    /// <summary>
    /// Returns the custom-selection paragraph map for <paramref name="block"/>. Character lengths and
    /// native TextPointer offsets are computed once per stable Blocks collection rather than once per
    /// pointer frame. The shared paragraph cache is invalidated at every preview mutation site.
    /// </summary>
    private ParagraphMetrics GetPreviewSelectionParagraphMetrics(RichTextBlock block)
    {
        ParagraphMetrics metrics = GetParagraphMetrics(block);
        if (metrics.TextParagraphs is not null
            && metrics.TextMetricsBlockCount == block.Blocks.Count)
        {
            return metrics;
        }

        var paragraphs = new List<Paragraph>(block.Blocks.Count);
        var textStarts = new List<int>(block.Blocks.Count);
        var textLengths = new List<int>(block.Blocks.Count);
        var nativeStarts = new List<int>(block.Blocks.Count);
        var nativeEnds = new List<int>(block.Blocks.Count);
        int textStart = 0;
        foreach (Block textBlock in block.Blocks)
        {
            if (textBlock is not Paragraph paragraph)
                continue;

            int textLength = GetParagraphTextLength(paragraph);
            paragraphs.Add(paragraph);
            textStarts.Add(textStart);
            textLengths.Add(textLength);
            nativeStarts.Add(paragraph.ContentStart.Offset);
            nativeEnds.Add(paragraph.ContentEnd.Offset);
            textStart += textLength + 1;
        }

        metrics.TextParagraphs = paragraphs.ToArray();
        metrics.TextStarts = textStarts.ToArray();
        metrics.TextLengths = textLengths.ToArray();
        metrics.NativeStarts = nativeStarts.ToArray();
        metrics.NativeEnds = nativeEnds.ToArray();
        metrics.TextMetricsBlockCount = block.Blocks.Count;
        return metrics;
    }

    private static int MapPreviewTextPointerToParagraphIndex(Paragraph paragraph, int pointerOffset, int paragraphLength)
    {
        int localIndex = 0;
        foreach (var inline in paragraph.Inlines)
        {
            if (inline is not Run run)
                continue;

            int runLength = run.Text?.Length ?? 0;
            int runStart = run.ContentStart.Offset;
            int runEnd = run.ContentEnd.Offset;
            if (pointerOffset <= runStart)
                return localIndex;
            if (pointerOffset <= runEnd)
                return Math.Clamp(localIndex + pointerOffset - runStart, 0, paragraphLength);

            localIndex += runLength;
        }

        return Math.Clamp(pointerOffset - paragraph.ContentStart.Offset, 0, paragraphLength);
    }

    private void UpdatePreviewCustomSelectionHighlighter()
    {
        var block = _previewCustomSelectionBlock;
        if (block is null)
            return;

        int startIndex = Math.Min(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        int endIndex = Math.Max(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        if (ReferenceEquals(_previewCustomSelectionLastRangeBlock, block)
            && startIndex == _previewCustomSelectionLastRangeStart
            && endIndex == _previewCustomSelectionLastRangeEnd)
        {
            return;
        }

        if (startIndex < 0 || endIndex <= startIndex)
        {
            RemovePreviewCustomSelectionHighlighter();
            return;
        }

        if (!ReferenceEquals(_previewCustomSelectionHighlighterBlock, block))
            RemovePreviewCustomSelectionHighlighter();

        DrawPreviewCustomSelectionOverlay(block, startIndex, endIndex);
        _previewCustomSelectionHighlighterBlock = block;
        _previewCustomSelectionLastRangeBlock = block;
        _previewCustomSelectionLastRangeStart = startIndex;
        _previewCustomSelectionLastRangeEnd = endIndex;
    }

    private void RefreshPreviewCustomSelectionOverlay()
    {
        var block = _previewCustomSelectionBlock;
        if (block is null || !HasPreviewCustomSelection(block))
        {
            ClearPreviewCustomSelectionOverlay();
            return;
        }

        int startIndex = Math.Min(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        int endIndex = Math.Max(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        DrawPreviewCustomSelectionOverlay(block, startIndex, endIndex);
    }

    // The selection overlay Canvas shares its top edge with the sticky file header that
    // floats over the top of the preview content when a section header has scrolled out
    // of view. Because the overlay sits at a higher z-index than that header, a selection
    // band whose top lands above the header bottom — e.g. while dragging/auto-scrolling
    // the selection upward — would paint over it. This returns the overlay-space Y of the
    // header's bottom edge so bands can be clamped below it (0 when nothing is pinned,
    // which also keeps a partially-scrolled band from spilling above the overlay).
    private double ResolvePreviewSelectionOverlayTopClip()
    {
        if (StickyFileHeader.Visibility != Visibility.Visible || StickyFileHeader.ActualHeight <= 0)
            return 0;

        try
        {
            double headerBottom = StickyFileHeader
                .TransformToVisual(PreviewSelectionOverlay)
                .TransformPoint(new Point(0, StickyFileHeader.ActualHeight)).Y;
            return Math.Max(0, headerBottom);
        }
        catch
        {
            return 0;
        }
    }

    private void DrawPreviewCustomSelectionOverlay(
        RichTextBlock block,
        int selectionStart,
        int selectionEnd)
    {
        double overlayWidth = PreviewSelectionOverlay.ActualWidth > 0
            ? PreviewSelectionOverlay.ActualWidth
            : PreviewScrollViewer.ActualWidth;
        double overlayHeight = PreviewSelectionOverlay.ActualHeight > 0
            ? PreviewSelectionOverlay.ActualHeight
            : PreviewScrollViewer.ActualHeight;
        if (overlayWidth <= 0 || overlayHeight <= 0)
        {
            ClearPreviewCustomSelectionOverlay();
            return;
        }

        // One shared marker pool across every drawer, so the cap bounds the whole selection.
        int markerIndex = 0;
        var group = _previewCustomSelectionGroupBlocks;
        if (group is null)
        {
            DrawPreviewCustomSelectionBands(block, selectionStart, selectionEnd, overlayWidth, overlayHeight, ref markerIndex);
        }
        else
        {
            foreach (var drawer in group)
            {
                if (ReferenceEquals(drawer, block))
                    DrawPreviewCustomSelectionBands(drawer, selectionStart, selectionEnd, overlayWidth, overlayHeight, ref markerIndex);
                else
                    DrawPreviewCustomSelectionBands(drawer, 0, GetBlockTotalTextLength(drawer), overlayWidth, overlayHeight, ref markerIndex);

                if (markerIndex >= PreviewCustomSelectionOverlayMaxMarkers)
                    break;
            }
        }

        for (int index = markerIndex; index < _previewCustomSelectionOverlayMarkers.Count; index++)
            _previewCustomSelectionOverlayMarkers[index].Visibility = Visibility.Collapsed;

        PreviewSelectionOverlay.Visibility = Visibility.Visible;
    }

    private void DrawPreviewCustomSelectionBands(
        RichTextBlock block,
        int selectionStart,
        int selectionEnd,
        double overlayWidth,
        double overlayHeight,
        ref int markerIndex)
    {
        if (selectionEnd <= selectionStart)
            return;

        // Clamp every highlight band to the visible content region of the BLOCK's own horizontal
        // scroller — the per-section content scroller on the sections surface (which sits to the RIGHT
        // of the line-number gutter and is narrower than the outer viewer), or the outer
        // PreviewScrollViewer for the single-file block. This keeps a band inside
        // [content-left, content-right] so it can never paint over the gutter on the left nor spill
        // past the visible right edge, in either wrap or no-wrap mode. Off-screen selected text stays
        // tracked for copy; it just isn't painted until the user scrolls it into view.
        var horizontalScroller = ResolvePreviewSelectionAutoScrollScroller(block) ?? PreviewScrollViewer;
        double scrollerLeftOverlay = 0;
        double scrollerViewportWidth = overlayWidth;
        try
        {
            var scrollerToOverlay = horizontalScroller.TransformToVisual(PreviewSelectionOverlay);
            scrollerLeftOverlay = scrollerToOverlay.TransformPoint(new Point(0, 0)).X;
            double viewportWidth = horizontalScroller.ViewportWidth > 0
                ? horizontalScroller.ViewportWidth
                : horizontalScroller.ActualWidth;
            if (viewportWidth > 0)
                scrollerViewportWidth = viewportWidth;
        }
        catch
        {
            // Fall back to the full overlay width when the scroller transform is unavailable.
        }
        double scrollerLeftBound = Math.Max(0, scrollerLeftOverlay);
        double contentRightBound = Math.Min(overlayWidth, scrollerLeftOverlay + scrollerViewportWidth);
        double topClipBound = ResolvePreviewSelectionOverlayTopClip();
        bool hasInlineGutter = !_sectionGutterBlocks.ContainsKey(block);

        ParagraphMetrics metrics = GetPreviewSelectionParagraphMetrics(block);
        Paragraph[] paragraphs = metrics.TextParagraphs!;
        int[] paragraphStarts = metrics.TextStarts!;
        int[] paragraphLengths = metrics.TextLengths!;
        int firstParagraphIndex = 0;
        int lastParagraphIndex = paragraphs.Length - 1;
        if (TryResolveVisiblePreviewSelectionIndexRange(
                block, out int visibleStartIndex, out int visibleEndIndex))
        {
            // One-paragraph overscan avoids a one-frame seam while a wrapped paragraph crosses the
            // viewport edge. More importantly, selection painting is now O(visible paragraphs), not
            // O(every paragraph selected since the original mouse-down).
            firstParagraphIndex = Math.Max(
                0, FindPreviewSelectionParagraphIndex(paragraphStarts, paragraphLengths, visibleStartIndex) - 1);
            lastParagraphIndex = Math.Min(
                paragraphs.Length - 1,
                FindPreviewSelectionParagraphIndex(paragraphStarts, paragraphLengths, visibleEndIndex) + 1);
        }

        for (int paragraphIndex = firstParagraphIndex; paragraphIndex <= lastParagraphIndex; paragraphIndex++)
        {
            Paragraph paragraph = paragraphs[paragraphIndex];
            int paragraphLength = paragraphLengths[paragraphIndex];
            int paragraphStart = paragraphStarts[paragraphIndex];
            int paragraphEnd = paragraphStart + paragraphLength;

            if (paragraphEnd <= selectionStart)
                continue;
            if (paragraphStart >= selectionEnd)
                break;

            int rangeStart = Math.Max(selectionStart, paragraphStart);
            int rangeEnd = Math.Min(selectionEnd, paragraphEnd);
            if (rangeEnd <= rangeStart)
                continue;

            var firstRun = paragraph.Inlines.OfType<Run>().FirstOrDefault(run => !string.IsNullOrEmpty(run.Text));
            if (firstRun is null)
                continue;

            Windows.Foundation.Rect rect;
            try { rect = firstRun.ContentStart.GetCharacterRect(LogicalDirection.Forward); }
            catch { continue; }
            if (!IsUsableTextRect(rect))
                continue;

            Point origin;
            try
            {
                origin = block.TransformToVisual(PreviewSelectionOverlay).TransformPoint(new Point(rect.X, rect.Y));
            }
            catch
            {
                continue;
            }

            double charWidth = Math.Max(1, GetPreviewCharWidth(block, paragraph));
            double markerHeight = Math.Max(12, rect.Height > 0 ? rect.Height : block.LineHeight);
            double top = origin.Y;
            if (top + markerHeight < 0 || top > overlayHeight)
                continue;

            int localStart = rangeStart - paragraphStart;
            int localEnd = rangeEnd - paragraphStart;

            if (block.TextWrapping == TextWrapping.Wrap
                && TryBuildWrappedPreviewSelectionRows(
                    block, paragraph, localStart, localEnd, rect, markerHeight, scrollerLeftBound, contentRightBound, topClipBound, overlayHeight, out var wrappedRows))
            {
                bool wrapCapReached = false;
                foreach (var rowRect in wrappedRows)
                {
                    var wrapMarker = GetPreviewCustomSelectionOverlayMarker(markerIndex++);
                    wrapMarker.Width = rowRect.Width;
                    wrapMarker.Height = rowRect.Height;
                    wrapMarker.Visibility = Visibility.Visible;
                    Canvas.SetLeft(wrapMarker, rowRect.X);
                    Canvas.SetTop(wrapMarker, rowRect.Y);
                    if (markerIndex >= PreviewCustomSelectionOverlayMaxMarkers)
                    {
                        wrapCapReached = true;
                        break;
                    }
                }
                if (wrapCapReached)
                    break;
                continue;
            }

            double left = origin.X + localStart * charWidth;
            double right = origin.X + localEnd * charWidth;
            // Prefer the real glyph edges over the uniform charWidth estimate so the
            // highlight does not overshoot past the last character of the line (the
            // estimate can be wider than the rendered glyphs, which made the blue band
            // extend beyond the text boundary in NoWrap mode).
            if (TryResolvePreviewSelectionEdgeX(block, paragraph, localStart, trailingEdge: false, out double actualLeft))
                left = actualLeft;
            if (TryResolvePreviewSelectionEdgeX(block, paragraph, localEnd, trailingEdge: true, out double actualRight))
                right = actualRight;
            if (double.IsNaN(left) || double.IsNaN(right) || double.IsInfinity(left) || double.IsInfinity(right))
                continue;

            // Clamp the band's left edge to the content column so a fully-selected line does
            // not paint the blue band over the inline line-number gutter. For the inline-gutter
            // single-file surface the content starts after the gutter runs; section content
            // blocks have no inline gutter (their gutter is a separate block), so this starts at
            // the section content scroller's left edge (scrollerLeftBound).
            double contentLeftBound = scrollerLeftBound;
            if (hasInlineGutter
                && TryGetParagraphInlineGutterLength(paragraph, out int gutterCharLength)
                && gutterCharLength > 0)
            {
                var contentStartPointer = GetPreviewParagraphTextPointerAtIndex(paragraph, gutterCharLength);
                if (contentStartPointer is not null)
                {
                    try
                    {
                        var contentStartRect = contentStartPointer.GetCharacterRect(LogicalDirection.Forward);
                        if (IsUsableTextRect(contentStartRect))
                        {
                            double contentLeftOverlay = block
                                .TransformToVisual(PreviewSelectionOverlay)
                                .TransformPoint(new Point(contentStartRect.X, contentStartRect.Y)).X;
                            contentLeftBound = Math.Max(scrollerLeftBound, contentLeftOverlay);
                        }
                    }
                    catch
                    {
                        // Fall back to the viewport-left clamp when the content edge is unmeasured.
                    }
                }
            }

            double visibleLeft = Math.Max(contentLeftBound, left);
            double visibleRight = Math.Min(contentRightBound, right);
            double width = visibleRight - visibleLeft;
            if (width <= 0)
                continue;

            // Clamp the band's top below any pinned sticky file header so an upward drag
            // does not paint the highlight over that header (the overlay is drawn on top).
            double bandTop = top;
            double bandHeight = markerHeight;
            if (bandTop < topClipBound)
            {
                bandHeight -= topClipBound - bandTop;
                bandTop = topClipBound;
            }
            if (bandHeight <= 0)
                continue;

            var marker = GetPreviewCustomSelectionOverlayMarker(markerIndex++);
            marker.Width = width;
            marker.Height = bandHeight;
            marker.Visibility = Visibility.Visible;
            Canvas.SetLeft(marker, visibleLeft);
            Canvas.SetTop(marker, bandTop);

            if (markerIndex >= PreviewCustomSelectionOverlayMaxMarkers)
                break;
        }
    }

    private bool TryResolveVisiblePreviewSelectionIndexRange(
        RichTextBlock block,
        out int visibleStartIndex,
        out int visibleEndIndex)
    {
        visibleStartIndex = 0;
        visibleEndIndex = 0;
        var verticalScroller = _previewSelectionAutoScrollVerticalScroller ?? PreviewScrollViewer;
        double viewportWidth = verticalScroller.ViewportWidth > 0
            ? verticalScroller.ViewportWidth
            : verticalScroller.ActualWidth;
        double viewportHeight = verticalScroller.ViewportHeight > 0
            ? verticalScroller.ViewportHeight
            : verticalScroller.ActualHeight;
        if (viewportWidth <= 1 || viewportHeight <= 1 || block.ActualWidth <= 1 || block.ActualHeight <= 1)
            return false;

        try
        {
            GeneralTransform toBlock = verticalScroller.TransformToVisual(block);
            Point topPoint = toBlock.TransformPoint(new Point(viewportWidth / 2, 0));
            Point bottomPoint = toBlock.TransformPoint(new Point(viewportWidth / 2, viewportHeight));
            topPoint.X = Math.Clamp(topPoint.X, 0, block.ActualWidth - 1);
            bottomPoint.X = Math.Clamp(bottomPoint.X, 0, block.ActualWidth - 1);
            topPoint.Y = Math.Clamp(topPoint.Y, 0, block.ActualHeight - 1);
            bottomPoint.Y = Math.Clamp(bottomPoint.Y, 0, block.ActualHeight - 1);

            TextPointer? topPointer = block.GetPositionFromPoint(topPoint);
            TextPointer? bottomPointer = block.GetPositionFromPoint(bottomPoint);
            if (topPointer is null || bottomPointer is null)
                return false;

            int topIndex = MapPreviewTextPointerToBlockIndex(block, topPointer);
            int bottomIndex = MapPreviewTextPointerToBlockIndex(block, bottomPointer);
            visibleStartIndex = Math.Min(topIndex, bottomIndex);
            visibleEndIndex = Math.Max(topIndex, bottomIndex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int FindPreviewSelectionParagraphIndex(
        int[] paragraphStarts,
        int[] paragraphLengths,
        int textIndex)
    {
        if (paragraphStarts.Length == 0)
            return 0;
        int found = Array.BinarySearch(paragraphStarts, textIndex);
        if (found >= 0)
            return found;
        int insertion = ~found;
        int previous = Math.Clamp(insertion - 1, 0, paragraphStarts.Length - 1);
        return textIndex <= paragraphStarts[previous] + paragraphLengths[previous] + 1
            ? previous
            : Math.Clamp(insertion, 0, paragraphStarts.Length - 1);
    }

    // Builds the highlight rectangles (in PreviewSelectionOverlay coordinates) for a
    // wrapped paragraph's selection sub-range. A selection that spans multiple visual
    // rows is rendered as the partial first row, a full-width middle band, and the
    // partial last row. Returns false (caller falls back to the single-row path) when
    // the native character rects cannot be resolved.
    private bool TryBuildWrappedPreviewSelectionRows(
        RichTextBlock block,
        Paragraph paragraph,
        int localStart,
        int localEnd,
        Windows.Foundation.Rect paragraphFirstCharRect,
        double markerHeight,
        double clampLeft,
        double contentRightBound,
        double topClip,
        double overlayHeight,
        out List<Windows.Foundation.Rect> rows)
    {
        rows = new List<Windows.Foundation.Rect>(3);
        if (localEnd <= localStart)
            return false;

        var startPointer = GetPreviewParagraphTextPointerAtIndex(paragraph, localStart);
        var endPointer = GetPreviewParagraphTextPointerAtIndex(paragraph, localEnd);
        if (startPointer is null || endPointer is null)
            return false;

        Windows.Foundation.Rect startRect, endRect;
        try
        {
            startRect = startPointer.GetCharacterRect(LogicalDirection.Forward);
            endRect = endPointer.GetCharacterRect(LogicalDirection.Backward);
        }
        catch
        {
            return false;
        }
        if (!IsUsableTextRect(startRect) || !IsUsableTextRect(endRect))
            return false;

        GeneralTransform toOverlay;
        try { toOverlay = block.TransformToVisual(PreviewSelectionOverlay); }
        catch { return false; }

        // Continuation (wrapped) rows start at the paragraph's true content-left edge,
        // i.e. the position BEFORE any leading inline such as a prefix "show more"
        // ellipsis InlineUIContainer. paragraphFirstCharRect is the first *Run*'s X,
        // which sits AFTER such a leading inline, so using it would push the continuation
        // bands right and leave an un-highlighted gap on the left of every wrapped row.
        double contentLeftBlock = paragraphFirstCharRect.X;
        try
        {
            var paragraphStartRect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (IsUsableTextRect(paragraphStartRect))
                contentLeftBlock = Math.Min(contentLeftBlock, paragraphStartRect.X);
        }
        catch
        {
            // Keep the first-run X fallback when the paragraph start rect is unavailable.
        }
        double contentRightBlock = Math.Max(contentLeftBlock + 1, block.ActualWidth);
        double rowHeight = Math.Max(markerHeight, startRect.Height > 0 ? startRect.Height : markerHeight);
        bool sameRow = Math.Abs(startRect.Y - endRect.Y) <= rowHeight * 0.5;

        if (sameRow)
        {
            AddOverlayBandRect(toOverlay, startRect.X, startRect.Y, endRect.X, startRect.Y + rowHeight,
                clampLeft, contentRightBound, topClip, overlayHeight, rows);
        }
        else
        {
            // Partial first row: selection start -> content right edge.
            AddOverlayBandRect(toOverlay, startRect.X, startRect.Y, contentRightBlock, startRect.Y + rowHeight,
                clampLeft, contentRightBound, topClip, overlayHeight, rows);
            // Full-width middle band covering every row strictly between start and end.
            double midTopBlock = startRect.Y + rowHeight;
            if (endRect.Y - midTopBlock > 1)
                AddOverlayBandRect(toOverlay, contentLeftBlock, midTopBlock, contentRightBlock, endRect.Y,
                    clampLeft, contentRightBound, topClip, overlayHeight, rows);
            // Partial last row: content left edge -> selection end.
            AddOverlayBandRect(toOverlay, contentLeftBlock, endRect.Y, endRect.X, endRect.Y + rowHeight,
                clampLeft, contentRightBound, topClip, overlayHeight, rows);
        }

        return rows.Count > 0;
    }

    // Transforms a block-space band [leftBlock,topBlock]-[rightBlock,bottomBlock] into
    // overlay-space and appends it (clamped to the overlay viewport) when it is valid
    // and visible.
    private static void AddOverlayBandRect(
        GeneralTransform toOverlay,
        double leftBlock,
        double topBlock,
        double rightBlock,
        double bottomBlock,
        double clampLeft,
        double clampRight,
        double topClip,
        double overlayHeight,
        List<Windows.Foundation.Rect> rows)
    {
        if (rightBlock <= leftBlock || bottomBlock <= topBlock)
            return;

        Point topLeft, bottomRight;
        try
        {
            topLeft = toOverlay.TransformPoint(new Point(leftBlock, topBlock));
            bottomRight = toOverlay.TransformPoint(new Point(rightBlock, bottomBlock));
        }
        catch
        {
            return;
        }

        double left = Math.Min(topLeft.X, bottomRight.X);
        double right = Math.Max(topLeft.X, bottomRight.X);
        double top = Math.Min(topLeft.Y, bottomRight.Y);
        double bottom = Math.Max(topLeft.Y, bottomRight.Y);
        if (double.IsNaN(left) || double.IsNaN(right) || double.IsNaN(top) || double.IsNaN(bottom)
            || double.IsInfinity(left) || double.IsInfinity(right) || double.IsInfinity(top) || double.IsInfinity(bottom))
            return;

        double visibleLeft = Math.Max(clampLeft, left);
        double visibleRight = Math.Min(clampRight, right);
        // Clamp the band below any pinned sticky file header (topClip) so an upward drag
        // does not paint the highlight over that header.
        double clampedTop = Math.Max(topClip, top);
        double width = visibleRight - visibleLeft;
        double height = bottom - clampedTop;
        if (width <= 0 || height <= 0)
            return;
        if (clampedTop > overlayHeight)
            return;

        rows.Add(new Windows.Foundation.Rect(visibleLeft, clampedTop, width, height));
    }

    // Resolves the overlay-space X of a paragraph-local character edge using the real
    // glyph rect (leading edge for the selection start, trailing edge for the end) so
    // the NoWrap selection band ends exactly at the text rather than at an estimated
    // charWidth multiple. Returns false when the rect cannot be measured.
    private bool TryResolvePreviewSelectionEdgeX(RichTextBlock block, Paragraph paragraph, int localIndex, bool trailingEdge, out double overlayX)
    {
        overlayX = 0;
        var pointer = GetPreviewParagraphTextPointerAtIndex(paragraph, localIndex);
        if (pointer is null)
            return false;
        Windows.Foundation.Rect rect;
        try { rect = pointer.GetCharacterRect(trailingEdge ? LogicalDirection.Backward : LogicalDirection.Forward); }
        catch { return false; }
        if (!IsUsableTextRect(rect))
            return false;
        try
        {
            overlayX = block.TransformToVisual(PreviewSelectionOverlay).TransformPoint(new Point(rect.X, rect.Y)).X;
            return true;
        }
        catch { return false; }
    }

    // Resolves a TextPointer at a paragraph-local character index by walking the
    // paragraph's Run inlines (mirrors MapPreviewTextPointerToParagraphIndex, which
    // counts only Run text).
    private static TextPointer? GetPreviewParagraphTextPointerAtIndex(Paragraph paragraph, int localIndex)
    {
        if (localIndex <= 0)
        {
            var first = paragraph.Inlines.OfType<Run>().FirstOrDefault(r => !string.IsNullOrEmpty(r.Text));
            try { return first?.ContentStart ?? paragraph.ContentStart; }
            catch { return null; }
        }

        int accumulated = 0;
        foreach (var inline in paragraph.Inlines)
        {
            if (inline is not Run run)
                continue;
            int runLength = run.Text?.Length ?? 0;
            if (localIndex <= accumulated + runLength)
            {
                try { return run.ContentStart.GetPositionAtOffset(localIndex - accumulated, LogicalDirection.Forward); }
                catch { return null; }
            }
            accumulated += runLength;
        }

        var last = paragraph.Inlines.OfType<Run>().LastOrDefault(r => !string.IsNullOrEmpty(r.Text));
        try { return last?.ContentEnd ?? paragraph.ContentEnd; }
        catch { return null; }
    }

    private Border GetPreviewCustomSelectionOverlayMarker(int markerIndex)
    {
        while (_previewCustomSelectionOverlayMarkers.Count <= markerIndex)
        {
            var marker = new Border
            {
                Background = _previewCustomSelectionOverlayBrush,
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Canvas.SetZIndex(marker, 1);
            _previewCustomSelectionOverlayMarkers.Add(marker);
            PreviewSelectionOverlay.Children.Add(marker);
        }

        return _previewCustomSelectionOverlayMarkers[markerIndex];
    }

    private void ClearPreviewCustomSelectionOverlay()
    {
        foreach (var marker in _previewCustomSelectionOverlayMarkers)
            marker.Visibility = Visibility.Collapsed;
        PreviewSelectionOverlay.Visibility = Visibility.Visible;
    }

    private RichTextBlock? _previewCustomSelectionHighlighterBlock;

    private void ClearPreviewCustomSelection()
    {
        RemovePreviewCustomSelectionHighlighter();
        _previewCustomSelectionBlock = null;
        _previewCustomSelectionGroupBlocks = null;
        _previewCustomSelectionAnchorIndex = -1;
        _previewCustomSelectionCurrentIndex = -1;
        _previewCustomSelectionDragging = false;
    }

    private void RemovePreviewCustomSelectionHighlighter()
    {
        if (_previewCustomSelectionHighlighterBlock is not null && _previewCustomSelectionHighlighter is not null)
            _previewCustomSelectionHighlighterBlock.TextHighlighters.Remove(_previewCustomSelectionHighlighter);
        _previewCustomSelectionHighlighterBlock = null;
        _previewCustomSelectionHighlighter = null;
        ClearPreviewCustomSelectionOverlay();
        _previewCustomSelectionLastRangeBlock = null;
        _previewCustomSelectionLastRangeStart = -1;
        _previewCustomSelectionLastRangeEnd = -1;
    }

    private bool HasPreviewCustomSelection(RichTextBlock block)
        => (ReferenceEquals(_previewCustomSelectionBlock, block) || IsPreviewSelectionGroupMember(block))
           && Math.Abs(_previewCustomSelectionCurrentIndex - _previewCustomSelectionAnchorIndex) > 0;

    private bool IsPreviewSelectionGroupMember(RichTextBlock block)
    {
        var group = _previewCustomSelectionGroupBlocks;
        if (group is null)
            return false;
        foreach (var drawer in group)
        {
            if (ReferenceEquals(drawer, block))
                return true;
        }
        return false;
    }

    private bool TryBuildPreviewCustomSelectionText(RichTextBlock block, bool withLineNumbers, out string text)
    {
        text = string.Empty;
        if (!HasPreviewCustomSelection(block))
            return false;

        int selectionStart = Math.Min(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        int selectionEnd = Math.Max(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);

        var group = _previewCustomSelectionGroupBlocks;
        if (group is null)
        {
            text = BuildPreviewDrawerSelectionText(block, selectionStart, selectionEnd, withLineNumbers);
            return text.Length > 0;
        }

        // Continuation drawers are one file split for layout, so copy them back as one document.
        // The ranged drawer is the one Ctrl+A ran in, not whichever drawer asked for the copy.
        var primary = _previewCustomSelectionBlock;
        var combined = new StringBuilder();
        foreach (var drawer in group)
        {
            string part = ReferenceEquals(drawer, primary)
                ? BuildPreviewDrawerSelectionText(drawer, selectionStart, selectionEnd, withLineNumbers)
                : BuildPreviewDrawerSelectionText(drawer, 0, GetBlockTotalTextLength(drawer), withLineNumbers);
            if (part.Length == 0)
                continue;
            if (combined.Length > 0)
                combined.AppendLine();
            combined.Append(part);
        }

        text = combined.ToString();
        YaguLog.For("PreviewSelection").LogDebug(
            "TryBuildPreviewCustomSelectionText: drawers={Drawers}, length={Length}", group.Count, text.Length);
        return text.Length > 0;
    }

    private string BuildPreviewDrawerSelectionText(
        RichTextBlock block, int selectionStart, int selectionEnd, bool withLineNumbers)
    {
        if (selectionEnd <= selectionStart)
            return string.Empty;

        bool hasInlineGutter = !_sectionGutterBlocks.ContainsKey(block);
        var selectedText = new StringBuilder();
        int blockIndex = 0;
        bool firstLine = true;
        int lastEmittedLineNumber = -1;

        foreach (var textBlock in block.Blocks)
        {
            if (textBlock is not Paragraph paragraph)
                continue;

            int paragraphLength = GetParagraphTextLength(paragraph);
            int paragraphStart = blockIndex;
            int paragraphEnd = paragraphStart + paragraphLength;
            blockIndex += paragraphLength + 1;

            if (paragraphEnd <= selectionStart)
                continue;
            if (paragraphStart >= selectionEnd)
                break;

            bool hasLineNumber = s_paragraphLineNumbers.TryGetValue(paragraph, out _);
            bool isContinuationTag = s_paragraphIsContinuation.TryGetValue(paragraph, out _);
            if (!hasInlineGutter && !hasLineNumber && !isContinuationTag)
                continue;

            string paragraphText = ExtractParagraphContent(paragraph, hasInlineGutter);
            int paragraphContentOffset = hasInlineGutter ? GetInlineGutterTextLength(paragraph) : 0;
            int localStart = Math.Max(selectionStart, paragraphStart) - paragraphStart - paragraphContentOffset;
            int localEnd = Math.Min(selectionEnd, paragraphEnd) - paragraphStart - paragraphContentOffset;
            localStart = Math.Clamp(localStart, 0, paragraphText.Length);
            localEnd = Math.Clamp(localEnd, 0, paragraphText.Length);
            if (localEnd <= localStart && paragraphText.Length > 0)
                continue;

            string slice = paragraphText.Substring(localStart, localEnd - localStart);
            int lineNumber = ResolveParagraphLineNumber(paragraph, hasInlineGutter);
            bool isContinuation = isContinuationTag || (lineNumber > 0 && lineNumber == lastEmittedLineNumber);

            if (!firstLine)
                selectedText.AppendLine();
            firstLine = false;

            if (withLineNumbers)
            {
                if (lineNumber > 0 && !isContinuation)
                {
                    selectedText.Append(CultureInfo.InvariantCulture, $"{lineNumber,5} \u2502 {slice}");
                    lastEmittedLineNumber = lineNumber;
                }
                else
                {
                    selectedText.Append(CultureInfo.InvariantCulture, $"      \u2502 {slice}");
                }
            }
            else
            {
                selectedText.Append(slice);
            }
        }

        return selectedText.ToString();
    }

    private static int GetInlineGutterTextLength(Paragraph paragraph)
    {
        int length = 0;
        int index = 0;
        foreach (var inline in paragraph.Inlines)
        {
            if (index++ >= 3)
                break;
            if (inline is Run run)
                length += run.Text?.Length ?? 0;
        }
        return length;
    }

    private bool TryCopyActivePreviewCustomSelection(DependencyObject? source)
    {
        var block = _previewCustomSelectionBlock;
        bool hasSelection = block is not null && HasPreviewCustomSelection(block);
        YaguLog.For("PreviewSelection").LogDebug(
            "TryCopyActivePreviewCustomSelection: block={Block}, hasSelection={HasSelection}, " +
            "anchor={Anchor}, current={Current}, " +
            "sourceWithinPreview={SourceWithinPreview}", DescribePreviewSelectionBlock(block), hasSelection, _previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex, source is null ? "n/a" : IsElementWithin(source, PreviewScrollViewer).ToString());
        if (block is null || !hasSelection)
            return false;
        if (source is not null
            && !IsElementWithin(source, PreviewScrollViewer)
            && !ReferenceEquals(source, block))
        {
            YaguLog.For("PreviewSelection").LogDebug("TryCopyActivePreviewCustomSelection: aborted \u2014 source outside preview");
            return false;
        }

        CopyPreviewSelection(block, withLineNumbers: false);
        return true;
    }

    /// <summary>Diagnostic label for a preview RichTextBlock: the single PreviewBlock, a section
    /// CONTENT block (a key of <see cref="_sectionGutterBlocks"/>), a section GUTTER/line-number block
    /// (a value), or other. Used by the verbose select-all/copy diagnostics so a silent failure on a
    /// file-name-only preview can be diagnosed from the log without a repro round-trip.</summary>
    private string DescribePreviewSelectionBlock(RichTextBlock? block)
    {
        if (block is null) return "null";
        if (ReferenceEquals(block, PreviewBlock)) return "PreviewBlock";
        if (_sectionGutterBlocks.ContainsKey(block)) return "section-content";
        if (_sectionGutterBlocks.ContainsValue(block)) return "section-gutter";
        return "other";
    }

    private bool TrySelectAllPreviewContent(DependencyObject? source)
    {
        if (source is not null && !IsElementWithin(source, PreviewScrollViewer))
        {
            YaguLog.For("PreviewSelection").LogDebug("TrySelectAllPreviewContent: aborted \u2014 source outside preview");
            return false;
        }

        // Find the target RichTextBlock: either PreviewBlock (single-block mode)
        // or the block that already has a custom selection, or the first visible section block.
        RichTextBlock? block = null;
        string branch;
        if (_previewCustomSelectionBlock is not null
            && IsElementWithin(_previewCustomSelectionBlock, PreviewScrollViewer))
        {
            block = _previewCustomSelectionBlock;
            branch = "existing-selection-block";
        }
        else if (PreviewBlock.Visibility == Visibility.Visible)
        {
            block = PreviewBlock;
            branch = "preview-block";
        }
        else if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            branch = "section-fallback";
            // Use the first section's CONTENT block. Each section has two RichTextBlocks — the
            // gutter (line-numbers) block sits first in the visual tree (column 0) but its
            // paragraphs are untagged, so selecting/copying it yields nothing. FindFirstRichTextBlock
            // would return that gutter block, which is exactly why Ctrl+A / Ctrl+C silently failed for
            // file-name-only previews (no content match was clicked first to seed the content block).
            foreach (var child in PreviewSectionsPanel.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                {
                    var rtb = FindFirstSectionContentRichTextBlock(fe);
                    if (rtb is not null)
                    {
                        block = rtb;
                        break;
                    }
                }
            }
        }
        else
        {
            branch = "none";
        }

        int totalLength = block is null ? 0 : GetBlockTotalTextLength(block);
        YaguLog.For("PreviewSelection").LogDebug(
            "TrySelectAllPreviewContent: branch={Branch}, block={Block}, totalLength={TotalLength}", branch, DescribePreviewSelectionBlock(block), totalLength);

        if (block is null)
            return false;

        if (totalLength <= 0)
            return false;

        _previewCustomSelectionBlock = block;
        _previewCustomSelectionGroupBlocks = ResolvePreviewSelectionDrawerGroup(block);
        _previewCustomSelectionAnchorIndex = 0;
        _previewCustomSelectionCurrentIndex = totalLength;
        UpdatePreviewCustomSelectionHighlighter();
        return true;
    }

    /// <summary>
    /// All drawers rendering <paramref name="block"/>'s file, in panel order, when that file overflowed
    /// into continuation drawers. A very long file is split across several sections that are one file to
    /// the user, so Ctrl+A must take the whole file rather than the drawer that happened to have focus.
    /// Returns null when the file occupies a single drawer, which keeps the ordinary path allocation-free.
    /// </summary>
    private List<RichTextBlock>? ResolvePreviewSelectionDrawerGroup(RichTextBlock block)
    {
        if (PreviewSectionsPanel.Visibility != Visibility.Visible)
            return null;

        string? filePath = ResolvePreviewBlockFilePath(block);
        if (string.IsNullOrEmpty(filePath))
            return null;

        var group = new List<RichTextBlock>();
        foreach (var child in PreviewSectionsPanel.Children)
        {
            if (child is not FrameworkElement fe || fe.Visibility != Visibility.Visible)
                continue;
            var content = FindFirstSectionContentRichTextBlock(fe);
            if (content is null)
                continue;
            if (string.Equals(ResolvePreviewBlockFilePath(content), filePath, StringComparison.OrdinalIgnoreCase))
                group.Add(content);
        }

        // The focused drawer must be part of the group; if the panel walk missed it the mapping is stale
        // and a partial group would silently drop text from the copy.
        if (group.Count < 2 || !group.Any(candidate => ReferenceEquals(candidate, block)))
            return null;

        YaguLog.For("PreviewSelection").LogDebug(
            "ResolvePreviewSelectionDrawerGroup: file='{File}', drawers={Drawers}", filePath, group.Count);
        return group;
    }

    private static int GetBlockTotalTextLength(RichTextBlock block)
    {
        int total = 0;
        bool first = true;
        foreach (var textBlock in block.Blocks)
        {
            if (textBlock is not Paragraph paragraph)
                continue;
            if (!first)
                total += 1; // paragraph separator
            first = false;
            total += GetParagraphTextLength(paragraph);
        }
        return total;
    }

    private static RichTextBlock? FindFirstRichTextBlock(DependencyObject parent)
    {
        if (parent is RichTextBlock rtb)
            return rtb;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            var found = FindFirstRichTextBlock(child);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>
    /// Finds the first SELECTABLE section content RichTextBlock under <paramref name="parent"/>,
    /// skipping the per-section gutter (line-number) blocks. Section content blocks are the keys of
    /// <see cref="_sectionGutterBlocks"/>; gutter blocks are the values and carry only untagged
    /// line-number paragraphs (selecting/copying them yields nothing). Used by Select-All so it
    /// targets the real text even when no content match was clicked first (file-name-only previews).
    /// </summary>
    private RichTextBlock? FindFirstSectionContentRichTextBlock(DependencyObject parent)
    {
        if (parent is RichTextBlock rtb)
            return _sectionGutterBlocks.ContainsKey(rtb) ? rtb : null;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var found = FindFirstSectionContentRichTextBlock(VisualTreeHelper.GetChild(parent, i));
            if (found is not null)
                return found;
        }
        return null;
    }

    private void StopPreviewSelectionAutoScroll(string reason)
    {
        StopPreviewSelectionAutoScrollTimer(reason);
        LogPreviewSelectionAutoScrollStop(reason);
        var block = _previewSelectionAutoScrollBlock;
        _previewSelectionAutoScrollBlock = null;
        _previewSelectionAutoScrollScroller = null;
        _previewSelectionAutoScrollVerticalScroller = null;
        _previewSelectionAutoScrollPointerId = 0;
        _previewSelectionAutoScrollPointerX = 0;
        _previewSelectionLastViewOffsetY = double.NaN;
        _previewSelectionAutoScrollWasAtEdge = false;
        _previewCustomSelectionDragging = false;
        block?.ReleasePointerCaptures();
    }

    private void CancelPreviewSelectionAutoScrollForShowMore(string reason)
    {
        if (_previewSelectionAutoScrollBlock is null
            && !_previewSelectionAutoScrollTimerRunning
            && !_previewCustomSelectionDragging)
        {
            return;
        }

        StopPreviewSelectionAutoScroll(reason);
    }

    private void ResetPreviewSelectionAutoScrollDiagnostics(long now)
    {
        _previewSelectionAutoScrollStartedTick = now;
        _previewSelectionAutoScrollLastLogTick = now;
        _previewSelectionAutoScrollPointerMoveCount = 0;
        _previewSelectionAutoScrollFrameCount = 0;
        _previewSelectionAutoScrollDelayedFrameCount = 0;
        _previewSelectionAutoScrollChangeViewAcceptedCount = 0;
        _previewSelectionAutoScrollChangeViewRejectedCount = 0;
        _previewSelectionAutoScrollNoOpFrameCount = 0;
        _previewSelectionAutoScrollMaxFrameMs = 0;
        _previewSelectionAutoScrollMaxRawFrameMs = 0;
        _previewSelectionAutoScrollMaxLagDip = 0;
        _previewSelectionAutoScrollTotalRequestedDip = 0;
        _previewSelectionAutoScrollLastRequestedX = double.NaN;
        _previewSelectionAutoScrollPrevBeforeX = double.NaN;
        _previewSelectionAutoScrollStuckFrameCount = 0;
    }

    private void LogPreviewSelectionAutoScrollStart(RichTextBlock block, ScrollViewer scroller, bool pointerCaptured)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        YaguLog.For("PreviewSelectionAutoScroll").LogDebug(
            "start: surface={Surface}, pointerCaptured={PointerCaptured}, pointerX={PointerX:N1}, offsetX={OffsetX:N1}, viewportW={ViewportW:N1}, actualW={ActualW:N1}, scrollableW={ScrollableW:N1}, wrap={Wrap}, horizontalMode={HorizontalMode}", DescribePreviewSelectionAutoScrollSurface(block), pointerCaptured, _previewSelectionAutoScrollPointerX, scroller.HorizontalOffset, scroller.ViewportWidth, scroller.ActualWidth, scroller.ScrollableWidth, block.TextWrapping, scroller.HorizontalScrollMode);
    }

    private void LogPreviewSelectionAutoScrollEdge(string state, ScrollViewer scroller, double velocity)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        YaguLog.For("PreviewSelectionAutoScroll").LogDebug(
            "{State}: pointerX={PointerX:N1}, velocity={Velocity:N1}, offsetX={OffsetX:N1}, viewportW={ViewportW:N1}, scrollableW={ScrollableW:N1}, moves={Moves}", state, _previewSelectionAutoScrollPointerX, velocity, scroller.HorizontalOffset, scroller.ViewportWidth, scroller.ScrollableWidth, _previewSelectionAutoScrollPointerMoveCount);
    }

    private void LogPreviewSelectionAutoScrollTimerState(string state)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        YaguLog.For("PreviewSelectionAutoScroll").LogDebug(
            "{State}: frames={Frames}, accepted={Accepted}, rejected={Rejected}, delayed={Delayed}, maxFrameMs={MaxFrameMs:N1}, maxRawFrameMs={MaxRawFrameMs:N1}, maxLag={MaxLag:N1}", state, _previewSelectionAutoScrollFrameCount, _previewSelectionAutoScrollChangeViewAcceptedCount, _previewSelectionAutoScrollChangeViewRejectedCount, _previewSelectionAutoScrollDelayedFrameCount, _previewSelectionAutoScrollMaxFrameMs, _previewSelectionAutoScrollMaxRawFrameMs, _previewSelectionAutoScrollMaxLagDip);
    }

    private void MaybeLogPreviewSelectionAutoScrollSample(
        ScrollViewer scroller,
        double velocity,
        double step,
        double beforeX,
        double targetX,
        bool accepted,
        double frameMs,
        double rawFrameMs,
        string source)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        long now = Environment.TickCount64;
        bool isNoOp = string.Equals(source, "noop", StringComparison.Ordinal);
        bool shouldLog = now - _previewSelectionAutoScrollLastLogTick >= PreviewSelectionAutoScrollLogIntervalMs
            || rawFrameMs >= PreviewSelectionAutoScrollDelayedFrameMs
            || (!isNoOp && !accepted);
        if (!shouldLog)
            return;

        _previewSelectionAutoScrollLastLogTick = now;
        YaguLog.For("PreviewSelectionAutoScroll").LogDebug(
            "sample:{Source}: frame={Frame}, frameMs={FrameMs:N1}, rawFrameMs={RawFrameMs:N1}, pointerX={PointerX:N1}, velocity={Velocity:N1}, step={Step:N1}, beforeX={BeforeX:N1}, targetX={TargetX:N1}, currentX={CurrentX:N1}, accepted={Accepted}, viewportW={ViewportW:N1}, scrollableW={ScrollableW:N1}, moves={Moves}, delayed={Delayed}, maxLag={MaxLag:N1}", source, _previewSelectionAutoScrollFrameCount, frameMs, rawFrameMs, _previewSelectionAutoScrollPointerX, velocity, step, beforeX, targetX, scroller.HorizontalOffset, accepted, scroller.ViewportWidth, scroller.ScrollableWidth, _previewSelectionAutoScrollPointerMoveCount, _previewSelectionAutoScrollDelayedFrameCount, _previewSelectionAutoScrollMaxLagDip);
    }

    private void LogPreviewSelectionAutoScrollStop(string reason)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        var scroller = _previewSelectionAutoScrollScroller;
        long durationMs = Math.Max(0, Environment.TickCount64 - _previewSelectionAutoScrollStartedTick);
        YaguLog.For("PreviewSelectionAutoScroll").LogDebug(
            "stop: reason={Reason}, durationMs={DurationMs}, frames={Frames}, pointerMoves={PointerMoves}, accepted={Accepted}, rejected={Rejected}, noop={Noop}, delayed={Delayed}, maxFrameMs={MaxFrameMs:N1}, maxRawFrameMs={MaxRawFrameMs:N1}, maxLag={MaxLag:N1}, requestedDip={RequestedDip:N1}, finalOffsetX={FinalOffsetX:N1}, scrollableW={ScrollableW:N1}", reason, durationMs, _previewSelectionAutoScrollFrameCount, _previewSelectionAutoScrollPointerMoveCount, _previewSelectionAutoScrollChangeViewAcceptedCount, _previewSelectionAutoScrollChangeViewRejectedCount, _previewSelectionAutoScrollNoOpFrameCount, _previewSelectionAutoScrollDelayedFrameCount, _previewSelectionAutoScrollMaxFrameMs, _previewSelectionAutoScrollMaxRawFrameMs, _previewSelectionAutoScrollMaxLagDip, _previewSelectionAutoScrollTotalRequestedDip, scroller?.HorizontalOffset, scroller?.ScrollableWidth);
    }

    private void DisposePreviewSelectionAutoScroll()
    {
        StopPreviewSelectionAutoScrollTimer("window-dispose");
        _previewSelectionAutoScrollTimer?.Dispose();
        _previewSelectionAutoScrollTimer = null;
        Interlocked.Exchange(ref _previewSelectionAutoScrollTickQueued, 0);
        _previewSelectionAutoScrollBlock?.ReleasePointerCaptures();
        _previewSelectionAutoScrollBlock = null;
        _previewSelectionAutoScrollScroller = null;
        _previewSelectionAutoScrollVerticalScroller = null;
    }

    private string DescribePreviewSelectionAutoScrollSurface(RichTextBlock block)
        => ReferenceEquals(block, PreviewBlock) ? "main" : "section";
}
