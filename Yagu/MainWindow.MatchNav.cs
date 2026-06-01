using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Yagu.Helpers;
using Yagu.Models;
using Yagu.Services;

namespace Yagu;

/// <summary>
/// Match navigation: boxing/unboxing the active match highlight,
/// scrolling the preview to the current match, bulk navigation,
/// section-level match stepping, and active-match overlay positioning.
/// </summary>
public sealed partial class MainWindow
{
    private void BoxMatchRun(Paragraph para, int matchInPara, bool boundaryFlash = false)
    {
        UnboxCurrentMatch();
        var matches = GetMatchRunsForParagraph(para);
        if ((uint)matchInPara >= (uint)matches.Count)
        {
            _paragraphMatchRunCache.Remove(para);
            matches = GetMatchRunsForParagraph(para);
            if ((uint)matchInPara >= (uint)matches.Count)
                return;
        }

        var (run, column) = matches[matchInPara];
        _activeMatchHighlight = (para, run, column, matchInPara);
        ResetActiveOverlayLayoutStability();

        if (boundaryFlash)
            FlashActiveMatchOverlayRed();

        if (LogService.Instance.IsVerboseEnabled)
        {
            int paragraphIndex = _matchParagraphs.Count > 0
                ? GetParagraphIndex(_matchParagraphs[Math.Clamp(_currentMatchIndex, 0, _matchParagraphs.Count - 1)].block, para)
                : -1;
            LogService.Instance.Verbose("MatchNav", $"BoxMatchRun: idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}/{matches.Count}, col={column}, runText='{run.Text}', boundaryFlash={boundaryFlash}");
        }
    }

    /// <summary>
    /// Briefly flashes the active match overlay band red to signal the user
    /// has hit the boundary (first or last match).
    /// </summary>
    private void FlashActiveMatchOverlayRed()
    {
        var flashBrush = new SolidColorBrush(Microsoft.UI.Colors.Red) { Opacity = 0.45 };
        var normalBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, _overlayColor.R, _overlayColor.G, _overlayColor.B));
        ActiveMatchBand.Background = flashBrush;
        ActiveMatchWordMarker.Background = new SolidColorBrush(Microsoft.UI.Colors.Red) { Opacity = 0.55 };
        ActiveMatchBand.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);
        ActiveMatchWordMarker.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);
        foreach (var marker in _activeMatchExtraWordMarkers)
        {
            marker.Background = new SolidColorBrush(Microsoft.UI.Colors.Red) { Opacity = 0.55 };
            marker.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);
        }

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(600);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            ActiveMatchBand.Background = normalBrush;
            ActiveMatchWordMarker.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, _overlayColor.R, _overlayColor.G, _overlayColor.B));
            ActiveMatchBand.BorderBrush = new SolidColorBrush(_overlayColor);
            ActiveMatchWordMarker.BorderBrush = new SolidColorBrush(_overlayColor);
            foreach (var marker in _activeMatchExtraWordMarkers)
            {
                marker.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, _overlayColor.R, _overlayColor.G, _overlayColor.B));
                marker.BorderBrush = new SolidColorBrush(_overlayColor);
            }
            timer.Stop();
        };
        timer.Start();
    }

    private void ResetActiveOverlayLayoutStability()
    {
        _activeOverlayStabilityBlock = null;
        _activeOverlayLastBlockTop = double.NaN;
        _activeOverlayLastMoveTick = Environment.TickCount64;
        _activeOverlayStablePasses = 0;
    }

    private List<(Run run, int column)> GetMatchRunsForParagraph(Paragraph para)
    {
        if (_paragraphMatchRunCache.TryGetValue(para, out var matches))
            return matches;

        matches = new List<(Run run, int column)>();
        int column = 0;
        for (int i = 0; i < para.Inlines.Count; i++)
        {
            if (para.Inlines[i] is not Run run)
                continue;

            if (IsSearchMatchRun(run))
                matches.Add((run, column));

            column += run.Text?.Length ?? 0;
        }
        _paragraphMatchRunCache[para] = matches;
        return matches;
    }

    private bool IsSearchMatchRun(Run run)
        => run.FontWeight.Weight == Microsoft.UI.Text.FontWeights.Bold.Weight
           && run.Foreground is SolidColorBrush brush
           && brush.Color == _matchTextBrush.Color;

    private void UnboxCurrentMatch([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_activeMatchHighlight is null) return;
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"UnboxCurrentMatch: idx={_currentMatchIndex}, caller={caller}");
        _activeMatchHighlight = null;
    }

    /// <summary>
    /// Scrolls the outer preview ScrollViewer so that <paramref name="targetPara"/>
    /// inside <paramref name="block"/> is visible, optionally centered.
    /// Must be called after the content has been added to the visual tree;
    /// actual scrolling is deferred to a low-priority dispatcher tick so layout
    /// has time to complete.
    /// </summary>
    private void ScrollPreviewToLine(RichTextBlock block, Paragraph targetPara, bool forceCenter = true)
    {
        int requestId = ++_matchScrollRequestId;
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav",
                $"ScrollPreviewToLine: entry idx={_currentMatchIndex}, requestId={requestId}, mode=estimated, forceCenter={forceCenter}");

        if (TryScrollPreviewToLine(block, targetPara, verifyAfterScroll: false, forceCenter, out _))
            return;

        ScrollPreviewToLine(block, targetPara, attemptsRemaining: 3, requestId, forceCenter);
    }

    /// <summary>
    /// Variant used immediately after MaterializeNextLazySection: forces a synchronous
    /// layout pass on the freshly-expanded section so the run's character rect is
    /// available BEFORE the first ScrollPreviewToLine, then hooks LayoutUpdated to
    /// re-center if the section continues to settle on subsequent layout passes.
    /// Without this the corrective scroll loop in VerifyActiveMatchVisibleAfterScroll
    /// converges on a stale position because the Expander's content is still reflowing.
    /// </summary>
    private void ScrollAfterMaterialization(RichTextBlock block, Paragraph targetPara)
    {
        try
        {
            block.UpdateLayout();
            PreviewScrollViewer.UpdateLayout();
        }
        catch { }

        ScrollPreviewToLine(block, targetPara);

        // Re-center on subsequent layout passes too: when many lines/Runs in a freshly
        // expanded section are measured/arranged, the absolute Y of our paragraph
        // can shift by hundreds of pixels after our initial scroll. Hook
        // LayoutUpdated on the PreviewScrollViewer (NOT the block — the Expander
        // animation reflows ancestors/siblings without firing block.LayoutUpdated
        // on every pass) and re-issue the scroll if the active run drifts off
        // screen. We track two timers: an "idle" timer reset on every rescroll
        // (so we keep watching as long as layout is still moving) and an
        // absolute hard cap to guard against pathological infinite reflow.
        int requestId = _matchScrollRequestId;
        int idxAtAttach = _currentMatchIndex;
        EventHandler<object>? handler = null;
        var idleStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var hardCapStopwatch = System.Diagnostics.Stopwatch.StartNew();
        const int kIdleWindowMs = 1500;        // detach if no rescroll needed for 1.5 s
        const int kHardCapMs = 5000;           // never watch longer than 5 s total
        const int kMaxRescrolls = 30;
        int rescrolls = 0;
        int consecutiveOnScreen = 0;
        int layoutPasses = 0;
        LogService.Instance.Info("MatchNav",
            $"ScrollAfterMaterialization: attach idx={idxAtAttach}, requestId={requestId}");
        handler = (_, __) =>
        {
            if (handler is null) return;
            layoutPasses++;
            if (requestId != _matchScrollRequestId
                || idleStopwatch.ElapsedMilliseconds > kIdleWindowMs
                || hardCapStopwatch.ElapsedMilliseconds > kHardCapMs
                || rescrolls >= kMaxRescrolls)
            {
                string reason = requestId != _matchScrollRequestId
                    ? $"new-request(curr={_matchScrollRequestId})"
                    : (hardCapStopwatch.ElapsedMilliseconds > kHardCapMs
                        ? "hard-cap"
                        : (idleStopwatch.ElapsedMilliseconds > kIdleWindowMs ? "idle" : "max-rescrolls"));
                // Take a final snapshot of the active highlight at detach time.
                // If the run has drifted off-screen since our last rescroll AND we
                // still have hardCap budget AND this is an idle (not new-request /
                // hard-cap / max-rescrolls) detach, issue one more corrective
                // scroll and STAY attached. Without this rescue, materialize-path
                // navs commonly look correct at first verify (~30ms in) but then
                // the section continues reflowing for another 100-1500ms, the run
                // drifts off-screen, no LayoutUpdated fires for the final settle,
                // and we detach silently \u2014 leaving the user looking at empty
                // space below the match.
                string snapshot = "(no-highlight)";
                bool snapshotOnScr = true; // assume on-screen if we can't measure
                double snapshotRunY = double.NaN;
                double snapshotRunH = double.NaN;
                try
                {
                    if (_activeMatchHighlight is { para: var ap, run: var ar } && ar is not null)
                    {
                        var fg = (ar.Foreground as SolidColorBrush)?.Color.ToString() ?? "(non-solid)";
                        var rect2 = ar.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                        var t2 = block.TransformToVisual(PreviewScrollViewer);
                        var p2 = t2.TransformPoint(new Windows.Foundation.Point(rect2.X, rect2.Y));
                        double vTop = PreviewScrollViewer.VerticalOffset;
                        double vH = PreviewScrollViewer.ViewportHeight;
                        double rY = vTop + p2.Y;
                        bool onScr = rect2.Height > 0 && rY >= vTop && rY + rect2.Height <= vTop + vH;
                        bool sameRun = ReferenceEquals(ap, targetPara);
                        snapshot = $"fg={fg}, sameRun={sameRun}, onScr={onScr}, runY={rY:N1}, runH={rect2.Height:N1}";
                        snapshotOnScr = onScr || rect2.Height <= 0;
                        snapshotRunY = rY;
                        snapshotRunH = rect2.Height;
                    }
                }
                catch { }

                bool isIdleDetach = reason == "idle";
                bool canRescue = isIdleDetach
                    && !snapshotOnScr
                    && !double.IsNaN(snapshotRunY)
                    && !double.IsNaN(snapshotRunH)
                    && snapshotRunH > 0
                    && hardCapStopwatch.ElapsedMilliseconds < kHardCapMs - 200
                    && rescrolls < kMaxRescrolls;
                if (canRescue)
                {
                    double vH2 = PreviewScrollViewer.ViewportHeight;
                    double target = snapshotRunY - vH2 / 2 + snapshotRunH / 2;
                    target = Math.Clamp(target, 0, PreviewScrollViewer.ScrollableHeight);
                    double vpTop2 = PreviewScrollViewer.VerticalOffset;
                    if (Math.Abs(target - vpTop2) > 1)
                    {
                        rescrolls++;
                        idleStopwatch.Restart();
                        consecutiveOnScreen = 0;
                        bool accepted2 = PreviewScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                        LogService.Instance.Info("MatchNav",
                            $"ScrollAfterMaterialization: detach-rescue idx={idxAtAttach}, snapshot={snapshot}, fromY={vpTop2:N1}, toY={target:N1}, accepted={accepted2}, rescrolls={rescrolls}, total={hardCapStopwatch.ElapsedMilliseconds}ms (staying attached)");
                        return; // do NOT detach \u2014 keep watching
                    }
                }

                LogService.Instance.Info("MatchNav",
                    $"ScrollAfterMaterialization: detach idx={idxAtAttach}, reason={reason}, layoutPasses={layoutPasses}, rescrolls={rescrolls}, idle={idleStopwatch.ElapsedMilliseconds}ms, total={hardCapStopwatch.ElapsedMilliseconds}ms, finalVpTop={PreviewScrollViewer.VerticalOffset:N1}, snapshot={snapshot}");
                PreviewScrollViewer.LayoutUpdated -= handler;
                handler = null;
                return;
            }
            try
            {
                if (_activeMatchHighlight is { para: var activePara, run: var activeRun }
                    && ReferenceEquals(activePara, targetPara) && activeRun is not null)
                {
                    var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    if (rect.Height > 0)
                    {
                        var t = block.TransformToVisual(PreviewScrollViewer);
                        var p = t.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                        double vpTop = PreviewScrollViewer.VerticalOffset;
                        double vpH = PreviewScrollViewer.ViewportHeight;
                        double runY = vpTop + p.Y;
                        bool onScreen = runY >= vpTop && runY + rect.Height <= vpTop + vpH;
                        if (!onScreen)
                        {
                            consecutiveOnScreen = 0;
                            double target = runY - vpH / 2 + rect.Height / 2;
                            target = Math.Clamp(target, 0, PreviewScrollViewer.ScrollableHeight);
                            if (Math.Abs(target - vpTop) > 1)
                            {
                                rescrolls++;
                                idleStopwatch.Restart();
                                bool accepted = PreviewScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                                LogService.Instance.Info("MatchNav",
                                    $"ScrollAfterMaterialization: post-layout re-center #{rescrolls} idx={_currentMatchIndex}, runY={runY:N1}, vpTop={vpTop:N1}, vpH={vpH:N1}, toY={target:N1}, accepted={accepted}, total={hardCapStopwatch.ElapsedMilliseconds}ms");
                            }
                        }
                        else
                        {
                            consecutiveOnScreen++;
                            // Two consecutive on-screen confirmations \u2192 layout has stabilised,
                            // BUT only allow early detach after the WinUI Expander animation
                            // has had time to play (~600 ms). Otherwise we routinely detach
                            // 1\u20132 ms in, before the animation re-shuffles layout and pushes
                            // the active run back off-screen with no one watching.
                            const int kMinElapsedForEarlyDetachMs = 600;
                            if (consecutiveOnScreen >= 2 && hardCapStopwatch.ElapsedMilliseconds >= kMinElapsedForEarlyDetachMs)
                            {
                                LogService.Instance.Info("MatchNav",
                                    $"ScrollAfterMaterialization: detach idx={idxAtAttach}, reason=stable-on-screen, layoutPasses={layoutPasses}, rescrolls={rescrolls}, idle={idleStopwatch.ElapsedMilliseconds}ms, total={hardCapStopwatch.ElapsedMilliseconds}ms, finalVpTop={PreviewScrollViewer.VerticalOffset:N1}, runY={runY:N1}");
                                PreviewScrollViewer.LayoutUpdated -= handler;
                                handler = null;
                            }
                        }
                    }
                }
            }
            catch
            {
                if (handler is not null)
                {
                    PreviewScrollViewer.LayoutUpdated -= handler;
                    handler = null;
                }
            }
        };
        PreviewScrollViewer.LayoutUpdated += handler;

        // Also poll on a 100ms DispatcherTimer. LayoutUpdated only fires when WinUI
        // actually invalidates layout, but the Expander animation can finish a
        // pass and leave the run drifted off-screen without firing another
        // LayoutUpdated. Polling guarantees drift is caught within ~100ms instead
        // of waiting for the 1.5s idle timeout's detach-rescue.
        var pollTimer = new Microsoft.UI.Xaml.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        EventHandler<object>? pollHandler = null;
        pollHandler = (_, __) =>
        {
            if (handler is null)
            {
                // Watcher already detached \u2014 stop polling.
                pollTimer.Stop();
                if (pollHandler is not null) pollTimer.Tick -= pollHandler;
                return;
            }
            try
            {
                if (_activeMatchHighlight is { para: var activePara, run: var activeRun }
                    && ReferenceEquals(activePara, targetPara) && activeRun is not null)
                {
                    var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    if (rect.Height > 0)
                    {
                        var t = block.TransformToVisual(PreviewScrollViewer);
                        var p = t.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                        double vpTop = PreviewScrollViewer.VerticalOffset;
                        double vpH = PreviewScrollViewer.ViewportHeight;
                        double runY = vpTop + p.Y;
                        bool onScreen = runY >= vpTop && runY + rect.Height <= vpTop + vpH;
                        if (!onScreen && rescrolls < kMaxRescrolls)
                        {
                            double target = runY - vpH / 2 + rect.Height / 2;
                            target = Math.Clamp(target, 0, PreviewScrollViewer.ScrollableHeight);
                            if (Math.Abs(target - vpTop) > 1)
                            {
                                rescrolls++;
                                idleStopwatch.Restart();
                                consecutiveOnScreen = 0;
                                bool accepted = PreviewScrollViewer.ChangeView(null, target, null, disableAnimation: true);
                                LogService.Instance.Info("MatchNav",
                                    $"ScrollAfterMaterialization: poll re-center #{rescrolls} idx={_currentMatchIndex}, runY={runY:N1}, vpTop={vpTop:N1}, vpH={vpH:N1}, toY={target:N1}, accepted={accepted}, total={hardCapStopwatch.ElapsedMilliseconds}ms");
                            }
                        }
                    }
                }
            }
            catch { }
        };
        pollTimer.Tick += pollHandler;
        pollTimer.Start();
    }

    private void ScrollPreviewToLine(RichTextBlock block, Paragraph targetPara, int attemptsRemaining, int requestId, bool forceCenter)
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (requestId != _matchScrollRequestId)
                return;

            if (TryScrollPreviewToLine(block, targetPara, verifyAfterScroll: false, forceCenter, out var reason))
                return;

            if (attemptsRemaining > 0)
            {
                ScrollPreviewToLine(block, targetPara, attemptsRemaining - 1, requestId, forceCenter);
                return;
            }

            LogService.Instance.Verbose("Preview", $"ScrollPreviewToLine: skipped after layout retries ({reason})");
        });
    }

    private void InvalidatePendingMatchScrolls()
    {
        _matchScrollRequestId++;
    }

    private void OnPreviewViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateScrollPositionCache();
        QueueActiveMatchOverlayRefresh();
    }

    private void QueueActiveMatchOverlayRefresh()
    {
        if (_activeMatchHighlight is null || _activeMatchOverlayRefreshPending || IsPreviewManualScrollActive())
            return;

        _activeMatchOverlayRefreshPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _activeMatchOverlayRefreshPending = false;
            RefreshActiveMatchOverlayPosition();
        });
    }

    private void RefreshActiveMatchOverlayPosition()
    {
        if (IsPreviewManualScrollActive())
        {
            HideActiveMatchOverlay();
            return;
        }

        if (_activeMatchHighlight is not { para: var activePara, run: var activeRun })
        {
            HideActiveMatchOverlay();
            return;
        }

        int activeIndex = FindActiveMatchIndex();
        if ((uint)activeIndex >= (uint)_matchParagraphs.Count)
        {
            HideActiveMatchOverlay();
            return;
        }

        var (block, para, _) = _matchParagraphs[activeIndex];
        if (!ReferenceEquals(para, activePara))
        {
            HideActiveMatchOverlay();
            return;
        }

        InvalidateScrollPositionCache(block);
        if (!TryUpdateActiveMatchOverlayFromActualRun(block, para, activeRun))
            HideActiveMatchOverlay();
    }

    private bool TryScrollPreviewToLine(RichTextBlock block, Paragraph targetPara, bool verifyAfterScroll, bool forceCenter, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (ExpandPreviewSectionForBlock(block))
            {
                reason = "section expanded; waiting for layout";
                return false;
            }

            if (PreviewScrollViewer.ViewportHeight <= 0)
            {
                reason = "preview viewport not ready";
                return false;
            }

            if (forceCenter
                && _activeMatchHighlight is { para: var activePara, run: var activeRun }
                && ReferenceEquals(activePara, targetPara))
            {
                if (TryGetActiveMatchTargetVerticalOffset(block, targetPara, activeRun, out double actualOffset, out string actualSource))
                {
                    double beforeActualVerticalOffset = PreviewScrollViewer.VerticalOffset;
                    bool requested = Math.Abs(actualOffset - beforeActualVerticalOffset) > 1;
                    bool accepted = requested && PreviewScrollViewer.ChangeView(null, actualOffset, null, disableAnimation: true);

                    if (LogService.Instance.IsVerboseEnabled)
                        LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine: idx={_currentMatchIndex}, mode=actual-run, source={actualSource}, forceCenter=True, requested={requested}, accepted={accepted}, fromY={beforeActualVerticalOffset:N1}, targetY={actualOffset:N1}, viewportH={PreviewScrollViewer.ViewportHeight:N1}");

                    ScrollMatchHorizontallyIntoView(block, targetPara);
                    QueueActiveMatchOverlayUpdate(block, targetPara, actualOffset);
                    return true;
                }

                // Fallback to an estimated position when the active run has not
                // produced a usable character rect yet. This is common right
                // after expanding a large section, before WinUI finishes measure.
                double estLineHeight = EstimatePreviewLineHeight(block);
                double estWrappedOffset = EstimateWrappedLineOffset(block, targetPara);
                double estCumHeight = EstimateCumulativeHeightBefore(block, targetPara, estLineHeight);
                double estBlockTop = GetBlockAbsoluteTop(block);
                double estLineCenter = estBlockTop + estCumHeight + estWrappedOffset * estLineHeight + estLineHeight / 2;
                double estimatedOffset = Math.Clamp(
                    estLineCenter - PreviewScrollViewer.ViewportHeight / 2,
                    0, PreviewScrollViewer.ScrollableHeight);

                double beforeEstimatedVerticalOffset = PreviewScrollViewer.VerticalOffset;
                bool requestedEstimated = Math.Abs(estimatedOffset - beforeEstimatedVerticalOffset) > 1;
                bool acceptedEstimated = requestedEstimated && PreviewScrollViewer.ChangeView(null, estimatedOffset, null, disableAnimation: true);

                if (LogService.Instance.IsVerboseEnabled)
                    LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine: idx={_currentMatchIndex}, mode=estimated-active, forceCenter=True, requested={requestedEstimated}, accepted={acceptedEstimated}, fromY={beforeEstimatedVerticalOffset:N1}, targetY={estimatedOffset:N1}, viewportH={PreviewScrollViewer.ViewportHeight:N1}");

                ScrollMatchHorizontallyIntoView(block, targetPara);
                QueueActiveMatchOverlayUpdate(block, targetPara, estimatedOffset);
                return true;
            }

            double lineHeight = EstimatePreviewLineHeight(block);

            int paragraphIndex = GetParagraphIndex(block, targetPara);
            if (paragraphIndex < 0)
            {
                reason = "paragraph not found";
                return false;
            }
            double blockTop = GetBlockAbsoluteTop(block);
            double wrappedLineOffset = EstimateWrappedLineOffset(block, targetPara);
            double cumulativeHeight = EstimateCumulativeHeightBefore(block, targetPara, lineHeight);
            double targetLineCenter = blockTop + cumulativeHeight + wrappedLineOffset * lineHeight + lineHeight / 2;
            double targetVerticalOffset = targetLineCenter - PreviewScrollViewer.ViewportHeight / 2;

            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine(estimated): idx={_currentMatchIndex}, para={paragraphIndex}/{block.Blocks.Count}, wrapOffset={wrappedLineOffset:N1}, blockTop={blockTop:N1}, lineH={lineHeight:N1}, cumulH={cumulativeHeight:N1}");

            targetVerticalOffset = Math.Clamp(targetVerticalOffset, 0, PreviewScrollViewer.ScrollableHeight);
            double beforeVerticalOffset = PreviewScrollViewer.VerticalOffset;
            bool verticalScrollNeeded = forceCenter;
            if (!verticalScrollNeeded)
            {
                double viewportHeight = PreviewScrollViewer.ViewportHeight;
                double guard = Math.Min(160, Math.Max(lineHeight * 4, viewportHeight * 0.22));
                verticalScrollNeeded = targetLineCenter < beforeVerticalOffset + guard
                    || targetLineCenter > beforeVerticalOffset + viewportHeight - guard;
            }

            bool verticalScrollRequested = verticalScrollNeeded && Math.Abs(targetVerticalOffset - beforeVerticalOffset) > 1;
            bool verticalScrollAccepted = false;
            double overlayVerticalOffset = beforeVerticalOffset;
            if (verticalScrollRequested)
            {
                verticalScrollAccepted = PreviewScrollViewer.ChangeView(null, targetVerticalOffset, null, disableAnimation: true);
                if (verticalScrollAccepted)
                    overlayVerticalOffset = targetVerticalOffset;
            }

            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("MatchNav", $"ScrollPreviewToLine: idx={_currentMatchIndex}, mode=estimated, forceCenter={forceCenter}, verticalScroll={verticalScrollNeeded}, requested={verticalScrollRequested}, accepted={verticalScrollAccepted}, fromY={beforeVerticalOffset:N1}, targetY={targetVerticalOffset:N1}, overlayY={overlayVerticalOffset:N1}, lineCenter={targetLineCenter:N1}, viewportH={PreviewScrollViewer.ViewportHeight:N1}");

            ScrollMatchHorizontallyIntoView(block, targetPara);
            QueueActiveMatchOverlayUpdate(block, targetPara, overlayVerticalOffset);
            if (verifyAfterScroll)
            {
                int paraIdx = GetParagraphIndex(block, targetPara);
                VerifyActiveMatchVisibleAfterScroll(block, targetPara, paraIdx);
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            HideActiveMatchOverlay();
            return false;
        }
    }

    private void QueueActiveMatchOverlayUpdate(RichTextBlock block, Paragraph targetPara, double? expectedVerticalOffset = null)
    {
        if (_activeMatchHighlight is not { para: var activePara, run: var activeRun }
            || !ReferenceEquals(activePara, targetPara))
        {
            HideActiveMatchOverlay();
            return;
        }

        HideActiveMatchOverlay();
        int navIndex = _currentMatchIndex;
        Run targetRun = activeRun;
        int requestId = ++_activeMatchOverlayUpdateRequestId;
                int manualScrollVersion = _previewManualScrollVersion;

        bool IsRequestCurrent()
            => _activeMatchOverlayUpdateRequestId == requestId
               && _currentMatchIndex == navIndex
                             && _previewManualScrollVersion == manualScrollVersion
               && _activeMatchHighlight is { para: var currentPara, run: var currentRun }
               && ReferenceEquals(currentPara, targetPara)
               && ReferenceEquals(currentRun, targetRun);

        void EnqueueUpdate(int retriesRemaining, int delayMs)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
            {
                if (!IsRequestCurrent())
                    return;

                if (!TryUpdateActiveMatchOverlayFromActualRun(block, targetPara, targetRun, expectedVerticalOffset, retryIfCenterRejected: retriesRemaining > 0))
                {
                    if (retriesRemaining > 0)
                    {
                        var retryTimer = new Microsoft.UI.Xaml.DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(delayMs)
                        };
                        EventHandler<object>? handler = null;
                        handler = (_, __) =>
                        {
                            retryTimer.Stop();
                            if (handler is not null)
                                retryTimer.Tick -= handler;
                            if (IsRequestCurrent())
                                EnqueueUpdate(retriesRemaining - 1, Math.Min(delayMs * 2, 250));
                        };
                        retryTimer.Tick += handler;
                        retryTimer.Start();
                    }
                    else if (IsRequestCurrent())
                    {
                        // All retries exhausted — force-scroll the run into
                        // view and do one last overlay attempt.
                        targetRun.ElementStart?.GetCharacterRect(
                            Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                        ScrollPreviewToLine(block, targetPara, forceCenter: true);
                    }
                }
            });
        }

        EnqueueUpdate(retriesRemaining: 12, delayMs: 16);
    }

    private bool TryGetActiveMatchTargetVerticalOffset(
        RichTextBlock block,
        Paragraph targetPara,
        Run activeRun,
        out double targetVerticalOffset,
        out string source)
    {
        targetVerticalOffset = 0;
        source = "unavailable";

        double viewportHeight = PreviewScrollViewer.ViewportHeight;
        double viewportWidth = PreviewScrollViewer.ActualWidth;
        if (viewportHeight <= 0)
            viewportHeight = Math.Max(0, PreviewScrollViewer.ActualHeight - PreviewScrollViewer.Padding.Top - PreviewScrollViewer.Padding.Bottom);
        if (viewportWidth <= 0)
            viewportWidth = PreviewScrollViewer.ViewportWidth;
        if (viewportWidth <= 0)
            viewportWidth = Math.Max(0, PreviewScrollViewer.ActualWidth - PreviewScrollViewer.Padding.Left - PreviewScrollViewer.Padding.Right);
        if (viewportHeight <= 0)
            return false;

        var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        if (!IsUsableTextRect(rect))
            return false;

        var point = TransformRunRectToOverlay(block, targetPara, rect);
        double markerHeight = Math.Max(12, rect.Height);
        source = "start";

        if (ViewModel.PreviewWordWrap)
        {
            double charWidth = Math.Max(GetPreviewCharWidth(block, targetPara), rect.Width > 0 ? rect.Width : 0);
            double markerWidth = Math.Max(12, (activeRun.Text?.Length ?? 1) * charWidth);
            bool usedEstimatedPoint = false;
            if (_activeMatchHighlight is { para: var activeParaForWrap, column: var activeColumn }
                && ReferenceEquals(activeParaForWrap, targetPara)
                && TryGetEstimatedWrappedMatchPoint(
                    block,
                    targetPara,
                    activeColumn,
                    markerHeight,
                    viewportWidth,
                    markerWidth,
                    point,
                    out var estimatedPoint,
                    out _)
                && ShouldUseEstimatedWrappedMatchPoint(block, point, estimatedPoint, markerWidth, markerHeight, viewportWidth, out _))
            {
                point = estimatedPoint;
                source = "estimated-wrap";
                usedEstimatedPoint = true;
            }

            if (!usedEstimatedPoint)
            {
                var endRect = activeRun.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward);
                double rowTolerance = Math.Max(4, markerHeight * 0.6);
                if (IsUsableTextRect(endRect) && Math.Abs(endRect.Y - rect.Y) > rowTolerance)
                {
                    var endPoint = TransformRunRectToOverlay(block, targetPara, endRect);
                    point = new Windows.Foundation.Point(ClampOverlayMarkerLeft(endPoint.X - markerWidth, markerWidth, viewportWidth), endPoint.Y);
                    source = "end";
                }
            }
        }

        double actualRunTop = PreviewScrollViewer.VerticalOffset + point.Y - PreviewScrollViewer.Padding.Top;
        double candidate = actualRunTop + markerHeight / 2 - viewportHeight / 2;
        if (double.IsNaN(candidate) || double.IsInfinity(candidate))
            return false;

        targetVerticalOffset = Math.Clamp(candidate, 0, PreviewScrollViewer.ScrollableHeight);
        return true;
    }

    private bool TryUpdateActiveMatchOverlayFromActualRun(RichTextBlock block, Paragraph targetPara, Run targetRun, double? expectedVerticalOffset = null, bool retryIfCenterRejected = false)
    {
        try
        {
            if (_activeMatchHighlight is not { para: var activePara, run: var activeRun, column: var activeColumn, matchInPara: var matchInPara }
                || !ReferenceEquals(activePara, targetPara)
                || !ReferenceEquals(activeRun, targetRun))
                return false;

            double viewportTop = PreviewScrollViewer.Padding.Top;
            double viewportHeight = PreviewScrollViewer.ViewportHeight;
            double viewportWidth = PreviewScrollViewer.ActualWidth;
            if (viewportHeight <= 0)
                viewportHeight = Math.Max(0, PreviewScrollViewer.ActualHeight - PreviewScrollViewer.Padding.Top - PreviewScrollViewer.Padding.Bottom);
            if (viewportWidth <= 0)
                viewportWidth = PreviewScrollViewer.ViewportWidth;
            if (viewportWidth <= 0)
                viewportWidth = Math.Max(0, PreviewScrollViewer.ActualWidth - PreviewScrollViewer.Padding.Left - PreviewScrollViewer.Padding.Right);
            double viewportBottom = viewportTop + viewportHeight;
            if (viewportWidth <= 0 || viewportHeight <= 0)
                return false;

            if (!IsPreviewSectionBodySettledForActiveOverlay(block, out var layoutReason))
            {
                if (LogService.Instance.IsVerboseEnabled)
                {
                    int paragraphIndex = GetParagraphIndex(block, targetPara);
                    LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: retry unsettled section layout idx={_currentMatchIndex}, paraIdx={paragraphIndex}, reason={layoutReason}");
                }
                return false;
            }

            // Flush pending layout so TransformToVisual reflects any recent
            // scroll offset changes (both vertical and horizontal).
            // The canvas must be Visible before UpdateLayout so it receives a
            // proper arrange pass — TransformToVisual against a Collapsed
            // element returns unreliable coordinates.
            // We also update the block itself to ensure word-wrap reflow has
            // completed before querying character positions.
            if (ActiveMatchOverlay.Visibility != Visibility.Visible)
                ActiveMatchOverlay.Visibility = Visibility.Visible;
            try
            {
                // Force text layout reflow at the current wrap width.
                // RichTextBlock.UpdateLayout() alone may not re-wrap text;
                // an explicit Measure ensures GetCharacterRect returns positions
                // consistent with the current block width.
                double measureWidth = ViewModel.PreviewWordWrap ? GetPreviewWrapTextWidth(block) : block.ActualWidth;
                if (measureWidth > 0)
                    block.Measure(new Windows.Foundation.Size(measureWidth, double.PositiveInfinity));
                block.UpdateLayout();
                PreviewScrollViewer.UpdateLayout();
            }
            catch { }

            var rect = targetRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
            if (!IsUsableTextRect(rect))
                return false;

            var endRect = targetRun.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward);
            var point = TransformRunRectToOverlay(block, targetPara, rect);
            var rawPoint = point;
            double markerHeight = Math.Max(12, rect.Height);
            double charWidth = Math.Max(GetPreviewCharWidth(block, targetPara), rect.Width > 0 ? rect.Width : 0);
            double markerWidth = Math.Max(12, (targetRun.Text?.Length ?? 1) * charWidth);

            bool usedEndRect = false;
            bool usedWrappedEstimate = false;
            bool usedWrappedPointEstimate = false;
            Windows.Foundation.Point? estimatedPointForLog = null;
            string estimateReasonForLog = "not-evaluated";
            List<Windows.Foundation.Rect>? wrappedMarkerRects = null;
            string wrappedMarkerDetails = string.Empty;
            if (ViewModel.PreviewWordWrap)
            {
                bool hasEstimatedPoint = TryGetEstimatedWrappedMatchPoint(
                        block,
                        targetPara,
                        activeColumn,
                        markerHeight,
                        viewportWidth,
                        markerWidth,
                        point,
                        out var estimatedPoint,
                        out var estimatedDetails);
                if (hasEstimatedPoint)
                {
                    estimatedPointForLog = estimatedPoint;
                }
                else
                {
                    estimateReasonForLog = "estimate-unavailable";
                }

                if (hasEstimatedPoint
                    && ShouldUseEstimatedWrappedMatchPoint(block, point, estimatedPoint, markerWidth, markerHeight, viewportWidth, out estimateReasonForLog))
                {
                    point = estimatedPoint;
                    usedWrappedPointEstimate = true;
                    wrappedMarkerDetails = estimatedDetails;
                }
                else if (hasEstimatedPoint)
                {
                    if (string.IsNullOrEmpty(estimateReasonForLog))
                        estimateReasonForLog = "estimate-rejected";
                }

                double rowTolerance = Math.Max(4, markerHeight * 0.6);
                bool measuredEndSameRow = IsUsableTextRect(endRect) && Math.Abs(endRect.Y - rect.Y) <= rowTolerance;
                if (usedWrappedPointEstimate && TryBuildWrappedActiveMatchMarkerRects(
                        block,
                        targetPara,
                        activeColumn,
                        targetRun.Text?.Length ?? 0,
                        markerHeight,
                        viewportWidth,
                        out var estimatedMarkerRectsFromPoint,
                        out var estimatedMarkerDetailsFromPoint))
                {
                    wrappedMarkerRects = estimatedMarkerRectsFromPoint;
                    usedWrappedEstimate = true;
                    wrappedMarkerDetails = estimatedMarkerDetailsFromPoint;
                    point = new Windows.Foundation.Point(wrappedMarkerRects[0].X, wrappedMarkerRects[0].Y);
                    markerWidth = wrappedMarkerRects[0].Width;
                }
                else if (!usedWrappedPointEstimate && TryBuildMeasuredWrappedActiveMatchMarkerRects(
                        block,
                        targetPara,
                        point,
                        endRect,
                        markerHeight,
                        viewportWidth,
                        charWidth,
                        out var measuredMarkerRects,
                        out wrappedMarkerDetails))
                {
                    wrappedMarkerRects = measuredMarkerRects;
                    point = new Windows.Foundation.Point(wrappedMarkerRects[0].X, wrappedMarkerRects[0].Y);
                    markerWidth = wrappedMarkerRects[0].Width;
                }
                else if (!measuredEndSameRow && TryBuildWrappedActiveMatchMarkerRects(
                        block,
                        targetPara,
                        activeColumn,
                        targetRun.Text?.Length ?? 0,
                        markerHeight,
                        viewportWidth,
                        out var estimatedMarkerRects,
                        out wrappedMarkerDetails))
                {
                    wrappedMarkerRects = estimatedMarkerRects;
                    usedWrappedEstimate = true;
                    point = new Windows.Foundation.Point(wrappedMarkerRects[0].X, wrappedMarkerRects[0].Y);
                    markerWidth = wrappedMarkerRects[0].Width;
                }

                if (wrappedMarkerRects is null && IsUsableTextRect(endRect) && Math.Abs(endRect.Y - rect.Y) > rowTolerance)
                {
                    var endPoint = TransformRunRectToOverlay(block, targetPara, endRect);
                    point = new Windows.Foundation.Point(ClampOverlayMarkerLeft(endPoint.X - markerWidth, markerWidth, viewportWidth), endPoint.Y);
                    usedEndRect = true;
                }
            }
            double currentVerticalOffset = PreviewScrollViewer.VerticalOffset;
            double actualRunTop = currentVerticalOffset + point.Y - viewportTop;
            double overlayTop = point.Y;
            double effectiveVerticalOffset = currentVerticalOffset;
            bool centeredFromActualRun = false;
            bool actualCenterAccepted = false;
            if (expectedVerticalOffset.HasValue)
            {
                double actualTargetVerticalOffset = actualRunTop + markerHeight / 2 - viewportHeight / 2;
                actualTargetVerticalOffset = Math.Clamp(actualTargetVerticalOffset, 0, PreviewScrollViewer.ScrollableHeight);
                centeredFromActualRun = true;
                bool actualCenterNeeded = Math.Abs(actualTargetVerticalOffset - currentVerticalOffset) > 1;
                if (actualCenterNeeded)
                    actualCenterAccepted = PreviewScrollViewer.ChangeView(null, actualTargetVerticalOffset, null, disableAnimation: true);

                if (actualCenterAccepted)
                {
                    if (LogService.Instance.IsVerboseEnabled)
                    {
                        int paragraphIndex = GetParagraphIndex(block, targetPara);
                        LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: centering requested idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, fromY={currentVerticalOffset:N1}, toY={actualTargetVerticalOffset:N1}; waiting for settled layout");
                    }
                    return false;
                }

                if (!actualCenterNeeded)
                    effectiveVerticalOffset = actualTargetVerticalOffset;
                else if (retryIfCenterRejected)
                {
                    if (LogService.Instance.IsVerboseEnabled)
                    {
                        int paragraphIndex = GetParagraphIndex(block, targetPara);
                        LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: retry centering idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, scrollY={currentVerticalOffset:N1}, expectedScrollY={expectedVerticalOffset.Value:N1}, actualTargetY={actualTargetVerticalOffset:N1}");
                    }
                    return false;
                }
                else
                {
                    return false;
                }

                overlayTop = actualRunTop - effectiveVerticalOffset + viewportTop;
            }

            List<Windows.Foundation.Rect>? effectiveWrappedMarkerRects = wrappedMarkerRects;
            double markerTopDelta = overlayTop - point.Y;
            if (wrappedMarkerRects is { Count: > 1 } && Math.Abs(markerTopDelta) > 0.01)
            {
                effectiveWrappedMarkerRects = new List<Windows.Foundation.Rect>(wrappedMarkerRects.Count);
                foreach (var markerRect in wrappedMarkerRects)
                {
                    effectiveWrappedMarkerRects.Add(new Windows.Foundation.Rect(
                        markerRect.X,
                        markerRect.Y + markerTopDelta,
                        markerRect.Width,
                        markerRect.Height));
                }
            }

            double viewportGuard = Math.Min(40, Math.Max(8, markerHeight * 0.75));
            bool markerOutsideViewport = effectiveWrappedMarkerRects is { Count: > 1 }
                ? effectiveWrappedMarkerRects.Any(markerRect => markerRect.Y < viewportTop || markerRect.Y + markerRect.Height > viewportBottom)
                : overlayTop < viewportTop || overlayTop + markerHeight > viewportBottom;
            bool markerTooCloseToEdge = effectiveWrappedMarkerRects is { Count: > 1 }
                ? effectiveWrappedMarkerRects.Any(markerRect => markerRect.Y < viewportTop + viewportGuard || markerRect.Y + markerRect.Height > viewportBottom - viewportGuard)
                : overlayTop < viewportTop + viewportGuard || overlayTop + markerHeight > viewportBottom - viewportGuard;
            if ((markerOutsideViewport || (expectedVerticalOffset.HasValue && markerTooCloseToEdge)) && retryIfCenterRejected)
            {
                double correctiveTarget = actualRunTop + markerHeight / 2 - viewportHeight / 2;
                correctiveTarget = Math.Clamp(correctiveTarget, 0, PreviewScrollViewer.ScrollableHeight);
                if (Math.Abs(correctiveTarget - currentVerticalOffset) > 1)
                {
                    bool accepted = PreviewScrollViewer.ChangeView(null, correctiveTarget, null, disableAnimation: true);
                    LogWordWrapOverlayDiagnostic(
                        "edge-correction",
                        block,
                        targetPara,
                        targetRun,
                        activeColumn,
                        matchInPara,
                        rect,
                        endRect,
                        rawPoint,
                        estimatedPointForLog,
                        point,
                        markerWidth,
                        markerHeight,
                        charWidth,
                        viewportTop,
                        viewportBottom,
                        viewportHeight,
                        viewportWidth,
                        currentVerticalOffset,
                        expectedVerticalOffset,
                        effectiveVerticalOffset,
                        overlayTop,
                        actualRunTop,
                        point.X,
                        markerWidth,
                        0,
                        viewportWidth,
                        usedEndRect,
                        usedWrappedPointEstimate,
                        usedWrappedEstimate,
                        centeredFromActualRun,
                        actualCenterAccepted,
                        estimateReasonForLog,
                        wrappedMarkerDetails,
                        effectiveWrappedMarkerRects);
                    if (LogService.Instance.IsVerboseEnabled)
                    {
                        int paragraphIndex = GetParagraphIndex(block, targetPara);
                        LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: edge correction idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, markerTop={overlayTop:N1}, markerH={markerHeight:N1}, viewportTop={viewportTop:N1}, viewportBottom={viewportBottom:N1}, viewportH={viewportHeight:N1}, fromY={currentVerticalOffset:N1}, toY={correctiveTarget:N1}, accepted={accepted}");
                    }
                    return false;
                }
            }

            if (overlayTop < viewportTop || overlayTop + markerHeight > viewportBottom)
            {
                LogWordWrapOverlayDiagnostic(
                    "reject-offscreen",
                    block,
                    targetPara,
                    targetRun,
                    activeColumn,
                    matchInPara,
                    rect,
                    endRect,
                    rawPoint,
                    estimatedPointForLog,
                    point,
                    markerWidth,
                    markerHeight,
                    charWidth,
                    viewportTop,
                    viewportBottom,
                    viewportHeight,
                    viewportWidth,
                    currentVerticalOffset,
                    expectedVerticalOffset,
                    effectiveVerticalOffset,
                    overlayTop,
                    actualRunTop,
                    point.X,
                    markerWidth,
                    0,
                    viewportWidth,
                    usedEndRect,
                    usedWrappedPointEstimate,
                    usedWrappedEstimate,
                    centeredFromActualRun,
                    actualCenterAccepted,
                    estimateReasonForLog,
                    wrappedMarkerDetails,
                    effectiveWrappedMarkerRects);
                if (LogService.Instance.IsVerboseEnabled)
                {
                    int paragraphIndex = GetParagraphIndex(block, targetPara);
                    LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: rejecting offscreen marker idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, markerTop={overlayTop:N1}, markerH={markerHeight:N1}, viewportTop={viewportTop:N1}, viewportBottom={viewportBottom:N1}, viewportH={viewportHeight:N1}, scrollY={currentVerticalOffset:N1}");
                }
                return false;
            }

            bool markerFullyOutside = point.X + markerWidth <= 0 || point.X >= viewportWidth;
            if (!ViewModel.PreviewWordWrap && markerFullyOutside)
            {
                if (retryIfCenterRejected)
                    ScrollMatchHorizontallyIntoView(block, targetPara);

                if (LogService.Instance.IsVerboseEnabled)
                {
                    int paragraphIndex = GetParagraphIndex(block, targetPara);
                    LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: rejecting horizontally offscreen marker idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, pointX={point.X:N1}, markerW={markerWidth:N1}, viewportW={viewportWidth:N1}, retry={retryIfCenterRejected}");
                }
                return false;
            }

            ClearActiveMatchExtraWordMarkers();

            // Compute the content area's left edge (after the gutter) so the band
            // doesn't extend underneath the line-number gutter.
            double bandLeft = 0;
            double bandWidth = viewportWidth;
            if (_sectionMatchNavs.TryGetValue(block, out var navForBand))
            {
                try
                {
                    var scrollerOrigin = navForBand.Scroller.TransformToVisual(ActiveMatchOverlay)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    if (scrollerOrigin.X > 0)
                    {
                        bandLeft = scrollerOrigin.X;
                        bandWidth = Math.Max(0, viewportWidth - bandLeft);
                    }
                }
                catch { }
            }

            // Clip the word marker to the visible content area (between gutter and right edge).
            double clippedMarkerLeft = Math.Max(point.X, bandLeft);
            double clippedMarkerRight = Math.Min(point.X + markerWidth, viewportWidth);
            double visibleMarkerWidth = Math.Max(0, clippedMarkerRight - clippedMarkerLeft);
            double markerLeft = clippedMarkerLeft;

            ActiveMatchBand.Height = markerHeight;
            ActiveMatchBand.Width = bandWidth;
            Canvas.SetTop(ActiveMatchBand, overlayTop);
            Canvas.SetLeft(ActiveMatchBand, bandLeft);

            if (effectiveWrappedMarkerRects is { Count: > 1 })
            {
                ApplyActiveMatchMarkerRect(ActiveMatchWordMarker, effectiveWrappedMarkerRects[0]);
                for (int i = 1; i < effectiveWrappedMarkerRects.Count; i++)
                {
                    var marker = CreateActiveMatchWordMarker();
                    ApplyActiveMatchMarkerRect(marker, effectiveWrappedMarkerRects[i]);
                    _activeMatchExtraWordMarkers.Add(marker);
                    ActiveMatchOverlay.Children.Add(marker);
                }
            }
            else
            {
                if (visibleMarkerWidth > 0)
                {
                    ActiveMatchWordMarker.Height = markerHeight;
                    ActiveMatchWordMarker.Width = visibleMarkerWidth;
                    Canvas.SetTop(ActiveMatchWordMarker, overlayTop);
                    Canvas.SetLeft(ActiveMatchWordMarker, markerLeft);
                }
                else
                {
                    ActiveMatchWordMarker.Width = 0;
                    ActiveMatchWordMarker.Height = 0;
                }
            }

            ActiveMatchOverlay.Visibility = Visibility.Visible;
            LogWordWrapOverlayDiagnostic(
                "applied",
                block,
                targetPara,
                targetRun,
                activeColumn,
                matchInPara,
                rect,
                endRect,
                rawPoint,
                estimatedPointForLog,
                point,
                markerWidth,
                markerHeight,
                charWidth,
                viewportTop,
                viewportBottom,
                viewportHeight,
                viewportWidth,
                currentVerticalOffset,
                expectedVerticalOffset,
                effectiveVerticalOffset,
                overlayTop,
                actualRunTop,
                markerLeft,
                visibleMarkerWidth,
                bandLeft,
                bandWidth,
                usedEndRect,
                usedWrappedPointEstimate,
                usedWrappedEstimate,
                centeredFromActualRun,
                actualCenterAccepted,
                estimateReasonForLog,
                wrappedMarkerDetails,
                effectiveWrappedMarkerRects);
            if (LogService.Instance.IsVerboseEnabled)
            {
                int paragraphIndex = GetParagraphIndex(block, targetPara);
                string expectedScroll = expectedVerticalOffset.HasValue ? expectedVerticalOffset.Value.ToString("N1") : "actual";
                LogService.Instance.Verbose("MatchNav", $"ActiveOverlay: idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, rect=({rect.X:N1},{rect.Y:N1},{rect.Width:N1},{rect.Height:N1}), endRect=({endRect.X:N1},{endRect.Y:N1},{endRect.Width:N1},{endRect.Height:N1}), point=({point.X:N1},{point.Y:N1}), scrollY={currentVerticalOffset:N1}, expectedScrollY={expectedScroll}, effectiveScrollY={effectiveVerticalOffset:N1}, centeredActual={centeredFromActualRun}, centerAccepted={actualCenterAccepted}, endRectUsed={usedEndRect}, wrapPointEstimateUsed={usedWrappedPointEstimate}, wrapEstimateUsed={usedWrappedEstimate}, wrapSegments={effectiveWrappedMarkerRects?.Count ?? 0} {wrappedMarkerDetails}, marker=({markerLeft:N1},{overlayTop:N1},{visibleMarkerWidth:N1},{markerHeight:N1}), unclampedMarkerW={markerWidth:N1}, text='{targetRun.Text}'");
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUsableTextRect(Windows.Foundation.Rect rect)
        => !double.IsNaN(rect.X)
           && !double.IsNaN(rect.Y)
           && rect.Height > 0;

    private bool TryBuildWrappedActiveMatchMarkerRects(
        RichTextBlock block,
        Paragraph targetPara,
        int column,
        int matchLength,
        double markerHeight,
        double viewportWidth,
        out List<Windows.Foundation.Rect> markerRects,
        out string details)
    {
        markerRects = new List<Windows.Foundation.Rect>();
        details = string.Empty;
        if (!ViewModel.PreviewWordWrap || column < 0 || matchLength <= 0)
            return false;

        double charWidth = GetPreviewCharWidth(block, targetPara);
        int charsPerWrappedLine = GetPreviewWrappedCharsPerLine(block, targetPara, charWidth);
        int startRow = column / charsPerWrappedLine;
        int endExclusive = column + matchLength;
        int endRow = (Math.Max(column, endExclusive - 1)) / charsPerWrappedLine;
        if (endRow <= startRow)
            return false;

        var firstRun = targetPara.Inlines.OfType<Run>().FirstOrDefault();
        if (firstRun is null)
            return false;

        var firstRect = firstRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        if (!IsUsableTextRect(firstRect))
            return false;

        var firstPoint = TransformRunRectToOverlay(block, targetPara, firstRect);
        double lineHeight = Math.Max(markerHeight, firstRect.Height);

        for (int row = startRow; row <= endRow; row++)
        {
            int rowStart = row * charsPerWrappedLine;
            int rowEnd = rowStart + charsPerWrappedLine;
            int segmentStart = Math.Max(column, rowStart);
            int segmentEnd = Math.Min(endExclusive, rowEnd);
            int segmentLength = segmentEnd - segmentStart;
            if (segmentLength <= 0)
                continue;

            double left = firstPoint.X + (segmentStart - rowStart) * charWidth;
            left = ClampOverlayMarkerLeft(left, segmentLength * charWidth, viewportWidth);
            double width = Math.Max(12, Math.Min(segmentLength * charWidth, Math.Max(12, viewportWidth - left)));
            double top = firstPoint.Y + row * lineHeight;
            markerRects.Add(new Windows.Foundation.Rect(left, top, width, markerHeight));
        }

        details = $"column={column}, len={matchLength}, charsPerLine={charsPerWrappedLine}, rows={startRow}-{endRow}";
        return markerRects.Count > 1;
    }

    private bool TryBuildMeasuredWrappedActiveMatchMarkerRects(
        RichTextBlock block,
        Paragraph targetPara,
        Windows.Foundation.Point startPoint,
        Windows.Foundation.Rect endRect,
        double markerHeight,
        double viewportWidth,
        double charWidth,
        out List<Windows.Foundation.Rect> markerRects,
        out string details)
    {
        markerRects = new List<Windows.Foundation.Rect>();
        details = string.Empty;
        if (!ViewModel.PreviewWordWrap || viewportWidth <= 0 || !IsUsableTextRect(endRect))
            return false;

        var endPoint = TransformRunRectToOverlay(block, targetPara, endRect);
        double lineHeight = Math.Max(markerHeight, endRect.Height);
        double rowTolerance = Math.Max(4, lineHeight * 0.6);
        double rowDistance = endPoint.Y - startPoint.Y;
        if (rowDistance <= rowTolerance)
            return false;

        double lineLeft = 0;
        var firstRun = targetPara.Inlines.OfType<Run>().FirstOrDefault();
        if (firstRun is not null)
        {
            var firstRect = firstRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
            if (IsUsableTextRect(firstRect))
                lineLeft = TransformRunRectToOverlay(block, targetPara, firstRect).X;
        }

        int rowSpan = Math.Max(1, (int)Math.Round(rowDistance / lineHeight, MidpointRounding.AwayFromZero));
        rowSpan = Math.Min(rowSpan, 64);
        double rowStep = rowDistance / rowSpan;
        double lineRight = Math.Max(lineLeft + 12, viewportWidth);
        double endRight = endPoint.X + Math.Max(1, Math.Max(endRect.Width, charWidth));

        for (int rowIndex = 0; rowIndex <= rowSpan; rowIndex++)
        {
            double top = rowIndex == rowSpan ? endPoint.Y : startPoint.Y + rowIndex * rowStep;
            double left;
            double right;
            if (rowIndex == 0)
            {
                left = startPoint.X;
                right = lineRight;
            }
            else if (rowIndex == rowSpan)
            {
                left = lineLeft;
                right = Math.Clamp(endRight, lineLeft + 12, lineRight);
            }
            else
            {
                left = lineLeft;
                right = lineRight;
            }

            double rawWidth = Math.Max(12, right - left);
            left = ClampOverlayMarkerLeft(left, rawWidth, viewportWidth);
            double width = Math.Max(12, Math.Min(rawWidth, Math.Max(12, viewportWidth - left)));
            markerRects.Add(new Windows.Foundation.Rect(left, top, width, markerHeight));
        }

        details = $"measured rows=0-{rowSpan}, start=({startPoint.X:N1},{startPoint.Y:N1}), end=({endPoint.X:N1},{endPoint.Y:N1}), lineLeft={lineLeft:N1}";
        return markerRects.Count > 1;
    }

    private Border CreateActiveMatchWordMarker()
    {
        var marker = new Border
        {
            Height = 18,
            MinWidth = 12,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x1A, _overlayColor.R, _overlayColor.G, _overlayColor.B)),
            BorderBrush = new SolidColorBrush(_overlayColor),
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(2),
        };
        Canvas.SetZIndex(marker, 21);
        return marker;
    }

    private static void ApplyActiveMatchMarkerRect(Border marker, Windows.Foundation.Rect rect)
    {
        marker.Height = rect.Height;
        marker.Width = rect.Width;
        Canvas.SetTop(marker, rect.Y);
        Canvas.SetLeft(marker, rect.X);
    }

    private void ClearActiveMatchExtraWordMarkers()
    {
        foreach (var marker in _activeMatchExtraWordMarkers)
            ActiveMatchOverlay.Children.Remove(marker);
        _activeMatchExtraWordMarkers.Clear();
    }

    private Windows.Foundation.Point TransformRunRectToOverlay(RichTextBlock block, Paragraph targetPara, Windows.Foundation.Rect rect)
    {
        var transform = block.TransformToVisual(ActiveMatchOverlay);
        // TextPointer.GetCharacterRect already reports a block-relative Y for
        // RichTextBlock runs. Adding paragraph cumulative height here double-
        // counted prior paragraphs and pushed later active-match overlays down.
        return transform.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
    }

    private bool IsPreviewSectionBodySettledForActiveOverlay(RichTextBlock block, out string reason)
    {
        reason = string.Empty;
        if (!_blockExpanderCache.TryGetValue(block, out var expander))
            return true;

        if (!expander.IsExpanded)
        {
            reason = "section collapsed";
            return false;
        }

        if (expander.Header is not FrameworkElement header || header.ActualHeight <= 0)
        {
            reason = "header not measured";
            return false;
        }

        try
        {
            var expanderPoint = expander.TransformToVisual(ActiveMatchOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var blockPoint = block.TransformToVisual(ActiveMatchOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double minBodyTop = expanderPoint.Y + Math.Clamp(header.ActualHeight, 24, 80) - 1;
            if (blockPoint.Y < minBodyTop)
            {
                reason = $"blockTop={blockPoint.Y:N1}, expanderTop={expanderPoint.Y:N1}, headerH={header.ActualHeight:N1}, minBodyTop={minBodyTop:N1}";
                return false;
            }

            long now = Environment.TickCount64;
            if (!ReferenceEquals(_activeOverlayStabilityBlock, block)
                || double.IsNaN(_activeOverlayLastBlockTop))
            {
                _activeOverlayStabilityBlock = block;
                _activeOverlayLastBlockTop = blockPoint.Y;
                _activeOverlayLastMoveTick = now;
                _activeOverlayStablePasses = 0;
                reason = $"waiting for stable section layout: blockTop={blockPoint.Y:N1}";
                return false;
            }

            if (Math.Abs(blockPoint.Y - _activeOverlayLastBlockTop) > 0.75)
            {
                reason = $"section still moving: prevBlockTop={_activeOverlayLastBlockTop:N1}, blockTop={blockPoint.Y:N1}";
                _activeOverlayLastBlockTop = blockPoint.Y;
                _activeOverlayLastMoveTick = now;
                _activeOverlayStablePasses = 0;
                return false;
            }

            _activeOverlayStablePasses++;
            const int requiredStablePasses = 2;
            const int requiredStableMilliseconds = 80;
            long stableFor = now - _activeOverlayLastMoveTick;
            if (_activeOverlayStablePasses < requiredStablePasses || stableFor < requiredStableMilliseconds)
            {
                reason = $"section not stable long enough: blockTop={blockPoint.Y:N1}, passes={_activeOverlayStablePasses}, stableFor={stableFor}ms";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name;
            return false;
        }

        return true;
    }

    private bool TryGetEstimatedWrappedMatchPoint(
        RichTextBlock block,
        Paragraph targetPara,
        int column,
        double markerHeight,
        double viewportWidth,
        double markerWidth,
        Windows.Foundation.Point actualPoint,
        out Windows.Foundation.Point estimatedPoint,
        out string details)
    {
        estimatedPoint = actualPoint;
        details = string.Empty;
        if (!ViewModel.PreviewWordWrap || column <= 0)
            return false;

        double charWidth = GetPreviewCharWidth(block, targetPara);
        int charsPerWrappedLine = GetPreviewWrappedCharsPerLine(block, targetPara, charWidth);
        int wrappedLineIndex = column / charsPerWrappedLine;
        if (wrappedLineIndex <= 0)
            return false;

        var firstRun = targetPara.Inlines.OfType<Run>().FirstOrDefault();
        if (firstRun is null)
            return false;

        var firstRect = firstRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        if (!IsUsableTextRect(firstRect))
            return false;

        var firstPoint = TransformRunRectToOverlay(block, targetPara, firstRect);
        double lineHeight = Math.Max(markerHeight, firstRect.Height);
        double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;
        double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;
        double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);
        estimatedPoint = new Windows.Foundation.Point(correctedLeft, expectedTop);
        details = $"column={column}, charsPerLine={charsPerWrappedLine}, wrapRow={wrappedLineIndex}, charW={charWidth:N1}, wrapW={GetPreviewWrapTextWidth(block):N1}, actual=({actualPoint.X:N1},{actualPoint.Y:N1}), estimated=({estimatedPoint.X:N1},{estimatedPoint.Y:N1})";
        return true;
    }

    private bool ShouldUseEstimatedWrappedMatchPoint(
        RichTextBlock block,
        Windows.Foundation.Point actualPoint,
        Windows.Foundation.Point estimatedPoint,
        double markerWidth,
        double markerHeight,
        double viewportWidth,
        out string reason)
    {
        reason = "not-needed";
        if (!ViewModel.PreviewWordWrap || viewportWidth <= 0)
        {
            reason = "wrap-off-or-no-viewport";
            return false;
        }

        if (double.IsNaN(actualPoint.X) || double.IsInfinity(actualPoint.X)
            || double.IsNaN(actualPoint.Y) || double.IsInfinity(actualPoint.Y)
            || double.IsNaN(estimatedPoint.X) || double.IsInfinity(estimatedPoint.X)
            || double.IsNaN(estimatedPoint.Y) || double.IsInfinity(estimatedPoint.Y))
        {
            reason = "invalid-point";
            return false;
        }

        GetPreviewTextOverlayBounds(block, viewportWidth, out double textLeft, out double textRight);
        double tolerance = Math.Max(8, markerHeight * 0.75);
        if (actualPoint.X < textLeft - tolerance || actualPoint.X > textRight + tolerance)
        {
            reason = $"actual-x-outside-text-bounds textLeft={textLeft:N1}, textRight={textRight:N1}, tolerance={tolerance:N1}";
            return true;
        }

        double horizontalDelta = Math.Abs(actualPoint.X - estimatedPoint.X);
        double verticalDelta = Math.Abs(actualPoint.Y - estimatedPoint.Y);
        bool useEstimate = horizontalDelta > Math.Max(markerWidth, 80) && verticalDelta > markerHeight * 2;
        reason = useEstimate
            ? $"large-delta dx={horizontalDelta:N1}, dy={verticalDelta:N1}"
            : $"actual-within-bounds dx={horizontalDelta:N1}, dy={verticalDelta:N1}, textLeft={textLeft:N1}, textRight={textRight:N1}";
        return useEstimate;
    }

    private void GetPreviewTextOverlayBounds(RichTextBlock block, double viewportWidth, out double textLeft, out double textRight)
    {
        textLeft = 0;
        textRight = Math.Max(0, viewportWidth);
        if (viewportWidth <= 0)
            return;

        if (_sectionMatchNavs.TryGetValue(block, out var sectionNav))
        {
            try
            {
                var scrollerOrigin = sectionNav.Scroller.TransformToVisual(ActiveMatchOverlay)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                double width = sectionNav.Scroller.ViewportWidth > 0
                    ? sectionNav.Scroller.ViewportWidth
                    : sectionNav.Scroller.ActualWidth;
                if (width > 0)
                {
                    textLeft = Math.Clamp(scrollerOrigin.X, 0, viewportWidth);
                    textRight = Math.Clamp(scrollerOrigin.X + width, textLeft, viewportWidth);
                }
            }
            catch { }
        }
    }

    private void LogWordWrapOverlayDiagnostic(
        string stage,
        RichTextBlock block,
        Paragraph targetPara,
        Run targetRun,
        int activeColumn,
        int matchInPara,
        Windows.Foundation.Rect startRect,
        Windows.Foundation.Rect endRect,
        Windows.Foundation.Point rawPoint,
        Windows.Foundation.Point? estimatedPoint,
        Windows.Foundation.Point finalPoint,
        double markerWidth,
        double markerHeight,
        double charWidth,
        double viewportTop,
        double viewportBottom,
        double viewportHeight,
        double viewportWidth,
        double currentVerticalOffset,
        double? expectedVerticalOffset,
        double effectiveVerticalOffset,
        double overlayTop,
        double actualRunTop,
        double markerLeft,
        double visibleMarkerWidth,
        double bandLeft,
        double bandWidth,
        bool usedEndRect,
        bool usedWrappedPointEstimate,
        bool usedWrappedEstimate,
        bool centeredFromActualRun,
        bool actualCenterAccepted,
        string estimateReason,
        string wrappedMarkerDetails,
        IReadOnlyList<Windows.Foundation.Rect>? markerRects)
    {
        if (!ViewModel.PreviewWordWrap)
            return;

        try
        {
            int paragraphIndex = GetParagraphIndex(block, targetPara);
            double wrapWidth = GetPreviewWrapTextWidth(block);
            int charsPerLine = GetPreviewWrappedCharsPerLine(block, targetPara, charWidth);
            GetPreviewTextOverlayBounds(block, viewportWidth, out double textLeft, out double textRight);

            var blockOverlayOrigin = block.TransformToVisual(ActiveMatchOverlay)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            var blockPreviewOrigin = block.TransformToVisual(PreviewScrollViewer)
                .TransformPoint(new Windows.Foundation.Point(0, 0));

            string sectionDetails = "section=none";
            if (_sectionMatchNavs.TryGetValue(block, out var sectionNav))
            {
                try
                {
                    var scrollerOverlayOrigin = sectionNav.Scroller.TransformToVisual(ActiveMatchOverlay)
                        .TransformPoint(new Windows.Foundation.Point(0, 0));
                    sectionDetails = $"section=present, scrollerOrigin=({scrollerOverlayOrigin.X:N1},{scrollerOverlayOrigin.Y:N1}), " +
                        $"scrollerActual=({sectionNav.Scroller.ActualWidth:N1},{sectionNav.Scroller.ActualHeight:N1}), " +
                        $"scrollerViewport=({sectionNav.Scroller.ViewportWidth:N1},{sectionNav.Scroller.ViewportHeight:N1}), " +
                        $"scrollerOffset=({sectionNav.Scroller.HorizontalOffset:N1},{sectionNav.Scroller.VerticalOffset:N1}), " +
                        $"scrollerScrollable=({sectionNav.Scroller.ScrollableWidth:N1},{sectionNav.Scroller.ScrollableHeight:N1})";
                }
                catch (Exception ex)
                {
                    sectionDetails = $"section=present, scrollerError={ex.GetType().Name}";
                }
            }

            string file = block.Tag as string ?? string.Empty;
            string text = targetRun.Text ?? string.Empty;
            text = text.Replace("\r", "\\r").Replace("\n", "\\n");
            if (text.Length > 100)
                text = string.Concat(text.AsSpan(0, 100), "...");

            string expectedScroll = expectedVerticalOffset.HasValue ? expectedVerticalOffset.Value.ToString("N1") : "actual";
            string estimatedText = estimatedPoint.HasValue ? FormatPoint(estimatedPoint.Value) : "none";
            string markerRectsText = FormatMarkerRects(markerRects);

            LogService.Instance.Warning("MatchNav",
                $"WrapOverlayDiag stage={stage}, idx={_currentMatchIndex}, paraIdx={paragraphIndex}, matchInPara={matchInPara}, " +
                $"column={activeColumn}, text='{text}', file='{file}', " +
                $"viewport=(w={viewportWidth:N1}, h={viewportHeight:N1}, top={viewportTop:N1}, bottom={viewportBottom:N1}, scrollY={currentVerticalOffset:N1}, expectedScrollY={expectedScroll}, effectiveScrollY={effectiveVerticalOffset:N1}), " +
                $"overlaySize=({ActiveMatchOverlay.ActualWidth:N1},{ActiveMatchOverlay.ActualHeight:N1}), previewActual=({PreviewScrollViewer.ActualWidth:N1},{PreviewScrollViewer.ActualHeight:N1}), previewViewport=({PreviewScrollViewer.ViewportWidth:N1},{PreviewScrollViewer.ViewportHeight:N1}), " +
                $"blockActual=({block.ActualWidth:N1},{block.ActualHeight:N1}), blockDesired=({block.DesiredSize.Width:N1},{block.DesiredSize.Height:N1}), blockOriginOverlay={FormatPoint(blockOverlayOrigin)}, blockOriginPreview={FormatPoint(blockPreviewOrigin)}, " +
                $"wrap=(width={wrapWidth:N1}, charW={charWidth:N1}, charsPerLine={charsPerLine}), textBounds=({textLeft:N1},{textRight:N1}), {sectionDetails}, " +
                $"rect={FormatRect(startRect)}, endRect={FormatRect(endRect)}, rawPoint={FormatPoint(rawPoint)}, estimatedPoint={estimatedText}, finalPoint={FormatPoint(finalPoint)}, " +
                $"actualRunTop={actualRunTop:N1}, overlayTop={overlayTop:N1}, marker=(left={markerLeft:N1}, visibleW={visibleMarkerWidth:N1}, markerW={markerWidth:N1}, markerH={markerHeight:N1}), band=(left={bandLeft:N1}, width={bandWidth:N1}), " +
                $"flags=(usedEndRect={usedEndRect}, usedWrappedPointEstimate={usedWrappedPointEstimate}, usedWrappedEstimate={usedWrappedEstimate}, centeredActual={centeredFromActualRun}, centerAccepted={actualCenterAccepted}), " +
                $"estimateReason='{estimateReason}', wrapDetails='{wrappedMarkerDetails}', markerRects={markerRectsText}");
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("MatchNav", $"WrapOverlayDiag failed stage={stage}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string FormatPoint(Windows.Foundation.Point point)
        => $"({point.X:N1},{point.Y:N1})";

    private static string FormatRect(Windows.Foundation.Rect rect)
        => $"({rect.X:N1},{rect.Y:N1},{rect.Width:N1},{rect.Height:N1})";

    private static string FormatMarkerRects(IReadOnlyList<Windows.Foundation.Rect>? rects)
    {
        if (rects is null || rects.Count == 0)
            return "none";

        int count = Math.Min(rects.Count, 4);
        var parts = new string[count];
        for (int index = 0; index < count; index++)
            parts[index] = FormatRect(rects[index]);

        string suffix = rects.Count > count ? $" +{rects.Count - count} more" : string.Empty;
        return string.Join(";", parts) + suffix;
    }

    private static double ClampOverlayMarkerLeft(double left, double markerWidth, double viewportWidth)
    {
        if (double.IsNaN(left) || double.IsInfinity(left))
            return 0;

        double maxLeft = Math.Max(0, viewportWidth - Math.Max(1, markerWidth));
        return Math.Clamp(left, 0, maxLeft);
    }

    private void HideActiveMatchOverlay()
    {
        ClearActiveMatchExtraWordMarkers();
        ActiveMatchOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Post-scroll diagnostic: after layout settles, log where the boxed (active) match
    /// actually rendered relative to the preview viewport. Helps diagnose cases where the
    /// nav advances but the highlighted match is off-screen or hidden.
    /// </summary>
    private void VerifyActiveMatchVisibleAfterScroll(RichTextBlock block, Paragraph targetPara, int paragraphIndex, int correctionAttempt = 0, double previousParaAbsY = double.NaN)
    {
        int navIdx = _currentMatchIndex;
        // Use Normal priority so rapid Next/Prev clicks don't starve the
        // corrective scroll — Low priority let the verify queue grow without
        // running until the user stopped clicking, leaving the highlighted
        // match off-screen for hundreds of ms.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
        {
            try
            {
                if (_activeMatchHighlight is not { para: var activePara, run: var activeRun, column: var column })
                {
                    LogService.Instance.Info("MatchNav", $"VerifyActiveMatch: idx={navIdx} -- NO active highlight set");
                    return;
                }

                // Bail out if the user has already navigated to a different match
                // since this verification was enqueued — avoids expensive work on
                // stale requests (especially the UpdateLayout fallback).
                if (_currentMatchIndex != navIdx)
                {
                    LogService.Instance.Verbose("MatchNav", $"VerifyActiveMatch: stale (current={_currentMatchIndex}, enqueued={navIdx}), skipping");
                    return;
                }

                bool paraMatches = ReferenceEquals(activePara, targetPara);
                int activeIdx = navIdx;

                double vpH = PreviewScrollViewer.ViewportHeight;
                double vpTop = PreviewScrollViewer.VerticalOffset;
                double vpBottom = vpTop + vpH;
                double viewportPaddingTop = PreviewScrollViewer.Padding.Top;

                double paraY = double.NaN;
                try
                {
                    var t = block.TransformToVisual(PreviewScrollViewer);
                    var p = t.TransformPoint(new Windows.Foundation.Point(0, 0));
                    paraY = vpTop + p.Y - viewportPaddingTop;
                }
                catch { }

                double runY = double.NaN;
                double runH = double.NaN;
                try
                {
                    var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    var t = block.TransformToVisual(PreviewScrollViewer);
                    var p = t.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                    runY = vpTop + p.Y - viewportPaddingTop;
                    runH = rect.Height;
                }
                catch { }

                bool runOnScreen = !double.IsNaN(runY) && runY >= vpTop && (runY + (double.IsNaN(runH) ? 0 : runH)) <= vpBottom;
                string runText = activeRun.Text ?? "";
                if (runText.Length > 30) runText = string.Concat(runText.AsSpan(0, 30), "…");

                LogService.Instance.Info("MatchNav",
                    $"VerifyActiveMatch: idx={navIdx}, activeIdx={activeIdx}, paraMatches={paraMatches}, paraIdx={paragraphIndex}, " +
                    $"col={column}, runText='{runText}', runFG={(activeRun.Foreground is SolidColorBrush sb ? sb.Color.ToString() : "?")}, " +
                    $"vpTop={vpTop:N1}, vpBottom={vpBottom:N1}, vpH={vpH:N1}, paraAbsY={paraY:N1}, runAbsY={runY:N1}, runH={runH:N1}, runOnScreen={runOnScreen}");

                // Self-correcting: if the run isn't centered/visible but we now have an
                // accurate rect, perform a corrective scroll. Allow extra attempts when
                // the layout is still shifting under us (paraAbsY changed since the
                // previous attempt) — those don't count against the cap because the
                // miss was caused by a moving target, not by a bad scroll computation.
                const int kHardCap = 6;
                bool layoutMoved = !double.IsNaN(previousParaAbsY) && Math.Abs(paraY - previousParaAbsY) > 2.0;
                int nextAttempt = layoutMoved ? correctionAttempt : correctionAttempt + 1;

                // If the run hasn't been measured yet (runH == 0 or NaN), the layout
                // pass hasn't reached this paragraph. Force an explicit UpdateLayout
                // and re-enqueue verification so we try again after layout has had
                // another tick. Without this we silently leave the user looking at
                // the wrong content — and the only way to recover used to be moving
                // the mouse over the preview panel (which forces realization).
                if ((double.IsNaN(runH) || runH <= 0) && nextAttempt <= kHardCap)
                {
                    bool layoutForced = false;
                    string layoutEx = "";
                    try
                    {
                        block.UpdateLayout();
                        PreviewScrollViewer.UpdateLayout();
                        layoutForced = true;
                    }
                    catch (Exception ex) { layoutEx = ex.GetType().Name; }
                    LogService.Instance.Info("MatchNav",
                        $"VerifyActiveMatch: run not yet measured (runH={runH:N1}), forcedLayout={layoutForced}{(layoutEx.Length > 0 ? $", layoutEx={layoutEx}" : "")}, re-enqueue verify idx={navIdx}, attempt={nextAttempt}/{kHardCap}");
                    VerifyActiveMatchVisibleAfterScroll(block, targetPara, paragraphIndex, nextAttempt, paraY);
                    return;
                }

                if (!runOnScreen && !double.IsNaN(runY) && !double.IsNaN(runH) && runH > 0 && nextAttempt <= kHardCap)
                {
                    double effectiveH = runH > 0 ? runH : EstimatePreviewLineHeight(block);
                    double correctedTarget = runY - vpH / 2 + effectiveH / 2;
                    correctedTarget = Math.Clamp(correctedTarget, 0, PreviewScrollViewer.ScrollableHeight);
                    if (Math.Abs(correctedTarget - vpTop) > 1)
                    {
                        // Wait for the ChangeView to actually settle before re-verifying.
                        EventHandler<ScrollViewerViewChangedEventArgs>? handler = null;
                        double capturedParaY = paraY;
                        handler = (_, ev) =>
                        {
                            if (ev.IsIntermediate) return;
                            PreviewScrollViewer.ViewChanged -= handler;
                            VerifyActiveMatchVisibleAfterScroll(block, targetPara, paragraphIndex, nextAttempt, capturedParaY);
                        };
                        PreviewScrollViewer.ViewChanged += handler;
                        bool accepted = PreviewScrollViewer.ChangeView(null, correctedTarget, null, disableAnimation: true);
                        LogService.Instance.Info("MatchNav",
                            $"VerifyActiveMatch: corrective scroll idx={navIdx}, attempt={nextAttempt}/{kHardCap}, layoutMoved={layoutMoved}, fromY={vpTop:N1}, toY={correctedTarget:N1}, accepted={accepted}");
                        if (!accepted)
                        {
                            // ChangeView rejected the request — no ViewChanged will fire,
                            // so detach the handler to avoid leaking.
                            PreviewScrollViewer.ViewChanged -= handler;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Instance.Info("MatchNav", $"VerifyActiveMatch: exception {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private static double EstimatePreviewLineHeight(RichTextBlock block) => Math.Max(16d, block.FontSize * 1.35d);

    private static double EstimatePreviewCharWidth(RichTextBlock block) => Math.Max(6d, block.FontSize * 0.58d);

    private double GetPreviewCharWidth(RichTextBlock block, Paragraph? paragraph = null)
    {
        if (paragraph is not null)
        {
            foreach (var run in paragraph.Inlines.OfType<Run>())
            {
                if (string.IsNullOrEmpty(run.Text))
                    continue;

                try
                {
                    var start = run.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    var nextPosition = run.ContentStart.GetPositionAtOffset(1, Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    if (nextPosition is null)
                        continue;

                    var next = nextPosition.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
                    double width = next.X - start.X;
                    if (width > 1 && Math.Abs(next.Y - start.Y) < Math.Max(1, start.Height * 0.25))
                        return width;
                }
                catch { }
            }
        }

        return EstimatePreviewCharWidth(block);
    }

    private double GetPreviewWrapTextWidth(RichTextBlock block)
    {
        if (_sectionMatchNavs.TryGetValue(block, out var sectionNav) && sectionNav.Scroller.ViewportWidth > 0)
            return sectionNav.Scroller.ViewportWidth;

        if (block.ActualWidth > 0)
            return block.ActualWidth;

        return Math.Max(1, PreviewScrollViewer.ViewportWidth);
    }

    private int GetPreviewWrappedCharsPerLine(RichTextBlock block, Paragraph? paragraph = null, double? measuredCharWidth = null)
    {
        double charWidth = measuredCharWidth.GetValueOrDefault();
        if (charWidth <= 0)
            charWidth = GetPreviewCharWidth(block, paragraph);

        return Math.Max(1, (int)Math.Floor(GetPreviewWrapTextWidth(block) / charWidth));
    }

    private double EstimateWrappedLineOffset(RichTextBlock block, Paragraph targetPara)
    {
        if (!ViewModel.PreviewWordWrap)
            return 0;

        if (_activeMatchHighlight is not { para: var activePara, column: var column }
            || !ReferenceEquals(activePara, targetPara))
            return 0;

        double charWidth = GetPreviewCharWidth(block, targetPara);
        double availableWidth = GetPreviewWrapTextWidth(block);
        int charsPerWrappedLine = GetPreviewWrappedCharsPerLine(block, targetPara, charWidth);
        double wrappedOffset = column / (double)charsPerWrappedLine;
        LogService.Instance.Verbose("Preview", $"EstimateWrappedLineOffset: idx={_currentMatchIndex}, column={column}, availableW={availableWidth:N1}, charW={charWidth:N1}, charsPerLine={charsPerWrappedLine}, wrappedOffset={wrappedOffset:N1}");
        return wrappedOffset;
    }

    private double EstimateCumulativeHeightBefore(RichTextBlock block, Paragraph targetPara, double lineHeight)
    {
        int paraIdx = GetParagraphIndex(block, targetPara);
        return paraIdx >= 0 ? GetCumulativeHeightBefore(block, paraIdx, lineHeight) : 0;
    }

    private int GetParagraphIndex(RichTextBlock block, Paragraph targetPara)
    {
        var metrics = GetParagraphMetrics(block);
        return metrics.IndexByParagraph.TryGetValue(targetPara, out int result) ? result : -1;
    }

    private ParagraphMetrics GetParagraphMetrics(RichTextBlock block)
    {
        if (_paragraphMetricsCache.TryGetValue(block, out var metrics))
            return metrics;

        var map = new Dictionary<Paragraph, int>(block.Blocks.Count);
        for (int idx = 0; idx < block.Blocks.Count; idx++)
        {
            if (block.Blocks[idx] is Paragraph p)
                map[p] = idx;
        }

        metrics = new ParagraphMetrics { IndexByParagraph = map };
        _paragraphMetricsCache[block] = metrics;
        return metrics;
    }

    private double GetCumulativeHeightBefore(RichTextBlock block, int blockIndex, double lineHeight)
    {
        if (blockIndex < 0) return 0;
        if (!ViewModel.PreviewWordWrap)
            return blockIndex * lineHeight;

        int charsPerLine = GetPreviewWrappedCharsPerLine(block);
        var metrics = GetParagraphMetrics(block);
        if (metrics.PrefixHeights is not null
            && metrics.PrefixHeights.Length == block.Blocks.Count + 1
            && metrics.PrefixCharsPerLine == charsPerLine
            && Math.Abs(metrics.PrefixLineHeight - lineHeight) < 0.01)
        {
            return metrics.PrefixHeights[Math.Min(blockIndex, metrics.PrefixHeights.Length - 1)];
        }

        var prefix = new double[block.Blocks.Count + 1];
        for (int i = 0; i < block.Blocks.Count; i++)
        {
            double height = lineHeight;
            if (block.Blocks[i] is Paragraph p)
            {
                int textLen = 0;
                foreach (var inline in p.Inlines)
                {
                    if (inline is Run r)
                        textLen += r.Text?.Length ?? 0;
                }
                int wrappedLines = Math.Max(1, (int)Math.Ceiling((double)Math.Max(1, textLen) / charsPerLine));
                height = wrappedLines * lineHeight;
            }
            prefix[i + 1] = prefix[i] + height;
        }
        metrics.PrefixHeights = prefix;
        metrics.PrefixCharsPerLine = charsPerLine;
        metrics.PrefixLineHeight = lineHeight;
        return prefix[Math.Min(blockIndex, prefix.Length - 1)];
    }

    private void InvalidateParagraphIndexCache(RichTextBlock? block = null)
    {
        if (block is not null)
            _paragraphMetricsCache.Remove(block);
        else
            _paragraphMetricsCache.Clear();
        _paragraphMatchRunCache.Clear();
        InvalidateScrollPositionCache();
    }

    private double GetBlockAbsoluteTop(RichTextBlock block)
    {
        if (_blockAbsoluteTopCache.TryGetValue(block, out double top))
            return top;

        var verticalTransform = block.TransformToVisual(PreviewScrollViewer);
        var verticalPoint = verticalTransform.TransformPoint(new Windows.Foundation.Point(0, 0));
        top = PreviewScrollViewer.VerticalOffset + verticalPoint.Y;
        _blockAbsoluteTopCache[block] = top;
        return top;
    }

    private void InvalidateScrollPositionCache(RichTextBlock? block = null)
    {
        if (block is not null)
            _blockAbsoluteTopCache.Remove(block);
        else
            _blockAbsoluteTopCache.Clear();
    }

    private double GetPreviewTextViewportWidth(RichTextBlock block)
    {
        if (_sectionMatchNavs.TryGetValue(block, out var sectionNav) && sectionNav.Scroller.ViewportWidth > 0)
            return sectionNav.Scroller.ViewportWidth;

        return PreviewScrollViewer.ViewportWidth;
    }

    private bool ExpandPreviewSectionForBlock(RichTextBlock block)
    {
        if (_blockExpanderCache.TryGetValue(block, out var cachedExpander))
        {
            if (cachedExpander.IsExpanded)
                return false;

            cachedExpander.IsExpanded = true;
            return true;
        }

        return false;
    }

    private void ScrollMatchHorizontallyIntoView(RichTextBlock block, Paragraph targetPara)
    {
        if (ViewModel.PreviewWordWrap)
        {
            return;
        }

        if (_activeMatchHighlight is not { para: var activePara, run: var activeRun, column: var column }
            || !ReferenceEquals(activePara, targetPara))
        {
            LogService.Instance.Verbose("Preview", "ScrollMatchHorizontallyIntoView: skipped because active match does not match target paragraph");
            return;
        }

        var scroller = _sectionMatchNavs.TryGetValue(block, out var sectionNav)
            ? sectionNav.Scroller
            : PreviewScrollViewer;

        if (scroller.ViewportWidth <= 0 || scroller.ScrollableWidth <= 0)
        {
            LogService.Instance.Verbose("Preview", $"ScrollMatchHorizontallyIntoView: skipped, viewportW={scroller.ViewportWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}");
            return;
        }

        double charWidth = EstimatePreviewCharWidth(block);
        double estimatedMatchStart = 8 + column * charWidth;
        double matchStart = estimatedMatchStart;
        string source = "estimated";
        var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);
        if (IsUsableTextRect(rect))
        {
            try
            {
                var measuredPoint = block.TransformToVisual(scroller)
                    .TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));
                if (!double.IsNaN(measuredPoint.X) && !double.IsInfinity(measuredPoint.X))
                {
                    matchStart = measuredPoint.X + scroller.HorizontalOffset;
                    source = "measured";
                }
            }
            catch { }
        }
        double matchWidth = Math.Max(charWidth, (activeRun.Text?.Length ?? 0) * charWidth);
        double matchEnd = matchStart + matchWidth;
        double viewportLeft = scroller.HorizontalOffset;
        double viewportRight = viewportLeft + scroller.ViewportWidth;
        double guard = Math.Min(96, Math.Max(16, scroller.ViewportWidth * 0.15));
        if (matchStart >= viewportLeft + guard && matchEnd <= viewportRight - guard)
        {
            if (LogService.Instance.IsVerboseEnabled)
                LogService.Instance.Verbose("Preview", $"ScrollMatchHorizontallyIntoView: skipped visible idx={_currentMatchIndex}, column={column}, viewport=({viewportLeft:N1},{viewportRight:N1})");
            return;
        }

        double matchCenter = matchStart + matchWidth / 2;
        double targetHorizontalOffset = matchCenter - scroller.ViewportWidth / 2;
        targetHorizontalOffset = Math.Clamp(targetHorizontalOffset, 0, scroller.ScrollableWidth);
        double beforeHorizontalOffset = scroller.HorizontalOffset;
        if (Math.Abs(targetHorizontalOffset - beforeHorizontalOffset) <= 1)
            return;

        bool horizontalAccepted = scroller.ChangeView(targetHorizontalOffset, null, null, disableAnimation: true);
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("Preview", $"ScrollMatchHorizontallyIntoView: idx={_currentMatchIndex}, column={column}, runLen={activeRun.Text?.Length ?? 0}, source={source}, matchStart={matchStart:N1}, estimateStart={estimatedMatchStart:N1}, charW={charWidth:N1}, beforeX={beforeHorizontalOffset:N1}, targetX={targetHorizontalOffset:N1}, viewportW={scroller.ViewportWidth:N1}, scrollableW={scroller.ScrollableWidth:N1}, accepted={horizontalAccepted}, sectionScroller={_sectionMatchNavs.ContainsKey(block)}");
    }

    private int MatchNavFileCount => _sectionMatchNavs.Count > 0
        ? _sectionMatchNavs.Count
        : _matchParagraphs.Select(m => m.block).Distinct().Count() + _lazySections.Count;

    private void ResetPreviewMatchTotals()
    {
        _previewTotalMatchCount = 0;
        _previewTotalFileCount = 0;
        _sectionTotalMatchCounts.Clear();
    }

    private void SetPreviewMatchTotals(int matches, int files)
    {
        _previewTotalMatchCount = Math.Max(0, matches);
        _previewTotalFileCount = Math.Max(0, files);
    }

    private void AddPreviewMatchTotals(int matches, int files)
    {
        _previewTotalMatchCount += Math.Max(0, matches);
        _previewTotalFileCount += Math.Max(0, files);
    }

    private void SubtractPreviewMatchTotals(int matches, int files)
    {
        _previewTotalMatchCount = Math.Max(0, _previewTotalMatchCount - Math.Max(0, matches));
        _previewTotalFileCount = Math.Max(0, _previewTotalFileCount - Math.Max(0, files));
    }

    private void RegisterSectionMatchTotal(RichTextBlock block, int total)
    {
        _sectionTotalMatchCounts[block] = Math.Max(0, total);
    }

    private int GetRenderedMatchTotal()
    {
        var (_, deferredMatches) = GetDeferredCounts();
        int overflowRemaining = 0;
        foreach (var ov in _sectionOverflow.Values)
            overflowRemaining += ov.RemainingResults.Count;
        return _matchParagraphs.Count + _lazyMatchCount + deferredMatches + overflowRemaining;
    }

    private int GetStableMatchNavTotal()
        => _previewTotalMatchCount > 0 ? _previewTotalMatchCount : GetRenderedMatchTotal();

    private int GetStableMatchNavFileCount(int deferredFiles)
        => _previewTotalFileCount > 0 ? _previewTotalFileCount : MatchNavFileCount + deferredFiles;

    private int GetSectionMatchTotal(SectionMatchNav sectionNav)
    {
        if (_sectionTotalMatchCounts.TryGetValue(sectionNav.Block, out int total))
            return total;

        total = sectionNav.Matches.Count;
        if (_sectionOverflow.TryGetValue(sectionNav.Block, out var ov))
            total += ov.RemainingResults.Count;
        return total;
    }

    /// <summary>
    /// Files and matches not yet inserted into the visual tree (waiting behind
    /// a "Show more" button). Included in the match-nav grand totals so the
    /// user sees the true scope of their result set.
    /// </summary>
    private (int Files, int Matches) GetDeferredCounts()
    {
        var list = _deferredOrderedFiles;
        if (list is null || _deferredCursor >= list.Count) return (0, 0);

        if (ReferenceEquals(list, _cachedDeferredCountsList)
            && _cachedDeferredCountsCursor == _deferredCursor)
        {
            return _cachedDeferredCounts;
        }

        int files = list.Count - _deferredCursor;
        int matches = 0;
        for (int i = _deferredCursor; i < list.Count; i++)
            matches += list[i].Value.Count;
        _cachedDeferredCountsList = list;
        _cachedDeferredCountsCursor = _deferredCursor;
        _cachedDeferredCounts = (files, matches);
        return _cachedDeferredCounts;
    }

    private void InvalidateDeferredCountsCache()
    {
        _cachedDeferredCountsList = null;
        _cachedDeferredCountsCursor = -1;
        _cachedDeferredCounts = default;
    }

    private string FormatMatchNavLabel(int index)
    {
        var (deferredFiles, _) = GetDeferredCounts();
        int totalMatches = GetStableMatchNavTotal();
        int fileCount = GetStableMatchNavFileCount(deferredFiles);
        return $"Match {index + 1} of {totalMatches} (across {fileCount} file{(fileCount != 1 ? "s" : "")})";
    }

    private void UpdateMatchNavPanel()
    {
        int totalMatches = GetStableMatchNavTotal();
        if (totalMatches > 0)
        {
            MatchNavPanel.Visibility = Visibility.Visible;
            bool hadActiveHighlight = _activeMatchHighlight is not null;
            var activeIndex = FindActiveMatchIndex();
            if (activeIndex >= 0)
                _currentMatchIndex = activeIndex;
            else if (_matchParagraphs.Count > 0)
            {
                if (_currentMatchIndex < 0)
                    _currentMatchIndex = 0;
                else if (_currentMatchIndex >= _matchParagraphs.Count)
                    _currentMatchIndex = _matchParagraphs.Count - 1;
            }
            else if (_currentMatchIndex < 0)
            {
                _currentMatchIndex = 0;
            }

            MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);

            // If the label says "Match 1 of N" but nothing is actually boxed/red yet,
            // box the current match and scroll to it.  Without this, the very first
            // match after a preview load shows a count but no visible highlight until
            // the user clicks Next.  Reuse the known-good OnNextMatch code path:
            // setting _currentMatchIndex = -1 makes the next click land on index 0
            // and run the same Box+Scroll flow that works for subsequent navigation.
            if (!_initialMatchScrolled
                && !_suppressInitialMatchAutoScroll
                && !hadActiveHighlight
                && _matchParagraphs.Count > 0)
            {
                _initialMatchScrolled = true;
                // Two chained Low-priority dispatches so this runs AFTER the
                // TryScrollToPreviewSection call that PrependPreviewSectionsForFilesAsync
                // also queues — otherwise that scroll-to-section overrides our
                // scroll-to-first-match and the user lands at the file header
                // instead of the first highlight.
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                    {
                        if (_matchParagraphs.Count == 0) return;
                        if (_activeMatchHighlight is not null
                            && _activeMatchHighlight.Value.para is var ap
                            && ReferenceEquals(ap, _matchParagraphs[0].para))
                        {
                            // Already on first match — just make sure it's visible.
                            ScrollPreviewToLine(_matchParagraphs[0].block, _matchParagraphs[0].para);
                            return;
                        }
                        _currentMatchIndex = -1;
                        _ = GoToNextMatchAsync();
                    });
                });
            }
        }
        else
        {
            HideMatchNavPanel();
        }
    }

    private int FindActiveMatchIndex()
    {
        if (_activeMatchHighlight is not { para: var activePara, matchInPara: var activeMatchInPara })
            return -1;

        for (int i = 0; i < _matchParagraphs.Count; i++)
        {
            if (ReferenceEquals(_matchParagraphs[i].para, activePara)
                && _matchParagraphs[i].matchInPara == activeMatchInPara)
                return i;
        }

        return -1;
    }

    private void SetCurrentMatchToParagraph(RichTextBlock block, Paragraph para)
        => SetCurrentMatchToMatch(block, para, matchInPara: null);

    private void SetCurrentMatchToMatch(RichTextBlock block, Paragraph para, int? matchInPara)
    {
        for (int i = 0; i < _matchParagraphs.Count; i++)
        {
            var match = _matchParagraphs[i];
            if (ReferenceEquals(match.block, block)
                && ReferenceEquals(match.para, para)
                && (matchInPara is null || match.matchInPara == matchInPara.Value))
            {
                _currentMatchIndex = i;
                MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
                if (_sectionMatchNavs.TryGetValue(block, out var sn))
                    SetSectionCurrentMatch(sn, para, match.matchInPara);
                ActivateSectionForBlock(block);
                BoxMatchRun(para, match.matchInPara);
                return;
            }
        }

        ActivateSectionForBlock(block);
    }

    private static void SetSectionCurrentMatch(SectionMatchNav sn, Paragraph para, int matchInPara)
    {
        // O(1) lookup once the cache is built. The cache is invalidated by
        // sites that mutate sn.Matches (see InsertSectionMatches / lazy materialization).
        var cache = sn.IndexByMatch;
        if (cache is null || cache.Count != sn.Matches.Count)
        {
            cache = new Dictionary<(Paragraph, int), int>(sn.Matches.Count);
            for (int i = 0; i < sn.Matches.Count; i++)
                cache[sn.Matches[i]] = i;
            sn.IndexByMatch = cache;
        }
        if (cache.TryGetValue((para, matchInPara), out int idx))
            sn.CurrentIndex = idx;
    }

    private void HideMatchNavPanel()
    {
        UnboxCurrentMatch();
        HideActiveMatchOverlay();
        MatchNavPanel.Visibility = Visibility.Collapsed;
        SectionNavOverlay.Visibility = Visibility.Collapsed;
        _matchParagraphs.Clear();
        InvalidateParagraphIndexCache();
        _currentMatchIndex = -1;
        _sectionMatchNavs.Clear();
        _activeSectionNav = null;
        _lazySections.Clear();
        _lazyMatchCount = 0;
        ResetPreviewMatchTotals();
        _sectionOverflow.Clear();
        _deferredOrderedFiles = null;
        _deferredAllSelected = null;
        _deferredButtonPanel = null;
        _deferredCursor = 0;
        InvalidateDeferredCountsCache();
        _autoLoadMoreInFlight = false;
        _initialMatchScrolled = false;
        _expanderFilePaths.Clear();
        _expanderHeaderArgs.Clear();
        _blockExpanderCache.Clear();
        InvalidateScrollPositionCache();
        _stickyHeaderExpander = null;
        StickyFileHeader.Child = null;
        StickyFileHeader.Visibility = Visibility.Collapsed;
    }

    private void UpdateSectionMatchNavPanels()
    {
        // Initialize or clamp match indices for all sections without resetting
        // the user's current position during background section loading.
        foreach (var sn in _sectionMatchNavs.Values)
        {
            if (sn.Matches.Count == 0)
                sn.CurrentIndex = -1;
            else if (sn.CurrentIndex < 0)
                sn.CurrentIndex = 0;
            else if (sn.CurrentIndex >= sn.Matches.Count)
                sn.CurrentIndex = sn.Matches.Count - 1;
        }

        // Only auto-activate per-file section nav when there's exactly one file section.
        // For multi-file views, the user must click/expand a section to activate its nav.
        if (_sectionMatchNavs.Count == 1)
        {
            var sn = _sectionMatchNavs.Values.First();
            if (GetSectionMatchTotal(sn) > 1)
                _activeSectionNav = sn;
            else
                _activeSectionNav = null;
        }
        else
        {
            _activeSectionNav = null;
        }

        HighlightActiveExpander();
        UpdateSectionNavOverlay();
    }

    private void ActivateSectionForBlock(RichTextBlock block)
    {
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
        {
            _activeSectionNav = sn;
            HighlightActiveExpander();
            UpdateSectionNavOverlay();
            UpdateStickyHorizontalScrollBar();
        }
    }

    private RichTextBlock? _lastHighlightedActiveBlock;

    private void HighlightActiveExpander()
    {
        var activeBlock = _activeSectionNav?.Block;
        // Avoid the per-click loop when the active section hasn't changed.
        if (ReferenceEquals(activeBlock, _lastHighlightedActiveBlock))
            return;
        _lastHighlightedActiveBlock = activeBlock;
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            bool isActive = child.Tag is RichTextBlock b && b == activeBlock;
            child.Background = null;
            ApplyPreviewSectionContentBackground(child, isActive);
        }
    }

    private void ApplyPreviewSectionBackgrounds()
    {
        var activeBlock = _activeSectionNav?.Block;
        foreach (var child in PreviewSectionsPanel.Children.OfType<Expander>())
        {
            bool isActive = child.Tag is RichTextBlock block && block == activeBlock;
            child.Background = null;
            ApplyPreviewSectionContentBackground(child, isActive);
        }
    }

    private void ApplyPreviewSectionContentBackground(Expander expander, bool isActive)
    {
        var brush = CreatePreviewSectionContentBackgroundBrush(isActive);
        if (expander.Content is Grid grid)
        {
            grid.Background = brush;
            foreach (var border in grid.Children.OfType<Border>())
                border.Background = brush;
            foreach (var scroller in grid.Children.OfType<ScrollViewer>())
            {
                scroller.Background = brush;
                if (scroller.Content is Border contentBorder)
                    contentBorder.Background = brush;
            }
        }
        else if (expander.Content is ScrollViewer scroller)
        {
            scroller.Background = brush;
            if (scroller.Content is Border contentBorder)
                contentBorder.Background = brush;
        }
        else if (expander.Content is Border border)
        {
            border.Background = brush;
        }
    }

    private SolidColorBrush CreatePreviewSectionContentBackgroundBrush(bool isActive)
    {
        string value = isActive
            ? ViewModel.SelectedPreviewContentBackgroundColor
            : ViewModel.UnselectedPreviewContentBackgroundColor;
        var fallback = isActive
            ? s_defaultSelectedPreviewContentBackground
            : s_defaultUnselectedPreviewContentBackground;
        return new SolidColorBrush(ColorStringHelper.Parse(value, fallback));
    }

    private void UpdateSectionNavOverlay()
    {
        if (_activeSectionNav is null)
        {
            SectionNavOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        int total = GetSectionMatchTotal(_activeSectionNav);
        if (total <= 1)
        {
            SectionNavOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        SectionNavOverlay.Visibility = Visibility.Visible;
        SectionNavLabel.Text = $"Match {_activeSectionNav.CurrentIndex + 1} of {total}";
    }

    private void OnSectionNavNext(object sender, RoutedEventArgs e)
    {
        if (_activeSectionNav is null || _activeSectionNav.Matches.Count == 0) return;
        OnSectionNextMatch(_activeSectionNav);
    }

    private void OnSectionNavPrev(object sender, RoutedEventArgs e)
    {
        if (_activeSectionNav is null || _activeSectionNav.Matches.Count == 0) return;
        OnSectionPrevMatch(_activeSectionNav);
    }

    private void OnSectionNavDismiss(object sender, RoutedEventArgs e)
    {
        SectionNavOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnSectionNavPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SectionNavOverlay.Opacity = 1.0;
    }

    private void OnSectionNavPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SectionNavOverlay.Opacity = 0.75;
    }

    private void OnSectionNextMatch(SectionMatchNav sn)
    {
        if (sn.Matches.Count == 0) return;
        Paragraph? previousPara = sn.CurrentIndex >= 0 && sn.CurrentIndex < sn.Matches.Count ? sn.Matches[sn.CurrentIndex].para : null;
        bool wrappedToStart = false;
        bool expandedOverflow = false;
        int nextIndex = sn.CurrentIndex + 1;
        if (nextIndex >= sn.Matches.Count)
        {
            if (_sectionOverflow.ContainsKey(sn.Block) && ExpandSectionNextChunk(sn.Block))
            {
                nextIndex = sn.CurrentIndex + 1;
                expandedOverflow = true;
            }
            else
            {
                nextIndex = 0;
                wrappedToStart = true;
            }
        }
        sn.CurrentIndex = Math.Clamp(nextIndex, 0, sn.Matches.Count - 1);
        _activeSectionNav = sn;
        HighlightActiveExpander();
        UpdateSectionNavOverlay();
        var (para, matchInPara) = sn.Matches[sn.CurrentIndex];
        BoxMatchRun(para, matchInPara);
        ScrollAfterMatchNavigation(sn.Block, para, justMaterialized: false, sameParagraph: !expandedOverflow && !wrappedToStart && ReferenceEquals(previousPara, para));
    }

    private void OnSectionPrevMatch(SectionMatchNav sn)
    {
        if (sn.Matches.Count == 0) return;
        Paragraph? previousPara = sn.CurrentIndex >= 0 && sn.CurrentIndex < sn.Matches.Count ? sn.Matches[sn.CurrentIndex].para : null;
        bool wrappedToEnd = sn.CurrentIndex <= 0;
        sn.CurrentIndex = (sn.CurrentIndex - 1 + sn.Matches.Count) % sn.Matches.Count;
        _activeSectionNav = sn;
        HighlightActiveExpander();
        UpdateSectionNavOverlay();
        var (para, matchInPara) = sn.Matches[sn.CurrentIndex];
        BoxMatchRun(para, matchInPara);
        ScrollAfterMatchNavigation(sn.Block, para, justMaterialized: false, sameParagraph: !wrappedToEnd && ReferenceEquals(previousPara, para));
    }

    private void ScrollAfterMatchNavigation(RichTextBlock block, Paragraph para, bool justMaterialized, bool sameParagraph)
    {
        if (justMaterialized)
        {
            ScrollAfterMaterialization(block, para);
            return;
        }

        if (sameParagraph)
        {
            if (ViewModel.PreviewWordWrap)
            {
                ScrollPreviewToLine(block, para, forceCenter: true);
            }
            else
            {
                ScrollMatchHorizontallyIntoView(block, para);
                QueueActiveMatchOverlayUpdate(block, para);
                QueueActiveMatchOverlayUpdate(block, para);
            }
            return;
        }

        ScrollPreviewToLine(block, para, forceCenter: true);
    }

    /// <summary>
    /// Shows a flyout on <paramref name="anchor"/> asking the user to pick a
    /// bulk match-navigation step size.  Once chosen, invokes
    /// <paramref name="navigate"/> with the step.
    /// </summary>
    private void ShowBulkMatchStepFlyout(FrameworkElement anchor, Action<int> navigate)
    {
        var sp = new StackPanel { Spacing = 6, MinWidth = 220 };

        sp.Children.Add(new TextBlock
        {
            Text = "Jump how many matches at a time?",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        var radioPanel = new StackPanel { Spacing = 2 };
        RadioButton? selected = null;
        foreach (int v in new[] { 10, 50, 100, 1000 })
        {
            var rb = new RadioButton { Content = v.ToString(), Tag = v, GroupName = "BulkStep" };
            if (v == 10) { rb.IsChecked = true; selected = rb; }
            rb.Checked += (s, _) => selected = s as RadioButton;
            radioPanel.Children.Add(rb);
        }

        var customPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var customRadio = new RadioButton { Content = "Other:", GroupName = "BulkStep" };
        var customBox = new NumberBox { Minimum = 2, Maximum = 100_000, Value = 25, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 100 };
        customRadio.Checked += (s, _) => { selected = s as RadioButton; customBox.Focus(FocusState.Programmatic); };
        customPanel.Children.Add(customRadio);
        customPanel.Children.Add(customBox);
        radioPanel.Children.Add(customPanel);
        sp.Children.Add(radioPanel);

        var saveCheck = new CheckBox { Content = "Remember for this session (skip this dialog on future Ctrl+clicks)" };
        sp.Children.Add(saveCheck);

        var okBtn = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        if (TryGetApplicationStyle("AccentButtonStyle") is { } okButtonStyle)
            okBtn.Style = okButtonStyle;

        var flyout = new Flyout { Content = sp };
        okBtn.Click += (_, _) =>
        {
            int step;
            if (selected is not null && selected.Tag is int tagVal)
                step = tagVal;
            else
                step = double.IsNaN(customBox.Value) ? 10 : (int)customBox.Value;
            step = Math.Max(2, step);

            _bulkMatchStep = step;
            _bulkMatchStepLocked = saveCheck.IsChecked == true;

            flyout.Hide();
            navigate(step);
        };
        sp.Children.Add(okBtn);

        flyout.ShowAt(anchor);
    }

    /// <summary>
    /// Navigate forward by <paramref name="step"/> matches from the current
    /// position.  If the target index exceeds the rendered match count, lazy
    /// sections and overflow chunks are materialized until enough matches are
    /// available or no more remain.
    /// </summary>
    private async void BulkNextMatch(int step)
    {
        if (_matchParagraphs.Count == 0 && _lazyMatchCount == 0) return;

        var navSw = System.Diagnostics.Stopwatch.StartNew();
        int startIndex = _currentMatchIndex;
        int renderedBefore = _matchParagraphs.Count;
        int expandedChunks = 0;
        int materializedSections = 0;
        bool materializedDuringBulk = false;
        int targetIndex = _currentMatchIndex + step;

        // Expand overflow / materialize lazy sections until we have enough matches.
        while (targetIndex >= _matchParagraphs.Count && (_lazyMatchCount > 0 || _sectionOverflow.Count > 0))
        {
            bool expanded = false;

            // First try expanding overflow in the current section.
            if (_currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count)
            {
                var curBlock = _matchParagraphs[_currentMatchIndex].block;
                if (_sectionOverflow.ContainsKey(curBlock))
                    expanded = ExpandSectionNextChunk(curBlock);
                if (expanded)
                    expandedChunks++;
            }

            // Then try materializing the next lazy section.
            if (!expanded && _lazyMatchCount > 0)
            {
                expanded = await MaterializeNextLazySectionAsync(forward: true);
                if (expanded)
                {
                    materializedSections++;
                    materializedDuringBulk = true;
                }
            }

            if (!expanded) break;
        }

        if (targetIndex >= _matchParagraphs.Count)
            targetIndex = _matchParagraphs.Count - 1;

        if (targetIndex < 0 || _matchParagraphs.Count == 0) return;

        _currentMatchIndex = targetIndex;
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
            SetSectionCurrentMatch(sn, para, matchInPara);
        ActivateSectionForBlock(block);
        BoxMatchRun(para, matchInPara);
        ScrollAfterMatchNavigation(block, para, justMaterialized: materializedDuringBulk, sameParagraph: false);
        navSw.Stop();
        LogService.Instance.Info("MatchNav", $"BulkNextMatch: step={step}, from={startIndex}, landed={_currentMatchIndex}, renderedBefore={renderedBefore}, renderedAfter={_matchParagraphs.Count}, expandedChunks={expandedChunks}, materializedSections={materializedSections}, elapsed={navSw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// Navigate backward by <paramref name="step"/> matches.  If fewer than
    /// <paramref name="step"/> matches exist before the current position,
    /// jump to match 0 and box it with a red highlight to signal the boundary.
    /// </summary>
    private void BulkPrevMatch(int step)
    {
        if (_matchParagraphs.Count == 0) return;

        var navSw = System.Diagnostics.Stopwatch.StartNew();
        int startIndex = _currentMatchIndex;
        int targetIndex = _currentMatchIndex - step;
        bool hitBoundary = targetIndex < 0;
        if (hitBoundary)
            targetIndex = 0;

        _currentMatchIndex = targetIndex;
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
            SetSectionCurrentMatch(sn, para, matchInPara);
        ActivateSectionForBlock(block);

        if (hitBoundary)
            BoxMatchRun(para, matchInPara, boundaryFlash: true);
        else
            BoxMatchRun(para, matchInPara);

        ScrollPreviewToLine(block, para, forceCenter: true);
        navSw.Stop();
        LogService.Instance.Info("MatchNav", $"BulkPrevMatch: step={step}, from={startIndex}, landed={_currentMatchIndex}, hitBoundary={hitBoundary}, rendered={_matchParagraphs.Count}, elapsed={navSw.ElapsedMilliseconds}ms");
    }

    private async void OnNextMatch(object sender, RoutedEventArgs e)
    {
        // Ctrl+Click: bulk navigation
        bool isCtrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (isCtrl)
        {
            if (_bulkMatchStepLocked && _bulkMatchStep >= 2)
            {
                BulkNextMatch(_bulkMatchStep);
                return;
            }
            ShowBulkMatchStepFlyout(NextMatchButton, BulkNextMatch);
            return;
        }

        await GoToNextMatchAsync();
    }

    private async Task GoToNextMatchAsync()
    {
        var navSw = System.Diagnostics.Stopwatch.StartNew();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount;
        if (totalMatches == 0) return;
        Paragraph? previousPara = _currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count ? _matchParagraphs[_currentMatchIndex].para : null;
        bool expandedOverflow = false;

        // If the current match is the last one in its section and that section
        // still has overflow (was truncated), expand the next chunk before
        // navigating so the user can progressively reach all matches.
        if (_sectionOverflow.Count > 0
            && _matchParagraphs.Count > 0
            && _currentMatchIndex >= 0
            && _currentMatchIndex < _matchParagraphs.Count)
        {
            var (curBlock, _, _) = _matchParagraphs[_currentMatchIndex];
            bool atSectionEnd = _currentMatchIndex == _matchParagraphs.Count - 1
                || !ReferenceEquals(_matchParagraphs[_currentMatchIndex + 1].block, curBlock);
            if (atSectionEnd && _sectionOverflow.ContainsKey(curBlock))
                expandedOverflow = ExpandSectionNextChunk(curBlock);
        }

        bool justMaterialized = false;
        bool wrappedToStart = false;
        if (_matchParagraphs.Count == 0 || _currentMatchIndex >= _matchParagraphs.Count - 1)
        {
            // Past the last rendered match — try to materialize the next lazy section.
            if (_lazyMatchCount > 0 && await MaterializeNextLazySectionAsync(forward: true))
            {
                // Land on the first match of the newly materialized section.
                _currentMatchIndex = _matchParagraphs.Count - _lazySectionJustAdded;
                UpdateSectionMatchNavPanels();
                justMaterialized = true;
            }
            else
            {
                // Wrap to start.
                _currentMatchIndex = 0;
                wrappedToStart = true;
            }
        }
        else
        {
            _currentMatchIndex++;
        }

        if (_matchParagraphs.Count == 0 || _currentMatchIndex < 0 || _currentMatchIndex >= _matchParagraphs.Count) return;
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
            SetSectionCurrentMatch(sn, para, matchInPara);
        ActivateSectionForBlock(block);
        BoxMatchRun(para, matchInPara);
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"OnNextMatch: idx={_currentMatchIndex}, path={(justMaterialized ? "materialize" : "normal")}");
        ScrollAfterMatchNavigation(block, para, justMaterialized, sameParagraph: !expandedOverflow && !wrappedToStart && ReferenceEquals(previousPara, para));
        navSw.Stop();
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("Preview", $"GoToNextMatchAsync: index={_currentMatchIndex}, elapsed={navSw.ElapsedMilliseconds}ms");
    }

    private async void OnPrevMatch(object sender, RoutedEventArgs e)
    {
        // Ctrl+Click: bulk navigation
        bool isCtrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (isCtrl)
        {
            if (_bulkMatchStepLocked && _bulkMatchStep >= 2)
            {
                BulkPrevMatch(_bulkMatchStep);
                return;
            }
            ShowBulkMatchStepFlyout(PrevMatchButton, BulkPrevMatch);
            return;
        }

        var navSw = System.Diagnostics.Stopwatch.StartNew();
        int totalMatches = _matchParagraphs.Count + _lazyMatchCount;
        if (totalMatches == 0) return;
        Paragraph? previousPara = _currentMatchIndex >= 0 && _currentMatchIndex < _matchParagraphs.Count ? _matchParagraphs[_currentMatchIndex].para : null;

        bool justMaterialized = false;
        bool wrappedToEnd = false;
        if (_matchParagraphs.Count == 0 || _currentMatchIndex <= 0)
        {
            // Before the first rendered match — try to materialize the last lazy section.
            if (_lazyMatchCount > 0 && await MaterializeNextLazySectionAsync(forward: false))
            {
                // Land on the last match of the newly materialized section.
                _currentMatchIndex = _matchParagraphs.Count - 1;
                UpdateSectionMatchNavPanels();
                justMaterialized = true;
            }
            else
            {
                // Wrap to end.
                _currentMatchIndex = _matchParagraphs.Count - 1;
                wrappedToEnd = true;
            }
        }
        else
        {
            _currentMatchIndex--;
        }

        if (_matchParagraphs.Count == 0 || _currentMatchIndex < 0 || _currentMatchIndex >= _matchParagraphs.Count) return;
        var (block, para, matchInPara) = _matchParagraphs[_currentMatchIndex];
        MatchNavLabel.Text = FormatMatchNavLabel(_currentMatchIndex);
        if (_sectionMatchNavs.TryGetValue(block, out var sn))
            SetSectionCurrentMatch(sn, para, matchInPara);
        ActivateSectionForBlock(block);
        BoxMatchRun(para, matchInPara);
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("MatchNav", $"OnPrevMatch: idx={_currentMatchIndex}, path={(justMaterialized ? "materialize" : "normal")}");
        ScrollAfterMatchNavigation(block, para, justMaterialized, sameParagraph: !wrappedToEnd && ReferenceEquals(previousPara, para));
        navSw.Stop();
        if (LogService.Instance.IsVerboseEnabled)
            LogService.Instance.Verbose("Preview", $"OnPrevMatch: index={_currentMatchIndex}, elapsed={navSw.ElapsedMilliseconds}ms");
    }

    // Tracks how many match entries the last MaterializeNextLazySection call added.
    private int _lazySectionJustAdded;

    /// <summary>
    /// Finds the next (or previous) lazy section in visual order and materializes it.
    /// Skips sections that produce zero match entries. Also expands the Expander.
    /// Returns true if at least one materialized section produced match entries.
    /// </summary>
    private async Task<bool> MaterializeNextLazySectionAsync(bool forward)
    {
        _lazySectionJustAdded = 0;

        // Walk expanders in visual order. Skip already-rendered sections and any
        // newly-materialized section that produces zero match entries.
        var children = PreviewSectionsPanel.Children;
        int start = forward ? 0 : children.Count - 1;
        int end = forward ? children.Count : -1;
        int step = forward ? 1 : -1;

        for (int i = start; i != end; i += step)
        {
            if (children[i] is Expander exp
                && exp.Tag is RichTextBlock b
                && _lazySections.ContainsKey(b))
            {
                int beforeCount = _matchParagraphs.Count;
                await MaterializeLazySectionAsync(b);
                int added = _matchParagraphs.Count - beforeCount;
                exp.IsExpanded = true;
                if (added > 0)
                {
                    _lazySectionJustAdded = added;
                    LogService.Instance.Info("MatchNav", $"MaterializeNextLazySection: forward={forward}, added={added}, expanderIdx={i}, isExpanded={exp.IsExpanded}");
                    return true;
                }
                // Otherwise keep walking — try the next lazy section.
            }
        }
        return false;
    }

    /// <summary>
    /// Expands the next chunk of un-rendered matches for a section that was
    /// truncated at <see cref="MaxMatchesPerSection"/>. Called from match
    /// navigation when the user reaches the end of the rendered range and
    /// the section still has overflow. Returns true if any matches were added.
    /// </summary>
    private bool ExpandSectionNextChunk(RichTextBlock section)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!_sectionOverflow.TryGetValue(section, out var ov)) return false;
        int chunkSize = Math.Min(MaxMatchesPerExpandChunk, ov.RemainingResults.Count);
        if (chunkSize <= 0)
        {
            _sectionOverflow.Remove(section);
            return false;
        }

        // Remove the existing truncation notice (we'll re-append it if more remain).
        if (ov.NoticePara != null)
        {
            section.Blocks.Remove(ov.NoticePara);
            if (_sectionGutterBlocks.TryGetValue(section, out var gb2) && gb2.Blocks.Count > 0)
                gb2.Blocks.RemoveAt(gb2.Blocks.Count - 1);
        }

        // Compute the insertion point in _matchParagraphs (right after this
        // section's last existing match) before appending new entries.
        int insertAt = -1;
        for (int i = _matchParagraphs.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_matchParagraphs[i].block, section))
            {
                insertAt = i + 1;
                break;
            }
        }
        if (insertAt < 0) insertAt = _matchParagraphs.Count;

        _sectionMatchNavs.TryGetValue(section, out var sn);
        int beforeCount = _matchParagraphs.Count;
        bool truncatePreviewLines = ShouldTruncatePreviewLines();

        // Stop early once we've added enough match entries to keep the UI
        // responsive — dense lines can produce 20×+ match entries per result.
        int consumed = 0;
        if (ov.IsHighlightMode && ov.AllLines != null)
        {
            AppendHighlightMatchWindows(
                section,
                ov.RemainingResults,
                ov.AllLines,
                ov.Rx,
                sn,
                ov.PreviewLines,
                MaxMatchEntriesPerExpandChunk,
                chunkSize,
                MaxPreviewBlocksPerExpandChunk,
                out consumed,
                out _,
                out int lastRenderedLine,
                ov.LastRenderedLine,
                truncatePreviewLines);
            ov.LastRenderedLine = Math.Max(ov.LastRenderedLine, lastRenderedLine);

            // Fallback: all remaining results are on already-rendered but truncated
            // lines (e.g. single-line minified JSON with 100K+ matches). Render
            // additional truncation windows centered around each next result so the
            // user can progressively navigate through all matches on the line.
            if (truncatePreviewLines && consumed == 0 && ov.RemainingResults.Count > 0)
            {
                AddGapIndicator(section);
                int entriesBefore = _matchParagraphs.Count;
                int maxResults = Math.Min(chunkSize, ov.RemainingResults.Count);
                for (int ri = 0; ri < maxResults; ri++)
                {
                    if (_matchParagraphs.Count - entriesBefore >= MaxMatchEntriesPerExpandChunk)
                        break;

                    var r = ov.RemainingResults[ri];
                    int lineIndex = r.LineNumber - 1;
                    if (lineIndex < 0 || lineIndex >= ov.AllLines.Length) continue;

                    AddPreviewLineParagraphsAroundResult(
                        section,
                        ov.AllLines[lineIndex],
                        r.LineNumber,
                        r,
                        ov.Rx,
                        _matchParagraphs,
                        sn,
                        out _,
                        out _,
                        truncate: truncatePreviewLines,
                        continuationGutter: true);
                    consumed++;
                }
            }
        }
        else
        {
            var matchLineNums = new HashSet<int>(ov.RemainingResults.Take(chunkSize).Select(r => r.LineNumber));
            int blocksAdded = 0;
            for (int ri = 0; ri < chunkSize; ri++)
            {
                if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                    break;

                var r = ov.RemainingResults[ri];
                var sep = new Paragraph { Margin = new Thickness(0, 8, 0, 4) };
                var label = $"\u00A0Line\u00A0{r.LineNumber}\u00A0";
                var sepRun = new Run { Text = $"{new string('\u2500', 6)}{label}{new string('\u2500', 6)}" };
                sepRun.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 60, 140, 60));
                sep.Inlines.Add(sepRun);
                section.Blocks.Add(sep);
                SyncGutterSpacer(section, sep.Margin);
                blocksAdded++;

                var lines = GetPreviewLines(r, ov.AllLines, ov.PreviewLines, fullFile: false);
                foreach (var (line, lineNum) in lines)
                {
                    if (blocksAdded >= MaxPreviewBlocksPerExpandChunk)
                        break;

                    bool isMatchLine = matchLineNums.Contains(lineNum);
                    AddPreviewLineParagraphs(section, line, lineNum, isMatchLine, r, ov.Rx, truncate: truncatePreviewLines,
                        lineNum == r.LineNumber ? _matchParagraphs : null, sn, out int addedParagraphs);
                    blocksAdded += addedParagraphs;
                }
                consumed++;
                if (_matchParagraphs.Count - beforeCount >= MaxMatchEntriesPerExpandChunk)
                    break;
            }
        }

        ov.RemainingResults.RemoveRange(0, consumed);

        int addedCount = _matchParagraphs.Count - beforeCount;
        // AddPreviewLineParagraphs appends to _matchParagraphs at the end. If
        // there are later sections whose matches come after this section's,
        // move the newly added entries into the correct slot.
        if (addedCount > 0 && insertAt < beforeCount)
        {
            var newEntries = _matchParagraphs.GetRange(beforeCount, addedCount);
            _matchParagraphs.RemoveRange(beforeCount, addedCount);
            _matchParagraphs.InsertRange(insertAt, newEntries);
            // Shift the current cursor if it sat after the insertion point.
            if (_currentMatchIndex >= insertAt)
                _currentMatchIndex += addedCount;
        }

        InvalidateParagraphIndexCache(section);
        if (sn != null) sn.IndexByMatch = null;

        ov.RenderedSoFar += consumed;

        if (ov.RemainingResults.Count > 0)
        {
            ov.NoticePara = AppendTruncationNotice(section, ov.OriginalTotal, ov.RenderedSoFar);
        }
        else
        {
            ov.NoticePara = null;
            _sectionOverflow.Remove(section);
        }

        sw.Stop();
        LogService.Instance.Info("MatchNav", $"ExpandSectionNextChunk: results={consumed}, addedEntries={addedCount}, renderedSoFar={ov.RenderedSoFar}, remaining={ov.RemainingResults.Count}, elapsed={sw.ElapsedMilliseconds}ms");
        return addedCount > 0;
    }
}
