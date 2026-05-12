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
    public void FileHeaderControlClickAndDoubleClick_AreHeaderPreviewAddGestures()
    {
        Assert.Contains("AutomationProperties.AutomationId=\"FileGroupCheckBox\"", MainWindowXaml);

        string headerCheckbox = ExtractXamlWindow("AutomationProperties.AutomationId=\"FileGroupCheckBox\"", 500);
        Assert.Contains("IsChecked=\"{x:Bind AllSelected, Mode=OneWay}\"", headerCheckbox);
        Assert.Contains("Click=\"OnFileGroupCheckBoxClicked\"", headerCheckbox);
        Assert.DoesNotContain("IsHitTestVisible=\"False\"", headerCheckbox);
        Assert.DoesNotContain("Checked=\"OnSelectAllChecked\"", headerCheckbox);
        Assert.DoesNotContain("Unchecked=\"OnSelectAllUnchecked\"", headerCheckbox);

        string headerGrid = ExtractXamlWindow("PointerPressed=\"OnFileGroupHeaderPointerPressed\"", 260);
        Assert.Contains("PointerReleased=\"OnFileGroupHeaderPointerReleased\"", headerGrid);
        Assert.Contains("Tapped=\"OnFileGroupHeaderTapped\"", headerGrid);
        Assert.Contains("DoubleTapped=\"OnFileGroupHeaderDoubleTapped\"", headerGrid);

        string pointerPressed = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderPointerPressed", window: 1800);
        Assert.Contains("_ctrlFileHeaderGestureWasExpanded = group.IsExpanded;", pointerPressed);
        Assert.Contains("e.Handled = true;", pointerPressed);

        string pointerReleased = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderPointerReleased", window: 2200);
        AssertContainsInOrder(pointerReleased,
            "bool wasExpanded = _ctrlFileHeaderGestureWasExpanded;",
            "ClearCtrlFileHeaderGesture();",
            "await SelectFileGroupMatchesAndPreviewAsync(group, \"ctrl click\", preserveExpansionState: wasExpanded);");

        string headerTap = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderTapped");
        AssertContainsInOrder(headerTap,
            "if (IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))",
            "return;",
            "if (IsControlKeyDown())",
            "e.Handled = true;",
            "if (WasCtrlFileHeaderPreviewJustHandled(g))",
            "return;",
            "await SelectFileGroupMatchesAndPreviewAsync(g, \"ctrl click\", preserveExpansionState: wasExpanded);",
            "if (g.IsExpanded)",
            "collapse only",
            "return;",
            "expand only");
        Assert.DoesNotContain("SelectFileGroupMatchesAndPreviewAsync(g, \"single click\")", headerTap);

        string doubleTap = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderDoubleTapped");
        AssertContainsInOrder(doubleTap,
            "if (IsInsideHeaderCommand(e.OriginalSource as DependencyObject, header))",
            "return;",
            "e.Handled = true;",
            "await SelectFileGroupMatchesAndPreviewAsync(g, \"double click\");");

        string selectAndPreview = ExtractMethodWindow(MainWindowSource, "SelectFileGroupMatchesAndPreviewAsync");
        Assert.Contains("SelectFileGroupMatches(group);", selectAndPreview);
        Assert.Contains("_initialMatchScrolled = false;", selectAndPreview);
        Assert.Contains("var results = group.Where(r => r.IsSelected).ToList();", selectAndPreview);
        Assert.Contains("RecordCtrlFileHeaderPreview(group.FilePath);", selectAndPreview);
        Assert.Contains("group.IsExpanded = targetState;", selectAndPreview);
        Assert.Contains("DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low", selectAndPreview);
        AssertContainsInOrder(selectAndPreview,
            "if (TryScrollToPreviewSection(group.FilePath))",
            "return;",
            "await PrependPreviewSectionsForFilesAsync(newFiles, group.FilePath);");

        string selectMatches = ExtractMethodWindow(MainWindowSource, "SelectFileGroupMatches");
        Assert.Contains("group.AllSelected && group.SelectedCount == group.Count", selectMatches);
    }

    [Fact]
    public void SelectionOnlyClicks_DoNotAddFilesToPreviewPanel()
    {
        string itemClick = ExtractMethodWindow(MainWindowSource, "OnResultItemClick", window: 450);
        Assert.Contains("OnResultItemClick: no preview change", itemClick);
        Assert.DoesNotContain("UpdatePreviewAsync(g[0])", itemClick);
        Assert.DoesNotContain("TryScrollToPreviewSection(g[0].FilePath)", itemClick);

        string headerTap = ExtractMethodWindow(MainWindowSource, "OnFileGroupHeaderTapped");
        Assert.DoesNotContain("SelectFileGroupMatchesAndPreviewAsync(g, \"single click\")", headerTap);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped", window: 900);
        Assert.Contains("OnMatchLineTapped: no preview change", tapped);
        Assert.DoesNotContain("UpdatePreviewAsync", tapped);
        Assert.DoesNotContain("UpdateMultiSelectPreviewAsync", tapped);
        Assert.DoesNotContain("PrependPreviewSectionsForFilesAsync", tapped);
    }

    [Fact]
    public void FileHeaderCheckbox_ClickSelectsOrDeselectsAllChildMatches()
    {
        string checkboxClicked = ExtractMethodWindow(MainWindowSource, "OnFileGroupCheckBoxClicked");
        Assert.Contains("sender is not CheckBox checkBox || checkBox.DataContext is not FileGroup group", checkboxClicked);
        Assert.Contains("bool shouldSelect = checkBox.IsChecked == true;", checkboxClicked);
        Assert.Contains("_suppressPreviewUpdate = true;", checkboxClicked);
        AssertContainsInOrder(checkboxClicked,
            "if (isShift && currentIndex >= 0)",
            "if (shouldSelect)",
            "ViewModel.ResultGroups[i].SelectAll();",
            "else",
            "ViewModel.ResultGroups[i].DeselectAll();",
            "else if (shouldSelect)",
            "group.SelectAll();",
            "else",
            "group.DeselectAll();");
    }

    [Fact]
    public void FileHeaderContextMenu_PreviewsRightClickedGroupWhenNothingIsChecked()
    {
        string headerFlyout = ExtractXamlWindow("<MenuFlyout Opening=\"OnFileHeaderContextMenuOpening\"", 500);
        string fileHeaderGrid = ExtractXamlWindow("PointerPressed=\"OnFileGroupHeaderPointerPressed\"", 1400);
        string resultsList = ExtractXamlWindow("x:Name=\"ResultsList\"", 1200);
        Assert.Contains("PointerPressed=\"OnResultsListPointerPressed\"", resultsList);
        Assert.Contains("RightTapped=\"OnResultsListRightTapped\"", resultsList);
        Assert.Contains("Tag=\"{x:Bind FilePath}\"", fileHeaderGrid);
        Assert.Contains("Background=\"Transparent\"", fileHeaderGrid);
        Assert.Contains("<Grid.ContextFlyout>", fileHeaderGrid);
        Assert.Contains("Text=\"Preview selected\"", headerFlyout);
        Assert.Contains("Click=\"OnPreviewSelectedFiles\"", headerFlyout);
        Assert.Contains("Tag=\"{x:Bind FilePath}\"", headerFlyout);

        string opening = ExtractMethodWindow(MainWindowSource, "OnFileHeaderContextMenuOpening", window: 1800);
        AssertContainsInOrder(opening,
            "var contextGroup = GetFileHeaderContextGroup(flyout)",
            "flyout.Items.OfType<MenuFlyoutItem>()",
            "int checkedCount = GetCheckedFileGroups().Count;",
            "int count = checkedCount > 0 ? checkedCount : contextGroup is null ? 0 : 1;",
            "previewItem.Text = $\"Preview selected ({count})\";",
            "previewItem.Tag = contextGroup;");

        string contextGroup = ExtractMethodWindow(MainWindowSource, "GetFileHeaderContextGroup", window: 1400);
        Assert.Contains("if (sender is MenuFlyout { Target: FrameworkElement target })", contextGroup);
        Assert.Contains("var taggedTargetGroup = GetFileHeaderContextGroup(target);", contextGroup);

        string resultsOpening = ExtractMethodWindow(MainWindowSource, "OnResultsContextMenuOpening", window: 1400);
        AssertContainsInOrder(resultsOpening,
            "var checkedGroups = GetCheckedFileGroups();",
            "var contextGroup = checkedGroups.Count == 0 ? GetRecentResultsContextMenuGroup() : null;",
            "int count = checkedGroups.Count > 0 ? checkedGroups.Count : contextGroup is null ? 0 : 1;",
            "CtxPreviewSelected.Text = $\"Preview selected ({count})\";",
            "CtxPreviewSelected.Tag = contextGroup;");

        string capture = ExtractMethodWindow(MainWindowSource, "CaptureResultsContextMenuGroup", window: 900);
        Assert.Contains("_lastResultsContextMenuGroup = FindContextFileGroup(source);", capture);
        Assert.Contains("_lastResultsContextMenuTick = Environment.TickCount64;", capture);

        string findContext = ExtractMethodWindow(MainWindowSource, "FindContextFileGroup", window: 1800);
        Assert.Contains("element.DataContext is FileGroup dataContextGroup", findContext);
        Assert.Contains("element.DataContext is SearchResult result", findContext);

        string previewSelected = ExtractMethodWindow(MainWindowSource, "OnPreviewSelectedFiles", window: 5000);
        AssertContainsInOrder(previewSelected,
            "var selectedGroups = GetPreviewFileGroups(sender);",
            "foreach (var g in selectedGroups)",
            "g.SelectAll();",
            "await PrependPreviewSectionsForFilesAsync(newFiles, scrollTo);");

        string previewGroups = ExtractMethodWindow(MainWindowSource, "GetPreviewFileGroups", window: 1200);
        AssertContainsInOrder(previewGroups,
            "var checkedGroups = GetCheckedFileGroups();",
            "if (checkedGroups.Count > 0)",
            "return checkedGroups;",
            "var contextGroup = GetFileHeaderContextGroup(sender);",
            "return contextGroup is null ? checkedGroups : [contextGroup];");
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
        string matchLineLoaded = ExtractMethodWindow(MainWindowSource, "OnMatchLineLoaded", window: 900);
        Assert.Contains("int matchStart = r.IsEvicted ? r.ShortPreviewMatchStart : r.MatchStartColumn;", matchLineLoaded);
        Assert.Contains("HighlightInline(para, r.MatchLine, matchStart, r.MatchLength);", matchLineLoaded);

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
    public void LineCheckboxClick_UpdatesSelectionOnly()
    {
        Assert.Contains("Click=\"OnMatchLineCheckBoxClicked\"", MainWindowXaml);

        string tapped = ExtractMethodWindow(MainWindowSource, "OnMatchLineTapped");
        Assert.Contains("e.OriginalSource is DependencyObject source && IsInsideButton(source)", tapped);
        Assert.Contains("OnMatchLineTapped: no preview change", tapped);

        string checkboxClicked = ExtractMethodWindow(MainWindowSource, "OnMatchLineCheckBoxClicked", window: 1000);
        Assert.Contains("result.IsSelected = isChecked;", checkboxClicked);
        Assert.Contains("UpdateSelectionForMatchLine(result, nameof(OnMatchLineCheckBoxClicked));", checkboxClicked);
        Assert.DoesNotContain("UpdatePreviewAsync", checkboxClicked);
        Assert.DoesNotContain("UpdateMultiSelectPreviewAsync", checkboxClicked);

        string selectionForLine = ExtractMethodWindow(MainWindowSource, "UpdateSelectionForMatchLine", window: 900);
        Assert.Contains("FindParentGroup(result)?.NotifySelectionChanged();", selectionForLine);
        Assert.Contains("ViewModel.GetAllSelectedResults();", selectionForLine);
        Assert.Contains("selection only", selectionForLine);
        Assert.DoesNotContain("UpdatePreviewAsync", selectionForLine);
        Assert.DoesNotContain("UpdateMultiSelectPreviewAsync", selectionForLine);
    }

    [Fact]
    public void PreviewContextChange_PreservesScrollAndAllowsUnboundedInput()
    {
        Assert.Contains("Value=\"{x:Bind ViewModel.PreviewContextLines, Mode=TwoWay}\" Minimum=\"0\"", MainWindowXaml);
        Assert.DoesNotContain("ViewModel.PreviewContextLines, Mode=TwoWay}\" Minimum=\"0\" Maximum", MainWindowXaml);
        Assert.Contains("var prevCtx = new NumberBox { Value = ViewModel.PreviewContextLines, Minimum = 0 };", MainWindowSource);
        Assert.Contains("RefreshCurrentPreview(preserveScroll: true);", MainWindowSource);

        string refresh = ExtractMethodWindow(MainWindowSource, "RefreshCurrentPreview", window: 4500);
        AssertContainsInOrder(refresh,
            "double restoreHorizontalOffset = PreviewScrollViewer.HorizontalOffset;",
            "double restoreVerticalOffset = PreviewScrollViewer.VerticalOffset;",
            "int restoreMatchIndex = preserveScroll ? _currentMatchIndex : -1;",
            "_suppressInitialMatchAutoScroll = true;",
            "await UpdateMultiSelectPreviewAsync();",
            "RestorePreviewScrollOffset(restoreHorizontalOffset, restoreVerticalOffset);",
            "RestoreActiveMatchAfterPreviewRefresh(restoreMatchIndex);");

        string restore = ExtractMethodWindow(MainWindowSource, "RestorePreviewScrollOffset", window: 2400);
        Assert.Contains("PreviewScrollViewer.ChangeView(targetX, targetY, null, disableAnimation: true);", restore);
        Assert.Contains("DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyRestore);", restore);

        string updateMatchNav = ExtractMethodWindow(MainWindowSource, "UpdateMatchNavPanel");
        Assert.Contains("&& !_suppressInitialMatchAutoScroll", updateMatchNav);
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
            "_ = GoToNextMatchAsync();");
        Assert.DoesNotContain("OnNextMatch(this, new RoutedEventArgs())", updateMatchNav);

        string nextMatch = ExtractMethodWindow(MainWindowSource, "OnNextMatch");
        Assert.Contains("ShowBulkMatchStepFlyout(NextMatchButton, BulkNextMatch);", nextMatch);
        Assert.Contains("await GoToNextMatchAsync();", nextMatch);

        string goToNext = ExtractMethodWindow(MainWindowSource, "GoToNextMatchAsync", window: 3200);
        Assert.DoesNotContain("ShowBulkMatchStepFlyout", goToNext);
        AssertContainsInOrder(nextMatch,
            "await GoToNextMatchAsync();");
        AssertContainsInOrder(goToNext,
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
        Assert.Contains("Background=\"#1AFF4500\"", MainWindowXaml);
        Assert.Contains("BorderBrush=\"#FFFF4500\"", MainWindowXaml);
        Assert.Contains("BorderThickness=\"0,0,0,2\"", MainWindowXaml);
        Assert.DoesNotContain("#CCFF4500", MainWindowXaml);
        Assert.DoesNotContain("#FFFFFFFF", MainWindowXaml);
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
    public void WrappedLongActiveMatchOverlay_UsesMultipleWordMarkers()
    {
        Assert.Contains("private readonly List<Border> _activeMatchExtraWordMarkers = new();", MainWindowSource);

        string updateOverlay = ExtractMethodWindow(MainWindowSource, "TryUpdateActiveMatchOverlayFromActualRun", window: 18000);
        AssertContainsInOrder(updateOverlay,
            "TryBuildMeasuredWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = measuredMarkerRects;",
            "else if (TryBuildWrappedActiveMatchMarkerRects(",
            "wrappedMarkerRects = estimatedMarkerRects;",
            "double markerTopDelta = overlayTop - point.Y;",
            "effectiveWrappedMarkerRects.Add(new Windows.Foundation.Rect(",
            "if (effectiveWrappedMarkerRects is { Count: > 1 })",
            "ApplyActiveMatchMarkerRect(ActiveMatchWordMarker, effectiveWrappedMarkerRects[0]);",
            "var marker = CreateActiveMatchWordMarker();",
            "_activeMatchExtraWordMarkers.Add(marker);",
            "ActiveMatchOverlay.Children.Add(marker);");

        string measuredRects = ExtractMethodWindow(MainWindowSource, "TryBuildMeasuredWrappedActiveMatchMarkerRects", window: 3600);
        AssertContainsInOrder(measuredRects,
            "var endPoint = TransformRunRectToOverlay(block, targetPara, endRect);",
            "double rowDistance = endPoint.Y - startPoint.Y;",
            "var firstRun = targetPara.Inlines.OfType<Run>().FirstOrDefault();",
            "int rowSpan = Math.Max(1, (int)Math.Round(rowDistance / lineHeight, MidpointRounding.AwayFromZero));",
            "double top = rowIndex == rowSpan ? endPoint.Y : startPoint.Y + rowIndex * rowStep;",
            "markerRects.Add(new Windows.Foundation.Rect(left, top, width, markerHeight));",
            "return markerRects.Count > 1;");

        string buildRects = ExtractMethodWindow(MainWindowSource, "TryBuildWrappedActiveMatchMarkerRects", window: 3600);
        AssertContainsInOrder(buildRects,
            "int charsPerWrappedLine = Math.Max(1, (int)Math.Floor(availableWidth / charWidth));",
            "int startRow = column / charsPerWrappedLine;",
            "int endRow = (Math.Max(column, endExclusive - 1)) / charsPerWrappedLine;",
            "for (int row = startRow; row <= endRow; row++)",
            "markerRects.Add(new Windows.Foundation.Rect(left, top, width, markerHeight));",
            "return markerRects.Count > 1;");

        string hide = ExtractMethodWindow(MainWindowSource, "HideActiveMatchOverlay", window: 500);
        Assert.Contains("ClearActiveMatchExtraWordMarkers();", hide);
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

    private static string ExtractXamlWindow(string marker, int window)
    {
        int index = MainWindowXaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"XAML marker '{marker}' not found.");
        int end = Math.Min(MainWindowXaml.Length, index + window);
        return MainWindowXaml[index..end];
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