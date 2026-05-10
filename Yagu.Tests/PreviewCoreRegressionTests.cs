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
    public void FileHeaderClick_SelectsAllAndDelegatesCollapsedPreviewAddToExpandingHandler()
    {
        string headerTap = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderTapped");
        Assert.Contains("SelectFileGroupMatches(g);", headerTap);
        Assert.Contains("_initialMatchScrolled = false;", headerTap);
        AssertContainsInOrder(headerTap,
            "if (!g.IsExpanded)",
            "return;",
            "var results = g.Where(r => r.IsSelected).ToList();");
        AssertContainsInOrder(headerTap,
            "if (TryScrollToPreviewSection(g.FilePath))",
            "return;",
            "await PrependPreviewSectionsForFilesAsync(newFiles, g.FilePath);");

        string expanding = ExtractMethodWindow(MainWindowSource, "OnFileGroupExpanding");
        Assert.Contains("_suppressPreviewUpdate = true;", expanding);
        Assert.Contains("_initialMatchScrolled = false;", expanding);
        Assert.Contains("g.SelectAll();", expanding);
        AssertContainsInOrder(expanding,
            "if (TryScrollToPreviewSection(g.FilePath))",
            "return;",
            "await PrependPreviewSectionsForFilesAsync(newFiles, g.FilePath);");
    }

    [Fact]
    public void PrependPreviewSections_GuardsAgainstDuplicatePendingAndExistingFiles()
    {
        Assert.Contains("_pendingPreviewFilePaths = new(StringComparer.OrdinalIgnoreCase)", MainWindowSource);

        string exists = ExtractMethodWindow(MainWindowSource, "PreviewSectionExists");
        Assert.Contains("ToolTipService.GetToolTip(child)", exists);
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

        string highlight = ExtractMethodWindow(MainWindowSource, "BuildHighlightSection");
        Assert.Contains("MaxMatchesPerSection", highlight);
        Assert.Contains("cappedResults", highlight);
        Assert.Contains("AppendHighlightMatchWindows", highlight);
        Assert.Contains("RegisterSectionOverflow", highlight);
        Assert.Contains("remaining = results.Skip(renderedCount).ToList()", highlight);

        string concatenated = ExtractMethodWindow(MainWindowSource, "BuildConcatenatedSection");
        Assert.Contains("cap = Math.Min(results.Count, MaxMatchesPerSection)", concatenated);
        Assert.Contains("if (matchIndex >= cap) break;", concatenated);
        Assert.Contains("RegisterSectionOverflow", concatenated);
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
        Assert.Contains("TransformToVisual(ActiveMatchOverlay)", updateOverlay);
        Assert.Contains("TransformRunRectToOverlay(block, targetPara, rect)", updateOverlay);
        Assert.Contains("IsPreviewSectionBodySettledForActiveOverlay(block, out var layoutReason)", updateOverlay);
        Assert.Contains("targetRun.ContentEnd.GetCharacterRect(Microsoft.UI.Xaml.Documents.LogicalDirection.Backward)", updateOverlay);
        Assert.Contains("usedEndRect", updateOverlay);
        Assert.Contains("TryGetEstimatedWrappedMatchPoint", updateOverlay);
        Assert.Contains("bool actualPointInViewport = point.X >= 0", updateOverlay);
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
        Assert.Contains("if (overlayTop < 0 || overlayTop + markerHeight > viewportHeight)", updateOverlay);

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
            "&& point.X <= viewportWidth",
            "&& point.Y >= 0",
            "&& point.Y + markerHeight <= viewportHeight",
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
    public void OverlayRunCoordinates_IncludeParagraphOffsetForLaterPreviewLines()
    {
        string transform = ExtractMethodWindow(MainWindowSource, "TransformRunRectToOverlay");
        AssertContainsInOrder(transform,
            "double paragraphOffset = GetParagraphOverlayOffset(block, targetPara);",
            "rect.Y + paragraphOffset");

        string offset = ExtractMethodWindow(MainWindowSource, "GetParagraphOverlayOffset");
        AssertContainsInOrder(offset,
            "int paragraphIndex = GetParagraphIndex(block, targetPara);",
            "if (paragraphIndex <= 0)",
            "return 0;",
            "return GetCumulativeHeightBefore(block, paragraphIndex, lineHeight);");
    }

    [Fact]
    public void SmallWrappedPreviewOverlay_HasColumnBasedFallbackWhenRunRectIsOnWrongRow()
    {
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");
        Assert.Contains("ViewModel.PreviewWordWrap", estimate);
        Assert.Contains("int wrappedLineIndex = column / charsPerWrappedLine;", estimate);
        Assert.Contains("double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;", estimate);
        Assert.Contains("TransformRunRectToOverlay(block, targetPara, firstRect)", estimate);
        Assert.Contains("Math.Abs(actualPoint.Y - expectedTop) <= rowTolerance", estimate);
        Assert.Contains("double expectedLeft = firstPoint.X + (column % charsPerWrappedLine) * charWidth;", estimate);
        Assert.Contains("expectedTop < actualPoint.Y - rowTolerance", estimate);
        Assert.Contains("? actualPoint.Y", estimate);
        Assert.Contains("estimatedPoint = new Windows.Foundation.Point", estimate);
    }

    [Fact]
    public void SecondFileWrappedOverlay_PreservesMeasuredRowWhenEstimateIsAboveActualRun()
    {
        string estimate = ExtractMethodWindow(MainWindowSource, "TryGetEstimatedWrappedMatchPoint");

        AssertContainsInOrder(estimate,
            "double expectedTop = firstPoint.Y + wrappedLineIndex * lineHeight;",
            "double rowTolerance = Math.Max(4, lineHeight * 0.6);",
            "double correctedTop = expectedTop < actualPoint.Y - rowTolerance",
            "? actualPoint.Y",
            ": expectedTop;",
            "estimatedPoint = new Windows.Foundation.Point(Math.Max(0, expectedLeft), correctedTop);");
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

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Yagu.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cannot find repo root (Yagu.sln)");
    }
}