using Yagu.Models;

namespace Yagu.Tests;

/// <summary>
/// Regression coverage for core preview behavior. Most of this behavior lives in
/// private WinUI event/rendering code, so these tests pin the source-level
/// contracts that keep normal unit tests independent from WindowsAppSDK.
/// </summary>
public sealed class PreviewCoreRegressionTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string MainWindowSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "MainWindow.xaml.cs"));
    private static readonly string PreviewEditorSource = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "MainWindow.PreviewEditor.cs"));
    private static readonly string MainWindowXaml = File.ReadAllText(
        Path.Combine(RepoRoot, "Yagu", "MainWindow.xaml"));

    [Fact]
    public void FileGroup_SelectAll_SelectsEveryMatch()
    {
        var group = new FileGroup(@"C:\temp\alpha.txt")
        {
            CreateResult(@"C:\temp\alpha.txt", 1),
            CreateResult(@"C:\temp\alpha.txt", 2),
            CreateResult(@"C:\temp\alpha.txt", 3),
        };

        group.SelectAll();

        Assert.True(group.AllSelected);
        Assert.Equal(group.Count, group.SelectedCount);
        Assert.All(group, result => Assert.True(result.IsSelected));
    }

    [Fact]
    public void FileHeaderClick_SelectsAllAndAddsPreview()
    {
        string headerTap = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderTapped");
        Assert.Contains("SelectFileGroupMatches(g);", headerTap);
        Assert.Contains("_initialMatchScrolled = false;", headerTap);
        Assert.DoesNotContain("if (!g.IsExpanded)", headerTap);
        Assert.Contains("var results = g.Where(r => r.IsSelected).ToList();", headerTap);
        AssertContainsInOrder(headerTap,
            "if (TryScrollToPreviewSection(g.FilePath))",
            "return;",
            "await PrependPreviewSectionsForFilesAsync(newFiles, g.FilePath);");
    }

    [Fact]
    public void FileGroupChevronExpand_DoesNotSelectOrAddPreview()
    {
        string expanding = ExtractMethodWindow(MainWindowSource, "OnFileGroupExpanding", window: 900);
        Assert.Contains("expand only", expanding);
        Assert.DoesNotContain("g.SelectAll();", expanding);
        Assert.DoesNotContain("SelectFileGroupMatches", expanding);
        Assert.DoesNotContain("TryScrollToPreviewSection", expanding);
        Assert.DoesNotContain("PrependPreviewSectionsForFilesAsync", expanding);
    }

    [Fact]
    public void PrependPreviewSections_GuardsAgainstDuplicatePendingAndExistingFiles()
    {
        Assert.Contains("_pendingPreviewFilePaths = new(StringComparer.OrdinalIgnoreCase)", MainWindowSource);

        string exists = ExtractMethodWindow(MainWindowSource, "PreviewSectionExists");
        Assert.Contains("_expanderFilePaths.TryGetValue(child, out var path)", exists);
        Assert.Contains("StringComparison.OrdinalIgnoreCase", exists);

        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync");
        AssertContainsInOrder(prepend,
            "foreach (var (filePath, results) in newFiles)",
            "if (_pendingPreviewFilePaths.Contains(filePath))",
            "if (PreviewSectionExists(filePath))",
            "_pendingPreviewFilePaths.Add(filePath)",
            "filesToPrepend[filePath] = results;");
        AssertContainsInOrder(prepend,
            "finally",
            "foreach (var filePath in filesToPrepend.Keys)",
            "_pendingPreviewFilePaths.Remove(filePath);");
    }

    [Fact]
    public void LargeMatchPreview_BuildsSectionsOffTreeYieldsAndRegistersOverflow()
    {
        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync");
        Assert.Contains("addToPanel: false", prepend);
        Assert.Contains("await YieldLowAsync();", prepend);
        Assert.Contains("PreviewSectionsPanel.Children.Insert", prepend);

        string highlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSectionAsync");
        Assert.Contains("MaxMatchesPerSection", highlight);
        Assert.Contains("MaxPreviewBlocksPerSection", highlight);
        Assert.Contains("cappedResults", highlight);
        Assert.Contains("await DispatchIdleAsync();", highlight);
        Assert.Contains("AppendHighlightMatchWindows", highlight);
        Assert.Contains("remainingBlockBudget", highlight);
        Assert.Contains("RegisterSectionOverflow", highlight);
        Assert.Contains("remaining = results.Skip(renderedCount).ToList()", highlight);

        string concatenated = ExtractMethodWindow(MainWindowSource, "BuildConcatenatedSection");
        Assert.Contains("cap = Math.Min(results.Count, MaxMatchesPerSection)", concatenated);
        Assert.Contains("section.Blocks.Count - startingBlocks >= MaxPreviewBlocksPerSection", concatenated);
        Assert.Contains("remainingResults: results.GetRange(renderedResults, results.Count - renderedResults)", concatenated);
        Assert.Contains("RegisterSectionOverflow", concatenated);

        string appendWindows = ExtractMethodWindow(MainWindowSource, "AppendHighlightMatchWindows");
        Assert.Contains("int maxAdditionalBlocks", appendWindows);
        Assert.Contains("paragraphsAdded < maxAdditionalBlocks", appendWindows);

        string expandChunk = ExtractMethodWindow(MainWindowSource, "ExpandSectionNextChunk");
        Assert.Contains("MaxPreviewBlocksPerExpandChunk", expandChunk);
    }

    [Fact]
    public void LargeLineMatchWindows_ResolveDisplayColumnsBackToSourceColumns()
    {
        string truncateAroundResult = ExtractMethodWindow(MainWindowSource, "TruncatePreviewLineAroundResult");
        Assert.Contains("ResolveSourceMatchStart(line, result, rx)", truncateAroundResult);

        string resolver = ExtractMethodWindow(MainWindowSource, "ResolveSourceMatchStart", window: 2400);
        Assert.Contains("result.MatchLine", resolver);
        Assert.Contains("displayLine.StartsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)", resolver);
        Assert.Contains("displayLine.EndsWith(LineTruncator.Ellipsis, StringComparison.Ordinal)", resolver);
        AssertContainsInOrder(resolver,
            "line.IndexOf(core, StringComparison.Ordinal)",
            "int resolved = coreIndex + offsetInCore;",
            "IsSourceMatchAt(line, resolved, result.MatchLength, rx)",
            "return resolved;");

        string matchAt = ExtractMethodWindow(MainWindowSource, "IsSourceMatchAt");
        Assert.Contains("rx.Match(line, start)", matchAt);
        Assert.Contains("match.Success && match.Index == start", matchAt);
    }

    [Fact]
    public void VisibleRegexMatches_AreYellowEvenOnContextLines()
    {
        string makeParagraph = ExtractMethodWindow(MainWindowSource, "MakePreviewParagraph");
        Assert.Contains("if (rx != null)", makeParagraph);
        Assert.DoesNotContain("if (rx != null && isMatchLine)", makeParagraph);
        Assert.Contains("hit.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gold);", makeParagraph);
        Assert.Contains("if (!isMatchLine) before.Foreground = s_contextTextBrush;", makeParagraph);
        Assert.Contains("if (!isMatchLine) tail.Foreground = s_contextTextBrush;", makeParagraph);

        string addParagraphs = ExtractMethodWindow(MainWindowSource, "AddPreviewLineParagraphs");
        Assert.Contains("if (!isMatchLine || matchParagraphs is null)", addParagraphs);
        Assert.Contains("AddMatchEntries(", addParagraphs);
    }

    [Fact]
    public void LineCheckboxClick_UpdatesPreviewAndSelectionSummary()
    {
        Assert.Contains("Click=\"OnMatchLineCheckBoxClicked\"", MainWindowXaml);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped");
        Assert.Contains("UpdatePreviewForMatchLineAsync(r, nameof(OnMatchLineTapped), previewClickedLineWhenNotMulti: true)", tapped);

        string checkboxClicked = ExtractMethodWindow(MainWindowSource, "OnMatchLineCheckBoxClicked");
        Assert.Contains("r.IsSelected = isChecked;", checkboxClicked);
        Assert.Contains("UpdatePreviewForMatchLineAsync(r, nameof(OnMatchLineCheckBoxClicked), previewClickedLineWhenNotMulti: false)", checkboxClicked);

        string previewForLine = ExtractMethodWindow(MainWindowSource, "UpdatePreviewForMatchLineAsync", window: 1800);
        Assert.Contains("FindParentGroup(r)?.NotifySelectionChanged();", previewForLine);
        Assert.Contains("ViewModel.GetAllSelectedResults();", previewForLine);
        AssertContainsInOrder(previewForLine,
            "if (selected.Count >= 2)",
            "await UpdateMultiSelectPreviewAsync(scrollTarget: r.IsSelected ? r : null);",
            "else if (selected.Count == 1)",
            "await UpdatePreviewAsync(previewClickedLineWhenNotMulti ? r : selected[0]);",
            "else if (previewClickedLineWhenNotMulti)",
            "await UpdatePreviewAsync(r);",
            "else",
            "await UpdateMultiSelectPreviewAsync();");
    }

    [Fact]
    public void FirstPreviewMatch_IsBoxedAndScrolledAfterPreviewLoad()
    {
        string updateMatchNav = ExtractMethodWindow(MainWindowSource, "UpdateMatchNavPanel");
        AssertContainsInOrder(updateMatchNav,
            "if (!_initialMatchScrolled",
            "&& !hadActiveHighlight",
            "&& _matchParagraphs.Count > 0)",
            "_initialMatchScrolled = true;");
        AssertContainsInOrder(updateMatchNav,
            "DispatcherQueue.TryEnqueue",
            "ScrollPreviewToLine(_matchParagraphs[0].block, _matchParagraphs[0].para);",
            "_currentMatchIndex = -1;",
            "OnNextMatch(this, new RoutedEventArgs());");

        string nextMatch = ExtractMethodWindow(MainWindowSource, "OnNextMatch");
        AssertContainsInOrder(nextMatch,
            "BoxMatchRun(para, matchInPara);",
            "ScrollAfterMatchNavigation(block, para");
    }

    [Fact]
    public void ActiveMatchOverlay_UsesActualOverlayCoordinatesAndWaitsForSettledScroll()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun");
        string transform = ExtractMethodWindow(MainWindowSource, "TransformRunRectToOverlay");
        Assert.Contains("TransformToVisual(ActiveMatchOverlay)", transform);
        Assert.Contains("TransformRunRectToOverlay(block, targetPara, rect)", updateOverlay);
        Assert.Contains("IsPreviewSectionBodySettledForActiveOverlay(block, out var layoutReason)", updateOverlay);
        Assert.Contains("targetRun.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward)", updateOverlay);
        Assert.Contains("usedEndRect", updateOverlay);
        Assert.Contains("TryGetEstimatedWrappedMatchPoint", updateOverlay);
        Assert.Contains("bool actualPointInViewport = point.X >= 0", updateOverlay);
        Assert.Contains("&& point.X + markerWidth <= viewportWidth", updateOverlay);
        Assert.Contains("ClampOverlayMarkerLeft", updateOverlay);
        AssertContainsInOrder(updateOverlay,
            "bool actualPointInViewport = point.X >= 0",
            "else if (!actualPointInViewport",
            "TryGetEstimatedWrappedMatchPoint");
        AssertContainsInOrder(updateOverlay,
            "if (actualCenterAccepted)",
            "waiting for settled layout",
            "return false;",
            "if (!actualCenterNeeded)");
        Assert.Contains("Canvas.SetTop(ActiveMatchWordMarker, overlayTop);", updateOverlay);
        Assert.Contains("Canvas.SetLeft(ActiveMatchWordMarker, markerLeft);", updateOverlay);
        Assert.Contains("if (overlayTop < viewportTop || overlayTop + markerHeight > viewportBottom)", updateOverlay);

        Assert.Contains("<Canvas x:Name=\"ActiveMatchOverlay\"", MainWindowXaml);
        Assert.Contains("HorizontalAlignment=\"Stretch\" VerticalAlignment=\"Stretch\"", MainWindowXaml);
        Assert.Contains("Canvas.ZIndex=\"20\"", MainWindowXaml);
    }

    [Fact]
    public void FirstFileWrappedOverlay_TrustsMeasuredRunWhenItIsAlreadyVisible()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun");

        AssertContainsInOrder(updateOverlay,
            "var point = TransformRunRectToOverlay(block, targetPara, rect);",
            "bool actualPointInViewport = point.X >= 0",
            "&& point.X + markerWidth <= viewportWidth",
            "&& point.Y >= viewportTop",
            "&& point.Y + markerHeight <= viewportBottom",
            "else if (!actualPointInViewport",
            "&& TryGetEstimatedWrappedMatchPoint",
            "point = estimatedPoint;");

        Assert.DoesNotContain("else if (TryGetEstimatedWrappedMatchPoint", updateOverlay);
    }

    [Fact]
    public void ActiveOverlayUpdate_IsTiedToTheBoxedRunForMatchOne()
    {
        string boxMatch = ExtractMethodWindow(MainWindowSource, "BoxMatchRun");
        AssertContainsInOrder(boxMatch,
            "var (run, column) = matches[matchInPara];",
            "_activeMatchHighlight = (para, run, column, matchInPara);");

        string queueOverlay = ExtractMethodWindow(MainWindowSource, "QueueActiveMatchOverlayUpdate");
        AssertContainsInOrder(queueOverlay,
            "Run targetRun = activeRun;",
            "ReferenceEquals(currentRun, targetRun)",
            "TryUpdateActiveMatchOverlayFromActualRun(block, targetPara, targetRun");
    }

    [Fact]
    public void OverlayRunCoordinates_UseTextPointerBlockRelativeCoordinates()
    {
        string transform = ExtractMethodWindow(MainWindowSource, "TransformRunRectToOverlay");
        AssertContainsInOrder(transform,
            "var transform = block.TransformToVisual(ActiveMatchOverlay);",
            "return transform.TransformPoint(new Windows.Foundation.Point(rect.X, rect.Y));");
        Assert.DoesNotContain("paragraphOffset", transform);
    }

    [Fact]
    public void SmallWrappedPreviewOverlay_HasColumnBasedFallbackWhenRunRectIsOnWrongRow()
    {
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");
        Assert.Contains("ViewModel.PreviewWordWrap", estimate);
        Assert.Contains("int wrappedLineIndex = column / charsPerWrappedLine;", estimate);
        Assert.Contains("double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;", estimate);
        Assert.Contains("TransformRunRectToOverlay(block, targetPara, firstRect)", estimate);
        Assert.Contains("bool actualRowMatchesEstimate = Math.Abs(actualPoint.Y - expectedTop) <= rowTolerance;", estimate);
        Assert.Contains("bool actualMarkerHorizontallyVisible = actualPoint.X >= 0", estimate);
        Assert.Contains("if (actualRowMatchesEstimate && actualMarkerHorizontallyVisible)", estimate);
        Assert.Contains("double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;", estimate);
        Assert.Contains("double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);", estimate);
        Assert.Contains("estimatedPoint = new Windows.Foundation.Point(correctedLeft, correctedTop);", estimate);
    }

    [Fact]
    public void WrappedPreviewOverlay_EstimatesWhenMeasuredXIsOffscreenEvenIfRowMatches()
    {
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");

        AssertContainsInOrder(estimate,
            "bool actualRowMatchesEstimate = Math.Abs(actualPoint.Y - expectedTop) <= rowTolerance;",
            "bool actualMarkerHorizontallyVisible = actualPoint.X >= 0",
            "if (actualRowMatchesEstimate && actualMarkerHorizontallyVisible)",
            "return false;",
            "double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;",
            "double correctedTop = actualRowMatchesEstimate",
            "double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);",
            "estimatedPoint = new Windows.Foundation.Point(correctedLeft, correctedTop);");
    }

    [Fact]
    public void NoWrapHorizontalMatchScroll_PrefersMeasuredRunRectOverColumnEstimate()
    {
        string scroll = ExtractMethodWindow(MainWindowSource, "ScrollMatchHorizontallyIntoView");

        AssertContainsInOrder(scroll,
            "double estimatedMatchStart = 8 + column * charWidth;",
            "double matchStart = estimatedMatchStart;",
            "string source = \"estimated\";",
            "var rect = activeRun.ContentStart.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Forward);",
            "var measuredPoint = block.TransformToVisual(scroller)",
            "matchStart = measuredPoint.X + scroller.HorizontalOffset;",
            "source = \"measured\";");
        Assert.Contains("source={source}, matchStart={matchStart:N1}, estimateStart={estimatedMatchStart:N1}", scroll);
    }

    [Fact]
    public void NoWrapOverlay_RetriesInsteadOfClampingHorizontallyOffscreenMarker()
    {
        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun");

        AssertContainsInOrder(updateOverlay,
            "bool markerHorizontallyOutside = point.X < 0 || point.X + markerWidth > viewportWidth;",
            "if (!ViewModel.PreviewWordWrap && markerHorizontallyOutside)",
            "if (retryIfCenterRejected)",
            "ScrollMatchHorizontallyIntoView(block, targetPara);",
            "rejecting horizontally offscreen marker",
            "return false;",
            "double markerLeft = ClampOverlayMarkerLeft(point.X, markerWidth, viewportWidth);");
    }

    [Fact]
    public void SecondFileWrappedOverlay_PreservesMeasuredRowWhenEstimateIsAboveActualRun()
    {
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");

        AssertContainsInOrder(estimate,
            "double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;",
            "double rowTolerance = Math.Max(4, lineHeight * 0.6);",
            "double correctedTop = actualRowMatchesEstimate",
            "? actualPoint.Y",
            ": (expectedTop < actualPoint.Y - rowTolerance ? actualPoint.Y : expectedTop);",
            "double correctedLeft = ClampOverlayMarkerLeft(expectedLeft, markerWidth, viewportWidth);",
            "estimatedPoint = new Windows.Foundation.Point(correctedLeft, correctedTop);");
    }

    [Fact]
    public void SecondAddedSectionOverlay_WaitsForSectionBodyLayoutAndUsesImmediateSectionScroll()
    {
        string scrollSection = ExtractMethodWindow(MainWindowSource, "TryScrollToPreviewSection");
        Assert.Contains("PreviewScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);", scrollSection);

        string settled = ExtractMethodWindow(MainWindowSource, "IsPreviewSectionBodySettledForActiveOverlay");
        Assert.Contains("_blockExpanderCache.TryGetValue(block, out var expander)", settled);
        Assert.Contains("expander.Header is not FrameworkElement header", settled);
        Assert.Contains("expander.TransformToVisual(ActiveMatchOverlay)", settled);
        Assert.Contains("block.TransformToVisual(ActiveMatchOverlay)", settled);
        Assert.Contains("blockPoint.Y < minBodyTop", settled);
        Assert.Contains("return false;", settled);
    }

    [Fact]
    public void PrependingSecondFile_ReordersMatchNavigationAndClearsStaleOverlayState()
    {
        string reorder = ExtractMethodWindow(MainWindowSource, "ReorderMatchParagraphsToPreviewSectionOrder");
        Assert.Contains("PreviewSectionsPanel.Children", reorder);
        Assert.Contains(".OfType<Expander>()", reorder);
        Assert.Contains("buckets[block]", reorder);
        AssertContainsInOrder(reorder,
            "_matchParagraphs.Clear();",
            "_matchParagraphs.AddRange(reordered);",
            "InvalidateParagraphIndexCache();");

        string prepend = ExtractMethodWindow(MainWindowSource, "PrependPreviewSectionsForFilesAsync");
        AssertContainsInOrder(prepend,
            "PreviewSectionsPanel.Children.Insert(insertIndex++, built[i]);",
            "ReorderMatchParagraphsToPreviewSectionOrder();",
            "UnboxCurrentMatch();",
            "HideActiveMatchOverlay();",
            "InvalidatePendingMatchScrolls();",
            "_currentMatchIndex = -1;",
            "_initialMatchScrolled = false;",
            "UpdateMatchNavPanel();");
    }

    [Fact]
    public void LargeChunkedPreviewEditor_AutoAppendsOnScrollWithoutPromptMitigation()
    {
        Assert.Contains("private const long PreviewEditorChunkByteLength = 10L * 1024 * 1024;", PreviewEditorSource);

        string showChunked = ExtractMethodWindow(PreviewEditorSource, "ShowChunkedPreviewEditorAsync");
        AssertContainsInOrder(showChunked,
            "_previewEditorForcedWrap = false;",
            "ApplyPreviewEditorWordWrap(ViewModel.PreviewWordWrap);");
        Assert.DoesNotContain("wrapSuppressed", showChunked);

        string scroll = ExtractMethodWindow(PreviewEditorSource, "OnPreviewEditorScrollViewChanged");
        Assert.Contains("_ = LoadMorePreviewEditorChunkAsync();", scroll);

        string loadMore = ExtractMethodWindow(PreviewEditorSource, "LoadMorePreviewEditorChunkAsync");
        Assert.DoesNotContain("auto-load skipped during scroll", loadMore);
        Assert.DoesNotContain("Use Load More", loadMore);
        Assert.Contains("_previewEditorChunkLoadInFlight = true;", loadMore);
    }

    [Fact]
    public void LongLinePreviewEditor_LeavesWordWrapAsUserConfigured()
    {
        Assert.DoesNotContain("_previewEditorWrapSuppressed", PreviewEditorSource);
        Assert.DoesNotContain("PreviewEditorWrapDisableLineLength", PreviewEditorSource);

        string showFullEditor = ExtractMethodWindow(PreviewEditorSource, "ShowFullFileEditorAsync");
        AssertContainsInOrder(showFullEditor,
            "_previewEditorForcedWrap = false;",
            "ApplyPreviewEditorWordWrap(ViewModel.PreviewWordWrap);",
            "long line loaded with user word-wrap setting");

        string applyEditorWrap = ExtractMethodWindow(PreviewEditorSource, "ApplyPreviewEditorWordWrap");
        Assert.Contains("PreviewEditor.WordWrap = wrap;", applyEditorWrap);

        string applyWrap = ExtractMethodWindow(MainWindowSource, "ApplyWordWrap");
        Assert.Contains("ApplyPreviewEditorWordWrap(_previewEditorForcedWrap || wrap);", applyWrap);
    }

    [Fact]
    public void TextControlBox_LongWrappedLinesUseVirtualizedRendering()
    {
        string textRenderer = ReadTextControlBoxSource("Core", "Renderer", "TextRenderer.cs");
        string lineNumberRenderer = ReadTextControlBoxSource("Core", "Renderer", "LineNumberRenderer.cs");

        Assert.Contains("LongWrappedLineVirtualizationThreshold", textRenderer);
        Assert.Contains("BuildVirtualizedWrappedLineRenderData", textRenderer);
        Assert.Contains("GetRenderedCharacterIndexForDocumentCharacter", textRenderer);

        string measure = ExtractMethodWindow(textRenderer, "MeasureWrappedRowCount");
        AssertContainsInOrder(measure,
            "lineText.Length >= LongWrappedLineVirtualizationThreshold",
            "return EstimateWrappedRowCount(canvasText, lineText.Length);");

        string draw = ExtractMethodWindow(textRenderer, "Draw", window: 18000);
        AssertContainsInOrder(draw,
            "ResetVirtualizedWrappedLineState();",
            "BuildVirtualizedWrappedLineRenderData(canvasText, NumberOfStartLine)",
            "RenderedText = renderTextData.Text;",
            "DrawTextOffsetY = IsVirtualizedWrappedLine",
            "DrawnTextLayout = textLayoutManager.CreateTextResource");

        string lineNumbers = ExtractMethodWindow(lineNumberRenderer, "GenerateLineNumberText");
        AssertContainsInOrder(lineNumbers,
            "if (textRenderer.IsVirtualizedWrappedLine)",
            "textRenderer.WrappedStartRowOffset == 0",
            "textRenderer.VirtualizedWrappedRowsToRender",
            "return;");
    }

    private static SearchResult CreateResult(string filePath, int lineNumber) =>
        new(filePath, lineNumber, $"line {lineNumber} test", 5, 4, Array.Empty<string>(), Array.Empty<string>());

    private static string ExtractMethodWindow(string source, string methodName, int window = 12000)
    {
        int index = FindMethodDefinition(source, methodName);
        int end = Math.Min(source.Length, index + window);
        return source[index..end];
    }

    private static int FindMethodDefinition(string source, string methodName)
    {
        string needle = methodName + "(";
        int search = 0;
        while (true)
        {
            int index = source.IndexOf(needle, search, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Method definition '{methodName}' not found in MainWindow.xaml.cs");

            int lineStart = source.LastIndexOf('\n', index);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            int lineEnd = source.IndexOf('\n', index);
            lineEnd = lineEnd < 0 ? source.Length : lineEnd;
            string line = source[lineStart..lineEnd];
            if (line.Contains("private ", StringComparison.Ordinal)
                || line.Contains("public ", StringComparison.Ordinal)
                || line.Contains("internal ", StringComparison.Ordinal)
                || line.Contains("protected ", StringComparison.Ordinal))
            {
                return lineStart;
            }

            search = index + needle.Length;
        }
    }

    private static void AssertContainsInOrder(string text, params string[] expected)
    {
        int cursor = 0;
        foreach (string value in expected)
        {
            int index = text.IndexOf(value, cursor, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected to find '{value}' after offset {cursor}.");
            cursor = index + value.Length;
        }
    }

    private static string ReadTextControlBoxSource(params string[] pathParts)
    {
        string textControlBoxRoot = Path.GetFullPath(Path.Combine(RepoRoot, "..", "src", "TextControlBox-WinUI", "TextControlBox"));
        string path = Path.Combine(new[] { textControlBoxRoot }.Concat(pathParts).ToArray());
        Assert.True(File.Exists(path), $"Expected TextControlBox source file at {path}");
        return File.ReadAllText(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}