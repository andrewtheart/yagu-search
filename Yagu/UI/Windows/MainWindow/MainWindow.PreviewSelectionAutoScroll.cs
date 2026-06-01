using System;
using System.Text;
using System.Threading;
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

    private RichTextBlock? _previewSelectionAutoScrollBlock;
    private ScrollViewer? _previewSelectionAutoScrollScroller;
    private Timer? _previewSelectionAutoScrollTimer;
    private RichTextBlock? _previewCustomSelectionBlock;
    private TextHighlighter? _previewCustomSelectionHighlighter;
    private uint _previewSelectionAutoScrollPointerId;
    private int _previewSelectionAutoScrollTickQueued;
    private bool _previewSelectionAutoScrollTimerRunning;
    private bool _previewSelectionAutoScrollWasAtEdge;
    private bool _previewCustomSelectionDragging;
    private Point _previewSelectionAutoScrollPointerPointInScroller;
    private double _previewSelectionAutoScrollPointerX;
    private int _previewCustomSelectionAnchorIndex = -1;
    private int _previewCustomSelectionCurrentIndex = -1;
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

        _previewSelectionAutoScrollBlock = block;
        _previewSelectionAutoScrollScroller = scroller;
        _previewSelectionAutoScrollPointerId = e.Pointer.PointerId;
        _previewSelectionAutoScrollPointerPointInScroller = e.GetCurrentPoint(scroller).Position;
        _previewSelectionAutoScrollPointerX = _previewSelectionAutoScrollPointerPointInScroller.X;
        _previewSelectionAutoScrollLastTick = Environment.TickCount64;
        _previewSelectionAutoScrollWasAtEdge = false;
        ResetPreviewSelectionAutoScrollDiagnostics(_previewSelectionAutoScrollLastTick);
        bool pointerCaptured = block.CapturePointer(e.Pointer);
        if (ShouldUseCustomPreviewSelection(block, scroller))
        {
            BeginPreviewCustomSelection(block, scroller);
            e.Handled = true;
        }
        LogPreviewSelectionAutoScrollStart(block, scroller, pointerCaptured);
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
        bool useNativeSelection = block.TextWrapping == TextWrapping.Wrap;
        if (block.IsTextSelectionEnabled != useNativeSelection)
            block.IsTextSelectionEnabled = useNativeSelection;
    }

    private static bool ShouldUseCustomPreviewSelection(RichTextBlock block, ScrollViewer scroller)
        => block.TextWrapping == TextWrapping.NoWrap
           && scroller.HorizontalScrollMode == ScrollMode.Enabled;

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
                int localIndex = Math.Clamp(pointerOffset - paragraphStart, 0, paragraphLength);
                return blockIndex + localIndex;
            }

            blockIndex += paragraphLength + 1;
        }

        return blockIndex;
    }

    private void UpdatePreviewCustomSelectionHighlighter()
    {
        var block = _previewCustomSelectionBlock;
        if (block is null)
            return;

        int startIndex = Math.Min(_previewCustomSelectionAnchorIndex, _previewCustomSelectionCurrentIndex);
        int length = Math.Abs(_previewCustomSelectionCurrentIndex - _previewCustomSelectionAnchorIndex);
        if (startIndex < 0 || length <= 0)
        {
            RemovePreviewCustomSelectionHighlighter();
            return;
        }

        if (!ReferenceEquals(_previewCustomSelectionHighlighterBlock, block))
            RemovePreviewCustomSelectionHighlighter();

        _previewCustomSelectionHighlighter ??= new TextHighlighter
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(135, 0, 120, 215)),
        };
        _previewCustomSelectionHighlighter.Ranges.Clear();
        _previewCustomSelectionHighlighter.Ranges.Add(new TextRange
        {
            StartIndex = startIndex,
            Length = length,
        });
        if (!block.TextHighlighters.Contains(_previewCustomSelectionHighlighter))
            block.TextHighlighters.Add(_previewCustomSelectionHighlighter);
        _previewCustomSelectionHighlighterBlock = block;
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