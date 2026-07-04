using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Yagu.Tests;

/// <summary>
/// Source-pin coverage for the preview match-navigation and active-match "red box" overlay
/// subsystem, which lives in <c>MainWindow.MatchNav.cs</c>. That file depends on WinUI 3
/// (RichTextBlock geometry, DispatcherQueue timers, the <c>ActiveMatchBand</c>/<c>ActiveMatchWordMarker</c>
/// overlay elements) and cannot run headless, so its behavior is locked by asserting on the source text:
/// the overlay lifecycle (box/flash/reset/reapply), the match-total arithmetic, and the section
/// match-navigation handlers. These have a long history of subtle regressions (see the preview-editor
/// instruction), so each characteristic statement is pinned so a refactor can't silently change it.
/// </summary>
public sealed class MatchNavOverlayRegressionTests
{
    private static readonly string MatchNavSource =
        ReadSource("Yagu", "UI", "Windows", "MainWindow", "MainWindow.MatchNav.cs");

    // ── Active-match overlay ("red box") lifecycle ───────────────────────────────

    [Fact]
    public void FlashActiveMatchOverlayRed_PaintsBandRedThenRevertsToOverlayColorAfterTimer()
    {
        string flash = Method("FlashActiveMatchOverlayRed", 1600);
        // Flash: the band + word marker + any extra markers go red.
        Assert.Contains("ActiveMatchBand.Background = flashBrush;", flash);
        Assert.Contains("Microsoft.UI.Colors.Red", flash);
        Assert.Contains("ActiveMatchBand.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Red);", flash);
        // A one-shot 600ms timer reverts the band/marker back to the configured overlay color.
        Assert.Contains("TimeSpan.FromMilliseconds(600)", flash);
        Assert.Contains("timer.IsRepeating = false;", flash);
        Assert.Contains("ActiveMatchBand.BorderBrush = new SolidColorBrush(_overlayColor);", flash);
    }

    [Fact]
    public void ResetActiveOverlayLayoutStability_ClearsAllStabilityTrackingState()
    {
        string reset = Method("ResetActiveOverlayLayoutStability", 400);
        Assert.Contains("_activeOverlayStabilityBlock = null;", reset);
        Assert.Contains("_activeOverlayLastBlockTop = double.NaN;", reset);
        Assert.Contains("_activeOverlayStablePasses = 0;", reset);
    }

    [Fact]
    public void RefreshActiveMatchOverlayPosition_HidesOverlayUnlessActualRunCanBeBoxed()
    {
        string refresh = Method("RefreshActiveMatchOverlayPosition", 900);
        // Manual scroll, no active highlight, or a stale paragraph all hide the overlay.
        Assert.Contains("if (IsPreviewManualScrollActive())", refresh);
        Assert.Contains("HideActiveMatchOverlay();", refresh);
        // The overlay is only shown when the actual run rect can be measured.
        Assert.Contains("if (!TryUpdateActiveMatchOverlayFromActualRun(block, para, activeRun))", refresh);
    }

    [Fact]
    public void ReapplyActiveMatchOverlayAfterExpansion_ReResolvesByAbsoluteSourceColumn()
    {
        string reapply = Method("ReapplyActiveMatchOverlayAfterExpansion", 5000);
        // A different paragraph keeps its valid run reference and just repositions.
        Assert.Contains("QueueActiveMatchOverlayRefresh();", reapply);
        // Same paragraph: re-resolve the occurrence by its stable absolute source column.
        Assert.Contains("int activeSourceColumn = oldWindowStart + activeColumn;", reapply);
        Assert.Contains("if (newWindowStart + matches[i].column == activeSourceColumn)", reapply);
        // Missing occurrence drops the overlay rather than boxing the wrong run.
        Assert.Contains("HideActiveMatchOverlay();", reapply);
        Assert.Contains("BoxMatchRun(rebuiltParagraph, newMatchInPara);", reapply);
    }

    [Fact]
    public void IsUsableTextRect_RequiresFiniteOriginAndPositiveHeight()
    {
        string rect = Method("IsUsableTextRect", 200);
        Assert.Contains("!double.IsNaN(rect.X)", rect);
        Assert.Contains("!double.IsNaN(rect.Y)", rect);
        Assert.Contains("rect.Height > 0", rect);
    }

    // ── Preview match-total arithmetic (the nav denominator) ─────────────────────

    [Fact]
    public void SetPreviewMatchTotals_ClampsToZeroAndMirrorsStableTotal()
    {
        string set = Method("SetPreviewMatchTotals", 300);
        Assert.Contains("_previewTotalMatchCount = Math.Max(0, matches);", set);
        Assert.Contains("_previewStableMatchNavTotal = _previewTotalMatchCount;", set);
        Assert.Contains("_previewTotalFileCount = Math.Max(0, files);", set);
    }

    [Fact]
    public void AddPreviewMatchTotals_AddsNonNegativeMatchesToRunningTotals()
    {
        string add = Method("AddPreviewMatchTotals", 300);
        Assert.Contains("int addedMatches = Math.Max(0, matches);", add);
        Assert.Contains("_previewTotalMatchCount += addedMatches;", add);
        Assert.Contains("_previewStableMatchNavTotal += addedMatches;", add);
    }

    [Fact]
    public void SubtractPreviewMatchTotals_ClampsRunningTotalsAtZero()
    {
        string sub = Method("SubtractPreviewMatchTotals", 400);
        Assert.Contains("int removedMatches = Math.Max(0, matches);", sub);
        Assert.Contains("_previewTotalMatchCount = Math.Max(0, _previewTotalMatchCount - removedMatches);", sub);
        Assert.Contains("_previewStableMatchNavTotal = Math.Max(0, _previewStableMatchNavTotal - removedMatches);", sub);
    }

    [Fact]
    public void ResetPreviewMatchTotals_ZeroesEveryTotalAndClearsPerSectionCounts()
    {
        string reset = Method("ResetPreviewMatchTotals", 300);
        Assert.Contains("_previewTotalMatchCount = 0;", reset);
        Assert.Contains("_previewStableMatchNavTotal = 0;", reset);
        Assert.Contains("_previewTotalFileCount = 0;", reset);
        Assert.Contains("_sectionTotalMatchCounts.Clear();", reset);
    }

    [Fact]
    public void GetStableMatchNavFileCount_PrefersRegisteredTotalElseFallsBackToRenderedPlusDeferred()
    {
        string count = Method("GetStableMatchNavFileCount", 200);
        Assert.Contains("_previewTotalFileCount > 0 ? _previewTotalFileCount : MatchNavFileCount + deferredFiles", count);
    }

    // ── Per-section match-navigation overlay handlers ────────────────────────────

    [Fact]
    public void OnSectionNavNext_And_Prev_GuardEmptyActiveSectionAndDelegate()
    {
        string next = Method("OnSectionNavNext", 200);
        Assert.Contains("if (_activeSectionNav is null || _activeSectionNav.Matches.Count == 0) return;", next);
        Assert.Contains("OnSectionNextMatch(_activeSectionNav);", next);

        string prev = Method("OnSectionNavPrev", 200);
        Assert.Contains("if (_activeSectionNav is null || _activeSectionNav.Matches.Count == 0) return;", prev);
        Assert.Contains("OnSectionPrevMatch(_activeSectionNav);", prev);
    }

    [Fact]
    public void SectionNavOverlay_DismissCollapsesAndHoverAdjustsOpacity()
    {
        Assert.Contains("SectionNavOverlay.Visibility = Visibility.Collapsed;", Method("OnSectionNavDismiss", 150));
        Assert.Contains("SectionNavOverlay.Opacity = 1.0;", Method("OnSectionNavPointerEntered", 150));
        Assert.Contains("SectionNavOverlay.Opacity = 0.75;", Method("OnSectionNavPointerExited", 150));
    }

    // ── Geometry estimation (overlay positioning math) ──────────────────────────

    [Fact]
    public void EstimatePreviewCharWidth_UsesFontSizeHeuristicWithFloor()
    {
        Assert.Contains("Math.Max(6d, block.FontSize * 0.58d)", Method("EstimatePreviewCharWidth", 200));
    }

    [Fact]
    public void GetPreviewCharWidth_MeasuresRunElseFallsBackToEstimate()
    {
        string charWidth = Method("GetPreviewCharWidth", 1200);
        // Measured via adjacent character rects, guarded to the same visual row.
        Assert.Contains("double width = next.X - start.X;", charWidth);
        Assert.Contains("Math.Abs(next.Y - start.Y) < Math.Max(1, start.Height * 0.25)", charWidth);
        // Fallback when no run can be measured.
        Assert.Contains("return EstimatePreviewCharWidth(block);", charWidth);
    }

    [Fact]
    public void GetPreviewWrapTextWidth_PrefersSectionScrollerThenBlockThenOuterViewport()
    {
        string width = Method("GetPreviewWrapTextWidth", 500);
        Assert.Contains("sectionNav.Scroller.ViewportWidth > 0", width);
        Assert.Contains("if (block.ActualWidth > 0)", width);
        Assert.Contains("Math.Max(1, PreviewScrollViewer.ViewportWidth)", width);
    }

    [Fact]
    public void EstimateWrappedLineOffset_IsZeroUnlessWrappingActiveMatchParagraph()
    {
        string offset = Method("EstimateWrappedLineOffset", 900);
        Assert.Contains("if (!ViewModel.PreviewWordWrap)", offset);
        Assert.Contains("!ReferenceEquals(activePara, targetPara)", offset);
        Assert.Contains("double wrappedOffset = column / (double)charsPerWrappedLine;", offset);
    }

    [Fact]
    public void EstimateCumulativeHeightBefore_ResolvesParagraphIndexThenSumsHeights()
    {
        Assert.Contains("paraIdx >= 0 ? GetCumulativeHeightBefore(block, paraIdx, lineHeight) : 0",
            Method("EstimateCumulativeHeightBefore", 300));
    }

    [Fact]
    public void GetParagraphIndex_ReturnsMinusOneWhenParagraphNotInMetricsMap()
    {
        Assert.Contains("metrics.IndexByParagraph.TryGetValue(targetPara, out int result) ? result : -1",
            Method("GetParagraphIndex", 300));
    }

    // ── Match-navigation state ───────────────────────────────────────────────────

    [Fact]
    public void FindActiveMatchIndex_MatchesByParagraphReferenceAndOrdinal()
    {
        string find = Method("FindActiveMatchIndex", 600);
        Assert.Contains("ReferenceEquals(_matchParagraphs[i].para, activePara)", find);
        Assert.Contains("_matchParagraphs[i].matchInPara == activeMatchInPara", find);
    }

    [Fact]
    public void SetCurrentMatchToParagraph_DelegatesWithNullOrdinal()
    {
        Assert.Contains("=> SetCurrentMatchToMatch(block, para, matchInPara: null);",
            Method("SetCurrentMatchToParagraph", 150));
    }

    [Fact]
    public void EnsureNavEntryForParagraphMatch_GuardsRunRangeAndInsertsInReadingOrder()
    {
        string ensure = Method("EnsureNavEntryForParagraphMatch", 1800);
        // Never register past the visible bold runs.
        Assert.Contains("if (matchInPara >= visualRuns)", ensure);
        // Already-navigable occurrence is a no-op.
        Assert.Contains("return; // already navigable", ensure);
        // Inserted in reading order (immediately after the lower-ordinal sibling).
        Assert.Contains("_matchParagraphs.Insert(insertAt, (section, para, matchInPara));", ensure);
    }

    [Fact]
    public void SetSectionCurrentMatch_BuildsO1CacheAndSetsCurrentIndex()
    {
        string set = Method("SetSectionCurrentMatch", 700);
        Assert.Contains("cache is null || cache.Count != sn.Matches.Count", set);
        Assert.Contains("sn.IndexByMatch = cache;", set);
        Assert.Contains("sn.CurrentIndex = idx;", set);
    }

    // ── Scroll / layout plumbing for the overlay ─────────────────────────────────

    [Fact]
    public void TryGetActiveMatchTargetVerticalOffset_BailsOnZeroViewportOrUnusableRect()
    {
        string target = Method("TryGetActiveMatchTargetVerticalOffset", 1500);
        Assert.Contains("if (viewportHeight <= 0)", target);
        Assert.Contains("if (!IsUsableTextRect(rect))", target);
        Assert.Contains("double markerHeight = Math.Max(12, rect.Height);", target);
    }

    [Fact]
    public void VerifyActiveMatchVisibleAfterScroll_UsesNormalPriorityAndSkipsStaleRequests()
    {
        string verify = Method("VerifyActiveMatchVisibleAfterScroll", 1400);
        Assert.Contains("DispatcherQueuePriority.Normal", verify);
        Assert.Contains("if (_currentMatchIndex != navIdx)", verify);
    }

    [Fact]
    public void ScrollAfterMaterialization_ReLayoutsThenScrollsToLine()
    {
        string scroll = Method("ScrollAfterMaterialization", 500);
        Assert.Contains("block.UpdateLayout();", scroll);
        Assert.Contains("ScrollPreviewToLine(block, targetPara);", scroll);
    }

    [Fact]
    public void OnPreviewViewportSizeChanged_InvalidatesScrollCacheAndRefreshesOverlay()
    {
        string resize = Method("OnPreviewViewportSizeChanged", 200);
        Assert.Contains("InvalidateScrollPositionCache();", resize);
        Assert.Contains("QueueActiveMatchOverlayRefresh();", resize);
    }

    [Fact]
    public void GetBlockAbsoluteTop_ComputesAndCachesScrollRelativeTop()
    {
        string top = Method("GetBlockAbsoluteTop", 500);
        Assert.Contains("top = PreviewScrollViewer.VerticalOffset + verticalPoint.Y;", top);
        Assert.Contains("_blockAbsoluteTopCache[block] = top;", top);
    }

    [Fact]
    public void InvalidateScrollPositionCache_RemovesOneBlockOrClearsAll()
    {
        string inv = Method("InvalidateScrollPositionCache", 250);
        Assert.Contains("_blockAbsoluteTopCache.Remove(block);", inv);
        Assert.Contains("_blockAbsoluteTopCache.Clear();", inv);
    }

    [Fact]
    public void InvalidateDeferredCountsCache_ResetsCachedCountsCursorAndValue()
    {
        string inv = Method("InvalidateDeferredCountsCache", 250);
        Assert.Contains("_cachedDeferredCountsList = null;", inv);
        Assert.Contains("_cachedDeferredCountsCursor = -1;", inv);
        Assert.Contains("_cachedDeferredCounts = default;", inv);
    }

    [Fact]
    public void GetPreviewTextViewportWidth_PrefersSectionScrollerElseOuterViewport()
    {
        string width = Method("GetPreviewTextViewportWidth", 300);
        Assert.Contains("sectionNav.Scroller.ViewportWidth > 0", width);
        Assert.Contains("return PreviewScrollViewer.ViewportWidth;", width);
    }

    [Fact]
    public void GetParagraphMetrics_BuildsAndCachesParagraphIndexMap()
    {
        string metrics = Method("GetParagraphMetrics", 700);
        Assert.Contains("map[p] = idx;", metrics);
        Assert.Contains("_paragraphMetricsCache[block] = metrics;", metrics);
    }

    [Fact]
    public void ExpandPreviewSectionForBlock_ExpandsCollapsedSectionAndReportsWhetherItChanged()
    {
        string expand = Method("ExpandPreviewSectionForBlock", 350);
        Assert.Contains("if (cachedExpander.IsExpanded)", expand);
        Assert.Contains("cachedExpander.IsExpanded = true;", expand);
        Assert.Contains("return true;", expand);
    }

    [Fact]
    public void MaterializeNextLazySectionAsync_WalksExpandersAndStopsAtFirstMatchYieldingSection()
    {
        string mat = Method("MaterializeNextLazySectionAsync", 1200);
        Assert.Contains("int start = forward ? 0 : children.Count - 1;", mat);
        Assert.Contains("await MaterializeLazySectionAsync(b);", mat);
        Assert.Contains("if (added > 0)", mat);
    }

    // ── Bulk (page) navigation + section backgrounds ─────────────────────────────

    [Fact]
    public void BulkPrevMatch_ClampsToStartAndBoundaryFlashesOnHit()
    {
        string bulk = Method("BulkPrevMatch", 800);
        Assert.Contains("int targetIndex = _currentMatchIndex - step;", bulk);
        Assert.Contains("bool hitBoundary = targetIndex < 0;", bulk);
        Assert.Contains("BoxMatchRun(para, matchInPara, boundaryFlash: true);", bulk);
    }

    [Fact]
    public void ApplyPreviewSectionBackgrounds_MarksOnlyActiveSectionBlockAsActive()
    {
        string apply = Method("ApplyPreviewSectionBackgrounds", 500);
        Assert.Contains("var activeBlock = _activeSectionNav?.Block;", apply);
        Assert.Contains("bool isActive = child.Tag is RichTextBlock block && block == activeBlock;", apply);
        Assert.Contains("ApplyPreviewSectionContentBackground(child, isActive);", apply);
    }

    // ── Diagnostic formatters (overlay geometry logging) ─────────────────────────

    [Fact]
    public void OverlayDiagnosticFormatters_ProduceStableCompactStrings()
    {
        Assert.Contains("({point.X:N1},{point.Y:N1})", Method("FormatPoint", 150));
        Assert.Contains("({rect.X:N1},{rect.Y:N1},{rect.Width:N1},{rect.Height:N1})", Method("FormatRect", 150));
        string markers = Method("FormatMarkerRects", 600);
        Assert.Contains("return \"none\";", markers);
        Assert.Contains("+{rects.Count - count} more", markers);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string Method(string name, int window)
    {
        int idx = MatchNavSource.IndexOf(name + "(", StringComparison.Ordinal);
        while (idx >= 0)
        {
            int lineStart = MatchNavSource.LastIndexOf('\n', idx) + 1;
            int lineEnd = MatchNavSource.IndexOf('\n', idx);
            string line = lineEnd > lineStart ? MatchNavSource[lineStart..lineEnd] : string.Empty;
            if (line.Contains("private ", StringComparison.Ordinal) || line.Contains("public ", StringComparison.Ordinal)
                || line.Contains("internal ", StringComparison.Ordinal) || line.Contains("protected ", StringComparison.Ordinal))
                return MatchNavSource.Substring(idx, Math.Min(window, MatchNavSource.Length - idx));
            idx = MatchNavSource.IndexOf(name + "(", idx + name.Length + 1, StringComparison.Ordinal);
        }

        throw new Xunit.Sdk.XunitException($"Definition of {name} not found in MainWindow.MatchNav.cs");
    }

    private static string ReadSource(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FindRepoRoot() }.Concat(parts).ToArray()));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root (Yagu.sln).");
    }
}
