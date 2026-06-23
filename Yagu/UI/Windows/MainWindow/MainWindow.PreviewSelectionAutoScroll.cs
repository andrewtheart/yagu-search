using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Yagu.Services;

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
    private Timer? _previewSelectionAutoScrollTimer;
    private RichTextBlock? _previewCustomSelectionBlock;
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
            LogService.Instance.Verbose("PreviewEditor",
                $"Double-click detected: point=({pressPoint.X:N1},{pressPoint.Y:N1}), wrap={block.TextWrapping}, surface={(ReferenceEquals(block, PreviewBlock) ? "single" : "section")}");
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
        _previewSelectionAutoScrollPointerId = e.Pointer.PointerId;
        _previewSelectionAutoScrollPointerPointInScroller = e.GetCurrentPoint(scroller).Position;
        _previewSelectionAutoScrollPointerX = _previewSelectionAutoScrollPointerPointInScroller.X;
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
            LogService.Instance.Verbose("PreviewEditor",
                "Pointer double-click editor entry skipped: preview is mutating");
            return;
        }
        DismissActiveIntroTip();
        _previewEditorPointerOpenTick = Environment.TickCount64;
        var filePath = ResolvePreviewBlockFilePath(block);
        LogService.Instance.Verbose("PreviewEditor",
            $"Pointer double-click editor entry: file='{(filePath is null ? "null" : System.IO.Path.GetFileName(filePath))}', point=({point.X:N1},{point.Y:N1})");
        bool opened = await TryEnterPreviewEditorAtPointAsync(block, point, filePath);
        LogService.Instance.Verbose("PreviewEditor",
            $"Pointer double-click editor entry result: opened={opened}");
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
        _previewSelectionAutoScrollPointerPointInScroller = point.Position;
        _previewSelectionAutoScrollPointerMoveCount++;
        if (_previewCustomSelectionDragging)
        {
            UpdatePreviewCustomSelectionFromCurrentPointer();
            e.Handled = true;
        }

        bool isAtEdge = TryGetPreviewSelectionAutoScrollVelocity(
            _previewSelectionAutoScrollScroller,
            _previewSelectionAutoScrollPointerX,
            out double velocity);

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

        if (block.TextWrapping != TextWrapping.NoWrap
            || scroller.HorizontalScrollMode != ScrollMode.Enabled
            || scroller.ScrollableWidth <= 0.5)
        {
            StopPreviewSelectionAutoScroll(
                $"invalid-state wrap={block.TextWrapping}, horizontalMode={scroller.HorizontalScrollMode}, scrollableW={scroller.ScrollableWidth:N1}");
            return;
        }

        if (!TryGetPreviewSelectionAutoScrollVelocity(scroller, _previewSelectionAutoScrollPointerX, out double velocity))
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
        if (!double.IsNaN(_previewSelectionAutoScrollLastRequestedX))
            _previewSelectionAutoScrollMaxLagDip = Math.Max(
                _previewSelectionAutoScrollMaxLagDip,
                Math.Abs(beforeX - _previewSelectionAutoScrollLastRequestedX));

        double step = velocity * elapsedSeconds;
        _previewSelectionAutoScrollTotalRequestedDip += Math.Abs(step);
        double targetX = Math.Clamp(scroller.HorizontalOffset + step, 0, scroller.ScrollableWidth);
        if (Math.Abs(targetX - beforeX) <= 0.5)
        {
            _previewSelectionAutoScrollNoOpFrameCount++;
            if (_previewCustomSelectionDragging)
                UpdatePreviewCustomSelectionFromCurrentPointer();
            MaybeLogPreviewSelectionAutoScrollSample(scroller, velocity, step, beforeX, targetX, false, frameMs, rawFrameMs, "noop");
            if ((beforeX <= 0.5 && step < 0) || (beforeX >= scroller.ScrollableWidth - 0.5 && step > 0))
                StopPreviewSelectionAutoScrollTimer("scroll-boundary-noop");
            return;
        }

        bool accepted = scroller.ChangeView(targetX, null, null, disableAnimation: true);
        if (accepted)
            _previewSelectionAutoScrollChangeViewAcceptedCount++;
        else
            _previewSelectionAutoScrollChangeViewRejectedCount++;
        _previewSelectionAutoScrollLastRequestedX = targetX;

        // Detect stuck scroller: ChangeView accepted but offset didn't move.
        if (!double.IsNaN(_previewSelectionAutoScrollPrevBeforeX)
            && Math.Abs(beforeX - _previewSelectionAutoScrollPrevBeforeX) <= 0.5
            && accepted)
        {
            _previewSelectionAutoScrollStuckFrameCount++;
            if (_previewSelectionAutoScrollStuckFrameCount >= 5)
            {
                if (_previewCustomSelectionDragging)
                    UpdatePreviewCustomSelectionFromCurrentPointer();
                MaybeLogPreviewSelectionAutoScrollSample(scroller, velocity, step, beforeX, targetX, accepted, frameMs, rawFrameMs, "frame");
                StopPreviewSelectionAutoScrollTimer("stuck-scroller");
                _previewSelectionAutoScrollPrevBeforeX = beforeX;
                return;
            }
        }
        else
        {
            _previewSelectionAutoScrollStuckFrameCount = 0;
        }
        _previewSelectionAutoScrollPrevBeforeX = beforeX;

        if (_previewCustomSelectionDragging)
            UpdatePreviewCustomSelectionFromCurrentPointer();
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

        if (TryResolvePreviewSelectionIndexFromCurrentPointer(block, scroller, out int index))
        {
            _previewCustomSelectionAnchorIndex = index;
            _previewCustomSelectionCurrentIndex = index;
            UpdatePreviewCustomSelectionHighlighter();
        }
    }

    private void UpdatePreviewCustomSelectionFromCurrentPointer()
    {
        var block = _previewCustomSelectionBlock;
        var scroller = _previewSelectionAutoScrollScroller;
        if (!_previewCustomSelectionDragging || block is null || scroller is null)
            return;

        if (!TryResolvePreviewSelectionIndexFromCurrentPointer(block, scroller, out int index))
            return;

        if (index == _previewCustomSelectionCurrentIndex)
            return;

        _previewCustomSelectionCurrentIndex = index;
        UpdatePreviewCustomSelectionHighlighter();
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

    private static int MapPreviewTextPointerToBlockIndex(RichTextBlock block, TextPointer pointer)
    {
        int pointerOffset = pointer.Offset;
        int blockIndex = 0;
        foreach (var textBlock in block.Blocks)
        {
            if (textBlock is not Paragraph paragraph)
                continue;

            int paragraphLength = GetParagraphTextLength(paragraph);
            int paragraphStart = paragraph.ContentStart.Offset;
            int paragraphEnd = paragraph.ContentEnd.Offset;
            if (pointerOffset <= paragraphStart)
                return blockIndex;
            if (pointerOffset <= paragraphEnd)
            {
                int localIndex = MapPreviewTextPointerToParagraphIndex(paragraph, pointerOffset, paragraphLength);
                return blockIndex + localIndex;
            }

            blockIndex += paragraphLength + 1;
        }

        return blockIndex;
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

        int markerIndex = 0;
        int blockIndex = 0;
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
                    block, paragraph, localStart, localEnd, rect, markerHeight, overlayWidth, overlayHeight, out var wrappedRows))
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
            if (double.IsNaN(left) || double.IsNaN(right) || double.IsInfinity(left) || double.IsInfinity(right))
                continue;

            double visibleLeft = Math.Max(0, left);
            double visibleRight = Math.Min(overlayWidth, right);
            double width = visibleRight - visibleLeft;
            if (width <= 0)
                continue;

            var marker = GetPreviewCustomSelectionOverlayMarker(markerIndex++);
            marker.Width = width;
            marker.Height = markerHeight;
            marker.Visibility = Visibility.Visible;
            Canvas.SetLeft(marker, visibleLeft);
            Canvas.SetTop(marker, top);

            if (markerIndex >= PreviewCustomSelectionOverlayMaxMarkers)
                break;
        }

        for (int index = markerIndex; index < _previewCustomSelectionOverlayMarkers.Count; index++)
            _previewCustomSelectionOverlayMarkers[index].Visibility = Visibility.Collapsed;

        PreviewSelectionOverlay.Visibility = Visibility.Visible;
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
        double overlayWidth,
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
                overlayWidth, overlayHeight, rows);
        }
        else
        {
            // Partial first row: selection start -> content right edge.
            AddOverlayBandRect(toOverlay, startRect.X, startRect.Y, contentRightBlock, startRect.Y + rowHeight,
                overlayWidth, overlayHeight, rows);
            // Full-width middle band covering every row strictly between start and end.
            double midTopBlock = startRect.Y + rowHeight;
            if (endRect.Y - midTopBlock > 1)
                AddOverlayBandRect(toOverlay, contentLeftBlock, midTopBlock, contentRightBlock, endRect.Y,
                    overlayWidth, overlayHeight, rows);
            // Partial last row: content left edge -> selection end.
            AddOverlayBandRect(toOverlay, contentLeftBlock, endRect.Y, endRect.X, endRect.Y + rowHeight,
                overlayWidth, overlayHeight, rows);
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
        double overlayWidth,
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

        double visibleLeft = Math.Max(0, left);
        double visibleRight = Math.Min(overlayWidth, right);
        double width = visibleRight - visibleLeft;
        double height = bottom - top;
        if (width <= 0 || height <= 0)
            return;
        if (top + height < 0 || top > overlayHeight)
            return;

        rows.Add(new Windows.Foundation.Rect(visibleLeft, top, width, height));
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
        => ReferenceEquals(_previewCustomSelectionBlock, block)
           && Math.Abs(_previewCustomSelectionCurrentIndex - _previewCustomSelectionAnchorIndex) > 0;

    private bool TryBuildPreviewCustomSelectionText(RichTextBlock block, bool withLineNumbers, out string text)
    {
        text = string.Empty;
        if (!HasPreviewCustomSelection(block))
            return false;

        int selectionStart = Math.Min(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        int selectionEnd = Math.Max(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
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
                    selectedText.Append(System.Globalization.CultureInfo.InvariantCulture, $"{lineNumber,5} \u2502 {slice}");
                    lastEmittedLineNumber = lineNumber;
                }
                else
                {
                    selectedText.Append(System.Globalization.CultureInfo.InvariantCulture, $"      \u2502 {slice}");
                }
            }
            else
            {
                selectedText.Append(slice);
            }
        }

        text = selectedText.ToString();
        return text.Length > 0;
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
        if (block is null || !HasPreviewCustomSelection(block))
            return false;
        if (source is not null
            && !IsElementWithin(source, PreviewScrollViewer)
            && !ReferenceEquals(source, block))
            return false;

        CopyPreviewSelection(block, withLineNumbers: false);
        return true;
    }

    private bool TrySelectAllPreviewContent(DependencyObject? source)
    {
        if (source is not null && !IsElementWithin(source, PreviewScrollViewer))
            return false;

        // Find the target RichTextBlock: either PreviewBlock (single-block mode)
        // or the block that already has a custom selection, or the first visible section block.
        RichTextBlock? block = null;
        if (_previewCustomSelectionBlock is not null
            && IsElementWithin(_previewCustomSelectionBlock, PreviewScrollViewer))
        {
            block = _previewCustomSelectionBlock;
        }
        else if (PreviewBlock.Visibility == Visibility.Visible)
        {
            block = PreviewBlock;
        }
        else if (PreviewSectionsPanel.Visibility == Visibility.Visible)
        {
            // Use the first section content block.
            foreach (var child in PreviewSectionsPanel.Children)
            {
                if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible)
                {
                    var rtb = FindFirstRichTextBlock(fe);
                    if (rtb is not null)
                    {
                        block = rtb;
                        break;
                    }
                }
            }
        }

        if (block is null)
            return false;

        int totalLength = GetBlockTotalTextLength(block);
        if (totalLength <= 0)
            return false;

        _previewCustomSelectionBlock = block;
        _previewCustomSelectionAnchorIndex = 0;
        _previewCustomSelectionCurrentIndex = totalLength;
        UpdatePreviewCustomSelectionHighlighter();
        return true;
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

    private void StopPreviewSelectionAutoScroll(string reason)
    {
        StopPreviewSelectionAutoScrollTimer(reason);
        LogPreviewSelectionAutoScrollStop(reason);
        var block = _previewSelectionAutoScrollBlock;
        _previewSelectionAutoScrollBlock = null;
        _previewSelectionAutoScrollScroller = null;
        _previewSelectionAutoScrollPointerId = 0;
        _previewSelectionAutoScrollPointerX = 0;
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

        LogService.Instance.Verbose("PreviewSelectionAutoScroll",
            $"start: surface={DescribePreviewSelectionAutoScrollSurface(block)}, pointerCaptured={pointerCaptured}, pointerX={_previewSelectionAutoScrollPointerX:N1}, offsetX={scroller.HorizontalOffset:N1}, viewportW={scroller.ViewportWidth:N1}, actualW={scroller.ActualWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}, wrap={block.TextWrapping}, horizontalMode={scroller.HorizontalScrollMode}");
    }

    private void LogPreviewSelectionAutoScrollEdge(string state, ScrollViewer scroller, double velocity)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        LogService.Instance.Verbose("PreviewSelectionAutoScroll",
            $"{state}: pointerX={_previewSelectionAutoScrollPointerX:N1}, velocity={velocity:N1}, offsetX={scroller.HorizontalOffset:N1}, viewportW={scroller.ViewportWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}, moves={_previewSelectionAutoScrollPointerMoveCount}");
    }

    private void LogPreviewSelectionAutoScrollTimerState(string state)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        LogService.Instance.Verbose("PreviewSelectionAutoScroll",
            $"{state}: frames={_previewSelectionAutoScrollFrameCount}, accepted={_previewSelectionAutoScrollChangeViewAcceptedCount}, rejected={_previewSelectionAutoScrollChangeViewRejectedCount}, delayed={_previewSelectionAutoScrollDelayedFrameCount}, maxFrameMs={_previewSelectionAutoScrollMaxFrameMs:N1}, maxRawFrameMs={_previewSelectionAutoScrollMaxRawFrameMs:N1}, maxLag={_previewSelectionAutoScrollMaxLagDip:N1}");
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
        LogService.Instance.Verbose("PreviewSelectionAutoScroll",
            $"sample:{source}: frame={_previewSelectionAutoScrollFrameCount}, frameMs={frameMs:N1}, rawFrameMs={rawFrameMs:N1}, pointerX={_previewSelectionAutoScrollPointerX:N1}, velocity={velocity:N1}, step={step:N1}, beforeX={beforeX:N1}, targetX={targetX:N1}, currentX={scroller.HorizontalOffset:N1}, accepted={accepted}, viewportW={scroller.ViewportWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}, moves={_previewSelectionAutoScrollPointerMoveCount}, delayed={_previewSelectionAutoScrollDelayedFrameCount}, maxLag={_previewSelectionAutoScrollMaxLagDip:N1}");
    }

    private void LogPreviewSelectionAutoScrollStop(string reason)
    {
        if (!LogService.Instance.IsVerboseEnabled)
            return;

        var scroller = _previewSelectionAutoScrollScroller;
        long durationMs = Math.Max(0, Environment.TickCount64 - _previewSelectionAutoScrollStartedTick);
        LogService.Instance.Verbose("PreviewSelectionAutoScroll",
            $"stop: reason={reason}, durationMs={durationMs}, frames={_previewSelectionAutoScrollFrameCount}, pointerMoves={_previewSelectionAutoScrollPointerMoveCount}, accepted={_previewSelectionAutoScrollChangeViewAcceptedCount}, rejected={_previewSelectionAutoScrollChangeViewRejectedCount}, noop={_previewSelectionAutoScrollNoOpFrameCount}, delayed={_previewSelectionAutoScrollDelayedFrameCount}, maxFrameMs={_previewSelectionAutoScrollMaxFrameMs:N1}, maxRawFrameMs={_previewSelectionAutoScrollMaxRawFrameMs:N1}, maxLag={_previewSelectionAutoScrollMaxLagDip:N1}, requestedDip={_previewSelectionAutoScrollTotalRequestedDip:N1}, finalOffsetX={scroller?.HorizontalOffset:N1}, scrollableW={scroller?.ScrollableWidth:N1}");
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
    }

    private string DescribePreviewSelectionAutoScrollSurface(RichTextBlock block)
        => ReferenceEquals(block, PreviewBlock) ? "main" : "section";
}